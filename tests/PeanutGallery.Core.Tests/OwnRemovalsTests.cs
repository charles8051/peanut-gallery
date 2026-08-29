using System.Linq;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// <see cref="OwnRemovals.Of"/> — which of a continued turn's removals this pull request had itself
/// added earlier (#178).
///
/// <para>Every test here reads in the same direction: the function may LOSE a claim for any reason
/// at all, and may never manufacture one. A false "this PR introduced it" tells a reviewer that a
/// genuine breaking change cannot break anything, which is worse than the bug being fixed.</para>
/// </summary>
public class OwnRemovalsTests
{
	// One file's diff segment, assembled so the tests read as diffs rather than as string plumbing.
	private static string File(string path, string hunk, string? header = null) =>
		$"diff --git a/{path} b/{path}\n"
		+ (header is null ? string.Empty : header + "\n")
		+ $"--- a/{path}\n+++ b/{path}\n@@ -1,4 +1,4 @@\n{hunk}";

	private static Diff Parse(params string[] files) => Diff.Parse(string.Join(string.Empty, files));

	private static string[] LinesFor(OwnRemovals own, string path) =>
		own.Files.Where(f => f.Path == path).SelectMany(f => f.Lines).ToArray();

	// The #175 case, reduced to its two diffs. Turn 1 introduced Trajectory.Of(IReadOnlyList<Turn>);
	// turn 2 renamed it to OfTurns in response to a turn-1 finding, and two personas filed `major`
	// for breaking callers of a method that has never existed on main.
	private const string Trajectory = "src/PeanutGallery.Core/Trajectory.cs";

	private const string RenamedAway = "public static Trajectory? Of(IReadOnlyList<Turn> turnsOldestFirst)";

	private const string RenamedTo = "public static Trajectory? OfTurns(IReadOnlyList<Turn> turnsOldestFirst)";

	[Fact]
	public void A_line_an_earlier_turn_of_this_pull_request_added_is_attributed_to_it()
	{
		var delta = Parse(File(Trajectory, $" \tsummary\n-\t{RenamedAway}\n+\t{RenamedTo}\n"));
		var cumulative = Parse(File(Trajectory, $" \tsummary\n+\t{RenamedTo}\n"));

		var own = OwnRemovals.Of(delta, cumulative);

		Assert.True(own.IsKnown);
		Assert.Equal([RenamedAway], LinesFor(own, Trajectory));
	}

	[Fact]
	public void A_line_that_predates_the_pull_request_is_not_attributed_to_it()
	{
		// The base branch had it, so the cumulative diff removes it too: the surplus is zero.
		const string established = "public static int Legacy(string s) => s.Length;";
		var delta = Parse(File(Trajectory, $"-\t{established}\n"));
		var cumulative = Parse(File(Trajectory, $"-\t{established}\n"));

		var own = OwnRemovals.Of(delta, cumulative);

		Assert.True(own.IsKnown);
		Assert.False(own.HasAny);
		Assert.Same(OwnRemovals.None, own);
	}

	// The ambiguous case, and the choice this type makes about it. Line texts are compared as a
	// MULTISET, never as identities: a diff cannot say which physical occurrence of an identical
	// line it removed, and no amount of prompt wording would recover that. So the question asked is
	// only ever "did the last-reviewed tree hold MORE copies of this text than the base did?".
	//
	// One copy at the base and one removal is therefore not this PR's (below); two removals against
	// one base copy is a surplus of exactly one, and the text is reported (next). The claim the
	// prompt makes is about the text, not about a particular occurrence - which is exactly what the
	// reviewer needs, since what it is about to call established API is a text it read in the diff.
	[Fact]
	public void One_removal_matched_by_one_base_copy_is_ambiguous_and_stays_unclaimed()
	{
		const string shared = "return Normalize(value);";
		var delta = Parse(File(Trajectory, $"-\t{shared}\n"));
		var cumulative = Parse(File(Trajectory, $"-\t{shared}\n+\tvar x = 1;\n"));

		Assert.False(OwnRemovals.Of(delta, cumulative).HasAny);
	}

	[Fact]
	public void A_surplus_copy_of_an_otherwise_established_line_is_claimed()
	{
		const string shared = "return Normalize(value);";
		// The delta removes two copies; the base held one (the cumulative removes it). Surplus: 1.
		var delta = Parse(File(Trajectory, $"-\t{shared}\n-\t{shared}\n"));
		var cumulative = Parse(File(Trajectory, $"-\t{shared}\n"));

		Assert.Equal([shared], LinesFor(OwnRemovals.Of(delta, cumulative), Trajectory));
	}

	[Fact]
	public void Every_line_of_a_file_this_pull_request_added_is_its_own()
	{
		const string added = "src/PeanutGallery.Core/Turn.cs";
		var delta = Parse(File(added, "-\tpublic sealed record Turn(DiffShape Shape);\n"));
		var cumulative = Parse(File(
			added,
			"+\tpublic sealed record Turn(DiffShape Shape);\n+\tpublic static Turn Of(DiffShape s) => new(s);\n",
			header: "new file mode 100644"));

		Assert.Equal(
			["public sealed record Turn(DiffShape Shape);"],
			LinesFor(OwnRemovals.Of(delta, cumulative), added));
	}

	[Fact]
	public void An_empty_delta_establishes_nothing()
	{
		var cumulative = Parse(File(Trajectory, $"+\t{RenamedTo}\n"));

		Assert.False(OwnRemovals.Of(Diff.Empty, cumulative).IsKnown);
		Assert.False(OwnRemovals.Of(Diff.Parse("   "), cumulative).IsKnown);
		Assert.Same(OwnRemovals.Unknown, OwnRemovals.Of(Diff.Empty, cumulative));
	}

	[Fact]
	public void A_delta_that_only_adds_claims_nothing()
	{
		var delta = Parse(File(Trajectory, $"+\t{RenamedTo}\n"));
		var cumulative = Parse(File(Trajectory, $"+\t{RenamedTo}\n"));

		Assert.True(OwnRemovals.Of(delta, cumulative).IsKnown);
		Assert.False(OwnRemovals.Of(delta, cumulative).HasAny);
	}

	// Totality, and the direction it must fail in. Each of these is a baseline that could not be
	// used; none of them may come back as an attribution.
	[Fact]
	public void An_absent_or_unusable_baseline_degrades_to_cannot_tell()
	{
		var delta = Parse(File(Trajectory, $"-\t{RenamedAway}\n"));

		Assert.False(OwnRemovals.Of(delta, null).IsKnown);
		Assert.False(OwnRemovals.Of(delta, Diff.Empty).IsKnown);
		Assert.False(OwnRemovals.Of(delta, Diff.Parse("   \n ")).IsKnown);
		// Total parsing means garbage yields a non-empty Diff with no files. Believed, that reads as
		// "base and head are identical" and attributes EVERY removal to the pull request.
		Assert.False(OwnRemovals.Of(delta, Diff.Parse("504 Gateway Time-out")).IsKnown);
		Assert.False(OwnRemovals.Of(delta, Diff.Parse("{\"message\":\"diff too large\"}")).IsKnown);
		Assert.False(OwnRemovals.Of(null, null).IsKnown);
		Assert.False(OwnRemovals.Of(delta, Diff.Parse("504 Gateway Time-out")).HasAny);
	}

	[Fact]
	public void A_file_the_baseline_never_mentions_is_one_the_pull_request_left_as_it_found_it()
	{
		// The cumulative diff omitting a file means that file is byte-identical at base and head. So
		// a line the delta removes from it was in the tree only between two turns of this PR - the
		// shape of an earlier turn's experiment reverted by a later one.
		var delta = Parse(File("src/Probe.cs", "-\tvar probe = new Experiment();\n"));
		var cumulative = Parse(File(Trajectory, $"+\t{RenamedTo}\n"));

		Assert.Equal(
			["var probe = new Experiment();"],
			LinesFor(OwnRemovals.Of(delta, cumulative), "src/Probe.cs"));
	}

	[Fact]
	public void Code_this_pull_request_moved_between_files_is_not_claimed_as_its_own()
	{
		// The realistic miss a per-file check alone would make: an earlier turn moved an established
		// method from A to B, and this turn deletes it from B. B's own arithmetic sees a surplus,
		// because B never had the line at the base. Repo-wide, A's removal cancels it.
		const string moved = "public static string Slug(string name) => name.ToLowerInvariant();";
		var delta = Parse(File("src/B.cs", $"-\t{moved}\n"));
		var cumulative = Parse(
			File("src/A.cs", $"-\t{moved}\n"),
			File("src/B.cs", "+\tpublic static string Other() => \"x\";\n"));

		Assert.False(OwnRemovals.Of(delta, cumulative).HasAny);
	}

	[Fact]
	public void A_line_this_pull_request_merely_reindented_is_not_mistaken_for_its_own()
	{
		// The case that makes trimming load-bearing rather than cosmetic. An established line sits
		// at one indent on the base branch; an earlier turn reindented it, and this turn deletes it.
		// The delta removes the REINDENTED text while the cumulative diff removes the BASE text, so
		// comparing raw lines finds no match, sees a surplus, and attributes an established line to
		// the pull request - the one direction that costs a real finding.
		const string established = "public static string Slug(string name) => name.Trim();";
		var delta = Parse(File(Trajectory, $"-\t\t{established}\n"));
		var cumulative = Parse(File(Trajectory, $"-\t{established}\n"));

		Assert.False(OwnRemovals.Of(delta, cumulative).HasAny);
	}

	[Fact]
	public void Lines_carrying_no_name_are_not_reported()
	{
		// Braces and closing parens cannot be mistaken for established API, and a rework's delta is
		// full of them. Filtering happens after the arithmetic, so it can only drop claims.
		var delta = Parse(File(Trajectory, "-\t}\n-\t{\n-\t});\n-\t// ---\n-\tvar keep = Real();\n"));
		var cumulative = Parse(File(Trajectory, "+\tvar keep = Real();\n"));

		Assert.Equal(["var keep = Real();"], LinesFor(OwnRemovals.Of(delta, cumulative), Trajectory));
	}

	[Fact]
	public void Diff_metadata_is_never_read_as_content()
	{
		// Everything before the first @@ is metadata: the file markers open with the same characters
		// as content lines, and an `index` or `similarity index` line is about the change, not in it.
		var delta = Diff.Parse(
			$"diff --git a/{Trajectory} b/{Trajectory}\n"
			+ "similarity index 88%\nindex 1a2b3c4..5d6e7f8 100644\n"
			+ $"--- a/{Trajectory}\n+++ b/{Trajectory}\n@@ -1,2 +1,2 @@\n"
			+ $"-\t{RenamedAway}\n");
		var cumulative = Parse(File(Trajectory, $"+\t{RenamedTo}\n"));

		var own = OwnRemovals.Of(delta, cumulative);

		Assert.Equal([RenamedAway], LinesFor(own, Trajectory));
		Assert.DoesNotContain(own.Files.SelectMany(f => f.Lines), l => l.Contains("similarity index"));
		Assert.DoesNotContain(own.Files.SelectMany(f => f.Lines), l => l.StartsWith("a/"));
	}

	[Fact]
	public void A_repeated_removal_is_reported_once_and_counted_once()
	{
		var delta = Parse(File(Trajectory, $"-\t{RenamedAway}\n-\t{RenamedAway}\n-\tvar other = 1 + 2;\n"));
		var cumulative = Parse(File(Trajectory, $"+\t{RenamedTo}\n"));

		var own = OwnRemovals.Of(delta, cumulative);

		Assert.Equal([RenamedAway, "var other = 1 + 2;"], LinesFor(own, Trajectory));
		Assert.Equal(2, own.LineCount);
	}

	[Fact]
	public void Files_are_reported_in_the_order_the_delta_lists_them()
	{
		var delta = Parse(
			File("src/Z.cs", "-\tvar zed = 1;\n"),
			File("src/A.cs", "-\tvar ay = 2;\n"));
		var cumulative = Parse(
			File("src/Z.cs", "+\tvar zed = 1;\n"),
			File("src/A.cs", "+\tvar ay = 2;\n"));

		var own = OwnRemovals.Of(delta, cumulative);

		Assert.Equal(["src/Z.cs", "src/A.cs"], own.Files.Select(f => f.Path));
	}

	[Fact]
	public void The_same_text_is_judged_per_file_when_the_repo_wide_count_allows_it()
	{
		// One text, two files, opposite answers - which is why the per-file gate exists alongside
		// the repo-wide one. New.cs never had it at the base, so its removal is a surplus; Old.cs
		// had it and the pull request removes it, so that one is a genuine deletion of established
		// code and is left alone.
		const string same = "return Cache.TryGet(key, out value);";
		var delta = Parse(
			File("src/New.cs", $"-\t{same}\n"),
			File("src/Old.cs", $"-\t{same}\n"));
		var cumulative = Parse(
			File("src/New.cs", "+\tvar x = 1;\n"),
			File("src/Old.cs", $"-\t{same}\n"));

		var own = OwnRemovals.Of(delta, cumulative);

		Assert.Equal([same], LinesFor(own, "src/New.cs"));
		Assert.Empty(LinesFor(own, "src/Old.cs"));
	}

	[Fact]
	public void Windows_line_endings_do_not_change_the_answer()
	{
		var delta = Parse(File(Trajectory, $"-\t{RenamedAway}\n").Replace("\n", "\r\n"));
		var cumulative = Parse(File(Trajectory, $"+\t{RenamedTo}\n").Replace("\n", "\r\n"));

		Assert.Equal([RenamedAway], LinesFor(OwnRemovals.Of(delta, cumulative), Trajectory));
	}

	[Fact]
	public void Known_with_nothing_found_is_distinguishable_from_could_not_tell()
	{
		Assert.True(OwnRemovals.None.IsKnown);
		Assert.False(OwnRemovals.None.HasAny);
		Assert.False(OwnRemovals.Unknown.IsKnown);
		Assert.False(OwnRemovals.Unknown.HasAny);
		Assert.Equal(0, OwnRemovals.Unknown.LineCount);
	}

	// ---- The two preconditions, pinned as executable statements of what the SHELLS must guarantee.
	// Both tests assert a WRONG answer on purpose. The core cannot detect either violation from the
	// diffs alone - that is the point, and it is why the guarantee lives in the shell.

	[Fact]
	public void A_baseline_from_a_newer_head_manufactures_a_claim_which_is_why_shells_anchor_it()
	{
		// #181: base has L; the reviewed head removed it; a push landing before the baseline fetch
		// put it back. A cumulative diff ending at that NEWER head shows no change for L, so the
		// count(head) terms no longer cancel and the leftover count(head') - count(base) reads as
		// this branch's own surplus. The reviewer would be told an established line is not on the
		// base branch. Nothing in the two diffs reveals the mismatch, so the shells resolve the
		// baseline as base...headSha against the run's immutable head SHA.
		const string established = "public static string Slug(string name) => name.Trim();";
		var delta = Parse(File("src/A.cs", $"-\t{established}\n"));
		var atTheReviewedHead = Parse(File("src/A.cs", $"-\t{established}\n"));
		var atANewerHead = Parse(File("src/A.cs", "+\tvar unrelated = 1;\n"));

		Assert.False(OwnRemovals.Of(delta, atTheReviewedHead).HasAny);
		Assert.Equal([established], LinesFor(OwnRemovals.Of(delta, atANewerHead), "src/A.cs"));
	}

	[Fact]
	public void An_incomplete_delta_manufactures_a_claim_which_is_why_the_arithmetic_reads_the_raw_one()
	{
		// #181: the same hazard on the other input. An earlier turn moved an established method from
		// A.cs to B.cs; this turn moves it on into a file the filter drops. Judged on the FILTERED
		// delta the cancelling addition is missing and the removal reads as a surplus.
		const string established = "public static string Slug(string name) => name.Trim();";
		var whole = Parse(
			File("src/B.cs", $"-\t{established}\n"),
			File("src/Gen.g.cs", $"+\t{established}\n"));
		var filtered = Parse(File("src/B.cs", $"-\t{established}\n"));
		var cumulative = Parse(
			File("src/A.cs", $"-\t{established}\n"),
			File("src/Gen.g.cs", $"+\t{established}\n"));

		Assert.False(OwnRemovals.Of(whole, cumulative).HasAny);
		Assert.Equal([established], LinesFor(OwnRemovals.Of(filtered, cumulative), "src/B.cs"));
	}

	// ---- OnlyIn: narrowing the ANSWER, which is the safe half of the completeness precondition.

	[Fact]
	public void Narrowing_keeps_only_files_the_model_was_shown()
	{
		var delta = Parse(
			File("src/Shown.cs", "-\tvar shown = Real();\n"),
			File("src/Hidden.cs", "-\tvar hidden = Real();\n"));
		var cumulative = Parse(
			File("src/Shown.cs", "+\tvar shown = Real();\n"),
			File("src/Hidden.cs", "+\tvar hidden = Real();\n"));
		var shown = Parse(File("src/Shown.cs", "-\tvar shown = Real();\n"));

		var narrowed = OwnRemovals.Of(delta, cumulative).OnlyIn(shown);

		Assert.True(narrowed.IsKnown);
		Assert.Equal(["src/Shown.cs"], narrowed.Files.Select(f => f.Path));
	}

	[Fact]
	public void Narrowing_never_turns_a_known_answer_into_cannot_tell()
	{
		// Narrowing says nothing about whether a baseline existed, so IsKnown survives even when
		// every file is dropped - and Unknown stays Unknown rather than being promoted.
		var delta = Parse(File("src/Shown.cs", "-\tvar shown = Real();\n"));
		var cumulative = Parse(File("src/Shown.cs", "+\tvar shown = Real();\n"));

		var narrowed = OwnRemovals.Of(delta, cumulative).OnlyIn(Diff.Empty);

		Assert.True(narrowed.IsKnown);
		Assert.False(narrowed.HasAny);
		Assert.False(OwnRemovals.Unknown.OnlyIn(delta).IsKnown);
		Assert.False(OwnRemovals.Of(delta, cumulative).OnlyIn(null).HasAny);
	}

	// ---- Renames. The panel raised "pure rename diffs can manufacture an own-removal claim" on
	// #181. They cannot, and these pin why in every shape the two diffs can disagree about - rename
	// DETECTION is a property of each comparison, not of the tree, and the delta and the cumulative
	// diff are computed over different ranges, so they can and do disagree.
	//
	// The reason is the repo-wide gate. Summed over every file, the delta's (removals - additions)
	// is count(lastReviewed) - count(head) for the WHOLE TREE and the cumulative's
	// (additions - removals) is count(head) - count(base) for the whole tree, so the repo-wide sum
	// is path-blind. A rename moves text between paths without changing any whole-tree count, so it
	// contributes exactly zero to that sum - whichever way either diff chose to render it.

	private static string Renamed(string from, string to, string? hunk = null) =>
		$"diff --git a/{from} b/{to}\n"
		+ $"similarity index {(hunk is null ? "100" : "92")}%\n"
		+ $"rename from {from}\nrename to {to}\n"
		+ (hunk is null ? string.Empty : $"--- a/{from}\n+++ b/{to}\n@@ -1,3 +1,2 @@\n{hunk}");

	private const string Established = "public static string Slug(string name) => name.Trim();";

	[Fact]
	public void A_pure_rename_carries_no_content_and_claims_nothing()
	{
		// 100% similarity emits no hunks at all, so there is not even a removal to consider.
		var delta = Parse(Renamed("src/Old.cs", "src/New.cs"));
		var cumulative = Parse(Renamed("src/Old.cs", "src/New.cs"));

		Assert.False(OwnRemovals.Of(delta, cumulative).HasAny);
	}

	[Fact]
	public void A_rename_that_also_deletes_an_established_line_claims_nothing()
	{
		var delta = Parse(Renamed("src/Old.cs", "src/New.cs", $" \tkeep\n-\t{Established}\n"));
		var cumulative = Parse(Renamed("src/Old.cs", "src/New.cs", $" \tkeep\n-\t{Established}\n"));

		Assert.False(OwnRemovals.Of(delta, cumulative).HasAny);
	}

	[Fact]
	public void A_rename_the_two_diffs_disagree_about_still_claims_nothing()
	{
		// The sharp case: one comparison detects the rename and the other renders it as a delete
		// plus an add, so the base-side evidence lands under a path the other diff never names.
		// Per-file this DOES look like a surplus; repo-wide it cancels, and the gates are a
		// conjunction. Both directions of the disagreement, plus neither detecting it.
		var detected = Parse(Renamed("src/Old.cs", "src/New.cs", $" \tkeep\n-\t{Established}\n"));
		var asDeleteAdd = Parse(
			File("src/Old.cs", $"-\tkeep\n-\t{Established}\n"),
			File("src/New.cs", "+\tkeep\n"));

		Assert.False(OwnRemovals.Of(detected, asDeleteAdd).HasAny);
		Assert.False(OwnRemovals.Of(asDeleteAdd, detected).HasAny);
		Assert.False(OwnRemovals.Of(asDeleteAdd, asDeleteAdd).HasAny);
	}

	[Fact]
	public void A_rename_of_a_file_this_pull_request_itself_created_is_still_attributed_to_it()
	{
		// The other side of the same coin, and the #175 shape when the rework crosses files: an
		// earlier turn CREATED the file, a later turn renames it away. The rename does not change
		// the whole-tree count, but that count was already above the base's - the base has no copy
		// at all - so the surplus survives and the claim is true. A rename cancels a claim only
		// when the text it moves was established to begin with.
		var delta = Parse(
			File("src/First.cs", "-\tpublic static int Probe() => 1;\n"),
			File("src/Second.cs", "+\tpublic static int Probe() => 1;\n"));
		var cumulative = Parse(File("src/Second.cs", "+\tpublic static int Probe() => 1;\n"));

		Assert.Equal(
			["public static int Probe() => 1;"],
			LinesFor(OwnRemovals.Of(delta, cumulative), "src/First.cs"));
	}
}
