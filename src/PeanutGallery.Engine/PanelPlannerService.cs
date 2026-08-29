using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;

namespace PeanutGallery.Engine;

/// <summary>
/// The orchestrator port: turn a change into the reviewers it needs. Async and IO-bearing, so it
/// lives in the shell (ADR-0001) - the persona list it returns is an immutable value the pure core
/// consumes exactly as if it had come from `peanut.json`.
/// </summary>
public interface IPanelPlanner
{
	/// <summary>
	/// Personas for this PR, or an empty list if no panel could be planned. Never throws for a
	/// model failure: the caller decides what an empty plan means (fall back to the seed, or the
	/// configured panel), and losing the review entirely is never the right answer.
	/// </summary>
	Task<IReadOnlyList<Persona>> PlanAsync(
		Diff diff, RepoConventions? conventions, IReadOnlyList<Persona> seed, CancellationToken ct = default);
}

/// <summary>
/// Model-backed <see cref="IPanelPlanner"/>. One call, then the pure pipeline:
/// <see cref="PanelPlanParser"/> reads it, <see cref="PanelFence"/> enforces the rules, and
/// <see cref="PanelComposition"/> applies the operator's decisions (model, temperature, and the
/// always-Diff tier that stops a diff-derived persona granting itself repo tools).
///
/// <para>Everything the orchestrator says is a proposal. Nothing it returns reaches a review
/// without passing the fence, which is why a hostile diff cannot talk its way onto the panel.</para>
/// </summary>
public sealed class ChatClientPanelPlanner(
	IReviewer reviewer,
	ModelRef orchestratorModel,
	ModelRef personaModel,
	double personaTemperature = 0.2,
	int cap = PanelFence.MaxPersonas,
	Action<string>? log = null,
	double? personaTopP = null,
	int? personaTopK = null) : IPanelPlanner
{
	public async Task<IReadOnlyList<Persona>> PlanAsync(
		Diff diff, RepoConventions? conventions, IReadOnlyList<Persona> seed, CancellationToken ct = default)
	{
		// A panel needs something to look at; an empty diff has no risks to anchor to.
		if (diff.Files.Count == 0)
		{
			return [];
		}

		// `cap` bounds the TOTAL panel, and in seedAndAuto the seed is already part of it. A seed
		// that fills the cap leaves nothing to convene, so planning would be a paid model call
		// whose every result the fence must then discard.
		var slots = PanelFence.AdditionalSlots(cap, seed.Count);
		if (slots == 0)
		{
			log?.Invoke($"[panel] the seed already fills the panel ({seed.Count} of {cap}); skipping the orchestrator");
			return [];
		}

		string text;
		try
		{
			var request = PanelPlanner.BuildRequest(orchestratorModel, diff, conventions, seed, cap);
			var reply = await reviewer.CompleteAsync(request, string.Empty, ct);
			text = reply.Text;
			// Logged rather than threaded through IPanelPlanner: the orchestrator runs at most once
			// per PR, so it belongs in the run log next to the plan it produced, not in the
			// per-persona-per-turn accounting the Job Summary table exists for.
			if (!reply.Usage.IsUnreported)
			{
				var cacheNote = reply.Usage.CachedInputTokens > 0 ? $" ({reply.Usage.CachedInputTokens} cached)" : "";
				log?.Invoke($"[panel] orchestrator used {reply.Usage.InputTokens} in / " +
					$"{reply.Usage.OutputTokens} out tokens{cacheNote}");
			}
		}
		catch (Exception e) when (e is not OperationCanceledException)
		{
			// Total at this seam: a failed plan degrades to "no panel", and the caller falls back.
			log?.Invoke($"[panel] orchestrator failed ({e.Message}); falling back");
			return [];
		}

		// Fenced against the ADDITIONAL slots, not the total cap, and against the seed's lenses:
		// both halves of "the orchestrator adds to the panel, it does not re-cover it".
		var fenced = PanelFence.Apply(
			PanelPlanParser.Parse(text), slots, seed.Select(p => p.Lens).ToList());
		foreach (var r in fenced.Rejected)
		{
			// Rejections are logged, never silent - a panel that quietly shrank is a panel nobody
			// can debug, and the reasons are how the meta-prompt gets tuned.
			log?.Invoke($"[panel] rejected '{r.Lens}': {r.Reason}");
		}

		var personas = PanelComposition.ToPersonas(fenced.Accepted, personaModel, personaTemperature, personaTopP, personaTopK);
		log?.Invoke(personas.Count == 0
			? "[panel] orchestrator proposed no usable reviewers; falling back"
			: $"[panel] convened {personas.Count} reviewer(s): {string.Join(", ", Lenses(personas))}");
		return personas;
	}

	private static IEnumerable<string> Lenses(IReadOnlyList<Persona> personas)
	{
		foreach (var p in personas)
		{
			yield return p.Lens;
		}
	}
}
