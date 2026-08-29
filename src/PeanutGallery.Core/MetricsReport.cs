using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PeanutGallery.Core;

/// <summary>
/// A pure fold of many <see cref="RunMetrics"/> (across runs and PRs) into the dogfooding numbers
/// that actually drive decisions: how often the panel fails and why, how much of what it finds the
/// verification pass kills, and what a review costs in latency and tokens. No IO, no clock — the
/// shell gathers the records (scraping the per-PR ledgers) and renders the result.
/// </summary>
public static class MetricsReport
{
	/// <summary>Aggregate stats for one model or one persona across the window.</summary>
	/// <param name="Resolved">Standing findings the author fixed, summed over the persona-reviews in
	/// this row that actually recorded verdicts. Rows from older ledger lines contribute nothing here
	/// and are counted in <paramref name="PreVerdictReviews"/> instead.</param>
	/// <param name="Withdrawn">Standing findings the author explained away, same basis.</param>
	/// <param name="PreVerdictReviews">Persona-reviews in this row that came from ledger lines written
	/// before verdicts were recorded. They are EXCLUDED from <see cref="AgreementRate"/> rather than
	/// counted as zero agreement, and reported so the ratio's denominator is never mistaken for the
	/// row's review count.</param>
	public sealed record Row(
		string Key, int Reviews, int Failures, string TopFailure,
		int Raised, int Posted, int Refuted, int Suppressed,
		long MedianMs, long P95Ms, long InputTokens, long OutputTokens,
		long Attempts, int MultiCall, int Recovered, long CachedInputTokens = 0,
		int Resolved = 0, int Withdrawn = 0, int PreVerdictReviews = 0)
	{
		public double FailureRate => Reviews == 0 ? 0 : (double)Failures / Reviews;

		/// <summary>Share of InputTokens that were cache hits, or null when there is nothing to divide by.</summary>
		public double? CacheHitRate => InputTokens == 0 ? null : (double)CachedInputTokens / InputTokens;

		/// <summary>Share of raised findings the verification pass refuted — the finding-killer rate.</summary>
		public double RefuteRate => Raised == 0 ? 0 : (double)Refuted / Raised;

		/// <summary>Average model calls per review — above 1 means the review path needed extra calls
		/// (a transient re-route, the empty-reply shrink ladder, or a JSON repair).</summary>
		public double CallsPerReview => Reviews == 0 ? 0 : (double)Attempts / Reviews;

		/// <summary>Of the reviews that took more than one model call (for ANY reason — re-route,
		/// ladder, or repair), the share that still produced a review rather than failing.</summary>
		public double RecoveryRate => MultiCall == 0 ? 0 : (double)Recovered / MultiCall;

		/// <summary>Persona-reviews in this row whose ledger line recorded author verdicts.</summary>
		public int VerdictReviews => Reviews - PreVerdictReviews;

		/// <summary>Findings the author actually ruled on — fixed, or explained away. The denominator
		/// of <see cref="AgreementRate"/>, and the only honest measure of how much evidence it has.</summary>
		public int Judged => Resolved + Withdrawn;

		/// <summary>
		/// Of the findings the author RULED ON, the share they fixed rather than explained away.
		/// <para><b>This is agreement, not precision.</b> An author can explain away a finding that
		/// was correct, and can "resolve" a title by changing something unrelated to it, so neither
		/// verdict establishes whether the finding was true — only whether the author acted on it.
		/// Null when nothing was ruled on, which includes a row made entirely of pre-verdict ledger
		/// lines; a caller must render that absence rather than substituting 0.</para>
		/// </summary>
		public double? AgreementRate => Judged == 0 ? null : (double)Resolved / Judged;
	}

	/// <summary>The whole report: the window's totals plus per-model and per-persona breakdowns.</summary>
	/// <param name="Trajectories">One per PR with at least two recorded runs, keyed by PR number.
	/// Carried so the window can answer "how often would each loop trigger fire" from data that is
	/// already being collected, before anything is built on top of either.</param>
	/// <param name="PreVerdictReviews">Persona-reviews in the window drawn from ledger lines written
	/// before author verdicts were recorded. The window is almost always mixed, so this is stated in
	/// the output: without it a reader has no way to know the agreement figure was computed over a
	/// fraction of the runs it appears to summarise.</param>
	public sealed record Report(
		int Runs, int PersonaReviews, int Failures,
		IReadOnlyList<Row> ByModel, IReadOnlyList<Row> ByPersona,
		IReadOnlyDictionary<string, int> FailureClasses,
		IReadOnlyDictionary<PrRef, Trajectory>? Trajectories = null,
		int Resolved = 0, int Withdrawn = 0, int PreVerdictReviews = 0)
	{
		public double FailureRate => PersonaReviews == 0 ? 0 : (double)Failures / PersonaReviews;

		/// <summary>Persona-reviews in the window whose ledger line recorded author verdicts.</summary>
		public int VerdictReviews => PersonaReviews - PreVerdictReviews;

		/// <summary>Findings the author ruled on across the window.</summary>
		public int Judged => Resolved + Withdrawn;

		/// <summary>Window-wide author agreement. Agreement, NOT precision — see
		/// <see cref="Row.AgreementRate"/>. Null when nothing was ruled on.</summary>
		public double? AgreementRate => Judged == 0 ? null : (double)Resolved / Judged;

		/// <summary>PRs whose trajectory trips the provisional test-bloat trigger, worst growth first.</summary>
		public IReadOnlyList<KeyValuePair<PrRef, Trajectory>> RabbitHoles =>
			(Trajectories ?? new Dictionary<PrRef, Trajectory>())
				.Where(kv => kv.Value.LooksLikeARabbitHole)
				.OrderByDescending(kv => kv.Value.Growth)
				.ToList();

		/// <summary>PRs whose trajectory trips the provisional repeat-class trigger, most returns
		/// first. Disjoint from <see cref="RabbitHoles"/> by construction — the two triggers'
		/// production-share clauses are exact complements — and reported apart from it because a
		/// test-bloat loop and a repeat-class loop are different diagnoses.</summary>
		public IReadOnlyList<KeyValuePair<PrRef, Trajectory>> RepeatClassLoops =>
			(Trajectories ?? new Dictionary<PrRef, Trajectory>())
				.Where(kv => kv.Value.LooksLikeARepeatClassLoop)
				.OrderByDescending(kv => kv.Value.RepeatRaiseTurns)
				.ThenByDescending(kv => kv.Value.RepeatShare)
				.ToList();
	}

	/// <summary>One persona-review, paired with whether its RUN recorded author verdicts. The pairing
	/// happens once, here, because the run is the only thing that knows: the version stamp is on the
	/// ledger line, not on the persona row. Everything downstream folds the flag rather than
	/// re-deriving it, so there is exactly one place that can get the old-line question wrong.</summary>
	private readonly record struct Reviewed(PersonaMetric P, bool Verdicts);

	public static Report From(IReadOnlyList<RunMetrics> runs)
	{
		var personas = runs
			.SelectMany(r => r.Personas.Select(p => new Reviewed(p, r.RecordsAuthorVerdicts)))
			.ToList();
		var byModel = personas.GroupBy(x => x.P.Model).Select(RowFor).OrderByDescending(r => r.Reviews).ToList();
		// Group by the persona Id, not the lens: a FAILED persona has no contribution and therefore no
		// lens, so grouping by lens split one persona's failures (keyed by id) from its successes
		// (keyed by lens) into two rows — making a seed reviewer that times out look like a separate
		// 100%-failing persona instead of showing its true failure rate in one place. The id is always
		// present, and for a convened persona it is already the lens slug, so the keys stay readable.
		var byPersona = personas.GroupBy(x => x.P.Id)
			.Select(RowFor).OrderByDescending(r => r.Reviews).ToList();

		var classes = personas.Where(x => x.P.Failure != FailureClass.None)
			.GroupBy(x => x.P.Failure.ToString())
			.ToDictionary(g => g.Key, g => g.Count());

		// Sum the verdicts ONLY over runs that recorded them. Numerically a pre-verdict row adds
		// zero anyway, but the filter is what makes the denominator (VerdictReviews) and the sums
		// come from the same set, so the window can state what its ratio was actually computed over.
		var recorded = personas.Where(x => x.Verdicts).ToList();
		return new Report(
			runs.Count, personas.Count,
			personas.Count(x => x.P.Failure != FailureClass.None),
			byModel, byPersona, classes, Trajectory.ByPr(runs),
			recorded.Sum(x => x.P.Resolved), recorded.Sum(x => x.P.Withdrawn),
			personas.Count - recorded.Count);
	}

	private static Row RowFor(IGrouping<string, Reviewed> g)
	{
		var items = g.Select(x => x.P).ToList();
		var recorded = g.Where(x => x.Verdicts).Select(x => x.P).ToList();
		var failures = items.Where(p => p.Failure != FailureClass.None).ToList();
		var topFailure = failures
			.GroupBy(p => p.Failure.ToString())
			.OrderByDescending(x => x.Count())
			.Select(x => x.Key)
			.FirstOrDefault() ?? "—";

		var latencies = items.Select(p => p.ElapsedMs).OrderBy(x => x).ToList();
		var multiCall = items.Where(p => p.Attempts > 1).ToList();
		return new Row(
			g.Key, items.Count, failures.Count, failures.Count == 0 ? "—" : topFailure,
			items.Sum(p => p.Raised), items.Sum(p => p.Posted), items.Sum(p => p.Refuted), items.Sum(p => p.Suppressed),
			Percentile(latencies, 0.50), Percentile(latencies, 0.95),
			items.Sum(p => p.InputTokens + p.VerifyInputTokens),
			items.Sum(p => p.OutputTokens + p.VerifyOutputTokens),
			items.Sum(p => (long)p.Attempts),
			multiCall.Count,
			multiCall.Count(p => p.Failure == FailureClass.None),
			items.Sum(p => p.CachedInputTokens + p.VerifyCachedInputTokens),
			recorded.Sum(p => p.Resolved),
			recorded.Sum(p => p.Withdrawn),
			items.Count - recorded.Count);
	}

	/// <summary>Nearest-rank percentile over an ASCENDING-sorted list; 0 for an empty list.</summary>
	public static long Percentile(IReadOnlyList<long> sortedAscending, double p)
	{
		if (sortedAscending.Count == 0)
		{
			return 0;
		}

		var rank = (int)System.Math.Ceiling(p * sortedAscending.Count);
		var index = System.Math.Clamp(rank - 1, 0, sortedAscending.Count - 1);
		return sortedAscending[index];
	}

	/// <summary>
	/// How many PRs in the window look like a loop, and of which KIND. The two triggers get their
	/// own headed block each and never share a line: one says the change stopped moving while its
	/// scaffolding ran away, the other says production code kept growing because one lens kept
	/// raising, and a reader who has to work out which a line means has been told nothing. They are
	/// disjoint by construction, so no PR appears twice.
	///
	/// <para>Reported rather than acted on, both of them: the triggers are provisional and these
	/// lines exist to find out whether they fire on the right PRs before anything is built on them.</para>
	/// </summary>
	private static void AppendTrajectories(StringBuilder sb, Report r)
	{
		var all = r.Trajectories;
		if (all is null || all.Count == 0)
		{
			return;
		}

		sb.Append("\nTrajectory: ").Append(all.Count).Append(" PR(s) with 2+ shaped runs\n");

		sb.Append("  Rabbit hole (scaffolding ran away, change stalled): ")
			.Append(r.RabbitHoles.Count).Append(" PR(s)")
			.Append(" — >=").Append(Trajectory.MinTurns).Append(" turns, >=")
			.Append(Trajectory.GrowthTrigger.ToString("0.#")).Append("x growth, <")
			.Append(Pct(Trajectory.ProductionShareTrigger))
			.Append(" of it outside tests, and the PR has production code and is growing net\n");

		foreach (var (pr, t) in r.RabbitHoles)
		{
			sb.Append("    ").Append(pr.ToString()).Append(": ").Append(t.Turns).Append(" turns, ")
				.Append(t.First.Added).Append(" -> ").Append(t.Last.Added).Append(" added (")
				.Append(t.Growth.ToString("0.0")).Append("x), ")
				.Append(Pct(t.ProductionShare)).Append(" of the growth outside tests, peak ")
				.Append(t.PeakProductionAdded).Append(" production line(s), net +")
				.Append(t.Last.Net).Append("\n");
		}

		sb.Append("  Repeat class (production grew, one lens kept raising): ")
			.Append(r.RepeatClassLoops.Count).Append(" PR(s)")
			.Append(" — one lens raising on >=").Append(Trajectory.MinRepeatTurns)
			.Append(" turns and on >=").Append(Pct(Trajectory.RepeatShareTrigger))
			.Append(" of the turns it sat, >=").Append(Pct(Trajectory.ProductionShareTrigger))
			.Append(" of the growth outside tests, and the PR is growing net")
			.Append(" — SAME LENS, not same finding: titles are not in the ledger\n");

		foreach (var (pr, t) in r.RepeatClassLoops)
		{
			sb.Append("    ").Append(pr.ToString()).Append(": ").Append(t.Turns).Append(" turns, ")
				.Append(t.First.Added).Append(" -> ").Append(t.Last.Added).Append(" added (")
				.Append(t.Growth.ToString("0.0")).Append("x), ")
				.Append(Pct(t.ProductionShare)).Append(" of the growth outside tests, ")
				.Append(t.RepeatLens).Append(" raised on ").Append(t.RepeatRaiseTurns).Append(" of ")
				.Append(t.RepeatLensTurns).Append(" turn(s) it sat, net +")
				.Append(t.Last.Net).Append("\n");
		}
	}

	/// <summary>
	/// What the AUTHOR did with the findings, and how much of the window could answer that at all.
	/// Every other number in this report is the tool grading its own work; this is the only one
	/// carrying a human's judgement, which makes it the one most worth overstating. So the caveat is
	/// printed here beside the figure, and the count of lines that predate the field is printed with
	/// it — a ratio whose denominator is a fraction of the window, presented as if it covered the
	/// window, is the failure mode this block exists to prevent.
	/// </summary>
	private static void AppendAuthorVerdicts(StringBuilder sb, Report r)
	{
		sb.Append("\nAuthor verdicts: ");
		if (r.VerdictReviews == 0)
		{
			sb.Append("NOT RECORDED on any of the ").Append(r.PersonaReviews)
				.Append(" persona-review(s) in this window\n")
				.Append("  every line predates the field, so there is no agreement figure — which is not\n")
				.Append("  the same as an agreement of zero\n");
			return;
		}

		sb.Append(r.Resolved).Append(" resolved, ").Append(r.Withdrawn).Append(" withdrawn across ")
			.Append(r.VerdictReviews).Append(" of ").Append(r.PersonaReviews)
			.Append(" persona-review(s) — agree% ")
			.Append(r.AgreementRate is { } rate ? Pct(rate) : "— (nothing ruled on yet)").Append('\n');
		sb.Append("  agree% = resolved / (resolved + withdrawn). It is AGREEMENT, not precision: an author\n")
			.Append("  can explain away a finding that was right, and can \"resolve\" a title by changing\n")
			.Append("  something unrelated. It reports what the author did, never whether the finding was true.\n");
		if (r.PreVerdictReviews > 0)
		{
			sb.Append("  ").Append(r.PreVerdictReviews)
				.Append(" persona-review(s) come from ledger lines written before the field existed (schema < ")
				.Append(RunMetrics.VerdictSchema).Append(")\n")
				.Append("  and are EXCLUDED from the ratio rather than counted as zero\n");
		}
	}

	/// <summary>A plain-text report for the CLI / a tracking-issue comment.</summary>
	public static string Render(Report r)
	{
		var sb = new StringBuilder();
		sb.Append("Peanut Gallery metrics — ").Append(r.Runs).Append(" run(s), ")
			.Append(r.PersonaReviews).Append(" persona-review(s)\n");
		sb.Append("Overall failure rate: ").Append(Pct(r.FailureRate))
			.Append(" (").Append(r.Failures).Append(" degraded)\n");

		if (r.FailureClasses.Count > 0)
		{
			sb.Append("Failures by class: ")
				.Append(string.Join(", ", r.FailureClasses.OrderByDescending(kv => kv.Value)
					.Select(kv => $"{kv.Key}={kv.Value}"))).Append('\n');
		}

		AppendAuthorVerdicts(sb, r);
		AppendTrajectories(sb, r);
		AppendRows(sb, "By model", r.ByModel);
		AppendRows(sb, "By persona/lens", r.ByPersona);
		return sb.ToString();
	}

	private static void AppendRows(StringBuilder sb, string title, IReadOnlyList<Row> rows)
	{
		sb.Append('\n').Append(title).Append(":\n");
		// agree% is deliberately NOT called precision — see Row.AgreementRate, and the caveat printed
		// above the tables. A row with nothing ruled on renders "—", never 0%.
		sb.Append(string.Format("  {0,-28} {1,7} {2,8} {3,8} {4,8} {5,8} {6,8} {7,8} {8,10} {9,8}\n",
			"key", "reviews", "fail%", "refute%", "agree%", "calls/rv", "p50 s", "p95 s", "out tok", "cache%"));
		foreach (var row in rows)
		{
			sb.Append(string.Format("  {0,-28} {1,7} {2,8} {3,8} {4,8} {5,8} {6,8} {7,8} {8,10} {9,8}",
				Trunc(row.Key, 28), row.Reviews, Pct(row.FailureRate), Pct(row.RefuteRate),
				row.AgreementRate is { } agree ? Pct(agree) : "—",
				$"{row.CallsPerReview:0.00}", Secs(row.MedianMs), Secs(row.P95Ms), row.OutputTokens,
				row.CacheHitRate is { } rate ? Pct(rate) : "—"));
			var notes = new List<string>();
			if (row.Failures > 0)
			{
				notes.Add("top: " + row.TopFailure);
			}

			if (row.MultiCall > 0)
			{
				notes.Add($"multi-call {row.MultiCall}, recovered {Pct(row.RecoveryRate)}");
			}

			// The counts behind agree%, so the column is never read without its sample size.
			if (row.Judged > 0)
			{
				notes.Add($"verdicts {row.Resolved} resolved / {row.Withdrawn} withdrawn");
			}

			if (row.PreVerdictReviews > 0)
			{
				notes.Add($"{row.PreVerdictReviews} of {row.Reviews} predate verdicts");
			}

			if (notes.Count > 0)
			{
				sb.Append("  (").Append(string.Join("; ", notes)).Append(')');
			}

			sb.Append('\n');
		}
	}

	private static string Pct(double v) => $"{v * 100:0.#}%";

	private static string Secs(long ms) => $"{ms / 1000.0:0.#}";

	private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
