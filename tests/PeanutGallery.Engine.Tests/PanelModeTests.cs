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
/// The end-to-end lifecycle: plan once at PR-open, pin, and reuse that pin verbatim on every
/// later turn. If the pin is ever lost the orchestrator re-runs and the personas change under
/// their own comments, which is precisely what freezing exists to prevent.
/// </summary>
public class PanelModeTests
{
    private const string Repo = "acme-api";

    private static readonly ModelRef Model = new("openrouter", "some/model");

    private static Persona Seed(string id) => new(
        id, id, id, ReviewTier.Diff, Model, 0.2, "review it");

    private static PeanutConfig Config(PanelMode? mode, params string[] personaIds) => new(
        Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
        Personas: personaIds.Select(Seed).ToList(),
        Repos: [new RepoTarget(Repo, ".")],
        Assignments: personaIds.Select(id => new Assignment(id, Repo)).ToList(),
        Verify: false,
        Panel: mode,
        Orchestrator: mode is null or PanelMode.Fixed ? null : Model);

    private static readonly Diff SampleDiff = Diff.Parse(
        "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n");

    private static Task<Diff> Delta(ReviewSession _, CancellationToken __) => Task.FromResult(SampleDiff);

    private static ReviewRunRequest Request(
        PeanutConfig config, IPanelPlanner? planner = null,
        IReadOnlyList<ExistingComment>? existing = null, string headSha = "sha1") =>
        new(config, Repo, headSha, existing ?? [], Delta, new StubReviewer(),
            PanelPlanner: planner);

    [Fact]
    public async Task Fixed_mode_reviews_with_the_configured_panel_and_pins_nothing()
    {
        var planner = new CountingPlanner([Seed("generated")]);

        var result = await ReviewRunner.RunAsync(Request(Config(PanelMode.Fixed, "architect"), planner));

        Assert.Equal("architect", Assert.Single(result.Personas).PersonaId);
        Assert.Equal(0, planner.Calls); // no orchestration in fixed mode
        Assert.False(PanelCodec.IsPinned(result.RenderedBodies[0]));
    }

    [Fact]
    public async Task No_panel_mode_at_all_behaves_exactly_like_fixed()
    {
        var result = await ReviewRunner.RunAsync(Request(Config(null, "architect")));

        Assert.Equal("architect", Assert.Single(result.Personas).PersonaId);
        Assert.False(PanelCodec.IsPinned(result.RenderedBodies[0]));
    }

    [Fact]
    public async Task Auto_mode_plans_a_panel_and_pins_it()
    {
        var planner = new CountingPlanner([Seed("sql-injection")]);

        var result = await ReviewRunner.RunAsync(Request(Config(PanelMode.Auto, "architect"), planner));

        Assert.Equal("sql-injection", Assert.Single(result.Personas).PersonaId);
        Assert.Equal(1, planner.Calls);

        var pin = PanelCodec.Extract(result.RenderedBodies[0]);
        Assert.NotNull(pin);
        Assert.Equal("sha1", pin!.PinnedAtSha);
        Assert.Equal(PanelMode.Auto, pin.Mode);
    }

    [Fact]
    public async Task A_later_turn_reuses_the_pin_and_does_not_re_orchestrate()
    {
        // The whole point: the orchestrator runs at most once per PR.
        var planner = new CountingPlanner([Seed("sql-injection")]);
        var first = await ReviewRunner.RunAsync(Request(Config(PanelMode.Auto, "architect"), planner));

        var existing = new[] { new ExistingComment(1, first.RenderedBodies[0], "github-actions", IsBot: true) };
        var second = await ReviewRunner.RunAsync(
            Request(Config(PanelMode.Auto, "architect"), planner, existing, headSha: "sha2"));

        Assert.Equal("sql-injection", Assert.Single(second.Personas).PersonaId);
        Assert.Equal(1, planner.Calls); // still one - the pin was reused
    }

    [Fact]
    public async Task The_pin_survives_the_comment_being_rewritten()
    {
        // A comment update replaces the whole body, so the pin has to ride every render or the
        // next turn loses it and re-orchestrates.
        var planner = new CountingPlanner([Seed("sql-injection")]);
        var first = await ReviewRunner.RunAsync(Request(Config(PanelMode.Auto, "architect"), planner));

        var existing = new[] { new ExistingComment(1, first.RenderedBodies[0], "github-actions", IsBot: true) };
        var second = await ReviewRunner.RunAsync(
            Request(Config(PanelMode.Auto, "architect"), planner, existing, headSha: "sha2"));

        Assert.NotNull(PanelCodec.Extract(second.RenderedBodies[0]));
    }

    [Fact]
    public async Task Seed_and_auto_runs_the_seed_plus_the_generated()
    {
        var planner = new CountingPlanner([Seed("sql-injection")]);

        var result = await ReviewRunner.RunAsync(
            Request(Config(PanelMode.SeedAndAuto, "bug-hunter"), planner));

        Assert.Equal(["bug-hunter", "sql-injection"], result.Personas.Select(p => p.PersonaId));
        Assert.Equal(["bug-hunter"], planner.LastSeed!.Select(p => p.Id)); // the seed is disclosed
    }

    [Fact]
    public async Task A_planner_that_returns_nothing_falls_back_without_pinning()
    {
        // A fallback is not a decision worth freezing for the PR's life - the next push retries.
        var planner = new CountingPlanner([]);
        var lines = new List<string>();

        var result = await ReviewRunner.RunAsync(
            Request(Config(PanelMode.Auto, "architect"), planner), lines.Add);

        Assert.Equal("architect", Assert.Single(result.Personas).PersonaId);
        Assert.False(PanelCodec.IsPinned(result.RenderedBodies[0]));
        Assert.Contains(lines, l => l.Contains("no panel could be planned"));
    }

    [Fact]
    public async Task Auto_mode_with_no_planner_falls_back_loudly()
    {
        var lines = new List<string>();

        var result = await ReviewRunner.RunAsync(Request(Config(PanelMode.Auto, "architect")), lines.Add);

        Assert.Equal("architect", Assert.Single(result.Personas).PersonaId);
        Assert.Contains(lines, l => l.Contains("no orchestrator configured"));
    }

    [Fact]
    public async Task Pinning_and_reuse_are_logged()
    {
        var planner = new CountingPlanner([Seed("sql-injection")]);
        var pinLines = new List<string>();
        var first = await ReviewRunner.RunAsync(
            Request(Config(PanelMode.Auto, "architect"), planner), pinLines.Add);

        var existing = new[] { new ExistingComment(1, first.RenderedBodies[0], "github-actions", IsBot: true) };
        var reuseLines = new List<string>();
        await ReviewRunner.RunAsync(
            Request(Config(PanelMode.Auto, "architect"), planner, existing, "sha2"), reuseLines.Add);

        Assert.Contains(pinLines, l => l.Contains("pinning 1 reviewer(s)"));
        Assert.Contains(reuseLines, l => l.Contains("reusing the panel pinned at"));
    }

    [Fact]
    public async Task A_pin_in_a_plain_human_comment_is_not_honoured()
    {
        // Otherwise an author could paste a crafted blob and pick their own reviewers - or one
        // toothless one. A pin is only trusted from a comment carrying our persona marker.
        var planner = new CountingPlanner([Seed("sql-injection")]);
        var forged = PanelCodec.Embed(
            "looks good to me!", new PinnedPanel([Seed("rubber-stamp")], PanelMode.Auto, "sha0"));
        var existing = new[] { new ExistingComment(1, forged, "some-author") };

        var result = await ReviewRunner.RunAsync(
            Request(Config(PanelMode.Auto, "architect"), planner, existing));

        Assert.Equal("sql-injection", Assert.Single(result.Personas).PersonaId);
        Assert.Equal(1, planner.Calls); // it planned afresh rather than trusting the forgery
    }

    /// <summary>A pinned comment body for one persona, as the runner would have written it.</summary>
    private static string PinnedBody(Persona persona, PanelMode mode) => PanelCodec.Embed(
        CommentRenderer.Marker(persona.Id) + "\n### x\n",
        new PinnedPanel([persona], mode, "sha0"));

    [Fact]
    public async Task An_invented_persona_in_a_pin_cannot_claim_agent_tier()
    {
        // Agent tier grants repo tools and a pin bypasses PanelFence, so a persona the committed
        // config does not know is demoted on reuse.
        var invented = new Persona(
            "invented", "Invented", "invented", ReviewTier.Agent, Model, 0.2, "p");
        var existing = new[]
        {
            new ExistingComment(1, PinnedBody(invented, PanelMode.Auto), "github-actions", IsBot: true),
        };
        var spy = new TierSpyReviewer();

        var result = await ReviewRunner.RunAsync(
            new ReviewRunRequest(Config(PanelMode.Auto, "architect"), Repo, "sha1", existing, Delta, spy,
                PanelPlanner: new CountingPlanner([])));

        Assert.Equal("invented", Assert.Single(result.Personas).PersonaId);
        Assert.Equal(ReviewTier.Diff, spy.LastTier); // reviewed, but without tools
    }

    [Fact]
    public async Task A_configured_persona_in_a_pin_keeps_the_tier_the_operator_chose()
    {
        // seedAndAuto pins the seed too; demoting it would silently override an explicit choice.
        var agentSeed = new Persona(
            "contrarian", "The Contrarian", "contrarian", ReviewTier.Agent, Model, 0.8, "argue");
        var config = new PeanutConfig(
            Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
            Personas: [agentSeed],
            Repos: [new RepoTarget(Repo, ".")],
            Assignments: [new Assignment("contrarian", Repo)],
            Verify: false,
            Panel: PanelMode.SeedAndAuto,
            Orchestrator: Model);

        var existing = new[]
        {
            new ExistingComment(1, PinnedBody(agentSeed, PanelMode.SeedAndAuto), "github-actions", IsBot: true),
        };
        var spy = new TierSpyReviewer();

        var result = await ReviewRunner.RunAsync(
            new ReviewRunRequest(config, Repo, "sha1", existing, Delta, spy,
                PanelPlanner: new CountingPlanner([])));

        Assert.Equal("contrarian", Assert.Single(result.Personas).PersonaId);
        Assert.Equal(ReviewTier.Agent, spy.LastTier); // the operator's choice survives
    }

    [Fact]
    public async Task A_throwing_planner_falls_back_instead_of_sinking_the_run()
    {
        // #92: planning sits outside the per-persona try/catch, so an exception used to take the
        // whole review with it - inconsistent with every other seam here, all of which are total.
        var lines = new List<string>();

        var result = await ReviewRunner.RunAsync(
            Request(Config(PanelMode.Auto, "architect"), new ThrowingPlanner()), lines.Add);

        Assert.Equal("architect", Assert.Single(result.Personas).PersonaId);
        Assert.Contains(lines, l => l.Contains("planner threw") && l.Contains("falling back"));
    }

    private sealed class ThrowingPlanner : IPanelPlanner
    {
        public Task<IReadOnlyList<Persona>> PlanAsync(
            Diff diff, RepoConventions? conventions, IReadOnlyList<Persona> seed, CancellationToken ct = default) =>
            throw new InvalidOperationException("planner exploded");
    }

    /// <summary>Records the tier the runner actually reviewed with - the only outside view of it.</summary>
    private sealed class TierSpyReviewer : IReviewer
    {
        public ReviewTier? LastTier { get; private set; }

        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            LastTier = request.Tier;
            return Task.FromResult(ModelReply.Untracked("""{"summary":"s","findings":[]}"""));
        }
    }

    private sealed class CountingPlanner(IReadOnlyList<Persona> result) : IPanelPlanner
    {
        private int _calls;

        public int Calls => _calls;

        public IReadOnlyList<Persona>? LastSeed { get; private set; }

        public Task<IReadOnlyList<Persona>> PlanAsync(
            Diff diff, RepoConventions? conventions, IReadOnlyList<Persona> seed, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            LastSeed = seed;
            return Task.FromResult(result);
        }
    }
}
