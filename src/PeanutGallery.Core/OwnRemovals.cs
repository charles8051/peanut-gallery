using System;
using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>One file's removed lines that this pull request had itself added earlier.</summary>
public sealed record OwnRemovalFile(string Path, IReadOnlyList<string> Lines);

/// <summary>
/// The lines a continued turn sees REMOVED which this same pull request had added on an earlier
/// turn — mechanically derived, never asked of a model.
///
/// <para><b>The defect this answers (#178).</b> Turn 1 gets the whole PR diff; turn 2+ gets only the
/// delta since the last reviewed SHA. So a symbol an earlier turn of the same PR introduced, when
/// later renamed or reworked, arrives in the delta as the removal of an apparently long-established
/// API — and the panel files a breaking-change finding against code that has never existed on the
/// base branch. It punishes exactly the behaviour a review wants: acting on feedback. On
/// <a href="https://github.com/charles8051/peanut-gallery/pull/175">#175</a> turn 1 added
/// <c>Trajectory.Of(IReadOnlyList&lt;Turn&gt;)</c>, turn 2 renamed it to <c>OfTurns</c> in response
/// to a turn-1 finding, and two personas filed <c>major</c> for breaking callers that cannot exist.
/// It compounds: every correction creates a fresh delta in which the previous correction reads as an
/// unexplained deletion.</para>
///
/// <para><b>Why arithmetic and not a question.</b> The
/// <a href="../../docs/feature-specs/finding-scope/ab-finding-scope.md">finding-scope A/B</a> asked a
/// model to self-report whether a hazard was introduced or pre-existing and got 0 <c>pre-existing</c>
/// verdicts in 48 trials: context is read as a CONTRAST detector, so evidence that agrees with the
/// diff is invisible. Its conclusion — derive scope from a baseline rather than asking — is what this
/// type is. Diffs in, a set of lines out; no model, no clock, no IO.</para>
///
/// <para><b>The identity.</b> Write <c>count(L, F, R)</c> for how many times the trimmed line text
/// <c>L</c> occurs in file <c>F</c> at revision <c>R</c>. The two diffs supply two differences
/// directly: the delta (last-reviewed → head) gives <c>count(lastReviewed) − count(head)</c> as (its
/// removals − its additions), and the cumulative diff (merge base → head) gives
/// <c>count(head) − count(base)</c> as (its additions − its removals). Summing them telescopes to
/// <c>count(lastReviewed) − count(base)</c>. A strictly positive result means the last-reviewed tree
/// held MORE copies of that line than the base did — the surplus can only have come from this pull
/// request, so removing one of them cannot break anything that predates it.</para>
///
/// <para>The sum is exact even though a diff shows only its hunks: every region a diff does not show
/// is byte-identical on both sides, so it contributes equally to both counts and cancels. That is
/// what makes the answer decidable from two diffs alone, with no file contents and no repository.</para>
///
/// <para><b>Totality, and which way it must fail.</b> A missing, empty or unusable cumulative diff
/// yields <see cref="Unknown"/> — "cannot tell" — and the prompt then states nothing, which is
/// today's behaviour. It must never degrade towards a false "this PR introduced it": that would
/// suppress a genuine breaking-change finding, which is worse than the bug being fixed. Every
/// approximation here is therefore chosen so it can LOSE claims, never manufacture them.</para>
/// </summary>
/// <param name="IsKnown">
/// False when no baseline was available, so nothing at all was established. Distinct from a known
/// answer carrying no files, which means "checked, and nothing removed here is this PR's own work".
/// </param>
public sealed record OwnRemovals(bool IsKnown, IReadOnlyList<OwnRemovalFile> Files)
{
	/// <summary>No baseline; nothing established. The prompt says nothing.</summary>
	public static OwnRemovals Unknown { get; } = new(false, []);

	/// <summary>A baseline was available and attributed nothing to this pull request.</summary>
	public static OwnRemovals None { get; } = new(true, []);

	/// <summary>True when there is a fact worth stating.</summary>
	public bool HasAny => Files.Count > 0;

	/// <summary>
	/// The same answer, narrowed to the files <paramref name="shown"/> actually contains — the diff
	/// the model is given, after filtering and after any rung of the shrink ladder.
	///
	/// <para>This is the second half of <see cref="Of"/>'s completeness precondition. The arithmetic
	/// needs the WHOLE delta or it can manufacture a claim; the prompt needs to name only lines the
	/// reviewer can see, or it is asserting things about code that is not in front of it. Deriving
	/// from the complete delta and narrowing here satisfies both, and narrowing is safe in a way
	/// filtering the input is not: dropping a file from the ANSWER removes claims, where dropping it
	/// from the INPUT removes a cancelling addition.</para>
	///
	/// <para><see cref="IsKnown"/> is carried through unchanged: narrowing what is reported says
	/// nothing about whether a baseline existed.</para>
	/// </summary>
	public OwnRemovals OnlyIn(Diff? shown)
	{
		if (!IsKnown || Files.Count == 0)
		{
			return this;
		}

		var paths = new HashSet<string>(StringComparer.Ordinal);
		foreach (var f in shown?.Files ?? [])
		{
			paths.Add(f.Path);
		}

		var kept = new List<OwnRemovalFile>();
		foreach (var f in Files)
		{
			if (paths.Contains(f.Path))
			{
				kept.Add(f);
			}
		}

		return kept.Count == Files.Count ? this : kept.Count == 0 ? None : this with { Files = kept };
	}

	/// <summary>Total attributed lines across every file, for an "and N more" note.</summary>
	public int LineCount
	{
		get
		{
			var n = 0;
			foreach (var f in Files)
			{
				n += f.Lines.Count;
			}

			return n;
		}
	}

	/// <summary>
	/// Which lines removed in <paramref name="delta"/> this pull request had itself added earlier,
	/// judged against <paramref name="cumulative"/> — the PR's whole diff, merge base → head.
	///
	/// <para><b>Both diffs must be COMPLETE, and must share a head.</b> This is a precondition, not
	/// a preference, and both halves of it are load-bearing:</para>
	///
	/// <para><b>Complete.</b> The identity telescopes only because the delta's additions cancel its
	/// own removals of the same text. Hand it a diff with whole files removed — a
	/// <see cref="DiffFilter"/>ed one, a rung of the shrink ladder — and a line the delta both
	/// removes from a kept file and adds to a dropped one loses its cancelling addition, so the sum
	/// rises and a claim can be MANUFACTURED. Pass the raw delta and narrow the answer afterwards
	/// with <see cref="OnlyIn"/>, which drops findings instead of inventing them.</para>
	///
	/// <para><b>Shared head.</b> The <c>count(head)</c> terms cancel only if both diffs end at the
	/// same commit. A cumulative diff resolved from a moving pull-request ref while the delta is
	/// anchored to the run's head SHA leaves a residue of <c>count(head') − count(head)</c>, which is
	/// positive exactly when the newer push re-added a line this turn removed — again the
	/// manufacturing direction. Shells must resolve the baseline against the immutable head SHA.</para>
	/// </summary>
	public static OwnRemovals Of(Diff? delta, Diff? cumulative)
	{
		// No delta means nothing was removed; no cumulative diff means no baseline at all. Both are
		// "cannot tell", and both must render as silence rather than as an attribution.
		//
		// A cumulative diff that parsed into NO FILES is the case worth spelling out. Diff.Parse is
		// total, so text that is not a diff at all comes back non-empty with an empty file list -
		// and an empty file list is indistinguishable, to the arithmetic below, from "the base
		// branch and the head are identical", under which EVERY removal in the delta is attributed
		// to this pull request. That is precisely the false direction: a wrong attribution suppresses
		// a real breaking-change finding. So an unusable baseline is rejected here rather than
		// believed.
		if (delta is null || cumulative is null || delta.IsEmpty || cumulative.IsEmpty
			|| delta.Files.Count == 0 || cumulative.Files.Count == 0)
		{
			return Unknown;
		}

		// count(lastReviewed) - count(base), accumulated per (file, line) and again repo-wide.
		var perFile = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
		var overall = new Dictionary<string, int>(StringComparer.Ordinal);
		var removed = new Dictionary<string, (List<string> Order, HashSet<string> Seen)>(StringComparer.Ordinal);

		foreach (var file in delta.Files)
		{
			var counts = Counts(perFile, file.Path);
			foreach (var (isAdd, text) in ContentLines(file.Segment))
			{
				Bump(counts, text, isAdd ? -1 : +1);
				Bump(overall, text, isAdd ? -1 : +1);
				if (isAdd)
				{
					continue;
				}

				if (!removed.TryGetValue(file.Path, out var order))
				{
					removed[file.Path] = order = ([], new HashSet<string>(StringComparer.Ordinal));
				}

				// Distinct texts, in the order the delta removes them: one reported line per
				// distinct text, not one per occurrence.
				if (order.Seen.Add(text))
				{
					order.Order.Add(text);
				}
			}
		}

		foreach (var file in cumulative.Files)
		{
			var counts = Counts(perFile, file.Path);
			foreach (var (isAdd, text) in ContentLines(file.Segment))
			{
				Bump(counts, text, isAdd ? +1 : -1);
				Bump(overall, text, isAdd ? +1 : -1);
			}
		}

		var files = new List<OwnRemovalFile>();
		foreach (var file in delta.Files)
		{
			if (!removed.TryGetValue(file.Path, out var order))
			{
				continue;
			}

			var counts = Counts(perFile, file.Path);
			var lines = new List<string>();
			foreach (var text in order.Order)
			{
				// BOTH gates, because each closes a hole the other leaves open, and requiring both
				// can only drop claims. Per-file alone mis-attributes code this PR MOVED between
				// files (an earlier turn moves a pre-existing method from A to B; a later turn
				// deletes it from B, and B's own arithmetic sees a surplus), and mis-attributes a
				// rename whose detection differs between the two diffs, where the base-side evidence
				// lands under a path the delta never names. Repo-wide alone is blind to a line text
				// that is genuinely this PR's in one file while another file removes an established
				// copy of the same text. The conjunction answers both.
				if (Net(counts, text) > 0 && Net(overall, text) > 0 && CarriesAName(text))
				{
					lines.Add(text);
				}
			}

			if (lines.Count > 0)
			{
				files.Add(new OwnRemovalFile(file.Path, lines));
			}
		}

		return files.Count == 0 ? None : new OwnRemovals(true, files);
	}

	private static Dictionary<string, int> Counts(Dictionary<string, Dictionary<string, int>> byPath, string path)
	{
		if (!byPath.TryGetValue(path, out var counts))
		{
			byPath[path] = counts = new Dictionary<string, int>(StringComparer.Ordinal);
		}

		return counts;
	}

	private static void Bump(Dictionary<string, int> counts, string text, int by) =>
		counts[text] = (counts.TryGetValue(text, out var n) ? n : 0) + by;

	private static int Net(Dictionary<string, int> counts, string text) =>
		counts.TryGetValue(text, out var n) ? n : 0;

	/// <summary>
	/// A line worth telling the model about carries a name it could mistake for established API.
	/// Braces, closing parens and blank lines cannot be, and a delta full of them would dilute the
	/// block that matters. Applied AFTER the arithmetic, so filtering only ever drops a claim.
	/// </summary>
	private static bool CarriesAName(string text)
	{
		foreach (var c in text)
		{
			if (char.IsLetterOrDigit(c))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// The <c>+</c>/<c>-</c> content lines of one file's diff segment, trimmed.
	///
	/// <para>Trimmed, deliberately: reindenting a line an earlier turn added would otherwise leave
	/// the removal unmatched. Trimming merges line texts into coarser classes, and since every
	/// class's value is the SUM of its members' — all three counts are additive — merging can only
	/// turn a positive into a non-positive, never the reverse. So the coarser comparison drops
	/// claims and cannot invent one.</para>
	///
	/// <para>Only lines inside a hunk count, and the hunk gate is what excludes the file markers
	/// rather than a prefix test: <c>--- a/…</c> and <c>+++ b/…</c> open with the same characters as
	/// content, but they — like <c>index</c>, <c>new file mode</c>, <c>rename from</c> and
	/// <c>GIT binary patch</c> — always precede the first <c>@@</c>. Testing the prefix instead would
	/// also swallow a genuine removal of a line reading <c>-- …</c>, and swallowing a base-side
	/// removal is the one direction that can manufacture a claim.</para>
	/// </summary>
	private static IEnumerable<(bool IsAdd, string Text)> ContentLines(string segment)
	{
		if (segment.Length == 0)
		{
			yield break;
		}

		var inHunk = false;
		foreach (var raw in segment.Split('\n'))
		{
			var line = raw.EndsWith('\r') ? raw[..^1] : raw;
			if (line.StartsWith("@@", StringComparison.Ordinal))
			{
				inHunk = true;
				continue;
			}

			if (!inHunk || line.Length == 0)
			{
				continue;
			}

			var text = line[1..].Trim();
			if (text.Length == 0)
			{
				continue;
			}

			if (line[0] == '+')
			{
				yield return (true, text);
			}
			else if (line[0] == '-')
			{
				yield return (false, text);
			}
		}
	}
}
