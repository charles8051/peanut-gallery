using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;

namespace PeanutGallery.Cli;

/// <summary>
/// One status check on a commit, as <c>await-review</c> needs to read it. <paramref name="Status"/>
/// is "queued" / "in_progress" / "completed"; <paramref name="Conclusion"/> is null until it is.
/// Both are carried because they answer different questions, and conflating them is the bug that
/// makes a naive waiter useless: a check that does not exist yet also has no conclusion.
/// </summary>
internal sealed record CheckRun(string Name, string Status, string? Conclusion);

/// <summary>The PR fields review-pr needs: refs, head repo (fork check), and opt-out signals.</summary>
internal sealed record PullRequestInfo(
	string HeadSha, string BaseRef, string HeadRepo,
	IReadOnlyList<string> Labels, string Title, string Body, bool IsDraft);

/// <summary>
/// Thin GitHub REST shell: fetch a PR's unified diff and create/update issue
/// comments. Just enough surface for `review-pr`; uses the in-box HttpClient (no
/// Octokit dependency) and a token from the environment. Works against github.com or
/// a GitHub Enterprise base via GITHUB_API_URL.
/// </summary>
internal sealed class GitHubClient : IDisposable
{
	private readonly HttpClient _http;

	public GitHubClient(string token, string apiBaseUrl)
	{
		_http = new HttpClient { BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/") };
		_http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
		_http.DefaultRequestHeaders.UserAgent.ParseAdd("peanut-gallery");
		_http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
		_http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
	}

	public async Task<string> GetPullRequestDiffAsync(string owner, string repo, int number, CancellationToken ct = default)
	{
		using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{owner}/{repo}/pulls/{number}");
		req.Headers.Accept.Clear();
		req.Headers.Accept.ParseAdd("application/vnd.github.diff");
		using var resp = await _http.SendAsync(req, ct);
		await EnsureOk(resp, "fetch PR diff", ct);
		return await resp.Content.ReadAsStringAsync(ct);
	}

	/// <summary>The PR's current head SHA and base branch ref — anchors the session's incremental diff.</summary>
	public async Task<PullRequestInfo> GetPullRequestAsync(string owner, string repo, int number, CancellationToken ct = default)
	{
		using var resp = await _http.GetAsync($"repos/{owner}/{repo}/pulls/{number}", ct);
		await EnsureOk(resp, "fetch PR", ct);
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
		var root = doc.RootElement;
		var headEl = root.GetProperty("head");
		var head = headEl.GetProperty("sha").GetString() ?? string.Empty;
		var baseRef = root.GetProperty("base").GetProperty("ref").GetString() ?? string.Empty;
		var headRepo = headEl.TryGetProperty("repo", out var hr) && hr.ValueKind == JsonValueKind.Object
			&& hr.TryGetProperty("full_name", out var fn) && fn.ValueKind == JsonValueKind.String
			? fn.GetString() ?? string.Empty
			: string.Empty;
		var title = root.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String ? ti.GetString() ?? string.Empty : string.Empty;
		var body = root.TryGetProperty("body", out var bo) && bo.ValueKind == JsonValueKind.String ? bo.GetString() ?? string.Empty : string.Empty;
		var isDraft = root.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True;

		var labels = new List<string>();
		if (root.TryGetProperty("labels", out var ls) && ls.ValueKind == JsonValueKind.Array)
		{
			foreach (var le in ls.EnumerateArray())
			{
				if (le.TryGetProperty("name", out var ln) && ln.ValueKind == JsonValueKind.String)
				{
					labels.Add(ln.GetString() ?? string.Empty);
				}
			}
		}

		return new PullRequestInfo(head, baseRef, headRepo, labels, title, body, isDraft);
	}

	/// <summary>
	/// The check runs GitHub currently has for a commit. An empty list is a real answer — right
	/// after a push the workflow has not registered its check yet — and is why <c>await-review</c>
	/// waits for the check to APPEAR before it waits for it to finish.
	/// </summary>
	public async Task<IReadOnlyList<CheckRun>> ListCheckRunsAsync(string owner, string repo, string sha, CancellationToken ct = default)
	{
		using var resp = await _http.GetAsync($"repos/{owner}/{repo}/commits/{sha}/check-runs?per_page=100", ct);
		await EnsureOk(resp, "list check runs", ct);
		using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

		var runs = new List<CheckRun>();
		if (doc.RootElement.ValueKind == JsonValueKind.Object
			&& doc.RootElement.TryGetProperty("check_runs", out var arr)
			&& arr.ValueKind == JsonValueKind.Array)
		{
			foreach (var el in arr.EnumerateArray())
			{
				runs.Add(new CheckRun(
					Text(el, "name"),
					Text(el, "status"),
					el.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String
						? c.GetString()
						: null));
			}
		}

		return runs;
	}

	private static string Text(JsonElement el, string name) =>
		el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
			? v.GetString() ?? string.Empty
			: string.Empty;

	/// <summary>
	/// Unified diff between two commit-ishes (base...head) — the delta since the last review.
	///
	/// <para>Both refs are escaped. One of them reaches this method as a SHA read back out of a PR
	/// comment, so it is only a SHA by convention: interpolated raw, a value carrying <c>../</c>,
	/// <c>?</c>, or <c>#</c> is not a different commit but a different endpoint, requested against
	/// api.github.com with this run's token attached and its body handed to a model. Escaping keeps
	/// the value a path segment whatever it contains.</para>
	/// </summary>
	public async Task<string> GetCompareDiffAsync(string owner, string repo, string baseRef, string headRef, CancellationToken ct = default)
	{
		var range = $"{Uri.EscapeDataString(baseRef)}...{Uri.EscapeDataString(headRef)}";
		using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{owner}/{repo}/compare/{range}");
		req.Headers.Accept.Clear();
		req.Headers.Accept.ParseAdd("application/vnd.github.diff");
		using var resp = await _http.SendAsync(req, ct);
		await EnsureOk(resp, "compare commits", ct);
		return await resp.Content.ReadAsStringAsync(ct);
	}

	/// <summary>
	/// PR numbers for the repo (most-recently-updated first), for scraping their metrics ledgers.
	/// <paramref name="state"/> is "open" / "closed" / "all". Stops early once a page's oldest PR is
	/// older than <paramref name="updatedAfter"/> (the API sorts by updated desc), so a bounded
	/// window does not page through years of closed PRs.
	/// </summary>
	public async Task<IReadOnlyList<int>> ListPullRequestNumbersAsync(
		string owner, string repo, string state, DateTimeOffset? updatedAfter, CancellationToken ct = default)
	{
		// Hard page ceiling so a bug or a pathological repo can never page unbounded. 100 pages x 100
		// per page = 10k PRs; past that the --since window (or an explicit smaller one) is the tool.
		const int MaxPages = 100;
		var numbers = new List<int>();
		for (var page = 1; page <= MaxPages; page++)
		{
			using var resp = await _http.GetAsync(
				$"repos/{owner}/{repo}/pulls?state={state}&sort=updated&direction=desc&per_page=100&page={page}", ct);
			await EnsureOk(resp, "list PRs", ct);

			using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
			if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
			{
				break;
			}

			var pastWindow = false;
			foreach (var el in doc.RootElement.EnumerateArray())
			{
				if (updatedAfter is not null
					&& el.TryGetProperty("updated_at", out var u) && u.ValueKind == JsonValueKind.String
					&& DateTimeOffset.TryParse(u.GetString(), out var updated) && updated < updatedAfter)
				{
					pastWindow = true;
					continue;
				}

				numbers.Add(el.GetProperty("number").GetInt32());
			}

			if (pastWindow || doc.RootElement.GetArrayLength() < 100)
			{
				break;
			}
		}

		return numbers;
	}

	public async Task<IReadOnlyList<ExistingComment>> ListIssueCommentsAsync(string owner, string repo, int number, CancellationToken ct = default)
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
				var body = el.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
					? b.GetString() ?? string.Empty
					: string.Empty;

				var author = string.Empty;
				var isBot = false;
				if (el.TryGetProperty("user", out var user))
				{
					author = user.TryGetProperty("login", out var l) && l.ValueKind == JsonValueKind.String
						? l.GetString() ?? string.Empty
						: string.Empty;
					isBot = user.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String
						&& ty.GetString() == "Bot";
				}

				// This is the boundary an untrusted comment enters through, so it is where the
				// author's standing has to be recorded — everything downstream reads the flag,
				// not the API. An absent/unreadable association stays null and is refused.
				var association = el.TryGetProperty("author_association", out var aa)
					&& aa.ValueKind == JsonValueKind.String
					? aa.GetString()
					: null;

				comments.Add(new ExistingComment(
					id, body, author, isBot, CommentTrust.IsTrustedAuthor(isBot, association)));
			}

			if (doc.RootElement.GetArrayLength() < 100)
			{
				break;
			}
		}

		return comments;
	}

	/// <summary>
	/// Create a comment and return its id, so a run that posts more than once (incremental
	/// publishing, #116) can UPDATE the comment it just created instead of creating a second one.
	/// Returns 0 if the id cannot be read back - the comment exists, we just cannot address it,
	/// which <see cref="CommentLedger"/> treats as "posted, do not duplicate".
	/// </summary>
	public async Task<long> CreateIssueCommentAsync(string owner, string repo, int number, string body, CancellationToken ct = default)
	{
		using var resp = await _http.PostAsync($"repos/{owner}/{repo}/issues/{number}/comments", JsonBody(body), ct);
		await EnsureOk(resp, "create comment", ct);
		try
		{
			using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
			return doc.RootElement.ValueKind == JsonValueKind.Object
				&& doc.RootElement.TryGetProperty("id", out var id) && id.TryGetInt64(out var value)
				? value
				: 0;
		}
		catch (JsonException)
		{
			return 0;
		}
	}

	public async Task UpdateIssueCommentAsync(string owner, string repo, long commentId, string body, CancellationToken ct = default)
	{
		using var resp = await _http.PatchAsync($"repos/{owner}/{repo}/issues/comments/{commentId}", JsonBody(body), ct);
		await EnsureOk(resp, "update comment", ct);
	}

	private static StringContent JsonBody(string body)
	{
		var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["body"] = body });
		return new StringContent(json, Encoding.UTF8, "application/json");
	}

	private static async Task EnsureOk(HttpResponseMessage resp, string what, CancellationToken ct)
	{
		if (resp.IsSuccessStatusCode)
		{
			return;
		}

		var detail = await resp.Content.ReadAsStringAsync(ct);
		throw new CliError($"GitHub API {(int)resp.StatusCode} on {what}: {detail.Trim()}");
	}

	public void Dispose() => _http.Dispose();
}
