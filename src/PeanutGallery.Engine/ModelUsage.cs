namespace PeanutGallery.Engine;

/// <summary>
/// What one model call (or a sum of them) actually cost, as the provider reported it.
///
/// <para>Reported, never estimated. A character- or word-derived guess is worse than no number at
/// all here: it reads like a measurement, and the whole point of carrying this is to replace
/// arguments about where the spend goes with the provider's own accounting. A provider that sends
/// no usage block yields <see cref="Zero"/>, which the summary renders as "—" rather than as free.</para>
/// </summary>
/// <param name="Reported">Whether these numbers came from the provider at all. Carried explicitly
/// because "we were told nothing" and "we were told zero" are different facts, and a count alone
/// cannot distinguish them: a cache hit can legitimately bill 0/0, and rendering that as unknown is
/// as wrong as rendering unknown as free.</param>
/// <param name="CachedInputTokens">How many of <paramref name="InputTokens"/> were served from the
/// provider's prompt cache (OpenAI-compatible <c>usage.prompt_tokens_details.cached_tokens</c>). A
/// SUBSET of <paramref name="InputTokens"/>, not additional spend — never add it into <see cref="Total"/>.
/// Zero means "no cache hit reported," which is indistinguishable from "provider doesn't report this
/// field"; that ambiguity is acceptable here because, unlike the top-level counts, no caller needs to
/// tell "definitely zero" apart from "unknown" for this figure.</param>
public sealed record ModelUsage(long InputTokens, long OutputTokens, bool Reported = true, long CachedInputTokens = 0)
{
	/// <summary>The provider said nothing. The identity for <c>+</c>, so seeding an accumulator
	/// with it leaves an all-silent run correctly reading as unknown rather than as free.</summary>
	public static ModelUsage Unreported { get; } = new(0, 0, Reported: false);

	/// <summary>A reported zero — a cache hit, or a call that genuinely cost nothing.</summary>
	public static ModelUsage Zero { get; } = new(0, 0);

	public long Total => InputTokens + OutputTokens;

	/// <summary>True when nothing in this figure came from a provider — render as unknown, not free.</summary>
	public bool IsUnreported => !Reported;

	/// <summary>
	/// Sums two figures. The result counts as reported if EITHER side was: a run where one call
	/// reported and another did not is partially known, and showing the known part beats claiming
	/// the whole run is a mystery — the alternative silently hides real spend behind one silent call.
	/// </summary>
	public static ModelUsage operator +(ModelUsage a, ModelUsage b) =>
		new(
			a.InputTokens + b.InputTokens,
			a.OutputTokens + b.OutputTokens,
			a.Reported || b.Reported,
			a.CachedInputTokens + b.CachedInputTokens);
}

/// <summary>
/// A model's reply plus what it cost. The port returns both together because they are one fact
/// about one call: threading usage out of band would either need a correlation id on the request
/// (polluting a core value with an observability concern) or a shared sink the concurrent fan-out
/// cannot attribute back to a persona.
/// </summary>
/// <param name="Attempts">How many times the call was actually issued to land this reply — 1 when it
/// succeeded first try, more when the retry loop re-routed past a transient failure. Summed across a
/// persona's calls, it is the "did retries recover or exhaust?" signal the metrics ledger tracks.</param>
/// <param name="Truncated">The provider stopped because the reply hit the output-token cap
/// (finish_reason:length), so <paramref name="Text"/> is almost certainly incomplete JSON. A caller
/// must not treat it as a normal unreadable reply — shrinking the prompt or re-asking cannot fit a
/// too-long reply into the cap; the fix is a bigger cap or a shorter review.</param>
public sealed record ModelReply(string Text, ModelUsage Usage, int Attempts = 1, bool Truncated = false)
{
	/// <summary>A reply from something that cannot report usage — a stub, a fixture, an offline run.</summary>
	public static ModelReply Untracked(string text) => new(text, ModelUsage.Unreported);
}
