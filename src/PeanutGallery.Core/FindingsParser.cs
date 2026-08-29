using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace PeanutGallery.Core;

/// <summary>
/// Pure extraction of structured findings from a model's free-form text. The
/// personas are prompted to return <c>{"findings":[…]}</c> (see
/// <see cref="PromptAssembly"/>); models in practice wrap that in prose or a code
/// fence, so this locates the JSON object, reads it with a reflection-free
/// <see cref="JsonDocument"/> (keeps the core AOT-clean), and maps it to
/// <see cref="Finding"/> values. Total: any input - garbage, empty, partial - yields
/// a (possibly empty) list, never an exception. Living in the core makes the
/// trickiest part of the shell's job exhaustively unit-testable with no network.
/// </summary>
public static class FindingsParser
{
	public static IReadOnlyList<Finding> Parse(string? modelText)
	{
		var json = ExtractJsonObject(modelText);
		if (json is null)
		{
			return [];
		}

		try
		{
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object
				|| !doc.RootElement.TryGetProperty("findings", out var arr)
				|| arr.ValueKind != JsonValueKind.Array)
			{
				return [];
			}

			return ReadFindingsArray(arr);
		}
		catch (JsonException)
		{
			return [];
		}
	}

	/// <summary>Read a JSON array element into <see cref="Finding"/>s; shared with the session parser.</summary>
	internal static IReadOnlyList<Finding> ReadFindingsArray(JsonElement arr)
	{
		var findings = new List<Finding>();
		foreach (var el in arr.EnumerateArray())
		{
			if (el.ValueKind != JsonValueKind.Object)
			{
				continue;
			}

			var title = GetString(el, "title");
			var body = GetString(el, "body");
			if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
			{
				continue; // nothing actionable
			}

			findings.Add(new Finding(
				ParseSeverity(GetString(el, "severity")),
				GetString(el, "file") ?? string.Empty,
				GetInt(el, "line"),
				title ?? string.Empty,
				body ?? string.Empty,
				GetConfidence(el)));
		}

		return findings;
	}

	/// <summary>Map a severity word to the enum; anything unknown is <see cref="Severity.Info"/>.</summary>
	public static Severity ParseSeverity(string? value) => value?.Trim().ToLowerInvariant() switch
	{
		"critical" => Severity.Critical,
		"major" => Severity.Major,
		"minor" => Severity.Minor,
		_ => Severity.Info,
	};

	// Take the substring from the first '{' to the last '}'. Code fences and prose
	// sit outside the braces, so this isolates the JSON object without a parser.
	internal static string? ExtractJsonObject(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}

		var start = text.IndexOf('{');
		var end = text.LastIndexOf('}');
		return start >= 0 && end > start ? text[start..(end + 1)] : null;
	}

	internal static string? GetString(JsonElement el, string name) =>
		el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

	/// <summary>
	/// Read a finding's self-rated confidence, clamped to [0,1]. Absent or unreadable means
	/// 1.0 (fully confident) on purpose: the gate must only suppress findings that explicitly
	/// admitted doubt, never ones from a model or a stored session that predates the field.
	/// </summary>
	internal static double GetConfidence(JsonElement el)
	{
		if (!el.TryGetProperty("confidence", out var v))
		{
			return 1.0;
		}

		return v.ValueKind switch
		{
			JsonValueKind.Number when v.TryGetDouble(out var d) => ConfidenceGate.Clamp(d),
			JsonValueKind.String when double.TryParse(
				v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => ConfidenceGate.Clamp(d),
			_ => 1.0,
		};
	}

	internal static int GetInt(JsonElement el, string name)
	{
		if (!el.TryGetProperty(name, out var v))
		{
			return 0;
		}

		return v.ValueKind switch
		{
			JsonValueKind.Number when v.TryGetInt32(out var n) => n,
			JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
			_ => 0,
		};
	}
}
