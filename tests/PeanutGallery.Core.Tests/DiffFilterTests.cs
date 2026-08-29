using System.Linq;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

public class DiffFilterTests
{
	private const string MixedDiff =
		"diff --git a/src/Real.cs b/src/Real.cs\n--- a/src/Real.cs\n+++ b/src/Real.cs\n@@ -1 +1 @@\n-a\n+b\n" +
		"diff --git a/package-lock.json b/package-lock.json\n@@ -1 +1 @@\n-x\n+y\n" +
		"diff --git a/Foo/obj/T.g.cs b/Foo/obj/T.g.cs\n@@ -0,0 +1 @@\n+gen\n" +
		"diff --git a/logo.png b/logo.png\nBinary files a/logo.png and b/logo.png differ\n" +
		"diff --git a/Old.cs b/New.cs\nsimilarity index 100%\nrename from Old.cs\nrename to New.cs\n";

	[Fact]
	public void Parse_flags_binary_and_rename_only_files()
	{
		var byPath = Diff.Parse(MixedDiff).Files.ToDictionary(f => f.Path);

		Assert.True(byPath["logo.png"].IsBinary);
		Assert.True(byPath["New.cs"].IsRenameOnly);
		Assert.False(byPath["src/Real.cs"].IsBinary);
		Assert.False(byPath["src/Real.cs"].IsRenameOnly);
		Assert.Contains("src/Real.cs", byPath["src/Real.cs"].Segment); // segment captured
	}

	[Fact]
	public void Drops_low_signal_files_and_keeps_substantive_ones()
	{
		var result = DiffFilter.Apply(Diff.Parse(MixedDiff), DiffFilterPolicy.Default);

		var keptFile = Assert.Single(result.Diff.Files);
		Assert.Equal("src/Real.cs", keptFile.Path);

		var reasons = result.Omitted.ToDictionary(o => o.Path, o => o.Reason);
		Assert.Equal("ignored", reasons["package-lock.json"]);
		Assert.Equal("ignored", reasons["Foo/obj/T.g.cs"]);
		Assert.Equal("binary", reasons["logo.png"]);
		Assert.Equal("rename-only", reasons["New.cs"]);

		// Rebuilt raw carries only the kept file.
		Assert.Contains("src/Real.cs", result.Diff.Raw);
		Assert.DoesNotContain("package-lock.json", result.Diff.Raw);
		Assert.DoesNotContain("logo.png", result.Diff.Raw);
	}

	[Fact]
	public void Size_cap_omits_the_largest_first_and_keeps_the_rest()
	{
		var raw =
			"diff --git a/small.cs b/small.cs\n@@ -0,0 +1 @@\n+x\n" +
			"diff --git a/big.cs b/big.cs\n@@ -0,0 +1 @@\n+" + new string('y', 400) + "\n";

		var result = DiffFilter.Apply(Diff.Parse(raw), new DiffFilterPolicy([], MaxBytes: 100));

		var kept = Assert.Single(result.Diff.Files);
		Assert.Equal("small.cs", kept.Path);
		var dropped = Assert.Single(result.Omitted);
		Assert.Equal("big.cs", dropped.Path);
		Assert.Equal("size budget", dropped.Reason);
	}

	[Fact]
	public void Under_budget_with_no_low_signal_files_keeps_everything()
	{
		var raw = "diff --git a/a.cs b/a.cs\n@@ -1 +1 @@\n-1\n+2\n";
		var result = DiffFilter.Apply(Diff.Parse(raw), DiffFilterPolicy.Default);

		Assert.Single(result.Diff.Files);
		Assert.Empty(result.Omitted);
	}

	[Theory]
	[InlineData("*.lock", "Cargo.lock", true)]
	[InlineData("*.lock", "src/deps.lock", true)]   // no slash in pattern -> basename match
	[InlineData("**/obj/**", "A/obj/x.cs", true)]
	[InlineData("**/obj/**", "src/A.cs", false)]
	[InlineData("*.min.js", "app.js", false)]
	public void Glob_matches_gitignore_style(string pattern, string path, bool expected)
	{
		Assert.Equal(expected, Glob.IsMatch(pattern, path));
	}
}
