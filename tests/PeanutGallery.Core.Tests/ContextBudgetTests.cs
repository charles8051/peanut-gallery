using System.Linq;
using System.Text;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// Diff-tier personas see only git's three lines around a hunk, which is how they come to
/// report a guard missing when it sits just off-hunk. File context is the fix; the budget keeps
/// it from eating the prompt, and omissions are disclosed rather than hidden.
///
/// <para>The budget used to be whole-file-or-nothing, so the one file a PR churned hardest -
/// usually its largest - could exceed it and be sent as nothing at all (#164). The windowing
/// tests below are the guard on that: over budget now costs a file its quiet lines, not its
/// presence.</para>
/// </summary>
public class ContextBudgetTests
{
	private static readonly RepoTarget Repo = new("demo", "/repos/demo");

	private static FileContext File(string path, int size) => new(path, new string('x', size));

	/// <summary>A file of numbered lines, so an assertion can name the exact line it expects to
	/// see - or expects to have been elided.</summary>
	private static FileContext Numbered(string path, int lines)
	{
		var sb = new StringBuilder();
		for (var i = 1; i <= lines; i++)
		{
			sb.Append("line ").Append(i).Append('\n');
		}

		return new FileContext(path, sb.ToString());
	}

	/// <summary>A diff touching <paramref name="path"/> at the given (start, count) new-side hunks -
	/// the hunk headers are the whole of what <see cref="ContextBudget"/> reads from a diff.</summary>
	private static Diff DiffTouching(string path, params (int Start, int Count)[] hunks)
	{
		var sb = new StringBuilder($"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n");
		foreach (var (start, count) in hunks)
		{
			sb.Append($"@@ -{start},{count} +{start},{count} @@\n+changed\n");
		}

		return Diff.Parse(sb.ToString());
	}

	[Fact]
	public void Everything_that_fits_is_kept()
	{
		var sel = ContextBudget.Fit([File("a.cs", 100), File("b.cs", 100)], 1000);

		Assert.Equal(2, sel.Kept.Count);
		Assert.Empty(sel.Omitted);
	}

	[Fact]
	public void Smallest_files_are_packed_first_to_maximise_coverage()
	{
		var sel = ContextBudget.Fit([File("huge.cs", 900), File("a.cs", 50), File("b.cs", 50)], 200);

		Assert.Equal(["a.cs", "b.cs"], sel.Kept.Select(f => f.Path));
		Assert.Equal(["huge.cs"], sel.Omitted);
	}

	[Fact]
	public void Omitted_files_are_reported_not_dropped_silently()
	{
		// No diff, so nothing is known about where this file changed and there is no window to
		// fall back to. It still has to be named.
		var sel = ContextBudget.Fit([File("big.cs", 5000)], 100);

		Assert.Empty(sel.Kept);
		Assert.Equal(["big.cs"], sel.Omitted);
	}

	[Fact]
	public void A_zero_budget_keeps_nothing_and_discloses_everything()
	{
		var sel = ContextBudget.Fit([File("a.cs", 10)], 0);

		Assert.Empty(sel.Kept);
		Assert.Equal(["a.cs"], sel.Omitted);
	}

	[Fact]
	public void No_candidates_is_an_empty_selection()
	{
		var sel = ContextBudget.Fit([], 1000);

		Assert.Empty(sel.Kept);
		Assert.Empty(sel.Omitted);
	}

	[Fact]
	public void Kept_files_are_presented_in_path_order()
	{
		// Size order is a packing detail; a reader wants a stable, alphabetical listing.
		var sel = ContextBudget.Fit([File("z.cs", 10), File("a.cs", 50), File("m.cs", 30)], 1000);

		Assert.Equal(["a.cs", "m.cs", "z.cs"], sel.Kept.Select(f => f.Path));
	}

	[Fact]
	public void Selection_is_deterministic_for_equal_sized_files()
	{
		var a = ContextBudget.Fit([File("b.cs", 50), File("a.cs", 50), File("c.cs", 50)], 100);
		var b = ContextBudget.Fit([File("c.cs", 50), File("b.cs", 50), File("a.cs", 50)], 100);

		Assert.Equal(a.Kept.Select(f => f.Path), b.Kept.Select(f => f.Path));
		Assert.Equal(a.Omitted, b.Omitted);
	}

	// ---- windowing ----

	[Fact]
	public void A_file_larger_than_the_whole_budget_contributes_windows_instead_of_nothing()
	{
		// Observed: the file one PR churned hardest was 85KB against a 64KB budget, so it was
		// sent as nothing at all on 15 runs - and the panel duly reported a gate missing that sat
		// 23 lines off-hunk. Over budget must cost a file its quiet lines, not its presence.
		var big = Numbered("big.cs", 4000);
		var sel = ContextBudget.Fit([big], budgetBytes: 2000, DiffTouching("big.cs", (2000, 4)));

		var kept = Assert.Single(sel.Kept);
		Assert.Empty(sel.Omitted);
		Assert.True(big.Text.Length > 2000, "the fixture has to be bigger than the budget to prove anything");
		Assert.True(kept.Text.Length <= 2000);

		Assert.Contains("line 2000\n", kept.Text);
		Assert.Contains("line 1950\n", kept.Text); // padded above the hunk
		Assert.Contains("line 2053\n", kept.Text); // and below it
		Assert.DoesNotContain("line 1949\n", kept.Text);
		Assert.DoesNotContain("line 1\n", kept.Text);
	}

	[Fact]
	public void A_file_that_still_fits_is_sent_whole_and_byte_for_byte()
	{
		// Windowing is how a file survives the budget, not a saving to chase: the lines outside a
		// hunk are exactly where the guard a reviewer is about to call missing tends to live.
		var file = Numbered("small.cs", 40);
		var sel = ContextBudget.Fit([file], 100_000, DiffTouching("small.cs", (20, 2)));

		Assert.Equal(file.Text, Assert.Single(sel.Kept).Text);
	}

	[Fact]
	public void Overlapping_hunks_merge_into_one_window()
	{
		var windows = ContextBudget.Windows([new LineRange(100, 104), new LineRange(120, 124)], 400, padLines: 20);

		var only = Assert.Single(windows);
		Assert.Equal(new LineRange(80, 144), only);
	}

	[Fact]
	public void Windows_a_single_line_apart_merge_rather_than_leaving_a_gap()
	{
		// A one-line gap costs more as an elision marker than as the line itself.
		var windows = ContextBudget.Windows([new LineRange(10, 10), new LineRange(31, 31)], 200, padLines: 10);

		Assert.Equal([new LineRange(1, 41)], windows);
	}

	[Fact]
	public void Distant_hunks_stay_separate_windows()
	{
		var windows = ContextBudget.Windows([new LineRange(10, 10), new LineRange(300, 310)], 400, padLines: 20);

		Assert.Equal([new LineRange(1, 30), new LineRange(280, 330)], windows);
	}

	[Fact]
	public void A_hunk_at_the_file_start_or_end_clamps_to_the_file()
	{
		var windows = ContextBudget.Windows([new LineRange(1, 2), new LineRange(199, 200)], 200, padLines: 50);

		Assert.Equal([new LineRange(1, 52), new LineRange(149, 200)], windows);
	}

	[Fact]
	public void A_hunk_at_the_file_start_renders_without_a_leading_elision()
	{
		var sel = ContextBudget.Fit(
			[Numbered("a.cs", 400)], budgetBytes: 700, DiffTouching("a.cs", (1, 3)), padLines: 20);

		var text = Assert.Single(sel.Kept).Text;
		Assert.StartsWith("@@ lines 1-23 of 400 @@\n", text);
		Assert.Contains("... 377 lines elided (lines 24-400) ...", text);
	}

	[Fact]
	public void A_hunk_at_the_file_end_renders_without_a_trailing_elision()
	{
		var sel = ContextBudget.Fit(
			[Numbered("a.cs", 400)], budgetBytes: 700, DiffTouching("a.cs", (399, 2)), padLines: 20);

		var text = Assert.Single(sel.Kept).Text;
		Assert.Contains("... 378 lines elided (lines 1-378) ...\n@@ lines 379-400 of 400 @@\n", text);
		Assert.EndsWith("line 400\n", text);
	}

	[Fact]
	public void Hunks_with_no_readable_location_fall_back_to_the_whole_file()
	{
		// A diff that mentions the file but carries no parseable hunk header says nothing about
		// where it changed. The honest answer is all of it, which is what callers got before
		// windowing existed.
		var file = Numbered("a.cs", 20);
		var sel = ContextBudget.Fit([file], 100_000, Diff.Parse("diff --git a/a.cs b/a.cs\n+one\n"));

		Assert.Equal(file.Text, Assert.Single(sel.Kept).Text);
	}

	[Fact]
	public void Every_elided_range_is_named_in_the_text()
	{
		// The model must be able to see that two windows are not contiguous - counting line
		// numbers straight through a gap is how a finding cites the wrong line.
		var sel = ContextBudget.Fit(
			[Numbered("a.cs", 300)], budgetBytes: 1500, DiffTouching("a.cs", (50, 2), (250, 2)), padLines: 10);

		var text = Assert.Single(sel.Kept).Text;
		Assert.Contains("... 39 lines elided (lines 1-39) ...", text);
		Assert.Contains("@@ lines 40-61 of 300 @@", text);
		Assert.Contains("... 178 lines elided (lines 62-239) ...", text);
		Assert.Contains("@@ lines 240-261 of 300 @@", text);
		Assert.Contains("... 39 lines elided (lines 262-300) ...", text);
	}

	[Fact]
	public void A_window_that_does_not_fit_is_dropped_but_still_disclosed()
	{
		// Room for one window only. The second is not silently missing: the gap it leaves is
		// spelled out by the same marker any other elision gets.
		var sel = ContextBudget.Fit(
			[Numbered("a.cs", 300)], budgetBytes: 300, DiffTouching("a.cs", (50, 2), (250, 2)), padLines: 10);

		var text = Assert.Single(sel.Kept).Text;
		Assert.Contains("@@ lines 40-61 of 300 @@", text);
		Assert.DoesNotContain("@@ lines 240-261 of 300 @@", text);
		Assert.Contains("... 239 lines elided (lines 62-300) ...", text);
		Assert.True(text.Length <= 300);
	}

	[Fact]
	public void A_file_with_no_window_that_fits_is_omitted_and_named()
	{
		var sel = ContextBudget.Fit(
			[Numbered("a.cs", 300)], budgetBytes: 20, DiffTouching("a.cs", (50, 2)), padLines: 10);

		Assert.Empty(sel.Kept);
		Assert.Equal(["a.cs"], sel.Omitted);
	}

	[Fact]
	public void A_window_too_big_for_the_room_is_skipped_rather_than_ending_the_file()
	{
		// The first hunk lands in a long-line region and cannot fit; the second is ordinary code.
		// Taking a prefix of the windows would drop the whole file for the sake of its first hunk -
		// which is this type's original defect, one level down, on exactly the large heavily-changed
		// files windowing exists to rescue.
		var lines = new string[200];
		for (var i = 0; i < 200; i++)
		{
			lines[i] = i < 100 ? new string('w', 200) : "line " + (i + 1);
		}

		var file = new FileContext("a.cs", string.Join("\n", lines) + "\n");
		var sel = ContextBudget.Fit(
			[file], budgetBytes: 900, DiffTouching("a.cs", (30, 2), (150, 2)), padLines: 5);

		var kept = Assert.Single(sel.Kept);
		Assert.Empty(sel.Omitted);
		Assert.DoesNotContain("@@ lines 25-36 of 200 @@", kept.Text); // did not fit
		Assert.Contains("... 144 lines elided (lines 1-144) ...", kept.Text); // and says so
		Assert.Contains("@@ lines 145-156 of 200 @@", kept.Text);
		Assert.Contains("line 150\n", kept.Text);
	}

	[Fact]
	public void A_file_is_kept_whenever_any_one_of_its_windows_fits_the_room_left()
	{
		// The property behind "a file is never dropped for a reason other than not fitting": a
		// window is measured against what has been accepted so far, and while nothing has been
		// accepted that is the window alone. So an omission means every window, measured alone, was
		// too big - never that an earlier one used up the file's chance. Here the last window is the
		// only one that fits, and it is one third of the file's full window set.
		var lines = new string[400];
		for (var i = 0; i < 400; i++)
		{
			lines[i] = i < 300 ? new string('w', 300) : "line " + (i + 1);
		}

		var file = new FileContext("a.cs", string.Join("\n", lines) + "\n");
		var diff = DiffTouching("a.cs", (60, 2), (160, 2), (380, 2));

		// The first two windows are ~3.6KB of 300-character lines each; the third is 12 short ones.
		var sel = ContextBudget.Fit([file], budgetBytes: 1000, diff, padLines: 5);

		var kept = Assert.Single(sel.Kept);
		Assert.Contains("@@ lines 375-386 of 400 @@", kept.Text);
		Assert.DoesNotContain("@@ lines 55-", kept.Text);
		Assert.DoesNotContain("@@ lines 155-", kept.Text);
	}

	[Fact]
	public void A_fragmented_file_never_renders_past_the_room_it_was_given()
	{
		// Window selection sums what the renderer will emit rather than rendering each candidate to
		// measure it. That arithmetic has to agree with the renderer byte for byte or a selection
		// overruns the budget it was checked against - so check the rendered result, at several
		// budgets, on a file with many windows and multi-byte characters to encode.
		var lines = new string[2000];
		for (var i = 0; i < 2000; i++)
		{
			lines[i] = i % 7 == 0 ? $"// コメント {i + 1} — ここに説明" : $"    var value{i + 1} = Compute({i + 1});";
		}

		var file = new FileContext("big.cs", string.Join("\n", lines) + "\n");
		var hunks = new (int, int)[15];
		for (var i = 0; i < 15; i++)
		{
			hunks[i] = (30 + (i * 130), 2);
		}

		var diff = DiffTouching("big.cs", hunks);
		foreach (var budget in new[] { 500, 1_500, 4_000, 9_000, 20_000, 60_000 })
		{
			var sel = ContextBudget.Fit([file], budget, diff);
			foreach (var kept in sel.Kept)
			{
				Assert.True(
					Encoding.UTF8.GetByteCount(kept.Text) <= budget,
					$"budget {budget}: rendered {Encoding.UTF8.GetByteCount(kept.Text)} bytes");
			}
		}
	}

	[Fact]
	public void The_budget_counts_utf8_bytes_not_utf16_characters()
	{
		// Three UTF-8 bytes per character: 60 characters are 180 bytes, and string.Length would
		// have called this 60 and fitted it inside a 100-byte budget with room to spare.
		var sel = ContextBudget.Fit([new FileContext("a.cs", new string('あ', 60))], 100);

		Assert.Empty(sel.Kept);
		Assert.Equal(["a.cs"], sel.Omitted);

		Assert.Single(ContextBudget.Fit([new FileContext("a.cs", new string('あ', 60))], 200).Kept);
	}

	[Fact]
	public void Spending_is_tracked_in_bytes_so_the_budget_is_not_overrun_across_files()
	{
		// 90 bytes + 40 bytes against a 120-byte budget: one of them fits. Counted as UTF-16 code
		// units it reads as 30 + 40 and both would be admitted, 10 bytes past the limit.
		var sel = ContextBudget.Fit([new FileContext("wide.cs", new string('あ', 30)), File("a.cs", 40)], 120);

		Assert.Equal(["a.cs"], sel.Kept.Select(f => f.Path));
		Assert.Equal(["wide.cs"], sel.Omitted);
	}

	[Fact]
	public void A_big_file_wins_back_the_budget_the_small_ones_left()
	{
		// Smallest-first still holds, and the big file now spends what is left rather than being
		// dropped: both of the files that panel kept filing against were the big ones.
		var sel = ContextBudget.Fit(
			[Numbered("big.cs", 4000), File("a.cs", 50), File("b.cs", 50)],
			budgetBytes: 3000,
			DiffTouching("big.cs", (2000, 4)));

		Assert.Equal(["a.cs", "b.cs", "big.cs"], sel.Kept.Select(f => f.Path));
		Assert.Empty(sel.Omitted);
		Assert.Contains("line 2000\n", sel.Kept.Single(f => f.Path == "big.cs").Text);
	}

	[Fact]
	public void Windowing_is_deterministic_down_to_the_byte()
	{
		// The personas of a run share one prompt prefix and the provider's cache only fires on a
		// byte-identical one, so the same inputs must render the same characters every time -
		// including through a differently ordered candidate list.
		var files = new[] { Numbered("a.cs", 500), Numbered("b.cs", 500), File("c.cs", 40) };
		var diff = Diff.Parse(
			DiffTouching("a.cs", (100, 3), (400, 3)).Raw + DiffTouching("b.cs", (250, 3)).Raw);

		var first = ContextBudget.Fit([files[0], files[1], files[2]], 2500, diff);
		var second = ContextBudget.Fit([files[2], files[1], files[0]], 2500, diff);

		Assert.Equal(
			first.Kept.Select(f => f.Path + "\u0000" + f.Text),
			second.Kept.Select(f => f.Path + "\u0000" + f.Text));
		Assert.Equal(first.Omitted, second.Omitted);
	}

	// ---- prompt rendering ----

	[Fact]
	public void Context_is_rendered_and_framed_as_surrounding_code()
	{
		var sel = ContextBudget.Fit([new FileContext("a.cs", "class A { }")], 1000);

		var user = Msg.User(SessionPlanner.Advance(
			TestData.BugHunter, Repo, ReviewSession.Initial, Diff.Empty, "sha", context: sel));

		Assert.Contains("--- a.cs ---", user);
		Assert.Contains("class A { }", user);
		Assert.Contains("Review the diff above", user); // the diff stays the subject
	}

	[Fact]
	public void The_prompt_warns_that_an_excerpt_is_not_contiguous()
	{
		// The old framing promised "the full current text of the changed files". Told that, a model
		// reads straight through an elision marker and counts line numbers through the gap.
		var sel = ContextBudget.Fit(
			[Numbered("a.cs", 300)], budgetBytes: 700, DiffTouching("a.cs", (150, 2)), padLines: 10);

		var user = Msg.User(SessionPlanner.Advance(
			TestData.BugHunter, Repo, ReviewSession.Initial, Diff.Empty, "sha", context: sel));

		Assert.DoesNotContain("the full current text", user);
		Assert.Contains("is NOT contiguous", user);
		Assert.Contains("@@ lines 140-161 of 300 @@", user);
		Assert.Contains("... 139 lines elided (lines 1-139) ...", user);
	}

	[Fact]
	public void Oversized_context_is_disclosed_in_the_prompt()
	{
		var sel = new ContextSelection([], ["huge.cs"]);

		var user = Msg.User(SessionPlanner.Advance(
			TestData.BugHunter, Repo, ReviewSession.Initial, Diff.Empty, "sha", context: sel));

		// Nothing was kept, so there is no context to show - but the file is still named. Suppressing
		// the block entirely dropped the omission list exactly where the reviewer had least to go on.
		Assert.DoesNotContain("--- huge.cs ---", user);
		Assert.Contains("No current file text could be included", user);
		Assert.Contains("huge.cs", user);
	}

	[Fact]
	public void An_empty_selection_still_leaves_the_prompt_untouched()
	{
		// Nothing offered and nothing omitted: there is no fact to disclose, so no block appears.
		var user = Msg.User(SessionPlanner.Advance(
			TestData.BugHunter, Repo, ReviewSession.Initial, Diff.Empty, "sha",
			context: new ContextSelection([], [])));

		Assert.DoesNotContain("No current file text could be included", user);
		Assert.DoesNotContain("For context, here is the current text", user);
	}

	[Fact]
	public void Partial_context_discloses_what_was_left_out()
	{
		var sel = new ContextSelection([new FileContext("a.cs", "ok")], ["huge.cs"]);

		var user = Msg.User(SessionPlanner.Advance(
			TestData.BugHunter, Repo, ReviewSession.Initial, Diff.Empty, "sha", context: sel));

		Assert.Contains("--- a.cs ---", user);
		Assert.Contains("too large to include: huge.cs", user);
	}

	[Fact]
	public void No_context_leaves_the_prompt_untouched()
	{
		var user = Msg.User(SessionPlanner.Advance(
			TestData.BugHunter, Repo, ReviewSession.Initial, Diff.Empty, "sha"));

		Assert.DoesNotContain("For context, here is the current text", user);
	}
}
