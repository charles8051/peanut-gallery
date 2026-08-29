using System;
using System.Collections.Generic;
using PeanutGallery.Core;
using PeanutGallery.Desktop.Model;
using PeanutGallery.Desktop.Views;
using Xunit;

namespace PeanutGallery.Desktop.Tests;

public class SnapshotBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    // A persona living comment: marker line + hidden session blob, exactly as the core writes it.
    private static ExistingComment PersonaComment(string personaId, params Finding[] open)
    {
        var session = new ReviewSession("abc1234", Turn: 1, Summary: "s", open, LastSeenCommentId: 0);
        var visible = CommentRenderer.Marker(personaId) + "\n### Persona\n_body_\n";
        return new ExistingComment(1, SessionCodec.Embed(visible, session), personaId);
    }

    private static Finding Maj() => new(Severity.Major, "a.cs", 3, "boom", "b");
    private static Finding Min() => new(Severity.Minor, "a.cs", 4, "nit", "b");

    private static RepoInput Repo(string owner, string name, params PrInput[] prs) =>
        new(owner, name, prs);

    private static PrInput Pr(int number, DateTimeOffset updated, params ExistingComment[] comments) =>
        new(new PrRaw(number, $"PR {number}", "dev", "feat/x", updated, "abc1234", false), comments);

    [Fact]
    public void No_persona_comments_is_not_reviewed()
    {
        var repo = Repo("acme", "api", Pr(10, Now, new ExistingComment(9, "just a human comment", "dev")));
        var snap = SnapshotBuilder.Build(new[] { repo }, "acme/api");

        var card = Assert.Single(snap.Selected.Prs);
        Assert.Equal(ReviewState.NotReviewed, card.State);
        Assert.Empty(snap.Selected.SubscribedPersonaIds);
        Assert.Null(snap.Selected.LastReviewed);
    }

    [Fact]
    public void Persona_comment_with_no_open_findings_is_clean()
    {
        var repo = Repo("acme", "api", Pr(10, Now, PersonaComment("bug-hunter")));
        var snap = SnapshotBuilder.Build(new[] { repo }, "acme/api");

        var card = Assert.Single(snap.Selected.Prs);
        Assert.Equal(ReviewState.Clean, card.State);
        Assert.Equal(0, card.High);
        Assert.Equal(0, card.Minor);
        Assert.Equal(Now, snap.Selected.LastReviewed);
    }

    [Fact]
    public void Findings_are_bucketed_high_vs_minor_across_personas()
    {
        var repo = Repo("acme", "api", Pr(10, Now,
            PersonaComment("bug-hunter", Maj(), Min()),
            PersonaComment("architect", Min())));
        var snap = SnapshotBuilder.Build(new[] { repo }, "acme/api");

        var card = Assert.Single(snap.Selected.Prs);
        Assert.Equal(ReviewState.Findings, card.State);
        Assert.Equal(1, card.High);   // one Major
        Assert.Equal(2, card.Minor);  // two Minor
    }

    [Fact]
    public void Subscribed_persona_ids_are_the_distinct_reviewers()
    {
        var repo = Repo("acme", "api",
            Pr(10, Now, PersonaComment("bug-hunter", Maj())),
            Pr(9, Now, PersonaComment("bug-hunter"), PersonaComment("architect")));
        var snap = SnapshotBuilder.Build(new[] { repo }, "acme/api");

        Assert.Equal(new[] { "architect", "bug-hunter" }, snap.Selected.SubscribedPersonaIds);
        Assert.Equal(2, snap.Selected.OpenPrs);
    }

    [Fact]
    public void Cards_are_sorted_by_number_descending()
    {
        var repo = Repo("acme", "api", Pr(7, Now), Pr(21, Now), Pr(13, Now));
        var snap = SnapshotBuilder.Build(new[] { repo }, "acme/api");

        Assert.Collection(snap.Selected.Prs,
            c => Assert.Equal(21, c.Number),
            c => Assert.Equal(13, c.Number),
            c => Assert.Equal(7, c.Number));
    }

    [Fact]
    public void Builder_keeps_raw_instants_not_formatted_strings()
    {
        var updated = Now.AddHours(-3);
        var repo = Repo("acme", "api", Pr(10, updated));
        var snap = SnapshotBuilder.Build(new[] { repo }, "acme/api");
        Assert.Equal(updated, Assert.Single(snap.Selected.Prs).Updated);
    }

    [Fact]
    public void AutoReview_flag_is_set_from_the_subscribed_set()
    {
        var repos = new List<RepoInput> { Repo("acme", "api"), Repo("acme", "web") };
        var snap = SnapshotBuilder.Build(repos, "acme/api", new[] { "acme/web" });

        Assert.False(snap.Repos[0].AutoReview);  // acme/api not subscribed
        Assert.True(snap.Repos[1].AutoReview);   // acme/web subscribed
        Assert.False(snap.Selected.AutoReview);  // selected is acme/api
    }

    [Fact]
    public void Empty_repo_list_yields_empty_detail()
    {
        var snap = SnapshotBuilder.Build(Array.Empty<RepoInput>(), null);
        Assert.Empty(snap.Repos);
        Assert.Empty(snap.Selected.Prs);
    }

    [Fact]
    public void Selection_defaults_to_first_when_slug_unmatched()
    {
        var repos = new List<RepoInput> { Repo("acme", "api"), Repo("acme", "web") };
        var snap = SnapshotBuilder.Build(repos, "acme/nope");
        Assert.Equal("api", snap.Selected.Name);
        Assert.True(snap.Repos[0].Selected);
    }
}

public class PresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(5, "5m ago")]
    [InlineData(180, "3h ago")]
    [InlineData(60 * 30, "yesterday")]
    [InlineData(60 * 24 * 4, "4d ago")]
    [InlineData(60 * 24 * 21, "3w ago")]
    public void Relative_time_buckets(int minutesAgo, string expected) =>
        Assert.Equal(expected, RelativeTime.Format(Now.AddMinutes(-minutesAgo), Now));

    [Fact]
    public void Relative_time_clamps_future_to_just_now() =>
        Assert.Equal("just now", RelativeTime.Format(Now.AddHours(2), Now));

    [Fact]
    public void Persona_style_is_deterministic_and_prettifies_the_id()
    {
        var a = PersonaStyle.Chip("bug-hunter");
        var b = PersonaStyle.Chip("bug-hunter");
        Assert.Equal("The Bug Hunter", a.Name);
        Assert.Equal(a.AccentHex, b.AccentHex);
    }

    [Fact]
    public void Persona_style_keeps_an_existing_the_prefix() =>
        Assert.Equal("The Architect", PersonaStyle.DisplayName("the-architect"));
}
