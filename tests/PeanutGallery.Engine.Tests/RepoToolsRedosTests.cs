using System;
using System.Diagnostics;
using System.IO;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// The grep pattern is MODEL-SUPPLIED, and the model reads a diff - untrusted on any PR. A
/// prompt-injected change could hand the tool a catastrophically backtracking regex and hang the
/// run until the outer per-call timeout fires.
/// </summary>
public sealed class RepoToolsRedosTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pg-redos-").FullName;
    private readonly RepoTools _tools;

    public RepoToolsRedosTests()
    {
        // The classic ReDoS shape: a long run of the repeated character with one that cannot
        // match at the end, forcing a backtracking engine through exponential possibilities.
        File.WriteAllText(Path.Combine(_dir, "bait.txt"), new string('a', 4000) + "!");
        File.WriteAllText(Path.Combine(_dir, "plain.txt"), "hello hello\nworld\n");
        _tools = new RepoTools(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp dir is not worth failing a run over.
        }
    }

    [Fact]
    public void A_catastrophic_pattern_returns_promptly_instead_of_hanging()
    {
        // NonBacktracking makes this LINEAR rather than merely bounded, so it should finish far
        // inside the per-match budget - not sit there until a timeout rescues it.
        var sw = Stopwatch.StartNew();
        var result = _tools.Grep("^(a+)+$");
        sw.Stop();

        Assert.NotNull(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"grep took {sw.Elapsed} - it should not backtrack");
    }

    [Fact]
    public void A_nested_quantifier_alternation_also_returns_promptly()
    {
        var sw = Stopwatch.StartNew();
        _tools.Grep("^(a|aa)+$");
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"grep took {sw.Elapsed}");
    }

    [Fact]
    public void A_pattern_needing_the_backtracking_engine_still_works()
    {
        // Backreferences are not expressible in NonBacktracking, so this exercises the fallback.
        // Rejecting such patterns outright would quietly break legitimate searches.
        var result = _tools.Grep(@"(hello) \1");

        Assert.Contains("plain.txt", result);
    }

    [Fact]
    public void A_catastrophic_pattern_on_the_backtracking_engine_is_abandoned_not_hung()
    {
        // A lookahead forces the fallback engine, and the nested quantifier then backtracks -
        // the one class where a bound is the best available guarantee.
        var sw = Stopwatch.StartNew();
        var result = _tools.Grep("^(?=a)(a+)+$");
        sw.Stop();

        // Either it was abandoned on the timeout, or it finished - both are fine. What is NOT
        // fine is running long enough to matter, which is what this pins.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"grep took {sw.Elapsed}");
        if (result.StartsWith("error:", StringComparison.Ordinal))
        {
            Assert.Contains("too long", result);
        }
    }

    [Fact]
    public void An_invalid_pattern_is_reported_as_an_error_not_thrown()
    {
        Assert.StartsWith("error: invalid regex", _tools.Grep("(unclosed"));
    }

    [Fact]
    public void Ordinary_searches_are_unaffected()
    {
        Assert.Contains("plain.txt", _tools.Grep("world"));
        Assert.Equal("(no matches)", _tools.Grep("nothing-matches-this-anywhere"));
    }
}
