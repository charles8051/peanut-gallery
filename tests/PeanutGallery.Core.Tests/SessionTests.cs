using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

public class SessionTests
{
	private static readonly RepoTarget Repo = new("demo", "/repos/demo");

	// ---- SessionCodec (state lives inside the comment) ----

	[Fact]
	public void Codec_round_trips_a_session_and_keeps_the_marker_on_top()
	{
		var session = new ReviewSession("abc1234", 3, "running summary",
			[new Finding(Severity.Major, "a.cs", 5, "t", "b")]);
		var visible = CommentRenderer.Marker("architect") + "\n### A\n_No findings._\n";

		var body = SessionCodec.Embed(visible, session);
		Assert.StartsWith(CommentRenderer.Marker("architect"), body); // upsert still matches by marker

		var back = SessionCodec.Extract(body);
		Assert.NotNull(back);
		Assert.Equal("abc1234", back!.LastReviewedSha);
		Assert.Equal(3, back.Turn);
		Assert.Equal("running summary", back.Summary);
		var f = Assert.Single(back.OpenFindings);
		Assert.Equal(Severity.Major, f.Severity);
		Assert.Equal("a.cs", f.File);
		Assert.Equal(5, f.Line);
	}

	[Fact]
	public void Codec_round_trips_the_initial_session_with_a_null_sha()
	{
		var back = SessionCodec.Extract(SessionCodec.Embed("x", ReviewSession.Initial));
		Assert.NotNull(back);
		Assert.Null(back!.LastReviewedSha);
		Assert.Equal(0, back.Turn);
		Assert.Empty(back.OpenFindings);
		Assert.True(back.IsFirstTurn);
	}

	[Fact]
	public void Codec_returns_null_when_no_state_is_present()
	{
		Assert.Null(SessionCodec.Extract("a normal human PR comment with no embedded state"));
	}

	// ---- SessionUpdateParser ----

	[Fact]
	public void Parses_summary_findings_and_resolved()
	{
		var u = SessionUpdateParser.Parse(
			"""{"summary":"s","findings":[{"severity":"major","title":"t","body":"b"}],"resolved":["old one"]}""");
		Assert.Equal("s", u.Summary);
		Assert.Single(u.Findings);
		Assert.Equal("old one", Assert.Single(u.Resolved));
	}

	[Fact]
	public void Parses_through_prose_and_code_fences()
	{
		var u = SessionUpdateParser.Parse("Sure:\n```json\n{\"summary\":\"x\",\"findings\":[],\"resolved\":[]}\n```\n");
		Assert.Equal("x", u.Summary);
	}

	[Fact]
	public void Unparseable_reply_yields_an_empty_update()
	{
		var u = SessionUpdateParser.Parse("no json at all");
		Assert.Equal(string.Empty, u.Summary);
		Assert.Empty(u.Findings);
		Assert.Empty(u.Resolved);
	}

	// ---- SessionPlanner (the Mealy step) ----

	[Fact]
	public void First_turn_sends_the_full_diff_and_the_protocol()
	{
		var req = SessionPlanner.Advance(
			TestData.BugHunter, Repo, ReviewSession.Initial,
			Diff.Parse("diff --git a/x b/x\n+a\n"), "abc1234567");

		Assert.Equal(2, req.Messages.Count);
		Assert.Contains("Find correctness bugs", Msg.System(req)); // persona prompt
		Assert.Contains("reply with JSON", Msg.System(req));       // protocol
		Assert.Contains("First review", Msg.User(req));
		Assert.Contains("abc1234", Msg.User(req));              // short sha
	}

	[Fact]
	public void Continued_turn_carries_summary_and_open_findings_forward()
	{
		var prior = new ReviewSession("old1234", 1, "prev summary",
			[new Finding(Severity.Minor, "a.cs", 1, "open thing", "x")]);

		var req = SessionPlanner.Advance(
			TestData.BugHunter, Repo, prior, Diff.Parse("diff --git a/y b/y\n+z\n"), "new5678");

		var user = Msg.User(req);
		Assert.Contains("continuing", user);
		Assert.Contains("turn 2", user);
		Assert.Contains("prev summary", user);
		Assert.Contains("open thing", user);   // prior finding carried forward
	}

	// ---- #178: the continued turn is TOLD what its own branch introduced ----

	private static readonly Diff RenameDelta = Diff.Parse(
		"diff --git a/src/Trajectory.cs b/src/Trajectory.cs\n"
		+ "--- a/src/Trajectory.cs\n+++ b/src/Trajectory.cs\n@@ -1,2 +1,2 @@\n"
		+ "-\tpublic static Trajectory? Of(IReadOnlyList<Turn> turns)\n"
		+ "+\tpublic static Trajectory? OfTurns(IReadOnlyList<Turn> turns)\n");

	private static readonly Diff RenameBaseline = Diff.Parse(
		"diff --git a/src/Trajectory.cs b/src/Trajectory.cs\n"
		+ "--- a/src/Trajectory.cs\n+++ b/src/Trajectory.cs\n@@ -1,1 +1,2 @@\n"
		+ "+\tpublic static Trajectory? OfTurns(IReadOnlyList<Turn> turns)\n");

	private static string ContinuedUser(OwnRemovals? own, Diff? delta = null) => Msg.User(
		SessionPlanner.Advance(
			TestData.BugHunter, Repo, new ReviewSession("old1234", 1, "prev summary", []),
			delta ?? RenameDelta, "new5678", own: own));

	// The block alone. The raw delta is in the same message and quotes every one of these lines, so
	// asserting on the whole prompt would pass whatever the block did or did not say.
	private static string OwnBlock(OwnRemovals? own, Diff? delta = null)
	{
		var user = ContinuedUser(own, delta);
		var at = user.IndexOf("These lines, removed", System.StringComparison.Ordinal);
		return at < 0 ? string.Empty : user[at..];
	}

	[Fact]
	public void A_continued_turn_is_told_which_removals_its_own_branch_introduced()
	{
		var block = OwnBlock(OwnRemovals.Of(RenameDelta, RenameBaseline));

		// Stated as a fact, with the line named. NOT as a question to consider: the finding-scope
		// A/B measured the question form at 0 useful answers in 48 trials.
		Assert.Contains("added by an EARLIER TURN of this same pull request", block);
		Assert.Contains("not a breaking change", block);
		Assert.Contains("src/Trajectory.cs", block);
		Assert.Contains("public static Trajectory? Of(IReadOnlyList<Turn> turns)", block);
		// And the renamed-TO form is not in it: it is on the branch, not removed from it.
		Assert.DoesNotContain("OfTurns", block);
		Assert.DoesNotContain("consider whether", block);
		Assert.DoesNotContain("may have been", block);
	}

	[Fact]
	public void Nothing_is_said_when_nothing_could_be_established()
	{
		// Unknown (no baseline) and None (a baseline that attributed nothing) both render as
		// silence, which is the pre-#178 prompt exactly. A hedge here would be the question form
		// creeping back in through the renderer.
		foreach (var own in new[] { null, OwnRemovals.Unknown, OwnRemovals.None })
		{
			Assert.DoesNotContain("EARLIER TURN", ContinuedUser(own));
		}
	}

	[Fact]
	public void The_first_turn_never_carries_the_block()
	{
		// Turn 1 is shown the whole pull request, so it has nothing to be confused about - and the
		// block would be describing the very diff it is reading.
		var user = Msg.User(SessionPlanner.Advance(
			TestData.BugHunter, Repo, ReviewSession.Initial, RenameDelta, "new5678",
			own: OwnRemovals.Of(RenameDelta, RenameBaseline)));

		Assert.Contains("First review", user);
		Assert.DoesNotContain("EARLIER TURN", user);
	}

	[Fact]
	public void The_block_is_capped_and_says_how_much_it_dropped()
	{
		var lines = new System.Text.StringBuilder();
		for (var i = 0; i < 50; i++)
		{
			lines.Append("-\tpublic static int Probe").Append(i).Append("() => ").Append(i).Append(";\n");
		}

		var delta = Diff.Parse(
			"diff --git a/src/Probe.cs b/src/Probe.cs\n"
			+ "--- a/src/Probe.cs\n+++ b/src/Probe.cs\n@@ -1,50 +1,1 @@\n" + lines);
		var own = OwnRemovals.Of(delta, RenameBaseline);
		Assert.Equal(50, own.LineCount);

		var block = OwnBlock(own, delta);

		Assert.Contains("Probe0()", block);
		Assert.Contains("Probe39()", block);
		Assert.DoesNotContain("Probe40()", block);
		Assert.Contains("(and 10 further line(s) this pull request introduced)", block);
	}

	[Fact]
	public void A_very_long_line_is_clipped_rather_than_swamping_the_block()
	{
		var long_ = "var payload = \"" + new string('x', 400) + "\";";
		var delta = Diff.Parse(
			"diff --git a/src/Probe.cs b/src/Probe.cs\n"
			+ "--- a/src/Probe.cs\n+++ b/src/Probe.cs\n@@ -1,1 +1,1 @@\n-\t" + long_ + "\n");

		var block = OwnBlock(OwnRemovals.Of(delta, RenameBaseline), delta);

		Assert.DoesNotContain(long_, block);
		Assert.Contains("var payload = \"xxx", block);
		Assert.Contains(" …", block);
	}

	[Fact]
	public void Every_turn_of_a_pr_review_is_asked_whether_a_finding_is_worth_its_fix()
	{
		// #166: the clause shipped in #156 reached only PromptAssembly, the LOCAL one-shot
		// composer. Every GitHub PR review is built here instead, so the surface the clause was
		// written for was the one surface that never saw it. Asserted on turn 1 and a continued
		// turn, because a session's later turns are where a reviewer escalates a guard it has
		// already asked to be extended once.
		foreach (var prior in new[]
			{ ReviewSession.Initial, new ReviewSession("old1234", 1, "prev summary", []) })
		{
			var system = Msg.System(SessionPlanner.Advance(
				TestData.BugHunter, Repo, prior, Diff.Parse("diff --git a/y b/y\n+z\n"), "new5678"));

			Assert.Contains("worth its fix", system);
			Assert.Contains("not commissioning machinery", system);
			Assert.Contains("Severity is the consequence if you are right", system);
			Assert.Contains("simpler mechanism than the one under review", system);
			// The persona's own voice still LEADS the message; the doctrine follows it.
			Assert.StartsWith("Find correctness bugs only.", system);
		}
	}

	[Fact]
	public void Agent_tier_advertises_the_read_only_tools()
	{
		var req = SessionPlanner.Advance(TestData.Contrarian, Repo, ReviewSession.Initial, Diff.Empty, "sha1234");
		Assert.Contains("read-only tools", Msg.System(req));
	}

	// ---- prompt-prefix caching (see the SessionPlanner type doc) ----

	// These two are about ORDER and SHARING, so they index deliberately where the rest of the
	// suite asks by role. Break either and the review path silently returns to 0% cache hits.
	[Fact]
	public void The_persona_independent_block_comes_first_so_a_runs_personas_share_a_prefix()
	{
		var req = SessionPlanner.Advance(TestData.BugHunter, Repo, ReviewSession.Initial, Diff.Empty, "sha1234");

		Assert.Equal(ChatRole.User, req.Messages[0].Role);
		Assert.Equal(ChatRole.System, req.Messages[^1].Role);
	}

	[Fact]
	public void Two_personas_on_one_first_turn_produce_a_byte_identical_user_block()
	{
		var diff = Diff.Parse("--- a/Foo.cs\n+++ b/Foo.cs\n@@ -1 +1 @@\n-old\n+new\n");
		var intent = new PullRequestIntent("Bump the retry budget", "because it flakes");
		ReviewRequest For(Persona p) =>
			SessionPlanner.Advance(p, Repo, ReviewSession.Initial, diff, "sha1234", intent: intent);

		// Different lens, tier, model and temperature — none of it may reach the shared block.
		Assert.Equal(Msg.User(For(TestData.BugHunter)), Msg.User(For(TestData.Contrarian)));
		Assert.NotEqual(Msg.System(For(TestData.BugHunter)), Msg.System(For(TestData.Contrarian)));
	}

	// The verify pass appends to the original conversation, so its prefix is the review call's
	// whole message list. That is why the verify path already cached at 95% before this change,
	// and it must keep doing so after it.
	[Fact]
	public void Verify_extends_the_review_conversation_rather_than_rebuilding_it()
	{
		var original = SessionPlanner.Advance(
			TestData.BugHunter, Repo, ReviewSession.Initial, Diff.Empty, "sha1234");
		var verify = SessionPlanner.Verify(original, [new Finding(Severity.Major, "a.cs", 1, "t", "b")]);

		Assert.Equal(original.Messages, verify.Messages.Take(original.Messages.Count));
		Assert.Equal(ChatRole.User, verify.Messages[^1].Role);
	}

	// ---- SessionCommentRenderer (the living comment) ----

	[Fact]
	public void Living_comment_shows_progress_and_resolved_from_turn_two()
	{
		var prior = new ReviewSession("old", 1, "s", []);
		var update = new SessionUpdate("s2",
			[new Finding(Severity.Major, "a.cs", 2, "boom", "null")], ["fixed thing"], []);

		var md = SessionCommentRenderer.Render(TestData.Architect, prior, update, "deadbeefcafe");

		Assert.StartsWith(CommentRenderer.Marker("architect"), md);
		Assert.Contains("Reviewed through `deadbee`", md);
		Assert.Contains("turn 2", md);
		Assert.Contains("boom", md);
		Assert.Contains("Resolved since last push:", md);
		Assert.Contains("fixed thing", md);
	}

	[Fact]
	public void First_turn_comment_has_no_resolved_section()
	{
		var md = SessionCommentRenderer.Render(
			TestData.BugHunter, ReviewSession.Initial, new SessionUpdate(string.Empty, [], [], []), "abc1234");

		Assert.DoesNotContain("Resolved since last push", md);
		Assert.Contains("turn 1", md);
		Assert.Contains("_No findings._", md);
	}
}
