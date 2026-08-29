using System.Collections.Generic;
using System.Linq;

namespace PeanutGallery.Core;

/// <summary>
/// How a persona's review ended when it did not produce findings — the machine-readable classes we
/// want to count across runs, so "the panel is flaky" becomes "N% of minimax calls return a
/// finish_reason:error this week". Derived purely from the failure reason text (the shell has no
/// richer signal to pass), so it is a best-effort bucketing, with <see cref="Other"/> the catch-all.
/// </summary>
public enum FailureClass
{
	/// <summary>The persona reviewed without error (it may still have reported zero findings).</summary>
	None,

	/// <summary>A per-attempt deadline elapsed — a slow or hung route.</summary>
	Timeout,

	/// <summary>The upstream generation failed and OpenRouter reported finish_reason:"error" (the SDK
	/// threw an unknown-ChatFinishReason). See #113.</summary>
	FinishReasonError,

	/// <summary>The model returned no content, even after the shrink-retry ladder. See #109.</summary>
	EmptyReply,

	/// <summary>The model hit its output-token cap (finish_reason:length), so the reply was
	/// truncated — almost always incomplete JSON. The output was too long, or the model was looping.</summary>
	Truncated,

	/// <summary>The provider returned a reply the SDK could not map at all — in practice a completion
	/// carrying NO CHOICES, which surfaces as an out-of-range index while parsing, before any usage is
	/// metered. Distinct from <see cref="EmptyReply"/>: a choice whose content is empty or null maps
	/// fine and belongs on the shrink-retry ladder, whereas here there was no choice to read at
	/// all. See #158.</summary>
	MalformedResponse,

	/// <summary>Some other transient provider/transport error (5xx, dropped connection).</summary>
	Transient,

	/// <summary>A configuration or auth failure — missing key, unknown provider (same every attempt).</summary>
	Config,

	/// <summary>Anything the buckets above did not catch.</summary>
	Other,
}

/// <summary>Pure classification of a persona failure reason into a <see cref="FailureClass"/>.</summary>
public static class FailureClassifier
{
	public static FailureClass Classify(string? reason)
	{
		if (string.IsNullOrWhiteSpace(reason))
		{
			return FailureClass.None;
		}

		// Ordered most-specific first. Substring, case-insensitive: the reasons are our own messages
		// (or an SDK message we already match by substring in TransientFailure), not structured codes.
		bool Has(string s) => reason.Contains(s, System.StringComparison.OrdinalIgnoreCase);

		// NB: a per-persona TimeBox deadline is NOT matched here — the shell catches the
		// TimeoutException and records FailureClass.Timeout structurally (PersonaObservability.
		// FailureKind), so the pure core never has to know how the shell worded that message. This
		// classifier only buckets reasons whose text originates OUTSIDE our shell (an SDK/provider
		// error), where there is no structural signal to prefer.
		// NB: MalformedResponse has NO arm here, deliberately. Only the model-call boundary can
		// establish that a reply came back unmappable rather than that we hit a bug of our own, so
		// it is tagged structurally (ReviewRunner) and never inferred from prose — any substring we
		// could match on, ours included, is text an unrelated failure may also carry. Ambiguous text
		// stays Other, which is the honest answer when origin is unknowable. (#158)
		return Has("ChatFinishReason") ? FailureClass.FinishReasonError
			: Has("empty reply") ? FailureClass.EmptyReply
			: Has("timed out") || Has("timeout") ? FailureClass.Timeout
			: Has("missing API key") || Has("no provider") || Has("unknown provider") ? FailureClass.Config
			: Has("could not be parsed") ? FailureClass.Other
			: FailureClass.Other;
	}
}

/// <summary>The run-identifying context the shell supplies (the core has no clock or repo identity).</summary>
/// <param name="TimestampUtc">ISO-8601 UTC instant the run finished, stamped by the shell.</param>
/// <param name="Shape">What this run's diff looked like. Recorded per run so the PR's TRAJECTORY is
/// a fold over facts rather than something any single run has to know - see <see cref="Trajectory"/>.
/// Appended with a default so pre-existing ledger lines keep parsing.
/// <para><b>Null means NOT RECORDED, which is not the same as an empty diff, and nothing may
/// collapse the two.</b> There was briefly a <c>Shape ?? Empty</c> convenience property here, and it
/// silently disabled the whole measurement: every ledger line predating the field became a factual
/// zero-added baseline, which forced <see cref="Trajectory"/>'s growth to 1.0 and meant the trigger
/// could never fire on any PR that already had history - which is all of them. A measurement that
/// reports "nothing ever trips" because it erased its own inputs is worse than no measurement, so
/// callers handle the null.</para></param>
public sealed record RunContext(
	string Repo, int Pr, string Sha, string TimestampUtc, string Panel, DiffShape? Shape = null);

/// <summary>One persona's contribution to a run, as a flat record of countable facts.</summary>
public sealed record PersonaMetric(
	string Id,
	string Name,
	string Lens,
	string Model,
	string Tier,
	string Outcome,
	long ElapsedMs,
	long InputTokens,
	long OutputTokens,
	long VerifyInputTokens,
	long VerifyOutputTokens,
	int Raised,
	int Posted,
	int Refuted,
	int Suppressed,
	FailureClass Failure,
	// Total model calls issued on the review path (>1 means the retry loop re-routed past a
	// transient failure). With Outcome, this is the retry-recovered-vs-exhausted signal. Defaults to
	// 1 so a record built before this field existed reads as "one call, no retry".
	int Attempts = 1,
	// A SUBSET of InputTokens/VerifyInputTokens (not additional spend) served from the provider's
	// prompt cache. Appended at the tail, both defaulting to 0, so every pre-existing positional
	// PersonaMetric construction (tests, older ledger lines) keeps compiling/parsing without change —
	// see ModelUsage.CachedInputTokens for why "0" reading as "no hit reported" is acceptable here.
	long CachedInputTokens = 0,
	long VerifyCachedInputTokens = 0,
	// What the AUTHOR did with this persona's standing findings this turn: titles they fixed
	// (Resolved) and titles they explained away as intentional or wrong (Withdrawn). Every other
	// count on this record is something the tool did to itself; these two are the only ones that
	// carry a human's judgement, which is why they are worth ledger bytes.
	//
	// A 0 here is NOT self-describing. On a line written at RunMetrics.VerdictSchema or later it
	// means "the author ruled on nothing"; on an older line it means the field did not exist. The
	// run's RunMetrics.SchemaVersion is the only thing that tells the two apart, so no consumer may
	// average these across a mixed corpus without consulting RunMetrics.RecordsAuthorVerdicts.
	int Resolved = 0,
	int Withdrawn = 0);

/// <summary>
/// Everything worth counting about one review run, as an immutable value — the unit that gets
/// serialized to one JSON line, appended to the PR's metrics ledger, and later folded across runs
/// into a dogfooding report. Run-level totals are derived from <see cref="Personas"/> rather than
/// stored, so the record has a single source of truth and cannot disagree with itself.
/// </summary>
/// <param name="SchemaVersion">The schema the line this record came from was WRITTEN at, not the
/// schema this build knows. Defaults to <see cref="Schema"/> because anything constructed in-process
/// is current by definition; <see cref="MetricsCodec"/> overrides it from the line's <c>v</c> key.
/// It exists so a reader can tell a field that was recorded as zero from a field that did not exist
/// when the line was written — see <see cref="RecordsAuthorVerdicts"/>.</param>
public sealed record RunMetrics(
	RunContext Context, IReadOnlyList<PersonaMetric> Personas, int SchemaVersion = RunMetrics.Schema)
{
	/// <summary>The schema every line this build writes is stamped with.</summary>
	public const int Schema = 2;

	/// <summary>
	/// The schema at which <see cref="PersonaMetric.Resolved"/> and <see cref="PersonaMetric.Withdrawn"/>
	/// started being recorded. Kept separate from <see cref="Schema"/> so a later bump does not
	/// silently move this test: a line at or above this version has verdict data (possibly a genuine
	/// zero), and a line below it has none at all.
	/// </summary>
	public const int VerdictSchema = 2;

	/// <summary>
	/// Whether this run's line carries author verdicts at all. False for every line written before
	/// the field existed, whose zeros are ABSENCE and must be excluded from any ratio rather than
	/// averaged in as agreement — the same failure <see cref="RunContext.Shape"/> already documents
	/// for diff shape, where collapsing "not recorded" into a factual zero disabled the measurement.
	/// </summary>
	public bool RecordsAuthorVerdicts => SchemaVersion >= VerdictSchema;

	public int Degraded => Personas.Count(p => p.Failure != FailureClass.None);

	public int PostedTotal => Personas.Sum(p => p.Posted);

	public int RefutedTotal => Personas.Sum(p => p.Refuted);

	public int SuppressedTotal => Personas.Sum(p => p.Suppressed);

	public int RaisedTotal => Personas.Sum(p => p.Raised);

	/// <summary>Standing findings the author fixed this run. Meaningless unless
	/// <see cref="RecordsAuthorVerdicts"/> — on an older line it is 0 because nobody wrote it down.</summary>
	public int ResolvedTotal => Personas.Sum(p => p.Resolved);

	/// <summary>Standing findings the author explained away this run. Same caveat as
	/// <see cref="ResolvedTotal"/>.</summary>
	public int WithdrawnTotal => Personas.Sum(p => p.Withdrawn);

	public long InputTokens => Personas.Sum(p => p.InputTokens + p.VerifyInputTokens);

	public long OutputTokens => Personas.Sum(p => p.OutputTokens + p.VerifyOutputTokens);

	/// <summary>Subset of <see cref="InputTokens"/> served from the provider's prompt cache.</summary>
	public long CachedInputTokens => Personas.Sum(p => p.CachedInputTokens + p.VerifyCachedInputTokens);

	/// <summary>Share of input tokens that were cache hits, or null when there is nothing to divide by.</summary>
	public double? CacheHitRate => InputTokens == 0 ? null : (double)CachedInputTokens / InputTokens;

	/// <summary>The critical-path latency: personas run concurrently, so the slowest is the run's wall time.</summary>
	public long SlowestMs => Personas.Count == 0 ? 0 : Personas.Max(p => p.ElapsedMs);
}
