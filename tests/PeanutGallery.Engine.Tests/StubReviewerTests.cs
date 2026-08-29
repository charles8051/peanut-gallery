using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

public class StubReviewerTests
{
	[Fact]
	public async Task Returns_a_single_info_finding_and_never_calls_a_model()
	{
		var persona = new Persona(
			"contrarian", "The Contrarian", "contrarian", ReviewTier.Agent,
			new ModelRef("openrouter", "x-ai/grok-4"), 0.8, "system");
		var repo = new RepoTarget("demo", "/tmp/demo");
		var task = new ReviewTask(persona, repo, PromptAssembly.Build(persona, repo, Diff.Empty));

		var review = await new StubReviewer().ReviewAsync(task);

		var finding = Assert.Single(review.Findings);
		Assert.Equal(Severity.Info, finding.Severity);
		Assert.Equal("contrarian", review.Persona.Id);
	}
}
