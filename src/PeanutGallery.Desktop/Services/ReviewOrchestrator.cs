using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Engine;

namespace PeanutGallery.Desktop.Services;

/// <summary>The result of a one-shot review, ready to preview and (on confirm) post.</summary>
public sealed record ReviewPreview(
    string PanelRepo,
    string HeadSha,
    IReadOnlyList<ExistingComment> Existing,
    ReviewRunResult Result,
    bool UsedDefaultPanel);

/// <summary>
/// Desktop one-shot review shell: fetch the repo's committed config (or fall back to the default
/// panel), run the shared <see cref="ReviewRunner"/> in-process against the PR, and — only when
/// the user confirms — post the rendered comments via <see cref="CommentSync.Plan"/>. Provider
/// keys are read from the environment by <see cref="ChatClientReviewer"/>, never stored.
/// </summary>
public static class ReviewOrchestrator
{
    /// <param name="reviewerFactory">
    /// Builds the <see cref="IReviewer"/> from the resolved config — injected by the composition
    /// root (MainWindow), so the orchestrator neither reads the environment nor hardcodes a
    /// concrete reviewer. Tests pass a stub.
    /// </param>
    /// <param name="allowUnchangedSkip">
    /// One-shot (false) reviews even when the head is unchanged, since the user asked for it.
    /// Auto-review (true) skips a persona whose session already covers this head with no new
    /// comments — so an already-reviewed PR costs zero model calls.
    /// </param>
    public static async Task<ReviewPreview> PreviewAsync(
        GitHubClient gh, string owner, string repo, int prNumber,
        Func<PeanutConfig, IReviewer> reviewerFactory,
        Action<string>? log = null, CancellationToken ct = default, bool allowUnchangedSkip = false)
    {
        // Fetch the committed config; fall back to the default panel if the repo has none.
        PeanutConfig config;
        var usedDefault = false;
        string? json = null;
        foreach (var path in ReviewConfigResolver.ConfigPaths)
        {
            json = await gh.GetFileTextAsync(owner, repo, path, ct: ct);
            if (json is not null)
            {
                log?.Invoke($"config: {path}");
                break;
            }
        }

        if (json is null)
        {
            config = DefaultPanel.For(repo);
            usedDefault = true;
            log?.Invoke("config: none committed — using the default panel");
        }
        else
        {
            config = ConfigCodec.Parse(json);
        }

        // The CLI's counterpart line (Commands.LoadConfig): a persona sampling at a temperature
        // nobody wrote down says so, on whichever surface decoded the config (#127).
        if (Persona.UnsetTemperatureNotice(config.Personas) is { } tempNotice)
        {
            log?.Invoke(tempNotice);
        }

        var panelRepo = ReviewConfigResolver.PanelRepoName(config, repo);
        var (headSha, baseRef) = await gh.GetPullAnchorAsync(owner, repo, prNumber, ct);
        var existing = await gh.ListIssueCommentsAsync(owner, repo, prNumber, ct);

        // Read at the PR's head, matching what CI reviews: the CLI's local-checkout counterparts
        // (Commands.cs ReadConventions / ReadFileContextAsync) read the code actually under
        // review, not the default branch, so the desktop app must too or its review of the same
        // PR silently differs from CI's (#82, #87).
        var conventions = await RemoteRepoContext.ReadConventionsAsync(gh, owner, repo, headSha, ct);
        if (conventions is not null)
        {
            log?.Invoke($"applying repo conventions from {conventions.Path}");
        }

        // Shared across every diff-tier persona's context fetch (ReviewRunner calls ContextSource
        // once per persona, and personas fan out concurrently), so a PR reviewed by N personas
        // fetches each changed file's bytes once, not N times. See RemoteRepoContext for why only
        // a completed fetch (never a transient failure) is written into it.
        var contextCache = new ConcurrentDictionary<string, byte[]?>(StringComparer.Ordinal);

        // The PR's cumulative diff, ANCHORED to headSha as base...headSha rather than fetched from
        // the PR's moving head: the baseline a continued turn needs to tell this branch's own
        // earlier work from established API (#178). One call per run, not per persona - it is
        // persona-independent. Anchoring is not a nicety; a baseline ending at a newer head than the
        // delta can manufacture the claim that an established line is the branch's own, which is the
        // one direction OwnRemovals exists to rule out. A failure costs the fact, never the review:
        // the run then behaves exactly as it did before #178, which is why this catch is broad.
        Diff? baseline = null;
        try
        {
            baseline = baseRef.Length == 0
                ? null
                : Diff.Parse(await gh.GetCompareDiffAsync(owner, repo, baseRef, headSha, ct));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.Invoke($"baseline: could not resolve the pull request's cumulative diff ({e.Message}); " +
                "continued turns review without it");
        }

        var reviewer = reviewerFactory(config);
        var result = await ReviewRunner.RunAsync(
            new ReviewRunRequest(
                config, panelRepo, headSha, existing,
                DeltaSource: (prior, c) => ResolveDeltaAsync(gh, owner, repo, prNumber, headSha, prior, c),
                reviewer,
                Filter: config.Filter,
                AllowUnchangedSkip: allowUnchangedSkip,
                ContextSource: (paths, token) =>
                    RemoteRepoContext.ReadFileContextAsync(gh, owner, repo, headSha, paths, contextCache, token),
                Conventions: conventions,
                PanelPlanner: BuildPanelPlanner(config, reviewer, log),
                Baseline: baseline),
            log, ct);

        return new ReviewPreview(panelRepo, headSha, existing, result, usedDefault);
    }

    // Mirrors the CLI's panel-planner wiring (Commands.cs) so a dynamic panel (Auto / SeedAndAuto)
    // costs — and reviews with — the same reviewers whether it runs from CI or the desktop app.
    // Null whenever the config does not ask for a dynamic panel, or asks but cannot resolve a
    // persona model for the generated reviewers to review with.
    private static IPanelPlanner? BuildPanelPlanner(PeanutConfig config, IReviewer reviewer, Action<string>? log)
    {
        var spec = ReviewConfigResolver.ResolvePanelPlannerSpec(config);
        if (spec is null)
        {
            if (ReviewConfigResolver.WantsPanelPlanner(config))
            {
                log?.Invoke("panel: needs a personaModel (or at least one configured persona); " +
                    "reviewing with the configured panel.");
            }

            return null;
        }

        return new ChatClientPanelPlanner(
            reviewer, spec.Value.Orchestrator, spec.Value.PersonaModel, spec.Value.PersonaTemperature,
            PanelFence.MaxPersonas, log, spec.Value.PersonaTopP, spec.Value.PersonaTopK);
    }

    /// <summary>Post the previewed comments to the PR (create/update per persona marker).</summary>
    public static async Task<(int Created, int Updated)> PostAsync(
        GitHubClient gh, string owner, string repo, int prNumber,
        ReviewPreview preview, CancellationToken ct = default)
    {
        var plan = CommentSync.Plan(preview.Existing, preview.Result.RenderedBodies);
        var created = 0;
        var updated = 0;
        foreach (var op in plan)
        {
            if (op.Action == UpsertAction.Update)
            {
                await gh.UpdateIssueCommentAsync(owner, repo, op.CommentId!.Value, op.Body, ct);
                updated++;
            }
            else
            {
                await gh.CreateIssueCommentAsync(owner, repo, prNumber, op.Body, ct);
                created++;
            }
        }

        return (created, updated);
    }

    // First turn -> full PR diff; continued -> the delta since last review (fall back to full on a force-push).
    private static async Task<Diff> ResolveDeltaAsync(
        GitHubClient gh, string owner, string repo, int prNumber, string headSha, ReviewSession prior, CancellationToken ct)
    {
        // A stored SHA that is not shaped like a commit id came out of a PR comment and is not a
        // commit — review the whole PR rather than compare against whatever it names.
        if (prior.IsFirstTurn || !Sha.IsCommitId(prior.LastReviewedSha))
        {
            return Diff.Parse(await gh.GetPullRequestDiffAsync(owner, repo, prNumber, ct));
        }

        try
        {
            return Diff.Parse(await gh.GetCompareDiffAsync(owner, repo, prior.LastReviewedSha!, headSha, ct));
        }
        // Only fall back on "no comparison" (force-push / rebased base): 404 no common ancestor,
        // 422 unprocessable. Let real errors (auth, rate-limit, 5xx) surface instead of silently
        // re-reviewing the whole PR.
        catch (GitHubApiException e) when (e.StatusCode is 404 or 422)
        {
            return Diff.Parse(await gh.GetPullRequestDiffAsync(owner, repo, prNumber, ct));
        }
    }
}
