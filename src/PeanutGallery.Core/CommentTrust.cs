namespace PeanutGallery.Core;

/// <summary>
/// Who on a pull request thread is allowed to be believed, as a pure decision.
///
/// <para>The panel keeps its whole datastore in PR comments - each persona's session, the pinned
/// panel, the metrics ledger - and a comment is a thing any reader of a public repository can
/// write. So "is this comment ours?" is a security question, not bookkeeping: a forged
/// <c>pg-panel</c> blob supplies the personas' system prompts and model ids, and a forged
/// <c>pg-state</c> blob can empty the board or set the SHA that makes the whole review skip.</para>
///
/// <para>Trusted means a bot, or a human GitHub reports as OWNER / MEMBER / COLLABORATOR. Bots
/// are in because the panel itself posts as one (<c>github-actions[bot]</c> under the default
/// token) and a run cannot cheaply learn its own login; the set that can post as a bot on a repo
/// is bounded by who can install an app on it. Everything else - CONTRIBUTOR, FIRST_TIME_
/// CONTRIBUTOR, NONE, and an association we could not read at all - is untrusted.</para>
///
/// <para>Shared with <see cref="GitHubEventGuard"/> so the trigger guard and the state guard
/// cannot drift apart on what "trusted author" means.</para>
/// </summary>
public static class CommentTrust
{
	/// <summary>
	/// May a comment from this author supply panel state? Total: an unrecognised or absent
	/// association is refused, because a guard that cannot tell has to say no.
	///
	/// <para>The trusted <c>author_association</c> values are matched inline rather than held in a
	/// collection. A security decision in the core has to be a function of its arguments and
	/// nothing else, and a static <c>HashSet</c> is a mutable field that anything in this assembly
	/// could add to.</para>
	/// </summary>
	public static bool IsTrustedAuthor(bool isBot, string? authorAssociation) =>
		isBot || authorAssociation is "OWNER" or "MEMBER" or "COLLABORATOR";

	/// <summary>
	/// May this comment carry the panel's own state - a session blob, a pin, the metrics ledger,
	/// or the marker that says which comment to update in place?
	/// </summary>
	public static bool CarriesState(ExistingComment comment) => comment.AuthorIsTrusted;

	/// <summary>
	/// May this comment steer the reviewers - the conversation turn that can withdraw or resolve
	/// findings? Bots are excluded here even though they are trusted state authors: this is the
	/// seam for a human explaining that a finding is intentional, and letting the panel answer
	/// its own comments is a loop, not a conversation.
	/// </summary>
	public static bool MayDirectPanel(ExistingComment comment) =>
		!comment.IsBot && comment.AuthorIsTrusted;
}
