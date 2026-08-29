using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

public class DiffTests
{
	[Fact]
	public void Empty_input_parses_to_Empty()
	{
		Assert.True(Diff.Parse(null).IsEmpty);
		Assert.True(Diff.Parse("").IsEmpty);
		Assert.True(Diff.Parse("   \n  ").IsEmpty);
		Assert.Empty(Diff.Parse(null).Files);
	}

	[Fact]
	public void Parses_git_header_and_prefers_the_b_path()
	{
		const string raw =
			"diff --git a/src/Foo.cs b/src/Foo.cs\n"
			+ "--- a/src/Foo.cs\n"
			+ "+++ b/src/Foo.cs\n"
			+ "@@ -1,3 +1,4 @@\n"
			+ " unchanged\n"
			+ "+added one\n"
			+ "+added two\n"
			+ "-removed one\n";

		var diff = Diff.Parse(raw);

		var file = Assert.Single(diff.Files);
		Assert.Equal("src/Foo.cs", file.Path);
		Assert.Equal(2, file.AddedLines);
		Assert.Equal(1, file.RemovedLines);
		Assert.Equal(raw, diff.Raw); // raw preserved verbatim for the model
	}

	[Fact]
	public void File_markers_and_hunk_headers_are_not_counted_as_content()
	{
		// The +++/--- markers and @@ header must not inflate the +/- counts.
		const string raw =
			"diff --git a/x b/x\n--- a/x\n+++ b/x\n@@ -0,0 +1,1 @@\n+only real addition\n";

		var file = Assert.Single(Diff.Parse(raw).Files);
		Assert.Equal(1, file.AddedLines);
		Assert.Equal(0, file.RemovedLines);
	}

	[Fact]
	public void Handles_multiple_files_and_crlf()
	{
		const string raw =
			"diff --git a/a.cs b/a.cs\r\n+one\r\n"
			+ "diff --git a/b.cs b/b.cs\r\n+two\r\n-three\r\n";

		var diff = Diff.Parse(raw);

		Assert.Equal(2, diff.Files.Count);
		Assert.Equal("a.cs", diff.Files[0].Path);
		Assert.Equal(1, diff.Files[0].AddedLines);
		Assert.Equal("b.cs", diff.Files[1].Path);
		Assert.Equal(1, diff.Files[1].AddedLines);
		Assert.Equal(1, diff.Files[1].RemovedLines);
	}

	// ---- hunk locations (what ContextBudget windows a large file around) ----

	[Fact]
	public void Hunk_ranges_read_the_new_side_of_each_header()
	{
		// Context text is the file at the reviewed head, so the post-image line numbers are the
		// ones that address it. The old-side numbers here are deliberately different.
		const string raw =
			"diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n"
			+ "@@ -1,3 +10,4 @@\n+one\n"
			+ "@@ -200,2 +300,7 @@\n+two\n";

		var ranges = Assert.Single(Diff.Parse(raw).Files).HunkRanges();

		Assert.Equal([new LineRange(10, 13), new LineRange(300, 306)], ranges);
	}

	[Fact]
	public void A_single_line_hunk_may_omit_its_count()
	{
		var file = Assert.Single(Diff.Parse("diff --git a/x b/x\n@@ -5 +7 @@\n+one\n").Files);

		Assert.Equal([new LineRange(7, 7)], file.HunkRanges());
	}

	[Fact]
	public void A_pure_deletion_hunk_points_at_where_the_code_used_to_be()
	{
		// "+42,0" adds no post-image lines at all; line 42 is where the deleted code sat, which is
		// where a window around it belongs.
		var file = Assert.Single(Diff.Parse("diff --git a/x b/x\n@@ -42,3 +42,0 @@\n-gone\n").Files);

		Assert.Equal([new LineRange(42, 42)], file.HunkRanges());
	}

	[Fact]
	public void A_content_line_quoting_a_hunk_header_is_not_mistaken_for_one()
	{
		// The quoted "@@" carries a diff prefix, so it never starts the line.
		var file = Assert.Single(Diff.Parse(
			"diff --git a/x b/x\n@@ -1,2 +1,2 @@\n+var s = \"@@ -9,9 +9999,9 @@\";\n").Files);

		Assert.Equal([new LineRange(1, 2)], file.HunkRanges());
	}

	[Fact]
	public void An_unreadable_header_is_skipped_rather_than_thrown_on()
	{
		// Parsing is total: a garbled header costs its own hunk's location, not the review.
		var file = Assert.Single(Diff.Parse(
			"diff --git a/x b/x\n@@ nonsense @@\n@@ -1,1 +8,2 @@\n+one\n").Files);

		Assert.Equal([new LineRange(8, 9)], file.HunkRanges());
	}

	[Fact]
	public void A_file_with_no_hunk_headers_reports_no_ranges()
	{
		var file = Assert.Single(Diff.Parse("diff --git a/x b/x\nBinary files differ\n").Files);

		Assert.Empty(file.HunkRanges());
	}
}
