using System.Collections.Generic;
using System.Linq;
using PeanutGallery.Core;

namespace PeanutGallery.Engine;

/// <summary>
/// Folds a completed <see cref="ReviewRunResult"/> into the pure <see cref="RunMetrics"/> value.
/// Lives in the engine (not the core) for the same reason <see cref="RunSummary"/> does: it reads
/// engine result types (<see cref="PersonaResult"/>, <see cref="ModelUsage"/>), but it is itself a
/// pure, clock-free fold — the shell supplies the run identity and timestamp via <see cref="RunContext"/>.
/// </summary>
public static class MetricsCollector
{
	public static RunMetrics From(ReviewRunResult run, RunContext context) =>
		new(context, run.Personas.Select(ToMetric).ToList());

	private static PersonaMetric ToMetric(PersonaResult p)
	{
		var c = p.Contribution;
		var posted = c?.Posted.Count ?? p.FindingCount;
		var refuted = c?.Refuted.Count ?? 0;
		var suppressed = c?.Suppressed ?? 0;
		// What the model actually raised this turn, before the gate and the adversarial pass took
		// findings off the board: posted + suppressed + refuted. On a failed/unchanged persona there
		// is no contribution, so this collapses to whatever count we have.
		var raised = posted + suppressed + refuted;

		// The author's verdict on this persona's standing findings, reconciled earlier this turn and
		// until now discarded. A persona with no contribution (Failed, Unchanged) ruled on nothing,
		// so it reports 0 — which is a fact about this turn, not a missing field: the run line is
		// stamped at RunMetrics.VerdictSchema either way.
		var resolved = c?.Resolved.Count ?? 0;
		var withdrawn = c?.Withdrawn.Count ?? 0;

		var review = p.Observability.Usage ?? ModelUsage.Unreported;
		var verify = p.Observability.VerifyUsage ?? ModelUsage.Unreported;

		// Lens/tier come off the persona, which we only have via the contribution; a failed or
		// unchanged persona has none, so they fall back to empty (the report groups by id then).
		return new PersonaMetric(
			p.PersonaId,
			p.PersonaName,
			c?.Persona.Lens ?? "",
			p.Observability.Model,
			c?.Persona.Tier.ToString() ?? "",
			p.Outcome.ToString(),
			(long)p.Observability.Elapsed.TotalMilliseconds,
			review.InputTokens,
			review.OutputTokens,
			verify.InputTokens,
			verify.OutputTokens,
			raised,
			posted,
			refuted,
			suppressed,
			// Prefer the kind the runner KNEW structurally (a caught TimeoutException) over guessing
			// from the reason text; fall back to classifying the reason for failures whose text comes
			// from outside our shell (an SDK/provider error), where there is no structural signal.
			p.Observability.FailureKind ?? FailureClassifier.Classify(p.Observability.FailureReason),
			// The review path counts every call, on success AND on an exhausted throw, so a persona
			// that issued calls always reports >= 1 (including the failures). 0 means it never issued
			// a call at all — a config error, or an unchanged persona that was skipped.
			p.Observability.Attempts,
			review.CachedInputTokens,
			verify.CachedInputTokens,
			resolved,
			withdrawn);
	}
}
