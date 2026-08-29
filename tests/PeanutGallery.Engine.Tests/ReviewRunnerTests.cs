using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

public class ReviewRunnerTests
{
    private const string Repo = "acme-api";

    private static Persona Persona(string id) => new(
        id, id, "bugs", ReviewTier.Diff, new ModelRef("openrouter", "some/model"), 0.2, "review it");

    private static PeanutConfig Config(params string[] personaIds) => new(
        Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
        Personas: personaIds.Select(Persona).ToList(),
        Repos: [new RepoTarget(Repo, ".")],
        Assignments: personaIds.Select(id => new Assignment(id, Repo)).ToList());

    private static readonly Diff SampleDiff = Diff.Parse(
        "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n");

    private static Task<Diff> Delta(ReviewSession _, CancellationToken __) => Task.FromResult(SampleDiff);

    private static ReviewRunRequest Request(
        PeanutConfig config, string headSha, IReadOnlyList<ExistingComment> existing,
        IReviewer reviewer, bool allowUnchangedSkip = true) =>
        new(config, Repo, headSha, existing, Delta, reviewer, AllowUnchangedSkip: allowUnchangedSkip);

    [Fact]
    public async Task Runs_one_advanced_comment_per_assigned_persona()
    {
        var result = await ReviewRunner.RunAsync(
            Request(Config("architect", "bug-hunter"), "sha1", [], new StubReviewer()));

        Assert.Equal(2, result.Personas.Count);
        Assert.All(result.Personas, p => Assert.Equal(PersonaOutcome.Reviewed, p.Outcome));
        Assert.Equal(2, result.RenderedBodies.Count);
        // Each body carries the persona marker + an embedded session blob.
        Assert.All(result.RenderedBodies, b =>
        {
            Assert.NotNull(CommentSync.MarkerOf(b));
            Assert.NotNull(SessionCodec.Extract(b));
        });
    }

    [Fact]
    public async Task Unknown_repo_yields_no_results()
    {
        var req = new ReviewRunRequest(Config("architect"), "no-such-repo", "sha1", [], Delta, new StubReviewer());
        var result = await ReviewRunner.RunAsync(req);
        Assert.Empty(result.Personas);
    }

    [Fact]
    public async Task Persona_unchanged_when_head_and_comments_are_the_same()
    {
        // Seed an existing comment whose embedded session already reviewed sha1 with no findings.
        var prior = new ReviewSession("sha1", Turn: 1, Summary: "s", Array.Empty<Finding>(), LastSeenCommentId: 0);
        var body = SessionCodec.Embed(CommentRenderer.Marker("architect") + "\n### A\n_x_\n", prior);
        var existing = new[] { new ExistingComment(10, body, "github-actions", IsBot: true) };

        var result = await ReviewRunner.RunAsync(
            Request(Config("architect"), "sha1", existing, new StubReviewer()));

        var p = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Unchanged, p.Outcome);
        Assert.Null(p.Body);
        Assert.Empty(result.RenderedBodies);
        Assert.Equal(1, result.Unchanged);
    }

    [Fact]
    public async Task Offline_run_never_skips_as_unchanged()
    {
        var prior = new ReviewSession("sha1", Turn: 1, Summary: "s", Array.Empty<Finding>(), LastSeenCommentId: 0);
        var body = SessionCodec.Embed(CommentRenderer.Marker("architect") + "\n### A\n_x_\n", prior);
        var existing = new[] { new ExistingComment(10, body, "github-actions", IsBot: true) };

        var result = await ReviewRunner.RunAsync(
            Request(Config("architect"), "sha1", existing, new StubReviewer(), allowUnchangedSkip: false));

        Assert.Equal(PersonaOutcome.Reviewed, Assert.Single(result.Personas).Outcome);
    }

    [Fact]
    public async Task Model_failure_becomes_a_failure_comment_not_a_throw()
    {
        var result = await ReviewRunner.RunAsync(
            Request(Config("architect"), "sha1", [], new ThrowingReviewer()));

        var p = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Failed, p.Outcome);
        Assert.NotNull(p.Body);
        Assert.Contains("could not run", p.Body);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ReviewRunner.RunAsync(Request(Config("architect"), "sha1", [], new CancelingReviewer()), ct: cts.Token));
    }

    [Fact]
    public async Task Progress_is_reported_per_persona()
    {
        var lines = new List<string>();
        await ReviewRunner.RunAsync(
            Request(Config("architect"), "sha1", [], new StubReviewer()),
            log: m => { lock (lines) lines.Add(m); });

        Assert.Contains(lines, l => l.Contains("[architect]") && l.Contains("reviewing"));
    }

    private sealed class ThrowingReviewer : IReviewer
    {
        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default) =>
            throw new InvalidOperationException("provider exploded");
    }

    private sealed class CancelingReviewer : IReviewer
    {
        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            throw new OperationCanceledException();

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(ModelReply.Untracked("{}"));
        }
    }
}
