using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>
/// What survived confidence gating, and what did not. <see cref="Suppressed"/> exists so a
/// caller can disclose the drop: silently hiding findings is the same class of defect as
/// silently reporting a clean review (see <see cref="SessionUpdateResult"/>), and this repo
/// already discloses filtered-out diff files rather than pretending they were reviewed.
/// </summary>
public sealed record GateResult(IReadOnlyList<Finding> Kept, IReadOnlyList<Finding> Suppressed, double Threshold);

/// <summary>
/// Drops findings the reviewer itself is not confident enough about. A reviewer that posts
/// every hunch trains its readers to skim, so the cheapest precision win is to let the model
/// admit doubt and then act on that admission. Pure: a list in, a list out, no IO, no clock.
/// <para>This deliberately overlaps with the protocol's "reporting nothing is better than
/// padding the list with guesses" instruction, and the two are not redundant: the instruction
/// asks the model not to emit guesses at all, while the gate catches the findings it chose to
/// emit but honestly hedged. A well-behaved model makes this a no-op - that is the intended
/// steady state, not a sign the gate is doing nothing.</para>
/// </summary>
public static class ConfidenceGate
{
	/// <summary>Suppress below 0.6 unless configured otherwise - low enough to keep genuine hedged findings.</summary>
	public const double DefaultMinConfidence = 0.6;

	/// <summary>Persona override, else the config default, else <see cref="DefaultMinConfidence"/>.</summary>
	public static double ThresholdFor(Persona persona, PeanutConfig config) =>
		Clamp(persona.MinConfidence ?? config.MinConfidence ?? DefaultMinConfidence);

	public static GateResult Apply(IReadOnlyList<Finding> findings, double threshold)
	{
		var t = Clamp(threshold);

		// A zero threshold is "gate disabled" - keep the list identical rather than allocating.
		if (t <= 0 || findings.Count == 0)
		{
			return new GateResult(findings, [], t);
		}

		var kept = new List<Finding>(findings.Count);
		var suppressed = new List<Finding>();
		foreach (var f in findings)
		{
			(f.Confidence >= t ? kept : suppressed).Add(f);
		}

		return new GateResult(kept, suppressed, t);
	}

	/// <summary>Confidence is a probability; anything outside [0,1] is a malformed input, not a signal.</summary>
	public static double Clamp(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
}
