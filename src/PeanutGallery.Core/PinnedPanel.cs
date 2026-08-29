using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>How the panel's findings reach the PR.</summary>
public enum CommentMode
{
	/// <summary>One self-updating comment per persona (the original shape).</summary>
	PerPersona,

	/// <summary>One comment for the whole panel, deduplicated and attributed.</summary>
	Panel,
}

/// <summary>How a PR's reviewer panel is chosen. Wired to config by #69.</summary>
public enum PanelMode
{
	/// <summary>Today's behaviour: the committed config is the panel, re-read every turn.</summary>
	Fixed,

	/// <summary>An orchestrator builds the panel from the diff, once, at PR-open.</summary>
	Auto,

	/// <summary>Curated seed personas always run; the orchestrator adds diff-specific ones on top.</summary>
	SeedAndAuto,
}

/// <summary>
/// The reviewer panel frozen for one pull request, plus the provenance of that decision.
///
/// <para>Freezing is the load-bearing decision behind auto mode (auto-panel ADR, Decision 2). The
/// incremental-review machinery is keyed on stable persona identity - one self-updating comment
/// per persona, with that persona's session encoded inside it - so a panel that reinvented its
/// personas on every push would orphan comments and lose resolve/withdraw continuity. Generating
/// once at PR-open and pinning removes the tension entirely: auto mode decides WHO reviews at open
/// time, and everything after that is the existing stateful flow, unchanged.</para>
///
/// <para><see cref="PinnedAtSha"/> and <see cref="OrchestratorModel"/> are provenance, not
/// behaviour: they let a reader (or a bug report) see which head the panel was chosen from and by
/// what, which is the first question anyone asks when a panel looks wrong.</para>
/// </summary>
/// <param name="OrchestratorModel">DISPLAY-ONLY provenance: which model chose this panel, as
/// <c>provider:modelId</c>. Deliberately a string rather than a <see cref="ModelRef"/> - nothing
/// consumes it structurally, and <c>ToString()</c> is not guaranteed to round-trip. If a consumer
/// ever needs it as a value (comparison, reconstruction), change the TYPE here rather than parsing
/// the string back.</param>
public sealed record PinnedPanel(
	IReadOnlyList<Persona> Personas,
	PanelMode Mode,
	string PinnedAtSha,
	string? OrchestratorModel = null);
