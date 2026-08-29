using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// One slow reviewer must not be able to cost the panel its finished work.
///
/// <para>Both halves of one observed run, where a hung persona ran past the job's 15-minute backstop
/// and took three completed reviews (13 findings) down with it, twice: comments are published as
/// each persona lands (#116), and a persona's whole turn shares ONE deadline that covers every
/// model call and retry it makes (#117).</para>
/// </summary>
public class ReviewResilienceTests
{
    private const string Repo = "acme-api";
    private const string Slow = "slowpoke";

    /// <summary>Short enough to keep the suite fast, long enough that the fast personas never race it.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(400);

    private static readonly ModelRef Model = new("openrouter", "some/model");

    // The system prompt carries the id, which is the only handle a fixture reviewer has on which
    // persona a request belongs to.
    private static Persona P(string id) => new(id, id, id, ReviewTier.Diff, Model, 0.2, $"you are {id}");

    private static PeanutConfig Config(CommentMode? comment, params string[] ids) => new(
        Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
        Personas: ids.Select(P).ToList(),
        Repos: [new RepoTarget(Repo, ".")],
        Assignments: ids.Select(id => new Assignment(id, Repo)).ToList(),
        Verify: false,
        Comment: comment);

    private static readonly Diff SampleDiff = Diff.Parse(
        "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n");

    private static Task<Diff> Delta(ReviewSession _, CancellationToken __) => Task.FromResult(SampleDiff);

    private const string OneFinding =
        """{"summary":"s","findings":[{"title":"null deref","file":"a.cs","line":7,"severity":"major"}]}""";

    private static ReviewRunRequest Request(
        PeanutConfig config, IReviewer reviewer,
        Func<IReadOnlyList<string>, CancellationToken, Task>? publish = null,
        TimeSpan? budget = null) =>
        new(config, Repo, "sha1", [], Delta, reviewer, Publish: publish, PersonaBudget: budget ?? Budget);

    // ---- #116: publish as each persona lands ------------------------------------------------

    [Fact]
    public async Task A_finished_persona_is_published_while_a_slow_colleague_is_still_running()
    {
        // The heart of the bug: with an end-of-run write, NOTHING is on the PR at this moment, so
        // killing the job here loses every finding the fast personas produced.
        using var release = new SemaphoreSlim(0, 1);
        var reviewer = new GatedReviewer(Slow, release);
        var published = new ConcurrentQueue<string>();

        var run = ReviewRunner.RunAsync(Request(
            Config(CommentMode.PerPersona, "architect", "bug-hunter", Slow),
            reviewer,
            publish: (bodies, _) => { foreach (var b in bodies) published.Enqueue(b); return Task.CompletedTask; },
            budget: TimeSpan.FromSeconds(30)));   // long: this test is about ordering, not deadlines

        await WaitUntil(() => published.Count >= 2);
        Assert.Equal(2, published.Count);        // published BEFORE the panel finished

        release.Release();
        var result = await run;
        Assert.Equal(3, result.Personas.Count);
        Assert.All(result.Personas, p => Assert.Equal(PersonaOutcome.Reviewed, p.Outcome));
    }

    [Fact]
    public async Task Each_published_body_is_that_personas_own_comment_in_per_persona_mode()
    {
        var published = new ConcurrentQueue<string>();
        await ReviewRunner.RunAsync(Request(
            Config(CommentMode.PerPersona, "architect", "bug-hunter"),
            new ScriptedReviewer(OneFinding),
            publish: (bodies, _) => { foreach (var b in bodies) published.Enqueue(b); return Task.CompletedTask; }));

        // One publish per persona, each carrying only that persona's marker.
        Assert.Equal(2, published.Count);
        var markers = published.Select(CommentSync.PersonaIdOf).ToList();
        Assert.Contains("architect", markers);
        Assert.Contains("bug-hunter", markers);
    }

    [Fact]
    public async Task A_partial_panel_says_it_is_still_running_instead_of_claiming_the_head_is_reviewed()
    {
        // "Reviewed through <sha>" is what humans and polling agents read as "this review is
        // complete". A partial panel has not earned it and must not print it.
        using var release = new SemaphoreSlim(0, 1);
        var reviewer = new GatedReviewer(Slow, release);
        var published = new ConcurrentQueue<string>();

        var run = ReviewRunner.RunAsync(Request(
            Config(CommentMode.Panel, "architect", Slow),
            reviewer,
            publish: (bodies, _) => { foreach (var b in bodies) published.Enqueue(b); return Task.CompletedTask; },
            budget: TimeSpan.FromSeconds(30)));

        await WaitUntil(() => !published.IsEmpty);
        var partial = published.First();
        Assert.Contains("still running", partial);
        Assert.DoesNotContain("Reviewed through", partial);
        Assert.Contains("still reviewing", partial);   // the persona that has not landed is named

        release.Release();
        var result = await run;
        Assert.NotNull(result.PanelBody);
        Assert.Contains("Reviewed through", result.PanelBody);
        Assert.DoesNotContain("still running", result.PanelBody);
    }

    [Fact]
    public async Task The_last_persona_does_not_publish_a_panel_the_end_of_run_write_is_about_to_repeat()
    {
        var published = new ConcurrentQueue<string>();
        var result = await ReviewRunner.RunAsync(Request(
            Config(CommentMode.Panel, "architect", "bug-hunter"),
            new ScriptedReviewer(OneFinding),
            publish: (bodies, _) => { foreach (var b in bodies) published.Enqueue(b); return Task.CompletedTask; }));

        // Two personas -> exactly one partial (after the first lands). The complete panel is the
        // caller's end-of-run write.
        Assert.Single(published);
        Assert.Contains("still running", published.First());
        Assert.NotNull(result.PanelBody);
    }

    [Fact]
    public async Task Concurrent_completions_publish_in_order_never_an_older_panel_after_a_newer_one()
    {
        // The single-writer invariant, exercised rather than asserted in a comment: two personas
        // complete while a third is gated, so two partial panels are rendered under contention. Each
        // must show strictly FEWER reviewers outstanding than the one before it - a render that
        // escaped the lock could publish the 2-pending panel after the 1-pending one and leave the
        // PR showing a review going backwards.
        using var release = new SemaphoreSlim(0, 1);
        var reviewer = new GatedReviewer(Slow, release);
        var published = new ConcurrentQueue<string>();

        var run = ReviewRunner.RunAsync(Request(
            Config(CommentMode.Panel, "architect", "bug-hunter", Slow),
            reviewer,
            publish: (bodies, _) => { foreach (var b in bodies) published.Enqueue(b); return Task.CompletedTask; },
            budget: TimeSpan.FromSeconds(30)));

        await WaitUntil(() => published.Count >= 2);
        release.Release();
        await run;

        var outstanding = published.Select(CountStillReviewing).ToList();
        Assert.Equal([2, 1], outstanding);
    }

    [Fact]
    public async Task A_publish_that_throws_leaves_the_thread_untouched_so_the_closing_write_still_creates()
    {
        // The failure semantics the CLI shell depends on: the ledger is advanced only AFTER a write
        // lands, so a thrown post leaves nothing recorded and the closing write creates the comment
        // rather than trying to update one that was never made.
        var ledger = CommentLedger.From([]);
        long nextId = 100;
        var failNext = true;

        Task Post(IReadOnlyList<string> bodies, CancellationToken _)
        {
            foreach (var op in ledger.Plan(bodies))
            {
                if (failNext)
                {
                    failNext = false;
                    throw new InvalidOperationException("GitHub said no");
                }

                ledger = ledger.Record(op, op.Action == UpsertAction.Update ? op.CommentId!.Value : nextId++);
            }

            return Task.CompletedTask;
        }

        var run = await ReviewRunner.RunAsync(Request(
            Config(CommentMode.PerPersona, "architect"), new ScriptedReviewer(OneFinding), publish: Post));

        Assert.Empty(ledger.Comments);   // the throw recorded nothing
        await Post(run.RenderedBodies, CancellationToken.None);
        var comment = Assert.Single(ledger.Comments);
        Assert.Equal(100, comment.Id);
        Assert.Equal("architect", CommentSync.PersonaIdOf(comment.Body));
    }

    private static int CountStillReviewing(string body)
    {
        var count = 0;
        var i = body.IndexOf("still reviewing", StringComparison.Ordinal);
        while (i >= 0)
        {
            count++;
            i = body.IndexOf("still reviewing", i + 1, StringComparison.Ordinal);
        }

        return count;
    }

    [Theory]
    [InlineData(CommentMode.PerPersona, 2)]
    [InlineData(CommentMode.Panel, 1)]
    public async Task Publishing_early_and_again_at_the_end_leaves_no_duplicate_comments(
        CommentMode mode, int expectedComments)
    {
        // The composition the CLI shell implements: every write - the incremental ones and the
        // closing one - goes through a CommentLedger over the same thread. This is the regression
        // guard for the obvious way incremental posting goes wrong.
        var ledger = CommentLedger.From([]);
        long nextId = 100;
        var writes = 0;

        Task Post(IReadOnlyList<string> bodies, CancellationToken _)
        {
            foreach (var op in ledger.Plan(bodies))
            {
                var id = op.Action == UpsertAction.Update ? op.CommentId!.Value : nextId++;
                ledger = ledger.Record(op, id);
                writes++;
            }

            return Task.CompletedTask;
        }

        var run = await ReviewRunner.RunAsync(Request(
            Config(mode, "architect", "bug-hunter"), new ScriptedReviewer(OneFinding), publish: Post));
        await Post(run.RenderedBodies, CancellationToken.None);

        Assert.Equal(expectedComments, ledger.Comments.Count);
        Assert.Equal(expectedComments, ledger.Comments.Select(c => CommentSync.MarkerOf(c.Body)).Distinct().Count());

        // Two writes either way, and neither is a duplicate. Per-persona: both bodies published as
        // they landed, the closing write a pure no-op. Panel: one partial, then the closing write
        // replacing it in place.
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task A_publish_that_throws_is_logged_and_never_sinks_the_run()
    {
        var lines = new List<string>();
        var result = await ReviewRunner.RunAsync(
            Request(
                Config(CommentMode.PerPersona, "architect"),
                new ScriptedReviewer(OneFinding),
                publish: (_, _) => throw new InvalidOperationException("GitHub said no")),
            log: m => { lock (lines) lines.Add(m); });

        var p = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Reviewed, p.Outcome);
        Assert.Contains(lines, l => l.Contains("[publish]") && l.Contains("GitHub said no"));
    }

    [Fact]
    public async Task No_publish_seam_means_the_old_end_of_run_behaviour_unchanged()
    {
        var result = await ReviewRunner.RunAsync(Request(
            Config(CommentMode.Panel, "architect", "bug-hunter"), new ScriptedReviewer(OneFinding)));

        Assert.NotNull(result.PanelBody);
        Assert.Contains("Reviewed through", result.PanelBody);
    }

    // ---- #117: one deadline per persona turn ------------------------------------------------

    [Fact]
    public async Task A_persona_that_blows_its_budget_fails_alone()
    {
        using var never = new SemaphoreSlim(0, 1);
        var result = await ReviewRunner.RunAsync(Request(
            Config(CommentMode.PerPersona, "architect", "bug-hunter", Slow),
            new GatedReviewer(Slow, never)));

        var hung = result.Personas.Single(p => p.PersonaId == Slow);
        Assert.Equal(PersonaOutcome.Failed, hung.Outcome);
        Assert.Contains("did not finish within its", hung.Body!);
        Assert.Contains("budget", hung.Observability.FailureReason!);

        // The point of the whole exercise: the other two are untouched and still have bodies to post.
        Assert.All(
            result.Personas.Where(p => p.PersonaId != Slow),
            p => Assert.Equal(PersonaOutcome.Reviewed, p.Outcome));
        Assert.Equal(3, result.RenderedBodies.Count);

        // ...and the structural signal is set: the shell caught the TimeoutException, so the metrics
        // fold reads FailureClass.Timeout without the core parsing the budget prose (#123/#133).
        Assert.Equal(FailureClass.Timeout, hung.Observability.FailureKind);
    }

    // ---- #133: the per-CALL timeout is classified structurally, not from message text -------------

    [Theory]
    [InlineData(true)]   // exhausted retries -> ModelCallException wrapping the TimeoutException
    [InlineData(false)]  // a single attempt   -> the raw TimeoutException
    public async Task A_per_call_timeout_failure_is_tagged_Timeout_structurally(bool wrapped)
    {
        // The per-call TimeBox exhaustion surfaces from CompleteAsync as a TimeoutException (or a
        // ModelCallException around one after RetryingModelCall.Enrich). ReviewRunner must record
        // FailureClass.Timeout structurally so the ledger reads it as a timeout, NOT Other - the very
        // misclassification observed post-#133. Nothing in the core parses the message to get there.
        var result = await ReviewRunner.RunAsync(Request(
            Config(CommentMode.PerPersona, "architect"),
            new ThrowingReviewer(wrapped)));

        var failed = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Failed, failed.Outcome);
        Assert.Equal(FailureClass.Timeout, failed.Observability.FailureKind);
    }

    [Theory]
    [InlineData(true)]   // exhausted retries -> ModelCallException wrapping the ArgumentOutOfRangeException
    [InlineData(false)]  // a single fatal attempt -> the raw exception
    public async Task A_reply_the_sdk_could_not_map_is_tagged_MalformedResponse_structurally(bool wrapped)
    {
        // #158: a completion with no choices surfaces from CompleteAsync as a MalformedResponseException.
        // It must not land in Other - that is precisely how it stayed invisible for a week while
        // failing whole panels across two repos.
        var malformed = new MalformedResponseException("no choices", new ArgumentOutOfRangeException("index"));
        var result = await ReviewRunner.RunAsync(Request(
            Config(CommentMode.PerPersona, "architect"),
            new ThrowingReviewer(wrapped: false, ex: wrapped
                ? new ModelCallException("no choices (after 2 attempts)", malformed, 2)
                : malformed)));

        var failed = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Failed, failed.Outcome);
        Assert.Equal(FailureClass.MalformedResponse, failed.Observability.FailureKind);
    }

    [Fact]
    public async Task An_out_of_range_index_from_our_own_code_is_not_blamed_on_the_provider()
    {
        // The bound on the tag above: only the call boundary may name a reply malformed. A bug of
        // ours that surfaces with the same exception shape anywhere else in the turn must keep NO
        // structural kind, so it falls to the text classifier instead of being reported as a
        // provider fault - and, upstream of here, is never retried.
        var result = await ReviewRunner.RunAsync(Request(
            Config(CommentMode.PerPersona, "architect"),
            new ThrowingReviewer(wrapped: false, ex: new ArgumentOutOfRangeException("index"))));

        var failed = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Failed, failed.Outcome);
        Assert.Null(failed.Observability.FailureKind);
    }

    [Fact]
    public async Task A_non_timeout_failure_carries_no_structural_kind_and_falls_to_the_text_classifier()
    {
        // The structural tag is timeout-specific: a provider error is left for FailureClassifier to
        // bucket from its text, so we do not silently relabel every failure a timeout.
        var result = await ReviewRunner.RunAsync(Request(
            Config(CommentMode.PerPersona, "architect"),
            new ThrowingReviewer(wrapped: false, ex: new InvalidOperationException("provider said no"))));

        var failed = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Failed, failed.Outcome);
        Assert.Null(failed.Observability.FailureKind);
    }

    [Fact]
    public async Task The_budget_is_spent_across_a_turns_calls_not_reset_by_each_one()
    {
        // The actual #117 defect: every model call (and every retry inside it) got its own fresh
        // budget, so a turn could run for a multiple of the number an operator set. Here each call
        // comfortably fits the budget on its own and returns an unusable reply, so the turn re-asks
        // - and the SECOND call must run into the turn's deadline. Under the old per-call semantics
        // both calls would have been allowed and the persona would have failed on the parse instead.
        var reviewer = new SlowUnreadableReviewer(TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();
        var result = await ReviewRunner.RunAsync(Request(
            Config(CommentMode.PerPersona, "architect"), reviewer, budget: TimeSpan.FromMilliseconds(500)));
        sw.Stop();

        var p = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Failed, p.Outcome);
        Assert.Contains("did not finish within its", p.Body!);
        Assert.True(reviewer.Calls > 1, $"expected the turn to make several calls, made {reviewer.Calls}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"the turn ran {sw.Elapsed} - the deadline did not bound it");
    }

    [Fact]
    public async Task A_review_inside_its_budget_is_untouched()
    {
        var result = await ReviewRunner.RunAsync(Request(
            Config(CommentMode.PerPersona, "architect"), new ScriptedReviewer(OneFinding),
            budget: TimeSpan.FromSeconds(30)));

        var p = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Reviewed, p.Outcome);
        Assert.Equal(1, p.FindingCount);
    }

    [Fact]
    public async Task Outer_cancellation_is_still_cancellation_not_a_blown_budget()
    {
        // The distinction TimeBox exists for: the caller tearing the run down must propagate, not
        // be laundered into "this persona was slow".
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReviewRunner.RunAsync(
            Request(Config(CommentMode.PerPersona, "architect"), new CancelObservingReviewer()),
            ct: cts.Token));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the expected progress");
            await Task.Delay(10);
        }
    }

    /// <summary>Answers every persona immediately except one, which blocks until released (or the
    /// turn's deadline cancels it) - a stand-in for the hung OpenRouter route.</summary>
    private sealed class GatedReviewer(string hangingPersonaId, SemaphoreSlim release) : IReviewer
    {
        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public async Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            var system = request.Messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content ?? string.Empty;
            if (system.Contains(hangingPersonaId, StringComparison.Ordinal))
            {
                await release.WaitAsync(ct);
            }

            return ModelReply.Untracked(OneFinding);
        }
    }

    /// <summary>Every call is slow-but-fine and unreadable, so the turn keeps re-asking: proves the
    /// deadline spans a turn's calls rather than restarting with each one.</summary>
    private sealed class SlowUnreadableReviewer(TimeSpan perCall) : IReviewer
    {
        private int _calls;

        public int Calls => _calls;

        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public async Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(perCall, ct);
            return ModelReply.Untracked("not json at all");
        }
    }

    private sealed class CancelObservingReviewer : IReviewer
    {
        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ModelReply.Untracked(OneFinding));
        }
    }

    private sealed class ScriptedReviewer(params string[] replies) : IReviewer
    {
        private int _calls;

        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            var i = Interlocked.Increment(ref _calls) - 1;
            return Task.FromResult(ModelReply.Untracked(replies[i < replies.Length ? i : replies.Length - 1]));
        }
    }

    // CompleteAsync throws the exception a per-call TimeBox exhaustion (or a provider error) would,
    // to exercise ReviewRunner's structural failure classification without a real clock or network.
    private sealed class ThrowingReviewer(bool wrapped, Exception? ex = null) : IReviewer
    {
        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            var toThrow = ex ?? (wrapped
                ? new ModelCallException("operation timed out after 180s (after 2 attempts)", new TimeoutException("operation timed out after 180s"), 2)
                : new TimeoutException("operation timed out after 180s"));
            return Task.FromException<ModelReply>(toThrow);
        }
    }
}
