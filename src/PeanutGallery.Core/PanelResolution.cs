using System;
using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>Where the panel for this turn came from.</summary>
public enum PanelSource
{
	/// <summary>The committed config, re-read this turn (fixed mode).</summary>
	Configured,

	/// <summary>The panel frozen at PR-open, reused unchanged.</summary>
	Pinned,

	/// <summary>Nothing pinned yet in a dynamic mode: the caller must orchestrate, then pin.</summary>
	NeedsGeneration,
}

/// <summary>The panel to review with, and whether the caller still owes a pin.</summary>
/// <param name="Panel">Empty when <see cref="Source"/> is <see cref="PanelSource.NeedsGeneration"/>.</param>
public sealed record PanelDecision(PanelSource Source, IReadOnlyList<Persona> Panel);

/// <summary>
/// Decides which panel reviews this turn. The whole of auto mode's correctness lives here: a
/// dynamic panel must be generated at most ONCE per PR, and every later turn must get exactly the
/// personas that already own comments (auto-panel ADR, Decision 2).
///
/// <para>Fixed mode deliberately ignores pinning. There the committed config is the source of
/// truth and an operator editing it mid-PR should see the change take effect - pinning would turn
/// a config edit into a silent no-op, which is the opposite of what a fixed panel is for.</para>
///
/// <para>Pure: values in, a decision out. Orchestrating and writing the pin are shell work.</para>
/// </summary>
public static class PanelResolution
{
	public static PanelDecision Resolve(
		PanelMode mode, PinnedPanel? pinned, IReadOnlyList<Persona> configured)
	{
		if (mode == PanelMode.Fixed)
		{
			return new PanelDecision(PanelSource.Configured, configured);
		}

		// A pin with no personas is not a pin; Extract already refuses those, and this is the
		// belt to that braces - reviewing with nobody would look like a clean review.
		if (pinned is { Personas.Count: > 0 })
		{
			return new PanelDecision(PanelSource.Pinned, pinned.Personas);
		}

		return new PanelDecision(PanelSource.NeedsGeneration, []);
	}

	/// <summary>
	/// The panel a dynamic mode should pin: the seed (empty in <see cref="PanelMode.Auto"/>) plus
	/// the generated personas, de-duplicated by id with the SEED winning. A curated persona is
	/// hand-tuned house knowledge; an orchestrator that proposes the same id should not be able to
	/// quietly replace it. Ids are made unique rather than dropped, so a generated persona with a
	/// colliding lens still gets to review under its own marker.
	///
	/// <para>Generated personas are bounded by <see cref="PanelFence.AdditionalSlots"/> here as
	/// well as at the fence. Belt and braces on purpose: this is the value that gets PINNED, and
	/// <see cref="PanelCodec.Extract"/> clamps on read - so a merged panel over the cap would
	/// review at full size on the turn that planned it and silently shrink on every turn after,
	/// orphaning the comments its dropped members already own.</para>
	///
	/// <para>The seed is never truncated. A cap is a bound on what an orchestrator may INVENT; an
	/// operator who configures more personas than the cap has said what they want, and dropping
	/// one of those on their behalf would be a config edit nobody asked for.</para>
	/// </summary>
	public static IReadOnlyList<Persona> Merge(
		IReadOnlyList<Persona> seed, IReadOnlyList<Persona> generated, int cap = PanelFence.MaxPersonas)
	{
		var merged = new List<Persona>(seed.Count + generated.Count);
		var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var p in seed)
		{
			if (taken.Add(p.Id))
			{
				merged.Add(p);
			}
		}

		var slots = PanelFence.AdditionalSlots(cap, merged.Count);
		foreach (var p in generated)
		{
			if (slots == 0)
			{
				break;
			}

			slots--;

			var id = PersonaIdentity.MakeUnique(taken, string.IsNullOrWhiteSpace(p.Id)
				? PersonaIdentity.FromLens(p.Lens)
				: p.Id);
			taken.Add(id);
			merged.Add(p.Id == id ? p : p with { Id = id });
		}

		return merged;
	}
}
