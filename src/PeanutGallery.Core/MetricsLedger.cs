using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PeanutGallery.Core;

/// <summary>
/// The PR's metrics comment IS the datastore (like <see cref="SessionCodec"/> for sessions): one
/// bot comment per PR, marked <c>&lt;!-- peanut-gallery:metrics --&gt;</c> so <see cref="CommentSync"/>
/// upserts it in place, carrying an <b>append-only</b> JSONL log of every run's <see cref="RunMetrics"/>
/// in a hidden block. Append-only is the point: unlike a session (one current state), metrics are a
/// history, so each run adds a line rather than overwriting. Base64 keeps the JSON's braces and the
/// <c>--&gt;</c> delimiter safe. Pure: the shell reads the existing comment, calls
/// <see cref="Append"/>, and posts the result.
/// <para>
/// The history is bounded by the <b>rendered size</b> of that comment (<see cref="BodyBudget"/>),
/// because that is the quantity GitHub actually rejects on. Oldest lines roll off first, and any
/// roll-off is stated in the rendered text — a partial window that reads as a whole history is the
/// same silent-shrink defect as a review that quietly drops findings.
/// </para>
/// </summary>
public static class MetricsLedger
{
	/// <summary>The persona id for the metrics comment's <see cref="CommentSync"/> marker.</summary>
	public const string PersonaId = "metrics";

	private const string Marker = "<!-- peanut-gallery:metrics -->";
	private const string Open = "<!-- pg-metrics:1:";
	private const string Close = " -->";
	private const string EvictedOpen = "<!-- pg-metrics-evicted:";

	/// <summary>GitHub's hard limit on an issue-comment body, in characters. Named rather than
	/// buried in an expression: it is GitHub's number, not a knob of ours, and the pure core may not
	/// call GitHub to discover it.</summary>
	public const int GitHubCommentLimit = 65536;

	/// <summary>Chars held back from <see cref="GitHubCommentLimit"/>. Covers the slack in the
	/// pre-rendered size estimate, the preamble growing a field later, and any server-side counting
	/// that is not exactly our char count — the cost of unused headroom is one or two runs of
	/// history, and the cost of having none is losing the entire ledger.</summary>
	public const int Headroom = 4096;

	/// <summary>What a rendered ledger body must fit in. This is the bound that binds.</summary>
	public const int BodyBudget = GitHubCommentLimit - Headroom;

	/// <summary>
	/// Secondary ceiling on retained lines, never the only guard. It is not what usually binds: a
	/// real 5-persona line is ~1.5 KB, which base64 renders to ~2 KB of comment, so
	/// <see cref="BodyBudget"/> is reached at roughly 30 runs — an order of magnitude before 250
	/// lines. (This doc comment used to reason "a few hundred bytes a line, so a few hundred runs
	/// fit"; that arithmetic is what kept #189 invisible until someone measured the body.) It
	/// survives for the other direction: lines small enough that hundreds would fit the budget
	/// still deserve a ceiling on parse work and on a history no reader will scroll.
	/// </summary>
	public const int DefaultCap = 250;

	/// <summary>
	/// The comment body for the ledger after appending <paramref name="newLine"/> to whatever prior
	/// lines <paramref name="existingBody"/> carried, dropping oldest-first until the rendered body
	/// fits <see cref="BodyBudget"/> and never keeping more than <paramref name="cap"/> lines. A
	/// null/blank existing body starts a fresh ledger. Anything dropped is disclosed in the rendered
	/// text and counted in <see cref="EvictedCount"/>. The newest line always survives, so a
	/// <paramref name="cap"/> below 1 is read as 1 and a line larger than the budget is kept alone.
	/// </summary>
	public static string Append(string? existingBody, string newLine, int cap = DefaultCap)
	{
		var lines = existingBody is null ? new List<string>() : Extract(existingBody).ToList();
		lines.Add(newLine.Replace("\n", " ").Replace("\r", " "));

		// Cumulative over the ledger's life, carried in a hidden marker. This call can only see the
		// lines still present, so a count derived from this append alone would decay from "31 runs
		// rolled off" to "1 run rolled off" the moment an append evicted a single line, and the
		// disclosure would understate the hole every time it mattered most.
		var evicted = existingBody is null ? 0 : EvictedCount(existingBody);

		// At least one line always survives, so cap 0 is read as cap 1 rather than as "keep nothing".
		var lineCap = Math.Max(1, cap);
		if (lines.Count > lineCap)
		{
			evicted += lines.Count - lineCap;
			lines = lines.Skip(lines.Count - lineCap).ToList();
		}

		// The trailing summary is parsed from the LAST line, and eviction only ever removes from the
		// front, so the summary cannot change while we evict: parse it once, here.
		var last = MetricsCodec.ReadLine(lines[^1]);

		// Measure, do not re-render. Base64 length is a function of the raw byte count alone, and the
		// non-payload text is bounded above by rendering it once with the widest counts it could
		// print (every line evicted, which is also the longer of the two header sentences). Two O(1)
		// renders total, instead of one full re-render per line considered for eviction.
		var budget = BodyBudget - Render(lines.Count, evicted + lines.Count, last, string.Empty).Length;

		var sizes = new int[lines.Count];
		var raw = (long)lines.Count - 1; // the '\n' each line after the first is joined with
		for (var i = 0; i < lines.Count; i++)
		{
			sizes[i] = Encoding.UTF8.GetByteCount(lines[i]);
			raw += sizes[i];
		}

		// Stop at one line. If the newest run alone will not fit, we keep it and let the upsert fail
		// — the shell already reports that failure on stderr — rather than post a ledger that
		// successfully records nothing. Both directions lose: dropping it loses the run we were
		// called to record and says nothing about it, while keeping it loses the whole comment but
		// leaves a visible error. Prefer the loud loss.
		var drop = 0;
		while (drop < lines.Count - 1 && Base64Length(raw) > budget)
		{
			raw -= sizes[drop] + 1; // the line, and the separator that joined it to the next
			drop++;
		}

		var kept = drop == 0 ? lines : lines.Skip(drop).ToList();
		return Render(kept.Count, evicted + drop, last,
			Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', kept))));
	}

	/// <summary>The JSONL lines carried in a ledger comment body, oldest first; empty if none/unreadable.</summary>
	public static IReadOnlyList<string> Extract(string commentBody)
	{
		var start = commentBody.LastIndexOf(Open, StringComparison.Ordinal);
		if (start < 0)
		{
			return [];
		}

		var payloadStart = start + Open.Length;
		var end = commentBody.IndexOf(Close, payloadStart, StringComparison.Ordinal);
		if (end < 0)
		{
			return [];
		}

		try
		{
			var jsonl = Encoding.UTF8.GetString(Convert.FromBase64String(commentBody[payloadStart..end].Trim()));
			return jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		}
		catch (FormatException)
		{
			return [];
		}
	}

	/// <summary>
	/// How many runs have rolled off this ledger, cumulative over its life; 0 for a full history,
	/// and for anything that is not a ledger. A reader — or an aggregation like
	/// <c>peanut-gallery metrics</c> — is looking at a partial window when this is non-zero.
	/// <para>
	/// A <b>lower bound</b>, and rendered as one. A ledger last written before this marker existed
	/// carries no count, so anything the old line cap had already rolled off is unknowable and reads
	/// as 0 here; the first append after this ships adds only what it evicts itself. Reconstructing
	/// the lost count is impossible, so the disclosure says "at least N" rather than asserting a
	/// total it cannot know.
	/// </para>
	/// </summary>
	public static int EvictedCount(string commentBody)
	{
		var start = commentBody.LastIndexOf(EvictedOpen, StringComparison.Ordinal);
		if (start < 0)
		{
			return 0;
		}

		var payloadStart = start + EvictedOpen.Length;
		var end = commentBody.IndexOf(Close, payloadStart, StringComparison.Ordinal);
		return end >= payloadStart
			&& int.TryParse(commentBody[payloadStart..end], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
				? n
				: 0;
	}

	/// <summary>True when a comment body is our metrics ledger (so the shell can find it among the thread).</summary>
	public static bool IsLedger(string commentBody) =>
		commentBody.StartsWith(Marker, StringComparison.Ordinal);

	/// <summary>The base64 length of <paramref name="rawBytes"/> bytes: four chars per three bytes,
	/// rounded up to the padded quantum. Deterministic, which is what lets the rendered size be
	/// computed rather than re-measured by rendering. Returns a long: an int would wrap on a
	/// pathological line, and a wrapped length compares as "it fits".</summary>
	private static long Base64Length(long rawBytes) => (rawBytes + 2) / 3 * 4;

	private static string Render(int kept, int evicted, RunMetrics? last, string payload)
	{
		var sb = new StringBuilder();
		sb.Append(Marker).Append("\n### Peanut Gallery — run metrics\n\n");
		if (evicted > 0)
		{
			// Never "every run" once a run has rolled off. A reader who takes a windowed ledger for
			// a complete one draws wrong conclusions from it, and so does anything aggregating it.
			//
			// "at least", and "size and line bounds" rather than GitHub's number: the count is a
			// lower bound (see EvictedCount), and BOTH bounds evict, so naming one of them would be
			// a guess stated as a fact in the very sentence whose job is not to overclaim.
			sb.Append("_A machine-readable log of review runs on this PR (").Append(kept)
				.Append(kept == 1 ? " run" : " runs").Append(" shown; at least ").Append(evicted)
				.Append(evicted == 1 ? " older run has" : " older runs have")
				.Append(" rolled off to keep this comment inside its size and line bounds, so this is a")
				.Append(" partial history). Aggregate across PRs with `peanut-gallery metrics`._\n");
		}
		else
		{
			sb.Append("_A machine-readable log of every review run on this PR (").Append(kept)
				.Append(kept == 1 ? " run" : " runs").Append("). Aggregate across PRs with `peanut-gallery metrics`._\n");
		}

		if (last is not null)
		{
			sb.Append("\n_Last run `").Append(last.Context.Sha).Append("`: ")
				.Append(last.Personas.Count).Append(" persona(s), ")
				.Append(last.PostedTotal).Append(" posted, ")
				.Append(last.RefutedTotal).Append(" refuted, ")
				.Append(last.Degraded).Append(" degraded");
			// The author's own verdict, stated only when the line recorded it and the author actually
			// ruled on something. Silence here means "nobody ruled on anything", not "the author
			// agreed with nothing".
			if (last.RecordsAuthorVerdicts && last.ResolvedTotal + last.WithdrawnTotal > 0)
			{
				sb.Append(", ").Append(last.ResolvedTotal).Append(" resolved and ")
					.Append(last.WithdrawnTotal).Append(" withdrawn by the author");
			}

			sb.Append("._\n");
		}

		if (evicted > 0)
		{
			sb.Append('\n').Append(EvictedOpen).Append(evicted).Append(Close).Append('\n');
		}

		sb.Append('\n').Append(Open).Append(payload).Append(Close).Append('\n');
		return sb.ToString();
	}
}
