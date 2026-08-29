using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Desktop.Model;

namespace PeanutGallery.Desktop.Services;

/// <summary>The imperative shell: fetch everything the pure <see cref="SnapshotBuilder"/> needs.</summary>
public static class SnapshotService
{
    /// <summary>
    /// Load a live snapshot from GitHub for the configured repos. Fetches each repo's open PRs
    /// and their comments (sequentially — fan-out is a later slice), then hands the raw data to
    /// the pure fold.
    /// </summary>
    public static async Task<WorkspaceSnapshot> LoadAsync(
        DesktopConfig config, IReadOnlyList<string> repoSlugs, string? selectedSlug = null,
        IReadOnlyList<string>? autoReviewSlugs = null, CancellationToken ct = default)
    {
        if (config.Token is null || repoSlugs.Count == 0)
        {
            throw new InvalidOperationException("No token or repos configured for a live load.");
        }

        using var gh = new GitHubClient(config.Token, config.ApiBaseUrl);

        var repoInputs = new List<RepoInput>(repoSlugs.Count);
        foreach (var slug in repoSlugs)
        {
            if (RepoSlug.Split(slug) is not { } parts) continue;
            var (owner, name) = parts;

            var prs = await gh.ListOpenPullRequestsAsync(owner, name, ct);
            var prInputs = new List<PrInput>(prs.Count);
            foreach (var pr in prs)
            {
                var comments = await gh.ListIssueCommentsAsync(owner, name, pr.Number, ct);
                prInputs.Add(new PrInput(pr, comments));
            }

            repoInputs.Add(new RepoInput(owner, name, prInputs));
        }

        return SnapshotBuilder.Build(repoInputs, selectedSlug ?? repoSlugs.FirstOrDefault(), autoReviewSlugs);
    }
}
