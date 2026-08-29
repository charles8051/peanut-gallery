using PeanutGallery.Core;

namespace PeanutGallery.Cli;

/// <summary>
/// The starter config written by `peanut-gallery init` - the three archetype personas, all on
/// one provider and one model, pointed at the repo the command was run in.
///
/// <para>Every value here is what a first-time user runs before they have opinions, so it is
/// deliberately the least demanding config that still does something useful: one provider means
/// one API key, and <see cref="KnownModels.Default"/> means the same model the built-in panel
/// and the action's bundled default use. It used to name three vendors across two providers with
/// a hand-maintained model id each, and to hardcode an absolute path on the author's disk as the
/// repo target, so the scaffold it wrote could not run anywhere but that machine (#196).</para>
/// </summary>
internal static class Sample
{
	/// <summary>
	/// A starter config whose single repo target is the current directory, under
	/// <paramref name="repoName"/> - the name every later command passes as <c>--repo</c>.
	/// </summary>
	public static PeanutConfig For(string repoName) => new(
		Providers:
		[
			KnownProviders.OpenRouterDefault,
		],
		Personas:
		[
			new Persona(
				Id: "architect",
				Name: "The Architect",
				Lens: "architecture",
				Tier: ReviewTier.Diff,
				Model: KnownModels.Default,
				Temperature: 1.0,
				SystemPrompt: "You review for architectural coherence: layering violations, leaky "
					+ "abstractions, and state/IO/timing fused where a functional core belongs. "
					+ "Cite file:line. Do not nitpick style. An empty findings list is fine."),
			new Persona(
				Id: "bug-hunter",
				Name: "The Bug Hunter",
				Lens: "bug-hunter",
				Tier: ReviewTier.Diff,
				Model: KnownModels.Default,
				Temperature: 1.0,
				SystemPrompt: "Find correctness bugs only: off-by-one, null/lifetime, races, error "
					+ "paths, boundary conditions. High precision; if unsure, say so. An empty "
					+ "findings list is fine."),
			// Diff tier, not agent tier. Agent tier grants repo tools and costs a tool loop, and
			// "is this change worth doing at all" is answerable from the diff - which is also why
			// this repo's own committed config runs its contrarian at diff tier.
			new Persona(
				Id: "contrarian",
				Name: "The Contrarian",
				Lens: "contrarian",
				Tier: ReviewTier.Diff,
				Model: KnownModels.Default,
				Temperature: 0.8,
				SystemPrompt: "You are a divergent contrarian. Argue whether this change is worth "
					+ "doing at all and sketch a from-scratch alternative. Be provocative but "
					+ "concrete, and offer one \"what if we deleted this whole subsystem\" idea. If "
					+ "the change is plainly worth doing, say so in one line and report nothing."),
		],
		Repos:
		[
			new RepoTarget(repoName, "."),
		],
		Assignments:
		[
			new Assignment("architect", repoName),
			new Assignment("bug-hunter", repoName),
			new Assignment("contrarian", repoName),
		]);

	/// <summary>
	/// A config repo name derived from a directory: its leaf name, with anything that is not a
	/// letter, digit, dash, underscore, or dot folded to a dash. The name is not cosmetic - it is
	/// what every later command passes as <c>--repo</c> - so it has to survive a shell word
	/// without quoting. Falls back to <c>repo</c> for a directory with no usable leaf (a drive
	/// root, say), because an empty name would fail validation as an unknown repo target.
	/// </summary>
	public static string RepoNameFor(string directory)
	{
		var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
		var folded = new string(leaf
			.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
			.ToArray())
			.Trim('-', '.');
		return folded.Length == 0 ? "repo" : folded;
	}
}
