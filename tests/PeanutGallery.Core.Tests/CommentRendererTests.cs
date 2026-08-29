using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

public class CommentRendererTests
{
	[Fact]
	public void First_line_is_the_stable_persona_marker()
	{
		var review = new PersonaReview(TestData.Architect, new RepoTarget("demo", "/d"), []);

		var md = CommentRenderer.Render(review);

		Assert.StartsWith("<!-- peanut-gallery:architect -->", md);
		Assert.Equal("<!-- peanut-gallery:architect -->", CommentRenderer.Marker("architect"));
	}

	[Fact]
	public void No_findings_renders_an_empty_review_note()
	{
		var review = new PersonaReview(TestData.BugHunter, new RepoTarget("demo", "/d"), []);

		Assert.Contains("_No findings._", CommentRenderer.Render(review));
	}

	[Fact]
	public void Findings_are_ordered_most_severe_first()
	{
		var review = new PersonaReview(TestData.BugHunter, new RepoTarget("demo", "/d"),
		[
			new Finding(Severity.Minor, "a.cs", 1, "small", ""),
			new Finding(Severity.Critical, "b.cs", 2, "boom", "null deref"),
			new Finding(Severity.Info, "c.cs", 3, "fyi", ""),
		]);

		var md = CommentRenderer.Render(review);

		var critical = md.IndexOf("boom", System.StringComparison.Ordinal);
		var minor = md.IndexOf("small", System.StringComparison.Ordinal);
		var info = md.IndexOf("fyi", System.StringComparison.Ordinal);

		Assert.True(critical < minor, "critical should precede minor");
		Assert.True(minor < info, "minor should precede info");
		Assert.Contains("b.cs:2", md);
		Assert.Contains("null deref", md);
	}
}
