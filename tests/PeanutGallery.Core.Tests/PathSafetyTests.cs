using System.IO;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// The paths this guards come from a diff, which is attacker-controlled on any PR. The headline
/// case is the sibling directory that merely shares a name prefix with the root.
/// </summary>
public class PathSafetyTests
{
	private static string Root => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pg-root"));

	private static string Under(params string[] parts) =>
		Path.GetFullPath(Path.Combine(new[] { Root }.Concat2(parts)));

	[Fact]
	public void A_file_under_the_root_is_inside()
	{
		Assert.True(PathSafety.IsInsideRoot(Root, Under("src", "a.cs")));
	}

	[Fact]
	public void The_root_itself_is_inside()
	{
		Assert.True(PathSafety.IsInsideRoot(Root, Root));
	}

	[Fact]
	public void A_sibling_directory_sharing_a_name_prefix_is_outside()
	{
		// The regression this class exists for: "pg-root-evil" starts with "pg-root", so a plain
		// StartsWith check would have let it through.
		var sibling = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pg-root-evil", "secret.txt"));

		Assert.StartsWith(Root, sibling, System.StringComparison.Ordinal); // the trap is real
		Assert.False(PathSafety.IsInsideRoot(Root, sibling));              // and closed
	}

	[Fact]
	public void A_traversal_out_of_the_root_is_outside()
	{
		var escaped = Path.GetFullPath(Path.Combine(Root, "..", "elsewhere", "secret.txt"));

		Assert.False(PathSafety.IsInsideRoot(Root, escaped));
	}

	[Fact]
	public void A_traversal_that_lands_back_inside_is_allowed()
	{
		var normalised = Path.GetFullPath(Path.Combine(Root, "src", "..", "a.cs"));

		Assert.True(PathSafety.IsInsideRoot(Root, normalised));
	}

	[Fact]
	public void A_file_whose_name_merely_begins_with_dots_is_inside()
	{
		// Guard against over-correcting into a prefix check on "..".
		Assert.True(PathSafety.IsInsideRoot(Root, Under("..config")));
		Assert.True(PathSafety.IsInsideRoot(Root, Under(".gitignore")));
	}

	[Fact]
	public void The_parent_directory_is_outside()
	{
		Assert.False(PathSafety.IsInsideRoot(Root, Path.GetFullPath(Path.Combine(Root, ".."))));
	}

	[Fact]
	public void Missing_or_blank_inputs_are_refused_not_thrown()
	{
		Assert.False(PathSafety.IsInsideRoot(null, Under("a.cs")));
		Assert.False(PathSafety.IsInsideRoot(Root, null));
		Assert.False(PathSafety.IsInsideRoot("", Under("a.cs")));
		Assert.False(PathSafety.IsInsideRoot(Root, "   "));
	}
}

internal static class ArrayJoin
{
	/// <summary>Tiny helper so the test paths read as a single Combine call.</summary>
	public static string[] Concat2(this string[] first, string[] second)
	{
		var all = new string[first.Length + second.Length];
		first.CopyTo(all, 0);
		second.CopyTo(all, first.Length);
		return all;
	}
}
