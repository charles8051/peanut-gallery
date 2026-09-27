using System.Collections.Generic;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// The commit status a required check reads. Success has to mean what <c>await-review</c>'s exit 0
/// means: the whole panel reported this commit and found nothing. Every other shape holds the merge.
/// </summary>
public class ReviewGateTests
{
	private const string Head = "aaaaaaaabbbbbbbbccccccccdddddddd11111111";
	private const string Previous = "9999999988888888777777776666666655555555";

	private static readonly PanelMember Architect = new("architect", "Architect", "layering", "openrouter/m", true);

	private static string Panel(
		IReadOnlyList<PanelMember> members, IReadOnlyList<AttributedFinding> findings, params (string Persona, string Sha)[] sessions)
	{
		var byPersona = new Dictionary<string, ReviewSession>();
		foreach (var (persona, sha) in sessions)
		{
			byPersona[persona] = new ReviewSession(sha, 2, "summary", []);
		}

		var visible = PanelCommentRenderer.Render(
			new PanelReport(members, new SynthesisResult(findings, 0), [], [], 0, []), Head, 2);
		return PanelSessionCodec.Embed(visible, new PanelSession(byPersona));
	}

	private static AttributedFinding AFinding() =>
		new(new Finding(Severity.Major, "a.cs", 12, "real thing", "body"), ["layering"]);

	private static CommitStatus After(string body) => ReviewGate.After(CommentMode.Panel, body, Head);

	[Fact]
	public void A_full_panel_with_an_empty_board_passes()
	{
		var status = After(Panel([Architect], [], ("architect", Head)));

		Assert.Equal(CommitState.Success, status.State);
		Assert.Contains("aaaaaaa", status.Description);
	}

	[Fact]
	public void Open_findings_fail_the_status()
	{
		Assert.Equal(CommitState.Failure, After(Panel([Architect], [AFinding()], ("architect", Head))).State);
	}

	/// <summary>
	/// A reviewer that timed out found nothing in the sense a closed eye sees nothing. Passing this
	/// would let a merge through on the lens that never looked.
	/// </summary>
	[Fact]
	public void An_empty_board_from_a_degraded_panel_does_not_pass()
	{
		var lost = new PanelMember("bug-hunter", "Bug Hunter", "bugs", "openrouter/m", false, "timed out");

		var status = After(Panel([Architect, lost], [], ("architect", Head)));

		Assert.Equal(CommitState.Error, status.State);
	}

	[Fact]
	public void A_panel_with_a_reviewer_still_on_an_older_commit_does_not_pass()
	{
		var hunter = new PanelMember("bug-hunter", "Bug Hunter", "bugs", "openrouter/m", true);

		var status = After(Panel([Architect, hunter], [], ("architect", Head), ("bug-hunter", Previous)));

		Assert.Equal(CommitState.Error, status.State);
	}

	/// <summary>Every reviewer failed this turn and kept the session it had: nothing reported this commit.</summary>
	[Fact]
	public void A_panel_where_no_reviewer_reached_the_head_does_not_pass()
	{
		Assert.Equal(CommitState.Error, After(Panel([Architect], [], ("architect", Previous))).State);
	}

	[Fact]
	public void A_run_that_rendered_no_panel_does_not_pass()
	{
		Assert.Equal(CommitState.Error, ReviewGate.After(CommentMode.Panel, null, Head).State);
	}

	/// <summary>
	/// Per-persona mode renders one comment per reviewer and no panel to judge. Passing on it would
	/// pass on whichever comment happened to be read, so the gate refuses the mode outright.
	/// </summary>
	[Fact]
	public void Per_persona_mode_cannot_be_judged_and_says_why()
	{
		var status = ReviewGate.After(CommentMode.PerPersona, Panel([Architect], [], ("architect", Head)), Head);

		Assert.Equal(CommitState.Error, status.State);
		Assert.Contains("panel", status.Description);
	}

	[Fact]
	public void A_review_in_progress_holds_the_merge()
	{
		Assert.Equal(CommitState.Pending, ReviewGate.Reviewing(Head).State);
	}

	[Fact]
	public void An_opted_out_pr_passes()
	{
		var status = ReviewGate.Skipped("label 'no-review'", isDraft: false);

		Assert.Equal(CommitState.Success, status.State);
		Assert.Contains("no-review", status.Description);
	}

	/// <summary>
	/// Marking a draft ready fires a review of the SAME commit. A success left over from the draft
	/// would let auto-merge go in the window before that review posted anything.
	/// </summary>
	[Fact]
	public void A_skipped_draft_stays_pending()
	{
		Assert.Equal(CommitState.Pending, ReviewGate.Skipped("draft PR", isDraft: true).State);
	}

	/// <summary>A skip reason carries repo-authored text; an over-long description would fail the post.</summary>
	[Fact]
	public void The_description_fits_the_api_limit()
	{
		var status = ReviewGate.Skipped("label '" + new string('x', 400) + "'", isDraft: false);

		Assert.Equal(CommitStatus.MaxDescription, status.Description.Length);
	}

	[Theory]
	[InlineData("1", true)]
	[InlineData("true", true)]
	[InlineData(" TRUE ", true)]
	[InlineData("yes", true)]
	[InlineData("false", false)]
	[InlineData("", false)]
	[InlineData(null, false)]
	[InlineData("0", false)]
	public void The_gate_is_off_unless_asked_for(string? raw, bool expected)
	{
		Assert.Equal(expected, ReviewGate.Enabled(raw));
	}
}
