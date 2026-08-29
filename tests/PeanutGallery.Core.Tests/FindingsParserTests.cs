using System.Linq;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

public class FindingsParserTests
{
	[Fact]
	public void Parses_a_plain_findings_object()
	{
		const string text =
			"""{"findings":[{"severity":"major","file":"a.cs","line":12,"title":"npe","body":"x may be null"}]}""";

		var finding = Assert.Single(FindingsParser.Parse(text));

		Assert.Equal(Severity.Major, finding.Severity);
		Assert.Equal("a.cs", finding.File);
		Assert.Equal(12, finding.Line);
		Assert.Equal("npe", finding.Title);
		Assert.Equal("x may be null", finding.Body);
	}

	[Fact]
	public void Finds_the_json_when_wrapped_in_prose_or_a_code_fence()
	{
		const string text =
			"Sure, here is my review:\n\n```json\n"
			+ """{"findings":[{"severity":"minor","title":"naming","body":"rename foo"}]}"""
			+ "\n```\nHope that helps!";

		var finding = Assert.Single(FindingsParser.Parse(text));
		Assert.Equal(Severity.Minor, finding.Severity);
		Assert.Equal("naming", finding.Title);
	}

	[Fact]
	public void Unknown_or_missing_severity_defaults_to_info()
	{
		const string text = """{"findings":[{"severity":"spicy","title":"t","body":"b"},{"title":"u","body":"c"}]}""";

		var findings = FindingsParser.Parse(text);

		Assert.Equal(2, findings.Count);
		Assert.All(findings, f => Assert.Equal(Severity.Info, f.Severity));
	}

	[Fact]
	public void Line_accepts_a_number_or_a_numeric_string()
	{
		const string text =
			"""{"findings":[{"title":"a","body":"b","line":7},{"title":"c","body":"d","line":"9"}]}""";

		var lines = FindingsParser.Parse(text).Select(f => f.Line).ToList();
		Assert.Equal([7, 9], lines);
	}

	[Fact]
	public void Entries_with_no_title_and_no_body_are_dropped()
	{
		const string text = """{"findings":[{"severity":"info"},{"title":"keep","body":""}]}""";

		var finding = Assert.Single(FindingsParser.Parse(text));
		Assert.Equal("keep", finding.Title);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("no json here at all")]
	[InlineData("{ this is not valid json }")]
	[InlineData("""{"findings":"not-an-array"}""")]
	[InlineData("""{"other":[]}""")]
	public void Unparseable_or_findingless_input_yields_empty(string? text)
	{
		Assert.Empty(FindingsParser.Parse(text));
	}
}
