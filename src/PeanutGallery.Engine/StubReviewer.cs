using PeanutGallery.Core;

namespace PeanutGallery.Engine;

/// <summary>
/// A deterministic, offline reviewer that calls no model - the <c>--dry-run</c> path.
/// It proves the end-to-end projection (config → plan → review → rendered comment)
/// runs with no API keys, and gives the test suite something total to assert against.
/// </summary>
public sealed class StubReviewer : IReviewer
{
	public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default)
	{
		var finding = new Finding(
			Severity.Info,
			File: string.Empty,
			Line: 0,
			Title: $"dry run: {task.Persona.Lens} review not executed",
			Body: $"Would call {task.Persona.Model} ({task.Persona.Tier} tier) for repo "
				+ $"'{task.Repo.Name}'. No model was invoked (dry run).");

		return Task.FromResult(new PersonaReview(task.Persona, task.Repo, [finding]));
	}

	// Canned session-update JSON so the stateful PR path renders offline with no model. No usage:
	// nothing was called, so a dry run reports no spend rather than a fabricated one.
	public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default) =>
		Task.FromResult(ModelReply.Untracked(
			"{\"summary\":\"dry run - no model called\",\"findings\":[],\"resolved\":[]}"));
}
