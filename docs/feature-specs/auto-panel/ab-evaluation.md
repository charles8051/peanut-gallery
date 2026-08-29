# Auto panel: A/B evaluation (fixed vs auto)

**Date:** 2026-07-23 · **Issue:** [#70](https://github.com/charles8051/peanut-gallery/issues/70) ·
**Feature:** [Auto panel](spec.md) · [ADR](adr.md)

## Question

Does an orchestrator-convened panel review better than the fixed hand-authored one? Everything from
[#65](https://github.com/charles8051/peanut-gallery/issues/65) onward was built on the assumption
that it does. This is the check.

## Method

Three PR diffs from this repo, reconstructed **as first pushed** (via `refs/pull/N/head` and each
branch's first commit) rather than as merged. The merged diff already contains the fixes for every
finding, so reviewing it cannot test whether a panel catches the original bug.

That gives real ground truth — what the fixed panel actually found at that commit:

| Diff | Known findings at that commit |
|---|---|
| [#82](https://github.com/charles8051/peanut-gallery/pull/82) `1a4bddf` | path traversal in the context reader (major) |
| [#84](https://github.com/charles8051/peanut-gallery/pull/84) `b990b76` | verification latency conflation (minor), refuted findings resurface (info) |
| [#90](https://github.com/charles8051/peanut-gallery/pull/90) `0b4f8e5` | `FindPin` trusts any comment (info), arbitrary persona model, orchestrator flattened to string, null-forgiving operator |

Both arms use identical models, both receive the repo conventions, both run the confidence gate and
the adversarial pass. **The only variable is who is on the panel.** Fixed is `architect` +
`bug-hunter`; auto has zero configured personas and lets the orchestrator convene from the diff.

Run with `review-pr --preview` (real models, nothing posted), which this evaluation had to add —
`--dry-run` fused "use the offline stub" with "don't post", so there was no way to exercise a review,
let alone compare two panels, without writing comments to somebody's PR.

## Result 0: the evaluation found a shipped bug before it compared anything

**Pure auto mode did not work at all.** `ReviewPrAsync` bailed with `no personas assigned` before
`ReviewRunner` was ever called, and that guard reads the *configured* panel — which in canonical auto
mode is empty by design. The CLI refused to run the mode the config asked for.

383 unit tests missed it, because they all drive `ReviewRunner` directly and never cross that guard.
This is the argument for running the evaluation before building
[#71](https://github.com/charles8051/peanut-gallery/issues/71) on top of the premise.

## Result 1: the panels auto convened

| Diff | Convened lenses |
|---|---|
| #82 | `path-traversal`, `prompt-injection`, `test-coverage` |
| #84 | `prompt-injection`, `title-matching`, `silent-failure`, `core-purity` |
| #90 | `pin-injection-via-user-comments`, `planner-output-as-identity`, `silent-auto-fallback`, `repo-target-flattening` |

These are not generic. Given a diff that adds file-reading from a checkout, the orchestrator
independently decided the risks were path traversal and prompt injection — the correct threat model,
chosen without being told. On #90 it named `pin-injection-via-user-comments`, which is precisely the
security hole that PR's review turned up.

## Result 2: findings

| Diff | Fixed | Auto |
|---|---|---|
| #82 | **0** | **5** — incl. the path traversal as *critical* |
| #84 | 3 (one being "review could not run") | 5 |
| #90 | 2 (both *critical*) | 7 |

On #82 auto found the known path traversal and rated it **critical**; the fixed arm found **nothing
at all** on the same diff.

## Result 3: two new real bugs, neither previously known

Auto surfaced defects nobody had found, both since verified and filed:

- **[#91](https://github.com/charles8051/peanut-gallery/issues/91) — the context reader follows
  symlinks.** `Path.GetFullPath` normalises `..` but does not resolve reparse points, so a committed
  symlink pointing outside the repo passes the containment check and has its target read. This is a
  *second* traversal vector that #82's fix did not close, in a guard whose entire job is containment.
- **[#92](https://github.com/charles8051/peanut-gallery/issues/92) — a throwing `IPanelPlanner`
  sinks the run.** Planning sits outside the per-persona try/catch, inconsistent with every other
  seam here, all of which are deliberately total.

## The caveat that limits all of the above

**Run-to-run variance rivals the effect being measured.** In the first pass the fixed panel found
**0** findings on #82 — failing to reproduce the path-traversal bug it had itself caught on the real
PR. Severity is unstable too: the `FindPin` issue was rated *info* on the live PR and *critical* here,
by the same panel on the same code.

So this is n=1 per cell against a noisy measurement. It cannot establish "auto beats fixed" as a
statistical claim, and the numbers above should not be read as one.

## What it does establish

Two things that do not depend on the counts:

1. **The lenses are demonstrably diff-appropriate.** `path-traversal` on the file-reading diff and
   `pin-injection-via-user-comments` on the pin diff are not luck — they are the orchestrator reading
   the change and naming its actual hazards.
2. **Auto found two real bugs the fixed panel never found**, across every run it has ever done. That
   is a floor on its value that variance cannot explain away.

Against that: auto's severity calibration is questionable (it rated "no direct unit tests" as *major*
and the symlink escape as *minor*, which is close to backwards), and it produces more findings, some
of which are test-coverage observations rather than defects.

## Recommendation

**Adopt `seedAndAuto`, not `auto`.** The seed keeps a curated floor that does not depend on the
orchestrator having a good day; the generated personas add the diff-specific lenses that produced
every novel finding here. Pure `auto` throws away hand-tuned house knowledge for no demonstrated gain.

**Do not treat this as settled.** Re-run with several trials per cell before flipping any default,
and prefer looking at *which* findings appear over *how many*.

## Reproducing

```bash
gh pr diff <N> -R <owner/repo> > merged.patch          # NOT this - already contains the fixes
git fetch origin refs/pull/<N>/head:refs/ab/pr<N>       # do this instead
git diff $(git merge-base origin/main refs/ab/pr<N>) $(git log --reverse --format=%H origin/main..refs/ab/pr<N> | head -1) > first.patch

peanut-gallery review-pr --config fixed.json --pr 0 --slug <owner/repo> --diff first.patch --preview
peanut-gallery review-pr --config auto.json  --pr 0 --slug <owner/repo> --diff first.patch --preview
```
