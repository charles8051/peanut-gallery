using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// A reply cut off at the output-token cap (finish_reason:length) is incomplete JSON. It must fail
/// cleanly as a Truncated kind — NOT burn a shrink-ladder + JSON-repair on a lost cause (neither can
/// fit a too-long reply into the cap), and NOT be reported as a generic parse error. Screenshot: a
/// minimax-m3 review returned 65,536 output tokens, finish_reason:length.
/// </summary>
public class TruncationTests
{
    private const string Repo = "acme-api";

    private static readonly Diff SampleDiff = Diff.Parse(
        "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n");

    private static Task<Diff> Delta(ReviewSession _, CancellationToken __) => Task.FromResult(SampleDiff);

    private static PeanutConfig Config() => new(
        Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
        Personas: [new Persona("architect", "Architect", "arch", ReviewTier.Diff, new ModelRef("openrouter", "m"), 0.2, "review")],
        Repos: [new RepoTarget(Repo, ".")],
        Assignments: [new Assignment("architect", Repo)],
        Verify: false);

    [Fact]
    public async Task A_truncated_unparseable_reply_fails_cleanly_as_Truncated_without_wasting_calls()
    {
        // Incomplete JSON, and the provider said finish_reason:length.
        var reviewer = new TruncatedReviewer("""{"summary":"s","findings":[{"title":"bug""");

        var run = await ReviewRunner.RunAsync(
            new ReviewRunRequest(Config(), Repo, "sha1", [], Delta, reviewer));

        var p = Assert.Single(run.Personas);
        Assert.Equal(PersonaOutcome.Failed, p.Outcome);
        Assert.Contains("output-token cap", p.Observability.FailureReason ?? "");
        Assert.Equal(1, reviewer.Calls); // no shrink ladder, no JSON repair — one call, then give up

        // The metrics fold reads the structural kind, not the reason prose.
        var metric = Assert.Single(MetricsCollector.From(run, new RunContext("a/b", 1, "s", "t", "fixed")).Personas);
        Assert.Equal(FailureClass.Truncated, metric.Failure);
    }

    [Fact]
    public async Task A_truncated_reply_that_still_parses_is_posted_not_failed()
    {
        // A cap hit right after a complete JSON object is rare but valid — don't discard a good review.
        var reviewer = new TruncatedReviewer("""{"summary":"s","findings":[{"title":"bug","body":"b"}]}""");

        var run = await ReviewRunner.RunAsync(
            new ReviewRunRequest(Config(), Repo, "sha1", [], Delta, reviewer));

        var p = Assert.Single(run.Personas);
        Assert.Equal(PersonaOutcome.Reviewed, p.Outcome);
    }

    /// <summary>Returns its reply flagged truncated (finish_reason:length).</summary>
    private sealed class TruncatedReviewer(string reply) : IReviewer
    {
        private int _calls;

        public int Calls => _calls;

        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ModelReply(reply, ModelUsage.Unreported, Attempts: 1, Truncated: true));
        }
    }
}
