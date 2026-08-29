# Auto panel: A/B evaluation (the `yagni` meta-prompt hint)

**Date:** 2026-08-07 · **Change under test:** [#151](https://github.com/charles8051/peanut-gallery/pull/151) ·
**Feature:** [Auto panel](spec.md) · [Prior A/B](ab-evaluation.md)

> **Outcome: the change was reverted** ([#155](https://github.com/charles8051/peanut-gallery/pull/155)).
> The hint fixed a real blind spot but was wrong too often on this codebase to earn one of four
> panel slots. This document is the record of what was measured, so the idea is not re-proposed as
> untried. See [Decision](#decision).

## Question

[#151](https://github.com/charles8051/peanut-gallery/pull/151) added one rule to the panel-selection
meta-prompt: over-engineering is a hazard, convene a `yagni` reviewer when the diff shows it. It was
merged without validation. Two things needed checking:

1. **Sensitivity** — does the hint make the orchestrator name unearned complexity it would otherwise
   miss? The premise was that this framing structurally hides it: every other rule asks what a change
   might *break*, and over-engineering breaks nothing.
2. **Crowd-out** — the panel caps at 4 and this repo's seed takes 2, so a `yagni` reviewer that
   convenes when it shouldn't displaces one anchored to a real hazard.

## Method

The measured quantity is **which lenses get convened**, so this calls only the orchestrator
(`ChatClientPanelPlanner`) — one model call per trial instead of five, isolating panel selection from
review quality. Everything else is a production run: real models, real config (`peanut.json`,
`seedAndAuto`, minimax-m3), `CLAUDE.md` passed as `RepoConventions`, seed = the configured panel.

The arms are two **builds**, because the meta-prompt is a compiled string:

| Arm | Build | Prompt |
|---|---|---|
| A | `8abce3f` | before #151 |
| B | `2c9e79f` | after #151 |

Verified distinct by checking the linked `PeanutGallery.Core.dll` for the inserted string rather than
trusting the build wiring.

**Corpus** — five diffs, three trials per cell, both arms (30 calls), plus a nine-call diagnostic
re-run:

| Diff | Role |
|---|---|
| `overbuilt` | synthetic probe: an interface with one implementer, a factory over a one-entry registry, a config enum with one value, a static event with no subscriber |
| `lean` | synthetic control: a small total string helper, no unearned structure |
| `pr82`, `pr84`, `pr90` | the prior evaluation's corpus, reconstructed as first pushed — real diffs with known ground truth |

Three trials per cell because the prior evaluation was n=1 and its own conclusion was that
run-to-run variance rivalled the effect being measured.

## Result 1: the blindness is real, and the hint fixes it

On the `overbuilt` probe, **arm A never once named the over-engineering** — across three trials it
convened `static-event-lifecycle`, `functional-core-purity,concurrency`, and
`aot-trimming,static-state-lifecycle`. It reliably found *failure modes inside* the unearned
structure and never the unearned structure itself, which is exactly the gap the change was premised
on. Arm B convened `yagni` 3/3.

| | Arm A | Arm B |
|---|---|---|
| `overbuilt` — fires | 0/3 | **3/3** |
| `lean` — fires (false positive) | 0/6 | 0/6 |

Sensitivity and control-specificity are both clean.

## Result 2: on real diffs it fires about a third of the time, and both cases inspected were wrong

`yagni` fire rate, arm B, across both runs:

| Diff | Fires | Verdict |
|---|---|---|
| `overbuilt` | 3/3 | correct |
| `lean` | 0/6 | correct |
| `pr82` | **3/3** | **false positive** |
| `pr84` | **2/6** | **false positive** |
| `pr90` | 0/6 | correct |

Arm A produced no `yagni`-like lens in any of its 15 trials.

Both false positives were checked against the diffs, by grepping added lines for the constructs the
meta-prompt names as tells. **Neither diff contains a single one** — zero matches for `interface `,
`abstract class`, `Factory`, `Strategy`, `Registry`, `event `, or `delegate ` across both:

```bash
grep -cE "^\+.*(interface |abstract class|Factory|Strategy|Registry|event |delegate )" pr82.patch  # 0
grep -cE "^\+.*(interface |abstract class|Factory|Strategy|Registry|event |delegate )" pr84.patch  # 0
```

The full added public surface, which is what a reader needs to judge the verdict:

- **`pr82`** — `record FileContext(string Path, string Text)`, `record ContextSelection(...)`,
  `static class ContextBudget` with one pure `Fit`, `const int DefaultBudgetBytes`, and a `Read`
  port whose only implementations are the real reader and a test fake. That port is the sole
  plausible hook for the lens, and a shell port with one implementer is
  [ADR-0001](../../adr/0001-functional-core-multi-shell.md) working as designed, not unearned
  structure.
- **`pr84`** — `record Verdict(string Title, bool Refuted, string Why)`,
  `record VerificationResult(...)`, `static class Verification` with one pure `Apply`,
  `static class VerdictParser` with one pure `Parse`, and `PromptAssembly.Verify`. Canonical
  functional core: values plus total functions, no indirection at all.

**Caveat on this verdict.** The judgement is that these diffs contain nothing a `yagni` reviewer
should have fired on — it is not a reading of what the convened persona went on to *say*, because
the harness measures panel selection and stops before the review. A firing on a diff with no tells
present is the strongest available evidence of a false positive, but a stronger design would capture
the persona's own cited construct. Worth building into any second attempt.

The cost is concrete. `pr82` is the file-reading diff whose known ground-truth defect is a **path
traversal**, and in arm B `yagni` took one of the two available slots on all three trials —
displacing a second security lens each time:

| Trial | Arm A | Arm B |
|---|---|---|
| 1 | `path-traversal, prompt-injection` | `path-traversal, yagni` |
| 2 | `prompt-injection` | `security, yagni` |
| 3 | `untrusted-file-read` | `yagni, secret-disclosure` |

This is the failure predicted before the change was built: *this repo's architecture legitimately
carries structure a single diff cannot justify*. `CLAUDE.md` was passed as conventions in every trial
and did not prevent it — the prime directive tells the orchestrator the core must be pure, not that
a one-implementer port at the shell boundary is load-bearing by fiat.

## Result 3: a reliability scare that did not reproduce

The first run showed arm B returning an **empty panel on 4 of 15 trials against arm A's 0** — a
serious result if real, since an empty plan silently degrades the review to the seed. It did not
survive scrutiny: re-running the same cells produced **0 empty panels in 9 trials**, with every
trial logging a normal convene. The empties correlate with 137–153 s call durations, i.e. retries
against a slow provider, not the prompt.

Recorded because the first run's numbers are in this repo's history and would otherwise look like
evidence. The harness initially suppressed the planner's log, which is why the first run could not
say *why* a panel was empty — model failure, unparseable reply, and everything-fenced-out are three
different bugs that look identical in the result.

## Result 4: variance is still the dominant term

`pr84` fired `yagni` 0/3 in the first run and 2/3 in the second — same arm, same diff, same
temperature (0.2, near-deterministic by design). The prior evaluation's caveat stands unchanged:
n=3 per cell is thin, and none of the rates above should be read as precise. The two findings that
do not depend on precision are the 0/3-vs-3/3 sensitivity gap on `overbuilt` and the fact that both
inspected real-diff firings were wrong.

## Decision

**Reverted** ([#155](https://github.com/charles8051/peanut-gallery/pull/155)).

The panel caps at 4 and this repo's seed takes 2, so the orchestrator has two slots to spend. A lens
that is wrong about a third of the time on this codebase does not earn one of them — especially when
the observed cost was displacing a security lens on the one corpus diff with a known path traversal.

Two narrower fixes were considered and not taken:

- **Add the missing convention** — state in `CLAUDE.md`/`AGENTS.md` that shell-boundary ports and
  small pure value records are load-bearing by architectural fiat and are not YAGNI. Plausible: the
  orchestrator already receives that file and is already told to prefer documented house rules. But
  it needs another full A/B to know whether it worked, which is more cost to keep an experiment that
  has not yet earned its place.
- **Narrow the tells** — the list is deliberately illustrative ("examples of the shape, not a
  checklist"), which is what lets it reach on architecture it does not understand. A closed list
  excluding single-implementer ports would trade recall for precision. Same objection: more tuning
  spent on an unproven lens.

## If this is proposed again

The blind spot is real and the sensitivity result is the reason to take a second attempt seriously —
an orchestrator reading a diff for failure modes genuinely does not see unearned complexity, because
over-engineering breaks nothing.

What a second attempt needs that this one did not have:

1. **A conventions line first**, not as a follow-up. This repo's architecture *is* the false-positive
   generator; the lens cannot be evaluated fairly until the orchestrator is told that a
   one-implementer port is deliberate.
2. **More trials per cell.** `pr84` fired 0/3 in one run and 2/3 in another, same arm, same diff, at
   temperature 0.2. Nothing here should be read as a precise rate.
3. **A precision target set in advance.** "Fires sometimes" is not a result. With two open slots, the
   bar is roughly: does it fire on genuinely over-engineered diffs *and* stay off well-shaped ones,
   often enough to beat whatever lens it displaces?

Do not re-run the corpus alone — `overbuilt` and `lean` are the probes that actually discriminate,
and both are cheap to rebuild.

## Reproducing

The harness is throwaway and lives outside the repo — it constructs a `ChatClientPanelPlanner`
directly, calls `PlanAsync(diff, conventions, seed)`, and prints the convened lenses plus the
planner log. The two arms are built by pointing a `PgRoot` MSBuild property at two worktrees:

```bash
git worktree add --detach ../arm-a 8abce3f
dotnet build Harness.csproj -c Release -p:PgRoot=../arm-a -o bin-a
dotnet build Harness.csproj -c Release -p:PgRoot=. -o bin-b
dotnet bin-a/abharness.dll A peanut.json 3 corpus/*.patch > results-a.tsv
```

Corpus diffs are reconstructed as first pushed, per [`ab-evaluation.md`](ab-evaluation.md#reproducing).
