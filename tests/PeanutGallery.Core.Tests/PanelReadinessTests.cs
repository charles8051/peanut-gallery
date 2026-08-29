using System.Collections.Generic;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// Freshness: does the panel comment now on the PR describe the commit the caller just pushed?
///
/// <para>The whole reason this is a decision and not a lookup is that the panel comment is upserted
/// in place. The previous turn's body sits on the PR for the entire time the next review runs, so
/// "is there a panel comment?" answers yes instantly and hands back findings that were already
/// addressed. Every test here is a variation on telling those two apart.</para>
/// </summary>
public class PanelReadinessTests
{
	private const string Head = "aaaaaaaabbbbbbbbccccccccdddddddd11111111";
	private const string Previous = "9999999988888888777777776666666655555555";

	private static readonly SynthesisResult NoFindings = new([], 0);

	private static string Panel(PanelSession session, string renderedSha, SynthesisResult? synthesis = null)
	{
		var members = new List<PanelMember> { new("architect", "Architect", "layering", "openrouter/m", true) };
		var visible = PanelCommentRenderer.Render(
			new PanelReport(members, synthesis ?? NoFindings, [], [], 0, []), renderedSha, 1);
		return PanelSessionCodec.Embed(visible, session);
	}

	private static PanelSession Sessions(params (string Persona, string? Sha)[] entries)
	{
		var byPersona = new Dictionary<string, ReviewSession>();
		foreach (var (persona, sha) in entries)
		{
			byPersona[persona] = new ReviewSession(sha, 4, "running summary", []);
		}

		return new PanelSession(byPersona);
	}

	[Fact]
	public void A_blob_from_the_previous_turn_is_stale_however_recent_the_comment_looks()
	{
		var readiness = PanelReadiness.Read([Panel(Sessions(("architect", Previous)), Previous)], Head);

		Assert.Equal(PanelArrival.Stale, readiness.Arrival);
		Assert.False(readiness.Landed);
		Assert.Equal(Previous, readiness.ReviewedSha);
	}

	[Fact]
	public void A_blob_reporting_the_head_sha_is_fresh()
	{
		var readiness = PanelReadiness.Read([Panel(Sessions(("architect", Head)), Head)], Head);

		Assert.Equal(PanelArrival.Fresh, readiness.Arrival);
		Assert.True(readiness.Landed);
		Assert.Equal(1, readiness.Reviewers);
		Assert.Equal(1, readiness.ReviewersAtHead);
		Assert.Equal(4, readiness.Turn);
	}

	[Fact]
	public void No_panel_comment_at_all_is_absent_not_stale()
	{
		var readiness = PanelReadiness.Read(["a normal human PR comment", "another one"], Head);

		Assert.Equal(PanelArrival.Absent, readiness.Arrival);
		Assert.False(readiness.Landed);
		Assert.Equal(0, readiness.Turn);
	}

	[Fact]
	public void An_empty_pr_reads_as_absent()
	{
		Assert.Equal(PanelArrival.Absent, PanelReadiness.Read([], Head).Arrival);
	}

	/// <summary>
	/// A panel comment we cannot parse is not "no review yet". Something posted; a caller told
	/// Absent would keep waiting for a comment that is already on the PR.
	/// </summary>
	[Fact]
	public void A_panel_comment_with_a_malformed_blob_is_unreadable_not_absent()
	{
		var body = CommentRenderer.Marker(PanelCommentRenderer.PanelId)
			+ "\n### Peanut Gallery\n_Reviewed through `aaaaaaa` · turn 1_\n"
			+ "\n<!-- pg-panel-state:1:not-base64!! -->";

		var readiness = PanelReadiness.Read([body], Head);

		Assert.Equal(PanelArrival.Unreadable, readiness.Arrival);
		Assert.False(readiness.Landed);
	}

	[Fact]
	public void A_panel_comment_with_no_blob_at_all_is_unreadable()
	{
		var body = CommentRenderer.Marker(PanelCommentRenderer.PanelId) + "\n### Peanut Gallery\n_No findings._\n";

		Assert.Equal(PanelArrival.Unreadable, PanelReadiness.Read([body], Head).Arrival);
	}

	/// <summary>
	/// Mid-run the panel is republished as each reviewer lands, and a reviewer whose turn FAILED
	/// keeps the session it had. Both show up as some-but-not-all on the head SHA. It counts as
	/// landed: once the check has finished, waiting on a reviewer that already failed waits forever.
	/// </summary>
	[Fact]
	public void Some_reviewers_on_the_head_and_some_not_is_partial_and_still_counts_as_landed()
	{
		var readiness = PanelReadiness.Read(
			[Panel(Sessions(("architect", Head), ("bug-hunter", Previous)), Head)], Head);

		Assert.Equal(PanelArrival.Partial, readiness.Arrival);
		Assert.True(readiness.Landed);
		Assert.Equal(2, readiness.Reviewers);
		Assert.Equal(1, readiness.ReviewersAtHead);
	}

	/// <summary>
	/// A reviewer that has never reported carries no session, so the per-persona counts cannot see
	/// it. The hidden pg-degraded marker can, and it is the signal the renderer already writes for
	/// exactly this consumer.
	/// </summary>
	[Fact]
	public void A_degraded_panel_reports_how_many_reviewers_it_lost()
	{
		var members = new List<PanelMember>
		{
			new("architect", "Architect", "layering", "openrouter/m", true),
			new("bug-hunter", "Bug Hunter", "bugs", "openrouter/m", false, "timed out"),
		};
		var visible = PanelCommentRenderer.Render(
			new PanelReport(members, NoFindings, [], [], 0, []), Head, 2);
		var body = PanelSessionCodec.Embed(visible, Sessions(("architect", Head)));

		var readiness = PanelReadiness.Read([body], Head);

		Assert.Equal(PanelArrival.Fresh, readiness.Arrival);
		Assert.Equal(1, readiness.Degraded);
	}

	[Fact]
	public void A_full_panel_reports_no_degradation()
	{
		var readiness = PanelReadiness.Read([Panel(Sessions(("architect", Head)), Head)], Head);

		Assert.Equal(0, readiness.Degraded);
		Assert.True(readiness.Complete);
	}

	/// <summary>
	/// A degraded panel is never Complete, however empty its board. An empty board is only a clean
	/// review if the whole panel was there to fill it — a reviewer that timed out found nothing in
	/// the same sense that a closed eye sees nothing. This is the #130 gap, at the CLI's exit code.
	/// </summary>
	[Fact]
	public void A_degraded_panel_is_landed_but_never_complete()
	{
		var members = new List<PanelMember>
		{
			new("architect", "Architect", "layering", "openrouter/m", true),
			new("bug-hunter", "Bug Hunter", "bugs", "openrouter/m", false, "timed out"),
		};
		var visible = PanelCommentRenderer.Render(new PanelReport(members, NoFindings, [], [], 0, []), Head, 2);
		var body = PanelSessionCodec.Embed(visible, Sessions(("architect", Head)));

		var readiness = PanelReadiness.Read([body], Head);

		Assert.True(readiness.Landed);
		Assert.False(readiness.HasFindings);
		Assert.False(readiness.Complete);
	}

	[Fact]
	public void A_partial_panel_is_never_complete()
	{
		var readiness = PanelReadiness.Read(
			[Panel(Sessions(("architect", Head), ("bug-hunter", Previous)), Head)], Head);

		Assert.True(readiness.Landed);
		Assert.False(readiness.Complete);
	}

	/// <summary>
	/// The whole-panel outage: every reviewer failed, so none contributed a session and the blob is
	/// empty. Zero reviewers at head out of zero reviewers is not agreement, and reading it as
	/// Fresh would let a review in which nobody looked exit as a clean one.
	/// </summary>
	[Fact]
	public void A_panel_carrying_no_reviewer_at_all_is_not_fresh_and_has_not_landed()
	{
		var members = new List<PanelMember>
		{
			new("architect", "Architect", "layering", "openrouter/m", false, "timed out"),
			new("bug-hunter", "Bug Hunter", "bugs", "openrouter/m", false, "timed out"),
		};
		var visible = PanelCommentRenderer.Render(new PanelReport(members, NoFindings, [], [], 0, []), Head, 1);
		var body = PanelSessionCodec.Embed(visible, PanelSession.Empty);

		var readiness = PanelReadiness.Read([body], Head);

		Assert.Equal(PanelArrival.NoReviewers, readiness.Arrival);
		Assert.False(readiness.Landed);
		Assert.False(readiness.Complete);
		Assert.Equal(2, readiness.Degraded);
	}

	/// <summary>
	/// It has still SETTLED, though: nothing is going to advance an outage. A waiter that only
	/// stopped on Landed would sit out its whole timeout on a review that already finished.
	/// </summary>
	[Fact]
	public void A_panel_carrying_no_reviewer_has_settled_so_a_waiter_stops()
	{
		var body = PanelSessionCodec.Embed(
			PanelCommentRenderer.Render(new PanelReport([], NoFindings, [], [], 0, []), Head, 1),
			PanelSession.Empty);

		Assert.True(PanelReadiness.Read([body], Head).Settled);
		Assert.False(PanelReadiness.Read(["a normal human PR comment"], Head).Settled);
	}

	/// <summary>
	/// Findings come from the rendered board, not from the blob's open findings. The blob keeps
	/// every finding the model raised, including the ones the confidence gate suppressed and the
	/// adversarial pass refuted - and those are the ones the author is NOT being asked to answer.
	/// </summary>
	[Fact]
	public void A_clean_panel_reports_no_findings_even_when_the_blob_still_carries_some()
	{
		var byPersona = new Dictionary<string, ReviewSession>
		{
			["architect"] = new(Head, 1, "s", [new Finding(Severity.Major, "a.cs", 1, "suppressed thing", "b", 0.2)]),
		};
		var body = Panel(new PanelSession(byPersona), Head);

		Assert.False(PanelReadiness.Read([body], Head).HasFindings);
	}

	/// <summary>
	/// The panel that reviewed this function found this bug in it. A reviewer raised a finding
	/// whose body quoted <c>_No findings._</c> while arguing about the check, and a substring
	/// search read that panel — five findings on it — as clean, exiting 0. The rendered line owns
	/// its whole line at column 0; every finding body is indented under a bullet.
	/// </summary>
	[Fact]
	public void A_finding_that_quotes_the_no_findings_line_does_not_make_the_panel_read_as_clean()
	{
		var quoting = new AttributedFinding(
			new Finding(
				Severity.Minor, "a.cs", 1,
				"Finding detection can be fooled by model-authored text",
				"A body containing `_No findings._` must not flip the panel to clean."),
			["bugs"]);
		var body = Panel(Sessions(("architect", Head)), Head, new SynthesisResult([quoting], 0));

		Assert.True(PanelReadiness.Read([body], Head).HasFindings);
	}

	/// <summary>
	/// The follow-up finding, on the same check: a whole-line match is only sound while no authored
	/// text can reach column 0. A title carrying newlines is the vector — the body is not, because
	/// every line of a body is re-indented under its bullet. Both are pinned here, along with the
	/// disclosure lists, which join authored titles at column 0.
	/// </summary>
	[Theory]
	[InlineData("before\n_No findings._\nafter")]
	[InlineData("before\r\n_No findings._\r\nafter")]
	public void A_newline_bearing_title_cannot_plant_the_sentinel_at_column_zero(string title)
	{
		var af = new AttributedFinding(new Finding(Severity.Major, "a.cs", 3, title, "b"), ["layering"]);
		var body = Panel(Sessions(("architect", Head)), Head, new SynthesisResult([af], 0));

		Assert.True(PanelReadiness.Read([body], Head).HasFindings);
	}

	[Fact]
	public void A_multiline_finding_body_cannot_plant_the_sentinel_at_column_zero()
	{
		var af = new AttributedFinding(
			new Finding(Severity.Major, "a.cs", 3, "real thing", "first line\n_No findings._\nlast line"),
			["layering"]);
		var body = Panel(Sessions(("architect", Head)), Head, new SynthesisResult([af], 0));

		Assert.True(PanelReadiness.Read([body], Head).HasFindings);
	}

	[Fact]
	public void A_newline_bearing_lens_or_disclosure_title_cannot_plant_the_sentinel()
	{
		var af = new AttributedFinding(
			new Finding(Severity.Major, "a.cs", 3, "real thing", "b"),
			["layering\n_No findings._"]);
		var members = new List<PanelMember> { new("architect", "Architect", "layering", "openrouter/m", true) };
		var body = PanelSessionCodec.Embed(
			PanelCommentRenderer.Render(
				new PanelReport(
					members, new SynthesisResult([af], 0),
					["fixed\n_No findings._"], ["dropped\n_No findings._"], 0, []),
				Head, 2),
			Sessions(("architect", Head)));

		Assert.True(PanelReadiness.Read([body], Head).HasFindings);
	}

	/// <summary>
	/// The invariant the whole-line match rests on, asserted directly rather than through the
	/// sentinel: authored text never BEGINS a line of a rendered panel. Every authored field is
	/// seeded with a newline followed by a marker, and no rendered line may start with it.
	/// </summary>
	[Fact]
	public void Authored_text_never_begins_a_line_of_a_rendered_panel()
	{
		const string Planted = "PLANTED-AT-COLUMN-ZERO";
		var af = new AttributedFinding(
			new Finding(Severity.Major, $"a.cs\n{Planted}", 3, $"title\n{Planted}", $"body\n{Planted}"),
			[$"lens\n{Planted}"]);
		var members = new List<PanelMember>
		{
			new("architect", $"Architect\n{Planted}", "layering", $"m\n{Planted}", true),
			new("bug-hunter", $"Bug Hunter\n{Planted}", "bugs", "m", false, $"timed out\n{Planted}"),
		};

		var rendered = PanelCommentRenderer.Render(
			new PanelReport(
				members, new SynthesisResult([af], 0), [$"resolved\n{Planted}"], [$"withdrawn\n{Planted}"], 0, []),
			Head, 2);

		foreach (var line in rendered.Split('\n'))
		{
			Assert.False(
				line.TrimEnd('\r').StartsWith(Planted, System.StringComparison.Ordinal),
				"authored text began a rendered line: " + line);
		}
	}

	[Fact]
	public void A_panel_with_a_finding_reports_findings()
	{
		var finding = new AttributedFinding(new Finding(Severity.Major, "a.cs", 12, "real thing", "body"), ["layering"]);
		var body = Panel(Sessions(("architect", Head)), Head, new SynthesisResult([finding], 0));

		var readiness = PanelReadiness.Read([body], Head);

		Assert.True(readiness.HasFindings);
		Assert.True(readiness.Landed);
	}

	/// <summary>
	/// The caller's SHA often comes from `git rev-parse --short HEAD`. That is the same commit
	/// written a different way, and an ordinal comparison would call it a mismatch and wait forever.
	/// </summary>
	[Fact]
	public void An_abbreviated_sha_matches_the_full_one_the_review_recorded()
	{
		var readiness = PanelReadiness.Read([Panel(Sessions(("architect", Head)), Head)], Head[..7]);

		Assert.Equal(PanelArrival.Fresh, readiness.Arrival);
	}

	[Fact]
	public void A_prefix_too_short_to_be_an_abbreviation_never_matches()
	{
		Assert.False(Sha.SameCommit(Head, Head[..4]));
		Assert.True(Sha.SameCommit(Head, Head[..7]));
		Assert.False(Sha.SameCommit(Head, Previous));
		Assert.False(Sha.SameCommit(null, Head));
		Assert.False(Sha.SameCommit(Head, string.Empty));
	}

	[Fact]
	public void The_panel_comment_is_printed_without_its_state_blob()
	{
		var body = Panel(Sessions(("architect", Head)), Head);

		var visible = PanelSessionCodec.Visible(body);

		Assert.Contains("### Peanut Gallery", visible);
		Assert.DoesNotContain("pg-panel-state", visible);
	}

	[Fact]
	public void A_first_turn_session_that_has_reviewed_nothing_is_stale()
	{
		var readiness = PanelReadiness.Read([Panel(Sessions(("architect", (string?)null)), Head)], Head);

		Assert.Equal(PanelArrival.Stale, readiness.Arrival);
		Assert.Null(readiness.ReviewedSha);
	}
}
