using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// A model can return an empty completion on an over-large prompt (it spends its output budget on
/// reasoning and returns no content). The runner retries with progressively smaller prompts before
/// giving up, turning what would be a total review loss into a smaller-but-real review. This is
/// distinct from the JSON repair, which handles a non-empty reply that ignored the contract.
/// </summary>
public class EmptyReplyRetryTests
{
    private const string Repo = "acme-api";

    private static Persona Persona(string id) => new(
        id, id, "bugs", ReviewTier.Diff, new ModelRef("openrouter", "some/model"), 0.2, "review it");

    private static PeanutConfig Config() => new(
        Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
        Personas: [Persona("architect")],
        Repos: [new RepoTarget(Repo, ".")],
        Assignments: [new Assignment("architect", Repo)],
        Verify: false);

    // A many-file diff comfortably over both retry budgets (48KB / 24KB), mirroring the real case
    // (one observed PR was 17 files / ~100KB). DiffFilter trims by dropping whole files largest-first,
    // so a multi-file diff shrinks to a nonempty subset - which is what makes the retry worthwhile.
    private static readonly Diff BigDiff = MakeDiff(fileCount: 16, bytesPerFile: 4 * 1024);

    // A diff under every budget, so with no context the ladder is empty (nothing to shrink).
    private static readonly Diff SmallDiff = Diff.Parse(
        "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n");

    private static Diff MakeDiff(int fileCount, int bytesPerFile)
    {
        var sb = new StringBuilder();
        for (var file = 0; file < fileCount; file++)
        {
            sb.Append("diff --git a/f").Append(file).Append(".cs b/f").Append(file).Append(".cs\n")
                .Append("--- a/f").Append(file).Append(".cs\n+++ b/f").Append(file).Append(".cs\n@@ -1 +1000 @@\n");
            var start = sb.Length;
            var line = 0;
            while (sb.Length - start < bytesPerFile)
            {
                sb.Append("+    Console.WriteLine(\"file ").Append(file).Append(" line ").Append(line++).Append("\");\n");
            }
        }

        return Diff.Parse(sb.ToString());
    }

    private static ReviewRunRequest Request(IReviewer reviewer, Diff diff) =>
        new(Config(), Repo, "sha1", [], (_, __) => Task.FromResult(diff), reviewer);

    private const string ValidReply = """{"summary":"s","findings":[{"title":"real bug","body":"b"}]}""";

    [Fact]
    public async Task An_empty_reply_on_a_large_diff_is_retried_smaller_and_the_smaller_answer_is_used()
    {
        // Empty first (the 60KB prompt), then a valid reply on the first shrink step (48KB trim).
        var reviewer = new ScriptedReviewer("", ValidReply);

        var result = await ReviewRunner.RunAsync(Request(reviewer, BigDiff));

        var p = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Reviewed, p.Outcome);
        Assert.Equal(1, p.FindingCount);
        Assert.Equal(2, reviewer.Calls); // original (empty) + one smaller retry (parsed)

        // The retry must actually be smaller - and still carry real diff content, not an empty diff.
        Assert.Equal(2, reviewer.PromptSizes.Count);
        Assert.True(reviewer.PromptSizes[1] < reviewer.PromptSizes[0], "retry prompt should be smaller");
        Assert.True(reviewer.PromptSizes[1] > 1024, "retry prompt should still carry a trimmed diff");
        // Content, not just size: the trimmed prompt still contains a surviving file's diff.
        Assert.Contains(".cs", reviewer.LastUserText);
        Assert.Contains("Console.WriteLine", reviewer.LastUserText);
    }

    [Fact]
    public async Task An_empty_reply_then_a_malformed_retry_falls_through_to_the_json_repair()
    {
        // Empty on the original (ladder fires), a non-empty MALFORMED reply on the first shrink step
        // (breaks the ladder - shrinking a format problem won't help), then the JSON repair fixes it.
        // Calls: original + one shrink retry + JSON repair = 3.
        var reviewer = new ScriptedReviewer("", "Looks good to me!", ValidReply);

        var result = await ReviewRunner.RunAsync(Request(reviewer, BigDiff));

        var p = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Reviewed, p.Outcome);
        Assert.Equal(1, p.FindingCount);
        Assert.Equal(3, reviewer.Calls);
    }

    [Fact]
    public async Task Cancellation_between_retry_iterations_aborts_the_run_it_is_not_a_failed_persona()
    {
        // The persona catch is `when (e is not OperationCanceledException)`, so a cancel during a
        // retry must propagate out of RunAsync (the review was aborted), NOT be swallowed into a
        // false "failed persona" that would look like the model choked.
        using var cts = new CancellationTokenSource();
        var reviewer = new CancelOnRetryReviewer(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReviewRunner.RunAsync(Request(reviewer, BigDiff), ct: cts.Token));

        Assert.Equal(2, reviewer.Calls); // original (empty) + the retry that observed the cancel
    }

    [Fact]
    public async Task If_the_model_throws_during_a_shrink_retry_the_persona_fails_gracefully()
    {
        // Empty on the original, then the retry call throws. The runner's own try/catch must turn
        // that into a failed persona (retried next push), not an unhandled exception out of RunAsync.
        var reviewer = new EmptyThenThrowReviewer();

        var result = await ReviewRunner.RunAsync(Request(reviewer, BigDiff));

        var p = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Failed, p.Outcome);
        Assert.Equal(2, reviewer.Calls); // original (empty) + the throwing retry
    }

    [Fact]
    public async Task It_keeps_shrinking_while_the_model_keeps_returning_empty_then_falls_to_the_json_repair()
    {
        // Empty on the original AND both trim steps, then the JSON repair also empty -> the persona
        // fails rather than posting a clean review. Calls: original + 48KB + 24KB + JSON repair = 4.
        var reviewer = new ScriptedReviewer("", "", "", "");

        var result = await ReviewRunner.RunAsync(Request(reviewer, BigDiff));

        var p = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Failed, p.Outcome);
        Assert.Equal(4, reviewer.Calls);
    }

    [Fact]
    public async Task A_small_diff_with_nothing_to_shrink_does_not_retry_smaller()
    {
        // No context, tiny diff -> the ladder is empty, so an empty reply goes straight to the JSON
        // repair (one re-ask), never a shrink retry. Guards against burning calls when it can't help.
        var reviewer = new ScriptedReviewer("", "");

        var result = await ReviewRunner.RunAsync(Request(reviewer, SmallDiff));

        var p = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Failed, p.Outcome);
        Assert.Equal(2, reviewer.Calls); // original + JSON repair only; no shrink step
    }

    [Fact]
    public async Task A_non_empty_unreadable_reply_is_not_shrunk_it_goes_straight_to_the_json_repair()
    {
        // Prose (non-empty) on a large diff must NOT trigger the shrink ladder - shrinking a format
        // problem just burns calls. One JSON repair, then success on the repaired answer.
        var reviewer = new ScriptedReviewer("Looks good to me!", ValidReply);

        var result = await ReviewRunner.RunAsync(Request(reviewer, BigDiff));

        var p = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Reviewed, p.Outcome);
        Assert.Equal(2, reviewer.Calls); // original (prose) + JSON repair, no shrink retries
    }

    /// <summary>Returns the scripted replies in order; repeats the last one once exhausted.</summary>
    private sealed class ScriptedReviewer(params string[] replies) : IReviewer
    {
        private int _calls;

        public int Calls => _calls;

        /// <summary>Total prompt character count per call, in order - lets a test assert a retry shrank.</summary>
        public List<int> PromptSizes { get; } = [];

        /// <summary>The user-role text of the most recent call - lets a test assert on prompt content.</summary>
        public string LastUserText { get; private set; } = string.Empty;

        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            var i = Interlocked.Increment(ref _calls) - 1;
            PromptSizes.Add(request.Messages.Sum(m => m.Content.Length));
            LastUserText = request.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? string.Empty;
            return Task.FromResult(ModelReply.Untracked(replies[i < replies.Length ? i : replies.Length - 1]));
        }
    }

    /// <summary>Empty on the first call, then cancels and observes the token on the retry - to prove
    /// a cancel mid-ladder aborts the run instead of being recorded as a failed persona.</summary>
    private sealed class CancelOnRetryReviewer(CancellationTokenSource cts) : IReviewer
    {
        private int _calls;

        public int Calls => _calls;

        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n == 1)
            {
                cts.Cancel();   // the caller's token is now cancelled for the retry
                return Task.FromResult(ModelReply.Untracked(string.Empty));
            }

            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ModelReply.Untracked(ValidReply));
        }
    }

    /// <summary>Returns an empty reply first, then throws - to prove a throwing retry fails the persona.</summary>
    private sealed class EmptyThenThrowReviewer : IReviewer
    {
        private int _calls;

        public int Calls => _calls;

        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n == 1)
            {
                return Task.FromResult(ModelReply.Untracked(string.Empty));
            }

            throw new HttpRequestException("provider blew up mid-retry");
        }
    }
}
