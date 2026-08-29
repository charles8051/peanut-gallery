using System.Linq;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

public class ConfigValidationTests
{
	[Fact]
	public void Well_formed_config_has_no_problems()
	{
		Assert.Empty(ConfigValidation.Validate(TestData.FullConfig));
	}

	[Fact]
	public void Persona_referencing_an_unknown_provider_is_flagged()
	{
		var config = TestData.FullConfig with
		{
			Personas = [TestData.Architect with { Model = new ModelRef("ghost-provider", "m") }],
			Assignments = [],
		};

		var problems = ConfigValidation.Validate(config);

		Assert.Contains(problems, p => p.Message.Contains("unknown provider"));
	}

	[Fact]
	public void Assignment_to_unknown_persona_or_repo_is_flagged()
	{
		var config = TestData.FullConfig with
		{
			Assignments = [new Assignment("nobody", "nowhere")],
		};

		var problems = ConfigValidation.Validate(config);

		Assert.Contains(problems, p => p.Message.Contains("unknown persona"));
		Assert.Contains(problems, p => p.Message.Contains("unknown repo"));
	}

	[Fact]
	public void Duplicate_persona_ids_are_flagged()
	{
		var config = TestData.FullConfig with
		{
			Personas = [TestData.Architect, TestData.Architect],
			Assignments = [],
		};

		Assert.Contains(ConfigValidation.Validate(config), p => p.Message.Contains("duplicate persona id"));
	}

	[Fact]
	public void Out_of_range_temperature_is_flagged()
	{
		var config = TestData.FullConfig with
		{
			Personas = [TestData.Architect with { Temperature = 5.0 }],
			Assignments = [],
		};

		Assert.Contains(ConfigValidation.Validate(config), p => p.Message.Contains("temperature"));
	}

	[Fact]
	public void Out_of_range_personaTemperature_is_flagged()
	{
		var config = TestData.FullConfig with { PersonaTemperature = 5.0 };

		Assert.Contains(
			ConfigValidation.Validate(config),
			p => p.Scope == "personaTemperature" && p.Message.Contains("temperature"));
	}

	[Fact]
	public void An_absent_personaTemperature_is_not_flagged()
	{
		Assert.DoesNotContain(
			ConfigValidation.Validate(TestData.FullConfig),
			p => p.Scope == "personaTemperature");
	}

	[Fact]
	public void Out_of_range_top_p_and_top_k_are_flagged_on_both_the_auto_keys_and_a_persona()
	{
		var config = TestData.FullConfig with
		{
			PersonaTopP = 1.5, PersonaTopK = 0,
			Personas = [TestData.Architect with { TopP = 0.0, TopK = -1 }],
			Assignments = [],
		};
		var problems = ConfigValidation.Validate(config);

		Assert.Contains(problems, p => p.Scope == "personaTopP" && p.Message.Contains("top_p"));
		Assert.Contains(problems, p => p.Scope == "personaTopK" && p.Message.Contains("top_k"));
		Assert.Contains(problems, p => p.Scope.StartsWith("persona:") && p.Message.Contains("top_p"));
		Assert.Contains(problems, p => p.Scope.StartsWith("persona:") && p.Message.Contains("top_k"));
	}

	[Fact]
	public void In_range_and_absent_top_p_top_k_are_not_flagged()
	{
		var ok = TestData.FullConfig with
		{
			PersonaTopP = 0.95, PersonaTopK = 40,
			Personas = [TestData.Architect with { TopP = 0.9, TopK = 1 }],
			Assignments = [],
		};
		Assert.DoesNotContain(ConfigValidation.Validate(ok), p => p.Message.Contains("top_p") || p.Message.Contains("top_k"));
		Assert.DoesNotContain(ConfigValidation.Validate(TestData.FullConfig), p => p.Message.Contains("top_p") || p.Message.Contains("top_k"));
	}

	// ---- panel mode (#69) ----

	private static PeanutConfig PanelConfig(PanelMode? mode, ModelRef? orchestrator, int personas = 1) => new(
		Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
		Personas: Enumerable.Range(0, personas).Select(i => new Persona(
			$"p{i}", $"p{i}", "bugs", ReviewTier.Diff,
			new ModelRef("openrouter", "m"), 0.2, "review")).ToList(),
		Repos: [new RepoTarget("r", ".")],
		Assignments: Enumerable.Range(0, personas).Select(i => new Assignment($"p{i}", "r")).ToList(),
		Panel: mode,
		Orchestrator: orchestrator);

	[Fact]
	public void Fixed_or_absent_panel_mode_needs_no_orchestrator()
	{
		Assert.Empty(ConfigValidation.Validate(PanelConfig(null, null)));
		Assert.Empty(ConfigValidation.Validate(PanelConfig(PanelMode.Fixed, null)));
	}

	[Fact]
	public void A_dynamic_panel_without_an_orchestrator_is_a_config_problem()
	{
		// The runtime is deliberately total and falls back silently, so "auto did nothing" would
		// otherwise stay invisible until someone noticed the personas never changed.
		var problems = ConfigValidation.Validate(PanelConfig(PanelMode.Auto, null));

		Assert.Contains(problems, p => p.Scope == "panel" && p.Message.Contains("orchestrator"));
	}

	[Fact]
	public void An_orchestrator_on_an_unknown_provider_is_a_config_problem()
	{
		var problems = ConfigValidation.Validate(
			PanelConfig(PanelMode.Auto, new ModelRef("nope", "m")));

		Assert.Contains(problems, p => p.Scope == "orchestrator" && p.Message.Contains("unknown provider"));
	}

	[Fact]
	public void Seed_and_auto_with_nothing_to_seed_is_a_config_problem()
	{
		var problems = ConfigValidation.Validate(
			PanelConfig(PanelMode.SeedAndAuto, new ModelRef("openrouter", "m"), personas: 0));

		Assert.Contains(problems, p => p.Message.Contains("no personas to seed"));
	}

	[Fact]
	public void A_well_formed_auto_config_validates()
	{
		Assert.Empty(ConfigValidation.Validate(PanelConfig(PanelMode.Auto, new ModelRef("openrouter", "m"))));
	}

	[Fact]
	public void An_auto_config_with_no_personas_is_valid_when_a_personaModel_is_given()
	{
		// The canonical auto setup: no configured personas at all, the orchestrator supplies them.
		// The CLI used to refuse to run this (see the #70 A/B), so it is worth pinning as valid.
		var config = new PeanutConfig(
			Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
			Personas: [],
			Repos: [new RepoTarget("r", ".")],
			Assignments: [],
			Panel: PanelMode.Auto,
			Orchestrator: new ModelRef("openrouter", "m"),
			PersonaModel: new ModelRef("openrouter", "m"));

		Assert.Empty(ConfigValidation.Validate(config));
	}
}
