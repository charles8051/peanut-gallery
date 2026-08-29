using System;

namespace PeanutGallery.Core;

/// <summary>
/// Pure decision: is this review run reviewing a stale head? A run triggered for one
/// head SHA can find, by the time it reaches the PR, that a newer push has moved the
/// head - a newer run is (or will be) reviewing the current head, so this run should
/// skip cleanly (exit 0) rather than post a review for an outdated SHA. The shell
/// supplies the trigger SHA (from the event payload via
/// <see cref="GitHubEventGuard.TriggerHeadSha"/>) and the live head SHA.
///
/// This is the "clean skip" half of the cancelled-review fix (issue #32): superseded
/// runs early-exit green instead of being killed by <c>cancel-in-progress</c>, which
/// turned the check rollup UNSTABLE. It pairs with <c>cancel-in-progress: false</c> in
/// the consumer workflows, which serialize push-vs-comment collisions instead of
/// cancelling them.
/// </summary>
public static class Supersession
{
	/// <summary>
	/// The reason this run is superseded, or null if it should proceed. A null/empty
	/// trigger SHA (e.g. an <c>issue_comment</c> event carries no head SHA) is never
	/// treated as superseded - those runs proceed and review the live head. An empty
	/// live head SHA also proceeds (nothing to compare against).
	/// </summary>
	public static string? SupersededReason(string? triggerHeadSha, string? liveHeadSha)
	{
		if (string.IsNullOrEmpty(triggerHeadSha) || string.IsNullOrEmpty(liveHeadSha))
		{
			return null;
		}

		// IgnoreCase: git SHAs are lowercase hex from the GitHub API, but a spurious case
		// difference must not read as "moved" (that would skip a review that should run).
		return string.Equals(triggerHeadSha, liveHeadSha, StringComparison.OrdinalIgnoreCase)
			? null
			: $"head moved {Sha.Short(triggerHeadSha)} -> {Sha.Short(liveHeadSha)} since this run was triggered";
	}

}
