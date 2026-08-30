using System;
using System.Text.Json;

namespace PeanutGallery.Core;

/// <summary>
/// Pure trust decision for a comment event payload, so the action can refuse a
/// comment-triggered review from a bot or an untrusted author on its own - without
/// relying on the consumer workflow's <c>if:</c> being correct. Returns true only for
/// a present comment from a non-bot OWNER/MEMBER/COLLABORATOR.
///
/// <para><b>Fails CLOSED.</b> Empty input, a payload with no <c>comment</c> object, and
/// unreadable JSON all return false. The caller only consults this once the event name
/// says a comment triggered the run, so each of those states means "a comment event
/// whose author cannot be established" - which is exactly the case the gate exists to
/// refuse. It fails open only in the sense that a non-comment run never asks.</para>
///
/// <para>Shape-based, not event-name-based, so it covers <c>issue_comment</c> and
/// <c>pull_request_review_comment</c> alike: both carry <c>comment.user.type</c> and
/// <c>comment.author_association</c>. Adding a third comment trigger needs a change to
/// the caller's event-name set, not to this function.</para>
/// </summary>
public static class GitHubEventGuard
{
	/// <summary>
	/// True when <paramref name="eventName"/> is a comment trigger this guard adjudicates.
	/// The caller gates on this; a trigger not named here is not a comment event.
	///
	/// <para>Matched inline rather than held in a collection, for the reason
	/// <see cref="CommentTrust.IsTrustedAuthor"/> gives about its own set: a security
	/// decision in the core has to be a function of its arguments and nothing else, and
	/// even a private static array is a mutable field that anything in this assembly
	/// could rewrite. Adding a trigger is a source change, reviewed like any other.</para>
	/// </summary>
	public static bool IsCommentEvent(string? eventName) =>
		eventName is "issue_comment" or "pull_request_review_comment";

	/// <summary>
	/// True when a comment event's payload says the comment is attached to a pull request.
	///
	/// <para>Two shapes, because <see cref="IsCommentEvent"/> covers two triggers.
	/// <c>issue_comment</c> fires for issues AND pull requests and separates them by
	/// <c>issue.pull_request</c>, an object GitHub populates only when the issue is a PR.
	/// <c>pull_request_review_comment</c> carries the pull request at the root instead and
	/// has no <c>issue</c> at all.</para>
	///
	/// <para><b>Fails CLOSED</b>, like the trust gate above: absent, unreadable, or
	/// non-object payloads on a comment-triggered run all mean "a comment whose subject
	/// cannot be established". Refusing costs a review that should have run and says so on
	/// stderr; accepting sends whatever number the workflow passed to
	/// <c>GET /pulls/{n}</c>, and a comment on a plain issue hands over an issue number -
	/// the 404 this exists to prevent (#37).</para>
	/// </summary>
	public static bool IsCommentOnPullRequest(string? eventJson)
	{
		if (string.IsNullOrWhiteSpace(eventJson))
		{
			return false;
		}

		try
		{
			using var doc = JsonDocument.Parse(eventJson);
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				return false;
			}

			// pull_request_review_comment: the PR is at the root, and its presence IS the answer.
			if (root.TryGetProperty("pull_request", out var pr) && pr.ValueKind == JsonValueKind.Object)
			{
				return true;
			}

			// issue_comment: the link object exists only for a PR, so a plain issue lands here
			// with no `pull_request` key and is refused.
			return root.TryGetProperty("issue", out var issue)
				&& issue.ValueKind == JsonValueKind.Object
				&& issue.TryGetProperty("pull_request", out var link)
				&& link.ValueKind == JsonValueKind.Object;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	public static bool IsTrustedCommentEvent(string? eventJson)
	{
		if (string.IsNullOrWhiteSpace(eventJson))
		{
			return false;
		}

		try
		{
			using var doc = JsonDocument.Parse(eventJson);

			// Every step checks ValueKind before descending. TryGetProperty THROWS
			// InvalidOperationException on a non-object element, and `[]`, `null`, `42` and
			// `"payload"` are all valid JSON - parsing them succeeds and the descent is what
			// fails. A thrown exception is not a refusal; it kills the command.
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object
				|| !root.TryGetProperty("comment", out var comment)
				|| comment.ValueKind != JsonValueKind.Object)
			{
				// A comment event with no comment object is malformed, not someone else's
				// event - the caller already established the trigger was a comment.
				return false;
			}

			// The author's identity must be POSITIVELY established, not merely
			// not-known-to-be-a-bot. Absent, null, non-object, or a user with no string
			// `type` all mean "cannot tell who wrote this", and a trusted
			// author_association alongside an unidentifiable user does not rescue it -
			// author_association is attacker-adjacent metadata, the user object is the
			// identity. This is what makes the fail-closed contract hold end to end.
			if (!comment.TryGetProperty("user", out var user)
				|| user.ValueKind != JsonValueKind.Object
				|| !user.TryGetProperty("type", out var type)
				|| type.ValueKind != JsonValueKind.String)
			{
				return false;
			}

			// ALLOWLIST, not a Bot denylist. GitHub sends "User" or "Bot" today, but this is a
			// public function over an arbitrary payload, and rejecting only "Bot" would treat
			// "App", "Alien" or any future type as an established human. Under a fail-closed
			// contract an unrecognised type is exactly the case to refuse.
			//
			// Bots are refused here and the flag is never forwarded.
			// CommentTrust.IsTrustedAuthor treats isBot as TRUSTED - `isBot || assoc is
			// OWNER/MEMBER/COLLABORATOR` - because bots author the panel's own state
			// comments. A trigger gate needs the opposite: a bot must never start a run, or
			// the panel answers itself.
			if (!string.Equals(type.GetString(), "User", StringComparison.Ordinal))
			{
				return false;
			}

			var assoc = comment.TryGetProperty("author_association", out var aa) && aa.ValueKind == JsonValueKind.String
				? aa.GetString()
				: null;
			// Same trusted ASSOCIATION set the state guard uses, so those cannot drift apart.
			// The bot handling deliberately differs; see above.
			return CommentTrust.IsTrustedAuthor(isBot: false, assoc);
		}
		catch (JsonException)
		{
			return false;
		}
	}

	/// <summary>
	/// The head commit SHA the run was triggered for, read from a <c>pull_request</c>
	/// event payload (<c>pull_request.head.sha</c>). Null when the payload carries no
	/// such SHA - e.g. an <c>issue_comment</c> event (a comment carries no head), or
	/// unreadable/absent JSON. Used by <see cref="Supersession"/> to detect a head that
	/// has moved on since this run started.
	/// </summary>
	public static string? TriggerHeadSha(string? eventJson)
	{
		if (string.IsNullOrWhiteSpace(eventJson))
		{
			return null;
		}

		try
		{
			using var doc = JsonDocument.Parse(eventJson);
			return doc.RootElement.TryGetProperty("pull_request", out var pr) && pr.ValueKind == JsonValueKind.Object
				&& pr.TryGetProperty("head", out var head) && head.ValueKind == JsonValueKind.Object
				&& head.TryGetProperty("sha", out var sha) && sha.ValueKind == JsonValueKind.String
				? sha.GetString()
				: null;
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
