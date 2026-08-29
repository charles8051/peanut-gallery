using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PeanutGallery.Core;

/// <summary>
/// Pure rendering of a <see cref="PersonaReview"/> into a Markdown PR comment. The
/// first line is a stable HTML marker (<c>&lt;!-- peanut-gallery:&lt;persona-id&gt; --&gt;</c>)
/// so the shell can find-and-update one persona's comment in place across pushes
/// instead of posting duplicates.
/// </summary>
public static class CommentRenderer
{
	public static string Marker(string personaId) => $"<!-- peanut-gallery:{personaId} -->";

	/// <summary>
	/// Model-authored text flattened to one line, for the places that interpolate it into a
	/// single-line construct like <c>- **&lt;title&gt;**</c>.
	///
	/// <para>A stray newline there does not merely look wrong: the bold span opens on one rendered
	/// line and closes on another, and whatever sat between them becomes a sibling element the
	/// reader cannot tell we did not write. CR is collapsed as well as LF - a reply carrying CRLF
	/// would otherwise leak a bare carriage return into the rendered bullet.</para>
	///
	/// <para>One definition, shared by every renderer. Three near-identical private copies is how
	/// two of them ended up handling LF only while the third handled both.</para>
	/// </summary>
	public static string OneLine(string? text) =>
		string.IsNullOrWhiteSpace(text)
			? string.Empty
			: text.Replace('\r', ' ').Replace('\n', ' ').Trim();

	public static string Render(PersonaReview review)
	{
		var sb = new StringBuilder();
		sb.Append(Marker(review.Persona.Id)).Append('\n');
		sb.Append("### ").Append(review.Persona.Name)
			.Append(" — `").Append(review.Persona.Model).Append("`\n\n");
		AppendFindings(sb, review.Findings);
		return sb.ToString();
	}

	/// <summary>Render the ordered finding list (or the empty-review note). Shared with the session renderer.</summary>
	internal static void AppendFindings(StringBuilder sb, IReadOnlyList<Finding> findings)
	{
		if (findings.Count == 0)
		{
			sb.Append("_No findings._\n");
			return;
		}

		// Highest severity first, then by file for stable ordering.
		var ordered = findings
			.OrderByDescending(f => f.Severity)
			.ThenBy(f => f.File, System.StringComparer.Ordinal)
			.ThenBy(f => f.Line);

		foreach (var f in ordered)
		{
			sb.Append("- ").Append(Badge(f.Severity)).Append(' ');
			if (!string.IsNullOrEmpty(f.File))
			{
				sb.Append('`').Append(f.File);
				if (f.Line > 0)
				{
					sb.Append(':').Append(f.Line);
				}

				sb.Append("` — ");
			}

			sb.Append("**").Append(f.Title).Append("**");
			if (!string.IsNullOrWhiteSpace(f.Body))
			{
				sb.Append("  \n  ").Append(f.Body.Replace("\n", "\n  "));
			}

			sb.Append('\n');
		}
	}

	/// <summary>Severity badge, shared with <see cref="PanelCommentRenderer"/> so both comment shapes read alike.</summary>
	internal static string Badge(Severity severity) => severity switch
	{
		Severity.Critical => "🔴 **critical**",
		Severity.Major => "🟠 **major**",
		Severity.Minor => "🟡 minor",
		_ => "⚪ info",
	};
}
