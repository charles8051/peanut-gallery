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
/// The orchestrator is one model call wrapped in the pure pipeline. Its failure mode must be "no
/// panel" - never a thrown review, and never an unfenced persona reaching a PR.
/// </summary>
public class PanelPlannerServiceTests
{
    private static readonly ModelRef Model = new("openrouter", "some/model");

    private static readonly Diff SampleDiff = Diff.Parse(
        "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n");

    private const string TwoGood =
        """
        {"personas":[
          {"lens":"sql-injection","name":"The DBA","risk":"raw interpolation into a query here","focus":"parameterisation"},
          {"lens":"concurrency","name":"The Racer","risk":"a lock is taken on only one path","focus":"races"}
        ]}
        """;

    private static ChatClientPanelPlanner Planner(
        IReviewer reviewer, Action<string>? log = null, int cap = PanelFence.MaxPersonas) =>
        new(reviewer, Model, Model, 0.2, cap, log);

    [Fact]
    public async Task A_plan_becomes_fenced_composed_personas()
    {
        var personas = await Planner(new ScriptedReviewer(TwoGood)).PlanAsync(SampleDiff, null, []);

        Assert.Equal(["sql-injection", "concurrency"], personas.Select(p => p.Id));
        Assert.All(personas, p => Assert.Equal(ReviewTier.Diff, p.Tier));
        Assert.All(personas, p => Assert.Equal(Model, p.Model));
    }

    [Fact]
    public async Task A_failing_orchestrator_yields_no_panel_rather_than_throwing()
    {
        var lines = new List<string>();

        var personas = await Planner(new ThrowingReviewer(), lines.Add).PlanAsync(SampleDiff, null, []);

        Assert.Empty(personas);
        Assert.Contains(lines, l => l.Contains("orchestrator failed") && l.Contains("falling back"));
    }

    [Fact]
    public async Task An_unreadable_plan_yields_no_panel()
    {
        var personas = await Planner(new ScriptedReviewer("How about a few reviewers?")).PlanAsync(SampleDiff, null, []);

        Assert.Empty(personas);
    }

    [Fact]
    public async Task An_empty_diff_is_not_worth_a_model_call()
    {
        var reviewer = new ScriptedReviewer(TwoGood);

        var personas = await Planner(reviewer).PlanAsync(Diff.Empty, null, []);

        Assert.Empty(personas);
        Assert.Equal(0, reviewer.Calls);
    }

    [Fact]
    public async Task Fenced_out_candidates_are_logged_with_their_reason()
    {
        // A panel that quietly shrank is a panel nobody can debug.
        var lines = new List<string>();
        var reply = """{"personas":[{"lens":"code quality","name":"Q","risk":"this diff changes a lot of code","focus":"x"}]}""";

        var personas = await Planner(new ScriptedReviewer(reply), lines.Add).PlanAsync(SampleDiff, null, []);

        Assert.Empty(personas);
        Assert.Contains(lines, l => l.Contains("rejected 'code quality'") && l.Contains("generic"));
        Assert.Contains(lines, l => l.Contains("no usable reviewers"));
    }

    [Fact]
    public async Task The_convened_panel_is_logged()
    {
        var lines = new List<string>();

        await Planner(new ScriptedReviewer(TwoGood), lines.Add).PlanAsync(SampleDiff, null, []);

        Assert.Contains(lines, l => l.Contains("convened 2 reviewer(s)") && l.Contains("sql-injection"));
    }

    [Fact]
    public async Task The_seed_and_conventions_reach_the_orchestrator_request()
    {
        var reviewer = new ScriptedReviewer(TwoGood);
        var conventions = new RepoConventions("CLAUDE.md", "Functional core, imperative shell.");

        await Planner(reviewer).PlanAsync(SampleDiff, conventions, [TestPersona("bug-hunter")]);

        var user = Msg.User(reviewer.LastRequest!);
        Assert.Contains("bug-hunter", user);
        Assert.Contains("Functional core, imperative shell.", user);
    }

    [Fact]
    public async Task The_cap_is_enforced_end_to_end()
    {
        var reply = """
            {"personas":[
              {"lens":"a-risk","name":"A","risk":"a specific hazard in this diff","focus":"x"},
              {"lens":"b-risk","name":"B","risk":"another specific hazard here","focus":"x"},
              {"lens":"c-risk","name":"C","risk":"a third specific hazard here","focus":"x"}
            ]}
            """;

        var personas = await Planner(new ScriptedReviewer(reply), cap: 2).PlanAsync(SampleDiff, null, []);

        Assert.Equal(2, personas.Count);
    }

    [Fact]
    public async Task The_cap_bounds_the_whole_panel_not_just_the_generated_half()
    {
        // The seed holds part of the cap. Fencing against the full cap let a model that took the
        // system line ("at most 4") over the user line ("2 more") push a 2-seed panel to 6 - which
        // then overflowed PanelCodec.Extract's read clamp and shed its tail on the next turn.
        var lines = new List<string>();
        var reply = """
            {"personas":[
              {"lens":"a-risk","name":"A","risk":"a specific hazard in this diff","focus":"x"},
              {"lens":"b-risk","name":"B","risk":"another specific hazard here","focus":"x"},
              {"lens":"c-risk","name":"C","risk":"a third specific hazard here","focus":"x"},
              {"lens":"d-risk","name":"D","risk":"a fourth specific hazard here","focus":"x"}
            ]}
            """;
        var seed = new[] { TestPersona("architect"), TestPersona("bug-hunter") };

        var personas = await Planner(new ScriptedReviewer(reply), lines.Add, cap: 4)
            .PlanAsync(SampleDiff, null, seed);

        Assert.Equal(["a-risk", "b-risk"], personas.Select(p => p.Id));
        Assert.Contains(lines, l => l.Contains("rejected 'c-risk'") && l.Contains("over the cap"));
    }

    [Fact]
    public async Task A_candidate_that_re_covers_a_seed_lens_is_fenced_out()
    {
        var lines = new List<string>();
        var reply = """
            {"personas":[
              {"lens":"Bug Hunter","name":"Bugs","risk":"this diff adds an unchecked index","focus":"x"},
              {"lens":"sql-injection","name":"The DBA","risk":"raw interpolation into a query here","focus":"x"}
            ]}
            """;

        var personas = await Planner(new ScriptedReviewer(reply), lines.Add)
            .PlanAsync(SampleDiff, null, [TestPersona("bug-hunter")]);

        Assert.Equal("sql-injection", Assert.Single(personas).Id);
        Assert.Contains(lines, l => l.Contains("rejected 'Bug Hunter'") && l.Contains("seed reviewer's lens"));
    }

    [Fact]
    public async Task A_seed_that_already_fills_the_panel_is_not_worth_a_model_call()
    {
        // Every candidate would be fenced out for want of a slot, so planning could only ever
        // return nothing - at the price of a model call.
        var reviewer = new ScriptedReviewer(TwoGood);
        var lines = new List<string>();
        var seed = new[] { TestPersona("a"), TestPersona("b") };

        var personas = await Planner(reviewer, lines.Add, cap: 2).PlanAsync(SampleDiff, null, seed);

        Assert.Empty(personas);
        Assert.Equal(0, reviewer.Calls);
        Assert.Contains(lines, l => l.Contains("seed already fills the panel"));
    }

    private static Persona TestPersona(string id) => new(
        id, id, id, ReviewTier.Diff, Model, 0.2, "review it");

    private sealed class ScriptedReviewer(string reply) : IReviewer
    {
        private int _calls;

        public int Calls => _calls;

        public ReviewRequest? LastRequest { get; private set; }

        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            LastRequest = request;
            return Task.FromResult(ModelReply.Untracked(reply));
        }
    }

    private sealed class ThrowingReviewer : IReviewer
    {
        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default) =>
            throw new InvalidOperationException("orchestrator exploded");
    }
}
