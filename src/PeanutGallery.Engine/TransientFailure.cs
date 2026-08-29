using System;
using System.ClientModel;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;

namespace PeanutGallery.Engine;

/// <summary>
/// Pure classification of a model-call exception as transient (worth a retry) or fatal.
/// Retry only helps when the failure is a symptom of *where* the request landed — a slow
/// or hung OpenRouter route (surfaces as our per-attempt <see cref="TimeoutException"/>), a
/// 5xx/429 from the upstream, a dropped connection, or an upstream generation that failed on
/// that route (OpenRouter reports it as <c>finish_reason:"error"</c>) — because a retry is a
/// fresh request OpenRouter may route to a healthy provider. A configuration or auth failure
/// (missing key, unknown provider, 400/401/403) is the same on every attempt, so it is fatal —
/// retrying just burns the budget. Outer cancellation is never transient: the caller asked to stop.
/// </summary>
public static class TransientFailure
{
	/// <summary>True when <paramref name="e"/> is worth re-issuing the call for.</summary>
	public static bool IsRetryable(Exception? e) => e switch
	{
		null => false,
		// Our own per-attempt deadline elapsed (TimeBox). A hung route: retry re-routes.
		TimeoutException => true,
		// Outer teardown — the run is being cancelled. Never retry.
		OperationCanceledException => false,
		// Provider returned an HTTP error. Retry the transient status class only.
		ClientResultException cre =>
			IsTransientStatus(cre.Status) || (cre.Status == 0 && IsRetryable(cre.InnerException)),
		// Transport-level faults (connection reset, DNS blip, read error) — retry.
		HttpRequestException => true,
		SocketException => true,
		IOException => true,
		// An upstream generation that failed on the chosen route. OpenRouter surfaces this as
		// finish_reason:"error", and the OpenAI SDK rejects that as an unknown ChatFinishReason and
		// throws (an ArgumentException) while parsing the response - before we ever see the text. It
		// is transient (that route/provider hiccuped), not a bug in our request, so re-issue and let
		// OpenRouter re-route to a healthy provider. Matched by the SDK's telltale message rather
		// than the exception type, which varies by SDK version. (#113)
		_ when IsUnknownFinishReason(e) => true,
		// The same shape as #113 on a different wire form: the provider returned no choices at all,
		// so the SDK could not map a reply. Raised at the call boundary, never inferred here. (#158)
		MalformedResponseException => true,
		// Fall through to a wrapped inner cause (e.g. an aggregate around a socket error, or a
		// wrapped unknown-finish-reason parse failure).
		_ => e.InnerException is not null && IsRetryable(e.InnerException),
	};

	/// <summary>
	/// True when this failure IS a <see cref="MalformedResponseException"/>, looking through any
	/// wrapper (an exhausted retry arrives inside a <see cref="ModelCallException"/>). Lets
	/// <c>ReviewRunner</c> tag <c>FailureClass.MalformedResponse</c> structurally, the same contract
	/// the timeout path uses — a TYPE raised at the boundary that knew, never a shape guessed here.
	/// </summary>
	public static bool IsMalformedResponse(Exception? e) => e switch
	{
		null => false,
		MalformedResponseException => true,
		_ => IsMalformedResponse(e.InnerException),
	};

	/// <summary>
	/// Recognises the OpenAI SDK failing to map a reply: a completion carrying <b>no choices at
	/// all</b>, which the SDK reports as an <see cref="ArgumentOutOfRangeException"/> on parameter
	/// <c>index</c>. That is how OpenRouter surfaces an upstream generation it could not route
	/// (<c>{"error":{…},"choices":[]}</c>) — #113's sibling, on the wire instead of as
	/// <c>finish_reason:"error"</c>. Observed against <c>openai/gpt-5.6-luna</c>, failing whole
	/// panels on four consecutive pushes without ever being retried. (#158)
	/// <para><b>Only ever call this at the model-call boundary</b>, where the SDK's mapping is the
	/// only code in scope — <see cref="ChatClientReviewer"/> does, and re-raises a
	/// <see cref="MalformedResponseException"/>. A parameter name is not evidence of origin, so
	/// applied any wider this would swallow an out-of-range bug of our own — retrying it, then
	/// mislabelling it as a provider fault. Matched on the type and parameter name rather than the
	/// message, whose wording varies with the SDK build.</para>
	/// </summary>
	public static bool IsSdkMappingFailure(Exception? e) =>
		e is ArgumentOutOfRangeException { ParamName: "index" };

	// The OpenAI SDK throws "Unknown ChatFinishReason value. (Parameter 'value')" (with "Actual
	// value was error.") when a provider returns a finish_reason outside the SDK's known set - which
	// is how an upstream failure reaches us via OpenRouter. Kept deliberately narrow (the enum name
	// must appear) so a genuine bad-argument failure, e.g. an invalid model id, stays fatal.
	private static bool IsUnknownFinishReason(Exception e) =>
		e is ArgumentException && e.Message.Contains("ChatFinishReason", StringComparison.Ordinal);

	// Retryable HTTP status classes: request timeout / conflict / too-early / rate-limit and 5xx.
	private static bool IsTransientStatus(int status) =>
		status is 408 or 409 or 425 or 429 or 500 or 502 or 503 or 504;
}
