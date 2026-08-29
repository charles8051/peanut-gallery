using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PeanutGallery.Core;

/// <summary>One skeptic's ruling on one finding: was it refuted, and on what grounds.</summary>
public sealed record Verdict(string Title, bool Refuted, string Why);

/// <summary>
/// A finding the adversarial pass took off the board, and the grounds it gave.
///
/// <para><see cref="Why"/> is carried rather than discarded because a pass that drops findings
/// without recording WHY cannot be audited. That is not hypothetical: asking "is verification
/// refuting true positives?" of live PRs meant reconstructing the answer from titles alone, because
/// the reasoning had already been thrown away at this seam. A drop is a decision; decisions that
/// nobody can review are how a filter quietly gets worse.</para>
/// </summary>
public sealed record RefutedFinding(string Title, string Why);

/// <summary>
/// What survived refutation. <see cref="Refuted"/> keeps the dropped findings AND the grounds, so
/// the drop can be disclosed and second-guessed rather than quietly shrinking the review.
/// </summary>
public sealed record VerificationResult(IReadOnlyList<Finding> Upheld, IReadOnlyList<RefutedFinding> Refuted);

/// <summary>
/// The adversarial second pass: a reviewer states its findings, then is asked to argue against
/// them, and only what it can still defend gets posted. This is the highest-precision technique
/// in the literature (refute-or-promote, chain-of-verification), and it works because a model
/// asked to *justify* a claim is far more forgiving than one asked to *break* it.
///
/// <para>The matching is fail-open on purpose. A finding with no verdict is UPHELD, and an
/// unreadable verification reply upholds everything: verification is an enhancement, so a
/// failure in it must cost a little precision, never a real finding. Silently dropping
/// findings because a second model call went wrong would be the same defect as silently
/// reporting a clean review (see <see cref="SessionUpdateResult"/>).</para>
///
/// <para>Pure: findings and verdicts in, findings out. The extra model call belongs to the shell.</para>
/// </summary>
public static class Verification
{
	public static VerificationResult Apply(IReadOnlyList<Finding> findings, IReadOnlyList<Verdict> verdicts)
	{
		if (findings.Count == 0 || verdicts.Count == 0)
		{
			return new VerificationResult(findings, []);
		}

		// Titles are the only handle the model has on a finding, so match on them - trimmed and
		// case-insensitively, because a model will re-type a title with drifting capitalisation.
		// The grounds ride along: a drop nobody can second-guess is a drop nobody can correct.
		var refutedReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var v in verdicts)
		{
			if (v.Refuted && !string.IsNullOrWhiteSpace(v.Title))
			{
				refutedReasons[v.Title.Trim()] = v.Why ?? string.Empty;
			}
		}

		var upheld = new List<Finding>(findings.Count);
		var refuted = new List<RefutedFinding>();
		foreach (var f in findings)
		{
			if (refutedReasons.TryGetValue(f.Title.Trim(), out var why))
			{
				// The TRIMMED title, matching what was matched on. Storing the raw one lets the
				// reader see a different string than the matcher acted on, and hands a padded title
				// straight through to a renderer that assumed it had been normalised.
				refuted.Add(new RefutedFinding(f.Title.Trim(), why));
			}
			else
			{
				upheld.Add(f);
			}
		}

		return new VerificationResult(upheld, refuted);
	}
}

/// <summary>
/// Pure parse of the skeptic's reply: <c>{"verdicts":[{"title":…,"verdict":"upheld|refuted",…}]}</c>.
/// Total - anything unreadable yields no verdicts, which (by <see cref="Verification.Apply"/>'s
/// fail-open rule) upholds every finding.
/// </summary>
public static class VerdictParser
{
	public static IReadOnlyList<Verdict> Parse(string? modelText)
	{
		var json = FindingsParser.ExtractJsonObject(modelText);
		if (json is null)
		{
			return [];
		}

		try
		{
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object
				|| !doc.RootElement.TryGetProperty("verdicts", out var arr)
				|| arr.ValueKind != JsonValueKind.Array)
			{
				return [];
			}

			var verdicts = new List<Verdict>();
			foreach (var el in arr.EnumerateArray())
			{
				if (el.ValueKind != JsonValueKind.Object)
				{
					continue;
				}

				var title = FindingsParser.GetString(el, "title");
				if (string.IsNullOrWhiteSpace(title))
				{
					continue;
				}

				// Anything that isn't explicitly "refuted" leaves the finding standing - the
				// skeptic has to actively make its case, not merely fail to endorse.
				var refuted = string.Equals(
					FindingsParser.GetString(el, "verdict")?.Trim(), "refuted", StringComparison.OrdinalIgnoreCase);
				verdicts.Add(new Verdict(title!, refuted, FindingsParser.GetString(el, "why") ?? string.Empty));
			}

			return verdicts;
		}
		catch (JsonException)
		{
			return [];
		}
	}
}
