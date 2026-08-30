using PeanutGallery.Core;
using PeanutGallery.Engine;

namespace PeanutGallery.Cli;

/// <summary>Verb handlers. Each loads config (shell IO), calls the pure core, and prints.</summary>
internal static class Commands
{
	public static int Init(Args a)
	{
		var path = a.Positionals.Count > 0 ? a.Positionals[0] : a.GetOr("out", "peanut.json");
		if (File.Exists(path) && !a.Flag("force"))
		{
			throw new CliError($"{path} already exists (use --force to overwrite)");
		}

		// The repo target is the directory the user ran this in, under a name derived from it -
		// not a name and an absolute path baked into the scaffold, which produced a config that
		// only ran on the machine it was authored on (#196).
		var repoName = Sample.RepoNameFor(Directory.GetCurrentDirectory());

		ConfigIo.Save(path, Sample.For(repoName));
		Console.WriteLine($"wrote sample config to {path}");
		Console.WriteLine("Next: set your provider API key (OPENROUTER_API_KEY),");
		Console.WriteLine($"then: git diff origin/main... | peanut-gallery plan --repo {repoName} --diff -");
		return 0;
	}

	public static int Personas(Args a)
	{
		var config = LoadConfig(a);
		if (a.Flag("json"))
		{
			Console.WriteLine(ConfigIo.Serialize(config.Personas));
			return 0;
		}

		if (config.Personas.Count == 0)
		{
			Console.WriteLine("(no personas configured)");
			return 0;
		}

		foreach (var p in config.Personas)
		{
			Console.WriteLine($"{p.Id,-12}  {p.Tier,-5}  {p.Model}  (lens: {p.Lens}, temp {p.SamplingTemperature()})");
		}

		return 0;
	}

	public static int Validate(Args a)
	{
		var config = LoadConfig(a);
		var problems = ConfigValidation.Validate(config);
		if (problems.Count == 0)
		{
			Console.WriteLine("config OK");
			return 0;
		}

		foreach (var problem in problems)
		{
			Console.Error.WriteLine($"  {problem.Scope}: {problem.Message}");
		}

		Console.Error.WriteLine($"{problems.Count} problem(s) found");
		return 1;
	}

	public static int Plan(Args a)
	{
		var config = LoadConfig(a);
		var repo = a.Require("repo");
		var diff = DiffFilter.Apply(LoadDiff(a), config.Filter ?? DiffFilterPolicy.Default).Diff;

		// No conventions here on purpose: `plan` prints which personas WOULD review, and never
		// shows the assembled prompt, so reading the file would be IO whose only visible effect
		// is a misleading "applying repo conventions" line on a command that reviews nothing.
		var tasks = ReviewPlanner.Plan(config, repo, diff);

		if (tasks.Count == 0)
		{
			Console.Error.WriteLine($"no personas assigned to '{repo}' (or unknown repo)");
			return 1;
		}

		if (a.Flag("json"))
		{
			var entries = tasks.Select(t => new PlanEntry(
				t.Persona.Id, t.Persona.Name, t.Persona.Tier.ToString(),
				t.Persona.Model.ToString(), t.Persona.SamplingTemperature(), t.Repo.Name)).ToList();
			Console.WriteLine(ConfigIo.Serialize(entries));
			return 0;
		}

		Console.WriteLine($"review plan for '{repo}' ({diff.Files.Count} file(s) changed):");
		foreach (var t in tasks)
		{
			Console.WriteLine($"  - {t.Persona.Name} [{t.Persona.Tier}] via {t.Persona.Model} (temp {t.Persona.SamplingTemperature()})");
		}

		return 0;
	}

	public static async Task<int> ReviewAsync(Args a)
	{
		var config = LoadConfig(a);
		var repo = a.Require("repo");
		var diff = DiffFilter.Apply(LoadDiff(a), config.Filter ?? DiffFilterPolicy.Default).Diff;

		// House rules apply to a one-shot local review too - a developer running `review` on a
		// checkout should get the same repo-aware feedback CI gives.
		var localConventions = ReadConventions(config.FindRepo(repo)?.Path);
		if (localConventions is not null)
		{
			Console.Error.WriteLine($"applying repo conventions from {localConventions.Path}");
		}

		var tasks = ReviewPlanner.Plan(config, repo, diff, localConventions);

		if (tasks.Count == 0)
		{
			Console.Error.WriteLine($"no personas assigned to '{repo}' (or unknown repo)");
			return 1;
		}

		// Real model-backed reviews by default; --dry-run uses the offline stub.
		var dryRun = a.Flag("dry-run");
		IReviewer reviewer = dryRun
			? new StubReviewer()
			: new ChatClientReviewer(config.Providers);

		// Fan out: every persona reviews concurrently - the shell's job, not the core's.
		// ChatClientReviewer turns provider/key failures into findings, so this never throws.
		var reviews = await Task.WhenAll(tasks.Select(t => reviewer.ReviewAsync(t)));

		if (a.Flag("json"))
		{
			Console.WriteLine(ConfigIo.Serialize(reviews));
			return 0;
		}

		foreach (var review in reviews)
		{
			Console.WriteLine(CommentRenderer.Render(review));
			Console.WriteLine();
		}

		if (dryRun)
		{
			Console.Error.WriteLine("(dry run: no model was called)");
		}

		return 0;
	}

	/// <summary>
	/// Longest single file worth reading for context - a ceiling on IO, not on the prompt.
	/// <see cref="ContextBudget"/> decides what actually goes to the model, and windows a file too
	/// big to send whole down to the regions around its hunks. A cap at the prompt budget would
	/// discard the file here, before the core could window it, which is how the largest file in a
	/// PR - the one it churned hardest - went unseen across 15 review runs (#164).
	/// </summary>
	private const int MaxContextFileBytes = 512 * 1024;

	/// <summary>
	/// Where a repo states its review conventions, most specific first. The Copilot file wins when
	/// present because it is unambiguously written FOR a code reviewer; the agent files are
	/// broader (build commands, workflow) but still carry the design rules that matter most.
	/// </summary>
	private static readonly string[] ConventionsCandidates =
	[
		".github/copilot-instructions.md",
		".github/peanut-gallery-instructions.md",
		"CLAUDE.md",
		"AGENTS.md",
	];

	/// <summary>Hard ceiling on what we will even read; the planner caps what it actually sends.</summary>
	private const int MaxConventionsBytes = 64 * 1024;

	/// <summary>
	/// Find the repo's conventions file in the checkout. Best-effort: no file, an unreadable one,
	/// or an absurdly large one simply means the reviewers run without house rules, exactly as
	/// they did before. Note the checkout is the PR head on push/pull_request runs, so a PR that
	/// edits its own conventions takes effect immediately - which is why the prompt frames the
	/// content as repo-provided data rather than instructions.
	/// </summary>
	private static RepoConventions? ReadConventions(string? repoRoot)
	{
		if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
		{
			return null;
		}

		var root = Path.GetFullPath(repoRoot);
		foreach (var candidate in ConventionsCandidates)
		{
			try
			{
				var full = Path.GetFullPath(Path.Combine(root, candidate));
				if (!FileSystemSafety.ResolvesInsideRoot(root, full) || !File.Exists(full))
				{
					continue;
				}

				if (new FileInfo(full).Length > MaxConventionsBytes)
				{
					continue;
				}

				var text = File.ReadAllText(full);
				if (!string.IsNullOrWhiteSpace(text))
				{
					return new RepoConventions(candidate, text);
				}
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
			{
				// Unreadable: try the next candidate rather than sink the review.
			}
		}

		return null;
	}

	/// <summary>
	/// Read the changed files' current text from the checkout, for the whole-file context block.
	/// Best-effort by design: a file that is missing, unreadable, oversized, or binary is simply
	/// not offered - context is an enhancement, and failing a review over it would be absurd.
	/// <para>Note the checkout is the PR head on push/pull_request runs, but the default branch on
	/// an issue_comment trigger, so context can lag the diff there. The prompt frames these as
	/// surrounding code rather than the change under review, which keeps that survivable.</para>
	/// </summary>
	private static Task<IReadOnlyList<FileContext>> ReadFileContextAsync(
		string? repoRoot, IReadOnlyList<string> paths, CancellationToken ct)
	{
		var found = new List<FileContext>();
		if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
		{
			return Task.FromResult<IReadOnlyList<FileContext>>(found);
		}

		var root = Path.GetFullPath(repoRoot);
		foreach (var path in paths)
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				var full = Path.GetFullPath(Path.Combine(root, path));

				// Diff paths are attacker-controlled on any PR, so containment is checked as a
				// resolved path relationship: PathSafety explains the sibling-directory trap a
				// StartsWith check walks into, and FileSystemSafety closes the symlink vector
				// that a string-only check cannot see (#91).
				if (!FileSystemSafety.ResolvesInsideRoot(root, full) || !File.Exists(full))
				{
					continue;
				}

				var info = new FileInfo(full);
				if (info.Length == 0 || info.Length > MaxContextFileBytes)
				{
					continue;
				}

				var text = File.ReadAllText(full);
				if (text.Contains('\0'))
				{
					continue; // binary
				}

				found.Add(new FileContext(path, text));
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
			{
				// Unreadable file: skip it rather than sink the review.
			}
		}

		return Task.FromResult<IReadOnlyList<FileContext>>(found);
	}

	// Imperative shell by design (ADR-0001): this method owns the GitHub IO, the clock
	// (head SHA), and the model call; the pure session logic lives in PeanutGallery.Core
	// (SessionPlanner / SessionCodec / SessionUpdateParser). IO here is the point, not a leak.
	/// <summary>
	/// Aggregate the persistent run-metrics ledgers across a repo's PRs into a dogfooding report:
	/// failure rate + top failure class per model/persona, verification refute rate, latency
	/// percentiles, token cost. Read-only; scrapes the per-PR metrics comments the reviews append.
	/// </summary>
	public static async Task<int> MetricsAsync(Args a)
	{
		var (owner, repoSlug) = ResolveSlug(a);
		var token = a.Get("token") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
			?? throw new CliError("metrics needs a token: set GITHUB_TOKEN or pass --token");
		var apiBase = Environment.GetEnvironmentVariable("GITHUB_API_URL") ?? "https://api.github.com";
		var state = a.Get("state") ?? "all";
		// Bounded by default: a full-history scrape on a large repo (1400+ PRs is realistic) is one
		// comment-list call PER PR, which is a needless way to burn the REST quota. Default to the
		// last 30 days; the operator widens with --since <n>, or --since 0 to scrape everything.
		var days = int.TryParse(a.Get("since"), out var d) ? d : 30;
		DateTimeOffset? since = days > 0 ? DateTimeOffset.UtcNow.AddDays(-days) : null;

		using var gh = new GitHubClient(token, apiBase);
		var prs = await gh.ListPullRequestNumbersAsync(owner, repoSlug, state, since, CancellationToken.None);

		var lines = new List<string>();
		var rolledOff = 0;
		var partialPrs = 0;
		foreach (var n in prs)
		{
			var comments = await gh.ListIssueCommentsAsync(owner, repoSlug, n, CancellationToken.None);
			// Same authorship rule as every other read of comment-held state: the ledger marker
			// is text, and a scraped report that believes a stranger's is a report of fiction.
			var ledger = comments
				.FirstOrDefault(c => CommentTrust.CarriesState(c) && MetricsLedger.IsLedger(c.Body));
			if (ledger is not null)
			{
				lines.AddRange(MetricsLedger.Extract(ledger.Body));
				var evicted = MetricsLedger.EvictedCount(ledger.Body);
				rolledOff += evicted;
				partialPrs += evicted > 0 ? 1 : 0;
			}
		}

		// A ledger longer than a GitHub comment rolls its oldest runs off, and it says so in its own
		// rendered text. Say it here too: this aggregate is the reader who would otherwise take a
		// windowed history for a whole one, and the PRs that lost runs are the busiest ones.
		if (rolledOff > 0)
		{
			// "at least": MetricsLedger.EvictedCount is a lower bound on any ledger old enough to
			// have evicted before it started counting.
			Console.Error.WriteLine($"(note: at least {rolledOff} older run(s) across {partialPrs} PR(s) have "
				+ "rolled off their ledger to keep it inside its size and line bounds, and are not counted below)");
		}

		var runs = MetricsCodec.ReadLines(lines);
		if (since is not null)
		{
			runs = runs.Where(r =>
				DateTimeOffset.TryParse(r.Context.TimestampUtc, out var ts) && ts >= since).ToList();
		}

		if (a.Flag("json"))
		{
			foreach (var line in lines)
			{
				Console.WriteLine(line);
			}

			return 0;
		}

		if (runs.Count == 0)
		{
			Console.WriteLine($"No metrics found for {owner}/{repoSlug}"
				+ (since is not null ? $" in the last {days} day(s)." : ". Reviews append a ledger once this ships."));
			return 0;
		}

		Console.Write(MetricsReport.Render(MetricsReport.From(runs)));
		return 0;
	}

	// await-review's exit codes. 0 is a review that both happened in full and found nothing, and 1
	// stays the usage/API error every other verb exits with, so the three outcomes a caller
	// actually branches on start at 2. A script that only checks "did it exit 0" still behaves:
	// everything that is not a complete, clean review is non-zero.
	private const int ExitFindings = 2;
	private const int ExitTimedOut = 3;

	// The job failed, OR it succeeded and published a panel missing a reviewer. Both are "the
	// review you asked for did not happen", and both must be distinguishable from 0 - which is the
	// gap #130 named: a panel that quietly shrank looks exactly like one that found nothing.
	private const int ExitReviewFailed = 4;

	/// <summary>
	/// Block until THIS push's review has landed, then print what it found. Read-only: it polls
	/// GitHub and writes nothing to the PR.
	///
	/// <para>It exists because the agent guides (<c>CLAUDE.md</c> and its byte-identical siblings)
	/// told a caller to wait for the automated review and gave them no way to do it. Every agent
	/// that read the instruction
	/// reached the same conclusion — that waiting was not an action available to it — and ended
	/// its turn with the review unread. An instruction naming an outcome with no mechanism is not
	/// an instruction.</para>
	///
	/// <para>Two things make the obvious version of this wrong, and both are handled here.</para>
	///
	/// <para>First: the check has to APPEAR before it can finish. Immediately after
	/// <c>gh pr create</c> the workflow has not registered its check run, so anything that asks
	/// "has it concluded?" gets a confident, wrong answer in under a second. So the wait has two
	/// phases — appearance, then conclusion.</para>
	///
	/// <para>Second, and subtler: the panel comment is upserted in place, so the PREVIOUS turn's
	/// findings are sitting on the PR the entire time the new review runs. "Is there a panel
	/// comment?" is therefore always yes and always useless. Freshness is decided against the head
	/// SHA by <see cref="PanelReadiness"/>, from the comment's structured state blob.</para>
	/// </summary>
	public static async Task<int> AwaitReviewAsync(Args a)
	{
		if (!int.TryParse(a.Require("pr"), out var pr))
		{
			throw new CliError($"--pr must be a number, got '{a.Get("pr")}'");
		}

		var (owner, repoSlug) = ResolveSlug(a);
		var token = a.Get("token") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
			?? throw new CliError("await-review needs a token: set GITHUB_TOKEN or pass --token");
		var apiBase = Environment.GetEnvironmentVariable("GITHUB_API_URL") ?? "https://api.github.com";
		var checkName = a.GetOr("check", "review");
		var timeout = TimeSpan.FromSeconds(Seconds(a, "timeout", 900));
		var interval = TimeSpan.FromSeconds(Seconds(a, "interval", 10));

		using var gh = new GitHubClient(token, apiBase);

		// --sha pins the wait to a commit the caller knows it pushed. Without it we ask GitHub for
		// the head, which is right in the ordinary case and wrong if somebody pushed while we were
		// starting - in which case we would wait on a commit the caller never asked about.
		var headSha = a.Get("sha") ?? (await gh.GetPullRequestAsync(owner, repoSlug, pr)).HeadSha;
		if (string.IsNullOrWhiteSpace(headSha))
		{
			throw new CliError($"could not resolve a head SHA for {owner}/{repoSlug}#{pr}");
		}

		Console.Error.WriteLine(
			$"awaiting `{checkName}` on {owner}/{repoSlug}#{pr} @ {Sha.Short(headSha)} "
			+ $"(timeout {timeout.TotalSeconds:F0}s, polling every {interval.TotalSeconds:F0}s)");

		var deadline = DateTimeOffset.UtcNow + timeout;
		var lastNote = string.Empty;
		while (true)
		{
			var runs = await gh.ListCheckRunsAsync(owner, repoSlug, headSha);
			var named = runs
				.Where(r => string.Equals(r.Name, checkName, StringComparison.OrdinalIgnoreCase))
				.ToList();

			var conclusion = Conclusion(named);
			if (conclusion is not null)
			{
				var panel = await ReadPanelAsync(gh, owner, repoSlug, pr, headSha);
				if (panel.Readiness.Settled || conclusion is not "success")
				{
					return ReportPanel(panel, conclusion, headSha);
				}

				Note(ref lastNote, $"`{checkName}` finished but the panel comment is still the previous turn's; waiting for it to update.");
			}
			else if (named.Count == 0)
			{
				Note(ref lastNote, $"`{checkName}` has not appeared yet for {Sha.Short(headSha)}.");
			}
			else
			{
				Note(ref lastNote, $"`{checkName}` is {string.Join(", ", named.Select(r => r.Status))}.");
			}

			if (DateTimeOffset.UtcNow + interval >= deadline)
			{
				return await TimedOutAsync(gh, owner, repoSlug, pr, headSha, timeout, checkName);
			}

			await Task.Delay(interval);
		}
	}

	/// <summary>
	/// The check's verdict, or null while it is still pending — which covers both "has not appeared"
	/// and "running", because neither is a verdict. Several runs can share a name (a re-run adds
	/// one), so a failure among them wins: a green sibling does not undo it.
	/// </summary>
	private static string? Conclusion(IReadOnlyList<CheckRun> named)
	{
		if (named.Count == 0 || named.Any(r => r.Status is not "completed"))
		{
			return null;
		}

		var failed = named.FirstOrDefault(r => r.Conclusion is not ("success" or "neutral" or "skipped"));
		return failed?.Conclusion ?? "success";
	}

	private sealed record PanelComment(PanelReadiness Readiness, string? Body);

	private static async Task<PanelComment> ReadPanelAsync(
		GitHubClient gh, string owner, string repo, int pr, string headSha)
	{
		var comments = await gh.ListIssueCommentsAsync(owner, repo, pr);
		var bodies = comments.Select(c => c.Body).ToList();
		var readiness = PanelReadiness.Read(bodies, headSha);
		var marker = CommentRenderer.Marker(PanelCommentRenderer.PanelId);
		return new PanelComment(readiness, bodies.FirstOrDefault(b => b.Contains(marker, StringComparison.Ordinal)));
	}

	// The findings go to stdout so the caller can pipe them; everything about the wait goes to
	// stderr. An agent that redirects stdout into its next step should get the review, not our
	// progress chatter.
	private static int ReportPanel(PanelComment panel, string conclusion, string headSha)
	{
		var r = panel.Readiness;
		if (panel.Body is not null)
		{
			Console.WriteLine(PanelSessionCodec.Visible(panel.Body));
		}

		if (conclusion is not "success")
		{
			Console.Error.WriteLine(
				$"the review job concluded '{conclusion}' — anything above may be incomplete. "
				+ "Check the workflow run before trusting it.");
			return ExitReviewFailed;
		}

		if (r.Arrival is PanelArrival.NoReviewers)
		{
			Console.Error.WriteLine(
				$"the review job succeeded but the panel it published carries no reviewer at all "
				+ $"({r.Degraded} named as not reporting). Nothing looked at {Sha.Short(headSha)}.");
			return ExitReviewFailed;
		}

		if (!r.Landed)
		{
			Console.Error.WriteLine(
				$"the review job succeeded but no panel comment reports {Sha.Short(headSha)} "
				+ $"(arrival: {r.Arrival}). Nothing above is this push's review.");
			return ExitTimedOut;
		}

		var who = r.Arrival is PanelArrival.Partial
			? $", {r.ReviewersAtHead}/{r.Reviewers} reviewers on this commit"
			: string.Empty;
		var degraded = r.Degraded > 0 ? $", {r.Degraded} reviewer(s) did not report" : string.Empty;
		Console.Error.WriteLine($"panel landed for {Sha.Short(headSha)} at turn {r.Turn}{who}{degraded}.");

		if (r.HasFindings)
		{
			Console.Error.WriteLine(
				"findings above. Address every one with a fix or with a refutation posted on the PR — "
				+ "silence does not close a finding.");
			return ExitFindings;
		}

		// An empty board is only a clean review if the whole panel was there to fill it. A reviewer
		// that timed out found nothing in the same sense that a closed eye sees nothing, and #130
		// exists because that shape used to read as green. Non-zero, and named.
		if (!r.Complete)
		{
			Console.Error.WriteLine(
				"no findings — but this panel is incomplete, so that is not a clean review. "
				+ "Re-run the review, or read the 'Did not report' line above and decide knowingly.");
			return ExitReviewFailed;
		}

		Console.Error.WriteLine("no findings.");
		return 0;
	}

	// On the way out, say whether the commit we waited on is still the head. A push that landed
	// mid-wait supersedes the run we were watching (the action early-exits green on supersession),
	// and without this the caller reads a bare timeout and re-runs the same doomed wait.
	private static async Task<int> TimedOutAsync(
		GitHubClient gh, string owner, string repo, int pr, string headSha, TimeSpan timeout, string checkName)
	{
		Console.Error.WriteLine(
			$"timed out after {timeout.TotalSeconds:F0}s waiting for `{checkName}` on {Sha.Short(headSha)}.");
		try
		{
			var head = (await gh.GetPullRequestAsync(owner, repo, pr)).HeadSha;
			if (!Sha.SameCommit(head, headSha))
			{
				Console.Error.WriteLine(
					$"the head has moved to {Sha.Short(head)} since the wait started — "
					+ "that push superseded the run being watched. Re-run against the new head.");
			}
		}
		catch (CliError)
		{
			// Best-effort explanation only; the timeout is the answer either way.
		}

		return ExitTimedOut;
	}

	private static void Note(ref string last, string message)
	{
		if (message == last)
		{
			return;
		}

		last = message;
		Console.Error.WriteLine(message);
	}

	private static int Seconds(Args a, string key, int fallback)
	{
		var raw = a.Get(key);
		if (raw is null)
		{
			return fallback;
		}

		return int.TryParse(raw, out var n) && n > 0
			? n
			: throw new CliError($"--{key} must be a positive number of seconds, got '{raw}'");
	}

	public static async Task<int> ReviewPrAsync(Args a)
	{
		var config = LoadConfig(a);
		if (!int.TryParse(a.Require("pr"), out var pr))
		{
			throw new CliError($"--pr must be a number, got '{a.Get("pr")}'");
		}

		var configRepo = a.Get("repo") ?? (config.Repos.Count == 1
			? config.Repos[0].Name
			: throw new CliError("config has multiple repos; pass --repo <name> to pick the persona panel"));

		var (owner, repoSlug) = ResolveSlug(a);
		var token = a.Get("token") ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
		var apiBase = Environment.GetEnvironmentVariable("GITHUB_API_URL") ?? "https://api.github.com";
		var dryRun = a.Flag("dry-run");

		// --preview decouples the two things --dry-run had fused: real models, but nothing posted.
		// Without it there is no way to exercise a review (or compare two panels) without writing
		// comments to somebody's PR, which made evaluating panel modes impossible.
		var preview = a.Flag("preview");
		var offlineDiff = a.Get("diff");
		var eventJson = ReadEventJson(); // the run's event payload, read once and reused below

		// Action-owned safety gate #1: refuse a comment-triggered run from a bot or an
		// untrusted author, regardless of the consumer workflow's `if:`.
		//
		// Keyed on the event-name SET, not on "issue_comment" alone. A
		// pull_request_review_comment trigger carries the same attacker-controlled comment
		// body and the same author fields, and gating only the one name left it ungated.
		//
		// An unset GITHUB_EVENT_NAME is an offline run and is not gated here. But once the
		// name says a comment triggered this, the guard fails CLOSED: a missing or
		// unreadable payload is a comment run whose author cannot be established, which is
		// the case this gate exists to refuse.
		var eventName = Environment.GetEnvironmentVariable("GITHUB_EVENT_NAME");
		if (GitHubEventGuard.IsCommentEvent(eventName))
		{
			if (!GitHubEventGuard.IsTrustedCommentEvent(eventJson))
			{
				Console.Error.WriteLine(
					$"refusing: {eventName} trigger is from a bot, an untrusted author, or a payload whose author cannot be read.");
				return 0;
			}

			// Action-owned safety gate #1b (issue #37): issue_comment fires on plain issues as
			// well as PRs, and the consumer hands us github.event.issue.number either way. An
			// issue number is not a PR number, so GET /pulls/<n> 404s and the run goes red on
			// an event that simply had nothing to review - every repo that adopted the
			// quickstart shipped that gap. Skipping clean here is what lets the consumer's
			// `if:` be an efficiency filter rather than the only thing standing between a
			// comment on a tracking issue and a failed check.
			if (!GitHubEventGuard.IsCommentOnPullRequest(eventJson))
			{
				Console.Error.WriteLine($"skipping: this {eventName} is not on a pull request; nothing to review.");
				return 0;
			}
		}

		// An unknown repo is always fatal - there is nothing to review against.
		if (config.FindRepo(configRepo) is null)
		{
			Console.Error.WriteLine($"unknown repo '{configRepo}'");
			return 1;
		}

		// An empty panel is only fatal in FIXED mode. In a dynamic mode the panel comes from the
		// orchestrator, and having no configured personas is the canonical 'auto' setup - bailing
		// here would refuse to run the very mode the config asked for.
		var pairs = ReviewPlanner.Plan(config, configRepo, Diff.Empty);
		if (pairs.Count == 0 && (config.Panel ?? PanelMode.Fixed) == PanelMode.Fixed)
		{
			Console.Error.WriteLine($"no personas assigned to '{configRepo}'");
			return 1;
		}

		using var gh = token is null ? null : new GitHubClient(token, apiBase);

		// Anchor the run: the PR's current head SHA + the comments that carry prior sessions.
		string headSha;
		IReadOnlyList<ExistingComment> existing;
		PullRequestIntent? intent = null; // what the author says the change is for (offline runs have none)
		// The branch this PR merges into. Carried out of the block below so the #178 baseline can be
		// resolved as base...headSha rather than from the PR's moving head - see ResolveBaselineAsync.
		string? baseRef = null;
		if (offlineDiff is not null)
		{
			headSha = "local";
			existing = [];
		}
		else
		{
			if (gh is null)
			{
				throw new CliError(
					"set GITHUB_TOKEN to review a PR, or pass --diff <file> with --dry-run (offline stub) "
					+ "or --preview (real models, nothing posted) for an offline run");
			}

			PullRequestInfo pull;
			try
			{
				pull = await gh.GetPullRequestAsync(owner, repoSlug, pr);
			}
			catch (GitHubApiError e) when (e.Status == 404 && GitHubEventGuard.IsCommentEvent(eventName))
			{
				// Belt for #37. The payload gate above is the primary fix; this covers what it
				// cannot see - a PR deleted between the comment and this fetch, or a --pr that
				// does not match the payload's subject. A comment-triggered run that finds no
				// PR has nothing to review, and that is not a failure. Narrow on purpose: only
				// 404, only the first fetch, only a comment trigger. A push-triggered 404 is a
				// real misconfiguration and still fails.
				Console.Error.WriteLine(
					$"skipping: no pull request #{pr} in {owner}/{repoSlug} for this {eventName}; nothing to review.");
				return 0;
			}

			headSha = pull.HeadSha;
			baseRef = pull.BaseRef;
			intent = new PullRequestIntent(pull.Title, pull.Body);

			// Action-owned safety gate #2: forks don't receive secrets and shouldn't consume
			// the key/runner. Refuse unless the PR head is the base repo (override: --allow-fork).
			if (!a.Flag("allow-fork") && !string.Equals(pull.HeadRepo, $"{owner}/{repoSlug}", StringComparison.OrdinalIgnoreCase))
			{
				var shown = pull.HeadRepo.Length == 0 ? "unknown" : pull.HeadRepo;
				Console.Error.WriteLine($"refusing: PR #{pr} head '{shown}' is not the base repo '{owner}/{repoSlug}'; forks don't receive secrets (use --allow-fork to override).");
				return 0;
			}

			// Per-PR opt-out: skip label / title-or-body marker / draft (config via peanut.json "skip").
			var (skip, skipReason) = (config.Skip ?? SkipPolicy.Default)
				.Evaluate(new PullRequestMeta(pull.Labels, pull.Title, pull.Body, pull.IsDraft));
			if (skip)
			{
				Console.Error.WriteLine($"skipping review of PR #{pr}: {skipReason}.");
				return 0;
			}

			// Clean-skip on supersession (issue #32): if a newer push moved the head since
			// this run was triggered, a newer run is reviewing the current head - exit 0
			// (green) before any model calls, instead of posting a review for a stale SHA and
			// leaving a CANCELLED check / UNSTABLE merge state. Pairs with
			// cancel-in-progress: false in the consumer workflows (push-vs-comment collisions
			// then serialize rather than cancel). issue_comment events carry no head SHA ->
			// TriggerHeadSha is null -> never skipped here.
			var supersededReason = Supersession.SupersededReason(GitHubEventGuard.TriggerHeadSha(eventJson), headSha);
			if (supersededReason is not null)
			{
				Console.Error.WriteLine($"superseded: {supersededReason}; skipping (a newer run reviews the current head).");
				return 0;
			}

			existing = await gh.ListIssueCommentsAsync(owner, repoSlug, pr);
		}

		// Review budget (env-tunable) so one slow/hung model can't stall the run, plus a bounded
		// in-process retry: a transient flake (slow/hung OpenRouter route, 5xx/429, dropped
		// connection) re-routes on the retry instead of needing a human to re-trigger. The final
		// attempt keeps the full budget so a legitimately-slow call never regresses. The SAME budget
		// is handed to the runner as the per-persona ceiling, which is what actually bounds a turn:
		// on its own the per-call budget is spent per attempt AND per call, so a turn could run for
		// multiples of it (#117).
		// Two nested budgets (issue #133): callTimeout bounds a SINGLE model attempt (~180s) so a
		// runaway is abandoned early; timeout is the whole-persona-turn wall (~600s) that all the
		// retries share. Fusing them - one hung call spending the entire 600s - was the shape of the
		// minimax-m3 timeouts. The per-call ceiling now applies to every attempt, so a runaway dies
		// fast and the retry gets a fresh, fail-fast shot inside the same turn budget.
		var (timeout, callTimeout, maxAttempts, maxOutputTokens) = ReviewBudget.FromEnvironment(Environment.GetEnvironmentVariable);
		// Opt-in (#130): make a degraded review fail the CI check. Off by default - reviews are
		// advisory, so a degraded persona is disclosed but the run stays green.
		var failOnDegraded = ReviewBudget.FailOnDegraded(Environment.GetEnvironmentVariable(ReviewBudget.FailOnDegradedVariable));
		var conventions = ReadConventions(config.FindRepo(configRepo)?.Path);
		if (conventions is not null)
		{
			Console.Error.WriteLine($"applying repo conventions from {conventions.Path}");
		}

		// Opt-in provider-side JSON mode. Off by default because it is a per-model gamble
		// (minimax-m3 returned an empty reply under it); see ChatClientReviewer for the detail.
		var jsonMode = Environment.GetEnvironmentVariable("PG_JSON_MODE") is "1" or "true";
		IReviewer reviewer = dryRun
			? new StubReviewer()
			: new ChatClientReviewer(
				config.Providers,
				perCallTimeout: callTimeout,
				maxAttempts: maxAttempts,
				jsonMode: jsonMode,
				maxOutputTokens: maxOutputTokens);

		// A dynamic panel needs an orchestrator; without one configured the runner falls back to
		// the committed panel (ConfigValidation flags that as a config problem, so it is loud).
		// The planner shares the reviewer, so it inherits the same timeout + retry behaviour.
		IPanelPlanner? panelPlanner = null;
		if ((config.Panel ?? PanelMode.Fixed) != PanelMode.Fixed && config.Orchestrator is not null)
		{
			// What generated personas review with. Explicit 'personaModel' wins; otherwise the
			// first configured persona's model, so a dynamic panel costs what the fixed one did.
			// The orchestrator is NOT a fallback - it plans lenses, it does not review, and
			// ConfigValidation requires personaModel when there are no personas to inherit from.
			var personaModel = config.PersonaModel ?? (config.Personas.Count > 0 ? config.Personas[0].Model : null);
			// What generated personas sample at. Explicit 'personaTemperature' wins and is respected
			// as-is (it is authored, like a seed persona's own temperature - a deliberate 0 stands).
			// Otherwise auto personas inherit the first seed persona's temperature, FLOORED at the
			// non-greedy default (#127/#129): an inherited seed at 0 would silently make every invented
			// persona greedy - the reasoning-runaway mode - so only the inherited fallback is floored.
			var seedTemp = config.Personas.Count > 0 ? config.Personas[0].SamplingTemperature() : PanelFence.DefaultTemperature;
			var personaTemp = PanelFence.PersonaTemperature(config.PersonaTemperature, seedTemp);
			// top_p/top_k for the auto reviewers: explicit 'personaTopP'/'personaTopK', else the seed's,
			// else the provider default (null). Resolved by PanelFence beside PersonaTemperature.
			var seedTopP = config.Personas.Count > 0 ? config.Personas[0].TopP : null;
			var seedTopK = config.Personas.Count > 0 ? config.Personas[0].TopK : null;
			var personaTopP = PanelFence.PersonaTopP(config.PersonaTopP, seedTopP);
			var personaTopK = PanelFence.PersonaTopK(config.PersonaTopK, seedTopK);
			if (personaModel is null)
			{
				Console.Error.WriteLine(
					"panel mode needs a 'personaModel' (or at least one configured persona); reviewing with the configured panel.");
			}
			else
			{
				panelPlanner = new ChatClientPanelPlanner(
					reviewer, config.Orchestrator, personaModel, personaTemp,
					PanelFence.MaxPersonas, Console.Error.WriteLine, personaTopP, personaTopK);
			}
		}

		// The thread as this run sees it, advanced by our own writes so posting twice for the same
		// marker updates in place instead of duplicating (#116).
		var ledger = CommentLedger.From(existing);
		var created = 0;
		var updated = 0;

		async Task PostAsync(IReadOnlyList<string> bodies, CancellationToken token)
		{
			foreach (var op in ledger.Plan(bodies))
			{
				if (op.Action == UpsertAction.Update)
				{
					await gh!.UpdateIssueCommentAsync(owner, repoSlug, op.CommentId!.Value, op.Body, token);
					ledger = ledger.Record(op, op.CommentId!.Value);
					updated++;
				}
				else
				{
					var id = await gh!.CreateIssueCommentAsync(owner, repoSlug, pr, op.Body, token);
					ledger = ledger.Record(op, id);
					created++;
				}
			}
		}

		// Publish as each persona lands, not once at the end: a hung reviewer must not be able to
		// take its finished colleagues' work down with it when the job hits its timeout backstop
		// (#116). Only when we are actually posting - a preview/dry run has nothing to publish to,
		// and the runner treats a null seam as "just give me the end-of-run result".
		var publishing = !dryRun && !preview && gh is not null;

		// The PR's CUMULATIVE diff (merge base -> headSha), resolved ONCE per run because it is
		// persona-independent, and spent twice: as the baseline that lets a continued turn tell this
		// branch's own earlier work from established API (#178), and as the shape a trajectory is a
		// series of. This shell was already fetching it for the metrics line at the end of the run,
		// so the baseline costs no extra call here - only the hoist. A failure costs the fact and the
		// metric, never the run: the review then runs exactly as it did before #178.
		Diff? cumulative = null;
		try
		{
			cumulative = await ResolveBaselineAsync(gh, owner, repoSlug, baseRef, headSha, offlineDiff, a);
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			Console.Error.WriteLine(
				$"[baseline] could not resolve the pull request's cumulative diff ({e.Message}); " +
				"continued turns lose the #178 baseline and this run has no diff shape");
		}

		// The shared review-orchestration fold. Each persona's session advances concurrently
		// (independent comment + state); a slow/hung persona is deadline-bounded and degrades to a
		// failure comment for that persona, never stalling the run. Progress goes to stderr.
		var run = await ReviewRunner.RunAsync(
			new ReviewRunRequest(
				config, configRepo, headSha, existing,
				DeltaSource: (prior, _) => ResolveDeltaAsync(gh, owner, repoSlug, pr, prior, headSha, offlineDiff, a),
				reviewer,
				Filter: config.Filter,
				AllowUnchangedSkip: offlineDiff is null,
				Intent: intent,
				ContextSource: (paths, token) => ReadFileContextAsync(config.FindRepo(configRepo)?.Path, paths, token),
				Conventions: conventions,
				PanelPlanner: panelPlanner,
				Publish: publishing ? PostAsync : null,
				PersonaBudget: timeout,
				Baseline: cumulative),
			log: Console.Error.WriteLine);

		// Observability (GitHub Actions only): a durable per-run Job Summary + a degradation
		// annotation per failed persona. These do NOT change the run conclusion — a degraded
		// review stays green — they make the flake legible even after the PR comment self-heals
		// on the next successful run. Emitted before the dry-run return so a dry run in Actions
		// still summarises.
		EmitActionsObservability(run, $"{owner}/{repoSlug}", pr, headSha);

		// A machine-readable metrics line for EVERY run (including preview), so a run is self-
		// documenting without hand-grepping progress lines. Persisted to the PR's ledger below on a
		// real post; on preview it is emitted for local inspection only. The shape is taken from the
		// run's cumulative diff resolved above - per-persona deltas are relative to each persona's
		// own session, so none of them is the PR's size - and is simply absent if that resolve failed.
		DiffShape? shape = cumulative is null ? null : DiffShape.Of(cumulative);

		var metrics = MetricsCollector.From(run, new RunContext(
			$"{owner}/{repoSlug}", pr, Sha.Short(headSha),
			DateTimeOffset.UtcNow.ToString("O"), (config.Panel ?? PanelMode.Fixed).ToString(), shape));
		var metricsLine = MetricsCodec.WriteLine(metrics);
		Console.WriteLine("PG_METRICS " + metricsLine);

		var rendered = run.RenderedBodies;
		var skipped = run.Unchanged;

		if (dryRun || preview)
		{
			foreach (var body in rendered)
			{
				Console.WriteLine(body);
				Console.WriteLine();
			}

			var mode = dryRun ? "dry run" : "preview";
			Console.Error.WriteLine($"({mode}: {rendered.Count} comment(s), {skipped} unchanged, on {owner}/{repoSlug}#{pr}; nothing posted)");
			return 0;
		}

		if (gh is null)
		{
			throw new CliError("posting needs a token: set GITHUB_TOKEN or pass --token");
		}

		// The closing write. Everything already published mid-run is a no-op here (the ledger drops
		// a body identical to what is on the thread), so this posts the final state of whatever
		// changed after its persona landed - in panel mode, the last reviewer's contribution plus
		// the drop of the "still running" header.
		await PostAsync(rendered, CancellationToken.None);

		// Append this run to the PR's metrics ledger (the metrics comment IS the datastore, upserted
		// by the same marker-matching path). Best-effort: a metrics write must never fail a review.
		try
		{
			var priorLedger = existing
				.FirstOrDefault(c => CommentTrust.CarriesState(c) && MetricsLedger.IsLedger(c.Body))?.Body;
			await PostAsync([MetricsLedger.Append(priorLedger, metricsLine)], CancellationToken.None);
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			Console.Error.WriteLine($"(could not update metrics ledger: {e.Message})");
		}

		Console.WriteLine($"{owner}/{repoSlug}#{pr} @ {Sha.Short(headSha)}: {created} new, {updated} updated, {skipped} unchanged");

		// The gate is deliberately the FINAL statement, and every side effect the opt-in promise
		// covers has already run ABOVE it, in order: the Job Summary + ::warning:: annotations in
		// EmitActionsObservability (called near the top of the post path, before the dry-run return,
		// so a dry run still summarises), the PR comments via PostAsync, and the metrics-ledger
		// append. So returning a non-zero code here cannot skip any of them - it only adds a red
		// check on top of the full advisory review (#130). Off by default: a no-op unless a repo
		// asked a partial panel to block its merge gate.
		var degraded = RunSummary.DegradedCount(run.Personas);
		if (failOnDegraded && degraded > 0)
		{
			Console.Error.WriteLine(
				$"PG_FAIL_ON_DEGRADED: {degraded} reviewer(s) degraded this run — failing the check (exit {DegradedExitCode}).");
			return DegradedExitCode;
		}

		return 0;
	}

	/// <summary>Exit code when the opt-in <c>PG_FAIL_ON_DEGRADED</c> gate trips (#130). Distinct from
	/// a <see cref="CliError"/>'s 1 so a degraded-but-posted review reads apart from a real failure.</summary>
	private const int DegradedExitCode = 3;

	// Write the run's Job Summary (durable, per-run) and print a ::warning:: annotation per degraded
	// persona. No-op outside GitHub Actions so local/offline runs stay quiet. Best-effort: a summary
	// write failure must never fail the review.
	private static void EmitActionsObservability(ReviewRunResult run, string slug, int pr, string headSha)
	{
		if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal))
		{
			return;
		}

		var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
		if (!string.IsNullOrEmpty(summaryPath))
		{
			try
			{
				File.AppendAllText(summaryPath, RunSummary.RenderStepSummary(run.Personas, slug, pr, headSha));
			}
			catch (IOException ex)
			{
				Console.Error.WriteLine($"(could not write job summary: {ex.Message})");
			}
		}

		foreach (var annotation in RunSummary.Annotations(run.Personas))
		{
			Console.WriteLine(annotation);
		}
	}


	/// <summary>The raw GitHub Actions event payload JSON (GITHUB_EVENT_PATH), or null offline.</summary>
	private static string? ReadEventJson()
	{
		var path = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
		return path is not null && File.Exists(path) ? File.ReadAllText(path) : null;
	}

	// First turn -> full PR diff; continued -> the delta since last review (fall back to full on a force-push).
	private static async Task<Diff> ResolveDeltaAsync(
		GitHubClient? gh, string owner, string repo, int pr, ReviewSession prior, string headSha, string? offlineDiff, Args a)
	{
		if (offlineDiff is not null)
		{
			return LoadDiff(a);
		}

		// A stored SHA that is not shaped like a commit id is not one: it came out of a PR comment,
		// and the only honest thing to do with it is review the whole PR again. The client escapes
		// it too, so this is not what stops it reaching the network - it is what stops an arbitrary
		// ref from being compared against at all.
		if (prior.IsFirstTurn || !Sha.IsCommitId(prior.LastReviewedSha))
		{
			return Diff.Parse(await gh!.GetPullRequestDiffAsync(owner, repo, pr));
		}

		try
		{
			return Diff.Parse(await gh!.GetCompareDiffAsync(owner, repo, prior.LastReviewedSha!, headSha));
		}
		catch (CliError)
		{
			return Diff.Parse(await gh!.GetPullRequestDiffAsync(owner, repo, pr));
		}
	}

	/// <summary>
	/// The PR's cumulative diff, ANCHORED to the run's head SHA: <c>compare/{base}...{headSha}</c>.
	///
	/// <para>Three dots, so this is the merge base of the base branch and <paramref name="headSha"/>
	/// — the same relation the PR's own <c>.diff</c> expresses, and stable while the base branch
	/// advances, because a commit added to the base afterwards cannot become an ancestor of a fixed
	/// head. What it does NOT share with the PR endpoint is the moving head.</para>
	///
	/// <para>That distinction is the fix for #181. Fetching <c>pulls/{n}</c> returns the diff for
	/// whatever the head is AT FETCH TIME, while the delta every persona reviews is anchored to the
	/// <c>headSha</c> captured when the run started. A push landing in that window leaves the two
	/// diffs ending at different commits, and <see cref="OwnRemovals"/>'s identity then carries a
	/// residue of <c>count(head') - count(head)</c>: positive precisely when the newer push re-added
	/// a line this turn removed, which manufactures the claim that an established line is the
	/// branch's own. The supersession gate above only covers trigger-to-run-setup, not
	/// run-setup-to-here.</para>
	///
	/// <para>No base ref (an offline run, or a PR whose base the API did not report) means no
	/// anchored baseline, and an unanchored one is worse than none: <see cref="Diff.Empty"/> reads as
	/// "cannot tell" and the prompt says nothing.</para>
	/// </summary>
	private static async Task<Diff> ResolveBaselineAsync(
		GitHubClient? gh, string owner, string repo, string? baseRef, string headSha, string? offlineDiff, Args a)
	{
		if (offlineDiff is not null)
		{
			return LoadDiff(a);
		}

		return gh is null || string.IsNullOrEmpty(baseRef)
			? Diff.Empty
			: Diff.Parse(await gh.GetCompareDiffAsync(owner, repo, baseRef, headSha));
	}

	private static (string Owner, string Repo) ResolveSlug(Args a)
	{
		var slug = a.Get("slug")
			?? Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")
			?? throw new CliError("repo slug required: pass --slug owner/name or set GITHUB_REPOSITORY");
		var parts = slug.Split('/');
		if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
		{
			throw new CliError($"--slug must be 'owner/name', got '{slug}'");
		}

		return (parts[0], parts[1]);
	}

	private static PeanutConfig LoadConfig(Args a)
	{
		var path = a.GetOr("config", "peanut.json");
		if (!File.Exists(path))
		{
			throw new CliError($"config not found: {path} (run `peanut-gallery init` to scaffold one)");
		}

		var config = ConfigIo.Load(path);

		// Say out loud which personas are sampling at a value nobody wrote down. The resolution is
		// safe (Persona.SamplingTemperature()), but #127 was as much about silence as about the value:
		// a config that omits the knob should not have to be read alongside the source to know what
		// it will sample at. stderr, so `--json` output on stdout stays machine-clean.
		if (Persona.UnsetTemperatureNotice(config.Personas) is { } notice)
		{
			Console.Error.WriteLine(notice);
		}

		return config;
	}

	private static Diff LoadDiff(Args a)
	{
		var src = a.Get("diff");
		if (src is null)
		{
			return Diff.Empty;
		}

		if (src == "-")
		{
			return Diff.Parse(Console.In.ReadToEnd());
		}

		if (!File.Exists(src))
		{
			throw new CliError($"diff file not found: {src}");
		}

		return Diff.Parse(File.ReadAllText(src));
	}

	// JSON projection for `plan --json` (avoids dumping the full prompt + diff).
	private sealed record PlanEntry(
		string PersonaId, string Persona, string Tier, string Model, double Temperature, string Repo);
}
