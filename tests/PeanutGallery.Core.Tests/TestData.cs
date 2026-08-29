using PeanutGallery.Core;

namespace PeanutGallery.Core.Tests;

/// <summary>Shared fixtures for the core tests.</summary>
internal static class TestData
{
	public static Persona Architect { get; } = new(
		"architect", "The Architect", "architecture", ReviewTier.Diff,
		new ModelRef("openrouter", "anthropic/claude-opus-4.1"), 0.2,
		"Review for architectural coherence.");

	public static Persona BugHunter { get; } = new(
		"bug-hunter", "The Bug Hunter", "bug-hunter", ReviewTier.Diff,
		new ModelRef("fireworks", "accounts/fireworks/models/deepseek-v3"), 0.0,
		"Find correctness bugs only.");

	public static Persona Contrarian { get; } = new(
		"contrarian", "The Contrarian", "contrarian", ReviewTier.Agent,
		new ModelRef("openrouter", "x-ai/grok-4"), 0.8,
		"Argue whether this change is worth doing at all.");

	public static PeanutConfig FullConfig { get; } = new(
		Providers:
		[
			new ProviderConfig("openrouter", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY"),
			new ProviderConfig("fireworks", "https://api.fireworks.ai/inference/v1", "FIREWORKS_API_KEY"),
		],
		Personas: [Architect, BugHunter, Contrarian],
		Repos: [new RepoTarget("demo", "/repos/demo"), new RepoTarget("other", "/repos/other")],
		Assignments:
		[
			new Assignment("architect", "demo"),
			new Assignment("bug-hunter", "demo"),
			new Assignment("contrarian", "other"),
		]);
}
