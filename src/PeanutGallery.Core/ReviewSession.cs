using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>
/// The persisted, per-(PR, persona) review state — the "session" of a stateful
/// reviewer. It is data, not a live process: each PR push (or new comment) loads it,
/// advances it one turn against the delta + any new conversation, and saves it again.
/// <see cref="LastSeenCommentId"/> is the watermark of PR comments already incorporated,
/// so a turn only ingests genuinely-new author/reviewer remarks.
/// </summary>
/// <param name="Dropped">Titles of findings this reviewer already had taken off the board - refuted
/// by the adversarial pass, or suppressed by the confidence gate. Carried forward so the model can
/// be told not to raise them again; without it the drops are invisible to the model, which re-emits
/// the same finding every push and pays to have it dropped again. See <see cref="DroppedMemory"/>.</param>
public sealed record ReviewSession(
	string? LastReviewedSha,
	int Turn,
	string Summary,
	IReadOnlyList<Finding> OpenFindings,
	long LastSeenCommentId = 0,
	IReadOnlyList<string>? Dropped = null)
{
	public static ReviewSession Initial { get; } = new(null, 0, string.Empty, []);

	/// <summary>True on the very first review (no prior state) — review the whole PR diff.</summary>
	public bool IsFirstTurn => Turn == 0 || LastReviewedSha is null;

	/// <summary><see cref="Dropped"/>, never null - sessions stored before this field existed have none.</summary>
	public IReadOnlyList<string> DroppedTitles => Dropped ?? [];
}

/// <summary>
/// One turn's structured reply from a reviewer: its refreshed running summary, its
/// CURRENT full set of open findings, the titles it now considers <see cref="Resolved"/>
/// (fixed in code), and the titles it <see cref="Withdrawn"/> (an author/reviewer
/// comment explained them as intentional or a false positive).
/// </summary>
public sealed record SessionUpdate(
	string Summary,
	IReadOnlyList<Finding> Findings,
	IReadOnlyList<string> Resolved,
	IReadOnlyList<string> Withdrawn);

/// <summary>
/// The outcome of reading one turn's reply. The distinction this type exists to make:
/// an <em>understood</em> reply with no findings is a legitimately clean review, whereas
/// a reply we could not read at all is a failure. Collapsing the two (returning an empty
/// <see cref="SessionUpdate"/> for both) turns a malformed model reply into a silent
/// "looks good" - a false negative the author never sees. <see cref="Parsed"/> false means
/// the caller must NOT post this as a review.
/// </summary>
/// <param name="WasEmpty">The model returned nothing at all (whitespace/empty), as opposed to a
/// non-empty reply that ignored the contract. An empty completion is the signature of a prompt the
/// model could not answer - typically one too large - so the shell can retry it with a smaller
/// prompt, where re-asking a malformed-but-present reply for the same size would just repeat.</param>
public sealed record SessionUpdateResult(SessionUpdate Update, bool Parsed, string? Reason, bool WasEmpty = false)
{
	private static readonly SessionUpdate Empty = new(string.Empty, [], [], []);

	public static SessionUpdateResult Unreadable(string reason) => new(Empty, false, reason);

	public static SessionUpdateResult EmptyReply(string reason) => new(Empty, false, reason, WasEmpty: true);

	public static SessionUpdateResult Ok(SessionUpdate update) => new(update, true, null);
}

/// <summary>A human PR comment (author or reviewer) fed into a turn as context.</summary>
public sealed record AuthorComment(string Author, string Body);

/// <summary>
/// What the author says this PR is *for* — its title and description. Fed into the first
/// turn so a reviewer judges the change against its stated intent instead of inferring it
/// from the diff alone (which manufactures "why would you do this" findings the description
/// already answers). Like <see cref="AuthorComment"/> it is untrusted human context, never
/// instructions: the prompt frames it as such so a PR body cannot steer the review.
/// </summary>
public sealed record PullRequestIntent(string Title, string Body)
{
	/// <summary>Nothing worth telling the model — neither a title nor a description.</summary>
	public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Body);
}
