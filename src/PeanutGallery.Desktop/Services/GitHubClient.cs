using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Desktop.Model;

namespace PeanutGallery.Desktop.Services;

/// <summary>
/// GitHub REST shell for the desktop (in-box HttpClient, bearer token, no Octokit). Reads are
/// used continuously to build the workspace snapshot; the writes here (posting review comments)
/// run only from an explicit user action — the "Post" button after a one-shot review preview —
/// per ADR-0002 (the GUI reads to detect, and writes only through an explicit action).
/// </summary>
public sealed class GitHubClient : IDisposable
{
    private readonly HttpClient _http;

    public GitHubClient(string token, string apiBaseUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("peanut-gallery-desktop");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    // ---- reads (snapshot) -----------------------------------------------------------

    public async Task<IReadOnlyList<PrRaw>> ListOpenPullRequestsAsync(
        string owner, string repo, CancellationToken ct = default)
    {
        var prs = new List<PrRaw>();
        for (var page = 1; ; page++)
        {
            using var resp = await _http.GetAsync(
                $"repos/{owner}/{repo}/pulls?state=open&sort=updated&direction=desc&per_page=100&page={page}", ct);
            await EnsureOk(resp, $"list PRs for {owner}/{repo}", ct);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                prs.Add(new PrRaw(
                    el.GetProperty("number").GetInt32(),
                    Str(el, "title"),
                    el.TryGetProperty("user", out var u) ? Str(u, "login") : string.Empty,
                    el.TryGetProperty("head", out var h) ? Str(h, "ref") : string.Empty,
                    ParseDate(Str(el, "updated_at")),
                    el.TryGetProperty("head", out var h2) ? Str(h2, "sha") : string.Empty,
                    el.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True));
            }

            if (doc.RootElement.GetArrayLength() < 100) break;
        }

        return prs;
    }

    public async Task<IReadOnlyList<ExistingComment>> ListIssueCommentsAsync(
        string owner, string repo, int number, CancellationToken ct = default)
    {
        var comments = new List<ExistingComment>();
        for (var page = 1; ; page++)
        {
            using var resp = await _http.GetAsync(
                $"repos/{owner}/{repo}/issues/{number}/comments?per_page=100&page={page}", ct);
            await EnsureOk(resp, "list PR comments", ct);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var id = el.GetProperty("id").GetInt64();
                var body = Str(el, "body");
                var author = el.TryGetProperty("user", out var user) ? Str(user, "login") : string.Empty;
                var isBot = el.TryGetProperty("user", out var u2)
                    && u2.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String
                    && ty.GetString() == "Bot";
                // Where an untrusted comment enters. Recorded here so everything downstream reads
                // the flag rather than re-deriving trust; an unreadable association is refused.
                var association = el.TryGetProperty("author_association", out var aa)
                    && aa.ValueKind == JsonValueKind.String
                    ? aa.GetString()
                    : null;
                comments.Add(new ExistingComment(
                    id, body, author, isBot, CommentTrust.IsTrustedAuthor(isBot, association)));
            }

            if (doc.RootElement.GetArrayLength() < 100) break;
        }

        return comments;
    }

    /// <summary>
    /// What anchors a review run: the PR's current head commit SHA, and the branch it merges into.
    ///
    /// <para>Both, from one request, because they are read together. The base ref is what lets the
    /// #178 baseline be resolved as <c>base...headSha</c> instead of from the PR's moving head — see
    /// the CLI's <c>ResolveBaselineAsync</c> for why an unanchored baseline can manufacture a claim.</para>
    /// </summary>
    public async Task<(string HeadSha, string BaseRef)> GetPullAnchorAsync(
        string owner, string repo, int number, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"repos/{owner}/{repo}/pulls/{number}", ct);
        await EnsureOk(resp, "fetch PR", ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        return (
            root.TryGetProperty("head", out var h) ? Str(h, "sha") : string.Empty,
            root.TryGetProperty("base", out var b) ? Str(b, "ref") : string.Empty);
    }

    /// <summary>Raw text of a file at <paramref name="gitRef"/> (default branch when null), or null
    /// if it does not exist there (404). Pass the PR's head SHA to read a file as the reviewed
    /// commit sees it — the committed config is deliberately read from the default branch (the
    /// caller omits <paramref name="gitRef"/>), but review-time context (conventions, whole-file
    /// context) must match the code actually under review.</summary>
    public async Task<string?> GetFileTextAsync(
        string owner, string repo, string path, string? gitRef = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ContentsUrl(owner, repo, path, gitRef));
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/vnd.github.raw");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureOk(resp, $"fetch {path}", ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Raw bytes of a file at <paramref name="gitRef"/>, or null if it does not exist
    /// there (404). The byte-accurate counterpart to <see cref="GetFileTextAsync"/>: a caller that
    /// needs to size-cap or binary-sniff a file must do it on the wire bytes, not on a string
    /// already lossily UTF-8-decoded by <see cref="HttpContent.ReadAsStringAsync(CancellationToken)"/>.</summary>
    public async Task<byte[]?> GetFileBytesAsync(
        string owner, string repo, string path, string? gitRef = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ContentsUrl(owner, repo, path, gitRef));
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/vnd.github.raw");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureOk(resp, $"fetch {path}", ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    // Every path segment is escaped independently (not the path as a whole, which would also
    // escape the '/' separators): a path is diff-derived and therefore attacker-controlled on any
    // PR (including a fork's), and the Contents API endpoint otherwise receives it unescaped.
    private static string ContentsUrl(string owner, string repo, string path, string? gitRef)
    {
        var escapedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var url = $"repos/{owner}/{repo}/contents/{escapedPath}";
        return gitRef is null ? url : $"{url}?ref={Uri.EscapeDataString(gitRef)}";
    }

    /// <summary>Unified diff of the whole PR (first-turn review).</summary>
    public Task<string> GetPullRequestDiffAsync(string owner, string repo, int number, CancellationToken ct = default) =>
        GetDiffAsync($"repos/{owner}/{repo}/pulls/{number}", "fetch PR diff", ct);

    /// <summary>
    /// Unified diff between two commit-ishes (base...head) — the delta since last review. Both refs
    /// are escaped for the same reason ContentsUrl escapes its path: one of them is a SHA read back
    /// out of a PR comment, and interpolated raw it can name a different endpoint rather than a
    /// different commit.
    /// </summary>
    public Task<string> GetCompareDiffAsync(string owner, string repo, string baseRef, string headRef, CancellationToken ct = default) =>
        GetDiffAsync(
            $"repos/{owner}/{repo}/compare/{Uri.EscapeDataString(baseRef)}...{Uri.EscapeDataString(headRef)}",
            "compare commits",
            ct);

    // ---- writes (explicit "Post" action) --------------------------------------------

    public async Task CreateIssueCommentAsync(string owner, string repo, int number, string body, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"repos/{owner}/{repo}/issues/{number}/comments", JsonBody(body), ct);
        await EnsureOk(resp, "create comment", ct);
    }

    public async Task UpdateIssueCommentAsync(string owner, string repo, long commentId, string body, CancellationToken ct = default)
    {
        using var resp = await _http.PatchAsync($"repos/{owner}/{repo}/issues/comments/{commentId}", JsonBody(body), ct);
        await EnsureOk(resp, "update comment", ct);
    }

    // ---- helpers --------------------------------------------------------------------

    private async Task<string> GetDiffAsync(string path, string what, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/vnd.github.diff");
        using var resp = await _http.SendAsync(req, ct);
        await EnsureOk(resp, what, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static StringContent JsonBody(string body)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["body"] = body });
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static DateTimeOffset ParseDate(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : DateTimeOffset.MinValue;

    private static async Task EnsureOk(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var detail = (await resp.Content.ReadAsStringAsync(ct)).Trim();
        throw new GitHubApiException((int)resp.StatusCode, $"GitHub API {(int)resp.StatusCode} on {what}: {detail}");
    }

    public void Dispose() => _http.Dispose();
}

public sealed class GitHubApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
