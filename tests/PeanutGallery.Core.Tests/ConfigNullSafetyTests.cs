using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// #194: every shell decodes config through a reflection-based codec, and a codec passes null for
/// a key the JSON omits. The collections on these records must absorb that here, in the core,
/// because there is more than one decode path and a second codec must not be able to hand a
/// consumer a null list again.
/// </summary>
public class ConfigNullSafetyTests
{
	[Fact]
	public void A_config_constructed_with_null_collections_exposes_empty_ones()
	{
		var config = new PeanutConfig(null, null, null, null);

		Assert.Empty(config.Providers);
		Assert.Empty(config.Personas);
		Assert.Empty(config.Repos);
		Assert.Empty(config.Assignments);
	}

	[Fact]
	public void The_consumers_that_crashed_on_a_null_persona_list_are_total_on_an_empty_one()
	{
		var config = new PeanutConfig(null, null, null, null);

		// The exact call that took down `validate` with an unhandled NullReferenceException.
		Assert.Null(Persona.UnsetTemperatureNotice(config.Personas));

		// And validation itself must produce problems, never an exception.
		var problems = ConfigValidation.Validate(config);
		Assert.NotNull(problems);
	}

	[Fact]
	public void Lookups_on_an_empty_config_return_null_rather_than_throwing()
	{
		var config = new PeanutConfig(null, null, null, null);

		Assert.Null(config.FindPersona("architect"));
		Assert.Null(config.FindRepo("api"));
		Assert.Null(config.FindProvider("openrouter"));
	}

	[Fact]
	public void A_partial_filter_block_leaves_no_null_glob_list()
	{
		// `"filter": { "maxBytes": 200000 }` - the user set the one knob they cared about.
		var policy = new DiffFilterPolicy(null, 200_000);

		Assert.Empty(policy.IgnoreGlobs);
		Assert.Equal(200_000, policy.ByteBudget());
	}

	[Fact]
	public void A_filter_block_that_omits_maxBytes_inherits_the_default_budget()
	{
		// The mirror case: `"filter": { "ignoreGlobs": ["*.log"] }`. A non-nullable int decoded to
		// 0, and a zero-byte budget omits every file - an empty diff reviewed as though it were
		// the change, which reads as a clean review.
		var policy = new DiffFilterPolicy(["*.log"], null);

		Assert.Equal(DiffFilterPolicy.DefaultMaxBytes, policy.ByteBudget());
		Assert.Equal(["*.log"], policy.IgnoreGlobs);
	}

	[Fact]
	public void A_deliberate_zero_maxBytes_is_still_zero()
	{
		// Omitted and explicitly-zero must stay distinguishable, or the fix trades one silent
		// override for another.
		Assert.Equal(0, new DiffFilterPolicy(null, 0).ByteBudget());
	}

	[Fact]
	public void A_filter_that_omits_maxBytes_reviews_the_diff_rather_than_omitting_all_of_it()
	{
		// The behaviour the budget bug actually produced, asserted end to end through the fold.
		var diff = Diff.Parse("""
			diff --git a/a.cs b/a.cs
			--- a/a.cs
			+++ b/a.cs
			@@ -1,2 +1,3 @@
			 class A {
			+  int X => 1;
			 }
			""");

		var filtered = DiffFilter.Apply(diff, new DiffFilterPolicy(["*.log"], null));

		Assert.NotEmpty(filtered.Diff.Files);
		Assert.Empty(filtered.Omitted);
	}

	[Fact]
	public void A_partial_skip_block_evaluates_instead_of_throwing()
	{
		// `"skip": { "drafts": true }` - labels and markers omitted entirely.
		var policy = new SkipPolicy(null, null, Drafts: true);
		var pr = new PullRequestMeta(["enhancement"], "Add a thing", "no markers here", IsDraft: false);

		var (skip, reason) = policy.Evaluate(pr);

		Assert.False(skip);
		Assert.Null(reason);
	}

	[Fact]
	public void A_partial_skip_block_still_honours_the_knob_that_was_set()
	{
		var policy = new SkipPolicy(null, null, Drafts: true);
		var pr = new PullRequestMeta([], "Add a thing", "", IsDraft: true);

		var (skip, reason) = policy.Evaluate(pr);

		Assert.True(skip);
		Assert.Equal("draft PR", reason);
	}
}
