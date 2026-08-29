using System.Linq;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

public class ReviewPlannerTests
{
	private static readonly Diff SomeDiff = Diff.Parse("diff --git a/x b/x\n+change\n");

	[Fact]
	public void Plans_one_task_per_persona_assigned_to_the_repo()
	{
		var tasks = ReviewPlanner.Plan(TestData.FullConfig, "demo", SomeDiff);

		Assert.Equal(2, tasks.Count);
		Assert.Equal(["architect", "bug-hunter"], tasks.Select(t => t.Persona.Id).OrderBy(x => x));
		Assert.All(tasks, t => Assert.Equal("demo", t.Repo.Name));
	}

	[Fact]
	public void Each_task_carries_a_pre_assembled_request_for_its_persona()
	{
		var task = ReviewPlanner.Plan(TestData.FullConfig, "demo", SomeDiff)
			.Single(t => t.Persona.Id == "architect");

		Assert.Equal(TestData.Architect.Model, task.Request.Model);
		Assert.Equal(TestData.Architect.Temperature, task.Request.Temperature);
		Assert.Equal(ReviewTier.Diff, task.Request.Tier);
		Assert.Equal(2, task.Request.Messages.Count); // user + system
		Assert.Contains("architectural coherence", Msg.System(task.Request));
	}

	[Fact]
	public void Unknown_repo_yields_no_tasks()
	{
		Assert.Empty(ReviewPlanner.Plan(TestData.FullConfig, "nope", SomeDiff));
	}

	[Fact]
	public void Dangling_assignment_is_skipped_not_thrown()
	{
		var config = TestData.FullConfig with
		{
			Assignments = [new Assignment("ghost", "demo"), new Assignment("architect", "demo")],
		};

		var tasks = ReviewPlanner.Plan(config, "demo", SomeDiff);

		var task = Assert.Single(tasks);
		Assert.Equal("architect", task.Persona.Id);
	}

	[Fact]
	public void Plan_is_pure_same_inputs_same_output()
	{
		var a = ReviewPlanner.Plan(TestData.FullConfig, "demo", SomeDiff);
		var b = ReviewPlanner.Plan(TestData.FullConfig, "demo", SomeDiff);

		Assert.Equal(a.Select(t => t.Persona.Id), b.Select(t => t.Persona.Id));
	}
}
