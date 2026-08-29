using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Desktop.Model;
using PeanutGallery.Engine;

namespace PeanutGallery.Desktop.Services;

/// <summary>
/// Runs auto-review for subscribed repos while the app is open: each cycle lists open PRs and,
/// for any whose head moved since we last handled it (<see cref="AutoReviewPlan.Pending"/>), runs
/// a review with <c>allowUnchangedSkip: true</c> and posts only if something new was produced.
///
/// Token-safe by construction: the head-SHA dedup keeps the steady state (no pushes) to one
/// cheap PR-list call per repo per cycle, and the runner's unchanged-skip means a persona whose
/// session already covers a head costs zero model calls — so it also cooperates with CI (whoever
/// reviews a head first, the other finds it unchanged and skips).
/// </summary>
public sealed class AutoReviewService
{
    private readonly Dictionary<string, string> _seenHeads = new(StringComparer.Ordinal);

    /// <summary>Run one poll cycle over the subscribed repos. Returns how many PRs were posted.</summary>
    public async Task<int> RunCycleAsync(
        DesktopConfig config, IReadOnlyList<string> subscribedSlugs,
        Func<PeanutConfig, IReviewer> reviewerFactory, Action<string>? log = null, CancellationToken ct = default)
    {
        if (config.Token is null || subscribedSlugs.Count == 0)
        {
            return 0;
        }

        using var gh = new GitHubClient(config.Token, config.ApiBaseUrl);
        var posted = 0;

        foreach (var slug in subscribedSlugs)
        {
            ct.ThrowIfCancellationRequested();
            if (RepoSlug.Split(slug) is not { } parts) continue;
            var (owner, name) = parts;

            IReadOnlyList<PrRaw> prs;
            try
            {
                prs = await gh.ListOpenPullRequestsAsync(owner, name, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log?.Invoke($"auto-review: {slug} — list failed: {e.Message}");
                continue;
            }

            PruneClosed(owner, name, prs);

            foreach (var pr in AutoReviewPlan.Pending(_seenHeads, owner, name, prs))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var preview = await ReviewOrchestrator.PreviewAsync(
                        gh, owner, name, pr.Number, reviewerFactory, log, ct, allowUnchangedSkip: true);

                    if (preview.Result.RenderedBodies.Count > 0)
                    {
                        var (created, updated) = await ReviewOrchestrator.PostAsync(gh, owner, name, pr.Number, preview, ct);
                        posted++;
                        log?.Invoke($"auto-review: {slug}#{pr.Number} — posted {created} new, {updated} updated");
                    }
                    else
                    {
                        log?.Invoke($"auto-review: {slug}#{pr.Number} — nothing new");
                    }

                    // Mark handled only after a successful attempt, so a transient failure retries next cycle.
                    _seenHeads[AutoReviewPlan.Key(owner, name, pr.Number)] = pr.HeadSha;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log?.Invoke($"auto-review: {slug}#{pr.Number} — failed: {e.Message}");
                }
            }
        }

        return posted;
    }

    // Drop dedup entries for PRs of this repo that are no longer open, so _seenHeads can't grow
    // unbounded over a long session. (The dictionary is per-instance and already empty on restart.)
    private void PruneClosed(string owner, string repo, IReadOnlyList<PrRaw> openPrs)
    {
        var live = new HashSet<string>(openPrs.Select(p => AutoReviewPlan.Key(owner, repo, p.Number)), StringComparer.Ordinal);
        var prefix = $"{owner}/{repo}#";
        foreach (var key in _seenHeads.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !live.Contains(k)).ToList())
        {
            _seenHeads.Remove(key);
        }
    }
}
