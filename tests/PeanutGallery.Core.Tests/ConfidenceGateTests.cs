using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// A reviewer that posts every hunch trains its readers to skim. The gate acts on the
/// model's own admitted doubt - and always discloses what it dropped.
/// </summary>
public class ConfidenceGateTests
{
	private static Finding At(double confidence, string title = "t") =>
		new(Severity.Major, "a.cs", 1, title, "b", confidence);

	[Fact]
	public void Findings_below_the_threshold_are_suppressed()
	{
		var g = ConfidenceGate.Apply([At(0.9, "keep"), At(0.3, "drop"), At(0.6, "edge")], 0.6);

		Assert.Equal(2, g.Kept.Count);
		Assert.Equal(1, g.Suppressed.Count);
		Assert.DoesNotContain(g.Kept, f => f.Title == "drop");
		Assert.Contains(g.Kept, f => f.Title == "edge"); // the threshold is inclusive
	}

	[Fact]
	public void A_finding_with_no_stated_confidence_is_kept()
	{
		// Default 1.0: the gate may only suppress findings that explicitly admitted doubt,
		// never ones from a model or stored session that predates the field.
		var legacy = new Finding(Severity.Major, "a.cs", 1, "t", "b");

		Assert.Single(ConfidenceGate.Apply([legacy], 0.9).Kept);
	}

	[Fact]
	public void A_zero_threshold_disables_the_gate()
	{
		var all = new[] { At(0.0), At(0.1), At(1.0) };
		var g = ConfidenceGate.Apply(all, 0);

		Assert.Equal(3, g.Kept.Count);
		Assert.Empty(g.Suppressed);
	}

	[Fact]
	public void Out_of_range_thresholds_are_clamped()
	{
		Assert.Equal(3, ConfidenceGate.Apply([At(0.1), At(0.5), At(1.0)], -5).Kept.Count); // <=0 disables
		Assert.Equal(1, ConfidenceGate.Apply([At(0.1), At(0.5), At(1.0)], 42).Kept.Count); // clamps to 1.0
	}

	[Fact]
	public void An_empty_list_gates_to_empty()
	{
		var g = ConfidenceGate.Apply([], 0.6);

		Assert.Empty(g.Kept);
		Assert.Empty(g.Suppressed);
	}

	// ---- threshold resolution ----

	[Fact]
	public void The_threshold_falls_back_from_persona_to_config_to_default()
	{
		var config = TestData.FullConfig;
		var persona = TestData.BugHunter;

		Assert.Equal(ConfidenceGate.DefaultMinConfidence, ConfidenceGate.ThresholdFor(persona, config));
		Assert.Equal(0.4, ConfidenceGate.ThresholdFor(persona, config with { MinConfidence = 0.4 }));
		Assert.Equal(0.8, ConfidenceGate.ThresholdFor(
			persona with { MinConfidence = 0.8 }, config with { MinConfidence = 0.4 })); // persona wins
	}

	// ---- parsing ----

	[Fact]
	public void Confidence_is_parsed_from_a_number_or_a_numeric_string()
	{
		var findings = FindingsParser.Parse(
			"""{"findings":[{"title":"a","confidence":0.25},{"title":"b","confidence":"0.75"}]}""");

		Assert.Equal(0.25, findings[0].Confidence);
		Assert.Equal(0.75, findings[1].Confidence);
	}

	[Fact]
	public void A_missing_or_nonsense_confidence_reads_as_fully_confident()
	{
		var findings = FindingsParser.Parse(
			"""{"findings":[{"title":"a"},{"title":"b","confidence":"very sure"},{"title":"c","confidence":null}]}""");

		Assert.All(findings, f => Assert.Equal(1.0, f.Confidence));
	}

	[Fact]
	public void An_out_of_range_confidence_is_clamped_on_parse()
	{
		var findings = FindingsParser.Parse(
			"""{"findings":[{"title":"a","confidence":9},{"title":"b","confidence":-3}]}""");

		Assert.Equal(1.0, findings[0].Confidence);
		Assert.Equal(0.0, findings[1].Confidence);
	}

	[Fact]
	public void Confidence_survives_a_session_round_trip()
	{
		var session = new ReviewSession("abc", 1, "s", [At(0.35, "hedged")]);

		var back = SessionCodec.Extract(SessionCodec.Embed("x", session));

		Assert.Equal(0.35, Assert.Single(back!.OpenFindings).Confidence);
	}

	// ---- disclosure ----

	[Fact]
	public void The_comment_discloses_how_many_were_suppressed()
	{
		var update = new SessionUpdate("s", [At(0.9)], [], []);
		var gate = new GateResult(update.Findings, [At(0.1, "d1"), At(0.2, "d2")], 0.6);

		var body = SessionCommentRenderer.Render(
			TestData.BugHunter, ReviewSession.Initial, update, "abc1234", gate);

		Assert.Contains("2 low-confidence findings (below 0.6) were suppressed", body);
	}

	[Fact]
	public void One_suppressed_finding_reads_in_the_singular()
	{
		var update = new SessionUpdate("s", [], [], []);
		var body = SessionCommentRenderer.Render(
			TestData.BugHunter, ReviewSession.Initial, update, "abc1234", new GateResult([], [At(0.1, "d1")], 0.6));

		Assert.Contains("1 low-confidence finding (below 0.6) was suppressed", body);
	}

	[Fact]
	public void The_renderer_shows_the_gates_kept_set_not_the_raw_update()
	{
		// The gate owns visibility end to end: callers pass the full update and the renderer
		// shows what survived, so there is one source of truth for "what is visible".
		var update = new SessionUpdate("s", [At(0.9, "shown"), At(0.1, "hidden")], [], []);
		var gate = new GateResult([At(0.9, "shown")], [At(0.1, "hidden")], 0.6);

		var body = SessionCommentRenderer.Render(
			TestData.BugHunter, ReviewSession.Initial, update, "abc1234", gate);

		Assert.Contains("shown", body);
		Assert.DoesNotContain("hidden", body);
	}

	[Fact]
	public void No_gate_means_every_finding_is_shown()
	{
		var update = new SessionUpdate("s", [At(0.1, "hedged")], [], []);

		var body = SessionCommentRenderer.Render(
			TestData.BugHunter, ReviewSession.Initial, update, "abc1234");

		Assert.Contains("hedged", body);
		Assert.DoesNotContain("suppressed", body);
	}

	[Fact]
	public void Nothing_suppressed_means_no_disclosure_line()
	{
		var update = new SessionUpdate("s", [At(0.9)], [], []);
		var body = SessionCommentRenderer.Render(
			TestData.BugHunter, ReviewSession.Initial, update, "abc1234", new GateResult(update.Findings, [], 0.6));

		Assert.DoesNotContain("suppressed", body);
	}

	[Fact]
	public void The_protocol_asks_the_model_to_rate_confidence_honestly()
	{
		var req = SessionPlanner.Advance(
			TestData.BugHunter, new RepoTarget("demo", "/d"), ReviewSession.Initial, Diff.Empty, "sha");

		var system = Msg.System(req);
		Assert.Contains("confidence", system);
		Assert.Contains("honestly", system);
	}
}
