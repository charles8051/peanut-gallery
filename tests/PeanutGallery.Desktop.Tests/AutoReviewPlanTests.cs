using System;
using System.Collections.Generic;
using System.Linq;
using PeanutGallery.Desktop.Model;
using Xunit;

namespace PeanutGallery.Desktop.Tests;

public class AutoReviewPlanTests
{
    private static PrRaw Pr(int n, string head) =>
        new(n, $"PR {n}", "dev", "feat/x", DateTimeOffset.UnixEpoch, head, false);

    [Fact]
    public void All_prs_are_pending_when_nothing_has_been_seen()
    {
        var pending = AutoReviewPlan.Pending(new Dictionary<string, string>(), "acme", "api",
            new[] { Pr(1, "aaa"), Pr(2, "bbb") });
        Assert.Equal(new[] { 1, 2 }, pending.Select(p => p.Number));
    }

    [Fact]
    public void Unchanged_heads_are_skipped_changed_and_new_are_pending()
    {
        var seen = new Dictionary<string, string>
        {
            [AutoReviewPlan.Key("acme", "api", 1)] = "aaa", // unchanged
            [AutoReviewPlan.Key("acme", "api", 2)] = "old", // head moved
        };
        var pending = AutoReviewPlan.Pending(seen, "acme", "api",
            new[] { Pr(1, "aaa"), Pr(2, "bbb"), Pr(3, "ccc") });

        Assert.Equal(new[] { 2, 3 }, pending.Select(p => p.Number)); // #1 skipped, #2 changed, #3 new
    }

    [Fact]
    public void Key_is_scoped_per_repo()
    {
        Assert.NotEqual(AutoReviewPlan.Key("acme", "api", 1), AutoReviewPlan.Key("acme", "web", 1));
    }
}
