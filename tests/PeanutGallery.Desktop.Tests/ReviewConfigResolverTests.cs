using PeanutGallery.Core;
using PeanutGallery.Desktop.Services;
using Xunit;

namespace PeanutGallery.Desktop.Tests;

public class ReviewConfigResolverTests
{
    private static PeanutConfig WithRepos(params string[] names) => new(
        Providers: [],
        Personas: [],
        Repos: [.. System.Array.ConvertAll(names, n => new RepoTarget(n, "."))],
        Assignments: []);

    [Fact]
    public void Exact_name_match_wins()
    {
        var config = WithRepos("other", "api", "web");
        Assert.Equal("api", ReviewConfigResolver.PanelRepoName(config, "api"));
    }

    [Fact]
    public void Single_repo_config_uses_its_only_repo_even_if_the_name_differs()
    {
        var config = WithRepos("peanut-gallery");
        Assert.Equal("peanut-gallery", ReviewConfigResolver.PanelRepoName(config, "some-fork"));
    }

    [Fact]
    public void Multiple_repos_no_match_falls_back_to_the_github_repo_name()
    {
        var config = WithRepos("api", "web");
        Assert.Equal("nope", ReviewConfigResolver.PanelRepoName(config, "nope"));
    }

    [Fact]
    public void Config_paths_prefer_the_dot_github_location_first()
    {
        Assert.Equal(".github/peanut-gallery.json", ReviewConfigResolver.ConfigPaths[0]);
        Assert.Contains("peanut.json", ReviewConfigResolver.ConfigPaths);
    }

    private static readonly ModelRef Orchestrator = new("openrouter", "orchestrator-model");
    private static readonly ModelRef SeedModel = new("openrouter", "seed-model");
    private static readonly ModelRef ExplicitPersonaModel = new("openrouter", "explicit-model");

    private static Persona Seed(double temperature = 0.4, double? topP = null, int? topK = null) =>
        new("seed", "Seed", "lens", ReviewTier.Diff, SeedModel, temperature, "prompt", TopP: topP, TopK: topK);

    [Fact]
    public void Fixed_mode_never_wants_a_panel_planner()
    {
        var config = new PeanutConfig([], [], [], [], Panel: PanelMode.Fixed, Orchestrator: Orchestrator);
        Assert.False(ReviewConfigResolver.WantsPanelPlanner(config));
        Assert.Null(ReviewConfigResolver.ResolvePanelPlannerSpec(config));
    }

    [Fact]
    public void Dynamic_mode_without_an_orchestrator_does_not_want_a_planner()
    {
        var config = new PeanutConfig([], [], [], [], Panel: PanelMode.Auto);
        Assert.False(ReviewConfigResolver.WantsPanelPlanner(config));
        Assert.Null(ReviewConfigResolver.ResolvePanelPlannerSpec(config));
    }

    [Fact]
    public void Dynamic_mode_with_an_orchestrator_but_no_resolvable_persona_model_wants_a_planner_but_resolves_none()
    {
        var config = new PeanutConfig([], [], [], [], Panel: PanelMode.Auto, Orchestrator: Orchestrator);
        Assert.True(ReviewConfigResolver.WantsPanelPlanner(config));
        Assert.Null(ReviewConfigResolver.ResolvePanelPlannerSpec(config));
    }

    [Fact]
    public void SeedAndAuto_inherits_the_seed_personas_model_and_temperature()
    {
        var config = new PeanutConfig(
            [], [Seed(temperature: 0.4)], [], [], Panel: PanelMode.SeedAndAuto, Orchestrator: Orchestrator);

        var spec = ReviewConfigResolver.ResolvePanelPlannerSpec(config);

        Assert.NotNull(spec);
        Assert.Equal(Orchestrator, spec!.Value.Orchestrator);
        Assert.Equal(SeedModel, spec.Value.PersonaModel);
        // Floored at PanelFence.DefaultTemperature (1.0): an inherited seed below that would
        // silently make every invented persona greedy (#127/#129).
        Assert.Equal(PanelFence.DefaultTemperature, spec.Value.PersonaTemperature);
    }

    [Fact]
    public void An_explicit_seed_temperature_above_the_floor_is_respected()
    {
        var config = new PeanutConfig(
            [], [Seed(temperature: 1.4)], [], [], Panel: PanelMode.SeedAndAuto, Orchestrator: Orchestrator);

        var spec = ReviewConfigResolver.ResolvePanelPlannerSpec(config);

        Assert.Equal(1.4, spec!.Value.PersonaTemperature);
    }

    [Fact]
    public void Explicit_personaModel_and_sampling_win_over_the_seed()
    {
        var config = new PeanutConfig(
            [], [Seed(temperature: 0.1, topP: 0.5, topK: 10)], [], [],
            Panel: PanelMode.SeedAndAuto, Orchestrator: Orchestrator,
            PersonaModel: ExplicitPersonaModel, PersonaTemperature: 0.3, PersonaTopP: 0.9, PersonaTopK: 40);

        var spec = ReviewConfigResolver.ResolvePanelPlannerSpec(config);

        Assert.NotNull(spec);
        Assert.Equal(ExplicitPersonaModel, spec!.Value.PersonaModel);
        // Authored explicitly, so respected as-is even below the floor (unlike the inherited case).
        Assert.Equal(0.3, spec.Value.PersonaTemperature);
        Assert.Equal(0.9, spec.Value.PersonaTopP);
        Assert.Equal(40, spec.Value.PersonaTopK);
    }

    [Fact]
    public void Pure_Auto_mode_with_no_seed_personas_can_still_resolve_from_an_explicit_personaModel()
    {
        var config = new PeanutConfig(
            [], [], [], [], Panel: PanelMode.Auto, Orchestrator: Orchestrator, PersonaModel: ExplicitPersonaModel);

        var spec = ReviewConfigResolver.ResolvePanelPlannerSpec(config);

        Assert.NotNull(spec);
        Assert.Equal(ExplicitPersonaModel, spec!.Value.PersonaModel);
        Assert.Null(spec.Value.PersonaTopP);
        Assert.Null(spec.Value.PersonaTopK);
    }
}
