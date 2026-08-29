using System.Collections.Generic;

namespace PeanutGallery.Desktop.Model;

/// <summary>
/// Pure decision for the auto-review poller: given the PR head SHAs already handled this session
/// and the repo's current open PRs, which PRs are new or have a changed head (and so warrant a
/// review pass). Belt-and-suspenders on top of the runner's own unchanged-skip: it keeps the
/// steady state (no new pushes) down to one cheap PR-list call per cycle, invoking a review
/// pass only when a head actually moved.
/// </summary>
public static class AutoReviewPlan
{
    public static IReadOnlyList<PrRaw> Pending(
        IReadOnlyDictionary<string, string> seenHeads, string owner, string repo, IReadOnlyList<PrRaw> openPrs)
    {
        var pending = new List<PrRaw>();
        foreach (var pr in openPrs)
        {
            if (!seenHeads.TryGetValue(Key(owner, repo, pr.Number), out var head) || head != pr.HeadSha)
            {
                pending.Add(pr);
            }
        }

        return pending;
    }

    public static string Key(string owner, string repo, int number) => $"{owner}/{repo}#{number}";
}
