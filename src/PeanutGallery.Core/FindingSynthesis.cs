using System;
using System.Collections.Generic;
using System.Text;

namespace PeanutGallery.Core;

/// <summary>One persona's contribution to the panel's findings.</summary>
public sealed record PersonaFindings(string PersonaId, string Lens, IReadOnlyList<Finding> Findings);

/// <summary>
/// A finding plus who raised it. <see cref="Lenses"/> holds every persona that reported it, so a
/// duplicate collapses into one entry that still names both voices - dedup the issue, keep the
/// attribution. Losing the lens would strip the why-this-reviewer-cares signal that is the entire
/// point of running a panel rather than one reviewer.
/// </summary>
public sealed record AttributedFinding(Finding Finding, IReadOnlyList<string> Lenses);

/// <summary>What the panel is saying, once merged.</summary>
/// <param name="Merged">How many duplicate reports were collapsed - disclosed, not hidden.</param>
public sealed record SynthesisResult(IReadOnlyList<AttributedFinding> Findings, int Merged);

/// <summary>
/// Merges N personas' findings into the one set a reader sees.
///
/// <para>Deduplication here is deliberately CONSERVATIVE and deterministic: two findings collapse
/// only when they name the same file, the same line, and the same title once punctuation and case
/// are normalised away. It does not attempt semantic matching.</para>
///
/// <para>That is the whole design decision. A model-driven reducer could catch "null deref in the
/// parser" and "parser crashes on empty input" as one issue - and it could equally merge two
/// genuinely distinct bugs and silently delete one. Over-merge is invisible: the reader cannot
/// tell that a finding was removed, which is the same class of defect as reporting a clean review
/// (see <see cref="SessionUpdateResult"/>). An under-merge is merely two similar bullets, which a
/// reader can see and judge. Given that asymmetry, the deterministic version ships first and
/// semantic dedup stays a follow-up with a model call and an evaluation behind it.</para>
///
/// <para>The looser rule a reader still wants - several lenses circling one root cause a few lines
/// apart, which this fold leaves as N separate bullets - is answered inside
/// <see cref="PanelCommentRenderer"/> instead, as grouping for display, and is private to it. It
/// can afford to guess where this cannot: it deletes nothing, so a wrong group is a heading a
/// reader discounts rather than a finding they never see. Do not import it back into this
/// fold (#169).</para>
///
/// <para>Pure: findings in, findings out.</para>
/// </summary>
public static class FindingSynthesis
{
	public static SynthesisResult Merge(IReadOnlyList<PersonaFindings> contributions)
	{
		var order = new List<string>();
		var groups = new Dictionary<string, (Finding Best, List<string> Lenses)>(StringComparer.Ordinal);

		foreach (var c in contributions)
		{
			foreach (var f in c.Findings)
			{
				var key = KeyOf(f);
				if (!groups.TryGetValue(key, out var group))
				{
					order.Add(key);
					groups[key] = (f, [c.Lens]);
					continue;
				}

				if (!group.Lenses.Contains(c.Lens, StringComparer.OrdinalIgnoreCase))
				{
					group.Lenses.Add(c.Lens);
				}

				// Keep the more alarming report of the same issue: a reader should see the worst
				// case anyone made, not whichever persona happened to be enumerated first.
				groups[key] = (Better(group.Best, f), group.Lenses);
			}
		}

		var merged = new List<AttributedFinding>(order.Count);
		var collapsed = 0;
		foreach (var key in order)
		{
			var (best, lenses) = groups[key];
			collapsed += lenses.Count - 1;
			merged.Add(new AttributedFinding(best, lenses));
		}

		return new SynthesisResult(merged, collapsed);
	}

	private static Finding Better(Finding a, Finding b)
	{
		if (a.Severity != b.Severity)
		{
			return a.Severity > b.Severity ? a : b;
		}

		if (Math.Abs(a.Confidence - b.Confidence) > 0.0001)
		{
			return a.Confidence > b.Confidence ? a : b;
		}

		// Same severity and confidence: prefer the one that explains itself.
		return b.Body.Length > a.Body.Length ? b : a;
	}

	/// <summary>
	/// File + line + normalised title. Titles are compared with case and punctuation stripped
	/// because two reviewers describing one issue rarely punctuate it identically - but nothing
	/// looser than that, so distinct issues are never fused.
	/// </summary>
	private static string KeyOf(Finding f) =>
		$"{f.File.ToLowerInvariant()}|{f.Line}|{NormalizeTitle(f.Title)}";

	private static string NormalizeTitle(string title)
	{
		var sb = new StringBuilder(title.Length);
		var pendingSpace = false;
		foreach (var ch in title)
		{
			if (char.IsLetterOrDigit(ch))
			{
				if (pendingSpace && sb.Length > 0)
				{
					sb.Append(' ');
				}

				pendingSpace = false;
				sb.Append(char.ToLowerInvariant(ch));
			}
			else
			{
				pendingSpace = true;
			}
		}

		return sb.ToString();
	}
}
