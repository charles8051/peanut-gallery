using System;
using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>
/// One changed file in a unified diff. <see cref="Segment"/> is that file's raw diff
/// block (from its <c>diff --git</c> line to the next), so a filtered diff can be
/// rebuilt from a subset of files. <see cref="IsBinary"/> / <see cref="IsRenameOnly"/>
/// mark low-signal files the filter can drop.
/// </summary>
public sealed record DiffFile(
	string Path,
	int AddedLines,
	int RemovedLines,
	string Segment = "",
	bool IsBinary = false,
	bool IsRenameOnly = false)
{
	/// <summary>
	/// The line spans this file's hunks touch on the NEW side, 1-based and inclusive, in the order
	/// the hunks appear. Parsed out of the <c>@@ -a,b +c,d @@</c> headers already inside
	/// <see cref="Segment"/>, so it costs no extra state: the segment is carried for filtering anyway.
	///
	/// <para>This is what lets <see cref="ContextBudget"/> send windows of a file rather than all of
	/// it. The NEW side is the side to read: context text is the file at the reviewed head, so its
	/// line numbers are post-image ones. A pure-deletion hunk (<c>+c,0</c>) has no post-image lines
	/// at all and is reported as the single line <c>c</c> - where the deleted code used to sit, and
	/// where the window around it should be centred.</para>
	///
	/// <para>Total, like <see cref="Diff.Parse"/>: an unparseable header is skipped, never thrown on.
	/// No readable header yields an empty list, which callers read as "nothing is known about where
	/// this file changed" and answer by falling back to the whole file.</para>
	/// </summary>
	public IReadOnlyList<LineRange> HunkRanges()
	{
		if (Segment.Length == 0)
		{
			return [];
		}

		var ranges = new List<LineRange>();
		foreach (var line in Segment.Split('\n'))
		{
			// Only a real hunk header starts at column zero with "@@"; a content line that quotes
			// "@@" carries a '+', '-', or ' ' prefix in a unified diff, so it cannot reach here.
			if (!line.StartsWith("@@", StringComparison.Ordinal))
			{
				continue;
			}

			var plus = line.IndexOf('+');
			if (plus < 0)
			{
				continue;
			}

			var rest = line[(plus + 1)..];
			var stop = rest.IndexOf(' ');
			var span = stop < 0 ? rest : rest[..stop];
			var comma = span.IndexOf(',');
			if (!int.TryParse(comma < 0 ? span : span[..comma], out var start))
			{
				continue;
			}

			var count = 1;
			if (comma >= 0 && !int.TryParse(span[(comma + 1)..], out count))
			{
				continue;
			}

			start = Math.Max(1, start);
			ranges.Add(new LineRange(start, count <= 0 ? start : start + count - 1));
		}

		return ranges;
	}
}

/// <summary>
/// A parsed unified ("git") diff. <see cref="Raw"/> is what gets handed to the model;
/// <see cref="Files"/> is a per-file index (with each file's raw segment) used for
/// summaries, routing, and filtering. Parsing is total: any input yields a
/// <see cref="Diff"/>, never an exception.
/// </summary>
public sealed record Diff(string Raw, IReadOnlyList<DiffFile> Files)
{
	public static Diff Empty { get; } = new(string.Empty, []);

	public bool IsEmpty => string.IsNullOrWhiteSpace(Raw);

	public static Diff Parse(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return Empty;
		}

		var lines = raw.Replace("\r\n", "\n").Split('\n');
		var files = new List<DiffFile>();

		List<string>? segment = null;
		string? path = null;
		var added = 0;
		var removed = 0;
		var binary = false;
		var rename = false;

		void Flush()
		{
			if (path is not null && segment is not null)
			{
				files.Add(new DiffFile(
					path, added, removed,
					string.Join("\n", segment),
					binary,
					rename && added == 0 && removed == 0));
			}
		}

		foreach (var line in lines)
		{
			if (line.StartsWith("diff --git ", StringComparison.Ordinal))
			{
				Flush();
				segment = [line];
				path = ParseGitHeaderPath(line);
				added = 0;
				removed = 0;
				binary = false;
				rename = false;
				continue;
			}

			if (segment is null)
			{
				continue; // preamble before the first file header
			}

			segment.Add(line);

			if (line.StartsWith("+++ ", StringComparison.Ordinal)
				|| line.StartsWith("--- ", StringComparison.Ordinal)
				|| line.StartsWith("@@", StringComparison.Ordinal))
			{
				// File markers and hunk headers are not content lines.
			}
			else if (line.StartsWith("rename from ", StringComparison.Ordinal)
				|| line.StartsWith("rename to ", StringComparison.Ordinal))
			{
				rename = true;
			}
			else if (line.StartsWith("Binary files ", StringComparison.Ordinal)
				|| line.StartsWith("GIT binary patch", StringComparison.Ordinal))
			{
				binary = true;
			}
			else if (line.StartsWith('+'))
			{
				added++;
			}
			else if (line.StartsWith('-'))
			{
				removed++;
			}
		}

		Flush();
		return new Diff(raw, files);
	}

	// "diff --git a/src/Foo.cs b/src/Foo.cs" -> "src/Foo.cs" (prefer the "b/" path).
	private static string ParseGitHeaderPath(string header)
	{
		var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		var b = parts.Length > 0 ? parts[^1] : header;
		return b.StartsWith("b/", StringComparison.Ordinal) ? b[2..] : b;
	}
}
