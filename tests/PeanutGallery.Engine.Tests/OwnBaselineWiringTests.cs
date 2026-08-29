using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// End-to-end for #178: the baseline a shell resolves once per run has to reach the continued
/// turn's prompt, and its absence has to reach nothing at all.
///
/// <para>The regression this pins is <a href="https://github.com/charles8051/peanut-gallery/pull/175">
/// #175</a> turn 2, where a rename of a method turn 1 had introduced was filed as <c>major</c> for
/// breaking callers that cannot exist.</para>
/// </summary>
public class OwnBaselineWiringTests
{
	private const string Repo = "acme-api";

	private const string RenamedAway = "public static Trajectory? Of(IReadOnlyList<Turn> turns)";

	private static readonly PeanutConfig Config = new(
		Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
		Personas:
		[
			new Persona("architect", "architect", "bugs", ReviewTier.Diff,
				new ModelRef("openrouter", "some/model"), 0.2, "review it"),
		],
		Repos: [new RepoTarget(Repo, ".")],
		Assignments: [new Assignment("architect", Repo)],
		Verify: false);

	// Turn 2: the rename of a method turn 1 introduced.
	private static readonly Diff Delta = Diff.Parse(
		"diff --git a/src/Trajectory.cs b/src/Trajectory.cs\n"
		+ "--- a/src/Trajectory.cs\n+++ b/src/Trajectory.cs\n@@ -1,2 +1,2 @@\n"
		+ $"-\t{RenamedAway}\n"
		+ "+\tpublic static Trajectory? OfTurns(IReadOnlyList<Turn> turns)\n");

	// The pull request as a whole: only the renamed-TO form is on the branch, and neither form is
	// on the base.
	private static readonly Diff Baseline = Diff.Parse(
		"diff --git a/src/Trajectory.cs b/src/Trajectory.cs\n"
		+ "--- a/src/Trajectory.cs\n+++ b/src/Trajectory.cs\n@@ -1,1 +1,2 @@\n"
		+ "+\tpublic static Trajectory? OfTurns(IReadOnlyList<Turn> turns)\n");

	private static readonly ExistingComment[] AfterTurnOne =
	[
		new(1,
			SessionCodec.Embed(
				CommentRenderer.Marker("architect") + "\n### A\n",
				new ReviewSession("older", 1, "running", [])),
			"github-actions",
			IsBot: true),
	];

	private static async Task<string> PromptAsync(ExistingComment[] existing, Diff? baseline)
	{
		var reviewer = new ScriptedReviewer("""{"summary":"s","findings":[]}""");
		await ReviewRunner.RunAsync(new ReviewRunRequest(
			Config, Repo, "sha1", existing,
			(_, _) => Task.FromResult(Delta), reviewer, Baseline: baseline));
		return Msg.User(reviewer.FirstRequest!);
	}

	[Fact]
	public async Task A_continued_turn_learns_which_removals_its_own_branch_introduced()
	{
		var user = await PromptAsync(AfterTurnOne, Baseline);

		Assert.Contains("added by an EARLIER TURN of this same pull request", user);

		// Sliced to the block: the raw delta in the same message quotes this line too, so asserting
		// on the whole prompt would pass even if the block named nothing.
		var block = user[user.IndexOf("These lines, removed", System.StringComparison.Ordinal)..];
		Assert.Contains(RenamedAway, block);
		Assert.Contains("src/Trajectory.cs", block);
	}

	[Fact]
	public async Task A_run_with_no_baseline_reviews_exactly_as_it_did_before()
	{
		// The shell could not fetch the cumulative diff. That costs the fact and nothing else - it
		// must never be filled in with a guess, because a wrong "your branch introduced this" would
		// suppress a real breaking-change finding.
		var user = await PromptAsync(AfterTurnOne, baseline: null);

		Assert.Contains("continuing your review", user);
		Assert.DoesNotContain("EARLIER TURN", user);
	}

	[Fact]
	public async Task The_first_turn_is_shown_the_whole_pull_request_and_needs_no_block()
	{
		var user = await PromptAsync([], Baseline);

		Assert.Contains("First review", user);
		Assert.DoesNotContain("EARLIER TURN", user);
	}

	// #181, and the reason the arithmetic reads the RAW delta rather than the filtered one.
	//
	// An earlier turn moved an established method from A.cs to B.cs; this turn moves it on into a
	// generated file, which DiffFilter drops by ignore-glob. Judged on the FILTERED delta the
	// cancelling addition is gone, so the method looks like a surplus this branch created and the
	// block would tell the reviewer that an established line is not on the base branch. The
	// byte-budget drop (largest file first, once a turn exceeds MaxBytes) is the same hole and the
	// commoner one; the glob is used here only because it is deterministic.
	private const string Established = "public static string Slug(string name) => name.Trim();";

	private static readonly Diff MovedIntoAnIgnoredFile = Diff.Parse(
		"diff --git a/src/B.cs b/src/B.cs\n--- a/src/B.cs\n+++ b/src/B.cs\n@@ -1,2 +1,1 @@\n"
		+ $"-\t{Established}\n"
		+ "diff --git a/src/Gen.g.cs b/src/Gen.g.cs\n--- a/src/Gen.g.cs\n+++ b/src/Gen.g.cs\n@@ -1,1 +1,2 @@\n"
		+ $"+\t{Established}\n");

	private static readonly Diff MovedBaseline = Diff.Parse(
		"diff --git a/src/A.cs b/src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,2 +1,1 @@\n"
		+ $"-\t{Established}\n"
		+ "diff --git a/src/Gen.g.cs b/src/Gen.g.cs\n--- a/src/Gen.g.cs\n+++ b/src/Gen.g.cs\n@@ -1,1 +1,2 @@\n"
		+ $"+\t{Established}\n");

	[Fact]
	public async Task A_file_the_filter_drops_still_cancels_the_removal_it_pays_for()
	{
		var reviewer = new ScriptedReviewer("""{"summary":"s","findings":[]}""");
		await ReviewRunner.RunAsync(new ReviewRunRequest(
			Config, Repo, "sha1", AfterTurnOne,
			(_, _) => Task.FromResult(MovedIntoAnIgnoredFile), reviewer, Baseline: MovedBaseline));

		var user = Msg.User(reviewer.FirstRequest!);

		// The generated file is omitted from the prompt, as it always was...
		Assert.Contains("Gen.g.cs", user);
		Assert.Contains("omitted", user);
		// ...but its addition still cancelled the removal, so nothing is attributed to the branch.
		Assert.DoesNotContain("EARLIER TURN", user);
	}

	private sealed class ScriptedReviewer(params string[] replies) : IReviewer
	{
		private int _calls;

		public ReviewRequest? FirstRequest { get; private set; }

		public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
			Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

		public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
		{
			var n = Interlocked.Increment(ref _calls);
			FirstRequest ??= request;
			return Task.FromResult(ModelReply.Untracked(replies[n - 1 < replies.Length ? n - 1 : replies.Length - 1]));
		}
	}
}
