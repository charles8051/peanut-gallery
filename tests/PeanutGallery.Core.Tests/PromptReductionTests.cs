using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// The pure ladder that decides how to shrink a prompt after a model returns an empty completion.
/// It must only ever propose shapes that are genuinely smaller than what was already sent, so the
/// shell never spends a round-trip re-sending the same bytes.
/// </summary>
public class PromptReductionTests
{
    private const int Small = 4 * 1024;    // under every budget
    private const int Big = 60 * 1024;     // over both budgets
    private const int Mid = 30 * 1024;     // over 24KB, under 48KB

    [Fact]
    public void No_context_and_a_small_diff_leaves_nothing_to_shrink()
    {
        Assert.Empty(PromptReduction.Ladder(hadContext: false, currentDiffBytes: Small));
    }

    [Fact]
    public void Context_present_yields_a_drop_context_step_even_when_the_diff_is_small()
    {
        var ladder = PromptReduction.Ladder(hadContext: true, currentDiffBytes: Small);
        var only = Assert.Single(ladder);
        Assert.False(only.IncludeContext);
        Assert.Null(only.DiffMaxBytes); // same diff, just no context
    }

    [Fact]
    public void A_large_diff_is_trimmed_at_each_budget_smaller_than_it_largest_first()
    {
        var ladder = PromptReduction.Ladder(hadContext: false, currentDiffBytes: Big);
        Assert.Equal(2, ladder.Count);
        Assert.Equal(new PromptShape(false, 48 * 1024), ladder[0]);
        Assert.Equal(new PromptShape(false, 24 * 1024), ladder[1]);
    }

    [Fact]
    public void Context_and_a_large_diff_drops_context_first_then_trims()
    {
        var ladder = PromptReduction.Ladder(hadContext: true, currentDiffBytes: Big);
        Assert.Equal(3, ladder.Count);
        Assert.Equal(new PromptShape(false, null), ladder[0]);       // drop context
        Assert.Equal(new PromptShape(false, 48 * 1024), ladder[1]);
        Assert.Equal(new PromptShape(false, 24 * 1024), ladder[2]);
    }

    [Fact]
    public void A_budget_not_smaller_than_the_diff_is_skipped()
    {
        // 30KB diff: 48KB would not shrink it, 24KB would.
        var ladder = PromptReduction.Ladder(hadContext: false, currentDiffBytes: Mid);
        var only = Assert.Single(ladder);
        Assert.Equal(new PromptShape(false, 24 * 1024), only);
    }

    [Fact]
    public void A_budget_exactly_equal_to_the_diff_is_skipped_the_comparison_is_strict()
    {
        // A diff of exactly 48KB must NOT be "trimmed" to 48KB - that re-sends the same bytes for
        // nothing. Only the strictly-smaller 24KB budget applies. Guards the < (not <=) boundary.
        var ladder = PromptReduction.Ladder(hadContext: false, currentDiffBytes: 48 * 1024);
        var only = Assert.Single(ladder);
        Assert.Equal(new PromptShape(false, 24 * 1024), only);
    }

    [Fact]
    public void Budgets_are_injectable_the_ladder_is_not_tied_to_the_default_set()
    {
        // The budgets are a parameter, so a caller that knows a model's tolerance (or a future
        // per-model config) supplies its own. The same skip-if-not-smaller rule still applies.
        var ladder = PromptReduction.Ladder(hadContext: false, currentDiffBytes: 100 * 1024,
            diffBudgets: [80 * 1024, 40 * 1024, 200 * 1024]);
        Assert.Equal(2, ladder.Count);
        Assert.Equal(new PromptShape(false, 80 * 1024), ladder[0]);
        Assert.Equal(new PromptShape(false, 40 * 1024), ladder[1]); // 200KB skipped: not < 100KB
    }
}
