using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;

namespace PeanutGallery.Desktop.Services;

/// <summary>
/// Remote counterparts to the CLI's local-checkout readers (<c>Commands.ReadConventions</c> /
/// <c>Commands.ReadFileContextAsync</c>): the desktop app reviews a PR it has no working copy
/// of, so whole-file context (#82) and repo conventions (#87) are read from GitHub at the PR's
/// head commit instead of the filesystem. Best-effort, like the CLI originals — a file that is
/// missing, oversized, unreadable, or binary is simply not offered; a review must never fail over
/// an enhancement.
/// </summary>
public static class RemoteRepoContext
{
    // Mirrors PeanutGallery.Cli.Commands.ConventionsCandidates. Kept local rather than shared
    // across shells: each shell owns its own IO (ADR-0001), and this is four short strings.
    private static readonly string[] ConventionsCandidates =
    [
        ".github/copilot-instructions.md",
        ".github/peanut-gallery-instructions.md",
        "CLAUDE.md",
        "AGENTS.md",
    ];

    private const int MaxConventionsBytes = 64 * 1024;

    /// <summary>
    /// Ceiling on what is worth fetching at all, mirroring <c>Commands.MaxContextFileBytes</c>. It
    /// is deliberately far above the prompt budget: <see cref="ContextBudget"/> windows a large file
    /// down to the regions around its hunks, so a cap set at the budget would throw away the file
    /// before the core got the chance - which is how an 85KB class went unseen on 15 runs (#164).
    /// </summary>
    private const int MaxContextFileBytes = 512 * 1024;

    /// <summary>The repo's review conventions at <paramref name="headSha"/>, most specific
    /// candidate first, or null if none apply (missing, empty, oversized, or unreadable).</summary>
    public static async Task<RepoConventions?> ReadConventionsAsync(
        GitHubClient gh, string owner, string repo, string headSha, CancellationToken ct)
    {
        foreach (var candidate in ConventionsCandidates)
        {
            ct.ThrowIfCancellationRequested();
            byte[]? bytes;
            try
            {
                bytes = await gh.GetFileBytesAsync(owner, repo, candidate, headSha, ct);
            }
            catch (GitHubApiException)
            {
                continue; // unreadable: try the next candidate rather than sink the review
            }

            var text = bytes is null ? null : AcceptAsText(bytes, MaxConventionsBytes);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            return new RepoConventions(candidate, text);
        }

        return null;
    }

    /// <summary>
    /// The current text of <paramref name="paths"/> at <paramref name="headSha"/>, for the
    /// whole-file context block. <paramref name="cache"/> is shared across every diff-tier
    /// persona's call within one review run — <see cref="PeanutGallery.Engine.ReviewRunner"/>
    /// invokes this once per persona, and personas fan out concurrently, so without it the same
    /// PR's changed files would be fetched once per persona instead of once per review.
    ///
    /// <para>Only a completed fetch is cached — including a genuine 404 (a stable fact: the file
    /// is not at this head). A <see cref="GitHubApiException"/> (rate-limit, 5xx, a network reset)
    /// is a transient failure of THIS call, not a fact about the path, so it is never written to
    /// the shared cache; the next persona to ask for the same path gets its own attempt rather than
    /// inheriting a stranger's outage.</para>
    /// </summary>
    public static async Task<IReadOnlyList<FileContext>> ReadFileContextAsync(
        GitHubClient gh, string owner, string repo, string headSha, IReadOnlyList<string> paths,
        ConcurrentDictionary<string, byte[]?> cache, CancellationToken ct)
    {
        var found = new List<FileContext>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (!cache.TryGetValue(path, out var bytes))
            {
                try
                {
                    bytes = await gh.GetFileBytesAsync(owner, repo, path, headSha, ct);
                    cache[path] = bytes;
                }
                catch (GitHubApiException)
                {
                    continue; // this attempt failed; leave no cache entry for the next persona to retry
                }
            }

            var text = bytes is null ? null : AcceptAsText(bytes, MaxContextFileBytes);
            if (text is not null)
            {
                found.Add(new FileContext(path, text));
            }
        }

        return found;
    }

    /// <summary>
    /// The file's text, or null if it should not be offered as context: empty, over
    /// <paramref name="maxBytes"/>, or binary. Pure — bytes in, a verdict out — and checked BEFORE
    /// decoding, not after: <see cref="Encoding.UTF8"/>'s decoder replaces an invalid byte sequence
    /// with U+FFFD rather than throwing, so a NUL-free binary (a PNG header, most compressed
    /// formats) decodes to a string with no embedded NUL and would sail past a post-decode
    /// <c>Contains('\0')</c> sniff. Checking the raw bytes catches both: an explicit NUL byte, or
    /// bytes that are not valid UTF-8 at all (surfaced as a replacement character post-decode).
    ///
    /// <para>Deliberately kept here rather than promoted to <c>PeanutGallery.Core</c>: it is total
    /// and side-effect-free like a core fold, but the byte-vs-decoded-text hazard it guards against
    /// is specific to fetching content over HTTP (this class's whole reason to exist) — the CLI's
    /// local-checkout equivalent never faces it, since <c>FileInfo.Length</c> is already an exact
    /// byte count with no decode step in between. A shell-local pure helper used only by its one
    /// caller does not need a cross-shell home in Core (same reasoning as
    /// <see cref="ReviewConfigResolver"/>'s pure methods staying in this project).</para>
    /// </summary>
    public static string? AcceptAsText(byte[] bytes, int maxBytes)
    {
        if (bytes.Length == 0 || bytes.Length > maxBytes || System.Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(bytes);
        return text.Contains('�') ? null : text; // the decoder's invalid-sequence marker
    }
}
