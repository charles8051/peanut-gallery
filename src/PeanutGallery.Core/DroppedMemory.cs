using System;
using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>
/// Remembers which findings a reviewer already had taken off the board, so it can be told not to
/// raise them again.
///
/// <para>Without this the drops are invisible to the model: the session carries the FULL finding
/// set forward (deliberately - it is the model's working state), so a finding the confidence gate
/// suppressed or the adversarial pass refuted comes back in the next turn's "currently open" list,
/// gets re-emitted, and gets dropped again. Every push pays for the same conclusion.</para>
///
/// <para>Bounded and self-clearing: only the most recent <see cref="MaxRemembered"/> are kept, and
/// a title that shows up in the posted set again is forgotten - if a finding earned its way back
/// after the code changed, it is no longer "dropped" and the model should not be discouraged from
/// raising it. Pure: lists in, list out.</para>
/// </summary>
public static class DroppedMemory
{
	/// <summary>Cap on remembered titles - enough to stop the loop, bounded so the prompt cannot grow without limit.</summary>
	public const int MaxRemembered = 20;

	/// <summary>
	/// The next turn's dropped-title memory: what was just dropped, then what was already
	/// remembered, minus anything that survived into <paramref name="posted"/>.
	/// </summary>
	public static IReadOnlyList<string> Next(
		IReadOnlyList<string> prior, IReadOnlyList<string> newlyDropped, IReadOnlyList<Finding> posted)
	{
		var survived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var f in posted)
		{
			survived.Add(f.Title.Trim());
		}

		var kept = new List<string>(MaxRemembered);
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// Newest first, so the cap evicts the stalest memory rather than the freshest.
		foreach (var title in Concat(newlyDropped, prior))
		{
			var key = title.Trim();
			if (key.Length == 0 || survived.Contains(key) || !seen.Add(key))
			{
				continue;
			}

			kept.Add(key);
			if (kept.Count == MaxRemembered)
			{
				break;
			}
		}

		return kept;
	}

	/// <summary>
	/// The other half of the same fact: which of a session's open findings are still ON the board.
	///
	/// <para><see cref="Next"/> answers "what came off"; this answers "what is left", and a caller
	/// re-rendering a persona's standing review from its session needs the latter. The session
	/// deliberately keeps the model's FULL working set - including everything the confidence gate
	/// suppressed and the adversarial pass refuted - so replaying <see cref="ReviewSession.OpenFindings"/>
	/// verbatim would resurface exactly the findings the pipeline decided not to show, undoing both
	/// filters silently.</para>
	///
	/// <para>Pure: findings + titles in, findings out.</para>
	/// </summary>
	public static IReadOnlyList<Finding> Standing(IReadOnlyList<Finding> open, IReadOnlyList<string> dropped)
	{
		if (open.Count == 0 || dropped.Count == 0)
		{
			return open;
		}

		var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var title in dropped)
		{
			off.Add(title.Trim());
		}

		var kept = new List<Finding>(open.Count);
		foreach (var f in open)
		{
			if (!off.Contains(f.Title.Trim()))
			{
				kept.Add(f);
			}
		}

		return kept;
	}

	/// <summary>The still-standing findings of a whole session — <see cref="Standing"/> over its own state.</summary>
	public static IReadOnlyList<Finding> Standing(ReviewSession session) =>
		Standing(session.OpenFindings, session.DroppedTitles);

	private static IEnumerable<string> Concat(IReadOnlyList<string> first, IReadOnlyList<string> second)
	{
		foreach (var s in first)
		{
			yield return s;
		}

		foreach (var s in second)
		{
			yield return s;
		}
	}
}
