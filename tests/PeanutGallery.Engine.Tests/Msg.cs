using System.Linq;
using PeanutGallery.Core;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// Role-based access to a <see cref="ReviewRequest"/>'s messages — the engine-test twin of the
/// core-test helper of the same name. See that one for why tests ask by role rather than by index.
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
