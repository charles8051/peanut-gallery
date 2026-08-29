using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PeanutGallery.Engine;

/// <summary>The result of a (possibly retried) model call: the reply text and how many attempts it took.</summary>
public sealed record ModelCallResult(string Text, int Attempts);

/// <summary>
/// Thrown at the model-call boundary when the provider's reply could not be mapped into a response
/// at all — in practice a completion carrying no choices, which is how OpenRouter reports an
/// upstream generation it could not route. Raised by <see cref="ChatClientReviewer"/>, the one place
/// that knows the SDK's mapping is the only code in scope, so everything downstream can classify it
/// by TYPE instead of re-guessing the shape. See <see cref="TransientFailure.IsSdkMappingFailure"/>.
/// </summary>
public sealed class MalformedResponseException(string message, Exception inner)
	: Exception(message, inner);

/// <summary>Thrown when a model call is abandoned after exhausting its attempts (or on a fatal first failure).</summary>
public sealed class ModelCallException : Exception
{
	public ModelCallException(string message, Exception inner, int attempts)
		: base(message, inner)
	{
		Attempts = attempts;
	}

	/// <summary>How many times the call was issued before it was abandoned — so a caller counting
	/// model calls can account for an EXHAUSTED retry, not just a successful one.</summary>
	public int Attempts { get; }
}

/// <summary>
/// Runs a single model call under a bounded, escalating retry loop — the shell's re-trigger,
/// done in-process so a transient flake never needs a human to push again. Each attempt runs
/// under its own <see cref="TimeBox"/> deadline (from <see cref="RetrySchedule"/>); a
/// <see cref="TransientFailure">transient</see> failure with attempts left backs off and
/// re-issues the call (OpenRouter re-routes on the fresh request), while a fatal failure or the
/// exhausted last attempt throws. Outer cancellation always propagates unchanged.
///
/// This is deliberately its own testable unit: the model call, the retryability predicate, and
/// the backoff delay are all injected, so the loop's behaviour (retry counting, exhaustion,
/// fatal short-circuit, cancellation) is exercised with no network and no real sleeps.
/// </summary>
public static class RetryingModelCall
{
	public static async Task<ModelCallResult> RunAsync(
		Func<CancellationToken, Task<string>> attempt,
		IReadOnlyList<TimeSpan> schedule,
		Func<Exception, bool> isRetryable,
		Func<int, CancellationToken, Task> delayBeforeRetry,
		CancellationToken ct = default)
	{
		var total = schedule.Count;
		for (var i = 0; i < total; i++)
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				var text = await TimeBox.RunAsync(attempt, schedule[i], ct);
				return new ModelCallResult(text, i + 1);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				// The caller asked to stop — tear down cleanly, never retry.
				throw;
			}
			catch (Exception e)
			{
				var isLast = i == total - 1;
				if (isLast || !isRetryable(e))
				{
					throw Enrich(e, attempts: i + 1);
				}

				await delayBeforeRetry(i, ct);
			}
		}

		// schedule is guaranteed non-empty by RetrySchedule.For (maxAttempts >= 1), so the loop
		// always returns or throws; this is unreachable and exists only to satisfy the compiler.
		throw new InvalidOperationException("retry loop exited without a result");
	}

	// Preserve the original exception on a first-attempt failure (unchanged UX for e.g. a bad key);
	// only annotate the attempt count once we actually retried, so the message stays honest.
	private static Exception Enrich(Exception e, int attempts) =>
		attempts <= 1 ? e : new ModelCallException($"{e.Message} (after {attempts} attempts)", e, attempts);
}
