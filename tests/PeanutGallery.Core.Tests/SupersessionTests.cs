using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

public class SupersessionTests
{
	[Fact]
	public void Same_head_is_not_superseded()
	{
		Assert.Null(Supersession.SupersededReason("abc1234def", "abc1234def"));
	}

	[Fact]
	public void Moved_head_is_superseded_with_short_shas_in_reason()
	{
		var reason = Supersession.SupersededReason("aaaaaaa1111", "bbbbbbb2222");
		Assert.NotNull(reason);
		Assert.Contains("aaaaaaa", reason!);
		Assert.Contains("bbbbbbb", reason!);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void Null_or_empty_trigger_sha_never_supersedes(string? trigger)
	{
		// e.g. an issue_comment event carries no head SHA -> proceed and review the live head.
		Assert.Null(Supersession.SupersededReason(trigger, "abc1234def"));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void Empty_live_head_never_supersedes(string? live)
	{
		Assert.Null(Supersession.SupersededReason("abc1234def", live));
	}
}
