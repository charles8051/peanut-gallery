using System;
using System.IO;
using PeanutGallery.Core;

namespace PeanutGallery.Engine;

/// <summary>
/// Containment that survives symlinks.
///
/// <para><see cref="PathSafety.IsInsideRoot"/> answers the question purely, on strings - but
/// <see cref="Path.GetFullPath(string)"/> normalises <c>..</c> and separators WITHOUT resolving
/// reparse points. So a symlink committed inside the repo and pointing outside it satisfies a
/// string-only check and still reads the target: every path in sight looks contained, and the
/// bytes come from elsewhere. Resolving needs the filesystem, so it lives here in the shell and
/// hands the actual decision back to the pure core.</para>
/// </summary>
public static class FileSystemSafety
{
	/// <summary>Bound on the component walk - deep enough for any real tree, finite against a cycle.</summary>
	private const int MaxDepth = 64;

	/// <summary>
	/// True when <paramref name="candidate"/> is inside <paramref name="root"/> even after every
	/// link on its path is resolved.
	///
	/// <para>Every component is checked, not just the leaf: a link ANYWHERE on the path escapes.
	/// With <c>repo/sub -&gt; /etc</c>, reading <c>repo/sub/passwd</c> leaves the repo while the
	/// leaf itself is not a link at all.</para>
	///
	/// <para>Refuses on anything it cannot establish - an unreadable path, a cycle, a filesystem
	/// that will not answer. This guards untrusted input, so "could not tell" has to mean no.</para>
	/// </summary>
	public static bool ResolvesInsideRoot(string? root, string? candidate)
	{
		if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
		{
			return false;
		}

		try
		{
			var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
			var current = Path.GetFullPath(candidate);

			for (var depth = 0; depth < MaxDepth; depth++)
			{
				if (!PathSafety.IsInsideRoot(normalizedRoot, current))
				{
					return false;
				}

				// Reached the root without leaving it.
				if (string.Equals(Path.TrimEndingDirectorySeparator(current), normalizedRoot, StringComparison.Ordinal))
				{
					return true;
				}

				// One hop at a time, via LinkTarget rather than a fully-resolved target. LinkTarget
				// reads the reparse data itself, so it still identifies a BROKEN link - one whose
				// target does not exist - which File.Exists and Directory.Exists both report as
				// simply "not there". Treating that as "not a link" would let a broken link
				// pointing outside the root pass: harmless the instant you read it (the read
				// fails), but a lie from a containment guard, and true only until someone creates
				// the target.
				if (LinkTargetOf(current) is { } link)
				{
					// A link target may be relative, and it is relative to the LINK's directory.
					var resolved = Path.GetFullPath(link, Path.GetDirectoryName(current) ?? normalizedRoot);
					if (!PathSafety.IsInsideRoot(normalizedRoot, resolved))
					{
						return false;
					}

					// Follow it and keep checking: the target's own parents may leave too. The
					// depth bound is what stops a cycle here.
					current = resolved;
					continue;
				}

				var parent = Path.GetDirectoryName(current);
				if (string.IsNullOrEmpty(parent))
				{
					return false;
				}

				current = parent;
			}

			return false; // ran out of depth: refuse rather than guess
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException
			or ArgumentException or NotSupportedException)
		{
			return false;
		}
	}

	/// <summary>
	/// The immediate link target of <paramref name="path"/>, or null if it is not a link. A path
	/// can be a link as a file or as a directory and the two are separate APIs, so both are asked.
	/// </summary>
	private static string? LinkTargetOf(string path)
	{
		try
		{
			return new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException
			or ArgumentException or NotSupportedException)
		{
			return null;
		}
	}
}
