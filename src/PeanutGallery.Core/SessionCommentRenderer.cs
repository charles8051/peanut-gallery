using System.Globalization;
using System.Text;

namespace PeanutGallery.Core;

/// <summary>
/// Renders the visible part of a stateful reviewer's living comment: the persona
/// header, a "reviewed through &lt;sha&gt; · turn N" line, the current open findings,
/// and — from turn 2 on — what was resolved since the last push. The hidden session
/// blob is appended separately by <see cref="SessionCodec.Embed"/>.
/// </summary>
public static class SessionCommentRenderer
{
	/// <param name="gate">Outcome of confidence gating, when it ran. The renderer owns this
	/// record end to end - it shows <see cref="GateResult.Kept"/> and discloses the rest - so
	/// callers pass the full update and let the gate decide what is visible. Suppressing
	/// findings without saying so is the same silent-omission defect the diff filter and the
	/// unreadable-reply guard both refuse to commit. Null = no gating ran; show everything.</param>
	/// <param name="verification">Outcome of the adversarial pass, when it ran. Like
	/// <paramref name="gate"/>, its drops are disclosed rather than quietly shrinking the review.</param>
	public static string Render(
		Persona persona, ReviewSession prior, SessionUpdate update, string headSha,
		GateResult? gate = null, VerificationResult? verification = null)
	{
		var sb = new StringBuilder();
		sb.Append(CommentRenderer.Marker(persona.Id)).Append('\n');
		sb.Append("### ").Append(persona.Name).Append(" — `").Append(persona.Model).Append("`\n");
		sb.Append("_Reviewed through `").Append(Sha.Short(headSha))
			.Append("` · turn ").Append(prior.Turn + 1).Append("_\n\n");

		CommentRenderer.AppendFindings(sb, verification?.Upheld ?? gate?.Kept ?? update.Findings);

		// With the grounds, not just the titles: a reader who disagrees can only push back on a drop
		// they can see the reasoning for, and this pass has been wrong before. Shared with the panel
		// renderer so the two cannot drift in how they flatten model-authored text.
		if (verification is { Refuted.Count: > 0 } v)
		{
			PanelCommentRenderer.AppendRefutations(sb, v.Refuted);
		}

		if (gate is { Suppressed.Count: > 0 } g)
		{
			var plural = g.Suppressed.Count == 1 ? "finding" : "findings";
			var verb = g.Suppressed.Count == 1 ? "was" : "were";
			sb.Append("\n_").Append(g.Suppressed.Count).Append(" low-confidence ").Append(plural)
				.Append(" (below ").Append(g.Threshold.ToString("0.0#", CultureInfo.InvariantCulture))
				.Append(") ").Append(verb).Append(" suppressed._\n");
		}

		if (!prior.IsFirstTurn && update.Resolved.Count > 0)
		{
			sb.Append("\n**Resolved since last push:** ")
				.Append(string.Join("; ", update.Resolved)).Append('\n');
		}

		if (update.Withdrawn.Count > 0)
		{
			sb.Append("\n**Withdrawn (author-explained):** ")
				.Append(string.Join("; ", update.Withdrawn)).Append('\n');
		}

		return sb.ToString();
	}

	/// <summary>A failure comment that does NOT advance the session (the caller re-embeds prior state).</summary>
	public static string RenderFailure(Persona persona, ReviewSession prior, string headSha, string message)
	{
		var sb = new StringBuilder();
		sb.Append(CommentRenderer.Marker(persona.Id)).Append('\n');
		sb.Append("### ").Append(persona.Name).Append(" — `").Append(persona.Model).Append("`\n");
		sb.Append("_Review could not run through `").Append(Sha.Short(headSha)).Append("`_\n\n");
		sb.Append("- 🟠 **major** **review could not run**  \n  ").Append(message).Append('\n');
		if (!prior.IsFirstTurn)
		{
			sb.Append("\n_(Prior review state preserved; will retry on the next push.)_\n");
		}

		return sb.ToString();
	}
}
