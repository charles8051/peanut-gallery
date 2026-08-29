using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PeanutGallery.Core;

/// <summary>
/// Serializes a <see cref="RunMetrics"/> to and from one compact JSON line (the unit the ledger
/// appends). Reflection-free (Utf8JsonWriter / JsonDocument) so the core stays AOT-clean; keys are
/// short because every line is stored inside a PR comment with a size cap. Run-level totals are NOT
/// written — they are pure derivations of the persona rows, so the reader recomputes them and the
/// stored line cannot disagree with itself.
/// </summary>
public static class MetricsCodec
{
	/// <summary>One run as a single-line JSON object (no newlines, safe to join with '\n').</summary>
	public static string WriteLine(RunMetrics m)
	{
		var buffer = new System.Buffers.ArrayBufferWriter<byte>();
		using (var w = new Utf8JsonWriter(buffer))
		{
			w.WriteStartObject();
			// The schema the RECORD carries, not this build's constant: a line re-written from a
			// parsed older one must keep saying how old it is, or the version stamp would launder
			// missing fields into recorded zeros the first time anything round-tripped the ledger.
			w.WriteNumber("v", m.SchemaVersion);
			w.WriteString("repo", m.Context.Repo);
			w.WriteNumber("pr", m.Context.Pr);
			w.WriteString("sha", m.Context.Sha);
			w.WriteString("ts", m.Context.TimestampUtc);
			w.WriteString("panel", m.Context.Panel);
			// The run's diff shape, so a PR's trajectory is a fold over recorded facts. Written
			// only when there is one: an absent object reads back as "shape not recorded", which is
			// what every ledger line predating this field is.
			if (m.Context.Shape is { } shape)
			{
				w.WriteStartObject("shape");
				w.WriteNumber("f", shape.Files);
				w.WriteNumber("a", shape.Added);
				w.WriteNumber("r", shape.Removed);
				w.WriteNumber("ta", shape.TestAdded);
				w.WriteEndObject();
			}

			w.WriteStartArray("p");
			foreach (var p in m.Personas)
			{
				w.WriteStartObject();
				w.WriteString("id", p.Id);
				w.WriteString("nm", p.Name);
				w.WriteString("ln", p.Lens);
				w.WriteString("md", p.Model);
				w.WriteString("tr", p.Tier);
				w.WriteString("oc", p.Outcome);
				w.WriteNumber("ms", p.ElapsedMs);
				w.WriteNumber("in", p.InputTokens);
				w.WriteNumber("out", p.OutputTokens);
				w.WriteNumber("vin", p.VerifyInputTokens);
				w.WriteNumber("vout", p.VerifyOutputTokens);
				w.WriteNumber("rz", p.Raised);
				w.WriteNumber("po", p.Posted);
				w.WriteNumber("rf", p.Refuted);
				w.WriteNumber("sp", p.Suppressed);
				w.WriteString("fl", p.Failure.ToString());
				w.WriteNumber("at", p.Attempts);
				w.WriteNumber("cin", p.CachedInputTokens);
				w.WriteNumber("vcin", p.VerifyCachedInputTokens);
				// The author's verdict. Written even when both are 0: on a line stamped
				// v>=RunMetrics.VerdictSchema a zero is a recorded fact, and the run-level "v" is what
				// separates that from a line that never had the keys. Two short keys, ~14 bytes a row.
				w.WriteNumber("rs", p.Resolved);
				w.WriteNumber("wd", p.Withdrawn);
				w.WriteEndObject();
			}

			w.WriteEndArray();
			w.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	/// <summary>Parse one line back into a <see cref="RunMetrics"/>, or null if it is unreadable.</summary>
	public static RunMetrics? ReadLine(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return null;
		}

		try
		{
			using var doc = JsonDocument.Parse(line);
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				return null;
			}

			var ctx = new RunContext(
				Str(root, "repo"), Int(root, "pr"), Str(root, "sha"), Str(root, "ts"), Str(root, "panel"),
				root.TryGetProperty("shape", out var s) && s.ValueKind == JsonValueKind.Object
					? new DiffShape(Int(s, "f"), Int(s, "a"), Int(s, "r"), Int(s, "ta"))
					: null);

			var personas = new List<PersonaMetric>();
			if (root.TryGetProperty("p", out var arr) && arr.ValueKind == JsonValueKind.Array)
			{
				foreach (var p in arr.EnumerateArray())
				{
					personas.Add(new PersonaMetric(
						Str(p, "id"), Str(p, "nm"), Str(p, "ln"), Str(p, "md"), Str(p, "tr"), Str(p, "oc"),
						Long(p, "ms"), Long(p, "in"), Long(p, "out"), Long(p, "vin"), Long(p, "vout"),
						Int(p, "rz"), Int(p, "po"), Int(p, "rf"), Int(p, "sp"),
						Enum.TryParse<FailureClass>(Str(p, "fl"), out var fc) ? fc : FailureClass.Other,
						p.TryGetProperty("at", out _) ? Int(p, "at") : 1, // pre-field lines default to 1
						Long(p, "cin"), // missing on pre-cache lines -> 0, "no hit reported" reads the same
						Long(p, "vcin"),
						// Missing on any line below RunMetrics.VerdictSchema -> 0. That 0 is NOT a
						// verdict; the version carried on the RunMetrics below is what says so, and
						// MetricsReport excludes such rows from the agreement ratio instead of
						// averaging them in.
						Int(p, "rs"),
						Int(p, "wd")));
				}
			}

			// A line with no "v" at all reads as version 0 — older than every schema, which is the
			// safe direction: it claims nothing was recorded rather than claiming zeros were.
			return new RunMetrics(ctx, personas, Int(root, "v"));
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>Parse many JSONL lines, silently dropping any that are unreadable (forward-compatible).</summary>
	public static IReadOnlyList<RunMetrics> ReadLines(IEnumerable<string> lines)
	{
		var list = new List<RunMetrics>();
		foreach (var line in lines)
		{
			var m = ReadLine(line);
			if (m is not null)
			{
				list.Add(m);
			}
		}

		return list;
	}

	private static string Str(JsonElement e, string name) =>
		e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

	private static int Int(JsonElement e, string name) =>
		e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

	private static long Long(JsonElement e, string name) =>
		e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
