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
/// The adversarial pass is spent only where it buys something (there are findings to refute) and
/// never costs a review: a skeptic that throws or babbles leaves every finding standing.
/// </summary>
public class VerificationPassTests
{
    private const string Repo = "acme-api";

    private static Persona Persona(string id) => new(
        id, id, "bugs", ReviewTier.Diff, new ModelRef("openrouter", "some/model"), 0.2, "review it");

    private static PeanutConfig Config(bool? verify = null) => new(
        Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
        Personas: [Persona("architect")],
        Repos: [new RepoTarget(Repo, ".")],
        Assignments: [new Assignment("architect", Repo)],
        Verify: verify);

    private static readonly Diff SampleDiff = Diff.Parse(
        "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n");

    private static Task<Diff> Delta(ReviewSession _, CancellationToken __) => Task.FromResult(SampleDiff);

    private const string TwoFindings =
        """{"summary":"s","findings":[{"title":"real bug","body":"b"},{"title":"nit","body":"b"}]}""";

    private static ReviewRunRequest Request(IReviewer reviewer, bool? verify = null) =>
        new(Config(verify), Repo, "sha1", [], Delta, reviewer);

    [Fact]
    public async Task A_refuted_finding_is_not_posted()
    {
        var reviewer = new ScriptedReviewer(
            TwoFindings,
            """{"verdicts":[{"title":"real bug","verdict":"upheld"},{"title":"nit","verdict":"refuted"}]}""");

        var result = await ReviewRunner.RunAsync(Request(reviewer));

        var p = Assert.Single(result.Personas);
        Assert.Equal(1, p.FindingCount);
        Assert.Contains("real bug", p.Body);
        Assert.Contains("dropped on an adversarial second pass", p.Body);
        Assert.Equal(2, reviewer.Calls); // review + verify
    }

    [Fact]
    public async Task A_clean_review_costs_no_extra_call()
    {
        var reviewer = new ScriptedReviewer("""{"summary":"all good","findings":[]}""");

        var result = await ReviewRunner.RunAsync(Request(reviewer));

        Assert.Equal(PersonaOutcome.Reviewed, Assert.Single(result.Personas).Outcome);
        Assert.Equal(1, reviewer.Calls); // nothing to refute, so no second call
    }

    [Fact]
    public async Task A_failing_skeptic_keeps_every_finding()
    {
        // Verification is an enhancement; a failure in it must never cost a real finding.
        var reviewer = new ScriptedReviewer(TwoFindings) { ThrowOnCall = 2 };

        var result = await ReviewRunner.RunAsync(Request(reviewer));

        var p = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Reviewed, p.Outcome); // NOT Failed
        Assert.Equal(2, p.FindingCount);
    }

    [Fact]
    public async Task An_unreadable_verdict_reply_keeps_every_finding()
    {
        var reviewer = new ScriptedReviewer(TwoFindings, "I am not sure about any of these.");

        var result = await ReviewRunner.RunAsync(Request(reviewer));

        Assert.Equal(2, Assert.Single(result.Personas).FindingCount);
    }

    [Fact]
    public async Task Verification_can_be_switched_off()
    {
        var reviewer = new ScriptedReviewer(TwoFindings);

        var result = await ReviewRunner.RunAsync(Request(reviewer, verify: false));

        Assert.Equal(2, Assert.Single(result.Personas).FindingCount);
        Assert.Equal(1, reviewer.Calls);
    }

    [Fact]
    public async Task Verification_is_on_by_default()
    {
        var reviewer = new ScriptedReviewer(
            TwoFindings, """{"verdicts":[{"title":"nit","verdict":"refuted"}]}""");

        await ReviewRunner.RunAsync(Request(reviewer)); // Verify unset

        Assert.Equal(2, reviewer.Calls);
    }

    [Fact]
    public async Task The_refutation_is_logged()
    {
        var lines = new List<string>();
        var reviewer = new ScriptedReviewer(
            TwoFindings, """{"verdicts":[{"title":"nit","verdict":"refuted"}]}""");

        await ReviewRunner.RunAsync(Request(reviewer), log: m => { lock (lines) lines.Add(m); });

        Assert.Contains(lines, l => l.Contains("adversarial pass refuted 1 of 2"));
    }

    [Fact]
    public async Task The_adversarial_pass_reports_its_own_latency_separately()
    {
        // Its cost can dominate a turn; folding it into the review's elapsed time would report
        // "the review was slow" and hide which half was responsible.
        var lines = new List<string>();
        var reviewer = new ScriptedReviewer(TwoFindings, """{"verdicts":[]}""");

        await ReviewRunner.RunAsync(Request(reviewer), log: m => { lock (lines) lines.Add(m); });

        Assert.Contains(lines, l => l.Contains("adversarial pass refuted 0 of 2") && l.Contains("s"));
    }

    private sealed class ScriptedReviewer(params string[] replies) : IReviewer
    {
        private int _calls;

        public int Calls => _calls;

        /// <summary>1-based call index that should throw, simulating a provider failure.</summary>
        public int ThrowOnCall { get; init; }

        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n == ThrowOnCall)
            {
                throw new InvalidOperationException("skeptic exploded");
            }

            return Task.FromResult(ModelReply.Untracked(replies[n - 1 < replies.Length ? n - 1 : replies.Length - 1]));
        }
    }
}
