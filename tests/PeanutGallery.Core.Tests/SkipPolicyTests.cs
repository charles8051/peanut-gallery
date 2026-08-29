using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

public class SkipPolicyTests
{
	private static PullRequestMeta Pr(string[]? labels = null, string title = "", string body = "", bool draft = false)
		=> new(labels ?? [], title, body, draft);

	[Fact]
	public void Default_skips_on_the_skip_label_case_insensitively()
	{
		var (skip, reason) = SkipPolicy.Default.Evaluate(Pr(labels: ["Peanut-Gallery: Skip"]));
		Assert.True(skip);
		Assert.Contains("label", reason!);
	}

	[Fact]
	public void Default_skips_on_a_title_or_body_marker()
	{
		Assert.True(SkipPolicy.Default.Evaluate(Pr(title: "WIP [skip-review] do not review")).Skip);
		Assert.True(SkipPolicy.Default.Evaluate(Pr(body: "please [no-peanut-gallery] this one")).Skip);
	}

	[Fact]
	public void Default_does_not_skip_drafts()
	{
		Assert.False(SkipPolicy.Default.Evaluate(Pr(draft: true)).Skip);
	}

	[Fact]
	public void Drafts_true_skips_a_draft()
	{
		var (skip, reason) = (SkipPolicy.Default with { Drafts = true }).Evaluate(Pr(draft: true));
		Assert.True(skip);
		Assert.Equal("draft PR", reason);
	}

	[Fact]
	public void No_signals_means_no_skip()
	{
		Assert.False(SkipPolicy.Default.Evaluate(Pr(labels: ["bug"], title: "fix the thing")).Skip);
	}

	[Fact]
	public void Custom_labels_and_markers_are_honored()
	{
		var policy = new SkipPolicy(["wip"], ["@@noreview@@"], false);
		Assert.True(policy.Evaluate(Pr(labels: ["WIP"])).Skip);
		Assert.True(policy.Evaluate(Pr(body: "x @@noreview@@ y")).Skip);
		Assert.False(policy.Evaluate(Pr(labels: ["no-review"])).Skip); // not in the custom set
	}
}
