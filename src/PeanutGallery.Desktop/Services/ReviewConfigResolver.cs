using System.Collections.Generic;
using PeanutGallery.Core;

namespace PeanutGallery.Desktop.Services;

/// <summary>
/// Pure decisions about which committed config to use and which persona panel it selects for a
/// given GitHub repo. No IO — the shell fetches the file bytes; the choices live here so they
/// stay total and testable.
/// </summary>
public static class ReviewConfigResolver
{
    /// <summary>Committed config locations to try, in order (matches the action's resolution).</summary>
    public static IReadOnlyList<string> ConfigPaths { get; } =
        [".github/peanut-gallery.json", ".github/peanut.json", "peanut.json", "peanut-gallery.json"];

    /// <summary>
    /// The repo name in <paramref name="config"/> whose assignment panel to run for the GitHub
    /// repo <paramref name="githubRepo"/>: an exact name match wins; else a single-repo config's
    /// only repo; else the GitHub repo name (which may yield an empty plan, surfaced to the user).
    /// </summary>
    public static string PanelRepoName(PeanutConfig config, string githubRepo)
    {
        foreach (var r in config.Repos)
        {
            if (r.Name == githubRepo)
            {
                return githubRepo;
            }
        }

        return config.Repos.Count == 1 ? config.Repos[0].Name : githubRepo;
    }

    /// <summary>Whether <paramref name="config"/> asks for a dynamic panel (Auto / SeedAndAuto)
    /// with an orchestrator configured — used only to decide whether a missing persona model is
    /// worth warning about (see <see cref="ResolvePanelPlannerSpec"/>).</summary>
    public static bool WantsPanelPlanner(PeanutConfig config) =>
        (config.Panel ?? PanelMode.Fixed) != PanelMode.Fixed && config.Orchestrator is not null;

    /// <summary>The model and sampling a dynamic panel's generated personas should review with,
    /// mirroring the CLI's resolution (Commands.cs) so a dynamic panel run from the desktop app
    /// costs — and reviews with — the same reviewers CI would generate. Null when
    /// <see cref="WantsPanelPlanner"/> is false, or when no persona model can be resolved (no
    /// explicit <c>personaModel</c> and no configured persona to inherit one from).</summary>
    public static PanelPlannerSpec? ResolvePanelPlannerSpec(PeanutConfig config)
    {
        if (!WantsPanelPlanner(config))
        {
            return null;
        }

        var personaModel = config.PersonaModel ?? (config.Personas.Count > 0 ? config.Personas[0].Model : null);
        if (personaModel is null)
        {
            return null;
        }

        var seedTemp = config.Personas.Count > 0 ? config.Personas[0].SamplingTemperature() : PanelFence.DefaultTemperature;
        var seedTopP = config.Personas.Count > 0 ? config.Personas[0].TopP : null;
        var seedTopK = config.Personas.Count > 0 ? config.Personas[0].TopK : null;

        return new PanelPlannerSpec(
            config.Orchestrator!,
            personaModel,
            PanelFence.PersonaTemperature(config.PersonaTemperature, seedTemp),
            PanelFence.PersonaTopP(config.PersonaTopP, seedTopP),
            PanelFence.PersonaTopK(config.PersonaTopK, seedTopK));
    }
}

/// <summary>What a dynamic panel's orchestrator and generated personas should use — the pure
/// result of <see cref="ReviewConfigResolver.ResolvePanelPlannerSpec"/>, before the shell turns it
/// into an IO-bearing <c>ChatClientPanelPlanner</c>.</summary>
public readonly record struct PanelPlannerSpec(
    ModelRef Orchestrator, ModelRef PersonaModel, double PersonaTemperature, double? PersonaTopP, int? PersonaTopK);
