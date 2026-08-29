using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// #121: a persona whose model call HANGS (a stalled read that never honours cancellation) must not
/// take the whole panel down with it. The per-persona budget has to fire on the wall clock, not on
/// the work cooperating — otherwise the run outlives its budget and the workflow's hard kill
/// destroys every finished reviewer's findings. This drives the real ReviewRunner fold.
/// </summary>
public class HungPersonaTests
{
	private const string Repo = "acme-api";

	private static readonly Diff SampleDiff = Diff.Parse(
		"diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n");

	private static Task<Diff> Delta(ReviewSession _, CancellationToken __) => Task.FromResult(SampleDiff);

	// The model id carries the persona id, so the reviewer can tell which one is calling.
	private static Persona Persona(string id) => new(
		id, id, id, ReviewTier.Diff, new ModelRef("openrouter", id), 0.2, "review it");

	private static PeanutConfig Config(params string[] ids) => new(
		Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
		Personas: ids.Select(Persona).ToList(),
		Repos: [new RepoTarget(Repo, ".")],
		Assignments: ids.Select(id => new Assignment(id, Repo)).ToList(),
		Verify: false);

	[Fact]
	public async Task A_hung_persona_fails_on_its_budget_and_its_siblings_still_produce_results()
	{
		// "healthy" answers immediately; "hung" ignores its token and would run for 30s.
		var reviewer = new PerPersonaReviewer(hungPersonaId: "hung");

		var sw = System.Diagnostics.Stopwatch.StartNew();
		var run = await ReviewRunner.RunAsync(
			new ReviewRunRequest(
				Config("healthy", "hung"), Repo, "sha1", [], Delta, reviewer,
				PersonaBudget: TimeSpan.FromMilliseconds(300)));
		sw.Stop();

		// The run completes on the ~300ms budget, NOT the 30s hang — that is the whole fix.
		Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
			$"the hung persona must not stall the run past its budget (took {sw.Elapsed})");

		// Both personas are accounted for: the healthy one reviewed, the hung one failed cleanly
		// (exactly like a provider error), so the panel still carries the healthy findings.
		var healthy = run.Personas.Single(p => p.PersonaId == "healthy");
		var hung = run.Personas.Single(p => p.PersonaId == "hung");
		Assert.Equal(PersonaOutcome.Reviewed, healthy.Outcome);
		Assert.Equal(PersonaOutcome.Failed, hung.Outcome);
		Assert.Contains("budget", hung.Observability.FailureReason ?? "");
	}

	/// <summary>Answers instantly for every persona except one, whose call hangs ignoring its token.</summary>
	private sealed class PerPersonaReviewer(string hungPersonaId) : IReviewer
	{
		private const string OneFinding =
			"""{"summary":"s","findings":[{"title":"bug","file":"x.cs","line":1,"severity":"major"}]}""";

		public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
			Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

		public async Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
		{
			if (string.Equals(request.Model.ModelId, hungPersonaId, StringComparison.Ordinal))
			{
				// The hang: a read that never observes cancellation. CancellationToken.None is the
				// point — passing ct would make it cooperative and defeat the test.
				await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
			}

			return ModelReply.Untracked(OneFinding);
		}
	}
}
