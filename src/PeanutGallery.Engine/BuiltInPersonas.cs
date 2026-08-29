using System.Collections.Generic;
using PeanutGallery.Core;

namespace PeanutGallery.Engine;

/// <summary>
/// The personas that ship with the tool (the built-in scope). Three archetypes, all on
/// <see cref="KnownModels.Default"/>; the first two (diff-tier) are also the
/// <see cref="DefaultPanel"/> fallback. Single source of truth so the library screen, the
/// default panel, and any scaffold agree.
///
/// <para>One model for all three, rather than a model picked per archetype. A per-persona pick
/// meant a first-time user needed a key for every vendor named here before the built-in panel
/// would run (#37 for the action default, #196 for the <c>init</c> scaffold), and it made the
/// panel a hand-maintained model index (#58) that went stale the moment a vendor shipped a new
/// tier. Pick your own model per persona in config; the built-ins pick one that works.</para>
/// </summary>
public static class BuiltInPersonas
{
    public static readonly Persona Architect = new(
        Id: "architect",
        Name: "The Architect",
        Lens: "architecture",
        Tier: ReviewTier.Diff,
        Model: KnownModels.Default,
        Temperature: 1.0,
        // Kept byte-identical to the architect prompt in action/default.json: DefaultPanel.For
        // builds the desktop's zero-config panel from these personas while the container action
        // loads that JSON, and a prompt that differs between them is the same PR reviewed under
        // different instructions depending on which shell ran it.
        SystemPrompt: "You review for architectural coherence: layering violations, leaky "
            + "abstractions, and tangled responsibilities. Cite file:line. Do not nitpick style. "
            + "An empty findings list is fine.");

    public static readonly Persona BugHunter = new(
        Id: "bug-hunter",
        Name: "The Bug Hunter",
        Lens: "bug-hunter",
        Tier: ReviewTier.Diff,
        Model: KnownModels.Default,
        Temperature: 1.0,
        SystemPrompt: "Find correctness bugs only: off-by-one, null/lifetime, races, error "
            + "paths, boundary conditions. High precision; if unsure, say so. An empty findings "
            + "list is fine.");

    public static readonly Persona Contrarian = new(
        Id: "contrarian",
        Name: "The Contrarian",
        Lens: "contrarian",
        Tier: ReviewTier.Agent,
        Model: KnownModels.Default,
        Temperature: 0.8,
        SystemPrompt: "You are a divergent contrarian. Argue whether this change is worth "
            + "doing at all and sketch a from-scratch alternative. Be provocative but "
            + "concrete, and offer one \"what if we deleted this whole subsystem\" idea.");

    /// <summary>All built-in personas, in display order.</summary>
    public static IReadOnlyList<Persona> All { get; } = [Architect, BugHunter, Contrarian];

    /// <summary>The default review panel: the two diff-tier archetypes (contrarian is agent-tier).</summary>
    public static IReadOnlyList<Persona> DefaultReviewers { get; } = [Architect, BugHunter];
}
