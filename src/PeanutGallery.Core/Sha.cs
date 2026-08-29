using System;

namespace PeanutGallery.Core;

/// <summary>Small shared helper for displaying commit SHAs — the 7-char short form used everywhere a SHA is shown.</summary>
public static class Sha
{
	public static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

	/// <summary>
	/// True when <paramref name="sha"/> has the shape of a git commit id: 7-40 hex characters.
	///
	/// <para>Checked because a SHA read back out of a PR comment is not necessarily a SHA. It is
	/// whatever the comment said, and it goes on to name a revision in a GitHub API path - so a
	/// value carrying a slash, a query, or a fragment is a different request, not a different
	/// commit. The URL is escaped at the client too; this is the half that keeps a well-formed
	/// but arbitrary ref from being followed at all.</para>
	/// </summary>
	public static bool IsCommitId(string? sha)
	{
		if (sha is null || sha.Length is < 7 or > 40)
		{
			return false;
		}

		foreach (var c in sha)
		{
			if (!char.IsAsciiHexDigit(c))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// True when two SHAs name the same commit, tolerating an abbreviated form on either side.
	/// A caller comparing a stored SHA against one it got from <c>git rev-parse --short</c> is
	/// comparing the same commit written two ways, and an ordinal equality would call that a
	/// mismatch. Requires at least 7 characters on both sides: shorter than that is not an
	/// abbreviation anyone uses, and a 4-character prefix would match unrelated commits.
	/// </summary>
	public static bool SameCommit(string? a, string? b)
	{
		if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
		{
			return false;
		}

		var length = Math.Min(a!.Length, b!.Length);
		return length >= 7
			&& a.AsSpan(0, length).Equals(b.AsSpan(0, length), StringComparison.OrdinalIgnoreCase);
	}
}
