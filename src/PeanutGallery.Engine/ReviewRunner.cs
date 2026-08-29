using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;

namespace PeanutGallery.Engine;

/// <summary>What happened to one persona's session this run.</summary>
public enum PersonaOutcome
{
    /// <summary>Nothing changed since this persona last reviewed — its comment is left untouched.</summary>
    Unchanged,

    /// <summary>The model ran and produced a (possibly empty) set of findings.</summary>
    Reviewed,

    /// <summary>The model call failed; the session is preserved and a failure comment is rendered.</summary>
    Failed,
}

/// <summary>
/// The observability facts for one persona's run — the model it used, wall-clock latency, and (on
/// failure) the reason. Grouped into its own value so <see cref="PersonaResult"/> stays readable and
/// a future observability field doesn't lengthen its positional constructor. Never affects what gets
/// posted; feeds the per-run Job Summary + annotations (see <see cref="RunSummary"/>).
/// </summary>
/// <param name="Usage">Tokens the review itself cost — the turn's model call plus any repair re-ask.</param>
/// <param name="VerifyUsage">Tokens the adversarial pass cost, kept SEPARATE from
/// <paramref name="Usage"/> on purpose. Verification re-sends the whole review request (see
/// <see cref="SessionPlanner.Verify"/>), so it is the one line item most likely to dominate a turn -
/// and "is it worth what it costs" is unanswerable if the two are summed before anyone sees them.</param>
public sealed record PersonaObservability(
    string Model, TimeSpan Elapsed, string? FailureReason,
    ModelUsage? Usage = null, ModelUsage? VerifyUsage = null, int Attempts = 0,
    FailureClass? FailureKind = null)
{
    /// <summary>Everything this persona spent this run. A pass that was never recorded contributes
    /// <see cref="ModelUsage.Unreported"/>, not a reported zero — otherwise a persona nobody metered
    /// would total as "we know: free".</summary>
    public ModelUsage TotalUsage =>
        (Usage ?? ModelUsage.Unreported) + (VerifyUsage ?? ModelUsage.Unreported);
}

/// <summary>
/// The raw material a panel comment needs from one persona. Null when the persona did not report,
/// which the panel comment then names rather than quietly omitting.
/// </summary>
public sealed record PersonaContribution(
    Persona Persona,
    ReviewSession NextSession,
    IReadOnlyList<Finding> Posted,
    IReadOnlyList<string> Resolved,
    IReadOnlyList<string> Withdrawn,
    int Suppressed,
    IReadOnlyList<RefutedFinding> Refuted);

/// <summary>One persona's result: the rendered comment body (null when unchanged) + its observability.</summary>
/// <param name="Prior">The session this persona started the run with, carried on the two outcomes that
/// produce no <see cref="Contribution"/> (<see cref="PersonaOutcome.Unchanged"/> and
/// <see cref="PersonaOutcome.Failed"/>). Panel mode re-renders one shared comment from scratch every
/// run, so it needs the standing review of a persona that did not report this turn - without it that
/// persona's findings vanish from the comment while still sitting in the state blob.</param>
public sealed record PersonaResult(
    string PersonaId, string PersonaName, PersonaOutcome Outcome,
    int FindingCount, string? Body, PersonaObservability Observability,
    PersonaContribution? Contribution = null,
    ReviewSession? Prior = null);

/// <summary>The whole run's per-persona results, plus the derived bodies to post.</summary>
public sealed record ReviewRunResult(IReadOnlyList<PersonaResult> Personas, string? PanelBody = null)
{
    /// <summary>
    /// The comment bodies to upsert. In panel mode that is the single synthesised comment; in
    /// per-persona mode it is one body per persona that had something to say.
    /// </summary>
    public IReadOnlyList<string> RenderedBodies { get; } = PanelBody is not null
        ? [PanelBody]
        : Personas.Where(p => p.Body is not null).Select(p => p.Body!).ToList();

    public int Unchanged { get; } = Personas.Count(p => p.Outcome == PersonaOutcome.Unchanged);
}

/// <summary>
/// The inputs one PR review run needs. The delegates are the only IO seams the runner touches —
/// the caller supplies them, so the runner never depends on a concrete GitHub client. Everything
/// else is pure core state the caller has already anchored (head SHA, existing comments, config
/// panel).
/// </summary>
/// <param name="DeltaSource">Where the diff comes from (GitHub compare/full-diff, or an offline file).</param>
/// <param name="Baseline">
/// The pull request's CUMULATIVE diff — merge base → <paramref name="HeadSha"/> — which is what lets
/// a continued turn tell code an earlier turn of this same PR introduced from established API
/// (#178). Resolved once per run by the shell, because it is persona-independent: every persona's
/// delta differs, the PR's own baseline does not. Null (or a fetch that failed) degrades to
/// <see cref="OwnRemovals.Unknown"/> and the prompt says nothing, which is the pre-#178 behaviour.
/// </param>
/// <param name="Publish">
/// Where a rendered body goes THE MOMENT it is ready, instead of waiting for the whole panel
/// (#116). Called once per persona as it lands, serialized single-writer, with the bodies to
/// upsert now: in per-persona mode that persona's comment, in panel mode the shared comment
/// re-rendered from everyone who has reported so far. Null = the caller only wants the end-of-run
/// result (preview, dry run, the desktop's confirm-then-post flow), which is the old behaviour.
/// The caller's implementation must be idempotent across calls — see <see cref="CommentLedger"/>.
/// </param>
/// <param name="PersonaBudget">
/// The overall wall-clock ONE persona's turn may take: every model call it makes (review, shrink
/// ladder, JSON repair, adversarial pass) and every retry inside them, sharing one deadline.
/// Exceeding it fails that persona (its comment is preserved, it retries next push) and never
/// touches its siblings. Defaults to <see cref="ReviewBudget.DefaultSeconds"/>.
/// </param>
public sealed record ReviewRunRequest(
    PeanutConfig Config,
    string ConfigRepo,
    string HeadSha,
    IReadOnlyList<ExistingComment> Existing,
    Func<ReviewSession, CancellationToken, Task<Diff>> DeltaSource,
    IReviewer Reviewer,
    DiffFilterPolicy? Filter = null,
    bool AllowUnchangedSkip = true,
    PullRequestIntent? Intent = null,
    Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<FileContext>>>? ContextSource = null,
    int ContextBudgetBytes = ContextBudget.DefaultBudgetBytes,
    RepoConventions? Conventions = null,
    IPanelPlanner? PanelPlanner = null,
    Func<IReadOnlyList<string>, CancellationToken, Task>? Publish = null,
    TimeSpan? PersonaBudget = null,
    Diff? Baseline = null);

/// <summary>
/// The reusable review-orchestration shell: fold a config panel + an anchored PR (head SHA +
/// existing comments) into one advanced, rendered comment per assigned persona. It composes the
/// pure core folds — <see cref="ReviewPlanner.Plan"/>, <see cref="SessionCodec"/>,
/// <see cref="SessionPlanner.Advance"/>, <see cref="SessionUpdateParser"/>,
/// <see cref="SessionCommentRenderer"/> — around the async <see cref="IReviewer"/> port, so every
/// shell (CLI, desktop GUI, future server) runs the *identical* review and can never disagree
/// about what a review is. Posting is deliberately left to the caller: this returns the rendered
/// bodies; the caller decides whether to preview them or apply <see cref="CommentSync.Plan"/>.
///
/// Total at the persona seam: a model failure becomes a failure comment for that persona (the
/// session is preserved), never a throw that sinks the whole fan-out. Cancellation propagates.
/// </summary>
public static class ReviewRunner
{
    public static async Task<ReviewRunResult> RunAsync(
        ReviewRunRequest req, Action<string>? log = null, CancellationToken ct = default)
    {
        var pairs = ReviewPlanner.Plan(req.Config, req.ConfigRepo, Diff.Empty);
        var policy = req.Filter ?? DiffFilterPolicy.Default;
        var maxCommentId = req.Existing.Count == 0 ? 0L : req.Existing.Max(c => c.Id);

        // The overall deadline for one turn's worth of work (#117). Applied around whole turns,
        // because the per-call budget underneath it is spent per attempt AND per call: a persona
        // turn issues several calls (review, shrink ladder, repair re-ask, adversarial pass) and
        // each of those retries, so nothing downstream bounds the quantity an operator actually
        // set. The orchestrator and the reconciler get the same ceiling for the same reason - the
        // run that motivated this spent 278s in the orchestrator before a single persona started.
        var budget = req.PersonaBudget ?? TimeSpan.FromSeconds(ReviewBudget.DefaultSeconds);

        // Who reviews. In fixed mode this is just the configured panel; in a dynamic mode the
        // panel is planned once at PR-open and pinned, then reused verbatim on every later turn.
        var panelSession = FindPanelSession(req.Existing);
        var (panel, pin) = await ResolvePanelAsync(req, pairs, policy, budget, log, ct);
        if (panel.Count == 0)
        {
            return new ReviewRunResult([]);
        }

        var repoTarget = pairs.Count > 0 ? pairs[0].Repo : req.Config.FindRepo(req.ConfigRepo);
        if (repoTarget is null)
        {
            return new ReviewRunResult([]);
        }

        // The pin rides EVERY rendered body, not just the turn that created it: a comment update
        // replaces the whole body, so writing the pin once would lose it on the next push and the
        // orchestrator would run again - exactly the churn freezing exists to prevent.
        string WithPin(string body) => pin is null ? body : PanelCodec.Embed(body, pin);

        // Each persona's starting session, resolved once up front. Hoisted out of the fan-out
        // because the conversation branch below has to know whether ANYONE still needs to look at
        // the code before it can decide that a comment is all this run has to deal with. Pure
        // lookups over comments already fetched - no IO.
        var priors = panel.ToDictionary(
            p => p.Id,
            // Panel state first, then the per-persona comment. That ordering IS the migration:
            // a PR reviewed before panel mode has no panel blob, so its existing per-persona
            // comments still supply each session and no history is dropped on the switch.
            // Find, not For: For() manufactures a fresh session for a persona the blob does not
            // carry, which would satisfy this ?? chain and skip the legacy fallback - dropping the
            // history of a persona that has a per-persona comment but is missing from the blob.
            p => panelSession?.Find(p.Id)
                ?? (FindExistingBody(req.Existing, p.Id) is { } body ? SessionCodec.Extract(body) : null)
                ?? ReviewSession.Initial,
            StringComparer.OrdinalIgnoreCase);

        var conversation = req.Config.Conversation ?? ConversationPolicy.Default;

        // A comment-only turn may not need the panel at all. Tried before the fan-out because the
        // whole point is to not pay for one; falls through (returns null) whenever the code itself
        // still needs reviewing, which always outranks bookkeeping.
        var reconciled = await ReconcileAsync(
            req, panel, priors, repoTarget, conversation, panelSession, pin, budget, log, ct);
        if (reconciled is not null)
        {
            return reconciled;
        }

        var isPanel = (req.Config.Comment ?? CommentMode.PerPersona) == CommentMode.Panel;

        // Post each persona's work AS IT LANDS (#116). Batching every write to the end of the run
        // meant one hung persona could take three finished reviews down with it when the job hit
        // its timeout backstop - 13 findings lost, twice, in one observed run.
        var progress = new ProgressPublisher(req.Publish, RenderProgress, log);

        var results = await Task.WhenAll(panel.Select(async persona =>
        {
            var result = await BoundedReviewAsync(persona);
            await progress.ReportAsync(result, ct);
            return result;
        }));

        // Panel mode collapses the fan-out into one comment. The personas still ran independently
        // and still hold independent sessions - only the reader-facing surface is unified.
        if (isPanel)
        {
            return BuildPanelResult(results, panel, panelSession, pin, req.HeadSha, log, priors: priors);
        }

        return new ReviewRunResult(results);

        // What to publish when `latest` has just finished and `done` is everyone who has finished
        // so far. Pure (it only folds already-computed results) and called under the publisher's
        // lock, so concurrent completions can never render a panel comment out of order.
        IReadOnlyList<string> RenderProgress(IReadOnlyList<PersonaResult> done, PersonaResult latest)
        {
            if (!isPanel)
            {
                return latest.Body is null ? [] : [latest.Body];
            }

            var pending = panel.Select(p => p.Id)
                .Where(id => !done.Any(d => string.Equals(d.PersonaId, id, StringComparison.OrdinalIgnoreCase)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Everyone is in: let the caller's end-of-run post carry the finished panel rather than
            // writing the same comment twice in a row.
            if (pending.Count == 0)
            {
                return [];
            }

            // log: null - a progress render is not a run event, and its "merged N duplicates" line
            // would be re-emitted (with a different N) on every persona that lands.
            var partial = BuildPanelResult(
                done, panel, panelSession, pin, req.HeadSha, log: null, priors: priors, pending: pending);
            return partial.PanelBody is null ? [] : [partial.PanelBody];
        }

        // One persona's turn under one deadline. The whole turn is the unit, not the call.
        async Task<PersonaResult> BoundedReviewAsync(Persona persona)
        {
            var prior = priors[persona.Id];
            var sw = Stopwatch.StartNew();
            try
            {
                return await TimeBox.RunAsync(token => ReviewOneAsync(persona, prior, token), budget, ct);
            }
            catch (TimeoutException)
            {
                sw.Stop();
                // Our OWN deadline, not the caller's teardown - TimeBox has already told those
                // apart. So this is a failed persona (comment preserved, retried on the next push),
                // exactly like a provider error, and never a throw that sinks its siblings' work.
                var reason = $"the review did not finish within its {budget.TotalSeconds:F0}s budget";
                var model = persona.Model.ToString();
                log?.Invoke($"[{persona.Id}] FAILED in {sw.Elapsed.TotalSeconds:F0}s: {reason} " +
                    $"[model={model}, {nameof(TimeoutException)}]");
                var visible = SessionCommentRenderer.RenderFailure(persona, prior, req.HeadSha, reason);
                return new PersonaResult(persona.Id, persona.Name, PersonaOutcome.Failed, 0,
                    WithPin(SessionCodec.Embed(visible, prior)),
                    // We caught the TimeoutException, so the failure kind is known structurally — the
                    // metrics fold reads this rather than re-deriving it from the reason prose, so the
                    // pure core never has to match a message this shell layer worded (#123 review).
                    new PersonaObservability(model, sw.Elapsed, reason, FailureKind: FailureClass.Timeout),
                    Contribution: null, Prior: prior);
            }
        }

        async Task<PersonaResult> ReviewOneAsync(Persona persona, ReviewSession prior, CancellationToken token)
        {
            var repo = repoTarget;

            // New human comments since this persona last reviewed, narrowed to the ones actually
            // addressed to the panel. The mention gate is what stops two humans talking to each
            // other in the PR thread from waking every reviewer.
            var newComments = ConversationGate.Addressed(NewComments(req, prior.LastSeenCommentId), conversation);

            // Nothing new (same head AND nothing addressed to us) -> leave the comment untouched.
            // In Off mode a comment never defeats the skip; it is still fed to a turn the code
            // itself earns, because reading it there is free.
            var commentsCanTrigger = conversation.Mode != ConversationMode.Off;
            var model = persona.Model.ToString();
            if (req.AllowUnchangedSkip && !prior.IsFirstTurn
                && prior.LastReviewedSha == req.HeadSha
                && (!commentsCanTrigger || newComments.Count == 0))
            {
                log?.Invoke($"[{persona.Id}] unchanged (already reviewed {Sha.Short(req.HeadSha)})");
                return new PersonaResult(
                    persona.Id, persona.Name, PersonaOutcome.Unchanged, 0, null,
                    new PersonaObservability(model, TimeSpan.Zero, null), Contribution: null, Prior: prior);
            }

            var rawDelta = await req.DeltaSource(prior, token);
            var filtered = DiffFilter.Apply(rawDelta, policy);

            // File context is for the diff tier, which cannot go read the file itself, and only on
            // the first turn - later turns review a delta against a session that already read the
            // code. Agent-tier personas have RepoTools and don't need the tokens. The filtered diff
            // goes in as well as the paths: it carries the hunk locations ContextBudget windows a
            // too-large file around, instead of dropping that file out of the prompt entirely.
            ContextSelection? context = null;
            if (req.ContextSource is not null && prior.IsFirstTurn && persona.Tier == ReviewTier.Diff
                && filtered.Diff.Files.Count > 0)
            {
                var paths = filtered.Diff.Files.Select(f => f.Path).ToList();
                context = ContextBudget.Fit(
                    await req.ContextSource(paths, token), req.ContextBudgetBytes, filtered.Diff);
            }

            // Which of this delta's removals this pull request had itself added earlier (#178).
            // First turn only ever sees the whole PR, so it has nothing to be confused about.
            //
            // Derived from the RAW delta, not the filtered one, and narrowed afterwards. The
            // arithmetic telescopes only over a complete diff: DiffFilter drops whole files (binary,
            // ignore-glob, and largest-first once over budget), and the shrink ladder below drops
            // more, so a line the delta removes from a kept file and adds to a dropped one would
            // lose its cancelling addition and be MANUFACTURED as this branch's own work. Narrowing
            // the answer to the files the model was shown is the safe half of the same operation:
            // it drops claims where filtering the input invents them.
            var ownInWholeDelta = prior.IsFirstTurn
                ? OwnRemovals.Unknown
                : OwnRemovals.Of(rawDelta, req.Baseline);
            OwnRemovals Own(FilteredDiff fd) => ownInWholeDelta.OnlyIn(fd.Diff);

            // Rebuilds the turn's request from a (possibly reduced) diff + context. Called again
            // with a smaller shape when the model returns an empty completion on an over-large prompt.
            ReviewRequest Build(FilteredDiff fd, ContextSelection? ctx) => SessionPlanner.Advance(
                persona, repo, prior, fd.Diff, req.HeadSha, fd.Omitted, newComments,
                req.Intent, ctx, req.Conventions, Own(fd));

            var request = Build(filtered, context);
            var kb = filtered.Diff.Raw.Length / 1024;
            log?.Invoke($"[{persona.Id}] reviewing via {model} " +
                $"({filtered.Diff.Files.Count} file(s)/{kb}KB, {filtered.Omitted.Count} omitted, {newComments.Count} new comment(s))...");

            var sw = Stopwatch.StartNew();
            // Both declared outside the try so the catch reports what was already spent. Scoping
            // verifyUsage to the try would silently drop a completed (and expensive) adversarial
            // pass whenever a later step in the turn threw.
            var usage = ModelUsage.Unreported;
            var verifyUsage = ModelUsage.Unreported;
            // Total model calls this persona issued on the review path (each CompleteAsync counts its
            // own retry re-issues) — the multi-call-recovered-vs-exhausted signal the metrics ledger
            // reads. Counted on BOTH success and throw: an exhausted retry throws a ModelCallException
            // carrying its count, and that failed review is exactly the case worth measuring, so it
            // must not be recorded as zero calls. Usage is added by the caller on success only (a
            // throw reported none). The manual "unparseable after repair" throw below is not a model
            // call, so it adds nothing — its calls were already counted here.
            var attempts = 0;
            async Task<ModelReply> Call(ReviewRequest r)
            {
                try
                {
                    var reply = await req.Reviewer.CompleteAsync(r, repo.Path, token);
                    attempts += reply.Attempts;
                    return reply;
                }
                catch (Exception e)
                {
                    attempts += e is ModelCallException mce ? mce.Attempts : 1;
                    throw;
                }
            }

            try
            {
                var reply = await Call(request);
                var text = reply.Text;
                usage += reply.Usage;
                var parsed = SessionUpdateParser.ParseResult(text);

                // A truncated reply (finish_reason:length) hit the output-token cap, so the JSON is
                // cut off. Neither the shrink ladder (smaller PROMPT) nor the JSON repair (re-ask,
                // same cap) can fit a too-long REPLY into the cap, so fail cleanly with a structural
                // Truncated kind rather than burning two more calls on a lost cause. Visible in the
                // metrics so the cap (PG_MAX_OUTPUT_TOKENS) can be tuned if real reviews hit it.
                if (reply.Truncated && !parsed.Parsed)
                {
                    sw.Stop();
                    var reason = "the reply hit the model's output-token cap and was truncated " +
                        "(the review was too long, or the model was looping)";
                    log?.Invoke($"[{persona.Id}] FAILED in {sw.Elapsed.TotalSeconds:F0}s: {reason} [model={model}]");
                    var failBody = SessionCommentRenderer.RenderFailure(persona, prior, req.HeadSha, reason);
                    return new PersonaResult(persona.Id, persona.Name, PersonaOutcome.Failed, 0,
                        WithPin(SessionCodec.Embed(failBody, prior)),
                        new PersonaObservability(model, sw.Elapsed, reason, usage, ModelUsage.Unreported,
                            attempts, FailureClass.Truncated),
                        Contribution: null, Prior: prior);
                }

                // An empty completion is the signature of a prompt the model could not answer -
                // typically one too large: it spends its output budget on reasoning and returns no
                // content. Retry with progressively smaller prompts (drop the whole-file context,
                // then trim the diff) before the JSON repair, turning a total review loss into a
                // smaller-but-real review. Only for an EMPTY reply: a malformed-but-present reply is
                // a format problem the JSON re-ask handles, and shrinking it would just burn calls.
                if (!parsed.Parsed && parsed.WasEmpty)
                {
                    foreach (var shape in PromptReduction.Ladder(context is not null, filtered.Diff.Raw.Length))
                    {
                        var fd = shape.DiffMaxBytes is int cap
                            ? DiffFilter.Apply(rawDelta, policy with { MaxBytes = cap })
                            : filtered;
                        var ctx = shape.IncludeContext ? context : null;
                        request = Build(fd, ctx);   // adopt as the current prompt so repair/verify use it too
                        var rkb = fd.Diff.Raw.Length / 1024;
                        log?.Invoke($"[{persona.Id}] empty reply; retrying smaller " +
                            $"({fd.Diff.Files.Count} file(s)/{rkb}KB, context {(ctx is null ? "off" : "on")}, {fd.Omitted.Count} omitted)...");
                        var retry = await Call(request);
                        text = retry.Text;
                        usage += retry.Usage;
                        parsed = SessionUpdateParser.ParseResult(text);

                        // Stop on success, or on a non-empty-but-malformed reply the JSON re-ask below
                        // can still fix - only keep shrinking while the model keeps returning nothing.
                        if (parsed.Parsed || !parsed.WasEmpty)
                        {
                            break;
                        }
                    }
                }

                // One corrective re-ask before giving up. A reply we cannot read must never be
                // posted as a clean review (that is a silent false negative), so the only two
                // acceptable outcomes are "understood" or "failed and retried next push".
                if (!parsed.Parsed)
                {
                    log?.Invoke($"[{persona.Id}] unreadable reply ({parsed.Reason}); re-asking for JSON...");
                    // Counted into the review's own usage, not hidden: a model that needs repairing
                    // costs roughly double, and that is exactly the kind of thing worth seeing.
                    var repair = await Call(SessionPlanner.Repair(request, text));
                    text = repair.Text;
                    usage += repair.Usage;
                    parsed = SessionUpdateParser.ParseResult(text);
                }

                if (!parsed.Parsed)
                {
                    throw new InvalidOperationException(
                        $"the model's reply could not be parsed after a repair attempt: {parsed.Reason}");
                }

                sw.Stop();
                var update = parsed.Update;

                // Gate on the reviewer's own stated certainty. The session keeps the FULL set:
                // it is the model's working state, and dropping a finding from it would just make
                // the model re-raise it next turn. Only the human-facing comment is filtered - and
                // the drop is disclosed there, never silent.
                var gate = ConfidenceGate.Apply(update.Findings, ConfidenceGate.ThresholdFor(persona, req.Config));

                // The adversarial pass: make the reviewer argue against what survived the gate.
                // Ordered after gating so the expensive call only ever runs on findings that were
                // going to be posted, and skipped entirely when there is nothing to refute - which
                // makes a clean review cost exactly what it did before.
                VerificationResult? verified = null;
                if ((req.Config.Verify ?? true) && gate.Kept.Count > 0)
                {
                    (verified, verifyUsage) = await VerifyAsync(
                        req, persona, request, gate.Kept, repo.Path, log, token);
                }

                var visible = SessionCommentRenderer.Render(persona, prior, update, req.HeadSha, gate, verified);
                var posted = verified?.Upheld ?? gate.Kept;

                // Remember what came off the board this turn. The session keeps the full finding
                // set (it is the model's working state), so without this memory the model never
                // learns a finding was dropped and re-emits it on every push.
                var droppedNow = gate.Suppressed.Select(f => f.Title)
                    .Concat((verified?.Refuted ?? []).Select(r => r.Title))
                    .ToList();

                var next = new ReviewSession(
                    req.HeadSha,
                    prior.Turn + 1,
                    string.IsNullOrWhiteSpace(update.Summary) ? prior.Summary : update.Summary,
                    update.Findings,
                    maxCommentId,
                    DroppedMemory.Next(prior.DroppedTitles, droppedNow, posted));
                var suppressedNote = gate.Suppressed.Count > 0 ? $", {gate.Suppressed.Count} low-confidence suppressed" : string.Empty;
                var refutedNote = verified is { Refuted.Count: > 0 } ? $", {verified.Refuted.Count} refuted" : string.Empty;
                var total = usage + verifyUsage;
                var tokenNote = total.IsUnreported
                    ? string.Empty
                    : $", {total.InputTokens} in / {total.OutputTokens} out tokens{CacheNote(total)}";
                log?.Invoke($"[{persona.Id}] done ({posted.Count} finding(s){suppressedNote}{refutedNote}) in {sw.Elapsed.TotalSeconds:F0}s{tokenNote}");
                return new PersonaResult(persona.Id, persona.Name, PersonaOutcome.Reviewed,
                    posted.Count, WithPin(SessionCodec.Embed(visible, next)),
                    new PersonaObservability(model, sw.Elapsed, null, usage, verifyUsage, attempts),
                    new PersonaContribution(
                        persona, next, posted, update.Resolved, update.Withdrawn,
                        gate.Suppressed.Count, verified?.Refuted ?? []));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                sw.Stop();
                // Don't advance the session on failure; retry on the next push.
                var visible = SessionCommentRenderer.RenderFailure(persona, prior, req.HeadSha, e.Message);
                // Structured failure line: enough to triage a flake (slow route vs big diff vs outage)
                // from the run log alone, without re-running or reading the overwritten comment.
                log?.Invoke($"[{persona.Id}] FAILED in {sw.Elapsed.TotalSeconds:F0}s: {e.Message} " +
                    $"[model={model}, {filtered.Diff.Files.Count} file(s)/{kb}KB, {e.GetType().Name}]");
                // A per-CALL TimeBox exhaustion (issue #133's split budget) surfaces here as a
                // TimeoutException, or a ModelCallException wrapping one once retries were spent. We
                // caught it, so the failure kind is known STRUCTURALLY - the metrics fold reads this
                // rather than re-deriving it from the reason prose, so the pure core never has to
                // match a message this shell layer worded (#123 review; same contract as the
                // per-persona TimeoutException catch above). Other exceptions keep the text fallback.
                // Same contract for a reply the SDK could not map (#158): we hold the exception, so
                // its TYPE settles the class and the core is spared matching an SDK message.
                var kind = IsTimeoutFailure(e) ? FailureClass.Timeout
                    : TransientFailure.IsMalformedResponse(e) ? FailureClass.MalformedResponse
                    : (FailureClass?)null;
                // Report what the turn had already ACCOUNTED FOR before it failed - a degraded turn
                // is not a free one. Note the limit: a call that throws never reaches its own usage
                // assignment, so a failure inside CompleteAsync itself contributes nothing here.
                // What this does capture is the turn that called successfully (and perhaps verified)
                // and then fell over afterwards, which would otherwise vanish from the accounting.
                return new PersonaResult(persona.Id, persona.Name, PersonaOutcome.Failed, 0,
                    WithPin(SessionCodec.Embed(visible, prior)),
                    new PersonaObservability(model, sw.Elapsed, e.Message, usage, verifyUsage, attempts, kind),
                    Contribution: null, Prior: prior);
            }
        }
    }

    /// <summary>
    /// The shell's single-writer window onto the PR thread: hands each persona's rendered body to
    /// the caller's <see cref="ReviewRunRequest.Publish"/> the moment it exists, instead of holding
    /// every write until the whole panel is done (#116).
    ///
    /// <para>Serialized on purpose. Rendering happens INSIDE the lock, so two personas finishing at
    /// once cannot produce a comment showing the earlier state after one showing the later, and the
    /// caller's ledger of what it has already posted is only ever touched by one writer.</para>
    ///
    /// <para>Total: a failed intermediate post is logged and swallowed. Publishing early is an
    /// improvement on the end-of-run write, never a new way for the run to die - and the end-of-run
    /// write still carries everything.</para>
    /// </summary>
    private sealed class ProgressPublisher(
        Func<IReadOnlyList<string>, CancellationToken, Task>? publish,
        Func<IReadOnlyList<PersonaResult>, PersonaResult, IReadOnlyList<string>> render,
        Action<string>? log)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly List<PersonaResult> _done = [];

        public async Task ReportAsync(PersonaResult result, CancellationToken ct)
        {
            if (publish is null)
            {
                return;
            }

            await _gate.WaitAsync(ct);
            try
            {
                _done.Add(result);
                var bodies = render(_done, result);
                if (bodies.Count > 0)
                {
                    await publish(bodies, ct);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log?.Invoke($"[publish] could not post {result.PersonaId}'s comment early " +
                    $"({e.Message}); the end-of-run write still carries it");
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>
    /// New human comments past a watermark - excludes bots, anything we wrote, and anyone who does
    /// not speak for the repo. That last filter is the point: the trigger guard
    /// (<see cref="Core.GitHubEventGuard"/>) only asks who caused THIS run, so on a push-triggered
    /// turn every comment on the thread arrives here regardless of who left it - and this feeds the
    /// reconciler, which withdraws and resolves findings.
    /// </summary>
    // A failure caused by a per-call TimeBox deadline: the raw TimeoutException (one attempt) or a
    // ModelCallException wrapping one once retries were exhausted (RetryingModelCall.Enrich). Lets the
    // shell tag FailureClass.Timeout structurally instead of the core parsing the message text (#133).
    private static bool IsTimeoutFailure(Exception e) =>
        e is TimeoutException || (e is ModelCallException && e.InnerException is TimeoutException);

    /// <summary>Log-line suffix for a cache hit, empty when none was reported - keeps the common
    /// (no-cache) case from cluttering every token-count line with a trailing " (0 cached)".</summary>
    private static string CacheNote(ModelUsage usage) =>
        usage.CachedInputTokens > 0 ? $" ({usage.CachedInputTokens} cached)" : "";

    private static IReadOnlyList<AuthorComment> NewComments(ReviewRunRequest req, long since) =>
        req.Existing
            .Where(c => c.Id > since && CommentTrust.MayDirectPanel(c)
                && !c.Body.Contains("<!-- peanut-gallery:", StringComparison.Ordinal))
            .Select(c => new AuthorComment(c.Author, c.Body))
            .ToList();

    /// <summary>
    /// The conversation turn: when the code has already been reviewed at this head and all that is
    /// new is a human talking about an existing finding, spend ONE call deciding what comes off the
    /// board instead of N full persona turns plus their verification passes.
    ///
    /// <para>Returns null to fall through to the normal fan-out, which is the answer whenever the
    /// code itself still needs looking at. A push always outranks bookkeeping: if the head moved,
    /// the panel reviews it and reads the comments in the same turn, and paying separately for both
    /// would be worse than the behaviour this replaces.</para>
    ///
    /// <para>Total, like every other seam here: a throw or an unreadable reply leaves the board
    /// exactly as it was and falls through, because this pass only ever REMOVES findings and a
    /// failure must never remove one.</para>
    /// </summary>
    private static async Task<ReviewRunResult?> ReconcileAsync(
        ReviewRunRequest req, IReadOnlyList<Persona> panel, IReadOnlyDictionary<string, ReviewSession> priors,
        RepoTarget repo, ConversationPolicy conversation, PanelSession? panelSession, PinnedPanel? pin,
        TimeSpan budget, Action<string>? log, CancellationToken ct)
    {
        if (conversation.Mode != ConversationMode.Reconcile || !req.AllowUnchangedSkip)
        {
            return null;
        }

        // One reconciler re-rendering N separate per-persona comments from sessions it did not
        // produce is a different feature; ConfigValidation flags the combination, and here it
        // degrades to the full fan-out rather than quietly doing nothing.
        if ((req.Config.Comment ?? CommentMode.PerPersona) != CommentMode.Panel)
        {
            log?.Invoke("[conversation] reconcile needs panel comment mode; using the full panel");
            return null;
        }

        // Anyone who has not reviewed THIS head still owes a review of the code.
        if (panel.Any(p => priors[p.Id].IsFirstTurn || priors[p.Id].LastReviewedSha != req.HeadSha))
        {
            return null;
        }

        // The board is shared, so the watermark is the lowest any member has seen - a comment one
        // persona already ingested is still new to the panel if another has not.
        var since = panel.Min(p => priors[p.Id].LastSeenCommentId);
        var addressed = ConversationGate.Addressed(NewComments(req, since), conversation);
        if (addressed.Count == 0)
        {
            return null; // nothing to reconcile; the fan-out will skip everyone as unchanged
        }

        var board = panel
            .Select(p => new PersonaFindings(p.Id, p.Lens, DroppedMemory.Standing(priors[p.Id])))
            .Where(c => c.Findings.Count > 0)
            .ToList();
        if (board.Count == 0)
        {
            log?.Invoke("[conversation] nothing on the board to reconcile; skipping");
            return null;
        }

        var model = conversation.Model ?? req.Config.PersonaModel ?? panel[0].Model;
        var maxCommentId = req.Existing.Count == 0 ? 0L : req.Existing.Max(c => c.Id);
        var sw = Stopwatch.StartNew();
        ReconcileVerdicts verdicts;
        var usage = ModelUsage.Unreported;
        try
        {
            // Bounded like a persona turn: this is one model call on the critical path, and its
            // failure mode is benign (the board is left exactly as it was), so a deadline here
            // costs nothing and closes the last unbounded call in the run (#117).
            var reply = await TimeBox.RunAsync(
                token => req.Reviewer.CompleteAsync(
                    SessionPlanner.Reconcile(model, repo, board, addressed), repo.Path, token),
                budget, ct);
            usage = reply.Usage;
            verdicts = ReconcileParser.Parse(reply.Text);
            sw.Stop();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            sw.Stop();
            log?.Invoke($"[conversation] reconciliation failed after {sw.Elapsed.TotalSeconds:F0}s " +
                $"({e.Message}); the board is unchanged");
            verdicts = ReconcileVerdicts.Empty;
        }

        var results = new List<PersonaResult>(panel.Count);
        var removed = new List<string>();
        foreach (var persona in panel)
        {
            var outcome = Reconciliation.Apply(priors[persona.Id], verdicts, maxCommentId);
            removed.AddRange(outcome.Removed);
            results.Add(new PersonaResult(
                persona.Id, persona.Name, PersonaOutcome.Unchanged, 0, null,
                new PersonaObservability(persona.Model.ToString(), TimeSpan.Zero, null),
                Contribution: null, Prior: outcome.Session));
        }

        var tokenNote = usage.IsUnreported
            ? string.Empty
            : $", {usage.InputTokens} in / {usage.OutputTokens} out tokens{CacheNote(usage)}";
        log?.Invoke($"[conversation] reconciled {addressed.Count} comment(s) in one call: " +
            $"{removed.Count} finding(s) came off the board in {sw.Elapsed.TotalSeconds:F0}s{tokenNote}");

        return BuildPanelResult(
            results, panel, panelSession, pin, req.HeadSha, log,
            withdrawn: verdicts.Withdrawn, resolved: verdicts.Resolved, reconciled: true);
    }

    /// <summary>
    /// Run the adversarial pass. Total by design: if the skeptic call throws or answers with
    /// nothing readable, every finding stands. Verification is an enhancement, so a failure in it
    /// costs a little precision - it must never cost a real finding, and it must never turn a
    /// working review into a failed one.
    /// </summary>
    private static async Task<(VerificationResult Result, ModelUsage Usage)> VerifyAsync(
        ReviewRunRequest req, Persona persona, ReviewRequest original, IReadOnlyList<Finding> findings,
        string repoPath, Action<string>? log, CancellationToken ct)
    {
        // Timed separately from the review it follows. Verification is an independent call with
        // its own provider risk, and on a large finding set it can dominate the turn - folding it
        // into the review's elapsed time would report "the review was slow" and hide which half.
        var sw = Stopwatch.StartNew();
        try
        {
            var reply = await req.Reviewer.CompleteAsync(
                SessionPlanner.Verify(original, findings), repoPath, ct);
            var result = Verification.Apply(findings, VerdictParser.Parse(reply.Text));
            sw.Stop();
            var tokenNote = reply.Usage.IsUnreported
                ? string.Empty
                : $" ({reply.Usage.InputTokens} in / {reply.Usage.OutputTokens} out tokens{CacheNote(reply.Usage)})";
            log?.Invoke($"[{persona.Id}] adversarial pass refuted {result.Refuted.Count} of {findings.Count} " +
                $"in {sw.Elapsed.TotalSeconds:F0}s{tokenNote}");
            return (result, reply.Usage);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            sw.Stop();
            log?.Invoke($"[{persona.Id}] adversarial pass failed after {sw.Elapsed.TotalSeconds:F0}s " +
                $"({e.Message}); keeping all findings");
            // Unreported, not Zero: the call threw, so there is no provider accounting to report.
            // A reported zero here would render as "0 / 0" instead of "—" and - because + treats
            // reported as sticky - would make one persona's failed verify vouch for the whole run's
            // verify column, printing "the adversarial pass is 0% of it" as though we knew.
            return (new VerificationResult(findings, []), ModelUsage.Unreported);
        }
    }

    /// <summary>
    /// Decide who reviews, and what pin (if any) must ride this run's comments.
    ///
    /// <para>Fixed mode short-circuits to the configured panel: there the committed config is the
    /// source of truth every turn, and pinning would turn an operator's edit into a silent no-op.
    /// A dynamic mode looks for an existing pin FIRST, and only plans a panel when there is none -
    /// which is what makes the orchestrator run at most once per PR.</para>
    ///
    /// <para>Total by design. A missing planner, a failed plan, or a plan that survives no
    /// guardrails all fall back to the configured panel WITHOUT pinning, so the next push retries
    /// rather than freezing a fallback. Reviewing with nobody would render as a clean review, so
    /// it is never an outcome.</para>
    /// </summary>
    private static async Task<(IReadOnlyList<Persona> Panel, PinnedPanel? Pin)> ResolvePanelAsync(
        ReviewRunRequest req, IReadOnlyList<ReviewTask> pairs, DiffFilterPolicy policy, TimeSpan budget,
        Action<string>? log, CancellationToken ct)
    {
        var configured = pairs.Select(p => p.Persona).ToList();
        var mode = req.Config.Panel ?? PanelMode.Fixed;

        // Found by scanning every comment, not by persona id: on a later turn we do not yet know
        // the ids - they come FROM the pin. That circularity is why the marker is panel-scoped.
        var existingPin = FindPin(req.Existing);
        var decision = PanelResolution.Resolve(mode, existingPin, configured);

        if (decision.Source == PanelSource.Configured)
        {
            return (decision.Panel, null);
        }

        // Resolve only reports Pinned for a non-null pin, but the invariant lives in another file;
        // binding it here means a future change to Resolve cannot turn this into a null deref.
        if (decision.Source == PanelSource.Pinned && existingPin is not null)
        {
            // A pinned persona the committed config does not know was invented (or forged), so it
            // may not hold agent tier - that grants repo tools, and a pin bypasses PanelFence. A
            // persona the operator actually configured keeps whatever tier they chose.
            var configuredIds = configured.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var safe = decision.Panel
                .Select(p => p.Tier == ReviewTier.Diff || configuredIds.Contains(p.Id)
                    ? p
                    : p with { Tier = ReviewTier.Diff })
                .ToList();

            log?.Invoke($"[panel] reusing the panel pinned at {Sha.Short(existingPin.PinnedAtSha)}: " +
                $"{string.Join(", ", safe.Select(p => p.Id))}");
            return (safe, existingPin);
        }

        // Nothing pinned yet in a dynamic mode: plan one, once.
        var seed = mode == PanelMode.SeedAndAuto ? configured : [];
        IReadOnlyList<Persona> generated = [];
        if (req.PanelPlanner is not null)
        {
            // Total at this seam, like every other one here: the shipped planner catches its own
            // model failures, but IPanelPlanner is a public port and that is a contract on
            // implementations, not something this runner can assume. A throw must cost the panel,
            // never the review (#92).
            try
            {
                // Under the same budget as a persona turn: the orchestrator is one more model call
                // on the critical path, and an unbounded one spends the job's backstop before any
                // reviewer has started (#117).
                generated = await TimeBox.RunAsync(
                    async token =>
                    {
                        var planningDiff = DiffFilter.Apply(await req.DeltaSource(ReviewSession.Initial, token), policy).Diff;
                        return await req.PanelPlanner.PlanAsync(planningDiff, req.Conventions, seed, token);
                    },
                    budget, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log?.Invoke($"[panel] planner threw ({e.Message}); falling back to the configured panel");
            }
        }
        else
        {
            log?.Invoke("[panel] no orchestrator configured; falling back to the configured panel");
        }

        // Capped at the same bound PanelCodec.Extract clamps to on read, so the panel that reviews
        // this turn is the panel that survives the pin on every turn after it.
        var merged = PanelResolution.Merge(seed, generated, PanelFence.MaxPersonas);
        if (merged.Count == 0)
        {
            // Deliberately unpinned: a fallback is not a decision worth freezing for the PR's life.
            log?.Invoke("[panel] no panel could be planned; falling back to the configured panel");
            return (configured, null);
        }

        log?.Invoke($"[panel] pinning {merged.Count} reviewer(s) at {Sha.Short(req.HeadSha)}: " +
            $"{string.Join(", ", merged.Select(p => p.Id))}");
        return (merged, new PinnedPanel(merged, mode, req.HeadSha, req.Config.Orchestrator?.ToString()));
    }

    /// <summary>
    /// Fold the fan-out into the panel's single comment. Every persona still ran independently and
    /// still holds its own session; only the reader-facing surface is unified, and everything
    /// removed from it - merged duplicates, suppressions, refutations, personas that never
    /// reported - is disclosed there rather than silently missing.
    ///
    /// <para>The comment is the panel's STANDING state, not a changelog of this turn. That
    /// distinction is the whole of issue #102: the single comment is re-rendered from scratch every
    /// run, so a persona that did not report this turn - skipped as unchanged, or failed - used to
    /// contribute nothing and have its still-open findings erased from the comment while they sat
    /// intact in the hidden state blob. Observed live: a plain re-run of an already-successful run
    /// replaced seven findings with "No findings". So a non-reporting persona's standing review is
    /// carried here, and only what it had already taken off the board stays off.</para>
    /// </summary>
    /// <param name="priors">Each persona's starting session as the fan-out resolved it. Needed for a
    /// persona with no result yet (see <paramref name="pending"/>): the blob lookup alone cannot see a
    /// session that came from a legacy per-persona comment.</param>
    /// <param name="pending">Personas still reviewing, when this renders a PARTIAL panel mid-run
    /// (#116). They are neither reporting nor failed, and saying "review could not run" about a
    /// persona that is at that moment running would be a lie the next render silently retracts.</param>
    private static ReviewRunResult BuildPanelResult(
        IReadOnlyList<PersonaResult> results, IReadOnlyList<Persona> panel,
        PanelSession? priorSession, PinnedPanel? pin, string headSha, Action<string>? log,
        IReadOnlyList<string>? withdrawn = null, IReadOnlyList<string>? resolved = null,
        bool reconciled = false,
        IReadOnlyDictionary<string, ReviewSession>? priors = null,
        IReadOnlySet<string>? pending = null)
    {
        var contributions = new List<PersonaFindings>();
        var members = new List<PanelMember>();
        var sessions = new Dictionary<string, ReviewSession>(StringComparer.OrdinalIgnoreCase);
        // Seeded from a reconciliation when one ran, so a conversation turn's withdrawals are
        // disclosed the same way a review turn's are rather than the board just quietly shrinking.
        var resolvedTitles = new List<string>(resolved ?? []);
        var withdrawnTitles = new List<string>(withdrawn ?? []);
        var suppressed = 0;
        var refuted = new List<RefutedFinding>();
        var turn = 0;

        foreach (var persona in panel)
        {
            var result = results.FirstOrDefault(r => string.Equals(
                r.PersonaId, persona.Id, StringComparison.OrdinalIgnoreCase));

            if (result?.Contribution is { } c)
            {
                contributions.Add(new PersonaFindings(persona.Id, persona.Lens, c.Posted));
                sessions[persona.Id] = c.NextSession;
                resolvedTitles.AddRange(c.Resolved);
                withdrawnTitles.AddRange(c.Withdrawn);
                suppressed += c.Suppressed;
                refuted.AddRange(c.Refuted);
                turn = Math.Max(turn, c.NextSession.Turn);
                members.Add(new PanelMember(
                    persona.Id, persona.Name, persona.Lens, persona.Model.ToString(), Reported: true));
                continue;
            }

            // Failed, or unchanged. Either way its state is carried (so its history is not lost)
            // AND its standing findings are re-contributed (so the shared comment does not quietly
            // shrink), with anything it already dropped left off - the session keeps the model's
            // full working set, so replaying it verbatim would resurface suppressed and refuted
            // findings. Prefer the session the fan-out actually resolved: it falls back to a legacy
            // per-persona comment, which this blob lookup alone cannot see.
            var carried = result?.Prior
                ?? (priors is not null && priors.TryGetValue(persona.Id, out var resolvedPrior) ? resolvedPrior : null)
                ?? priorSession?.For(persona.Id)
                ?? ReviewSession.Initial;
            sessions[persona.Id] = carried;
            turn = Math.Max(turn, carried.Turn);

            var standing = DroppedMemory.Standing(carried);
            if (standing.Count > 0)
            {
                contributions.Add(new PersonaFindings(persona.Id, persona.Lens, standing));
            }

            // Unchanged is not an absence: nothing moved since this persona last reviewed, so its
            // review still stands and it belongs on the panel line - per-persona mode expresses the
            // same thing by leaving its comment untouched. Naming it as "did not report" is what
            // made a fully-reviewed PR read as never reviewed. A FAILURE is a real gap and keeps
            // its disclosure, even though its earlier findings are still shown.
            var stillRunning = pending?.Contains(persona.Id) == true;
            var failed = !stillRunning && (result is null || result.Outcome == PersonaOutcome.Failed);
            string? reason = null;
            if (stillRunning)
            {
                reason = "still reviewing";
            }
            else if (failed)
            {
                reason = result?.Observability.FailureReason ?? "review could not run";
            }

            // Say so, or the findings below carrying this persona's lens read as this turn's.
            if (reason is not null && standing.Count > 0)
            {
                reason += $"; its {standing.Count} earlier finding(s) still stand";
            }

            members.Add(new PanelMember(
                persona.Id, persona.Name, persona.Lens, persona.Model.ToString(),
                Reported: !failed && !stillRunning, reason));
        }

        var synthesis = FindingSynthesis.Merge(contributions);
        if (synthesis.Merged > 0)
        {
            log?.Invoke($"[panel] merged {synthesis.Merged} duplicate report(s) across the panel");
        }

        var report = new PanelReport(
            members, synthesis, Dedupe(resolvedTitles), Dedupe(withdrawnTitles), suppressed, refuted,
            reconciled, InProgress: pending is { Count: > 0 });

        var visible = PanelCommentRenderer.Render(report, headSha, turn == 0 ? 1 : turn);
        var body = PanelSessionCodec.Embed(visible, new PanelSession(sessions));
        if (pin is not null)
        {
            body = PanelCodec.Embed(body, pin);
        }

        return new ReviewRunResult(results, body);
    }

    private static IReadOnlyList<string> Dedupe(IEnumerable<string> titles)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var t in titles)
        {
            if (!string.IsNullOrWhiteSpace(t) && seen.Add(t.Trim()))
            {
                kept.Add(t.Trim());
            }
        }

        return kept;
    }

    /// <summary>
    /// The panel's state lives in PR comments, and a PR comment is something a stranger can write.
    /// So every read of it is filtered to authors who speak for the repo
    /// (<see cref="CommentTrust.CarriesState"/>) - otherwise a forged <c>pg-panel</c> blob supplies
    /// the personas' system prompts and model ids, and a forged <c>pg-state</c> blob empties the
    /// board or sets the SHA that makes the whole turn skip as unchanged.
    /// </summary>
    private static IEnumerable<ExistingComment> StateComments(IReadOnlyList<ExistingComment> existing) =>
        existing.Where(CommentTrust.CarriesState);

    private static PanelSession? FindPanelSession(IReadOnlyList<ExistingComment> existing)
    {
        foreach (var c in StateComments(existing))
        {
            if (PanelSessionCodec.Extract(c.Body) is { } session)
            {
                return session;
            }
        }

        return null;
    }

    /// <summary>
    /// The pin from a comment we wrote. A pin decides who reviews, carries their system prompts and
    /// model ids, and bypasses <see cref="PanelFence"/> entirely (the fence only ever saw the
    /// orchestrator's output) - so the author check in <see cref="StateComments"/> is what stands
    /// between a pasted blob and a panel of someone else's choosing. The persona marker and
    /// PanelCodec.Extract's clamp are shape checks on top of that, not the trust boundary: a marker
    /// is text anyone can type.
    /// </summary>
    private static PinnedPanel? FindPin(IReadOnlyList<ExistingComment> existing)
    {
        foreach (var c in StateComments(existing))
        {
            if (!c.Body.Contains("<!-- peanut-gallery:", StringComparison.Ordinal))
            {
                continue;
            }

            if (PanelCodec.Extract(c.Body) is { } pin)
            {
                return pin;
            }
        }

        return null;
    }

    private static string? FindExistingBody(IReadOnlyList<ExistingComment> existing, string personaId)
    {
        var marker = CommentRenderer.Marker(personaId);
        foreach (var c in StateComments(existing))
        {
            if (c.Body.Contains(marker, StringComparison.Ordinal))
            {
                return c.Body;
            }
        }

        return null;
    }

}
