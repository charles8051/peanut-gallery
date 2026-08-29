using System;
using System.Collections.Generic;
using System.Linq;

namespace PeanutGallery.Core;

/// <summary>The PR facts a per-PR opt-out decision is made from.</summary>
public sealed record PullRequestMeta(IReadOnlyList<string> Labels, string Title, string Body, bool IsDraft);

/// <summary>
/// Per-PR opt-out policy: skip the review when the PR carries a skip <see cref="Labels"/>
/// label, a title/body <see cref="Markers"/> marker, or (when <see cref="Drafts"/> is set)
/// is a draft. Pure + total; lives in config so it can't be misconfigured per-repo.
/// </summary>
public sealed record SkipPolicy(
	IReadOnlyList<string>? Labels,
	IReadOnlyList<string>? Markers,
	bool Drafts)
{
	/// <summary>Skip labels; empty when none were configured. Never null - see <see cref="Markers"/>.</summary>
	public IReadOnlyList<string> Labels { get; init; } = Labels ?? [];

	/// <summary>
	/// Title/body skip markers; empty when none were configured. Never null: a partial block such
	/// as <c>{"drafts": true}</c> reaches this constructor with two null lists from any
	/// reflection-based codec, and <see cref="Evaluate"/> would then throw on a PR that carries no
	/// skip signal at all (#194).
	/// </summary>
	public IReadOnlyList<string> Markers { get; init; } = Markers ?? [];

	public static SkipPolicy Default { get; } = new(
		["peanut-gallery: skip", "no-review"],
		["[skip-review]", "[no-peanut-gallery]"],
		false);

	/// <summary>(true, reason) when the PR opts out of review; (false, null) otherwise.</summary>
	public (bool Skip, string? Reason) Evaluate(PullRequestMeta pr)
	{
		if (Drafts && pr.IsDraft)
		{
			return (true, "draft PR");
		}

		foreach (var label in pr.Labels)
		{
			if (Labels.Any(l => string.Equals(l, label, StringComparison.OrdinalIgnoreCase)))
			{
				return (true, $"label '{label}'");
			}
		}

		var haystack = pr.Title + "\n" + pr.Body;
		foreach (var marker in Markers)
		{
			if (!string.IsNullOrEmpty(marker) && haystack.Contains(marker, StringComparison.OrdinalIgnoreCase))
			{
				return (true, $"marker '{marker}'");
			}
		}

		return (false, null);
	}
}
