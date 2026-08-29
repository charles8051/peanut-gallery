using System;
using System.Linq;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

public class RunSummaryTests
{
	private static PersonaResult Reviewed(string name, int findings, double seconds) =>
		new("id-" + name, name, PersonaOutcome.Reviewed, findings, "body",
			new PersonaObservability("openrouter:some/model", TimeSpan.FromSeconds(seconds), null));

	private static PersonaResult Failed(string name, string reason) =>
		new("id-" + name, name, PersonaOutcome.Failed, 0, "body",
			new PersonaObservability("openrouter:minimax/minimax-m2.5", TimeSpan.FromSeconds(600), reason));

	private static PersonaResult Unchanged(string name) =>
		new("id-" + name, name, PersonaOutcome.Unchanged, 0, null,
			new PersonaObservability("openrouter:some/model", TimeSpan.Zero, null));

	[Fact]
	public void DegradedCount_counts_only_failed_reviewers_not_unchanged_or_reviewed()
	{
		var personas = new[]
		{
			Reviewed("Bug Hunter", 2, 41),
			Failed("Race Hunter", "timed out"),
			Unchanged("Architect"),
			Failed("Parser Adversary", "truncated"),
		};

		// The one signal the opt-in fail gate (#130) keys off - Unchanged is a standing review, not a gap.
		Assert.Equal(2, RunSummary.DegradedCount(personas));
	}

	[Fact]
	public void DegradedCount_is_zero_for_a_clean_run()
	{
		var personas = new[] { Reviewed("Bug Hunter", 0, 12), Unchanged("Architect") };
		Assert.Equal(0, RunSummary.DegradedCount(personas));
	}

	[Fact]
	public void Summary_renders_a_row_per_persona_with_latency()
	{
		var md = RunSummary.RenderStepSummary(
			new[] { Reviewed("Bug Hunter", 2, 41), Unchanged("Architect") },
			"acme/api", 123, "abcdef1234567890");

		Assert.Contains("acme/api #123", md);
		Assert.Contains("Bug Hunter", md);
		Assert.Contains("✅ reviewed", md);
		Assert.Contains("41s", md);
		Assert.Contains("➖ unchanged", md);
		// A clean run carries no warning callout.
		Assert.DoesNotContain("[!WARNING]", md);
	}

	[Fact]
	public void A_degraded_persona_gets_a_warning_callout_and_a_reason_footnote()
	{
		var md = RunSummary.RenderStepSummary(
			new[] { Reviewed("Bug Hunter", 0, 30), Failed("General Reviewer", "operation exceeded 600s (after 2 attempts)") },
			"acme/api", 99, "deadbeefcafe");

		Assert.Contains("[!WARNING]", md);
		Assert.Contains("1 reviewer degraded", md);
		Assert.Contains("⚠️ degraded", md);
		Assert.Contains("operation exceeded 600s (after 2 attempts)", md);
	}

	[Fact]
	public void Annotations_are_emitted_only_for_degraded_personas()
	{
		var annotations = RunSummary.Annotations(new[]
		{
			Reviewed("Bug Hunter", 1, 20),
			Failed("General Reviewer", "operation exceeded 600s"),
			Unchanged("Architect"),
		});

		var line = Assert.Single(annotations);
		Assert.StartsWith("::warning title=Peanut Gallery::", line);
		Assert.Contains("General Reviewer", line);
		Assert.Contains("operation exceeded 600s", line);
	}

	[Fact]
	public void Annotation_reason_is_flattened_to_one_line()
	{
		var line = Assert.Single(RunSummary.Annotations(new[] { Failed("R", "line one\nline two") }));
		Assert.DoesNotContain('\n', line);
		Assert.Contains("line one line two", line);
	}

	[Fact]
	public void No_personas_renders_a_placeholder_not_a_table()
	{
		var md = RunSummary.RenderStepSummary(Array.Empty<PersonaResult>(), "o/r", 1, "sha");
		Assert.Contains("No personas assigned", md);
		Assert.DoesNotContain("| Persona |", md);
	}

	// ---- refutations are durable per run, unlike the self-overwriting comment ----

	[Fact]
	public void The_summary_records_what_the_adversarial_pass_dropped_and_why()
	{
		// The PR comment overwrites itself, so a refutation made on turn 2 is gone by turn 3.
		// Auditing whether the pass drops true findings needs the history, and the first such audit
		// had to reconstruct drops from titles alone because the grounds were never kept.
		var persona = new Persona(
			"bug-hunter", "Bug Hunter", "bug-hunter", ReviewTier.Diff,
			new ModelRef("openrouter", "some/model"), 0.2, "find bugs");
		var contribution = new PersonaContribution(
			persona, ReviewSession.Initial, [], [], [], 0,
			[new RefutedFinding("doc contradicts its own example", "the example was updated in this diff")]);
		var result = new PersonaResult(
			"bug-hunter", "Bug Hunter", PersonaOutcome.Reviewed, 0, "body",
			new PersonaObservability("openrouter:some/model", TimeSpan.FromSeconds(10), null),
			contribution);

		var md = RunSummary.RenderStepSummary([result], "charles8051/peanut-gallery", 1, "abcdef1234567890");

		Assert.Contains("1 finding dropped on the adversarial pass", md);
		Assert.Contains("doc contradicts its own example", md);
		Assert.Contains("the example was updated in this diff", md);
		Assert.Contains("Bug Hunter", md);
	}

	[Fact]
	public void A_run_with_no_refutations_has_no_refutation_section()
	{
		var md = RunSummary.RenderStepSummary(
			[Reviewed("Bug Hunter", 1, 10)], "charles8051/peanut-gallery", 1, "abcdef1234567890");

		Assert.DoesNotContain("adversarial pass", md);
	}
}
