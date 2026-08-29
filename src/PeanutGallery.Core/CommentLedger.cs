using System;
using System.Collections.Generic;
using System.Linq;

namespace PeanutGallery.Core;

/// <summary>
/// Pure: the PR's comment thread as one run sees it, advanced by that run's OWN writes.
///
/// <para><see cref="CommentSync.Plan"/> answers "create or update?" against a snapshot taken
/// before the run started. That is exactly right for a single end-of-run write, and exactly
/// wrong once a run posts more than once — the comment the run created a minute ago is not in
/// the snapshot, so the next write to the same marker plans a <see cref="UpsertAction.Create"/>
/// and the PR grows a duplicate. The ledger closes that: the shell records each write as it
/// lands, so the next plan sees it. Incremental posting (issue #116) is what makes this
/// necessary; the marker matching rule itself is unchanged.</para>
///
/// <para>It also drops a body byte-identical to what the thread already holds. Re-posting an
/// unchanged comment costs an API call and stamps a fresh "edited" timestamp on a comment
/// nobody edited — and after incremental posting the end-of-run write is usually exactly that,
/// a re-post of what already went up.</para>
/// </summary>
public sealed record CommentLedger
{
    private readonly IReadOnlyList<ExistingComment> _comments;

    private CommentLedger(IReadOnlyList<ExistingComment> comments) => _comments = comments;

    /// <summary>The thread as this ledger knows it — the pre-run comments plus this run's writes.</summary>
    public IReadOnlyList<ExistingComment> Comments => _comments;

    /// <summary>Seed from the comments fetched before the run.</summary>
    public static CommentLedger From(IReadOnlyList<ExistingComment> existing) => new([.. existing]);

    /// <summary>
    /// The writes <paramref name="bodies"/> needs, against everything this ledger knows — including
    /// comments this run already created. Two bodies are omitted rather than written:
    /// <list type="bullet">
    /// <item><description>one identical to what is already on the thread (a no-op write);</description></item>
    /// <item><description>one whose only match is a comment recorded without a usable id — it was
    /// already posted this run but cannot be addressed again, and a create here would duplicate it.
    /// Losing one refresh beats leaving a second copy on the PR forever.</description></item>
    /// </list>
    /// </summary>
    public IReadOnlyList<CommentUpsert> Plan(IReadOnlyList<string> bodies)
    {
        var plan = new List<CommentUpsert>();
        foreach (var op in CommentSync.Plan(_comments, bodies))
        {
            if (op.Action == UpsertAction.Update)
            {
                var id = op.CommentId!.Value;
                if (id <= 0)
                {
                    continue;
                }

                if (Find(id) is { } current && string.Equals(current.Body, op.Body, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            plan.Add(op);
        }

        return plan;
    }

    /// <summary>
    /// Record a write that landed: an update replaces the stored body in place, a create appends the
    /// new comment so the next <see cref="Plan"/> updates it instead of creating a second one.
    /// </summary>
    /// <param name="commentId">The comment the write landed on. A create whose id could not be read
    /// back records 0 — the body is still remembered (so nothing duplicates it) but no later write
    /// can target it.</param>
    public CommentLedger Record(CommentUpsert op, long commentId)
    {
        var next = new List<ExistingComment>(_comments.Count + 1);
        var replaced = false;
        foreach (var c in _comments)
        {
            if (commentId > 0 && c.Id == commentId)
            {
                next.Add(c with { Body = op.Body });
                replaced = true;
            }
            else
            {
                next.Add(c);
            }
        }

        if (!replaced)
        {
            next.Add(new ExistingComment(commentId, op.Body));
        }

        return new CommentLedger(next);
    }

    private ExistingComment? Find(long id) => _comments.FirstOrDefault(c => c.Id == id);
}
