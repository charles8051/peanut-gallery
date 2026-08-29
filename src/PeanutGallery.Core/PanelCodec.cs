using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PeanutGallery.Core;

/// <summary>
/// Persists a <see cref="PinnedPanel"/> inside a PR comment, exactly as
/// <see cref="SessionCodec"/> persists a session: base64-encoded JSON in a hidden HTML comment,
/// so the comment IS the datastore and the state dies with the PR. base64 keeps the <c>--&gt;</c>
/// delimiter safe whatever a persona's system prompt contains. Pure and reflection-free
/// (Utf8JsonWriter / JsonDocument), so the core stays AOT-clean.
///
/// <para>A distinct marker from the session blob, because the two have different lifetimes: the
/// session advances every turn, the panel is written once and then only read.</para>
/// </summary>
public static class PanelCodec
{
	private const string Open = "<!-- pg-panel:1:";
	private const string Close = " -->";

	/// <summary>True when a body already carries a pinned panel - cheaper than a full extract.</summary>
	public static bool IsPinned(string commentBody) =>
		commentBody.Contains(Open, StringComparison.Ordinal);

	public static string Embed(string visibleMarkdown, PinnedPanel panel) =>
		visibleMarkdown.TrimEnd() + "\n\n" + Open + ToBase64Json(panel) + Close;

	/// <summary>The pinned panel in this body, or null if there is none / it is unreadable.</summary>
	public static PinnedPanel? Extract(string commentBody)
	{
		var start = commentBody.LastIndexOf(Open, StringComparison.Ordinal);
		if (start < 0)
		{
			return null;
		}

		var payloadStart = start + Open.Length;
		var end = commentBody.IndexOf(Close, payloadStart, StringComparison.Ordinal);
		if (end < 0)
		{
			return null;
		}

		try
		{
			var json = Encoding.UTF8.GetString(Convert.FromBase64String(commentBody[payloadStart..end].Trim()));
			return FromJson(json);
		}
		catch (Exception e) when (e is FormatException or JsonException)
		{
			return null;
		}
	}

	private static string ToBase64Json(PinnedPanel panel)
	{
		var buffer = new System.Buffers.ArrayBufferWriter<byte>();
		using (var w = new Utf8JsonWriter(buffer))
		{
			w.WriteStartObject();
			w.WriteString("mode", panel.Mode.ToString().ToLowerInvariant());
			w.WriteString("sha", panel.PinnedAtSha);
			if (panel.OrchestratorModel is not null)
			{
				w.WriteString("by", panel.OrchestratorModel);
			}

			w.WriteStartArray("personas");
			foreach (var p in panel.Personas)
			{
				w.WriteStartObject();
				w.WriteString("id", p.Id);
				w.WriteString("name", p.Name);
				w.WriteString("lens", p.Lens);
				w.WriteString("tier", p.Tier.ToString().ToLowerInvariant());
				w.WriteString("provider", p.Model.Provider);
				w.WriteString("model", p.Model.ModelId);
				// The RESOLVED value, not the raw nullable: a pin is the record of what this PR's
				// panel actually ran at, frozen at pin time (#64), so it must not re-resolve to a
				// different default later. Unlike a config, a pin has no "unspecified" state to
				// preserve — every pinned persona has already been given a temperature.
				w.WriteNumber("temperature", p.SamplingTemperature());
				w.WriteString("prompt", p.SystemPrompt);
				// A convened persona's assignment lives here and NOT in "prompt" (#202). Omitted for
				// a seed persona, which has none. Dropping it would not fail loudly: the persona
				// would still review, from turn 2 on, with the doctrine and no subject - the same
				// shape of silent loss as the top_p/top_k pin bug below.
				if (p.Brief is { Length: > 0 } brief)
				{
					w.WriteString("brief", brief);
				}

				if (p.MinConfidence is { } mc)
				{
					w.WriteNumber("minConfidence", mc);
				}

				// Sampling knobs must survive the pin, or a panel generated with top_p/top_k reviews
				// WITHOUT them from turn 2 on (and on the review after pinning) — the bug that kept
				// minimax-m3's recommended sampling from ever reaching the model (#136 follow-up).
				if (p.TopP is { } tp)
				{
					w.WriteNumber("topP", tp);
				}

				if (p.TopK is { } tk)
				{
					w.WriteNumber("topK", tk);
				}

				w.WriteEndObject();
			}

			w.WriteEndArray();
			w.WriteEndObject();
		}

		return Convert.ToBase64String(buffer.WrittenSpan);
	}

	private static PinnedPanel? FromJson(string json)
	{
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;
		if (root.ValueKind != JsonValueKind.Object
			|| !root.TryGetProperty("personas", out var arr)
			|| arr.ValueKind != JsonValueKind.Array)
		{
			return null;
		}

		var personas = new List<Persona>();
		foreach (var el in arr.EnumerateArray())
		{
			if (el.ValueKind != JsonValueKind.Object)
			{
				continue;
			}

			var id = FindingsParser.GetString(el, "id");
			if (string.IsNullOrWhiteSpace(id))
			{
				continue; // a persona with no id cannot own a comment marker
			}

			var lens = FindingsParser.GetString(el, "lens") ?? string.Empty;
			var prompt = FindingsParser.GetString(el, "prompt") ?? string.Empty;
			var brief = FindingsParser.GetString(el, "brief");
			if (brief is null)
			{
				// A pin written before "brief" existed carries a convened persona's assignment inside
				// "prompt", which every later turn then sends as the SYSTEM message - so leaving it
				// there would keep ADR-0003's rule false for exactly the panels that predate it, on
				// every PR already open. Split it here instead. A seed persona comes back unchanged:
				// its prompt is the operator's and belongs where it is.
				(prompt, brief) = PanelComposition.MigrateLegacyPrompt(prompt, lens);
			}

			personas.Add(new Persona(
				id!,
				FindingsParser.GetString(el, "name") ?? id!,
				lens,
				ParseTier(FindingsParser.GetString(el, "tier")),
				new ModelRef(
					FindingsParser.GetString(el, "provider") ?? string.Empty,
					FindingsParser.GetString(el, "model") ?? string.Empty),
				// Absent stays absent. This codec used to answer "the safe non-greedy default" here
				// while ConfigCodec's non-nullable double answered 0 — two decoders, two answers, and
				// the config one was greedy (#127). Neither decides now: null flows through to
				// Persona.SamplingTemperature(), which owns the default for every path.
				GetDouble(el, "temperature"),
				prompt,
				GetDouble(el, "minConfidence"),
				GetDouble(el, "topP"),
				GetInt(el, "topK"),
				brief));
		}

		// A panel with no usable personas is not a panel - report "unpinned" so the caller
		// re-decides rather than silently reviewing with nobody.
		if (personas.Count == 0)
		{
			return null;
		}

		// A pin is text living in a PR comment, so it is only ever as trustworthy as the comment
		// it came from - and it bypasses PanelFence, which only ever saw the orchestrator's
		// output. Bound the cost here on READ. Tier is deliberately NOT clamped at this layer:
		// a seed persona in seedAndAuto may legitimately be agent-tier by an operator's choice,
		// and this codec cannot tell a configured persona from an invented one. The caller can
		// (see ReviewRunner.ResolvePanelAsync), so that is where tier is policed.
		if (personas.Count > PanelFence.MaxPersonas)
		{
			personas = personas.GetRange(0, PanelFence.MaxPersonas);
		}

		return new PinnedPanel(
			personas,
			ParseMode(FindingsParser.GetString(root, "mode")),
			FindingsParser.GetString(root, "sha") ?? string.Empty,
			FindingsParser.GetString(root, "by"));
	}

	private static PanelMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
	{
		"auto" => PanelMode.Auto,
		"seedandauto" => PanelMode.SeedAndAuto,
		_ => PanelMode.Fixed,
	};

	private static ReviewTier ParseTier(string? value) =>
		string.Equals(value?.Trim(), "agent", StringComparison.OrdinalIgnoreCase)
			? ReviewTier.Agent
			: ReviewTier.Diff;

	private static double? GetDouble(JsonElement el, string name) =>
		el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
			? d
			: null;

	private static int? GetInt(JsonElement el, string name) =>
		el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
			? i
			: null;
}
