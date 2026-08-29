using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>Chat role. Kept provider-agnostic so the core never depends on an SDK.</summary>
public enum ChatRole
{
	System,
	User,
}

/// <summary>One chat message in an assembled review request.</summary>
public sealed record Message(ChatRole Role, string Content);

/// <summary>
/// A fully assembled, provider-agnostic review request: which model to call, at
/// what temperature, with which messages. The shell maps this onto whatever
/// client it uses (e.g. an <c>IChatClient</c> from Microsoft.Extensions.AI).
/// </summary>
public sealed record ReviewRequest(
	ModelRef Model,
	double Temperature,
	ReviewTier Tier,
	IReadOnlyList<Message> Messages,
	double? TopP = null,
	int? TopK = null);

/// <summary>
/// One unit of review work: a persona reviewing a repo, with its request
/// pre-assembled. A <see cref="ReviewPlanner"/> turns config + a diff into a set
/// of these; the shell executes them (in parallel) and feeds results back to the
/// <see cref="CommentRenderer"/>.
/// </summary>
public sealed record ReviewTask(Persona Persona, RepoTarget Repo, ReviewRequest Request);
