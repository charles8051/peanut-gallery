using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>
/// One fallback prompt shape to retry after an empty completion: whether to keep the whole-file
/// context, and the byte budget to trim the diff to (<c>null</c> = keep the diff unchanged). Pure
/// data; the shell rebuilds the <see cref="ReviewRequest"/> from it (re-fitting context / re-running
/// <see cref="DiffFilter"/>) and re-issues the model call.
/// </summary>
public sealed record PromptShape(bool IncludeContext, int? DiffMaxBytes);

/// <summary>
/// The pure policy for shrinking a prompt after a model returns an empty completion. A
/// large-context model can burn its whole output budget on internal reasoning and return no content
/// when the prompt is very large; the cure is a smaller prompt, not a re-ask at the same size. This
/// decides <em>which</em> smaller shapes to try, in order; the shell owns the retry loop and IO.
/// </summary>
public static class PromptReduction
{
	/// <summary>
	/// The default diff byte budgets to retry at, largest first so as much of the change survives as
	/// possible. These are general conservative fallbacks, not model-specific logic - the ladder
	/// applies them uniformly to every model. The values were sized from a motivating observation
	/// (OpenRouter <c>minimax/minimax-m3</c> answered a ~24KB diff but returned an empty completion at
	/// 99KB); a caller that knows a model's tolerance can pass its own budgets to <see cref="Ladder"/>
	/// instead. The shell stops at the first shape that parses, so the smaller budget is only ever
	/// reached when the larger one also failed.
	/// </summary>
	public static readonly IReadOnlyList<int> DefaultRetryDiffBudgets = new[] { 48 * 1024, 24 * 1024 };

	/// <summary>
	/// The ordered fallback shapes to try after an empty reply, using <see cref="DefaultRetryDiffBudgets"/>.
	/// </summary>
	public static IReadOnlyList<PromptShape> Ladder(bool hadContext, int currentDiffBytes) =>
		Ladder(hadContext, currentDiffBytes, DefaultRetryDiffBudgets);

	/// <summary>
	/// The ordered fallback shapes to try after an empty reply, given the first attempt's prompt and
	/// the diff budgets to trim to. Drops the whole-file context first (the cheapest large chunk to
	/// shed and the one the diff tier can most afford to lose), then progressively trims the diff.
	/// Steps that would not actually shrink the prompt are skipped - no context to drop, or a budget
	/// that is not smaller than the current diff - so an already-small, contextless prompt yields an
	/// empty ladder and the shell falls straight through to the JSON repair. The budgets are a
	/// parameter, not a baked-in constant, so a caller (today the shell's default, later per-model or
	/// per-provider config) decides them; the ladder logic stays a pure, model-agnostic value transform.
	/// </summary>
	/// <param name="hadContext">Whether the first attempt included whole-file context.</param>
	/// <param name="currentDiffBytes">The raw byte length of the diff sent on the first attempt.</param>
	/// <param name="diffBudgets">The byte budgets to trim the diff to, in the order they should be tried.</param>
	public static IReadOnlyList<PromptShape> Ladder(
		bool hadContext, int currentDiffBytes, IReadOnlyList<int> diffBudgets)
	{
		var shapes = new List<PromptShape>();

		// Same diff, context removed - only worth an extra round-trip if there was context to remove.
		if (hadContext)
		{
			shapes.Add(new PromptShape(IncludeContext: false, DiffMaxBytes: null));
		}

		// Then trim the diff (context already gone) at each budget that is genuinely smaller than
		// what we just sent. A budget >= the current diff would re-send the same bytes.
		foreach (var budget in diffBudgets)
		{
			if (budget < currentDiffBytes)
			{
				shapes.Add(new PromptShape(IncludeContext: false, DiffMaxBytes: budget));
			}
		}

		return shapes;
	}
}
