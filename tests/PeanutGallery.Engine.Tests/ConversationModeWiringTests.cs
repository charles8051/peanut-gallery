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
/// The branch table from the conversation-modes spec: what a comment costs, and the rule that a
/// push always outranks bookkeeping. Call counts are the assertion that matters here — the point of
/// the feature is that one sentence of English stops waking four reviewers.
/// </summary>
public class ConversationModeWiringTests
{
    private const string Repo = "acme-api";

    private static readonly ModelRef Model = new("openrouter", "some/model");

    private static Persona P(string id) => new(
        id, id, id, ReviewTier.Diff, Model, 0.2, $"review it, you are {id}");

    private static PeanutConfig Config(ConversationPolicy? conversation, params string[] ids) => new(
        Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
        Personas: ids.Select(P).ToList(),
        Repos: [new RepoTarget(Repo, ".")],
        Assignments: ids.Select(id => new Assignment(id, Repo)).ToList(),
        Verify: false,
        Comment: CommentMode.Panel,
        Conversation: conversation);

    private static readonly Diff SampleDiff = Diff.Parse(
        "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n");

    private static Task<Diff> Delta(ReviewSession _, CancellationToken __) => Task.FromResult(SampleDiff);

    private const string OneFinding =
        """{"summary":"s","findings":[{"title":"null deref","file":"a.cs","line":7,"severity":"major"}]}""";

    private const string WithdrawIt = """{"withdrawn":["null deref"],"resolved":[]}""";

    /// <summary>A first turn at sha1, returned as the existing panel comment for the next run.</summary>
    private static async Task<ExistingComment[]> ReviewedOnce(PeanutConfig config, CountingReviewer reviewer)
    {
        var first = await ReviewRunner.RunAsync(
            new ReviewRunRequest(config, Repo, "sha1", [], Delta, reviewer));
        return [new ExistingComment(1, first.RenderedBodies[0], "github-actions", IsBot: true)];
    }

    private static ExistingComment[] Plus(ExistingComment[] existing, params string[] bodies) =>
        [.. existing, .. bodies.Select((b, i) => new ExistingComment(100 + i, b, "charles8051", IsBot: false))];

    // ---- panel mode (the default) is unchanged ----

    [Fact]
    public async Task An_unset_policy_still_wakes_the_whole_panel_for_a_comment()
    {
        // This feature is additive: a config that never heard of it behaves exactly as before.
        var config = Config(null, "architect", "bug-hunter");
        var reviewer = new CountingReviewer(OneFinding);
        var existing = await ReviewedOnce(config, reviewer);
        var before = reviewer.Calls;

        var run = await ReviewRunner.RunAsync(new ReviewRunRequest(
            config, Repo, "sha1", Plus(existing, "this is intentional"), Delta, reviewer));

        Assert.Equal(2, reviewer.Calls - before); // one per persona
        Assert.Equal(0, run.Unchanged);
    }

    // ---- the mention gate ----

    [Fact]
    public async Task An_unaddressed_comment_costs_nothing()
    {
        var config = Config(new ConversationPolicy(Mentions: ["@peanut-gallery"]), "architect", "bug-hunter");
        var reviewer = new CountingReviewer(OneFinding);
        var existing = await ReviewedOnce(config, reviewer);
        var before = reviewer.Calls;

        var run = await ReviewRunner.RunAsync(new ReviewRunRequest(
            config, Repo, "sha1", Plus(existing, "agreed, let's ship it"), Delta, reviewer));

        Assert.Equal(0, reviewer.Calls - before);
        Assert.Equal(2, run.Unchanged);
        Assert.Contains("null deref", run.RenderedBodies[0]); // and the board survives the re-render
    }

    [Fact]
    public async Task An_addressed_comment_still_gets_a_turn()
    {
        var config = Config(new ConversationPolicy(Mentions: ["@peanut-gallery"]), "architect");
        var reviewer = new CountingReviewer(OneFinding);
        var existing = await ReviewedOnce(config, reviewer);
        var before = reviewer.Calls;

        await ReviewRunner.RunAsync(new ReviewRunRequest(
            config, Repo, "sha1", Plus(existing, "@peanut-gallery that is intentional"), Delta, reviewer));

        Assert.Equal(1, reviewer.Calls - before);
    }

    // ---- off ----

    [Fact]
    public async Task Off_means_a_comment_never_triggers_a_turn()
    {
        var config = Config(new ConversationPolicy(ConversationMode.Off), "architect", "bug-hunter");
        var reviewer = new CountingReviewer(OneFinding);
        var existing = await ReviewedOnce(config, reviewer);
        var before = reviewer.Calls;

        var run = await ReviewRunner.RunAsync(new ReviewRunRequest(
            config, Repo, "sha1", Plus(existing, "@peanut-gallery please withdraw that"), Delta, reviewer));

        Assert.Equal(0, reviewer.Calls - before);
        Assert.Equal(2, run.Unchanged);
    }

    // ---- reconcile ----

    [Fact]
    public async Task Reconcile_spends_exactly_one_call_for_a_whole_panel()
    {
        var config = Config(new ConversationPolicy(ConversationMode.Reconcile), "architect", "bug-hunter");
        var reviewer = new CountingReviewer(OneFinding) { ThenReply = WithdrawIt };
        var existing = await ReviewedOnce(config, reviewer);
        var before = reviewer.Calls;

        var run = await ReviewRunner.RunAsync(new ReviewRunRequest(
            config, Repo, "sha1", Plus(existing, "that is intentional"), Delta, reviewer));

        Assert.Equal(1, reviewer.Calls - before); // one, not one per persona
        var body = run.RenderedBodies[0];
        Assert.Contains("_No findings._", body);                          // taken off the board
        Assert.Contains("**Withdrawn (author-explained):** null deref", body); // and named, not silently dropped
        Assert.Contains("without re-running the panel", body);            // as a conversation turn
    }

    [Fact]
    public async Task A_reconciled_withdrawal_is_remembered_so_the_next_push_does_not_re_raise_it()
    {
        var config = Config(new ConversationPolicy(ConversationMode.Reconcile), "architect");
        var reviewer = new CountingReviewer(OneFinding) { ThenReply = WithdrawIt };
        var existing = await ReviewedOnce(config, reviewer);

        var run = await ReviewRunner.RunAsync(new ReviewRunRequest(
            config, Repo, "sha1", Plus(existing, "that is intentional"), Delta, reviewer));

        var session = PanelSessionCodec.Extract(run.RenderedBodies[0])!.For("architect");
        Assert.Contains("null deref", session.DroppedTitles);
        Assert.Empty(session.OpenFindings);
    }

    [Fact]
    public async Task A_push_outranks_a_comment_so_the_panel_reviews_and_reads_it_in_one_turn()
    {
        // Paying for a reconciliation AND a review would be worse than what this replaces.
        var config = Config(new ConversationPolicy(ConversationMode.Reconcile), "architect", "bug-hunter");
        var reviewer = new CountingReviewer(OneFinding);
        var existing = await ReviewedOnce(config, reviewer);
        var before = reviewer.Calls;

        await ReviewRunner.RunAsync(new ReviewRunRequest(
            config, Repo, "sha2", Plus(existing, "that is intentional"), Delta, reviewer));

        Assert.Equal(2, reviewer.Calls - before); // a review each, no separate reconciliation
    }

    [Fact]
    public async Task An_unreadable_reconciliation_leaves_every_finding_on_the_board()
    {
        // This pass only removes, so a failure to read it must remove nothing.
        var config = Config(new ConversationPolicy(ConversationMode.Reconcile), "architect");
        var reviewer = new CountingReviewer(OneFinding) { ThenReply = "I'm afraid I can't do that" };
        var existing = await ReviewedOnce(config, reviewer);

        var run = await ReviewRunner.RunAsync(new ReviewRunRequest(
            config, Repo, "sha1", Plus(existing, "that is intentional"), Delta, reviewer));

        Assert.Contains("null deref", run.RenderedBodies[0]);
    }

    [Fact]
    public async Task A_reconciler_that_throws_leaves_every_finding_on_the_board()
    {
        var config = Config(new ConversationPolicy(ConversationMode.Reconcile), "architect");
        var reviewer = new CountingReviewer(OneFinding) { ThenThrow = true };
        var existing = await ReviewedOnce(config, reviewer);

        var run = await ReviewRunner.RunAsync(new ReviewRunRequest(
            config, Repo, "sha1", Plus(existing, "that is intentional"), Delta, reviewer));

        Assert.Contains("null deref", run.RenderedBodies[0]);
    }

    [Fact]
    public async Task Reconcile_degrades_to_the_full_panel_when_comments_are_per_persona()
    {
        // ConfigValidation flags the combination; the runtime must not silently do nothing.
        var config = Config(new ConversationPolicy(ConversationMode.Reconcile), "architect") with
        {
            Comment = CommentMode.PerPersona,
        };
        var reviewer = new CountingReviewer(OneFinding);
        var first = await ReviewRunner.RunAsync(
            new ReviewRunRequest(config, Repo, "sha1", [], Delta, reviewer));
        var existing = new[] { new ExistingComment(1, first.RenderedBodies[0], "bot", IsBot: true) };
        var before = reviewer.Calls;

        await ReviewRunner.RunAsync(new ReviewRunRequest(
            config, Repo, "sha1", Plus(existing, "that is intentional"), Delta, reviewer));

        Assert.Equal(1, reviewer.Calls - before); // the persona took a full turn
    }

    /// <summary>Counts calls; answers <see cref="ThenReply"/> (or throws) after the first turn.</summary>
    private sealed class CountingReviewer(string reply) : IReviewer
    {
        private int _calls;

        public int Calls => _calls;

        public string? ThenReply { get; init; }

        public bool ThenThrow { get; init; }


        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);

            // A reconciliation request is identifiable by its system prompt.
            if (Msg.System(request).Contains("never raise findings", StringComparison.Ordinal))
            {
                if (ThenThrow)
                {
                    throw new InvalidOperationException("reconciler exploded");
                }

                return Task.FromResult(ModelReply.Untracked(ThenReply ?? """{"withdrawn":[],"resolved":[]}"""));
            }

            return Task.FromResult(ModelReply.Untracked(reply));
        }
    }
}
