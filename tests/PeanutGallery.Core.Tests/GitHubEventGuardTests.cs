using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

public class GitHubEventGuardTests
{
	private static string Comment(string type, string assoc) =>
		"{\"comment\":{\"user\":{\"type\":\"" + type + "\"},\"author_association\":\"" + assoc + "\"}}";

	[Theory]
	[InlineData("User", "OWNER", true)]
	[InlineData("User", "MEMBER", true)]
	[InlineData("User", "COLLABORATOR", true)]
	[InlineData("User", "CONTRIBUTOR", false)]   // not write-trusted
	[InlineData("User", "NONE", false)]          // random commenter
	[InlineData("Bot", "OWNER", false)]          // bot never trusted (loop guard)
	public void Trust_depends_on_non_bot_and_write_association(string type, string assoc, bool expected)
	{
		Assert.Equal(expected, GitHubEventGuard.IsTrustedCommentEvent(Comment(type, assoc)));
	}

	[Theory]
	[InlineData(null)]                          // GITHUB_EVENT_PATH unset or missing
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("not json")]                    // unreadable payload
	[InlineData("""{"action":"opened"}""")]     // comment event with no comment object
	[InlineData("""{"comment":null}""")]
	[InlineData("""{"comment":"a string"}""")]  // present but not an object
	public void Fails_CLOSED_when_the_author_cannot_be_established(string? json)
	{
		// The caller only asks once GITHUB_EVENT_NAME says a comment triggered the run, so
		// every state here is "a comment run whose author is unknown" - refuse it. This
		// inverted in #220; it used to return true and let an unreadable payload through.
		Assert.False(GitHubEventGuard.IsTrustedCommentEvent(json));
	}

	[Theory]
	[InlineData("""{"comment":{"author_association":"OWNER"}}""")]        // no user at all
	[InlineData("""{"comment":{"user":null,"author_association":"OWNER"}}""")]
	[InlineData("""{"comment":{"user":[],"author_association":"OWNER"}}""")]
	[InlineData("""{"comment":{"user":{},"author_association":"OWNER"}}""")]   // no type
	[InlineData("""{"comment":{"user":{"type":7},"author_association":"OWNER"}}""")]
	[InlineData("""{"comment":{"user":{"type":"App"},"author_association":"OWNER"}}""")]
	[InlineData("""{"comment":{"user":{"type":"Alien"},"author_association":"OWNER"}}""")]
	[InlineData("""{"comment":{"user":{"type":"user"},"author_association":"OWNER"}}""")]  // ordinal
	public void An_author_whose_identity_is_absent_is_refused_even_with_a_trusted_association(string json)
	{
		Assert.False(GitHubEventGuard.IsTrustedCommentEvent(json));
	}

	[Theory]
	[InlineData("[]")]                          // valid JSON, non-object root
	[InlineData("null")]
	[InlineData("\"payload\"")]
	[InlineData("42")]
	public void A_valid_but_non_object_root_is_refused_not_thrown(string json)
	{
		Assert.False(GitHubEventGuard.IsTrustedCommentEvent(json));
	}

	[Fact]
	public void The_same_shape_decides_both_comment_triggers()
	{
		// pull_request_review_comment carries the same comment.user.type and
		// comment.author_association as issue_comment, so one shape-based guard covers both.
		// The caller keys on the event-name set; this function never sees the name.
		Assert.True(GitHubEventGuard.IsCommentEvent("issue_comment"));
		Assert.True(GitHubEventGuard.IsCommentEvent("pull_request_review_comment"));
		Assert.False(GitHubEventGuard.IsCommentEvent("pull_request"));
		Assert.False(GitHubEventGuard.IsCommentEvent("push"));
		Assert.False(GitHubEventGuard.IsCommentEvent(null));
		Assert.False(GitHubEventGuard.IsCommentEvent("Issue_Comment"));   // ordinal, not case-folded

		Assert.True(GitHubEventGuard.IsTrustedCommentEvent(Comment("User", "OWNER")));
		Assert.False(GitHubEventGuard.IsTrustedCommentEvent(Comment("User", "NONE")));
	}

	[Fact]
	public void Missing_association_on_a_present_comment_is_refused()
	{
		Assert.False(GitHubEventGuard.IsTrustedCommentEvent("""{"comment":{"user":{"type":"User"}}}"""));
	}

	[Fact]
	public void TriggerHeadSha_reads_pull_request_head_sha()
	{
		Assert.Equal("deadbeef", GitHubEventGuard.TriggerHeadSha("""{"pull_request":{"head":{"sha":"deadbeef"}}}"""));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("not json")]
	[InlineData("""{"action":"created","comment":{"id":1}}""")]  // issue_comment: no pull_request.head
	[InlineData("""{"pull_request":{"head":{}}}""")]              // no sha under head
	public void TriggerHeadSha_is_null_without_a_head_sha(string? json)
	{
		Assert.Null(GitHubEventGuard.TriggerHeadSha(json));
	}
}
