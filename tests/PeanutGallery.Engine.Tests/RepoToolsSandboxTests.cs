using System;
using System.IO;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// The agent tier hands a model read_file / grep / glob against the checkout. The paths it asks
/// for come from reading a diff, which is attacker-controlled on any PR, and the checkout itself
/// is that PR's code - so a symlink can be part of the change. A string-only containment check
/// cannot see one: every path in sight is inside the root, and the bytes come from elsewhere.
/// </summary>
public sealed class RepoToolsSandboxTests : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "pg-sandbox-" + Guid.NewGuid().ToString("N"));

    private readonly string _root;
    private readonly string _outside;
    private readonly RepoTools _tools;

    public RepoToolsSandboxTests()
    {
        _root = Path.Combine(_tmp, "repo");
        _outside = Path.Combine(_tmp, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "inside.txt"), "public");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "SENSITIVE-abcdef");
        _tools = new RepoTools(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp dir is not worth failing a test run over.
        }
    }

    /// <summary>
    /// Creating symlinks needs privilege on Windows (admin or developer mode). Where it is not
    /// available the test degrades to skipped rather than failing — the Linux CI container is
    /// where these actually get exercised.
    /// </summary>
    private static bool TryLink(Action create)
    {
        try
        {
            create();
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public void Reading_a_normal_file_still_works() =>
        Assert.Equal("public", _tools.ReadFile("inside.txt"));

    [Fact]
    public void A_plain_traversal_is_still_refused() =>
        Assert.StartsWith("error: path", _tools.ReadFile("../outside/secret.txt"));

    [Fact]
    public void A_file_symlink_pointing_outside_the_root_cannot_be_read()
    {
        var link = Path.Combine(_root, "leak.txt");
        if (!TryLink(() => File.CreateSymbolicLink(link, Path.Combine(_outside, "secret.txt"))))
        {
            return;
        }

        Assert.Null(_tools.Resolve("leak.txt"));
        Assert.StartsWith("error: path", _tools.ReadFile("leak.txt"));
    }

    /// <summary>
    /// The link is a DIRECTORY on the path, and the leaf is not a link at all — so checking only
    /// the final component would pass this.
    /// </summary>
    [Fact]
    public void A_directory_symlink_on_the_path_cannot_be_read_through()
    {
        var link = Path.Combine(_root, "vendor");
        if (!TryLink(() => Directory.CreateSymbolicLink(link, _outside)))
        {
            return;
        }

        Assert.Null(_tools.Resolve("vendor/secret.txt"));
        Assert.StartsWith("error: path", _tools.ReadFile("vendor/secret.txt"));
    }

    /// <summary>
    /// EnumerateFiles with AllDirectories walks THROUGH a symlinked directory, so grep and glob
    /// leak the same content without ever calling Resolve.
    /// </summary>
    [Fact]
    public void Grep_does_not_walk_through_a_directory_symlink()
    {
        var link = Path.Combine(_root, "vendor");
        if (!TryLink(() => Directory.CreateSymbolicLink(link, _outside)))
        {
            return;
        }

        Assert.DoesNotContain("SENSITIVE", _tools.Grep("SENSITIVE"), StringComparison.Ordinal);
    }

    [Fact]
    public void Glob_does_not_list_files_reached_through_a_symlink()
    {
        var link = Path.Combine(_root, "vendor");
        if (!TryLink(() => Directory.CreateSymbolicLink(link, _outside)))
        {
            return;
        }

        Assert.DoesNotContain("secret.txt", _tools.Glob("**/*.txt"), StringComparison.Ordinal);
        Assert.Contains("inside.txt", _tools.Glob("**/*.txt"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The point is that the walk does not GO there, not that its results are filtered afterwards.
    /// A link pointing back at the root is the cheapest way to tell the two apart: filtering
    /// returns the right files either way, while a walk that still recurses does not terminate.
    /// </summary>
    [Fact]
    public void The_walk_does_not_recurse_into_a_link_that_points_at_the_root()
    {
        var link = Path.Combine(_root, "loop");
        if (!TryLink(() => Directory.CreateSymbolicLink(link, _root)))
        {
            return;
        }

        Assert.Equal("inside.txt", _tools.Glob("**/*.txt"));
    }

    /// <summary>
    /// The two halves of the link policy, which are deliberately asymmetric.
    ///
    /// <para><c>read_file</c> was handed that exact path by the model, so a link resolving inside
    /// the root is read. The WALK does not follow links at all, even safe ones - the same default
    /// ripgrep has (<c>--follow</c> opts in) and the same thing git grep does by searching a link's
    /// blob rather than its target. Following them would report one line of code at two paths, and
    /// the link is not the path to send a reviewer to.</para>
    /// </summary>
    [Fact]
    public void A_link_inside_the_root_is_readable_by_path_but_is_not_walked_into()
    {
        var link = Path.Combine(_root, "alias.txt");
        if (!TryLink(() => File.CreateSymbolicLink(link, Path.Combine(_root, "inside.txt"))))
        {
            return;
        }

        Assert.Equal("public", _tools.ReadFile("alias.txt"));       // named directly: resolved and read
        Assert.Equal("inside.txt", _tools.Glob("**/*.txt"));        // enumerated: the target, once

        // And nothing goes unsearched: the link's target is inside the root, so grep reaches the
        // content on its own. What the walk drops is the second path to it, not the bytes.
        Assert.Contains("inside.txt:1: public", _tools.Grep("public"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Hidden and system files were never skipped here: the <see cref="SearchOption"/> overload
    /// builds compatibility options with <c>AttributesToSkip = None</c>, not the property default
    /// of <c>Hidden | System</c>. Assigning ReparsePoint outright is what preserves that.
    /// </summary>
    [Fact]
    public void A_hidden_file_is_still_enumerated()
    {
        var hidden = Path.Combine(_root, ".hidden.txt");
        File.WriteAllText(hidden, "dotfile");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hidden, FileAttributes.Hidden);
        }

        Assert.Contains(".hidden.txt", _tools.Glob("**/*.txt"), StringComparison.Ordinal);
    }
}
