using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PeanutGallery.Core;

/// <summary>Every persona's review session, carried together because they now share one comment.</summary>
public sealed record PanelSession(IReadOnlyDictionary<string, ReviewSession> ByPersona)
{
	public static PanelSession Empty { get; } =
		new(new Dictionary<string, ReviewSession>(StringComparer.OrdinalIgnoreCase));

	/// <summary>
	/// This persona's session, or null if this blob does not carry one. Callers deciding whether
	/// to fall back to another source MUST use this rather than <see cref="For"/>: the latter
	/// manufactures a fresh session for an unknown persona, which silently satisfies a
	/// null-coalescing fallback and drops the history the fallback existed to find.
	/// </summary>
	public ReviewSession? Find(string personaId) =>
		ByPersona.TryGetValue(personaId, out var s) ? s : null;

	/// <summary>This persona's session, or a fresh one - an unknown persona is simply on turn zero.</summary>
	public ReviewSession For(string personaId) => Find(personaId) ?? ReviewSession.Initial;
}

/// <summary>
/// Persists every persona's session inside the single panel comment.
///
/// <para>The per-persona design put each session in that persona's own comment, which worked
/// because the comment and the session had the same owner. Collapsing to one comment breaks that:
/// the sessions still need per-persona identity, but there is now only one place to keep them. So
/// they travel together, keyed by persona id, in one blob under a distinct marker.</para>
///
/// <para>Same mechanics as <see cref="SessionCodec"/> - base64 JSON in a hidden HTML comment,
/// reflection-free, total - and it reuses that codec's session shape rather than restating it, so
/// the two cannot drift into silently losing state.</para>
/// </summary>
public static class PanelSessionCodec
{
	private const string Open = "<!-- pg-panel-state:1:";
	private const string Close = " -->";

	public static string Embed(string visibleMarkdown, PanelSession session) =>
		visibleMarkdown.TrimEnd() + "\n\n" + Open + ToBase64Json(session) + Close;

	/// <summary>
	/// The comment without its state blob — what a reader should be shown. A consumer that prints
	/// the panel for a human (or for an agent about to act on it) would otherwise hand them a
	/// screen of base64 that carries nothing they can read.
	/// </summary>
	public static string Visible(string commentBody)
	{
		var start = commentBody.LastIndexOf(Open, StringComparison.Ordinal);
		return start < 0 ? commentBody.TrimEnd() : commentBody[..start].TrimEnd();
	}

	public static PanelSession? Extract(string commentBody)
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
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object
				|| !doc.RootElement.TryGetProperty("sessions", out var arr)
				|| arr.ValueKind != JsonValueKind.Array)
			{
				return null;
			}

			var byPersona = new Dictionary<string, ReviewSession>(StringComparer.OrdinalIgnoreCase);
			foreach (var el in arr.EnumerateArray())
			{
				if (el.ValueKind != JsonValueKind.Object)
				{
					continue;
				}

				var id = FindingsParser.GetString(el, "persona");
				if (!string.IsNullOrWhiteSpace(id))
				{
					byPersona[id!] = SessionCodec.ReadSessionBody(el);
				}
			}

			return new PanelSession(byPersona);
		}
		catch (Exception e) when (e is FormatException or JsonException)
		{
			return null;
		}
	}

	private static string ToBase64Json(PanelSession session)
	{
		var buffer = new System.Buffers.ArrayBufferWriter<byte>();
		using (var w = new Utf8JsonWriter(buffer))
		{
			w.WriteStartObject();
			w.WriteStartArray("sessions");
			foreach (var (personaId, s) in session.ByPersona)
			{
				w.WriteStartObject();
				w.WriteString("persona", personaId);
				SessionCodec.WriteSessionBody(w, s);
				w.WriteEndObject();
			}

			w.WriteEndArray();
			w.WriteEndObject();
		}

		return Convert.ToBase64String(buffer.WrittenSpan);
	}
}
