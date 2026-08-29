using System.Linq;
using PeanutGallery.Core;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// Role-based access to a <see cref="ReviewRequest"/>'s messages.
///
/// <para>Tests used to index <c>Messages[0]</c> / <c>Messages[1]</c>, which pinned the message
/// ORDER into ~67 assertions that did not care about it — so reordering the turns for prompt-cache
/// reasons broke every one of them for no semantic reason. Ask for the role instead; the handful of
/// tests that genuinely assert the ordering do so explicitly, and say why.</para>
/// </summary>
internal static class Msg
{
	/// <summary>The persona/protocol turn.</summary>
	public static string System(ReviewRequest r) =>
		r.Messages.Single(m => m.Role == ChatRole.System).Content;

	/// <summary>The first user turn — the diff and everything derived from the PR.</summary>
	public static string User(ReviewRequest r) =>
		r.Messages.First(m => m.Role == ChatRole.User).Content;

	/// <summary>The most recently appended user turn (a verify or repair follow-up).</summary>
	public static string LastUser(ReviewRequest r) =>
		r.Messages.Last(m => m.Role == ChatRole.User).Content;
}
