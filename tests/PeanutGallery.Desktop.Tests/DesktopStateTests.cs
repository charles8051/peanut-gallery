using System.IO;
using PeanutGallery.Desktop.Model;
using PeanutGallery.Desktop.Services;
using Xunit;

namespace PeanutGallery.Desktop.Tests;

public class DesktopStateTests
{
    [Fact]
    public void AddRepo_appends_and_selects_the_first()
    {
        var s = DesktopState.Empty.AddRepo("acme/api");
        Assert.Equal(new[] { "acme/api" }, s.Repos);
        Assert.Equal("acme/api", s.Selected);
    }

    [Fact]
    public void AddRepo_is_case_insensitive_dedup_and_keeps_selection()
    {
        var s = DesktopState.Empty.AddRepo("acme/api").AddRepo("ACME/API").AddRepo("acme/web");
        Assert.Equal(new[] { "acme/api", "acme/web" }, s.Repos);
        Assert.Equal("acme/api", s.Selected); // unchanged by the second add
    }

    [Theory]
    [InlineData("noslash")]
    [InlineData("too/many/slashes")]
    [InlineData("  ")]
    public void AddRepo_rejects_invalid_slugs(string slug) =>
        Assert.Empty(DesktopState.Empty.AddRepo(slug).Repos);

    [Fact]
    public void RemoveRepo_repoints_selection_when_the_selected_repo_goes()
    {
        var s = DesktopState.Empty.AddRepo("acme/api").AddRepo("acme/web").Select("acme/web");
        var after = s.RemoveRepo("acme/web");
        Assert.Equal(new[] { "acme/api" }, after.Repos);
        Assert.Equal("acme/api", after.Selected);
    }

    [Fact]
    public void RemoveRepo_clears_selection_when_the_last_repo_goes()
    {
        var after = DesktopState.Empty.AddRepo("acme/api").RemoveRepo("acme/api");
        Assert.Empty(after.Repos);
        Assert.Null(after.Selected);
    }

    [Fact]
    public void Select_ignores_an_untracked_repo()
    {
        var s = DesktopState.Empty.AddRepo("acme/api");
        Assert.Same(s.Repos, s.Select("acme/nope").Repos);
        Assert.Equal("acme/api", s.Select("acme/nope").Selected);
    }

    [Fact]
    public void Select_uses_the_stored_casing_not_the_callers()
    {
        var s = DesktopState.Empty.AddRepo("acme/api").AddRepo("acme/web").Select("ACME/WEB");
        Assert.Equal("acme/web", s.Selected); // matches the entry in Repos exactly
    }

    [Fact]
    public void Seed_normalizes_dedups_and_selects_first()
    {
        var s = DesktopState.Seed(new[] { "acme/api", "ACME/api", "bad", "acme/web" });
        Assert.Equal(new[] { "acme/api", "acme/web" }, s.Repos);
        Assert.Equal("acme/api", s.Selected);
    }

    [Fact]
    public void Store_round_trips_through_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pg-state-{System.Guid.NewGuid():N}.json");
        try
        {
            var store = new DesktopStateStore(path);
            Assert.False(store.Exists);
            var state = DesktopState.Empty.AddRepo("acme/api").AddRepo("acme/web").Select("acme/web");
            store.Save(state);
            Assert.True(store.Exists);

            var loaded = new DesktopStateStore(path).Load();
            Assert.Equal(state.Repos, loaded.Repos);
            Assert.Equal(state.Selected, loaded.Selected);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Store_load_of_missing_file_is_empty()
    {
        var store = new DesktopStateStore(Path.Combine(Path.GetTempPath(), $"pg-missing-{System.Guid.NewGuid():N}.json"));
        Assert.Equal(DesktopState.Empty, store.Load());
    }

    [Fact]
    public void SetAutoReview_toggles_for_a_tracked_repo_by_stored_casing()
    {
        var s = DesktopState.Empty.AddRepo("acme/api").SetAutoReview("ACME/API", true);
        Assert.True(s.IsAutoReview("acme/api"));
        Assert.Equal(new[] { "acme/api" }, s.AutoReview);

        var off = s.SetAutoReview("acme/api", false);
        Assert.False(off.IsAutoReview("acme/api"));
        Assert.Empty(off.AutoReview);
    }

    [Fact]
    public void SetAutoReview_ignores_an_untracked_repo() =>
        Assert.Empty(DesktopState.Empty.SetAutoReview("acme/api", true).AutoReview);

    [Fact]
    public void RemoveRepo_also_drops_the_subscription()
    {
        var s = DesktopState.Empty.AddRepo("acme/api").SetAutoReview("acme/api", true).RemoveRepo("acme/api");
        Assert.Empty(s.AutoReview);
    }

    [Fact]
    public void Store_round_trips_auto_review_subscriptions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pg-state-{System.Guid.NewGuid():N}.json");
        try
        {
            var state = DesktopState.Empty.AddRepo("acme/api").AddRepo("acme/web").SetAutoReview("acme/web", true);
            new DesktopStateStore(path).Save(state);
            var loaded = new DesktopStateStore(path).Load();
            Assert.Equal(new[] { "acme/web" }, loaded.AutoReview);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
