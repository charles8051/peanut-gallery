using System;
using System.IO;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// The vector a string-only containment check cannot see: a symlink committed inside the repo and
/// pointing outside it. Every path in sight looks contained; the bytes come from elsewhere.
/// </summary>
public sealed class FileSystemSafetyTests : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "pg-fss-" + Guid.NewGuid().ToString("N"));

    private readonly string _root;
    private readonly string _outside;

    public FileSystemSafetyTests()
    {
        _root = Path.Combine(_tmp, "repo");
        _outside = Path.Combine(_tmp, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "inside.txt"), "ok");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "sensitive");
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
    /// available the test degrades to skipped rather than failing - the Linux CI container is
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
    public void A_real_file_inside_the_root_is_contained()
    {
        Assert.True(FileSystemSafety.ResolvesInsideRoot(_root, Path.Combine(_root, "inside.txt")));
    }

    [Fact]
    public void The_root_itself_is_contained()
    {
        Assert.True(FileSystemSafety.ResolvesInsideRoot(_root, _root));
    }

    [Fact]
    public void A_sibling_directory_sharing_a_name_prefix_is_not_contained()
    {
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);

        Assert.False(FileSystemSafety.ResolvesInsideRoot(_root, Path.Combine(sibling, "secret.txt")));
    }

    [Fact]
    public void A_traversal_out_of_the_root_is_not_contained()
    {
        Assert.False(FileSystemSafety.ResolvesInsideRoot(
            _root, Path.Combine(_root, "..", "outside", "secret.txt")));
    }

    [Fact]
    public void A_file_symlink_pointing_outside_the_root_is_not_contained()
    {
        // #91: the leaf is a link. Its own path string is inside the root, so a string-only
        // check passes it and File.ReadAllText then reads the target.
        var link = Path.Combine(_root, "link.txt");
        if (!TryLink(() => File.CreateSymbolicLink(link, Path.Combine(_outside, "secret.txt"))))
        {
            return;
        }

        Assert.True(PeanutGallery.Core.PathSafety.IsInsideRoot(_root, link)); // the trap is real
        Assert.False(FileSystemSafety.ResolvesInsideRoot(_root, link));       // and closed
    }

    [Fact]
    public void A_directory_symlink_on_the_path_is_not_contained()
    {
        // The subtler half: the LEAF is not a link at all, a parent directory is.
        var linkDir = Path.Combine(_root, "sub");
        if (!TryLink(() => Directory.CreateSymbolicLink(linkDir, _outside)))
        {
            return;
        }

        Assert.False(FileSystemSafety.ResolvesInsideRoot(_root, Path.Combine(linkDir, "secret.txt")));
    }

    [Fact]
    public void A_symlink_pointing_back_inside_the_root_is_contained()
    {
        // Not a blanket ban on links - only on ones that leave.
        var link = Path.Combine(_root, "self.txt");
        if (!TryLink(() => File.CreateSymbolicLink(link, Path.Combine(_root, "inside.txt"))))
        {
            return;
        }

        Assert.True(FileSystemSafety.ResolvesInsideRoot(_root, link));
    }

    [Fact]
    public void A_BROKEN_symlink_pointing_outside_the_root_is_not_contained()
    {
        // File.Exists follows the link, so a broken one reports as "not there" and an
        // existence-based check walks straight past it. Harmless the instant you read it - the
        // read fails too - but a lie from a containment guard, and only true until somebody
        // creates the target.
        var link = Path.Combine(_root, "broken.txt");
        var missingTarget = Path.Combine(_outside, "does-not-exist.txt");
        if (!TryLink(() => File.CreateSymbolicLink(link, missingTarget)))
        {
            return;
        }

        // Deliberately does not assert what File.Exists reports here: it is platform-dependent,
        // and the whole point of the fix is that the guard reads the reparse data via LinkTarget
        // rather than depending on an existence check at all.
        Assert.False(FileSystemSafety.ResolvesInsideRoot(_root, link));
    }

    [Fact]
    public void A_relative_symlink_is_resolved_against_the_links_own_directory()
    {
        // A relative target resolved against the wrong base silently checks the wrong path.
        var sub = Path.Combine(_root, "nested");
        Directory.CreateDirectory(sub);
        var link = Path.Combine(sub, "up.txt");
        if (!TryLink(() => File.CreateSymbolicLink(link, Path.Combine("..", "..", "outside", "secret.txt"))))
        {
            return;
        }

        Assert.False(FileSystemSafety.ResolvesInsideRoot(_root, link));
    }

    [Fact]
    public void A_symlink_cycle_terminates_and_is_refused()
    {
        var a = Path.Combine(_root, "a.txt");
        var b = Path.Combine(_root, "b.txt");
        if (!TryLink(() => File.CreateSymbolicLink(a, b)) || !TryLink(() => File.CreateSymbolicLink(b, a)))
        {
            return;
        }

        Assert.False(FileSystemSafety.ResolvesInsideRoot(_root, a)); // bounded, not hung
    }

    [Fact]
    public void Blank_input_is_refused_not_thrown()
    {
        Assert.False(FileSystemSafety.ResolvesInsideRoot(null, "x"));
        Assert.False(FileSystemSafety.ResolvesInsideRoot(_root, null));
        Assert.False(FileSystemSafety.ResolvesInsideRoot("", ""));
        Assert.False(FileSystemSafety.ResolvesInsideRoot(_root, "   "));
    }

    [Fact]
    public void A_path_that_does_not_exist_yet_is_still_judged_on_containment()
    {
        Assert.True(FileSystemSafety.ResolvesInsideRoot(_root, Path.Combine(_root, "nope.txt")));
        Assert.False(FileSystemSafety.ResolvesInsideRoot(_root, Path.Combine(_outside, "nope.txt")));
    }
}
