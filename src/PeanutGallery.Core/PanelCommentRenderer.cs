using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PeanutGallery.Core;

/// <summary>One persona's outcome, as the panel comment needs to report it.</summary>
/// <param name="Reported">False when this persona failed or was skipped - named, never quietly missing.</param>
public sealed record PanelMember(
	string PersonaId,
	string Name,
	string Lens,
	string Model,
	bool Reported,
	string? NotReportedReason = null);

/// <summary>Everything the panel comment renders, gathered by the shell.</summary>
/// <param name="Refuted">What the adversarial pass dropped, WITH the grounds it gave. A bare count
/// tells a reader that something was removed but leaves them no way to disagree with it — and this
/// pass has demonstrably refuted true findings, so the reasoning is the part that matters.</param>
/// <param name="Reconciled">True when this update came from a conversation turn rather than a
/// review — one pass over the board in response to a comment, not the panel re-reading the code.
/// Disclosed because the reader is entitled to know that the persona named beside a withdrawn
/// finding did not personally agree to drop it.</param>
/// <param name="InProgress">True when this is a PARTIAL panel posted mid-run, because reviewers are
/// published as they land rather than all at the end (#116). It must change the header, not just add
/// a note: "Reviewed through &lt;sha&gt;" is the marker both humans and polling agents read as "the
/// review is complete for this commit", and a partial panel has not earned it.</param>
public sealed record PanelReport(
	IReadOnlyList<PanelMember> Members,
	SynthesisResult Synthesis,
	IReadOnlyList<string> Resolved,
	IReadOnlyList<string> Withdrawn,
	int SuppressedByConfidence,
	IReadOnlyList<RefutedFinding> Refuted,
	bool Reconciled = false,
	bool InProgress = false);

/// <summary>
/// Renders the panel's single comment: one review, spoken with one voice, with every finding still
/// attributed to the reviewer that raised it.
///
/// <para>Replaces N self-updating comments with one. The reason is noise: N personas reporting the
/// same issue produce N comments, and with a per-PR panel a reader also sees comments from
/// personas that will not exist on the next PR. What it must NOT do is anonymise - "the Architect
/// flagged the layering violation" is the signal that makes a panel worth more than a single
/// reviewer.</para>
///
/// <para>Everything removed from the reader's view is disclosed: merged duplicates, confidence
/// suppressions, refutations, and any persona that did not report. A comment that quietly shrank
/// is indistinguishable from a clean review.</para>
/// </summary>
public static class PanelCommentRenderer
{
	/// <summary>The marker that makes this one comment upsertable in place, like a persona's was.</summary>
	public const string PanelId = "panel";

	/// <summary>
	/// A hidden, machine-readable marker written on a <b>settled</b> panel comment that lost one or
	/// more reviewers: <c>&lt;!-- pg-degraded:N --&gt;</c>. A merge-gate polling consumer greps for it
	/// to tell "clean review, no findings" apart from "the review that mattered didn't happen" — the
	/// gap #130 exists to close. Absent on a full panel and while a review is still in progress
	/// (a not-yet-reported reviewer is pending, not degraded).
	/// </summary>
	public const string DegradedMarkerPrefix = "<!-- pg-degraded:";

	/// <summary>
	/// The hidden degradation marker for <paramref name="count"/> non-reporting reviewers. Throws on
	/// <c>count &lt; 1</c>: this marker is a grep-target contract for merge-gate consumers, and a
	/// <c>pg-degraded:0</c> would be read as degradation on a clean panel — a false positive worse
	/// than a crash. A marker names at least one gap, so a zero/negative count is a caller bug, not a
	/// value to encode.
	/// </summary>
	public static string DegradedMarker(int count) =>
		count < 1
			? throw new System.ArgumentOutOfRangeException(
				nameof(count), count, "a degraded marker names at least one non-reporting reviewer")
			: $"{DegradedMarkerPrefix}{count} -->";

	/// <summary>
	/// How many reviewers the hidden marker says this panel lost, or 0 when it carries none. The
	/// counterpart to <see cref="DegradedMarker"/>, kept beside it so the grep-target contract has
	/// one writer and one reader instead of a marker written here and a regex in whatever polls
	/// for it.
	/// </summary>
	public static int DegradedCount(string commentBody)
	{
		var start = commentBody.IndexOf(DegradedMarkerPrefix, System.StringComparison.Ordinal);
		if (start < 0)
		{
			return 0;
		}

		var digits = start + DegradedMarkerPrefix.Length;
		var end = commentBody.IndexOf(' ', digits);
		return end > digits && int.TryParse(commentBody[digits..end], out var count) && count > 0 ? count : 0;
	}

	/// <summary>
	/// The exact line a panel with nothing to report renders. Public because it is the signal a
	/// polling consumer (<c>await-review</c>) reads to tell a clean review from one carrying
	/// findings, and a literal on the reading side would be free to drift from the one written here.
	/// </summary>
	public const string NoFindingsLine = "_No findings._";

	/// <summary>
	/// True when this panel body is the renderer's own "nothing to report" board.
	///
	/// <para>A substring search is not good enough, and the panel that reviewed this very function
	/// proved it: a reviewer raised a finding whose body quoted <c>_No findings._</c> while arguing
	/// about this check, and a <c>Contains</c> read that panel — five findings on it — as clean.
	/// So the match is a whole line.</para>
	///
	/// <para>That is sound because of an invariant this renderer holds and a test pins: <b>no
	/// authored text ever reaches column 0.</b> Every single-line authored fragment — title, file,
	/// lens, persona name, model, non-reporting reason, resolved and withdrawn titles — goes
	/// through <see cref="CommentRenderer.OneLine"/>, and the one field allowed to be several lines,
	/// a finding body, has EVERY line of it re-indented under its bullet, not just the first. Break
	/// that invariant and a model-authored newline can plant a sentinel here.</para>
	/// </summary>
	public static bool ReportsNoFindings(string commentBody)
	{
		foreach (var line in commentBody.Split('\n'))
		{
			if (line.TrimEnd('\r') == NoFindingsLine)
			{
				return true;
			}
		}

		return false;
	}

	public static string Render(PanelReport report, string headSha, int turn)
	{
		var sb = new StringBuilder();
		sb.Append(CommentRenderer.Marker(PanelId)).Append('\n');
		sb.Append("### Peanut Gallery\n");
		sb.Append(report.InProgress ? "_Reviewing `" : "_Reviewed through `")
			.Append(Sha.Short(headSha))
			.Append("` · turn ").Append(turn);
		if (report.InProgress)
		{
			sb.Append(" · still running — this comment updates as each reviewer lands");
		}

		sb.Append("_\n\n");

		AppendDegradationBanner(sb, report);

		AppendPanelLine(sb, report.Members);

		if (report.Synthesis.Findings.Count == 0)
		{
			sb.Append('\n').Append(NoFindingsLine).Append('\n');
		}
		else
		{
			var clusters = Cluster(report.Synthesis.Findings);
			sb.Append('\n');
			AppendCountLine(sb, clusters.Count, report.Synthesis.Findings.Count);
			AppendFindings(sb, clusters);
		}

		AppendDisclosures(sb, report);

		if (report.Resolved.Count > 0)
		{
			sb.Append("\n**Resolved since last push:** ")
				.Append(string.Join("; ", report.Resolved.Select(CommentRenderer.OneLine))).Append('\n');
		}

		if (report.Withdrawn.Count > 0)
		{
			sb.Append("\n**Withdrawn (author-explained):** ")
				.Append(string.Join("; ", report.Withdrawn.Select(CommentRenderer.OneLine))).Append('\n');
		}

		if (report.Reconciled)
		{
			sb.Append("\n_Updated from the PR conversation without re-running the panel. ")
				.Append("Push a change to have the reviewers look at the code again._\n");
		}

		return sb.ToString();
	}

	// A prominent, decision-time banner when a SETTLED panel lost a reviewer (#130). The muted
	// "_Did not report:_" line below names who and why, but it reads like the other disclosures and
	// is easy to miss - so a partial panel (one that may have lost the lens most relevant to this
	// change) looked identical to a clean review to anyone skimming. Only on a settled render: while
	// InProgress a not-yet-reported reviewer is pending, not degraded, and every render would cry
	// wolf. A hidden pg-degraded marker rides alongside so a merge-gate consumer can read the same
	// signal a machine can't get from prose.
	private static void AppendDegradationBanner(StringBuilder sb, PanelReport report)
	{
		if (report.InProgress)
		{
			return;
		}

		var missing = report.Members.Count(m => !m.Reported);
		if (missing == 0)
		{
			return;
		}

		sb.Append(DegradedMarker(missing)).Append('\n');
		sb.Append("> [!WARNING]\n> **")
			.Append(missing).Append(missing == 1 ? " reviewer did not report" : " reviewers did not report")
			.Append(" this run — this review is partial.** See _Did not report_ below.\n\n");
	}

	// Who is on the panel, and who did not report. A persona that failed used to be visible as its
	// own failure comment; with one comment it has to be named here or it just vanishes.
	private static void AppendPanelLine(StringBuilder sb, IReadOnlyList<PanelMember> members)
	{
		if (members.Count == 0)
		{
			return;
		}

		// Only when somebody did report: an empty "_Panel: ._" is what a whole-panel outage used to
		// render, and it reads as a formatting glitch rather than the outage the next line names.
		var reported = members.Where(m => m.Reported).ToList();
		if (reported.Count > 0)
		{
			sb.Append("_Panel: ")
				.Append(string.Join(", ", reported.Select(m =>
					$"{CommentRenderer.OneLine(m.Name)} (`{CommentRenderer.OneLine(m.Model)}`)")))
				.Append("._\n");
		}

		var missing = members.Where(m => !m.Reported).ToList();
		if (missing.Count > 0)
		{
			sb.Append("_Did not report: ")
				.Append(string.Join("; ", missing.Select(m =>
					CommentRenderer.OneLine(m.NotReportedReason) is { Length: > 0 } r
						? $"{CommentRenderer.OneLine(m.Name)} ({r})"
						: CommentRenderer.OneLine(m.Name))))
				.Append("._\n");
		}
	}

	/// <summary>
	/// The two numbers a reader needs to size their response: how many distinct problem AREAS the
	/// panel found, and how many findings describe them. A bare finding count is what #169 is about
	/// - five lenses circling one root cause read as "five majors" and the author answered the five.
	///
	/// <para>Whenever there is a finding to report, BOTH numbers appear, even when they are equal:
	/// "5 problem areas · 5 findings" is the honest reading of five unrelated problems, and printing
	/// the pair only when something clustered would make its absence mean "nothing grouped" - a
	/// second signal the reader has to learn. A panel with no findings is the one case that does not
	/// get the line: it renders "_No findings._", which is a plainer sentence than "0 problem areas
	/// · 0 findings", and there is no response to calibrate. That is the contract, and a test pins
	/// it, so the empty case is a decision rather than an omission.</para>
	/// </summary>
	private static void AppendCountLine(StringBuilder sb, int clusters, int findings)
	{
		sb.Append('_')
			.Append(clusters).Append(clusters == 1 ? " problem area" : " problem areas")
			.Append(" · ")
			.Append(findings).Append(findings == 1 ? " finding" : " findings")
			.Append("._\n\n");
	}

	// One list, two shapes. A cluster of one is exactly the bullet this renderer has always
	// produced - no heading, nothing to read past - because a heading over a single finding is pure
	// ceremony. A cluster of several gets a heading item naming the area and the lenses that landed
	// on it, with its members nested one level under it. The nesting is load-bearing: headings and
	// bullets in a flat list would leave a reader unable to tell where a cluster stops, and adjacent
	// markdown lists merge no matter how many blank lines separate them.
	private static void AppendFindings(StringBuilder sb, IReadOnlyList<FindingCluster> clusters)
	{
		foreach (var cluster in clusters)
		{
			if (cluster.Findings.Count == 1)
			{
				AppendFindingBullet(sb, cluster.Findings[0], indent: "");
				continue;
			}

			AppendClusterHeading(sb, cluster);
			foreach (var af in cluster.Findings)
			{
				AppendFindingBullet(sb, af, indent: "  ");
			}
		}
	}

	// The heading claims an area, never a verdict: it counts what is underneath and names who
	// raised it. Every member is still printed below with its own severity, title, body and lenses,
	// so a heading that groups too eagerly costs a reader one glance, not a finding.
	private static void AppendClusterHeading(StringBuilder sb, FindingCluster cluster)
	{
		sb.Append("- ").Append(CommentRenderer.Badge(cluster.Findings[0].Finding.Severity)).Append(' ');
		if (!string.IsNullOrEmpty(cluster.File))
		{
			sb.Append('`').Append(CommentRenderer.OneLine(cluster.File));
			if (cluster.AnchorLine > 0)
			{
				sb.Append(':').Append(cluster.AnchorLine);
				if (cluster.LastLine > cluster.AnchorLine)
				{
					sb.Append('-').Append(cluster.LastLine);
				}
			}

			sb.Append("` — ");
		}

		sb.Append("**").Append(cluster.Findings.Count).Append(" findings in one area**");
		if (cluster.Lenses.Count > 0)
		{
			sb.Append(" _(").Append(Lenses(cluster.Lenses)).Append(")_");
		}

		sb.Append('\n');
	}

	// Lens names reach the panel from a persona config and, in auto mode, from an orchestrator
	// model. Folded like every other single-line authored fragment.
	private static string Lenses(IReadOnlyList<string> lenses) =>
		string.Join(", ", lenses.Select(CommentRenderer.OneLine));

	private static void AppendFindingBullet(StringBuilder sb, AttributedFinding af, string indent)
	{
		var f = af.Finding;
		sb.Append(indent).Append("- ").Append(CommentRenderer.Badge(f.Severity)).Append(' ');
		if (!string.IsNullOrEmpty(f.File))
		{
			sb.Append('`').Append(CommentRenderer.OneLine(f.File));
			if (f.Line > 0)
			{
				sb.Append(':').Append(f.Line);
			}

			sb.Append("` — ");
		}

		sb.Append("**").Append(CommentRenderer.OneLine(f.Title)).Append("**");

		// The attribution: dedup the issue, keep the voices. It stays on every bullet even inside a
		// cluster whose heading already lists the union - the heading says who is in the room, the
		// bullet says who said this particular thing, and only the second one lets a reader weigh a
		// finding against the lens it came from.
		if (af.Lenses.Count > 0)
		{
			sb.Append(" _(").Append(Lenses(af.Lenses)).Append(")_");
		}

		// The body is the one authored field allowed to be several lines, and it stays inside its
		// bullet because EVERY line of it is re-indented, not just the first.
		if (!string.IsNullOrWhiteSpace(f.Body))
		{
			sb.Append("  \n").Append(indent).Append("  ")
				.Append(f.Body.Replace("\n", "\n" + indent + "  "));
		}

		sb.Append('\n');
	}

	/// <summary>
	/// How far apart two findings in one file can sit and still read as one problem area.
	///
	/// <para>Sized to the failure this fixes. Two lenses describing one root cause rarely anchor on
	/// the same line - one cites the call, another the guard clause above it, a third the field it
	/// dereferences - but they land inside the same method. Twenty lines is roughly one method's
	/// worth of code, so it absorbs that drift while stopping well short of "same file, therefore
	/// same problem": findings 300 lines apart get separate headings, which is right, because they
	/// are almost certainly separate problems.</para>
	///
	/// <para>Distance is measured from the cluster's ANCHOR - its first line - never from its last
	/// member, so a busy file cannot chain 10-30-50-70 into one 60-line "area". A cluster therefore
	/// spans at most this many lines by construction.</para>
	/// </summary>
	private const int ClusterProximityLines = 20;

	/// <summary>
	/// Findings that point at one area of one file, grouped for display.
	///
	/// <para>Private, and staying that way. This is a display heuristic's intermediate value: it is
	/// produced by <see cref="Cluster"/> and consumed by <see cref="AppendFindings"/> a few lines
	/// later, and nothing else in the repo has a use for it. Making it public would put a heading
	/// layout's anchor and ordering invariants into the core's compatibility surface, so a later
	/// change to how areas are drawn would owe compatibility to callers that never existed - and
	/// would let a caller build a cluster that satisfies none of the invariants the renderer
	/// assumes. The two-pass shape stays because the area count has to be known before the first
	/// finding is written, but the shape is an implementation detail, not an API.</para>
	///
	/// <para><see cref="AnchorLine"/> is the cluster's first line and <see cref="LastLine"/> its
	/// last; both are 0 for a cluster with no line coordinate, which is always a cluster of one.</para>
	/// </summary>
	private sealed record FindingCluster(
		string File,
		int AnchorLine,
		int LastLine,
		IReadOnlyList<AttributedFinding> Findings,
		IReadOnlyList<string> Lenses);

	/// <summary>
	/// Groups findings that point at one area of one file, so a reader calibrates to problem AREAS
	/// rather than to a raw finding count (#169). Five lenses circling one root cause used to render
	/// as five top-level bullets, and authors sized their response to the five.
	///
	/// <para>Why this is grouping in the RENDERER and not more merging in
	/// <see cref="FindingSynthesis"/>: that fold's doc comment argues, correctly, that dedup must
	/// stay conservative, because an over-merge DELETES a finding and the reader has no way to see
	/// that it happened, while an under-merge is two similar bullets they can judge. Nothing here
	/// merges. This is presentation: no finding is dropped, reworded, or hidden, a cluster is a
	/// heading with every member printed underneath, and each member keeps its own
	/// <see cref="AttributedFinding.Lenses"/> attribution. So the worst case of a cluster drawn too
	/// eagerly is a heading that overclaims - visible, and cheap to discount. That is the same
	/// asymmetry the fold reasons from, and it is precisely why the looser rule is allowed to live
	/// here and not there. Nothing upstream sees clusters: the <see cref="SynthesisResult"/> the
	/// session stores and re-reads on the next turn is untouched.</para>
	///
	/// <para>Pure and total: findings in, the same findings out, grouped and ordered
	/// deterministically. Same input, same markdown.</para>
	/// </summary>
	private static IReadOnlyList<FindingCluster> Cluster(IReadOnlyList<AttributedFinding> findings)
	{
		var clusters = new List<FindingCluster>();

		// No coordinate, no area. A Line of 0 means "not tied to a line" - a contrarian's "delete
		// this subsystem" - not line 1, and a finding with no file at all is repo-scoped. Neither
		// offers anything to measure proximity with, so each stands alone.
		//
		// That includes two file-wide findings about the SAME file, which an earlier draft grouped
		// (the panel's own contrarian caught it on #172). Nothing establishes that "this file has no
		// tests" and "this file should not exist" are one area; grouping them would manufacture
		// agreement out of a shared filename and understate the very count this change exists to
		// make honest. Under-grouping is the safe direction for the same reason it is in
		// FindingSynthesis: it costs a reader one extra heading, not a false claim.
		foreach (var af in findings.Where(a => HasNoArea(a.Finding)))
		{
			clusters.Add(new FindingCluster(af.Finding.File, 0, 0, [af], af.Lenses));
		}

		// One sort, then one walk. Sorting by file and then line puts every finding next to the ones
		// it could possibly join, which turns "is this the same area?" into a single comparison
		// against the group's first member - no per-file rescan, and no separate record of which
		// file is being walked or where the current group started. The group IS the anchor.
		var anchored = findings
			.Where(a => !HasNoArea(a.Finding))
			.OrderBy(a => a.Finding.File, StringComparer.OrdinalIgnoreCase)
			.ThenBy(a => a.Finding.Line)
			.ThenByDescending(a => a.Finding.Severity)
			.ThenBy(a => a.Finding.Title, StringComparer.Ordinal)
			.ToList();

		var current = new List<AttributedFinding>();
		foreach (var af in anchored)
		{
			if (current.Count > 0 && !JoinsArea(current[0].Finding, af.Finding))
			{
				clusters.Add(Build(current));
				current = [];
			}

			current.Add(af);
		}

		if (current.Count > 0)
		{
			clusters.Add(Build(current));
		}

		// Worst first, then a stable walk down the tree. Title breaks the last tie so two runs over
		// the same findings cannot swap two equally severe clusters in the same spot.
		return clusters
			.OrderByDescending(c => c.Findings[0].Finding.Severity)
			.ThenBy(c => c.File, StringComparer.Ordinal)
			.ThenBy(c => c.AnchorLine)
			.ThenBy(c => c.Findings[0].Finding.Title, StringComparer.Ordinal)
			.ToList();
	}

	// The whole grouping rule, in one place: same file, and within the threshold of the anchor -
	// the group's FIRST member, never its last, so a busy file cannot chain 10-30-50-70 into one
	// 60-line area. Members arrive in line order, so the anchor is simply members[0].
	private static bool JoinsArea(Finding anchor, Finding candidate) =>
		string.Equals(anchor.File, candidate.File, StringComparison.OrdinalIgnoreCase)
		&& candidate.Line - anchor.Line <= ClusterProximityLines;

	// Members arrive sorted by line, so the span is the first and last of them. Render order is a
	// different question from grouping order - worst first inside the area - so it is applied here,
	// once, and the cluster carries the result.
	private static FindingCluster Build(List<AttributedFinding> members)
	{
		var ordered = members
			.OrderByDescending(a => a.Finding.Severity)
			.ThenBy(a => a.Finding.Line)
			.ThenBy(a => a.Finding.Title, StringComparer.Ordinal)
			.ToList();

		return new FindingCluster(
			members[0].Finding.File,
			members[0].Finding.Line,
			members[^1].Finding.Line,
			ordered,
			LensesOf(ordered));
	}

	// The two ways a finding can carry no area: no line to measure from, or no file to measure in.
	// Both land it in a cluster of its own, and the pair is named once so the two loops above cannot
	// drift apart and drop - or double-count - whatever falls between them.
	private static bool HasNoArea(Finding f) => f.Line <= 0 || string.IsNullOrEmpty(f.File);

	// The union of the lenses in render order, deduped the way FindingSynthesis dedupes them, so a
	// heading reads as "who agreed here" without repeating a persona that spoke twice.
	private static IReadOnlyList<string> LensesOf(IReadOnlyList<AttributedFinding> members)
	{
		var lenses = new List<string>();
		foreach (var lens in members.SelectMany(m => m.Lenses))
		{
			if (!lenses.Contains(lens, StringComparer.OrdinalIgnoreCase))
			{
				lenses.Add(lens);
			}
		}

		return lenses;
	}


	/// <summary>
	/// What the adversarial pass dropped, with its grounds — a drop a reader cannot see the
	/// reasoning for is a drop they cannot correct.
	///
	/// <para>Plain markdown, deliberately NOT a <c>&lt;details&gt;</c> block. Collapsing a long list
	/// is nicer, but it puts model-authored titles inside a raw HTML element, where a title
	/// containing the closing tag ends the block early and everything after it renders as content
	/// this tool appears to have written. Filtering for that is whack-a-mole; not building the
	/// escapable thing is not. Refutation lists are short enough that the collapse was never worth
	/// an injection surface.</para>
	/// </summary>
	internal static void AppendRefutations(StringBuilder sb, IReadOnlyList<RefutedFinding> refuted)
	{
		if (refuted.Count == 0)
		{
			return;
		}

		var plural = refuted.Count == 1 ? "finding" : "findings";
		sb.Append("\n**").Append(refuted.Count).Append(' ').Append(plural)
			.Append(" dropped on an adversarial second pass:**\n");
		foreach (var r in refuted)
		{
			sb.Append("- **").Append(CommentRenderer.OneLine(r.Title)).Append("**");
			var why = CommentRenderer.OneLine(r.Why);
			if (why.Length > 0)
			{
				sb.Append("  \n  ").Append(why);
			}

			sb.Append('\n');
		}
	}

	private static void AppendDisclosures(StringBuilder sb, PanelReport report)
	{
		var notes = new List<string>();
		if (report.Synthesis.Merged > 0)
		{
			notes.Add($"{report.Synthesis.Merged} duplicate report(s) merged");
		}

		if (report.SuppressedByConfidence > 0)
		{
			notes.Add($"{report.SuppressedByConfidence} low-confidence finding(s) suppressed");
		}

		if (notes.Count > 0)
		{
			sb.Append("\n_").Append(string.Join("; ", notes)).Append("._\n");
		}

		AppendRefutations(sb, report.Refuted);
	}
}
