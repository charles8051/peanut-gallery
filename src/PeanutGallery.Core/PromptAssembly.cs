using System.Collections.Generic;
using System.Text;

namespace PeanutGallery.Core;

/// <summary>
/// Pure assembly of a persona + repo + diff into a <see cref="ReviewRequest"/>.
/// The model is asked to return findings as a small JSON object so the shell can
/// deserialize them (matching Microsoft.Extensions.AI's <c>GetResponseAsync&lt;T&gt;</c>
/// typed-output path) rather than scrape prose.
///
/// <para>Message order matches <see cref="SessionPlanner"/> and for the same reason: the
/// persona-independent block first, the persona's system message last, so a run's personas share
/// one cacheable prompt prefix. See the <see cref="SessionPlanner"/> type doc for the measurement
/// behind it.</para>
/// </summary>
public static class PromptAssembly
{
	public static ReviewRequest Build(
		Persona persona, RepoTarget repo, Diff diff, RepoConventions? conventions = null)
	{
		var system = new Message(ChatRole.System, BuildSystemPrompt(persona));
		// Conventions ride the USER turn here for the same reason they do in SessionPlanner:
		// repo-derived text must never sit in the system message. Shared block, no drift.
		var user = new Message(
			ChatRole.User,
			BuildUserPrompt(repo, diff) + (conventions?.PromptBlock() ?? string.Empty));
		// A convened persona's brief is model-written and diff-derived, so it obeys that same rule:
		// user turn, after the shared block, before the system message. Null for a seed persona,
		// which is why the message list is still exactly [user, system] for one.
		var brief = PersonaPrompt.BriefMessage(persona);
		Message[] messages = brief is null ? [user, system] : [user, brief, system];
		return new ReviewRequest(persona.Model, persona.SamplingTemperature(), persona.Tier, messages, persona.TopP, persona.TopK);
	}

	// The agent-tier tool note lives here rather than in the user block (where it used to sit)
	// because it varies by persona: one persona-dependent sentence early in an otherwise shared
	// block would split the prefix cache into one group per tier for no benefit. It is tool
	// instruction, not repo-derived text, so the system message is the right home for it.
	internal static string BuildSystemPrompt(Persona persona)
	{
		// This composer builds only its own protocol block; PersonaPrompt puts the persona and the
		// doctrine in front of it. The clause used to be appended here and so reached only this
		// path, leaving the PR path (SessionPlanner) without it. See PersonaPrompt.
		var protocol = new StringBuilder();
		if (persona.Tier == ReviewTier.Agent)
		{
			protocol.Append("\n\nYou have read-only tools to explore the repository beyond the diff ")
				.Append("(read_file, grep, glob). Use them to ground your review in how the ")
				.Append("change fits the surrounding code.");
		}

		return PersonaPrompt.Compose(persona, protocol.ToString());
	}

	// Takes no Persona, deliberately — the compiler then guarantees the block is byte-identical
	// across a run's personas, which is what the prefix cache is keyed on.
	internal static string BuildUserPrompt(RepoTarget repo, Diff diff)
	{
		var sb = new StringBuilder();
		sb.Append("You are reviewing a change to the repository '").Append(repo.Name).Append("'.\n\n");

		if (diff.Files.Count > 0)
		{
			sb.Append("Files changed:\n");
			foreach (var f in diff.Files)
			{
				sb.Append("  - ").Append(f.Path)
					.Append(" (+").Append(f.AddedLines)
					.Append(" / -").Append(f.RemovedLines).Append(")\n");
			}

			sb.Append('\n');
		}

		sb.Append("Return findings as JSON of the shape ")
			.Append("{\"findings\":[{\"severity\":\"info|minor|major|critical\",")
			.Append("\"file\":\"path\",\"line\":0,\"title\":\"...\",\"body\":\"...\"}]}. ")
			.Append("Report only what your lens cares about; an empty list is a valid review.\n\n");

		sb.Append("Unified diff:\n\n").Append(diff.Raw);
		return sb.ToString();
	}
}
