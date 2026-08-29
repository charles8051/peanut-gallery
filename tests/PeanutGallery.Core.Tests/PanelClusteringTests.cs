using System;
using System.Collections.Generic;
using System.Linq;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// Clustering is presentation, and the only reason it is allowed to be looser than
/// <see cref="FindingSynthesis"/> is that it never removes anything. So the load-bearing test here
/// is conservation: every finding still reaches the reader, exactly once.
///
/// <para>These assert against the rendered markdown rather than an intermediate, because the
/// grouping model is private to the renderer (#172 review) and because the markdown is what a
/// reader actually gets - a cluster that exists in a data structure but renders wrong has still
/// lost the finding. The counts are read back out of the comment the same way, which makes the
/// summary line and the list check each other.</para>
/// </summary>
public class PanelClusteringTests
{
	private static AttributedFinding A(string title, string file = "a.cs", int line = 10,
		Severity sev = Severity.Major, string[]? lenses = null) =>
		new(new Finding(sev, file, line, title, "why this matters"), lenses ?? ["bugs"]);

	private static PanelReport Report(params AttributedFinding[] findings) =>
		new([new PanelMember("a", "A", "a", "openrouter:m", true)],
			new SynthesisResult(findings, 0), [], [], 0, []);

	private static string Render(params AttributedFinding[] findings) =>
		PanelCommentRenderer.Render(Report(findings), "abc1234", 1);

	// ---- reading the comment back the way a reader does ----

	private const string HeadingMark = "findings in one area";

	/// <summary>Every line that is a list item, top-level or nested under a cluster heading.</summary>
	private static IReadOnlyList<string> Bullets(string body) =>
		body.Split('\n').Where(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal)).ToList();

	/// <summary>Top-level items: one per cluster, whether it is a heading or a lone finding.</summary>
	private static IReadOnlyList<string> TopLevel(string body) =>
		Bullets(body).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();

	/// <summary>Items that are findings rather than cluster headings - what must be conserved.</summary>
	private static IReadOnlyList<string> FindingBullets(string body) =>
		Bullets(body).Where(l => !l.Contains(HeadingMark, StringComparison.Ordinal)).ToList();

	/// <summary>The two numbers off the summary line, which is the reader's whole calibration.</summary>
	private static (int Areas, int Findings) Summary(string body)
	{
		var m = System.Text.RegularExpressions.Regex.Match(
			body, @"_(\d+) problem areas? · (\d+) findings?\._");
		Assert.True(m.Success, $"no summary line in:\n{body}");
		return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
	}

	private static int Occurrences(string body, string needle)
	{
		var (count, at) = (0, 0);
		while ((at = body.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
		{
			count++;
			at += needle.Length;
		}

		return count;
	}

	// ---- conservation: the invariant the whole design rests on ----

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(7)]
	[InlineData(20)]
	public void Every_finding_reaches_the_reader_exactly_once(int count)
	{
		// Lines straddle the threshold in both directions, over two files, so one theory walks
		// clustered and unclustered shapes rather than only the easy one.
		var findings = Enumerable.Range(0, count)
			.Select(i => A($"finding {i}", i % 2 == 0 ? "a.cs" : "b.cs", (i * 13) + 1))
			.ToArray();

		var body = Render(findings);

		Assert.All(findings, f => Assert.Equal(1, Occurrences(body, $"**{f.Finding.Title}**")));
		// Not just present in the prose somewhere - present as its own bullet, once.
		Assert.Equal(count, FindingBullets(body).Count);
		Assert.Equal(count, Summary(body).Findings);
	}

	[Fact]
	public void Nothing_is_dropped_when_everything_collapses_into_one_area()
	{
		// The #169 case: five findings, five lenses, one root cause. One heading, five bullets.
		var findings = new[]
		{
			A("null deref", line: 40, lenses: ["bugs"]),
			A("unguarded call", line: 42, lenses: ["architecture"]),
			A("missing precondition", line: 44, lenses: ["security"]),
			A("this can throw", line: 45, lenses: ["contrarian"]),
			A("no test covers the null path", line: 47, lenses: ["tests"]),
		};

		var body = Render(findings);

		Assert.Equal((1, 5), Summary(body));
		Assert.Equal(5, FindingBullets(body).Count);
		Assert.Contains("**5 findings in one area** _(bugs, architecture, security, contrarian, tests)_", body);
	}

	[Fact]
	public void The_area_count_is_the_number_of_top_level_items()
	{
		// The summary line and the list have to agree, or the number a reader calibrates to is
		// describing something other than what they can see.
		var body = Render(
			A("one", "a.cs", 10), A("two", "a.cs", 12), A("three", "a.cs", 14),
			A("four", "a.cs", 400),
			A("five", "b.cs", 3));

		Assert.Equal((3, 5), Summary(body));
		Assert.Equal(3, TopLevel(body).Count);
	}

	// ---- what does and does not group ----

	[Fact]
	public void Findings_in_different_files_never_cluster()
	{
		var body = Render(A("same line, other file", "a.cs", 10), A("same line, other file", "b.cs", 10));

		Assert.Equal(2, Summary(body).Areas);
		Assert.DoesNotContain(HeadingMark, body);
	}

	[Fact]
	public void Findings_far_apart_in_one_file_are_separate_areas()
	{
		var body = Render(A("near the top", line: 10), A("way below", line: 310));

		Assert.Equal(2, Summary(body).Areas);
	}

	[Fact]
	public void Findings_a_few_lines_apart_in_one_file_are_one_area()
	{
		var body = Render(A("the call", line: 10), A("the guard above it", line: 25));

		Assert.Equal(1, Summary(body).Areas);
	}

	[Fact]
	public void A_cluster_never_chains_past_its_own_span()
	{
		// Distance is measured from the anchor, not the previous member, so a file with a finding
		// every fifteen lines cannot snowball into one enormous "area". Pins the behaviour with
		// literal lines rather than restating the threshold constant back at the implementation.
		var body = Render(A("a", line: 10), A("b", line: 25), A("c", line: 40), A("d", line: 55));

		Assert.Equal((2, 4), Summary(body));
		Assert.Contains("`a.cs:10-25`", body);
		Assert.Contains("`a.cs:40-55`", body);
	}

	// ---- findings with no area to measure ----

	[Fact]
	public void A_file_wide_finding_does_not_join_a_line_anchored_one()
	{
		// Line 0 means "not tied to a line", not line 1. By raw distance it would swallow anything
		// near the top of the file and claim an agreement nobody made.
		var body = Render(
			A("delete this whole subsystem", line: 0, lenses: ["contrarian"]),
			A("null deref", line: 5, lenses: ["bugs"]));

		Assert.Equal(2, Summary(body).Areas);
	}

	[Fact]
	public void Two_file_wide_findings_about_one_file_are_still_two_areas()
	{
		// Raised by the panel's own contrarian on #172. A shared filename is not evidence that two
		// subsystem-scale claims are one problem, and grouping them would manufacture agreement and
		// understate the count this whole change exists to make honest.
		var body = Render(
			A("this file has no tests", line: 0, lenses: ["tests"]),
			A("this file should not exist", line: 0, lenses: ["contrarian"]));

		Assert.Equal(2, Summary(body).Areas);
		Assert.DoesNotContain(HeadingMark, body);
	}

	[Fact]
	public void A_finding_with_no_file_stands_alone()
	{
		var body = Render(
			A("the PR has no tests", file: "", line: 0),
			A("the PR has no docs", file: "", line: 0));

		Assert.Equal(2, Summary(body).Areas);
	}

	[Fact]
	public void A_finding_with_a_line_but_no_file_is_still_rendered()
	{
		// It belongs to neither the file walk nor a proximity cluster. If the two paths ever drift
		// apart, this is the finding that falls between them and disappears.
		var body = Render(A("no file, but a line", file: "", line: 42));

		Assert.Equal((1, 1), Summary(body));
		Assert.Contains("**no file, but a line**", body);
	}

	// ---- determinism and ordering ----

	[Fact]
	public void Rendering_the_same_findings_twice_gives_the_same_comment()
	{
		var findings = new[]
		{
			A("b", "z.cs", 40, Severity.Minor), A("a", "z.cs", 41, Severity.Minor),
			A("c", "a.cs", 5, Severity.Critical), A("d", "a.cs", 500),
			A("e", "", 0),
		};

		Assert.Equal(Render(findings), Render(findings));
	}

	[Fact]
	public void The_worst_area_is_rendered_first()
	{
		var body = Render(
			A("minor thing", "a.cs", 10, Severity.Minor),
			A("the bad one", "z.cs", 10, Severity.Critical));

		Assert.StartsWith("- 🔴 **critical** `z.cs:10` — **the bad one**", TopLevel(body)[0]);
	}

	// ---- rendering shape ----

	[Fact]
	public void A_single_finding_area_renders_as_a_plain_bullet_with_no_heading()
	{
		var body = Render(A("lonely finding", lenses: ["bugs"]));

		Assert.Contains("- 🟠 **major** `a.cs:10` — **lonely finding** _(bugs)_", body);
		Assert.DoesNotContain(HeadingMark, body);
	}

	[Fact]
	public void A_cluster_renders_one_heading_with_every_member_still_under_it()
	{
		var body = Render(
			A("null deref", line: 40, lenses: ["bugs"]),
			A("unguarded call", line: 44, lenses: ["architecture"]));

		Assert.Contains("`a.cs:40-44` — **2 findings in one area** _(bugs, architecture)_", body);
		// Every finding is still printed, nested, still attributed to the lens that raised it.
		Assert.Contains("  - 🟠 **major** `a.cs:40` — **null deref** _(bugs)_", body);
		Assert.Contains("  - 🟠 **major** `a.cs:44` — **unguarded call** _(architecture)_", body);
	}

	// ---- the summary line's contract ----

	[Fact]
	public void The_summary_line_counts_areas_and_findings_separately()
	{
		// The bug this closes: a reader saw "five" and answered five problems when there were three.
		var body = Render(
			A("one", "a.cs", 10), A("two", "a.cs", 12), A("three", "a.cs", 14),
			A("four", "a.cs", 400),
			A("five", "b.cs", 3));

		Assert.Contains("_3 problem areas · 5 findings._", body);
	}

	[Fact]
	public void The_summary_line_is_singular_for_one_of_each()
	{
		Assert.Contains("_1 problem area · 1 finding._", Render(A("only one")));
	}

	[Fact]
	public void Both_numbers_appear_even_when_nothing_grouped()
	{
		// Printing the pair only when something clustered would make its absence a second signal a
		// reader has to learn. Five unrelated problems really are five areas.
		var body = Render(A("a", "a.cs", 1), A("b", "b.cs", 1), A("c", "c.cs", 1));

		Assert.Contains("_3 problem areas · 3 findings._", body);
	}

	[Fact]
	public void An_empty_panel_says_no_findings_and_carries_no_count_line()
	{
		// The one case that does not get the pair: "_No findings._" is plainer than "0 problem areas
		// · 0 findings", and there is no response to calibrate. Pinned so it stays a decision.
		var body = PanelCommentRenderer.Render(Report(), "abc1234", 1);

		Assert.Contains("_No findings._", body);
		Assert.DoesNotContain("problem area", body);
	}
}
