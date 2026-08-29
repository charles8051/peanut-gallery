using System.Collections.Generic;
using System.Text.Json;

namespace PeanutGallery.Core;

/// <summary>
/// Pure parse of a stateful reviewer's per-turn JSON reply
/// (<c>{"summary":...,"findings":[...],"resolved":[...]}</c>) into a
/// <see cref="SessionUpdate"/>. Total: anything unparseable yields an empty update
/// (and still salvages a bare findings array if that is all the model returned).
/// </summary>
public static class SessionUpdateParser
{
	/// <summary>
	/// Lossy convenience wrapper: the update alone, with the read/unreadable distinction
	/// discarded. Callers that post a review must use <see cref="ParseResult"/> instead, or
	/// an unreadable reply silently becomes a clean review.
	/// </summary>
	public static SessionUpdate Parse(string? modelText) => ParseResult(modelText).Update;

	/// <summary>
	/// Read one turn's reply, distinguishing "understood, nothing to report" from
	/// "could not read this at all". Still total - never throws.
	/// </summary>
	public static SessionUpdateResult ParseResult(string? modelText)
	{
		var json = FindingsParser.ExtractJsonObject(modelText);
		if (json is null)
		{
			return string.IsNullOrWhiteSpace(modelText)
				? SessionUpdateResult.EmptyReply("the model returned an empty reply")
				: SessionUpdateResult.Unreadable("no JSON object was found in the reply");
		}

		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				return SessionUpdateResult.Unreadable("the reply's JSON was not an object");
			}

			// A JSON object carrying none of the protocol's keys did not answer the question -
			// it is a reply that ignored the contract, not an empty review. Posting it as clean
			// would be the same false negative this type exists to prevent.
			if (!root.TryGetProperty("summary", out _)
				&& !root.TryGetProperty("findings", out _)
				&& !root.TryGetProperty("resolved", out _)
				&& !root.TryGetProperty("withdrawn", out _))
			{
				return SessionUpdateResult.Unreadable(
					"the reply had none of the expected keys (summary/findings/resolved/withdrawn)");
			}

			var summary = FindingsParser.GetString(root, "summary") ?? string.Empty;
			var findings = root.TryGetProperty("findings", out var f) && f.ValueKind == JsonValueKind.Array
				? FindingsParser.ReadFindingsArray(f)
				: [];
			var resolved = ReadStringArray(root, "resolved");
			var withdrawn = ReadStringArray(root, "withdrawn");
			return SessionUpdateResult.Ok(new SessionUpdate(summary, findings, resolved, withdrawn));
		}
		catch (JsonException)
		{
			return SessionUpdateResult.Unreadable("the reply was not valid JSON");
		}
	}

	private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
		{
			return [];
		}

		var items = new List<string>();
		foreach (var el in arr.EnumerateArray())
		{
			if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
			{
				items.Add(s);
			}
		}

		return items;
	}
}
