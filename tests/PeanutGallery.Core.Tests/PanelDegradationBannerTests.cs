using System.Collections.Generic;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// The decision-time degradation signal (#130): a SETTLED panel that lost a reviewer gets a
/// prominent banner plus a hidden, machine-readable <c>pg-degraded</c> marker, so a partial panel
/// never reads like a clean review to a human skimming or a merge gate polling.
/// </summary>
public class PanelDegradationBannerTests
{
	private static readonly SynthesisResult NoFindings = new([], 0);

	private static PanelReport Report(bool inProgress, params PanelMember[] members) =>
		new(members, NoFindings, [], [], 0, [], InProgress: inProgress);

	private static PanelMember Reported(string id) =>
		new(id, id, id, "openrouter/m", Reported: true);

	private static PanelMember Missing(string id, string reason) =>
		new(id, id, id, "openrouter/m", Reported: false, reason);

	[Fact]
	public void A_settled_panel_that_lost_a_reviewer_gets_a_prominent_banner_and_a_marker()
	{
		var body = PanelCommentRenderer.Render(
			Report(false, Reported("architect"), Missing("bug-hunter", "timed out")),
			"deadbeefcafe", 2);

		Assert.Contains("> [!WARNING]", body);
		Assert.Contains("1 reviewer did not report this run", body);
		Assert.Contains(PanelCommentRenderer.DegradedMarker(1), body);
		// The muted disclosure still names who and why - the banner points at it, does not replace it.
		Assert.Contains("Did not report", body);
	}

	[Fact]
	public void The_banner_pluralises_and_counts_every_non_reporting_reviewer()
	{
		var body = PanelCommentRenderer.Render(
			Report(false, Reported("a"), Missing("b", "timed out"), Missing("c", "truncated")),
			"sha", 1);

		Assert.Contains("2 reviewers did not report this run", body);
		Assert.Contains(PanelCommentRenderer.DegradedMarker(2), body);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void DegradedMarker_refuses_a_zero_or_negative_count(int count)
	{
		// pg-degraded:0 is a grep-target false positive - a clean panel that reads as degraded. The
		// marker names >=1 gap or it is a caller bug; fail loud rather than encode a misleading token.
		Assert.Throws<System.ArgumentOutOfRangeException>(() => PanelCommentRenderer.DegradedMarker(count));
	}

	[Fact]
	public void DegradedMarker_encodes_a_positive_count()
	{
		Assert.Equal("<!-- pg-degraded:3 -->", PanelCommentRenderer.DegradedMarker(3));
	}

	[Fact]
	public void A_full_panel_gets_no_banner_and_no_marker()
	{
		var body = PanelCommentRenderer.Render(
			Report(false, Reported("architect"), Reported("bug-hunter")), "sha", 3);

		Assert.DoesNotContain("[!WARNING]", body);
		Assert.DoesNotContain(PanelCommentRenderer.DegradedMarkerPrefix, body);
	}

	/// <summary>
	/// A reviewer that went missing without a reason is named on its own, and rendering it does not
	/// throw. <c>NotReportedReason</c> is nullable and <c>CommentRenderer.OneLine</c> takes a
	/// <c>string?</c>, returning empty for null — so folding it before the emptiness test reaches
	/// the name-only branch by the same route the pattern match did. Pinned because a panel review
	/// of #187 read the fold as an unguarded dereference.
	/// </summary>
	[Fact]
	public void A_reviewer_missing_without_a_reason_is_named_alone_and_does_not_throw()
	{
		var body = PanelCommentRenderer.Render(
			Report(false, Reported("architect"), new PanelMember("ghost", "Ghost", "g", "openrouter/m", false)),
			"sha", 3);

		Assert.Contains("_Did not report: Ghost._", body);
		Assert.DoesNotContain("Ghost ()", body);
	}

	[Fact]
	public void An_in_progress_panel_never_cries_wolf_a_pending_reviewer_is_not_degraded()
	{
		// While the review runs, a not-yet-reported reviewer is pending, not failed. Banner-ing it
		// would fire the degradation signal on every intermediate render.
		var body = PanelCommentRenderer.Render(
			Report(true, Reported("architect"), Missing("bug-hunter", "still reviewing")),
			"sha", 1);

		Assert.DoesNotContain("[!WARNING]", body);
		Assert.DoesNotContain(PanelCommentRenderer.DegradedMarkerPrefix, body);
	}
}
