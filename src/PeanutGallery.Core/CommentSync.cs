using System;
using System.Collections.Generic;
using System.Linq;

namespace PeanutGallery.Core;

/// <summary>
/// A PR comment already on the thread: id, body, author login, and whether it's a bot.
/// </summary>
/// <param name="AuthorIsTrusted">Whether this comment's author may be believed - see
/// <see cref="CommentTrust"/> for what that means and why it matters. Defaults to true because
/// the only values constructed WITHOUT going through the API are ones this run wrote itself
/// (<see cref="CommentLedger.Record"/>), and those are trusted by definition. Every value that
/// comes off the GitHub API must set it from <see cref="CommentTrust.IsTrustedAuthor"/>; the two
/// clients that do that are the only boundary where an untrusted comment can enter.</param>
public sealed record ExistingComment(
	long Id,
	string Body,
	string Author = "",
	bool IsBot = false,
	bool AuthorIsTrusted = true);

/// <summary>Whether a rendered comment should be created new or update an existing one.</summary>
public enum UpsertAction
{
	Create,
	Update,
}

/// <summary>A single planned comment operation.</summary>
/// <param name="Action">Create or update.</param>
/// <param name="CommentId">The existing comment id to update; null for a create.</param>
/// <param name="Body">The rendered comment body to post.</param>
public sealed record CommentUpsert(UpsertAction Action, long? CommentId, string Body);

/// <summary>
/// Pure decision of how to reconcile freshly rendered persona comments against the
/// comments already on a PR: match by each comment's stable marker
/// (<c>&lt;!-- peanut-gallery:&lt;id&gt; --&gt;</c>) so each persona keeps exactly one
/// comment, updated in place across pushes instead of duplicated. No IO - the shell
/// fetches the existing comments and executes the plan; the matching logic is here,
/// total and testable.
/// </summary>
public static class CommentSync
{
	public static IReadOnlyList<CommentUpsert> Plan(
		IReadOnlyList<ExistingComment> existing,
		IReadOnlyList<string> renderedComments)
	{
		var plan = new List<CommentUpsert>();
		foreach (var body in renderedComments)
		{
			var marker = MarkerOf(body);
			// Only a comment we could have written is a candidate to update. A marker is just text,
			// so without this a stranger's comment carrying one becomes the target: with
			// pull-requests:write the PATCH succeeds, the review lands inside their comment, and
			// the panel's own comment is never refreshed again.
			var match = marker is null
				? null
				: existing.FirstOrDefault(c =>
					CommentTrust.CarriesState(c) && c.Body.Contains(marker, StringComparison.Ordinal));

			plan.Add(match is null
				? new CommentUpsert(UpsertAction.Create, null, body)
				: new CommentUpsert(UpsertAction.Update, match.Id, body));
		}

		return plan;
	}

	private const string MarkerOpen = "<!-- peanut-gallery:";
	private const string MarkerClose = "-->";

	/// <summary>The marker is the first line when it has the peanut-gallery marker shape; else null.</summary>
	public static string? MarkerOf(string body)
	{
		var newline = body.IndexOf('\n');
		var firstLine = (newline >= 0 ? body[..newline] : body).Trim();
		return firstLine.StartsWith(MarkerOpen, StringComparison.Ordinal)
			&& firstLine.EndsWith(MarkerClose, StringComparison.Ordinal)
			? firstLine
			: null;
	}

	/// <summary>The persona id inside a comment's marker (<c>&lt;!-- peanut-gallery:&lt;id&gt; --&gt;</c>), else null.</summary>
	public static string? PersonaIdOf(string body)
	{
		var marker = MarkerOf(body);
		if (marker is null)
		{
			return null;
		}

		var end = marker.LastIndexOf(MarkerClose, StringComparison.Ordinal);
		return end > MarkerOpen.Length ? marker[MarkerOpen.Length..end].Trim() : null;
	}
}
