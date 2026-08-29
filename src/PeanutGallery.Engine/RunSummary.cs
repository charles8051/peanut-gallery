using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PeanutGallery.Core;

namespace PeanutGallery.Engine;

/// <summary>
/// Pure rendering of a run's per-persona results into the two GitHub Actions observability
/// surfaces: a Job Summary table (durable per run, survives the self-overwriting PR comment) and
/// degradation annotations. Neither changes the run's conclusion — a degraded persona shows as a
/// ⚠️ row and a yellow annotation, but the run stays green (degrade-to-a-finding is intentional).
/// The shell (the CLI) does the IO — writing the summary file, printing the annotation lines;
/// this is pure over the results so it is unit-tested with no Actions runtime.
/// </summary>
public static class RunSummary
{
	/// <summary>
	/// How many reviewers degraded (timeout, truncation, provider error) this run — the one signal
	/// the opt-in fail gate (#130) and the annotations both key off <see cref="PersonaOutcome.Failed"/>.
	///
	/// <para>This is the same runner-computed fact the panel comment's degradation banner surfaces:
	/// <c>PanelMember.Reported</c> is the Core projection of this same per-persona outcome (derived at
	/// one site in <see cref="ReviewRunner"/>), because Core must not import <see cref="PersonaOutcome"/>.
	/// The gate reads it directly here so it works in per-persona mode too, where there is no panel
	/// comment. Two faithful projections of one fact, not two independent predicates — pinned in
	/// <c>PanelCommentModeTests</c>.</para>
	/// </summary>
	public static int DegradedCount(IReadOnlyList<PersonaResult> personas) =>
		personas.Count(p => p.Outcome == PersonaOutcome.Failed);

	/// <summary>The Markdown written to <c>$GITHUB_STEP_SUMMARY</c> for this run.</summary>
	public static string RenderStepSummary(
		IReadOnlyList<PersonaResult> personas, string slug, int pr, string headSha)
	{
		var sb = new StringBuilder();
		sb.Append("### Peanut Gallery — ").Append(slug).Append(" #").Append(pr)
			.Append(" @ `").Append(Sha.Short(headSha)).Append("`\n\n");

		if (personas.Count == 0)
		{
			sb.Append("_No personas assigned to this repo._\n");
			return sb.ToString();
		}

		var degraded = personas.Count(p => p.Outcome == PersonaOutcome.Failed);
		if (degraded > 0)
		{
			sb.Append("> [!WARNING]\n> ")
				.Append(degraded).Append(degraded == 1 ? " reviewer degraded" : " reviewers degraded")
				.Append(" this run (⚠️ below). The run stays green; the reviewer retries on the next push.\n\n");
		}

		sb.Append("| Persona | Model | Outcome | Findings | Latency | Review tokens | Verify tokens |\n");
		sb.Append("|---|---|---|---|---|---|---|\n");
		foreach (var p in personas)
		{
			sb.Append("| ").Append(p.PersonaName)
				.Append(" | `").Append(p.Observability.Model).Append('`')
				.Append(" | ").Append(OutcomeCell(p.Outcome))
				.Append(" | ").Append(p.Outcome == PersonaOutcome.Reviewed ? p.FindingCount.ToString() : "—")
				.Append(" | ").Append(Latency(p))
				.Append(" | ").Append(Tokens(p.Observability.Usage))
				.Append(" | ").Append(Tokens(p.Observability.VerifyUsage))
				.Append(" |\n");
		}

		AppendSpend(sb, personas);

		AppendRefutations(sb, personas);

		var failed = personas.Where(p => p.Outcome == PersonaOutcome.Failed).ToList();
		if (failed.Count > 0)
		{
			sb.Append('\n');
			foreach (var p in failed)
			{
				sb.Append("- ⚠️ **").Append(p.PersonaName).Append("** — ")
					.Append(OneLine(p.Observability.FailureReason)).Append('\n');
			}
		}

		return sb.ToString();
	}

	/// <summary>
	/// One <c>::warning::</c> workflow command per degraded persona. Printed to stdout by the CLI,
	/// GitHub renders each as a run annotation — visible on the run and in the checks rollup without
	/// failing the job.
	/// </summary>
	public static IReadOnlyList<string> Annotations(IReadOnlyList<PersonaResult> personas) =>
		personas
			.Where(p => p.Outcome == PersonaOutcome.Failed)
			.Select(p => $"::warning title=Peanut Gallery::{p.PersonaName} ({p.Observability.Model}) degraded: {OneLine(p.Observability.FailureReason)}")
			.ToList();

	/// <summary>
	/// The run's total spend, and how much of it the adversarial pass accounts for.
	///
	/// <para>The share is the number this table exists to surface. Verification re-sends the entire
	/// review request (<see cref="PeanutGallery.Core.SessionPlanner.Verify"/>), so it can cost about
	/// as much as the review it checks - and whether that is worth paying is a judgement nobody can
	/// make from a single summed figure. Rendered only when a provider actually reported usage;
	/// an unreported run says so rather than claiming the run was free.</para>
	/// </summary>
	private static void AppendSpend(StringBuilder sb, IReadOnlyList<PersonaResult> personas)
	{
		// Seeded with Unreported (the identity for +), so a run where no provider reported stays
		// unreported instead of being turned into a confident zero by the seed itself.
		var review = personas.Aggregate(
			ModelUsage.Unreported, (acc, p) => acc + (p.Observability.Usage ?? ModelUsage.Unreported));
		var verify = personas.Aggregate(
			ModelUsage.Unreported, (acc, p) => acc + (p.Observability.VerifyUsage ?? ModelUsage.Unreported));
		var total = review + verify;

		sb.Append('\n');
		if (total.IsUnreported)
		{
			sb.Append("_Token usage not reported by the provider for this run._\n");
			return;
		}

		sb.Append("**Spend:** ").Append(total.InputTokens).Append(" in / ")
			.Append(total.OutputTokens).Append(" out (")
			.Append(total.Total).Append(" tokens)");

		if (!verify.IsUnreported && total.Total > 0)
		{
			// "<1%" rather than a rounded "0%": a small share is not a free one, and this line
			// exists to inform a decision about whether to keep paying for the pass at all.
			var share = 100.0 * verify.Total / total.Total;
			var rendered = share > 0 && share < 0.5 ? "<1" : share.ToString("F0");
			sb.Append(" — the adversarial pass is ").Append(rendered)
				.Append("% of it (").Append(verify.Total)
				.Append(verify.Total == 1 ? " token)" : " tokens)");
		}

		// Cache hits are a subset of input tokens, not additional spend - rendered only when the
		// provider actually reported one, same "silence over a confident zero" rule as the rest of
		// this method.
		if (total.CachedInputTokens > 0 && total.InputTokens > 0)
		{
			var rate = 100.0 * total.CachedInputTokens / total.InputTokens;
			var renderedRate = rate > 0 && rate < 0.5 ? "<1" : rate.ToString("F0");
			sb.Append(" — ").Append(renderedRate).Append("% of input tokens were cache hits (")
				.Append(total.CachedInputTokens).Append(')');
		}

		sb.Append(".\n");
	}

	/// <summary>
	/// What the adversarial pass dropped this run, and on what grounds.
	///
	/// <para>Here rather than only in the PR comment because this summary is <b>durable per run</b>
	/// and the comment overwrites itself: a refutation made on turn 2 is gone from the comment by
	/// turn 3. Auditing whether the pass is refuting true findings is exactly the question that needs
	/// the history, and answering it once already meant reconstructing drops from titles alone.</para>
	/// </summary>
	private static void AppendRefutations(StringBuilder sb, IReadOnlyList<PersonaResult> personas)
	{
		var refuted = personas
			.Where(p => p.Contribution is not null)
			.SelectMany(p => p.Contribution!.Refuted.Select(r => (p.PersonaName, r)))
			.ToList();
		if (refuted.Count == 0)
		{
			return;
		}

		// Plain markdown, not a <details> block: this interpolates model-authored titles, and a
		// title carrying the closing tag would end the block early and let everything after it
		// render as summary text we appear to have written.
		sb.Append("\n**").Append(refuted.Count)
			.Append(refuted.Count == 1 ? " finding" : " findings")
			.Append(" dropped on the adversarial pass:**\n");
		foreach (var (persona, r) in refuted)
		{
			sb.Append("- **").Append(CommentRenderer.OneLine(r.Title))
				.Append("** _(").Append(persona).Append(")_");
			var why = CommentRenderer.OneLine(r.Why);
			if (why.Length > 0)
			{
				sb.Append("  \n  ").Append(why);
			}

			sb.Append('\n');
		}
	}

	private static string Tokens(ModelUsage? usage)
	{
		if (usage is null || usage.IsUnreported)
		{
			return "—";
		}

		var cell = $"{usage.InputTokens} / {usage.OutputTokens}";
		return usage.CachedInputTokens > 0 ? $"{cell} ({usage.CachedInputTokens} cached)" : cell;
	}

	private static string OutcomeCell(PersonaOutcome outcome) => outcome switch
	{
		PersonaOutcome.Reviewed => "✅ reviewed",
		PersonaOutcome.Failed => "⚠️ degraded",
		PersonaOutcome.Unchanged => "➖ unchanged",
		_ => outcome.ToString(),
	};

	private static string Latency(PersonaResult p) =>
		p.Outcome == PersonaOutcome.Unchanged ? "—" : $"{p.Observability.Elapsed.TotalSeconds:F0}s";

	// Failure reasons keep their own fallback wording; the flattening itself is CommentRenderer's,
	// shared so this cannot drift from how the comment renderers treat the same model-authored text.
	// Three private near-copies is exactly how two of them ended up handling LF but not CR.
	private static string OneLine(string? text) =>
		string.IsNullOrWhiteSpace(text) ? "review could not run" : CommentRenderer.OneLine(text);
}
