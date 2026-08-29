using System.Linq;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// Dedup the issue, keep the voices - and never fuse two findings that are merely similar. An
/// over-merge silently deletes a real finding and the reader cannot tell; an under-merge is two
/// bullets they can see and judge.
/// </summary>
public class FindingSynthesisTests
{
	private static Finding F(string title, string file = "a.cs", int line = 10,
		Severity sev = Severity.Major, double confidence = 1.0, string body = "b") =>
		new(sev, file, line, title, body, confidence);

	private static PersonaFindings From(string lens, params Finding[] findings) =>
		new(lens, lens, findings);

	[Fact]
	public void Findings_from_several_personas_are_gathered()
	{
		var r = FindingSynthesis.Merge([
			From("architecture", F("layering violation")),
			From("bugs", F("null deref", "b.cs", 3)),
		]);

		Assert.Equal(2, r.Findings.Count);
		Assert.Equal(0, r.Merged);
	}

	[Fact]
	public void The_same_finding_from_two_personas_collapses_but_keeps_both_lenses()
	{
		var r = FindingSynthesis.Merge([
			From("architecture", F("Null deref in the parser")),
			From("bugs", F("null deref in the parser")),
		]);

		var only = Assert.Single(r.Findings);
		Assert.Equal(["architecture", "bugs"], only.Lenses);
		Assert.Equal(1, r.Merged);
	}

	[Fact]
	public void Title_matching_ignores_case_and_punctuation_only()
	{
		var r = FindingSynthesis.Merge([
			From("a", F("Off-by-one in the loop!")),
			From("b", F("off by one in the loop")),
		]);

		Assert.Single(r.Findings);
	}

	[Fact]
	public void Different_lines_are_different_findings()
	{
		var r = FindingSynthesis.Merge([
			From("a", F("null deref", "a.cs", 10)),
			From("b", F("null deref", "a.cs", 99)),
		]);

		Assert.Equal(2, r.Findings.Count);
		Assert.Equal(0, r.Merged);
	}

	[Fact]
	public void Different_files_are_different_findings()
	{
		var r = FindingSynthesis.Merge([
			From("a", F("null deref", "a.cs")),
			From("b", F("null deref", "b.cs")),
		]);

		Assert.Equal(2, r.Findings.Count);
	}

	[Fact]
	public void Merely_similar_titles_are_NOT_fused()
	{
		// The line this design refuses to cross: these could be one issue or two, and guessing
		// wrong deletes a real finding invisibly.
		var r = FindingSynthesis.Merge([
			From("a", F("null deref in the parser")),
			From("b", F("parser crashes on empty input")),
		]);

		Assert.Equal(2, r.Findings.Count);
	}

	[Fact]
	public void The_more_alarming_report_of_one_issue_wins()
	{
		// A reader should see the worst case anyone made, not whoever was enumerated first.
		var r = FindingSynthesis.Merge([
			From("a", F("same issue", sev: Severity.Minor)),
			From("b", F("same issue", sev: Severity.Critical)),
		]);

		Assert.Equal(Severity.Critical, Assert.Single(r.Findings).Finding.Severity);
	}

	[Fact]
	public void At_equal_severity_the_more_confident_report_wins()
	{
		var r = FindingSynthesis.Merge([
			From("a", F("same issue", confidence: 0.6)),
			From("b", F("same issue", confidence: 0.95)),
		]);

		Assert.Equal(0.95, Assert.Single(r.Findings).Finding.Confidence);
	}

	[Fact]
	public void At_equal_severity_and_confidence_the_better_explained_report_wins()
	{
		var r = FindingSynthesis.Merge([
			From("a", F("same issue", body: "short")),
			From("b", F("same issue", body: "a much longer explanation of the failure")),
		]);

		Assert.Contains("much longer", Assert.Single(r.Findings).Finding.Body);
	}

	[Fact]
	public void One_persona_reporting_a_thing_twice_does_not_inflate_its_lens_list()
	{
		var r = FindingSynthesis.Merge([From("a", F("same issue"), F("same issue"))]);

		Assert.Single(Assert.Single(r.Findings).Lenses);
	}

	[Fact]
	public void Nothing_to_merge_is_an_empty_result()
	{
		var r = FindingSynthesis.Merge([]);

		Assert.Empty(r.Findings);
		Assert.Equal(0, r.Merged);
	}

	// ---- rendering ----

	private static PanelReport Report(SynthesisResult synthesis, params PanelMember[] members) =>
		new(members, synthesis, [], [], 0, []);

	private static PanelMember Member(string id, bool reported = true, string? reason = null) =>
		new(id, id, id, "openrouter:m", reported, reason);

	[Fact]
	public void The_panel_comment_attributes_each_finding_to_its_lenses()
	{
		var synthesis = FindingSynthesis.Merge([
			From("architecture", F("layering violation")),
			From("bugs", F("layering violation")),
		]);

		var body = PanelCommentRenderer.Render(
			Report(synthesis, Member("architecture"), Member("bugs")), "abc1234", 1);

		Assert.Contains("layering violation", body);
		Assert.Contains("(architecture, bugs)", body);
	}

	[Fact]
	public void The_panel_comment_names_a_persona_that_did_not_report()
	{
		// With one comment, a failed persona has nowhere else to be visible.
		var body = PanelCommentRenderer.Render(
			Report(FindingSynthesis.Merge([]), Member("architect"), Member("bug-hunter", false, "provider timeout")),
			"abc1234", 2);

		Assert.Contains("Did not report", body);
		Assert.Contains("bug-hunter", body);
		Assert.Contains("provider timeout", body);
	}

	[Fact]
	public void Everything_removed_from_view_is_disclosed()
	{
		var synthesis = FindingSynthesis.Merge([
			From("a", F("dupe")),
			From("b", F("dupe")),
		]);
		var report = new PanelReport([Member("a"), Member("b")], synthesis, [], [], 3,
			[new RefutedFinding("a claim", "the caller cannot reach it"),
			 new RefutedFinding("another", "the doc matches the code")]);

		var body = PanelCommentRenderer.Render(report, "abc1234", 1);

		Assert.Contains("1 duplicate report(s) merged", body);
		Assert.Contains("3 low-confidence finding(s) suppressed", body);
		Assert.Contains("2 findings dropped on an adversarial second pass", body);
		Assert.Contains("the caller cannot reach it", body); // the grounds, not just a count
	}

	[Fact]
	public void An_empty_panel_review_says_so()
	{
		var body = PanelCommentRenderer.Render(
			Report(FindingSynthesis.Merge([]), Member("a")), "abc1234", 1);

		Assert.Contains("_No findings._", body);
	}

	[Fact]
	public void The_panel_comment_carries_one_stable_marker_so_it_upserts_in_place()
	{
		var body = PanelCommentRenderer.Render(Report(FindingSynthesis.Merge([]), Member("a")), "abc1234", 1);

		Assert.Equal(CommentRenderer.Marker(PanelCommentRenderer.PanelId), CommentSync.MarkerOf(body));
	}

	[Fact]
	public void Resolved_and_withdrawn_are_reported_once_across_the_whole_panel()
	{
		var report = new PanelReport(
			[Member("a")], FindingSynthesis.Merge([]), ["fixed thing"], ["intentional thing"], 0, []);

		var body = PanelCommentRenderer.Render(report, "abc1234", 2);

		Assert.Contains("**Resolved since last push:** fixed thing", body);
		Assert.Contains("**Withdrawn (author-explained):** intentional thing", body);
	}

	// ---- session store ----

	[Fact]
	public void Every_personas_session_round_trips_in_one_blob()
	{
		var session = new PanelSession(new System.Collections.Generic.Dictionary<string, ReviewSession>
		{
			["architect"] = new("sha1", 3, "running", [F("open one")], 42, ["dropped one"]),
			["bug-hunter"] = new("sha1", 1, "other", []),
		});

		var back = PanelSessionCodec.Extract(PanelSessionCodec.Embed("### visible", session));

		Assert.NotNull(back);
		Assert.Equal(3, back!.For("architect").Turn);
		Assert.Equal("running", back.For("architect").Summary);
		Assert.Equal(42, back.For("architect").LastSeenCommentId);
		Assert.Equal(["dropped one"], back.For("architect").DroppedTitles);
		Assert.Equal("open one", Assert.Single(back.For("architect").OpenFindings).Title);
		Assert.Equal(1, back.For("bug-hunter").Turn);
	}

	[Fact]
	public void An_unknown_persona_reads_as_a_fresh_session()
	{
		Assert.True(PanelSession.Empty.For("nobody").IsFirstTurn);
	}

	[Fact]
	public void A_body_with_no_panel_state_extracts_to_null()
	{
		Assert.Null(PanelSessionCodec.Extract("a normal human PR comment"));
		Assert.Null(PanelSessionCodec.Extract("<!-- pg-panel-state:1:not-base64!! -->"));
	}

	[Fact]
	public void Panel_state_coexists_with_the_pinned_panel_blob()
	{
		var persona = new Persona("a", "A", "a", ReviewTier.Diff, new ModelRef("p", "m"), 0.2, "x");
		var body = PanelCodec.Embed(
			PanelSessionCodec.Embed("### visible", PanelSession.Empty),
			new PinnedPanel([persona], PanelMode.Auto, "sha"));

		Assert.NotNull(PanelCodec.Extract(body));
		Assert.NotNull(PanelSessionCodec.Extract(body));
	}
}
