using System.Linq;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

public class BuiltInPersonasTests
{
    [Fact]
    public void Has_the_three_archetypes_with_unique_ids()
    {
        Assert.Equal(3, BuiltInPersonas.All.Count);
        Assert.Equal(3, BuiltInPersonas.All.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void No_built_in_persona_samples_at_greedy_zero()
    {
        // Temperature 0 is greedy decoding - a bad default for any model. The invariant is
        // non-greedy (> 0) rather than any particular value: the built-ins all run
        // KnownModels.Default now, but a persona is free to sample below the panel default (the
        // contrarian's 0.8), so pinning this to DefaultTemperature would forbid that.
        Assert.All(BuiltInPersonas.All, p =>
            Assert.True(p.Temperature > 0, $"{p.Id} at {p.Temperature} is greedy (0)"));
    }

    [Fact]
    public void Default_reviewers_are_the_diff_tier_built_ins()
    {
        Assert.All(BuiltInPersonas.DefaultReviewers, p => Assert.Equal(ReviewTier.Diff, p.Tier));
        Assert.DoesNotContain(BuiltInPersonas.DefaultReviewers, p => p.Id == "contrarian");
    }

    [Fact]
    public void Default_panel_is_built_from_the_default_reviewers_and_assigns_them()
    {
        var config = DefaultPanel.For("acme-api");
        Assert.Equal(
            BuiltInPersonas.DefaultReviewers.Select(p => p.Id).OrderBy(x => x),
            config.Personas.Select(p => p.Id).OrderBy(x => x));
        Assert.All(config.Assignments, a => Assert.Equal("acme-api", a.RepoName));
        Assert.Equal(config.Personas.Count, config.Assignments.Count);
    }
}
