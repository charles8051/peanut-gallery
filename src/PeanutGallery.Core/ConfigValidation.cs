using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>A single configuration problem: which element, and what is wrong.</summary>
public sealed record ConfigProblem(string Scope, string Message);

/// <summary>
/// Pure structural validation of a <see cref="PeanutConfig"/>: every persona points
/// at a known provider, every assignment names an existing persona and repo, and
/// ids are unique. Secret presence (does <c>$OPENROUTER_API_KEY</c> actually
/// resolve?) is intentionally NOT checked here - that is environment state the
/// shell owns. Returns an empty list when the config is well-formed.
/// </summary>
public static class ConfigValidation
{
	public static IReadOnlyList<ConfigProblem> Validate(PeanutConfig config)
	{
		var problems = new List<ConfigProblem>();

		var providerNames = new HashSet<string>();
		foreach (var p in config.Providers)
		{
			if (!providerNames.Add(p.Name))
			{
				problems.Add(new ConfigProblem($"provider:{p.Name}", "duplicate provider name"));
			}
		}

		// A dynamic panel needs an orchestrator to build it and a provider that can reach it.
		// Catching this in validation matters because the runtime is deliberately total: without
		// a planner it silently falls back to the configured panel, so "auto mode did nothing"
		// would otherwise be invisible until someone noticed the personas never changed.
		var mode = config.Panel ?? PanelMode.Fixed;
		if (mode != PanelMode.Fixed)
		{
			if (config.Orchestrator is null)
			{
				problems.Add(new ConfigProblem(
					"panel",
					$"panel mode '{mode.ToString().ToLowerInvariant()}' needs an 'orchestrator' model to plan the panel"));
			}
			else if (!providerNames.Contains(config.Orchestrator.Provider))
			{
				problems.Add(new ConfigProblem(
					"orchestrator",
					$"references unknown provider '{config.Orchestrator.Provider}'"));
			}

			if (config.PersonaModel is not null && !providerNames.Contains(config.PersonaModel.Provider))
			{
				problems.Add(new ConfigProblem(
					"personaModel",
					$"references unknown provider '{config.PersonaModel.Provider}'"));
			}

			// Generated personas need a model to review with. Falling back to the orchestrator
			// would conflate two jobs - it plans lenses, it does not review - so say so instead.
			if (config.PersonaModel is null && config.Personas.Count == 0)
			{
				problems.Add(new ConfigProblem(
					"personaModel",
					"panel mode 'auto' with no configured personas needs a 'personaModel' for the generated reviewers to use"));
			}

			// seed+auto with no personas is just auto, and an operator who wrote seed+auto
			// probably meant to seed something.
			if (mode == PanelMode.SeedAndAuto && config.Personas.Count == 0)
			{
				problems.Add(new ConfigProblem(
					"panel", "panel mode 'seedAndAuto' has no personas to seed from (use 'auto' instead)"));
			}
		}

		// The explicit auto-persona temperature is a real temperature - hold it to the same
		// [0, 2] range as a seed persona's (#129), and unconditionally: a bad value is malformed
		// config whether or not auto mode is on today, exactly like the per-persona check below.
		// An explicit value is respected unfloored at review time, so a bad one must fail here.
		if (config.PersonaTemperature is < 0 or > 2)
		{
			problems.Add(new ConfigProblem(
				"personaTemperature",
				$"temperature {config.PersonaTemperature} is outside the sane range [0, 2]"));
		}

		// Auto-persona nucleus/top-k sampling (#133 follow-up): same ranges as the per-persona checks
		// below. top_p is a probability mass in (0, 1]; top_k is a positive candidate count.
		if (config.PersonaTopP is <= 0 or > 1)
		{
			problems.Add(new ConfigProblem(
				"personaTopP", $"top_p {config.PersonaTopP} is outside the sane range (0, 1]"));
		}

		if (config.PersonaTopK is < 1)
		{
			problems.Add(new ConfigProblem(
				"personaTopK", $"top_k {config.PersonaTopK} must be at least 1"));
		}

		// Conversation policy. Same reasoning as the panel checks above: the runtime is total, so a
		// reconcile mode it cannot honour degrades to the full fan-out - which looks like the
		// setting simply did nothing. Say so here instead of leaving an operator to infer it from a
		// token bill that never dropped.
		if (config.Conversation is { } conversation)
		{
			if (conversation.Mode == ConversationMode.Reconcile
				&& (config.Comment ?? CommentMode.PerPersona) != CommentMode.Panel)
			{
				problems.Add(new ConfigProblem(
					"conversation",
					"conversation mode 'reconcile' needs \"comment\": \"panel\" - one reconciler cannot "
					+ "re-render N per-persona comments from sessions it did not produce"));
			}

			if (conversation.Model is not null && !providerNames.Contains(conversation.Model.Provider))
			{
				problems.Add(new ConfigProblem(
					"conversation.model", $"references unknown provider '{conversation.Model.Provider}'"));
			}

			// A gate of blanks matches nothing and would silently mute the panel; an operator who
			// wanted no conversation has 'off' for that.
			if (conversation.MentionTokens.Count > 0
				&& conversation.MentionTokens.All(string.IsNullOrWhiteSpace))
			{
				problems.Add(new ConfigProblem(
					"conversation.mentions",
					"every mention token is blank, so no comment can ever address the panel "
					+ "(use conversation mode 'off' if that is the intent)"));
			}
		}

		var personaIds = new HashSet<string>();
		foreach (var persona in config.Personas)
		{
			if (!personaIds.Add(persona.Id))
			{
				problems.Add(new ConfigProblem($"persona:{persona.Id}", "duplicate persona id"));
			}

			if (!providerNames.Contains(persona.Model.Provider))
			{
				problems.Add(new ConfigProblem(
					$"persona:{persona.Id}",
					$"references unknown provider '{persona.Model.Provider}'"));
			}

			// Unset (null) is not a problem and must not be reported as one — it is the documented
			// "let the default stand" state, resolved by Persona.SamplingTemperature(), exactly like
			// top_p/top_k below. A relational pattern never matches null, so this reads as intended.
			// An authored 0 is likewise not a problem: greedy is a legitimate choice when it is
			// chosen. #127 was about the value nobody chose, not the one somebody did.
			if (persona.Temperature is < 0 or > 2)
			{
				problems.Add(new ConfigProblem(
					$"persona:{persona.Id}",
					$"temperature {persona.Temperature} is outside the sane range [0, 2]"));
			}

			if (persona.TopP is <= 0 or > 1)
			{
				problems.Add(new ConfigProblem(
					$"persona:{persona.Id}", $"top_p {persona.TopP} is outside the sane range (0, 1]"));
			}

			if (persona.TopK is < 1)
			{
				problems.Add(new ConfigProblem(
					$"persona:{persona.Id}", $"top_k {persona.TopK} must be at least 1"));
			}
		}

		var repoNames = new HashSet<string>();
		foreach (var r in config.Repos)
		{
			if (!repoNames.Add(r.Name))
			{
				problems.Add(new ConfigProblem($"repo:{r.Name}", "duplicate repo name"));
			}
		}

		foreach (var a in config.Assignments)
		{
			if (!personaIds.Contains(a.PersonaId))
			{
				problems.Add(new ConfigProblem(
					$"assignment:{a.PersonaId}->{a.RepoName}",
					$"unknown persona '{a.PersonaId}'"));
			}

			if (!repoNames.Contains(a.RepoName))
			{
				problems.Add(new ConfigProblem(
					$"assignment:{a.PersonaId}->{a.RepoName}",
					$"unknown repo '{a.RepoName}'"));
			}
		}

		return problems;
	}
}
