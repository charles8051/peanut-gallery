using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// Which conventions files win, decided once for both shells: the CLI reading a checkout and the
/// desktop app fetching from GitHub at the PR head must not disagree about whose rules apply to a
/// review. The probe stands in for the IO; everything asserted here is the fold.
/// </summary>
public class ConventionsReaderTests
{
    /// <summary>A fake repo: paths that exist, and a record of what was actually looked at.</summary>
    private sealed class Tree(params (string Path, string Text)[] files)
    {
        private readonly Dictionary<string, string> _files =
            files.ToDictionary(f => f.Path, f => f.Text, StringComparer.Ordinal);

        public List<string> Probes { get; } = [];

        public Task<string?> ProbeAsync(string candidate, CancellationToken ct)
        {
            Probes.Add(candidate);
            return Task.FromResult(_files.TryGetValue(candidate, out var text) ? text : null);
        }
    }

    private static Task<RepoConventions?> CollectAsync(
        Tree tree, IEnumerable<string>? changed, int maxProbes = ConventionsReader.Unbounded) =>
        ConventionsReader.CollectAsync(tree.ProbeAsync, changed, maxProbes);

    [Fact]
    public async Task A_repo_whose_only_conventions_sit_in_a_subdirectory_is_no_longer_reviewed_bare()
    {
        // The shape this change exists for: nothing at the root, rules one directory down.
        var tree = new Tree(("tests/CLAUDE.md", "Prefer tests that are pure logic."));

        var conventions = await CollectAsync(tree, ["tests/Decode/ReplayTests.cs"]);

        Assert.NotNull(conventions);
        var file = Assert.Single(conventions.Files);
        Assert.Equal("tests/CLAUDE.md", file.Path);
        Assert.Equal("tests", file.Scope);
    }

    [Fact]
    public async Task The_root_file_applies_alongside_the_subtree_one()
    {
        var tree = new Tree(("CLAUDE.md", "Functional core."), ("tests/CLAUDE.md", "Pure logic."));

        var conventions = await CollectAsync(tree, ["tests/X.cs"]);

        Assert.NotNull(conventions);
        Assert.Equal(["CLAUDE.md", "tests/CLAUDE.md"], conventions.Files.Select(f => f.Path));
        Assert.Equal(string.Empty, conventions.Files[0].Scope);
    }

    [Fact]
    public async Task The_nearest_ancestor_wins_and_the_ones_above_it_are_not_added()
    {
        // Two levels of subtree rules. The near one supersedes the far one for this change;
        // sending both would hand the reviewer two answers to the same question.
        var tree = new Tree(
            ("src/CLAUDE.md", "Outer."),
            ("src/app/CLAUDE.md", "Inner."));

        var conventions = await CollectAsync(tree, ["src/app/Thing.cs"]);

        Assert.NotNull(conventions);
        Assert.Equal(["src/app/CLAUDE.md"], conventions.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task Two_directories_can_each_contribute_their_own_rules()
    {
        var tree = new Tree(("src/CLAUDE.md", "Outer."), ("tests/AGENTS.md", "Pure logic."));

        var conventions = await CollectAsync(tree, ["src/A.cs", "tests/B.cs"]);

        Assert.NotNull(conventions);
        Assert.Equal(["src/CLAUDE.md", "tests/AGENTS.md"], conventions.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task One_file_reached_from_two_directories_is_sent_once()
    {
        var tree = new Tree(("src/CLAUDE.md", "Outer."));

        var conventions = await CollectAsync(tree, ["src/a/A.cs", "src/b/B.cs"]);

        Assert.NotNull(conventions);
        Assert.Equal(["src/CLAUDE.md"], conventions.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task A_shared_ancestor_is_probed_once_however_many_chains_cross_it()
    {
        // Every probe is a GitHub call on the desktop path, and chains overlap heavily.
        var tree = new Tree();

        await CollectAsync(tree, ["src/a/A.cs", "src/b/B.cs", "src/c/C.cs"]);

        Assert.Equal(tree.Probes.Count, tree.Probes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task The_probe_budget_keeps_what_it_found_rather_than_failing()
    {
        var tree = new Tree(("CLAUDE.md", "Functional core."), ("deep/a/b/c/CLAUDE.md", "Inner."));

        // Three probes is exactly what the root pass spent to find CLAUDE.md, so the subtree
        // pass gets nothing - and the review runs on the repo-wide rules rather than failing.
        var conventions = await CollectAsync(tree, ["deep/a/b/c/X.cs"], maxProbes: 3);

        Assert.NotNull(conventions);
        Assert.Equal(["CLAUDE.md"], conventions.Files.Select(f => f.Path));
        Assert.Equal(3, tree.Probes.Count);
    }

    [Fact]
    public async Task The_most_specific_root_candidate_wins_and_stops_the_root_pass()
    {
        var tree = new Tree(
            (".github/copilot-instructions.md", "Written for a reviewer."),
            ("CLAUDE.md", "Written for an agent."));

        var conventions = await CollectAsync(tree, []);

        Assert.NotNull(conventions);
        Assert.Equal([".github/copilot-instructions.md"], conventions.Files.Select(f => f.Path));
        Assert.DoesNotContain("CLAUDE.md", tree.Probes);
    }

    [Fact]
    public async Task A_repo_with_no_conventions_anywhere_reviews_exactly_as_before()
    {
        Assert.Null(await CollectAsync(new Tree(), ["src/A.cs"]));
    }

    [Fact]
    public async Task An_empty_conventions_file_is_not_a_conventions_file()
    {
        var tree = new Tree(("CLAUDE.md", "   \n\n"), ("src/CLAUDE.md", "\t"));

        Assert.Null(await CollectAsync(tree, ["src/A.cs"]));
    }

    [Fact]
    public async Task No_changed_file_list_still_reads_the_root()
    {
        // A failed baseline resolve costs the subtree rules, never the repo-wide ones.
        var tree = new Tree(("CLAUDE.md", "Functional core."), ("src/CLAUDE.md", "Inner."));

        var conventions = await CollectAsync(tree, null);

        Assert.NotNull(conventions);
        Assert.Equal(["CLAUDE.md"], conventions.Files.Select(f => f.Path));
    }
}
