using System;
using System.Collections.Generic;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

public class MetricsCollectorTests
{
	private static readonly RunContext Ctx =
		new("acme/api", 7, "abc1234", "2026-07-27T06:00:00Z", "seedAndAuto");

	private static Persona Persona(string id, string lens) =>
		new(id, id + " name", lens, ReviewTier.Diff, new ModelRef("openrouter", "minimax/minimax-m3"), 0.2, "review");

	[Fact]
	public void A_reviewed_persona_maps_its_funnel_tokens_and_no_failure()
	{
		var persona = Persona("bug-hunter", "bug-hunter");
		var contribution = new PersonaContribution(
			persona, ReviewSession.Initial,
			Posted: [new Finding(Severity.Major, "f.cs", 1, "bug", "body")],
			Resolved: [], Withdrawn: [],
			Suppressed: 1,
			Refuted: [new RefutedFinding("nope", "guarded")]);
		var result = new PersonaResult(
			"bug-hunter", "Bug Hunter", PersonaOutcome.Reviewed, 1, "body",
			new PersonaObservability("openrouter:minimax/minimax-m3", TimeSpan.FromSeconds(12), null,
				new ModelUsage(30000, 400, CachedInputTokens: 18000),
				new ModelUsage(5000, 200, CachedInputTokens: 1000), Attempts: 2),
			contribution);

		var m = MetricsCollector.From(new ReviewRunResult([result]), Ctx);
		var p = Assert.Single(m.Personas);

		Assert.Equal("bug-hunter", p.Lens);
		Assert.Equal("Diff", p.Tier);
		Assert.Equal("Reviewed", p.Outcome);
		Assert.Equal(FailureClass.None, p.Failure);
		Assert.Equal(12000, p.ElapsedMs);
		Assert.Equal(1, p.Posted);
		Assert.Equal(1, p.Suppressed);
		Assert.Equal(1, p.Refuted);
		Assert.Equal(3, p.Raised);            // posted 1 + suppressed 1 + refuted 1
		Assert.Equal(30000, p.InputTokens);
		Assert.Equal(200, p.VerifyOutputTokens);
		Assert.Equal(2, p.Attempts); // the review path re-issued once (a retry), threaded from observability
		Assert.Equal(18000, p.CachedInputTokens);
		Assert.Equal(1000, p.VerifyCachedInputTokens);
	}

	[Fact]
	public void The_authors_verdict_is_read_off_the_contribution_instead_of_being_discarded()
	{
		var persona = Persona("bug-hunter", "bug-hunter");
		var contribution = new PersonaContribution(
			persona, ReviewSession.Initial,
			Posted: [new Finding(Severity.Major, "f.cs", 1, "still open", "body")],
			Resolved: ["the author fixed this", "and this"],
			Withdrawn: ["the author said this was intentional"],
			Suppressed: 0,
			Refuted: []);
		var result = new PersonaResult(
			"bug-hunter", "Bug Hunter", PersonaOutcome.Reviewed, 1, "body",
			new PersonaObservability("openrouter:minimax/minimax-m3", TimeSpan.FromSeconds(12), null),
			contribution);

		var m = MetricsCollector.From(new ReviewRunResult([result]), Ctx);
		var p = Assert.Single(m.Personas);

		Assert.Equal(2, p.Resolved);
		Assert.Equal(1, p.Withdrawn);
		Assert.Equal(2, m.ResolvedTotal);
		Assert.Equal(1, m.WithdrawnTotal);
		// Verdicts are NOT findings the persona raised this turn: Raised stays posted+suppressed+refuted.
		Assert.Equal(1, p.Raised);
		Assert.True(m.RecordsAuthorVerdicts);
	}

	[Fact]
	public void A_persona_with_no_contribution_reports_no_verdicts_and_invents_none()
	{
		// Failed and Unchanged both arrive with Contribution: null. Nothing was reconciled, so nothing
		// was ruled on — 0, and the run line still stamps the current schema, so the report reads it
		// as "this persona ruled on nothing" rather than "this line predates the field".
		var failed = new PersonaResult(
			"reviewer", "Reviewer", PersonaOutcome.Failed, 0, null,
			new PersonaObservability("m", TimeSpan.FromSeconds(1), "boom"), Contribution: null);
		var unchanged = new PersonaResult(
			"architect", "Architect", PersonaOutcome.Unchanged, 0, null,
			new PersonaObservability("m", TimeSpan.Zero, null), Contribution: null);

		var m = MetricsCollector.From(new ReviewRunResult([failed, unchanged]), Ctx);

		Assert.All(m.Personas, p => Assert.Equal(0, p.Resolved));
		Assert.All(m.Personas, p => Assert.Equal(0, p.Withdrawn));
		Assert.True(m.RecordsAuthorVerdicts);
	}

	[Fact]
	public void A_failed_persona_is_classified_from_its_failure_reason()
	{
		var result = new PersonaResult(
			"reviewer", "Reviewer", PersonaOutcome.Failed, 0, null,
			new PersonaObservability("openrouter:minimax/minimax-m3", TimeSpan.FromSeconds(180),
				"Unknown ChatFinishReason value. (Parameter 'value')"),
			Contribution: null);

		var m = MetricsCollector.From(new ReviewRunResult([result]), Ctx);
		var p = Assert.Single(m.Personas);

		Assert.Equal(FailureClass.FinishReasonError, p.Failure);
		Assert.Equal(0, p.Posted);
		Assert.Equal(1, m.Degraded);
		Assert.Equal("", p.Lens); // no contribution -> no persona -> falls back to empty
	}

	[Fact]
	public void A_structural_failure_kind_wins_over_the_reason_text()
	{
		// A per-persona TimeBox timeout: the runner caught the TimeoutException and set FailureKind,
		// so the fold uses it and does NOT parse the reason prose (which the core would bucket as
		// Other). This is what keeps the pure core from depending on how the shell worded the message.
		var result = new PersonaResult(
			"reviewer", "Reviewer", PersonaOutcome.Failed, 0, null,
			new PersonaObservability("openrouter:minimax/minimax-m3", TimeSpan.FromSeconds(600),
				"the review did not finish within its 600s budget", FailureKind: FailureClass.Timeout),
			Contribution: null);

		var p = Assert.Single(MetricsCollector.From(new ReviewRunResult([result]), Ctx).Personas);
		Assert.Equal(FailureClass.Timeout, p.Failure); // structural, not re-derived from the text
	}

	[Fact]
	public void The_run_context_is_carried_onto_the_value()
	{
		var m = MetricsCollector.From(new ReviewRunResult([]), Ctx);
		Assert.Equal("acme/api", m.Context.Repo);
		Assert.Equal(7, m.Context.Pr);
		Assert.Equal("seedAndAuto", m.Context.Panel);
		Assert.Empty(m.Personas);
	}
}
