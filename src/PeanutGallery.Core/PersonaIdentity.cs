using System;
using System.Collections.Generic;
using System.Text;

namespace PeanutGallery.Core;

/// <summary>
/// Derives a persona's stable id from its lens.
///
/// <para>An orchestrator-invented persona still has to own a PR comment, and that comment is
/// found by an id-derived marker. Deriving the id from the lens - rather than, say, a counter or
/// a random value - means the same lens always keys to the same marker, so a panel can be
/// reconstructed (or a lost pin re-derived) without orphaning the comment it already owns.</para>
///
/// <para>Pure and total: any input yields a usable slug, and one that is already a slug is
/// returned unchanged.</para>
/// </summary>
public static class PersonaIdentity
{
	/// <summary>Bound on a derived id - long enough to stay readable, short enough for a marker.</summary>
	public const int MaxLength = 48;

	private const string Fallback = "reviewer";

	/// <summary>
	/// Lowercase, alphanumeric-and-hyphen slug of <paramref name="lens"/>: runs of anything else
	/// collapse to a single hyphen, and leading/trailing hyphens are trimmed.
	/// </summary>
	public static string FromLens(string? lens)
	{
		if (string.IsNullOrWhiteSpace(lens))
		{
			return Fallback;
		}

		var sb = new StringBuilder(lens.Length);
		var pendingHyphen = false;
		foreach (var ch in lens)
		{
			if (char.IsAsciiLetterOrDigit(ch))
			{
				// Only emit a separator once we know a real character follows it.
				if (pendingHyphen && sb.Length > 0)
				{
					sb.Append('-');
				}

				pendingHyphen = false;
				sb.Append(char.ToLowerInvariant(ch));
				if (sb.Length == MaxLength)
				{
					break;
				}
			}
			else
			{
				pendingHyphen = true;
			}
		}

		var slug = sb.ToString().Trim('-');
		return slug.Length == 0 ? Fallback : slug;
	}

	/// <summary>
	/// <paramref name="candidate"/> if free, else the first <c>candidate-2</c>, <c>candidate-3</c>…
	/// that is not in <paramref name="taken"/>. Two personas sharing an id would fight over one
	/// comment, so a collision has to be resolved before the panel is pinned, not after.
	/// </summary>
	public static string MakeUnique(IReadOnlyCollection<string> taken, string candidate)
	{
		var seen = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
		if (!seen.Contains(candidate))
		{
			return candidate;
		}

		for (var n = 2; ; n++)
		{
			var suffixed = $"{candidate}-{n}";
			if (!seen.Contains(suffixed))
			{
				return suffixed;
			}
		}
	}
}
