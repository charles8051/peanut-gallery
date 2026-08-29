# Degraded-panel visibility: make a partial review visible at decision time

**Status:** Implemented (options 2 + 3) · **Issue:** resolves [#130](https://github.com/charles8051/peanut-gallery/issues/130)

## Problem

A degraded reviewer (timeout, truncation, provider error) still lets the `review` check go **green** —
the run exits 0 by design (reviews are advisory), and the panel comment merely annotates the missing
persona in a muted `_Did not report:_` line. So **a panel that lost the lens most relevant to the
change reads identically to a clean review** from the outside.

The concrete case that surfaced this (#127): on one production PR the `resolution-correctness` persona —
whose entire remit was that PR's central change — timed out on the first run, and nobody would have
known without decoding the metrics blob. A merge-gate polling consumer could not tell "clean review,
no findings" from "the review that mattered didn't happen."

What already existed, and why it was insufficient:
- The Job Summary + `::warning::` annotations show degradation **per run**, but are ephemeral
  (bounded by run-log retention) and not a signal a gate reads.
- The metrics ledger records it, but only **after the fact** (`peanut-gallery metrics`).
- The panel comment named the non-reporting persona, but in muted prose that reads like the other
  disclosures — easy to miss, and it changed no machine-readable signal.

## Design

Two complementary surfaces, both keyed off the one canonical degradation signal
(`PersonaOutcome.Failed`, surfaced pure by `RunSummary.DegradedCount`). Reviews stay **advisory by
default** — nothing here changes the green check unless a repo opts in.

### Option 2 — a prominent inline banner + a machine-readable marker (always on)

`PanelCommentRenderer` emits, on a **settled** panel that lost one or more reviewers:

- A GitHub alert callout above the findings:
  `> [!WARNING]` **`N reviewer(s) did not report this run — this review is partial.`** — pointing at
  the existing `_Did not report:_` disclosure rather than replacing it (the banner says *that* a lens
  is missing; the disclosure says *who* and *why*).
- A hidden, machine-readable marker `<!-- pg-degraded:N -->` (`PanelCommentRenderer.DegradedMarker`),
  so a merge-gate polling consumer greps one token to tell a degraded turn from a clean one — the gap
  the issue exists to close — without parsing prose.

**Marker contract.** `<!-- pg-degraded:N -->` is the stable grep-target for external consumers: the
literal `pg-degraded:` prefix (`PanelCommentRenderer.DegradedMarkerPrefix`) followed by the
non-reporting count `N`, in a raw HTML comment (hidden in GitHub's rendered view, present in raw — a
consumer reads raw). `N` is always **≥ 1**: `DegradedMarker` throws on a zero/negative count rather
than emit a `pg-degraded:0` that a gate would misread as degradation on a clean panel, and the banner
only writes it when a reviewer is actually missing. Absence of the marker means "not degraded."

**Settled only.** While `InProgress`, a not-yet-reported reviewer is *pending*, not degraded; banner-ing
it would fire the signal on every intermediate render. On a settled render `!Reported` ⟺ a genuine
failure (a `stillRunning` member only exists while pending), so the banner fires exactly on real gaps.

This is a **presentation** change (pure core, in `PanelCommentRenderer`); it is the panel-mode
counterpart to per-persona mode, where a failed persona's own comment simply never reaches
`Reviewed through <head>` and the polling loop already sees the gap.

### Option 3 — opt-in fail-the-check (off by default)

`PG_FAIL_ON_DEGRADED=1` (parsed by `ReviewBudget.FailOnDegraded`; only `1`/`true`/`yes`, any case)
makes a run that degraded any reviewer exit **non-zero** (`3`, distinct from a `CliError`'s `1`) so
the CI `review` check goes red. The gate runs **last** — after the comments, metrics ledger, and
annotations are all posted — so a repo that opts in still gets the full advisory review, plus a red
check its branch-protection gate can require. Unset/blank/`0`/`false` is the safe default: advisory,
green.

## What was deliberately deferred

**Option 1 — a distinct `neutral`/`action_required` check-run status** for a degraded panel. This is
the richer "first-class, machine-readable, non-blocking" signal, but the action posts *comments*; it
does not create a GitHub **Check Run**, so a `neutral` conclusion needs the Checks API and a larger
surface. Its practical outcome (a non-green signal a gate can read) is already reachable via option 3
for repos that want it, and via the `pg-degraded` marker for those that only want to *detect* it. Left
as a follow-up; revisit if a consumer needs a non-blocking-but-visible status without opting into a
hard fail.

## Affected layers

| Project / area | Change |
|---|---|
| `PeanutGallery.Core` | `PanelCommentRenderer` — the settled-degradation banner + `pg-degraded` marker (pure) |
| `PeanutGallery.Engine` | `ReviewBudget.FailOnDegraded` (env parse); `RunSummary.DegradedCount` (pure predicate) |
| `PeanutGallery.Cli` | reads `PG_FAIL_ON_DEGRADED`; returns exit `3` when the gate trips, after posting |

## Consumer contract note

The polling-loop contract callers are expected to follow gates on each persona's
`Reviewed through <sha>` marker. A degraded **panel** now also carries a `<!-- pg-degraded:N -->`
marker on its settled comment; a consumer that wants to treat a partial panel as not-yet-clean can
gate on its absence, or require the `review` check with `PG_FAIL_ON_DEGRADED=1` set.

## Tests

- `PanelDegradationBannerTests` (core): settled + degraded → banner + marker + count/pluralisation;
  full panel → neither; in-progress → neither (no crying wolf).
- `ReviewBudgetTests`: `FailOnDegraded` truthy-only parse.
- `RunSummaryTests`: `DegradedCount` counts only `Failed`, not `Unchanged`/`Reviewed`.
