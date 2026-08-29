using System;
using System.Collections.Generic;
using System.Text;

namespace PeanutGallery.Core;

/// <summary>
/// One changed file's current text at the reviewed commit, offered as context. What a shell reads
/// is the whole file; what survives <see cref="ContextBudget.Fit"/> is either that same text or an
/// excerpt of it - the windows around the diff's hunks, with every elided range marked inside the
/// text itself.
/// </summary>
public sealed record FileContext(string Path, string Text);

/// <summary>A 1-based, inclusive span of lines: a diff hunk, or the window grown around one.</summary>
public sealed record LineRange(int Start, int End);

/// <summary>Which context files fit the budget, and which were left out (disclosed, never silent).</summary>
public sealed record ContextSelection(IReadOnlyList<FileContext> Kept, IReadOnlyList<string> Omitted);

/// <summary>
/// Chooses how much of the changed files' current text to send alongside the diff.
///
/// <para>A diff-tier persona sees only the unified diff - git's default three lines around each
/// hunk - which is how it comes to report a missing guard that exists five lines above the hunk.
/// Agent-tier personas can go read the file; diff-tier ones cannot, and they are the default
/// panel. Attaching the changed files' text is the cheap fix, and it beats a wider unified-diff
/// context because GitHub's diff endpoint has no context-width knob to turn.</para>
///
/// <para><b>Windows, not whole files.</b> This was whole-file-or-nothing until one production run, where
/// the file the PR churned hardest - an 85KB repository class - was on its own larger than the
/// entire budget and so could not be sent on any of the 15 review runs. The panel reviewed it
/// hunk-only and filed precisely the defect this type exists to prevent: a finding citing
/// <c>:635</c> for a gate that sits 23 lines above the citation, plus a guard that does not exist.
/// The file a PR churns hardest is usually its largest, so whole-file-or-nothing withheld context
/// exactly where it was worth most. Each file now contributes the regions around its own hunks,
/// padded by <see cref="WindowPadLines"/> lines and merged where they overlap; a file drops out
/// entirely only when not one of its windows fits.</para>
///
/// <para>Windowing is how a file survives the budget, not a saving to chase: a file that still fits
/// whole is still sent whole, because the lines outside a hunk are exactly where the guard a
/// reviewer is about to call missing turns out to live.</para>
///
/// <para>Smallest-first, mirroring <see cref="DiffFilter"/>: with a fixed budget, packing the small
/// files first maximises how many files the reviewer can see, and it is the big generated file you
/// least want to spend the budget on. Rank is by the cheapest form a file could take - windowed,
/// where that is smaller - so a file is ordered by what it may actually cost the prompt rather than
/// by how big it happens to be on disk.</para>
///
/// <para><b>Nothing disappears quietly.</b> A file the budget could not take at all is named in
/// <see cref="ContextSelection.Omitted"/>; a line range dropped from a file that WAS taken is
/// marked in that file's text, so the model cannot read two windows as contiguous code or count
/// line numbers straight through a gap.</para>
///
/// <para>The budget is counted in UTF-8 bytes, which is what the provider request is encoded as -
/// not <see cref="string.Length"/>, which counts UTF-16 code units and lets a non-ASCII file
/// overrun a limit that has said "bytes" in its name since it was written.</para>
///
/// <para>Pure - text and ranges in, text and ranges out. Every ordering here is total (size, then
/// ordinal path) because a run's personas share one prompt prefix and the provider's automatic
/// prefix cache only fires on a byte-identical one: same inputs, same bytes.</para>
/// </summary>
public static class ContextBudget
{
	/// <summary>Default context budget, separate from (and smaller than) the diff's own cap.</summary>
	public const int DefaultBudgetBytes = 64 * 1024;

	/// <summary>
	/// Lines kept either side of a hunk. Git's three are what produce the "missing" guard that sits
	/// four lines up; ~50 reaches the enclosing method's preamble - the signature, the null checks,
	/// the early returns - which is where the evidence refuting a diff-tier finding almost always
	/// lives. Much wider mostly buys unrelated methods at full token price.
	/// </summary>
	public const int WindowPadLines = 50;

	/// <summary>
	/// Pack as much context as the budget allows: each file whole where it fits, else windowed
	/// around its hunks, else named as omitted. <paramref name="diff"/> is where the hunk locations
	/// come from; without it (or for a candidate the diff does not mention) there is nothing to
	/// window around, so the file is offered whole or not at all, exactly as before.
	/// </summary>
	public static ContextSelection Fit(
		IReadOnlyList<FileContext> candidates,
		int budgetBytes,
		Diff? diff = null,
		int padLines = WindowPadLines)
	{
		if (candidates.Count == 0 || budgetBytes <= 0)
		{
			return new ContextSelection([], [.. Paths(candidates)]);
		}

		var hunks = HunksByPath(diff);
		var windowed = new List<Windowed>(candidates.Count);
		foreach (var c in candidates)
		{
			var lines = SplitLines(c.Text);
			var windows = Windows(hunks.TryGetValue(c.Path, out var h) ? h : [], lines.Count, padLines);
			var rendered = Render(c.Text, lines, windows);
			windowed.Add(new Windowed(
				c.Path, c.Text, lines, windows, rendered,
				Math.Min(Bytes(c.Text), Bytes(rendered))));
		}

		// Stable order by cheapest size, then path, so the same inputs always produce the same prompt.
		windowed.Sort((a, b) =>
		{
			var bySize = a.Cost.CompareTo(b.Cost);
			return bySize != 0 ? bySize : string.CompareOrdinal(a.Path, b.Path);
		});

		var kept = new List<FileContext>();
		var omitted = new List<string>();
		var spent = 0;
		foreach (var w in windowed)
		{
			var text = Choose(w, budgetBytes - spent);
			if (text is null)
			{
				omitted.Add(w.Path);
				continue;
			}

			kept.Add(new FileContext(w.Path, text));
			spent += Bytes(text);
		}

		// Present kept files in path order - size order is a packing detail, not a reading order.
		kept.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
		omitted.Sort(string.CompareOrdinal);
		return new ContextSelection(kept, omitted);
	}

	/// <summary>
	/// Grow each hunk by <paramref name="padLines"/> either side, clamp to the file, and merge what
	/// overlaps - so two hunks in the same method arrive as one readable region instead of the same
	/// lines twice. Ranges that touch or sit one line apart are merged too: a one-line gap costs
	/// more as an elision marker than as the line itself.
	///
	/// <para>No hunks means nothing is known about where the file changed, and the honest answer is
	/// the whole file - which is also what every caller predating windowing got.</para>
	/// </summary>
	public static IReadOnlyList<LineRange> Windows(
		IReadOnlyList<LineRange> hunks, int totalLines, int padLines = WindowPadLines)
	{
		if (totalLines <= 0)
		{
			return [];
		}

		if (hunks.Count == 0)
		{
			return [new LineRange(1, totalLines)];
		}

		var grown = new List<LineRange>(hunks.Count);
		foreach (var h in hunks)
		{
			var start = Math.Max(1, Math.Min(h.Start, h.End) - padLines);
			var end = Math.Min(totalLines, Math.Max(h.Start, h.End) + padLines);
			if (start <= end)
			{
				grown.Add(new LineRange(start, end));
			}
		}

		// Every hunk fell outside the text (a stale read: context is fetched at head, the diff may
		// describe a different one). Treat that as "location unknown" rather than "nothing to show".
		if (grown.Count == 0)
		{
			return [new LineRange(1, totalLines)];
		}

		grown.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));

		var merged = new List<LineRange>();
		var current = grown[0];
		for (var i = 1; i < grown.Count; i++)
		{
			var next = grown[i];
			if (next.Start <= current.End + 1)
			{
				current = current with { End = Math.Max(current.End, next.End) };
			}
			else
			{
				merged.Add(current);
				current = next;
			}
		}

		merged.Add(current);
		return merged;
	}

	/// <summary>
	/// A candidate plus the shapes it can take in the prompt. <c>Cost</c> is what the file occupies
	/// when taken in full - the whole text, or its complete window set where that is smaller - and
	/// is what smallest-first ranks on. Measured once at construction rather than in the comparator,
	/// which would otherwise re-encode both strings on every comparison.
	///
	/// <para>Deliberately NOT the smallest single window the file could be cut down to, though that
	/// is the true floor now that <see cref="Choose"/> can skip a window. Ranking by a floor a file
	/// will almost never occupy orders the pack around a hypothetical: a file admitted early on a
	/// 200-byte floor then takes its whole 40KB window set and starves the files behind it. Rank by
	/// what a file will actually spend, and the ordering predicts the packing.</para>
	/// </summary>
	private sealed record Windowed(
		string Path,
		string Text,
		IReadOnlyList<string> Lines,
		IReadOnlyList<LineRange> Windows,
		string Rendered,
		int Cost);

	/// <summary>
	/// What this text costs the budget. UTF-8, because that is what the provider request is encoded
	/// as by the time the budget means anything - and <see cref="string.Length"/> is UTF-16 code
	/// units, which undercounts every non-ASCII file by up to 3x (and every emoji or CJK-heavy one
	/// reliably). The budget has said "bytes" in its name and doc since it was written; this is what
	/// makes that true rather than approximately true for ASCII.
	/// </summary>
	private static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);

	/// <summary>
	/// The most of this file that fits in <paramref name="room"/> bytes: all of it, else all of its
	/// windows, else the windows that fit, else null - nothing of it can be sent.
	///
	/// <para>Windows are offered in line order and a window that does not fit is SKIPPED, not
	/// terminal. Taking a prefix instead was the obvious loop and the wrong one: window size follows
	/// line length, so one hunk landing in a generated or long-line region could exceed the room
	/// left and drop the whole file - recreating this type's original defect for exactly the large,
	/// heavily-changed files windowing exists to rescue. Skipping is the same loop and cannot do
	/// that. Each candidate set is measured as actually rendered, markers included, because the
	/// markers are part of what the budget pays for.</para>
	///
	/// <para>Greedy in line order, deliberately, and not a knapsack: earlier hunks are not more
	/// important than later ones, but some total order is needed and reading order is the one a
	/// human can predict and the cache can rely on. Dropped windows need no separate disclosure -
	/// the renderer derives its elision markers from the gaps between the windows it is handed, so
	/// a window that was skipped simply reads as elided, which it is.</para>
	///
	/// <para><b>Nothing is omitted while one of its windows still fits.</b> A window is measured
	/// against what has been accepted so far, and while nothing has been accepted that is the window
	/// on its own - so returning null means every window, measured alone, was too big for the room.
	/// The property is worth stating because the whole point of this method is that a file is never
	/// dropped for a reason other than not fitting.</para>
	///
	/// <para>The size of each candidate is summed rather than rendered. Rendering every candidate to
	/// measure it was the obvious version and it is quadratic - bounded, since what has been accepted
	/// can never exceed the budget, but measured at 92ms for a 160KB file with 227 windows, against
	/// ~1ms here. The arithmetic mirrors <see cref="Render"/> exactly because both count the same
	/// two strings (<see cref="MarkerText"/>, <see cref="HeaderText"/>), and the selected set is
	/// handed back to <see cref="Render"/> at the end rather than assembled here: one authority on
	/// layout, which the prompt-cache determinism depends on.</para>
	/// </summary>
	private static string? Choose(Windowed w, int room)
	{
		if (Bytes(w.Text) <= room)
		{
			return w.Text;
		}

		if (Bytes(w.Rendered) <= room)
		{
			return w.Rendered;
		}

		var taken = new List<LineRange>(w.Windows.Count);
		var spent = 0; // everything Render would emit for `taken` except its trailing marker
		var cursor = 1;
		foreach (var window in w.Windows)
		{
			// Markers and headers are ASCII, so their character count is their byte count.
			var gap = MarkerText(cursor, window.Start - 1).Length
				+ HeaderText(window, w.Lines.Count).Length
				+ ContentBytes(w.Lines, window);
			var trailing = MarkerText(window.End + 1, w.Lines.Count).Length;
			if (spent + gap + trailing > room)
			{
				continue;
			}

			taken.Add(window);
			spent += gap;
			cursor = window.End + 1;
		}

		return taken.Count == 0 ? null : Render(w.Text, w.Lines, taken);
	}

	private static int ContentBytes(IReadOnlyList<string> lines, LineRange window)
	{
		var bytes = 0;
		for (var line = window.Start; line <= window.End; line++)
		{
			bytes += Bytes(lines[line - 1]) + 1; // + the newline the renderer appends
		}

		return bytes;
	}

	/// <summary>
	/// These windows as one block of text, every gap between them spelled out - including the gaps
	/// left by windows the caller chose not to pass. A file whose single window covers all of it is
	/// returned byte-for-byte as it was read: nothing was elided, so there is nothing to mark, and
	/// the prompt stays identical to what it was before windowing existed.
	/// </summary>
	private static string Render(
		string original, IReadOnlyList<string> lines, IReadOnlyList<LineRange> windows)
	{
		if (windows.Count == 1 && windows[0].Start == 1 && windows[0].End == lines.Count)
		{
			return original;
		}

		var sb = new StringBuilder();
		var cursor = 1;
		for (var i = 0; i < windows.Count; i++)
		{
			var w = windows[i];
			sb.Append(MarkerText(cursor, w.Start - 1)).Append(HeaderText(w, lines.Count));
			for (var line = w.Start; line <= w.End; line++)
			{
				sb.Append(lines[line - 1]).Append('\n');
			}

			cursor = w.End + 1;
		}

		sb.Append(MarkerText(cursor, lines.Count));
		return sb.ToString();
	}

	// Where a window starts and ends, and how long the file it was cut from is - so a citation can
	// be checked against a real line number rather than counted from the top of an excerpt.
	private static string HeaderText(LineRange window, int totalLines) =>
		$"@@ lines {window.Start}-{window.End} of {totalLines} @@\n";

	// The marker that keeps an excerpt honest: it names the exact lines the model is not being
	// shown, so a gap can be neither mistaken for contiguous code nor counted through. Empty for an
	// empty range, which is what makes a window at the first or last line render without one.
	private static string MarkerText(int from, int to) => to < from
		? string.Empty
		: from == to
			? $"... 1 line elided (line {from}) ...\n"
			: $"... {to - from + 1} lines elided (lines {from}-{to}) ...\n";

	private static Dictionary<string, List<LineRange>> HunksByPath(Diff? diff)
	{
		var map = new Dictionary<string, List<LineRange>>(StringComparer.Ordinal);
		if (diff is null)
		{
			return map;
		}

		foreach (var f in diff.Files)
		{
			var ranges = f.HunkRanges();
			if (ranges.Count == 0)
			{
				continue;
			}

			if (!map.TryGetValue(f.Path, out var list))
			{
				map[f.Path] = list = [];
			}

			list.AddRange(ranges);
		}

		return map;
	}

	// A trailing newline ends the last line; it does not begin another one. CRLF text survives:
	// the '\r' stays on the end of each line and the renderer joins with '\n' again.
	private static List<string> SplitLines(string text)
	{
		if (text.Length == 0)
		{
			return [];
		}

		var parts = text.Split('\n');
		var count = parts.Length;
		if (parts[count - 1].Length == 0)
		{
			count--;
		}

		var lines = new List<string>(count);
		for (var i = 0; i < count; i++)
		{
			lines.Add(parts[i]);
		}

		return lines;
	}

	private static IEnumerable<string> Paths(IReadOnlyList<FileContext> files)
	{
		foreach (var f in files)
		{
			yield return f.Path;
		}
	}
}
