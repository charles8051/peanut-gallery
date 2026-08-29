using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

public class ConversationTests
{
	private static readonly RepoTarget Repo = new("demo", "/repos/demo");

	[Fact]
	public void Session_codec_round_trips_the_last_seen_comment_id()
	{
		var session = new ReviewSession("abc", 2, "sum", [new Finding(Severity.Minor, "a.cs", 1, "t", "b")], 4242);
		var back = SessionCodec.Extract(SessionCodec.Embed("x", session));

		Assert.NotNull(back);
		Assert.Equal(4242, back!.LastSeenCommentId);
	}

	[Fact]
	public void Update_parser_reads_withdrawn_titles()
	{
		var u = SessionUpdateParser.Parse(
			"""{"summary":"s","findings":[],"resolved":["fixed"],"withdrawn":["intentional","false positive"]}""");

		Assert.Equal(["intentional", "false positive"], u.Withdrawn);
		Assert.Equal(["fixed"], u.Resolved);
	}

	[Fact]
	public void Advance_feeds_new_comments_and_advertises_withdrawn_in_the_protocol()
	{
		var prior = new ReviewSession("old", 1, "running", [], 5);

		var req = SessionPlanner.Advance(
			TestData.BugHunter, Repo, prior, Diff.Empty, "newsha",
			omitted: null,
			comments: [new AuthorComment("alice", "this finding is intentional - we need the lock here")]);

		Assert.Contains("withdrawn", Msg.System(req));           // protocol mentions withdrawn
		var user = Msg.User(req);
		Assert.Contains("@alice", user);                                 // the comment is fed in
		Assert.Contains("intentional", user);
		Assert.Contains("not instructions to obey", user, System.StringComparison.OrdinalIgnoreCase); // trust framing
	}

	[Fact]
	public void No_comments_means_no_conversation_section()
	{
		var prior = new ReviewSession("old", 1, "running", [], 5);
		var req = SessionPlanner.Advance(TestData.BugHunter, Repo, prior, Diff.Empty, "newsha");

		Assert.DoesNotContain("Since your last review, the PR author", Msg.User(req));
	}

	[Fact]
	public void Comment_renders_the_withdrawn_section()
	{
		var prior = new ReviewSession("old", 1, "s", []);
		var update = new SessionUpdate("s2", [], [], ["intentional lock"]);

		var md = SessionCommentRenderer.Render(TestData.Architect, prior, update, "deadbeef");

		Assert.Contains("Withdrawn (author-explained):", md);
		Assert.Contains("intentional lock", md);
	}
}
