using System.Collections.Generic;
using System.Linq;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// A repo does not have to keep its rules at the root. Reading only the root meant a repo whose
/// sole conventions file sat one directory down was reviewed with no house rules at all, which is
/// indistinguishable from a repo that has none.
/// </summary>
public class ConventionsDiscoveryTests
{
	private static IReadOnlyList<string> Chain(params string[] changed) =>
		ConventionsDiscovery.ScopeChains(changed).SelectMany(c => c).ToList();

	[Fact]
	public void A_changed_file_is_looked_up_from_its_own_directory_outwards()
	{
		var chains = ConventionsDiscovery.ScopeChains(["src/app/Feature/Thing.cs"]);

		var chain = Assert.Single(chains);
		Assert.Equal(
			[
				"src/app/Feature/CLAUDE.md", "src/app/Feature/AGENTS.md",
				"src/app/CLAUDE.md", "src/app/AGENTS.md",
				"src/CLAUDE.md", "src/AGENTS.md",
			],
			chain);
	}

	[Fact]
	public void The_repo_root_is_never_in_a_chain()
	{
		// RootCandidates covers it, with a wider list, and it applies whether or not a changed
		// file sits in a subdirectory. Probing it twice would also double-render it.
		Assert.DoesNotContain("CLAUDE.md", Chain("tests/Decode/ReplayTests.cs"));
		Assert.DoesNotContain("AGENTS.md", Chain("tests/Decode/ReplayTests.cs"));
	}

	[Fact]
	public void A_root_level_file_contributes_no_chain()
	{
		Assert.Empty(ConventionsDiscovery.ScopeChains(["README.md", "Directory.Build.props"]));
	}

	[Fact]
	public void Files_sharing_a_directory_are_searched_once()
	{
		var chains = ConventionsDiscovery.ScopeChains(["tests/A.cs", "tests/B.cs", "tests/C.cs"]);

		Assert.Single(chains);
	}

	[Fact]
	public void Each_distinct_directory_gets_its_own_chain()
	{
		var chains = ConventionsDiscovery.ScopeChains(["tests/A.cs", "src/B.cs"]);

		Assert.Equal(2, chains.Count);
	}

	[Fact]
	public void Windows_separators_and_dot_slash_prefixes_are_normalised()
	{
		var expected = Chain("tests/Decode/X.cs");

		Assert.Equal(expected, Chain(@"tests\Decode\X.cs"));
		Assert.Equal(expected, Chain("./tests/Decode/X.cs"));
	}

	[Fact]
	public void A_path_that_climbs_out_of_the_repo_is_dropped()
	{
		// Diff paths are attacker-controlled on any PR: a chain must never name a path that
		// resolves above the root, whatever the shell does afterwards.
		Assert.Empty(ConventionsDiscovery.ScopeChains(["../evil/x.cs"]));
		Assert.Empty(ConventionsDiscovery.ScopeChains(["src/../../evil/x.cs"]));
		Assert.Empty(ConventionsDiscovery.ScopeChains(["/etc/passwd"]));
		Assert.Empty(ConventionsDiscovery.ScopeChains([@"C:\windows\x.cs"]));
	}

	[Fact]
	public void Nothing_to_go_on_yields_nothing()
	{
		// A failed baseline resolve means no changed-file list; the root pass still runs.
		Assert.Empty(ConventionsDiscovery.ScopeChains(null));
		Assert.Empty(ConventionsDiscovery.ScopeChains([]));
		Assert.Empty(ConventionsDiscovery.ScopeChains(["", "   "]));
	}

	[Fact]
	public void A_wide_change_is_capped_deterministically()
	{
		var changed = Enumerable.Range(0, 40).Select(i => $"dir{i:00}/x.cs").ToList();

		var chains = ConventionsDiscovery.ScopeChains(changed, maxScopes: 3);

		// Ordinal-sorted, so the same change always probes the same directories in the same
		// order - a cap that varied with diff order would make a review irreproducible.
		Assert.Equal(3, chains.Count);
		Assert.Equal("dir00/CLAUDE.md", chains[0][0]);
		Assert.Equal("dir01/CLAUDE.md", chains[1][0]);
		Assert.Equal("dir02/CLAUDE.md", chains[2][0]);
	}

	[Fact]
	public void A_nonsense_cap_searches_nothing_rather_than_everything()
	{
		Assert.Empty(ConventionsDiscovery.ScopeChains(["src/x.cs"], maxScopes: 0));
		Assert.Empty(ConventionsDiscovery.ScopeChains(["src/x.cs"], maxScopes: -1));
	}

	[Fact]
	public void A_scoped_file_reports_the_subtree_it_governs()
	{
		Assert.Equal("tests", ConventionsDiscovery.ScopeOf("tests/CLAUDE.md"));
		Assert.Equal("src/app", ConventionsDiscovery.ScopeOf("src/app/AGENTS.md"));
		Assert.Equal(string.Empty, ConventionsDiscovery.ScopeOf("CLAUDE.md"));
		Assert.Equal(string.Empty, ConventionsDiscovery.ScopeOf(null));
	}
}
