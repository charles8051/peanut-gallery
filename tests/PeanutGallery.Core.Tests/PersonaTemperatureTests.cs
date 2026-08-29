using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// The one place a persona's sampling temperature is decided (#127). The defect was not a bad
/// default but a MISSING model of "absent": <c>Persona.Temperature</c> was a non-nullable
/// <c>double</c>, so a config that omitted the key was indistinguishable from one that chose 0 —
/// greedy decoding, the reasoning-runaway mode <c>ReviewBudget</c> documents. These tests pin the
/// three properties that fix has to keep: absent is never greedy, authored 0 still is, and no
/// decode path gets to answer "absent" for itself.
/// </summary>
public class PersonaTemperatureTests
{
	private static Persona P(double? temperature) => new(
		"seed", "Seed", "bugs", ReviewTier.Diff, new ModelRef("openrouter", "m"), temperature, "review it");

	private static readonly RepoTarget Repo = new("demo", "/repos/demo");

	[Fact]
	public void An_unset_temperature_resolves_to_the_default_and_is_never_greedy()
	{
		var persona = P(null);

		Assert.Null(persona.Temperature);
		Assert.Equal(PanelFence.DefaultTemperature, persona.SamplingTemperature());
		// The point of the whole change: whatever the default becomes, it may not become 0.
		Assert.NotEqual(0.0, persona.SamplingTemperature());
	}

	[Fact]
	public void An_authored_zero_is_honoured_and_not_raised_to_the_default()
	{
		// Greedy is a legitimate operator choice when it is CHOSEN. #127 is about the value nobody
		// chose; a fix that also overrode explicit 0 would be a different (and wrong) change.
		var persona = P(0.0);

		Assert.Equal(0.0, persona.Temperature);
		Assert.Equal(0.0, persona.SamplingTemperature());
	}

	[Theory]
	[InlineData(0.2)]
	[InlineData(1.0)]
	[InlineData(1.4)]
	public void An_authored_value_passes_through_untouched(double authored) =>
		Assert.Equal(authored, P(authored).SamplingTemperature());

	[Fact]
	public void PromptAssembly_sends_the_resolved_temperature_not_the_raw_default_of_a_double()
	{
		// The diff-tier path. Before the fix this request went out at 0.0 for a persona whose
		// config simply had no 'temperature' key.
		var req = PromptAssembly.Build(P(null), Repo, Diff.Empty);

		Assert.Equal(PanelFence.DefaultTemperature, req.Temperature);
	}

	[Fact]
	public void SessionPlanner_sends_the_resolved_temperature_on_the_pull_request_path()
	{
		// The PR path is the one every enrolled repo actually reviews through, so it needs its own
		// assertion rather than trusting that it shares PromptAssembly's resolution.
		var req = SessionPlanner.Advance(
			P(null), Repo, new ReviewSession("old", 1, "running", [], 5), Diff.Empty, "newsha");

		Assert.Equal(PanelFence.DefaultTemperature, req.Temperature);
	}

	[Fact]
	public void The_unset_notice_names_every_persona_that_left_the_knob_out()
	{
		// Resolution being safe is only half of it - the issue's complaint is also that nothing
		// said which value a config was sampling at. The core builds the sentence; the shells log it.
		var notice = Persona.UnsetTemperatureNotice(
			[P(null) with { Id = "architect" }, P(0.0) with { Id = "bug-hunter" }, P(null) with { Id = "contrarian" }]);

		Assert.NotNull(notice);
		Assert.Contains("architect", notice);
		Assert.Contains("contrarian", notice);
		Assert.DoesNotContain("bug-hunter", notice); // authored 0 is a choice, not an omission
		Assert.Contains(PanelFence.DefaultTemperature.ToString(), notice);
	}

	[Fact]
	public void The_unset_notice_is_null_when_every_persona_authored_a_temperature() =>
		Assert.Null(Persona.UnsetTemperatureNotice([P(0.0), P(1.0)]));

	[Fact]
	public void The_unset_notice_is_null_for_a_config_with_no_personas_at_all() =>
		Assert.Null(Persona.UnsetTemperatureNotice([]));

	[Fact]
	public void An_unset_temperature_is_not_a_validation_problem()
	{
		// Null is the documented "let the default stand" state, exactly like top_p/top_k. Flagging
		// it would turn every pre-existing config into a validation failure on upgrade.
		var config = TestData.FullConfig with { Personas = [P(null)] };

		Assert.DoesNotContain(
			ConfigValidation.Validate(config),
			p => p.Message.Contains("temperature"));
	}
}
