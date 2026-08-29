using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// The panel's datastore is the PR comment thread, and a PR comment is something a stranger can
/// write. These pin down who gets believed.
/// </summary>
public class CommentTrustTests
{
	[Theory]
	[InlineData("OWNER")]
	[InlineData("MEMBER")]
	[InlineData("COLLABORATOR")]
	public void An_author_who_speaks_for_the_repo_is_trusted(string association) =>
		Assert.True(CommentTrust.IsTrustedAuthor(isBot: false, association));

	[Theory]
	[InlineData("CONTRIBUTOR")]
	[InlineData("FIRST_TIME_CONTRIBUTOR")]
	[InlineData("FIRST_TIMER")]
	[InlineData("MANNEQUIN")]
	[InlineData("NONE")]
	[InlineData("")]
	[InlineData(null)]
	public void Everyone_else_is_not(string? association) =>
		Assert.False(CommentTrust.IsTrustedAuthor(isBot: false, association));

	[Fact]
	public void Case_and_whitespace_do_not_buy_trust()
	{
		// GitHub sends these upper-case and exact. Anything else is not the value we recognise,
		// and a guard that normalises attacker-adjacent input is a guard with a second parser.
		Assert.False(CommentTrust.IsTrustedAuthor(isBot: false, "owner"));
		Assert.False(CommentTrust.IsTrustedAuthor(isBot: false, " OWNER "));
	}

	[Fact]
	public void A_bot_is_trusted_because_the_panel_posts_as_one() =>
		Assert.True(CommentTrust.IsTrustedAuthor(isBot: true, "NONE"));

	[Fact]
	public void A_bot_may_carry_state_but_may_not_direct_the_panel()
	{
		var bot = new ExistingComment(1, "body", "github-actions[bot]", IsBot: true, AuthorIsTrusted: true);

		Assert.True(CommentTrust.CarriesState(bot));
		Assert.False(CommentTrust.MayDirectPanel(bot)); // else the panel answers itself
	}

	[Fact]
	public void A_stranger_may_do_neither()
	{
		var stranger = new ExistingComment(2, "body", "drive-by", IsBot: false, AuthorIsTrusted: false);

		Assert.False(CommentTrust.CarriesState(stranger));
		Assert.False(CommentTrust.MayDirectPanel(stranger));
	}

	/// <summary>
	/// A marker is just text. Without an authorship check the panel would PATCH a stranger's
	/// comment (the token can), writing the review inside it and never refreshing its own.
	/// </summary>
	[Fact]
	public void An_untrusted_comment_carrying_our_marker_is_not_an_update_target()
	{
		var body = CommentRenderer.Marker("architect") + "\n\nreview text";
		var forged = new ExistingComment(7, body, "drive-by", IsBot: false, AuthorIsTrusted: false);

		var plan = Assert.Single(CommentSync.Plan([forged], [body]));

		Assert.Equal(UpsertAction.Create, plan.Action);
		Assert.Null(plan.CommentId);
	}

	[Fact]
	public void A_trusted_comment_carrying_our_marker_still_is()
	{
		var body = CommentRenderer.Marker("architect") + "\n\nreview text";
		var ours = new ExistingComment(7, body, "github-actions[bot]", IsBot: true, AuthorIsTrusted: true);

		var plan = Assert.Single(CommentSync.Plan([ours], [body + "\nnew"]));

		Assert.Equal(UpsertAction.Update, plan.Action);
		Assert.Equal(7, plan.CommentId);
	}
}

/// <summary>
/// A SHA read back out of a PR comment is only a SHA by convention; it is whatever the comment
/// said, and it goes on to name a revision in an API path.
/// </summary>
public class ShaShapeTests
{
	[Theory]
	[InlineData("abc1234")]                                    // shortest form anyone uses
	[InlineData("0a8b0a0")]
	[InlineData("2bdc320d4f1e9a7c5b3e8f6a2d4c1b9e7f3a5c8d")]   // full 40
	public void A_commit_id_is_seven_to_forty_hex_characters(string sha) =>
		Assert.True(Sha.IsCommitId(sha));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("abc123")]                                     // too short to be an abbreviation
	[InlineData("2bdc320d4f1e9a7c5b3e8f6a2d4c1b9e7f3a5c8d0")]  // too long
	[InlineData("main")]
	[InlineData("local")]
	[InlineData("../../../../orgs/acme/members")]              // the shape this exists to refuse
	[InlineData("HEAD?per_page=1")]
	[InlineData("abc1234#frag")]
	[InlineData("abc1234/../x")]
	[InlineData("abc 1234")]
	public void Anything_else_is_not(string? sha) =>
		Assert.False(Sha.IsCommitId(sha));
}
