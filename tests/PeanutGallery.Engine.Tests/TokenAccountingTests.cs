using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// What a run cost, as the provider reported it. The point of carrying this is to settle where the
/// spend goes with accounting rather than argument - so the two things that must hold are that the
/// numbers reach the summary attributed to the right persona, and that the adversarial pass is
/// counted SEPARATELY from the review it checks (it re-sends the whole request, so it is the line
/// item most likely to dominate a turn).
/// </summary>
public class TokenAccountingTests
{
    private const string Repo = "acme-api";

    private static readonly ModelRef Model = new("openrouter", "some/model");

    private static readonly Diff SampleDiff = Diff.Parse(
        "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n");

    private static Task<Diff> Delta(ReviewSession _, CancellationToken __) => Task.FromResult(SampleDiff);

    private static PeanutConfig Config(bool verify) => new(
        Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
        Personas: [new Persona("architect", "The Architect", "architecture", ReviewTier.Diff, Model, 0.2, "review it")],
        Repos: [new RepoTarget(Repo, ".")],
        Assignments: [new Assignment("architect", Repo)],
        Verify: verify);

    private const string OneFinding =
        """{"summary":"s","findings":[{"title":"null deref","file":"a.cs","line":7,"severity":"major"}]}""";

    private const string AllUpheld =
        """{"verdicts":[{"title":"null deref","verdict":"upheld","why":"it holds"}]}""";

    /// <summary>Sentinel reply that makes <see cref="MeteredReviewer"/> throw for that call.</summary>
    private const string Throws = "\u0000throw";

    [Fact]
    public async Task The_review_and_the_adversarial_pass_are_counted_separately()
    {
        // Summing them before anyone sees them makes "is verification worth what it costs"
        // unanswerable - which is the whole question this accounting exists to answer.
        var reviewer = new MeteredReviewer(
            (OneFinding, new ModelUsage(1000, 50)),
            (AllUpheld, new ModelUsage(1200, 20)));

        var run = await ReviewRunner.RunAsync(
            new ReviewRunRequest(Config(verify: true), Repo, "sha1", [], Delta, reviewer));

        var o = Assert.Single(run.Personas).Observability;
        Assert.Equal(new ModelUsage(1000, 50), o.Usage);
        Assert.Equal(new ModelUsage(1200, 20), o.VerifyUsage);
        Assert.Equal(2270, o.TotalUsage.Total);
    }

    [Fact]
    public async Task A_repair_re_ask_is_counted_into_the_review_it_repaired()
    {
        // A model that needs repairing costs roughly double. Hiding that would make the expensive
        // model look as cheap as the well-behaved one.
        var reviewer = new MeteredReviewer(
            ("not json at all", new ModelUsage(900, 40)),
            (OneFinding, new ModelUsage(950, 45)));

        var run = await ReviewRunner.RunAsync(
            new ReviewRunRequest(Config(verify: false), Repo, "sha1", [], Delta, reviewer));

        var o = Assert.Single(run.Personas).Observability;
        Assert.Equal(new ModelUsage(1850, 85), o.Usage);
        Assert.Equal(ModelUsage.Unreported, o.VerifyUsage); // it never ran, so it reported nothing
    }

    // ---- "we were told nothing" is not "it was free" ----

    [Fact]
    public void A_reported_zero_and_an_unreported_call_are_different_facts()
    {
        // A cache hit can legitimately bill 0/0. Collapsing that into "unknown" is exactly as wrong
        // as rendering unknown as free - and this type is the whole reason the summary can tell.
        Assert.False(ModelUsage.Zero.IsUnreported);
        Assert.True(ModelUsage.Unreported.IsUnreported);
        Assert.Equal(0, ModelUsage.Zero.Total);
    }

    [Fact]
    public void Cached_input_tokens_are_a_subset_of_input_not_extra_spend()
    {
        var usage = new ModelUsage(1000, 50, CachedInputTokens: 600);

        Assert.Equal(600, usage.CachedInputTokens);
        Assert.Equal(1050, usage.Total); // Total stays InputTokens + OutputTokens, not + cached
    }

    [Fact]
    public void Cached_input_tokens_sum_across_calls_like_the_other_counts()
    {
        var sum = new ModelUsage(1000, 50, CachedInputTokens: 600) + new ModelUsage(500, 20, CachedInputTokens: 100);

        Assert.Equal(700, sum.CachedInputTokens);
    }

    [Fact]
    public void Unreported_is_the_identity_so_one_silent_call_does_not_erase_a_known_one()
    {
        var sum = ModelUsage.Unreported + new ModelUsage(500, 20) + ModelUsage.Unreported;

        Assert.False(sum.IsUnreported); // partially known beats claiming the run is a mystery
        Assert.Equal(520, sum.Total);
        Assert.True((ModelUsage.Unreported + ModelUsage.Unreported).IsUnreported);
    }

    [Fact]
    public void A_run_that_genuinely_cost_zero_is_reported_as_zero_not_as_unknown()
    {
        var md = RunSummary.RenderStepSummary(
            [Spent("A", ModelUsage.Zero, ModelUsage.Zero)],
            "charles8051/peanut-gallery", 1, "abcdef1234567890");

        Assert.Contains("**Spend:** 0 in / 0 out (0 tokens)", md);
        Assert.DoesNotContain("not reported", md);
    }

    [Fact]
    public async Task A_verify_call_that_threw_reports_nothing_rather_than_a_reported_zero()
    {
        // The call threw, so there is no provider accounting. A reported zero would render "0 / 0"
        // instead of "—", and because + treats reported as sticky it would let this one failure
        // vouch for the whole run's verify column.
        var reviewer = new MeteredReviewer(
            (OneFinding, new ModelUsage(1000, 50)),
            (Throws, ModelUsage.Unreported));

        var run = await ReviewRunner.RunAsync(
            new ReviewRunRequest(Config(verify: true), Repo, "sha1", [], Delta, reviewer));

        var persona = Assert.Single(run.Personas);
        Assert.Equal(PersonaOutcome.Reviewed, persona.Outcome);   // a failed skeptic keeps the review
        Assert.True(persona.Observability.VerifyUsage!.IsUnreported);

        var md = RunSummary.RenderStepSummary([persona], "x/y", 1, "abcdef1234567890");
        Assert.DoesNotContain("adversarial pass is", md);
    }

    [Fact]
    public void A_persona_with_no_metering_at_all_totals_as_unknown_not_as_free()
    {
        var never = new PersonaObservability("openrouter:some/model", TimeSpan.Zero, null);

        Assert.True(never.TotalUsage.IsUnreported);
    }

    [Fact]
    public void A_small_but_real_verification_share_is_not_rounded_away_to_zero()
    {
        var md = RunSummary.RenderStepSummary(
            [Spent("A", new ModelUsage(10_000, 0), new ModelUsage(20, 0))],
            "charles8051/peanut-gallery", 1, "abcdef1234567890");

        Assert.Contains("<1% of it (20 tokens)", md);
    }

    [Fact]
    public async Task A_turn_that_failed_still_reports_what_it_burned()
    {
        // A degraded turn is not a free one, and a persona that fails expensively every push is
        // exactly what this table should make visible.
        var reviewer = new MeteredReviewer(
            ("not json", new ModelUsage(800, 30)),
            ("still not json", new ModelUsage(820, 30)));

        var run = await ReviewRunner.RunAsync(
            new ReviewRunRequest(Config(verify: false), Repo, "sha1", [], Delta, reviewer));

        var persona = Assert.Single(run.Personas);
        Assert.Equal(PersonaOutcome.Failed, persona.Outcome);
        Assert.Equal(1680, persona.Observability.Usage!.Total); // 830 for the try, 850 for the repair
    }

    // ---- the summary ----

    private static PersonaResult Spent(string name, ModelUsage review, ModelUsage verify) =>
        new("id-" + name, name, PersonaOutcome.Reviewed, 1, "body",
            new PersonaObservability("openrouter:some/model", TimeSpan.FromSeconds(30), null, review, verify));

    [Fact]
    public void The_summary_reports_the_run_total_and_the_verification_share()
    {
        var md = RunSummary.RenderStepSummary(
            [Spent("A", new ModelUsage(1000, 100), new ModelUsage(800, 100)),
             Spent("B", new ModelUsage(1000, 100), new ModelUsage(800, 100))],
            "charles8051/peanut-gallery", 1, "abcdef1234567890");

        Assert.Contains("**Spend:** 3600 in / 400 out (4000 tokens)", md);
        Assert.Contains("adversarial pass is 45% of it (1800 tokens)", md);
    }

    [Fact]
    public void The_summary_surfaces_the_cache_hit_share_of_input_tokens()
    {
        var md = RunSummary.RenderStepSummary(
            [Spent("A", new ModelUsage(1000, 100, CachedInputTokens: 600), ModelUsage.Zero)],
            "charles8051/peanut-gallery", 1, "abcdef1234567890");

        Assert.Contains("600 cached", md); // the per-persona token cell
        Assert.Contains("60% of input tokens were cache hits (600)", md); // the run-total spend line
    }

    [Fact]
    public void A_run_with_no_cache_hits_does_not_mention_caching_at_all()
    {
        var md = RunSummary.RenderStepSummary(
            [Spent("A", new ModelUsage(1000, 100), ModelUsage.Zero)],
            "charles8051/peanut-gallery", 1, "abcdef1234567890");

        Assert.DoesNotContain("cached", md);
        Assert.DoesNotContain("cache hit", md);
    }

    [Fact]
    public void A_provider_that_reports_no_usage_says_so_rather_than_claiming_the_run_was_free()
    {
        // Zero tokens and "we were not told" are different facts. Rendering the second as the
        // first is how a cost review reaches a confidently wrong conclusion.
        var md = RunSummary.RenderStepSummary(
            [Spent("A", ModelUsage.Unreported, ModelUsage.Unreported)],
            "charles8051/peanut-gallery", 1, "abcdef1234567890");

        Assert.Contains("Token usage not reported", md);
        Assert.DoesNotContain("**Spend:**", md);
    }

    [Fact]
    public async Task Review_path_attempts_accumulate_onto_the_persona_observability()
    {
        // Each CompleteAsync reports how many times it was issued (retry re-routes). The runner sums
        // them across the review path so the metrics ledger can tell recovered from exhausted. Here
        // the single review call took 3 tries to land -> observability.Attempts is 3.
        var reviewer = new AttemptReviewer(OneFinding, attempts: 3);

        var run = await ReviewRunner.RunAsync(
            new ReviewRunRequest(Config(verify: false), Repo, "sha1", [], Delta, reviewer));

        Assert.Equal(3, Assert.Single(run.Personas).Observability.Attempts);
    }

    [Fact]
    public async Task A_review_that_needs_a_repair_counts_both_calls()
    {
        // The review reply was unparseable and a JSON repair landed it: two model calls on the review
        // path, so attempts is 2 (this is a "multi-call recovered" review in the report).
        var reviewer = new MeteredReviewer(
            ("not json", new ModelUsage(1, 1)),
            (OneFinding, new ModelUsage(1, 1)));

        var run = await ReviewRunner.RunAsync(
            new ReviewRunRequest(Config(verify: false), Repo, "sha1", [], Delta, reviewer));

        var persona = Assert.Single(run.Personas);
        Assert.Equal(PersonaOutcome.Reviewed, persona.Outcome);
        Assert.Equal(2, persona.Observability.Attempts); // review + repair
    }

    [Fact]
    public async Task The_adversarial_pass_call_does_not_count_toward_review_path_attempts()
    {
        // Verify is a separate concern (spec: verify-path attempts are excluded). A clean single
        // review call plus the adversarial pass call must leave attempts at 1, not 2.
        var reviewer = new MeteredReviewer(
            (OneFinding, new ModelUsage(1, 1)),
            (AllUpheld, new ModelUsage(1, 1)));

        var run = await ReviewRunner.RunAsync(
            new ReviewRunRequest(Config(verify: true), Repo, "sha1", [], Delta, reviewer));

        var persona = Assert.Single(run.Personas);
        Assert.Equal(PersonaOutcome.Reviewed, persona.Outcome);
        Assert.Equal(1, persona.Observability.Attempts); // the verify call is not on the review path
    }

    [Fact]
    public async Task An_exhausted_retry_that_throws_still_records_its_attempts()
    {
        // The failure we most want to measure: the call re-routed twice and then gave up (a
        // ModelCallException carrying attempts=3). If the throw path did not count, this exhausted
        // review would report ZERO calls and vanish from the multi-call-recovery denominator.
        var reviewer = new ThrowingAttemptReviewer(attempts: 3);

        var run = await ReviewRunner.RunAsync(
            new ReviewRunRequest(Config(verify: false), Repo, "sha1", [], Delta, reviewer));

        var persona = Assert.Single(run.Personas);
        Assert.Equal(PersonaOutcome.Failed, persona.Outcome);
        Assert.Equal(3, persona.Observability.Attempts); // counted on the throw, not lost
    }

    /// <summary>A reviewer that reports it took <paramref name="attempts"/> tries to land its reply.</summary>
    private sealed class AttemptReviewer(string reply, int attempts) : IReviewer
    {
        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default) =>
            Task.FromResult(new ModelReply(reply, ModelUsage.Unreported, attempts));
    }

    /// <summary>A reviewer whose call exhausts its retries and throws a ModelCallException carrying the count.</summary>
    private sealed class ThrowingAttemptReviewer(int attempts) : IReviewer
    {
        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default) =>
            throw new ModelCallException("upstream error (after 3 attempts)", new HttpRequestException(), attempts);
    }

    /// <summary>A reviewer that hands back a scripted reply plus the usage a provider would report.</summary>
    private sealed class MeteredReviewer(params (string Reply, ModelUsage Usage)[] script) : IReviewer
    {
        private int _calls;

        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            var i = Interlocked.Increment(ref _calls) - 1;
            var (reply, usage) = script[i < script.Length ? i : script.Length - 1];
            if (reply == Throws)
            {
                throw new InvalidOperationException("provider exploded");
            }

            return Task.FromResult(new ModelReply(reply, usage));
        }
    }
}
