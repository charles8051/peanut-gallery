# Feature Spec: Conversation modes (one-shot review)

## Status
**Draft**

| Field        | Value        |
|--------------|--------------|
| Author       | Charles Lee  |
| Created      | 2026-07-26   |
| Last Updated | 2026-07-26   |

## Purpose

Let a repo run the **full panel on the code** while spending far less on the
**conversation around it**. Today those are the same code path: a new PR comment wakes
every persona for a full turn, whether the comment was addressed to the panel or was two
humans talking to each other.

This adds a `conversation` policy with two independent dials — **which comments count**
(a mention gate) and **what a comment turn does** (`panel`, `reconcile`, or `off`).

## Background — what we do today

[Conversational reviewer](../conversational-reviewer/spec.md) made the panel responsive to
human comments: an author says "this is intentional", the reviewer moves the finding to
**withdrawn**. That is genuinely valuable and this spec does not remove it.

What it costs, though, is shaped oddly. `ReviewRunner` skips a persona only when the head
is unchanged **and** there are no new comments, so *any* new human comment defeats the
skip and runs the whole fan-out:

- N personas × 1 review call, plus a verification call each if findings survive the gate;
- with a **panel of 4, that is up to 8 model calls to process one sentence of English**.

The payload is small — on a comment-only turn `LastReviewedSha == headSha`, so the delta
diff is empty and whole-file context is `IsFirstTurn`-gated and absent. So this is a
**call-count and noise** problem far more than a token-volume one. The measured share is
what [`RunSummary`](../../../src/PeanutGallery.Engine/RunSummary.cs) now reports; this
spec should be re-read against real numbers before its second dial is tuned.

The deeper mismatch is conceptual. A **push** means *the code changed, re-review it*. A
**comment** means *a human has something to say about a finding that already exists*.
The second is a bookkeeping decision over the current board, not a fresh review of a
diff — and it does not need four independent lenses to make.

## How it works

### Two dials

```jsonc
"conversation": {
  "mode": "reconcile",              // panel | reconcile | off
  "mentions": ["@peanut-gallery"],  // empty = every human comment counts
  "model": { "provider": "openrouter", "modelId": "..." }  // optional
}
```

Omitting `conversation` entirely is **exactly today's behaviour** — `panel` mode with no
mention gate. This is a purely additive, opt-in feature; no existing consumer changes.

**Dial 1 — the mention gate (`mentions`).** Which comments the panel considers addressed
to it. Empty (the default) means all of them, as today. Non-empty means a comment must
**address** one of the tokens, which is narrower than containing it:

- the token must appear as its own word, so `@peanut-gallery-bot` (a different account)
  and `@@peanut-gallery` do not count;
- fenced blocks and inline code spans are ignored, because naming the token in a code
  block is how you *document* the gate rather than use it;
- quoted lines (`>`) are ignored, so repeating what someone else said is not a new
  address — without this, quoting an earlier mention re-triggers the panel forever.

This is orthogonal to the mode: it decides *whether there is a conversation turn at all*.

**Dial 2 — the mode.**

| Mode | A comment addressed to the panel causes… | Model calls |
|---|---|---|
| `panel` (default) | every persona takes a full turn, as today | N, plus verification |
| `reconcile` | one pass over the whole panel's board | **1** |
| `off` | nothing; comments never trigger a turn | **0** |

A comment that is *not* addressed (mention gate) costs zero calls in every mode.

### The reconciliation turn

When the head has **not** moved and there are addressed comments, `reconcile` replaces the
fan-out with a single call that sees the panel's combined board and the new comments, and
answers with nothing but verdicts:

```json
{"withdrawn":["<finding title>"],"resolved":["<finding title>"]}
```

**The reply schema has no `findings` array, and the fold that applies it can only remove.**
That invariant is deliberate and load-bearing in two directions:

- **Cost** — a conversation turn is bounded. It cannot grow the board, so it cannot
  cascade into verification.
- **Trust** — comments are untrusted input, framed in the prompt as context and never as
  instructions. A turn driven by untrusted input that is *structurally incapable* of
  authoring a finding is a much smaller target than one that can. It is not zero risk:
  talking a real finding off the board is still an attack, which is why every withdrawal
  stays **disclosed** in the comment rather than silently shrinking it, and why the
  existing [comment-trust guard](../conversational-reviewer/spec.md) still gates who may
  speak at all. The gate says *who*; the mention says *were they talking to us*; the
  subtractive schema bounds *what can happen if they were*.

Withdrawn titles land in each persona's [`DroppedMemory`](../../../src/PeanutGallery.Core/DroppedMemory.cs),
so a reviewer does not re-raise on the next push something a human just explained away.

A reconciliation is **not a review**, so it does not advance `Turn` or `LastReviewedSha`.
It advances `LastSeenCommentId` (so the same comment cannot re-trigger it) and prunes the
board. The panel comment discloses that the withdrawal came from a reconciliation pass
rather than from the persona that raised the finding.

### When each path runs

| Head moved? | Addressed comments? | Mode | Outcome |
|---|---|---|---|
| yes | either | any | normal review turn; comments flow in as today |
| no | yes | `panel` | full fan-out (today's behaviour) |
| no | yes | `reconcile` | one reconciliation call |
| no | yes | `off` | nothing runs; personas skip as unchanged |
| no | no | any | nothing runs; personas skip as unchanged |

A push always wins. If the code moved, the panel reviews it and reads the comments in the
same turn — there is no reason to pay for a separate reconciliation.

### Why the gate is in the action, not the workflow

A consumer workflow could filter comments with an `if:`, and that would avoid even
spawning the job. The *authority* still belongs in the action: the workflow cannot read the
repo's `peanut.json`, so tokens duplicated in YAML would silently diverge from the config
that actually governs, and a filter **narrower** than the configured gate swallows real
addresses — the worst failure this feature has, because the panel then sits mute while
someone waits for it. Same reasoning the
[conversational-reviewer spec](../conversational-reviewer/spec.md) applies to its security
guards: the decision lives where it is configured and testable.

**But "a spawned job that makes zero model calls is cheap" is not true**, and this spec
said so in an earlier draft. A comment-triggered job takes the review's
[concurrency](../../../.github/workflows/autoreview.yml) slot, and GitHub cancels the
previously *pending* run in a group — so a comment posted seconds after a push can cancel
the queued review **of the pushed code**, leaving the PR with no `review` check at all.
Observed on this feature's own PR. That is a workflow-level interaction this gate does not
fix and should not claim to; it is tracked separately. A workflow `if:` is therefore worth
having as an efficiency filter, provided it is written to err **broad**.

## Constraint: `reconcile` requires `comment: panel`

One reconciler updating a board that is rendered as N separate per-persona comments would
have to re-render each of them from a session it did not produce. `ConfigValidation` flags
`reconcile` + `comment: perPersona`, and the runner falls back to `panel` conversation
behaviour rather than silently doing nothing.

## Affected layers

| Project / area | Change |
|---|---|
| `PeanutGallery.Core` | `ConversationPolicy`/`ConversationMode`, `ConversationGate` (pure: which comments are addressed), `SessionPlanner.Reconcile` (pure request), `ReconcileParser`, `Reconciliation.Apply` (pure, subtractive fold); `PeanutConfig.Conversation`; `ConfigValidation` |
| `PeanutGallery.Engine` | `ReviewRunner` branches to the reconciliation path; `ConfigCodec` reads the new block |
| `peanut.json` | dogfood: `reconcile` + `@peanut-gallery` |

## Testing

Pure core, no keys, no network: the gate (empty/matching/non-matching/case), the request
shape, the parser (including that a `findings` array in the reply is **ignored**), and the
fold (removes only, feeds dropped memory, leaves other personas alone). Engine tests cover
the branch table above — in particular that a push still wins, that `off` makes zero calls,
and that `reconcile` makes exactly one.

## Open questions

- Should `resolved` be reconcilable at all? A human claiming "fixed" without a push is
  unverifiable — the existing prompt tells reviewers to verify a claimed fix against the
  diff, and a reconciliation has no diff to check. Shipping it because withholding it
  would make the reconciler unable to act on the most common reply, but it is the weakest
  part of this design and worth revisiting against real transcripts.
- Whether the mention gate should default to on once the token accounting shows what
  ungated conversation actually costs across repos.
