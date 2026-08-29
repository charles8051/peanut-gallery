using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>
/// How a persona consumes the change under review.
/// <para><see cref="Diff"/> personas (architect, bug-hunter) see the diff plus a
/// little surrounding context in a single model call - no tools, fast and cheap.</para>
/// <para><see cref="Agent"/> personas (the divergent contrarian, whole-codebase
/// architecture) need to explore the repo with read-only tools and so run an
/// agentic tool loop in the shell. The core only records the intent; the shell
/// decides how to honour it.</para>
/// </summary>
public enum ReviewTier
{
	Diff,
	Agent,
}

/// <summary>
/// A provider/model pair, e.g. <c>openrouter:anthropic/claude-opus-4.1</c> or
/// <c>fireworks:accounts/fireworks/models/deepseek-v3</c>. <see cref="Provider"/>
/// is the <see cref="ProviderConfig.Name"/> this model is reached through.
/// </summary>
public sealed record ModelRef(string Provider, string ModelId)
{
	public override string ToString() => $"{Provider}:{ModelId}";
}

/// <summary>
/// The model the tool picks when the user has not picked one: the built-in panel, the
/// <c>init</c> scaffold, and the action's bundled default config all name this rather than each
/// carrying its own. One constant so that changing the default is one edit and the three
/// zero-config surfaces cannot drift apart on which model a first-time user actually gets.
/// </summary>
public static class KnownModels
{
	/// <summary>The default model id, reached through <see cref="KnownProviders.OpenRouter"/>.</summary>
	public const string DefaultModelId = "openai/gpt-5.6-luna";

	/// <summary>The default provider/model pair.</summary>
	public static ModelRef Default { get; } = new(KnownProviders.OpenRouter, DefaultModelId);
}

/// <summary>
/// A reviewer persona: a named lens, a model, and a system prompt. Immutable
/// value - the same persona produces the same review request for the same diff.
/// </summary>
/// <param name="Id">Stable slug used as the PR-comment marker and assignment key.</param>
/// <param name="Name">Human-facing display name.</param>
/// <param name="Lens">Free-form lens tag - <c>architecture</c>, <c>bug-hunter</c>,
/// <c>contrarian</c>, or anything an operator coins. Advisory metadata only.</param>
/// <param name="Tier">Whether this persona is diff-scoped or agentic.</param>
/// <param name="Model">The provider/model this persona reviews with.</param>
/// <param name="Temperature">Sampling temperature (0 = greedy, higher = divergent). <b>Null = not
/// specified</b>, resolved by <see cref="SamplingTemperature()"/> to the recommended default — the same
/// "null = the default stands" contract <see cref="TopP"/> and <see cref="TopK"/> already keep three
/// lines below. It has to be nullable: a non-nullable <c>double</c> cannot tell an omitted JSON key
/// from a deliberate <c>0</c>, so a reflection-deserialized config that simply left the knob out
/// decoded to <c>default(double)</c> = 0 — greedy decoding, the reasoning-runaway mode (#127). An
/// <em>authored</em> 0 remains a legitimate operator choice and is honoured as-is.</param>
/// <param name="SystemPrompt">The persona's instructions to the model.</param>
/// <param name="MinConfidence">Optional per-persona confidence floor, overriding the config
/// default. Useful for a deliberately speculative lens (a contrarian earns a lower bar) or a
/// noisy model (raise it). Null = inherit. See <see cref="ConfidenceGate"/>.</param>
/// <param name="TopP">Optional nucleus-sampling <c>top_p</c> (probability mass, (0, 1]). Null = omit
/// from the request, so the provider default stands. Paired with <see cref="Temperature"/> to match a
/// model's recommended sampling (e.g. minimax-m3: temperature 1.0, top_p 0.95, top_k 40).</param>
/// <param name="TopK">Optional <c>top_k</c> (candidate count, ≥ 1). Null = omit / provider default.</param>
/// <param name="Brief">What this persona was convened to look at, when a model wrote it rather
/// than an operator. Null for every configured persona; set by <see cref="PanelComposition"/> for
/// the personas an orchestrator invents from the diff.
/// <para><b>Separate from <see cref="SystemPrompt"/> on purpose, and that is the whole point of
/// the field.</b> An orchestrator reads the pull request before it writes this, so on any PR the
/// text is attacker-influenced. <see cref="SystemPrompt"/> is the operator's channel and carries
/// the doctrine; a brief belongs in the user turn with the diff, which is where both composers put
/// it (see <see cref="PersonaPrompt.BriefMessage"/>). Delimiting it inside the system message was
/// the earlier answer and was only ever mitigation — untrusted prose in the privileged channel
/// stays indistinguishable from the instructions beside it however it is labelled (#201, #202).
/// The prompt-layer twin of pinning <see cref="ReviewTier.Diff"/> in
/// <see cref="PanelComposition.ToPersonas"/>: an invented persona gets no repo tools, and no
/// operator voice.</para></param>
public sealed record Persona(
	string Id,
	string Name,
	string Lens,
	ReviewTier Tier,
	ModelRef Model,
	double? Temperature,
	string SystemPrompt,
	double? MinConfidence = null,
	double? TopP = null,
	int? TopK = null,
	string? Brief = null)
{
	/// <summary>
	/// The temperature this persona actually samples at: its own if one was authored — including a
	/// deliberate <c>0</c> — else <see cref="PanelFence.DefaultTemperature"/>.
	///
	/// <para><b>This is the single resolution point.</b> Every consumer calls this rather than reading
	/// <see cref="Temperature"/>: prompt assembly, the session planner, both codecs, and the shells'
	/// displays. #127 was precisely two decode paths disagreeing about what an absent temperature
	/// means — <c>PanelCodec</c> answered "the recommended default", <c>ConfigCodec</c> answered
	/// <c>default(double)</c> = 0 — so the answer is owned here, once, where a third codec cannot
	/// invent a third one.</para>
	///
	/// <para>A METHOD, deliberately, not a property. <c>ConfigCodec</c> serializes this record by
	/// reflection over its public properties, and it writes <c>peanut.json</c> and the desktop's
	/// persona-library files to disk — so a computed property here would have emitted a derived
	/// <c>samplingTemperature</c> key into every file a user hand-edits, ignored on read, and would
	/// have re-materialised the default this fix exists to keep out of them. A method has nothing for
	/// a serializer to find, which beats teaching the core about a shell's JSON shape.</para>
	/// </summary>
	public double SamplingTemperature() => Temperature ?? PanelFence.DefaultTemperature;

	/// <summary>
	/// A one-line notice naming the personas that left <see cref="Temperature"/> unset, for a shell
	/// to print where it logs its other config decisions; null when every persona authored one.
	/// A safe fallback is not enough on its own — half of #127's complaint was that <em>nothing said</em>
	/// which value a config was sampling at. Pure: this builds the sentence, the shell decides where
	/// it goes.
	/// </summary>
	public static string? UnsetTemperatureNotice(IReadOnlyList<Persona> personas)
	{
		var unset = new List<string>();
		foreach (var persona in personas)
		{
			if (persona.Temperature is null)
			{
				unset.Add(persona.Id);
			}
		}

		return unset.Count == 0
			? null
			: $"temperature unset for persona(s) {string.Join(", ", unset)}; "
				+ $"sampling at the default {PanelFence.DefaultTemperature}";
	}
}
