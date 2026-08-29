using System.Linq;
using PeanutGallery.Core;

namespace PeanutGallery.Engine;

/// <summary>
/// The persona panel a shell uses when a repo has no committed config — the built-in diff-tier
/// reviewers (<see cref="BuiltInPersonas.DefaultReviewers"/>) as a seed, with the orchestrator
/// adding diff-specific reviewers on top. Provider API keys are resolved from the environment at
/// review time (never stored here); this is only the panel + model wiring.
///
/// <para><b>Kept byte-for-byte in step with <c>action/default.json</c></b>, which is the same
/// decision expressed as the config the container action loads. Two zero-config surfaces that
/// disagree would mean the desktop app and CI review the same PR with different panels.</para>
///
/// <para><b><see cref="PanelMode.SeedAndAuto"/>, not <see cref="PanelMode.Auto"/>.</b> A default
/// has to have a floor. Under plain auto with no configured personas, an orchestrator that fails,
/// times out, or plans nothing that survives <see cref="PanelFence"/> falls back to the configured
/// panel — which in that setup is empty, and <c>ReviewRunner</c> returns without posting anything.
/// A green check over a review that never happened is the one outcome a default must not have.
/// Seeding means the fallback is two reviewers rather than none, and costs nothing when the
/// orchestrator does work: the seed runs either way, and the generated reviewers fill the
/// remaining slots up to <see cref="PanelFence.MaxPersonas"/>.</para>
/// </summary>
public static class DefaultPanel
{
    /// <summary>A default config whose seed personas are all assigned to <paramref name="repoName"/>.</summary>
    public static PeanutConfig For(string repoName) => new(
        Providers: [KnownProviders.OpenRouterDefault],
        Personas: BuiltInPersonas.DefaultReviewers,
        Repos: [new RepoTarget(repoName, ".")],
        Assignments: BuiltInPersonas.DefaultReviewers.Select(p => new Assignment(p.Id, repoName)).ToList(),
        // One deduplicated, lens-attributed comment for the whole panel rather than one per
        // persona. A dynamic panel is exactly where per-persona comments read worst: the reviewers
        // are not the same two every time, so the reader cannot learn the shape of the board.
        Comment: CommentMode.Panel,
        Panel: PanelMode.SeedAndAuto,
        Orchestrator: KnownModels.Default,
        // What the generated reviewers run on. Required by ConfigValidation under a dynamic mode
        // when there are no personas to inherit a model from, and named here anyway so the seed
        // and the reviewers convened beside it are never on different models by accident.
        PersonaModel: KnownModels.Default,
        // Two humans arguing in the PR thread should cost nothing, and a question addressed to the
        // panel should cost one call rather than one per reviewer.
        Conversation: new ConversationPolicy(ConversationMode.Reconcile, ["@peanut-gallery"]));
}
