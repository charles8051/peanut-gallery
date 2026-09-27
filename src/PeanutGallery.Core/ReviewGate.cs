using System;

namespace PeanutGallery.Core;

/// <summary>The states GitHub's commit-status API accepts.</summary>
public enum CommitState
{
	Pending,
	Success,
	Failure,
	Error,
}

/// <summary>One commit status as the gate posts it.</summary>
public sealed record CommitStatus(CommitState State, string Description)
{
	/// <summary>GitHub's limit on a status description.</summary>
	public const int MaxDescription = 140;

	/// <summary>Capped at <see cref="MaxDescription"/>: a skip reason carries a label or marker the
	/// repo authored, and an over-long description would fail the post rather than shorten it.</summary>
	public string Description { get; init; } = Description.Length <= MaxDescription
		? Description
		: string.Concat(Description.AsSpan(0, MaxDescription - 1), "…");
}

/// <summary>
/// Pure: the <c>peanut-gallery</c> commit status on a PR's head commit. A repository that makes
/// this context a required check gets a review that holds auto-merge, which the advisory comment
/// cannot do: auto-merge reads required checks and required reviews, never comments.
///
/// <para>The status is written against a SHA rather than carried by the job's own check run. An
/// <c>issue_comment</c> run is attached to the default branch, so the check it produces never
/// reaches the PR head, and a finding withdrawn in conversation could not clear a check that only
/// the <c>pull_request</c> run owns. A status posted by SHA can be updated from either trigger.</para>
///
/// <para>Success is only ever the verdict <see cref="ReviewVerdict.Clean"/>, the same one
/// <c>await-review</c> exits 0 on.</para>
/// </summary>
public static class ReviewGate
{
	/// <summary>The status context a repository names in its required checks.</summary>
	public const string Context = "peanut-gallery";

	/// <summary>Opt-in switch, set by the action's <c>commit-status</c> input.</summary>
	public const string Variable = "PG_COMMIT_STATUS";

	/// <summary>Total: only <c>1</c> / <c>true</c> / <c>yes</c> (any case) turn the gate on.</summary>
	public static bool Enabled(string? raw) => raw?.Trim().ToLowerInvariant() is "1" or "true" or "yes";

	/// <summary>Posted before any model call, so a required check holds while the review runs.</summary>
	public static CommitStatus Reviewing(string headSha) =>
		new(CommitState.Pending, $"Reviewing {Sha.Short(headSha)}");

	/// <summary>
	/// The PR opted out. A skip is the repository's own switch and only someone with write access
	/// can set it on a same-repo PR, so it passes the gate. A draft stays pending instead: marking it
	/// ready fires a review of the same SHA, and a success left over from the draft would let
	/// auto-merge go before that review posted anything.
	/// </summary>
	public static CommitStatus Skipped(string reason, bool isDraft) => isDraft
		? new(CommitState.Pending, $"Skipped while draft ({reason}); decided when marked ready")
		: new(CommitState.Success, $"Review skipped: {reason}");

	/// <summary>
	/// The verdict of a finished run, read from the panel body this run rendered. Only panel mode can
	/// be judged: per-persona comments are each a partial picture, and a gate that passed on them
	/// would pass on whichever it happened to read.
	/// </summary>
	/// <param name="panelBody">The run's rendered panel comment, or null when no reviewer ran.</param>
	public static CommitStatus After(CommentMode mode, string? panelBody, string headSha)
	{
		if (mode != CommentMode.Panel)
		{
			return new(CommitState.Error, "The status gate needs \"comment\": \"panel\" in the config");
		}

		var sha = Sha.Short(headSha);
		if (panelBody is null)
		{
			return new(CommitState.Error, $"No reviewer ran on {sha}");
		}

		return PanelReadiness.Read([panelBody], headSha).Verdict switch
		{
			ReviewVerdict.Clean => new(CommitState.Success, $"No findings at {sha}"),
			ReviewVerdict.Findings => new(CommitState.Failure, $"Findings to address at {sha}"),
			ReviewVerdict.Incomplete => new(CommitState.Error, $"Partial review at {sha}: a reviewer did not report"),
			_ => new(CommitState.Error, $"No reviewer reported {sha}"),
		};
	}
}
