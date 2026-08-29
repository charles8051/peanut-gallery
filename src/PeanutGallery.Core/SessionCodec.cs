using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PeanutGallery.Core;

/// <summary>
/// Persists a <see cref="ReviewSession"/> inside the persona's PR comment — the
/// comment IS the per-(PR, persona) datastore, so there is no external store and the
/// state dies with the PR. The session is base64-encoded JSON wrapped in a hidden
/// HTML comment appended after the visible review; base64 keeps the <c>--&gt;</c>
/// delimiter safe regardless of summary/finding text. Pure and reflection-free
/// (Utf8JsonWriter / JsonDocument), so the core stays AOT-clean.
/// </summary>
public static class SessionCodec
{
	private const string Open = "<!-- pg-state:1:";
	private const string Close = " -->";

	public static string Embed(string visibleMarkdown, ReviewSession session) =>
		visibleMarkdown.TrimEnd() + "\n\n" + Open + ToBase64Json(session) + Close;

	public static ReviewSession? Extract(string commentBody)
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

	/// <summary>
	/// Write a session's fields into an already-open JSON object. Shared with
	/// <see cref="PanelSessionCodec"/> so the on-disk session shape has exactly one definition -
	/// two copies would drift, and a drifted session blob is silently lost review state.
	/// </summary>
	internal static void WriteSessionBody(Utf8JsonWriter w, ReviewSession s)
	{
			if (s.LastReviewedSha is null)
			{
				w.WriteNull("sha");
			}
			else
			{
				w.WriteString("sha", s.LastReviewedSha);
			}

			w.WriteNumber("turn", s.Turn);
			w.WriteNumber("seen", s.LastSeenCommentId);
			w.WriteString("summary", s.Summary);
			w.WriteStartArray("open");
			foreach (var f in s.OpenFindings)
			{
				w.WriteStartObject();
				w.WriteString("severity", f.Severity.ToString().ToLowerInvariant());
				w.WriteString("file", f.File);
				w.WriteNumber("line", f.Line);
				w.WriteString("title", f.Title);
				w.WriteString("body", f.Body);
				w.WriteNumber("confidence", f.Confidence);
				w.WriteEndObject();
			}

			w.WriteEndArray();

			if (s.DroppedTitles.Count > 0)
			{
				w.WriteStartArray("dropped");
				foreach (var title in s.DroppedTitles)
				{
					w.WriteStringValue(title);
				}

				w.WriteEndArray();
			}

	}

	private static string ToBase64Json(ReviewSession s)
	{
		var buffer = new System.Buffers.ArrayBufferWriter<byte>();
		using (var w = new Utf8JsonWriter(buffer))
		{
			w.WriteStartObject();
			WriteSessionBody(w, s);
			w.WriteEndObject();
		}

		return Convert.ToBase64String(buffer.WrittenSpan);
	}

	private static ReviewSession FromJson(string json)
	{
		using var doc = JsonDocument.Parse(json);
		return ReadSessionBody(doc.RootElement);
	}

	/// <summary>Read a session from a JSON object written by <see cref="WriteSessionBody"/>.</summary>
	internal static ReviewSession ReadSessionBody(JsonElement root)
	{
		var sha = root.TryGetProperty("sha", out var shaEl) && shaEl.ValueKind == JsonValueKind.String
			? shaEl.GetString()
			: null;
		var turn = root.TryGetProperty("turn", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
		var seen = root.TryGetProperty("seen", out var se) && se.ValueKind == JsonValueKind.Number ? se.GetInt64() : 0;
		var summary = FindingsParser.GetString(root, "summary") ?? string.Empty;
		IReadOnlyList<Finding> open = root.TryGetProperty("open", out var o) && o.ValueKind == JsonValueKind.Array
			? FindingsParser.ReadFindingsArray(o)
			: [];
		List<string>? dropped = null;
		if (root.TryGetProperty("dropped", out var d) && d.ValueKind == JsonValueKind.Array)
		{
			dropped = [];
			foreach (var el in d.EnumerateArray())
			{
				if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } title)
				{
					dropped.Add(title);
				}
			}
		}

		return new ReviewSession(sha, turn, summary, open, seen, dropped);
	}
}
