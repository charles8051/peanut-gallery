using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;
using PeanutGallery.Core;

namespace PeanutGallery.Engine;

/// <summary>
/// The real reviewer: maps a core <see cref="ReviewRequest"/> onto a
/// Microsoft.Extensions.AI <see cref="IChatClient"/> over an OpenAI-compatible
/// provider (OpenRouter/Fireworks), runs the call, and hands the model's text to the
/// pure <see cref="FindingsParser"/>. Diff-tier personas make a single chat call;
/// agent-tier personas get the read-only <see cref="RepoTools"/> and the function
/// invocation loop. The impure surface here is small on purpose: build a client, send
/// messages, parse the reply (pure, in the core).
/// </summary>
public sealed class ChatClientReviewer : IReviewer
{
	// The fail-fast deadline for the first (non-final) attempt; later non-final attempts double it,
	// the final attempt gets the full budget. See RetrySchedule for the property this guarantees.
	private static readonly TimeSpan FirstAttempt = TimeSpan.FromSeconds(240);

	private readonly IReadOnlyDictionary<string, ProviderConfig> _providers;
	private readonly Func<string, string?> _env;
	private readonly TimeSpan _timeout;
	private readonly int _maxAttempts;
	private readonly bool _jsonMode;
	private readonly int _maxOutputTokens;
	private readonly Func<ProviderConfig, string, string, TimeSpan, IChatClient> _clientFactory;

	/// <param name="providers">Providers from the config.</param>
	/// <param name="env">API-key resolver (defaults to environment variables; injectable for tests).</param>
	/// <param name="perCallTimeout">Budget for the final model attempt (default 10 min); a slower call becomes a failure finding instead of stalling the run.</param>
	/// <param name="maxAttempts">How many times to (re)issue a call before giving up (default 2 = one retry). A transient failure re-routes on the retry; the final attempt gets the full <paramref name="perCallTimeout"/>.</param>
	/// <param name="jsonMode">Ask the provider to constrain diff-tier replies to JSON. Off by default: it is a per-model gamble (see the remarks where it is applied), and the parser plus the repair re-ask already cover a wrapped reply.</param>
	public ChatClientReviewer(
		IEnumerable<ProviderConfig> providers,
		Func<string, string?>? env = null,
		TimeSpan? perCallTimeout = null,
		int maxAttempts = 2,
		bool jsonMode = false,
		int maxOutputTokens = ReviewBudget.DefaultMaxOutputTokens,
		Func<ProviderConfig, string, string, TimeSpan, IChatClient>? clientFactory = null)
	{
		_providers = providers.ToDictionary(p => p.Name, StringComparer.Ordinal);
		_env = env ?? Environment.GetEnvironmentVariable;
		// The real factory builds an OpenAI client per call; a test injects a fake to capture the
		// ChatOptions (that MaxOutputTokens/Temperature reach the wire) without a live provider.
		_clientFactory = clientFactory ?? ProviderClientFactory.Create;
		// Default per-call budget = 10 min (matches the perCallTimeout XML doc above).
		_timeout = perCallTimeout ?? TimeSpan.FromMinutes(10);
		_maxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
		_jsonMode = jsonMode;
		_maxOutputTokens = maxOutputTokens > 0 ? maxOutputTokens : ReviewBudget.DefaultMaxOutputTokens;
	}

	public async Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default)
	{
		try
		{
			var reply = await CompleteAsync(task.Request, task.Repo.Path, ct);
			return new PersonaReview(task.Persona, task.Repo, FindingsParser.Parse(reply.Text), reply.Text);
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			return Failure(task, e.Message);
		}
	}

	/// <summary>
	/// The raw model call: resolve the provider/key, build the client (with read-only
	/// tools for the agent tier), send the request, return the reply text. THROWS on a
	/// missing provider/key or a provider error — the stateful caller catches so a bad
	/// turn doesn't advance the session.
	/// </summary>
	public async Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
	{
		if (!_providers.TryGetValue(request.Model.Provider, out var provider))
		{
			throw new InvalidOperationException($"no provider '{request.Model.Provider}' is configured");
		}

		var key = _env(provider.ApiKeyEnv);
		if (string.IsNullOrWhiteSpace(key))
		{
			throw new InvalidOperationException($"missing API key: environment variable {provider.ApiKeyEnv} is not set");
		}

		// The SDK's per-attempt NetworkTimeout is the *outer* backstop: set it looser than the
		// largest per-attempt TimeBox (the final attempt's full _timeout) so the TimeBox always
		// fires first and owns the deadline. This is what makes the retry work — earlier attempts
		// get a short TimeBox (RetrySchedule) that abandons a hung route well before the SDK's own
		// timeout, so we can re-issue the call (OpenRouter re-routes) instead of sitting on one
		// stuck attempt for the whole budget. Were the network timeout to fire first, TimeBox
		// reclassifies its cancellation as a timeout too (see its cancellation-contract remarks).
		using var client = _clientFactory(
			provider, request.Model.ModelId, key, _timeout + TimeSpan.FromSeconds(30));
		var messages = request.Messages.Select(ToChatMessage).ToList();
		// Cap the output. A review reply is small; without this a model can run away to its full
		// context window (observed: minimax-m3 emitting 65,536 tokens, finish_reason:length), which is
		// truncated garbage, expensive, and — on a slow provider route — slow enough to blow the
		// per-persona budget (the "hang"). The cap turns a runaway into a bounded, cheap call.
		var options = new ChatOptions
		{
			Temperature = (float)request.Temperature,
			TopP = request.TopP is double p ? (float)p : null,
			TopK = request.TopK,
			MaxOutputTokens = _maxOutputTokens,
		};
		if (request.Tier == ReviewTier.Agent)
		{
			options.Tools = new RepoTools(repoPath).AsTools();
		}
		else if (_jsonMode)
		{
			// Constrain the reply to JSON, removing the prose/code-fence wrapping the parser
			// otherwise digs through. Diff tier only: JSON mode and tool-calling conflict on
			// several providers, and the agent tier needs its tools.
			//
			// OFF by default, and that default is empirical, not timid: with JSON mode on,
			// minimax-m3 via OpenRouter returned a completely EMPTY reply where the same model
			// on the same diff answered normally without it. "Enforcing" structure can silently
			// cost you the whole answer, and support varies per model, not per provider - which
			// is the granularity the config has. So this is an opt-in escape hatch
			// (PG_JSON_MODE=1) rather than the default path; the parser's unreadable/clean
			// distinction and the repair re-ask are the durable fix.
			options.ResponseFormat = ChatResponseFormat.Json;
		}

		// Usage rides out on a captured local rather than through the retry loop's return type: a
		// failing attempt throws before the assignment, so the value that survives is always the
		// winning attempt's. Confined to this call's stack - no state crosses calls, so the
		// concurrent fan-out cannot interleave here.
		var usage = ModelUsage.Unreported;
		var truncated = false;
		var schedule = RetrySchedule.For(_timeout, _maxAttempts, FirstAttempt);
		var result = await RetryingModelCall.RunAsync(
			async token =>
			{
				// The SDK's mapping is the ONLY code inside this try, which is what makes the shape
				// match safe: an out-of-range 'index' escaping here cannot be ours. Re-raised as a
				// type so the retry predicate and the metrics classification both read the fact
				// rather than re-deriving it from an exception shape further out. (#158)
				ChatResponse response;
				try
				{
					response = await client.GetResponseAsync(messages, options, token);
				}
				catch (Exception e) when (TransientFailure.IsSdkMappingFailure(e))
				{
					throw new MalformedResponseException(
						"the provider returned a reply the SDK could not map (no choices) — "
							+ "usually an upstream generation that failed on the chosen route",
						e);
				}

				usage = Meter(response.Usage);
				// finish_reason:length -> the reply was cut off at MaxOutputTokens. Surface it so the
				// caller fails cleanly with a Truncated kind instead of a confusing parse error.
				truncated = response.FinishReason == ChatFinishReason.Length;
				return response.Text;
			},
			schedule,
			TransientFailure.IsRetryable,
			BackoffAsync,
			ct);
		return new ModelReply(result.Text, usage, result.Attempts, truncated);
	}

	// The provider's own accounting, or Unreported when it sent none. Never estimated from the text -
	// a guess that looks like a measurement is worse than an honest blank (see ModelUsage).
	//
	// A block carrying NEITHER count is the same fact as no block at all, so it reads as unreported
	// rather than as a reported zero. A block carrying only one is genuinely reported and kept: half
	// the truth beats none, and calling it unknown would discard a real number we were given.
	private static ModelUsage Meter(UsageDetails? usage) =>
		usage is null || (usage.InputTokenCount is null && usage.OutputTokenCount is null)
			? ModelUsage.Unreported
			: new ModelUsage(usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0, CachedInputTokens: usage.CachedInputTokenCount ?? 0);

	// Jittered backoff between attempts. Randomness + the clock are shell concerns: the retry
	// loop itself is pure over an injected delay, so tests drive it with no real sleep. The
	// exponential escalation only bites at maxAttempts >= 3; at the default 2 attempts this is
	// called once (index 0) for a single ~3s pause before the final full-budget attempt.
	private static Task BackoffAsync(int attemptIndex, CancellationToken ct)
	{
		var seconds = 3 * Math.Pow(2, attemptIndex);
		var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));
		return Task.Delay(TimeSpan.FromSeconds(seconds) + jitter, ct);
	}

	private static ChatMessage ToChatMessage(Message m) => new(
		m.Role == PeanutGallery.Core.ChatRole.System
			? Microsoft.Extensions.AI.ChatRole.System
			: Microsoft.Extensions.AI.ChatRole.User,
		m.Content);

	// A failed review is a Major finding, not a thrown exception - keeps the fan-out total.
	private static PersonaReview Failure(ReviewTask task, string message) => new(
		task.Persona,
		task.Repo,
		[new Finding(Severity.Major, string.Empty, 0, "review could not run", message)]);
}
