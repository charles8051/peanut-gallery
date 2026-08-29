using System;
using System.IO;

namespace PeanutGallery.Core;

/// <summary>
/// Is a path actually inside a root directory? Lives in the core because it is pure decision
/// logic that a shell must not improvise: the file paths it guards come from a diff, which is
/// attacker-controlled input on any PR.
///
/// <para>The trap this exists to close is a plain prefix comparison. <c>full.StartsWith(root)</c>
/// has no separator boundary, so a sibling directory whose name merely begins with the root's
/// name - <c>/repos/demo-evil/secret</c> against a root of <c>/repos/demo</c> - passes it. On a CI
/// runner with predictable sibling checkouts and caches, that is enough to read a file outside the
/// repo and paste its contents into a model prompt.</para>
/// </summary>
public static class PathSafety
{
	/// <summary>
	/// True when <paramref name="fullPath"/> is <paramref name="root"/> itself or sits beneath it.
	/// Both are expected to be absolute, normalised paths (<see cref="Path.GetFullPath(string)"/>).
	/// Total: malformed input is refused, never thrown.
	/// </summary>
	public static bool IsInsideRoot(string? root, string? fullPath)
	{
		if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(fullPath))
		{
			return false;
		}

		try
		{
			var rel = Path.GetRelativePath(root, fullPath);

			// Rooted means GetRelativePath could not express one under the other at all
			// (a different drive or share on Windows) - so it is definitively outside.
			if (Path.IsPathRooted(rel))
			{
				return false;
			}

			// "." is the root itself. Anything that has to climb out starts with a ".." SEGMENT -
			// checked as a segment, not a prefix, so a legitimately-named file like "..config" is
			// not mistaken for an escape.
			return rel != ".."
				&& !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
				&& !rel.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
		}
		catch (ArgumentException)
		{
			return false; // invalid path characters
		}
	}
}
