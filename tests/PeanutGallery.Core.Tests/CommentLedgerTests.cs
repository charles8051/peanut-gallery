using System.Collections.Generic;
using System.Linq;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// The rule that makes posting a run's comments MORE THAN ONCE safe (#116). Without it, a body
/// published the moment its persona landed would be created a second time by the end-of-run write:
/// <see cref="CommentSync.Plan"/> matches against the pre-run snapshot, which cannot contain a
/// comment this run created.
/// </summary>
public class CommentLedgerTests
{
    private static string Body(string personaId, string text) =>
        $"{CommentRenderer.Marker(personaId)}\n### {personaId}\n{text}\n";

    /// <summary>Runs a plan against a fake thread, handing out ids the way GitHub would.</summary>
    private static (CommentLedger Ledger, int Created, int Updated) Apply(
        CommentLedger ledger, IReadOnlyList<string> bodies, ref long nextId)
    {
        var created = 0;
        var updated = 0;
        foreach (var op in ledger.Plan(bodies))
        {
            if (op.Action == UpsertAction.Update)
            {
                ledger = ledger.Record(op, op.CommentId!.Value);
                updated++;
            }
            else
            {
                ledger = ledger.Record(op, nextId++);
                created++;
            }
        }

        return (ledger, created, updated);
    }

    [Fact]
    public void A_body_published_twice_in_one_run_is_created_once_then_updated()
    {
        long nextId = 100;
        var ledger = CommentLedger.From([]);

        var first = Apply(ledger, [Body("architect", "turn 1")], ref nextId);
        var second = Apply(first.Ledger, [Body("architect", "turn 1, now with a verdict")], ref nextId);

        Assert.Equal(1, first.Created);
        Assert.Equal(0, second.Created);   // the duplicate that used to appear on the PR
        Assert.Equal(1, second.Updated);
        Assert.Single(second.Ledger.Comments);
    }

    [Fact]
    public void The_update_targets_the_comment_this_run_created()
    {
        long nextId = 100;
        var ledger = CommentLedger.From([]);
        var first = Apply(ledger, [Body("panel", "one reviewer in")], ref nextId);

        var plan = first.Ledger.Plan([Body("panel", "all reviewers in")]);

        var op = Assert.Single(plan);
        Assert.Equal(UpsertAction.Update, op.Action);
        Assert.Equal(100, op.CommentId);
    }

    [Fact]
    public void Re_publishing_an_identical_body_is_a_no_op()
    {
        // The common shape after incremental posting: the end-of-run write repeats what already
        // went up. Writing it again costs an API call and stamps "edited" on nobody's edit.
        long nextId = 100;
        var body = Body("architect", "turn 1");
        var first = Apply(CommentLedger.From([]), [body], ref nextId);

        Assert.Empty(first.Ledger.Plan([body]));
    }

    [Fact]
    public void A_body_identical_to_a_pre_existing_comment_is_also_a_no_op()
    {
        var body = Body("architect", "nothing moved");
        var ledger = CommentLedger.From([new ExistingComment(7, body)]);

        Assert.Empty(ledger.Plan([body]));
    }

    [Fact]
    public void A_changed_body_still_updates_the_pre_existing_comment()
    {
        var ledger = CommentLedger.From([new ExistingComment(7, Body("architect", "turn 1"))]);

        var op = Assert.Single(ledger.Plan([Body("architect", "turn 2")]));
        Assert.Equal(UpsertAction.Update, op.Action);
        Assert.Equal(7, op.CommentId);
    }

    [Fact]
    public void Personas_keep_separate_comments()
    {
        long nextId = 100;
        var run = Apply(CommentLedger.From([]), [Body("architect", "a"), Body("bug-hunter", "b")], ref nextId);

        Assert.Equal(2, run.Created);
        Assert.Equal(2, run.Ledger.Comments.Count);
        Assert.Equal([100L, 101L], run.Ledger.Comments.Select(c => c.Id).ToList());
    }

    [Fact]
    public void A_create_whose_id_could_not_be_read_back_is_never_duplicated()
    {
        // Belt and braces: if the create response is unparseable we know the comment exists but
        // cannot address it. Skipping the refresh beats leaving a second copy on the PR forever.
        var body = Body("architect", "turn 1");
        var ledger = CommentLedger.From([]);
        var op = Assert.Single(ledger.Plan([body]));
        ledger = ledger.Record(op, 0);

        Assert.Empty(ledger.Plan([body]));                              // identical: nothing to do
        Assert.Empty(ledger.Plan([Body("architect", "turn 2")]));       // changed: still no duplicate
    }

    [Fact]
    public void An_unmarked_body_is_always_created()
    {
        var ledger = CommentLedger.From([new ExistingComment(7, Body("architect", "x"))]);

        var op = Assert.Single(ledger.Plan(["a plain comment with no marker"]));
        Assert.Equal(UpsertAction.Create, op.Action);
    }

    [Fact]
    public void Recording_leaves_the_original_ledger_alone()
    {
        var before = CommentLedger.From([]);
        var op = Assert.Single(before.Plan([Body("architect", "x")]));

        before.Record(op, 100);

        Assert.Empty(before.Comments);
    }
}
