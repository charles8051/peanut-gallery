using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>
/// The heart of the core: fold a configuration plus a concrete trigger (which repo
/// changed, and its diff) into the exact set of review tasks to run - one per
/// persona assigned to that repo, each with its request pre-assembled. Pure and
/// total: same config + same diff yields the same plan, every time, with no IO.
/// Every shell (CLI, headless server, desktop GUI) runs this identical fold, so
/// they can never disagree about what a review is.
/// </summary>
public static class ReviewPlanner
{
	public static IReadOnlyList<ReviewTask> Plan(
		PeanutConfig config, string repoName, Diff diff, RepoConventions? conventions = null)
	{
		var repo = config.FindRepo(repoName);
		if (repo is null)
		{
			return [];
		}

		var tasks = new List<ReviewTask>();
		foreach (var assignment in config.Assignments)
		{
			if (assignment.RepoName != repoName)
			{
				continue;
			}

			var persona = config.FindPersona(assignment.PersonaId);
			if (persona is null)
			{
				continue; // dangling assignment; ConfigValidation surfaces it separately.
			}

			tasks.Add(new ReviewTask(persona, repo, PromptAssembly.Build(persona, repo, diff, conventions)));
		}

		return tasks;
	}
}
