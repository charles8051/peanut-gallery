# Auto panel: A/B evaluation (the `disproportion` meta-prompt hint)

**Date:** 2026-08-07 · **Feature:** [Auto panel](spec.md) ·
**Predecessor:** [the reverted `yagni` lens](ab-yagni-lens.md)

## Question

The [`yagni` lens](ab-yagni-lens.md) was reverted because it was calibrated to the wrong axis: it
counted **abstractions**, flagging one-implementer shell ports that
[ADR-0001](../../adr/0001-functional-core-multi-shell.md) mandates. The hazard actually worth
catching is **machinery out of proportion to its problem**, which is a different thing and can occur
with no abstraction at all.

The motivating example is the **scaffolding-runaway case**, on a private repository: a
four-call-site, ten-line refactor shipped with a 343-line guardrail test containing a hand-rolled C#
lexer (verbatim, interpolated and raw string literals, escape handling, interpolation holes) and a
regex enumerating every compound assignment operator including `>>>=` and `??=`. Of its nine tests,
**two** test the rule and **seven** test the regex and the masker. It contains no interface, factory,
or event — every tell the old lens looked for returns zero.

Two questions:

1. **Does the new lens fire on that diff**, which the old one would have scored clean?
2. **Has the axis actually moved** — does it stay off diffs whose only "offence" is a
   single-implementer port?

## Method

Same harness as the prior evaluation: orchestrator-only (`ChatClientPanelPlanner`), one model call
per trial, arms are two builds because the meta-prompt is a compiled string. Verified the linked
`PeanutGallery.Core.dll` differs rather than trusting the build wiring.

| Arm | Build | Prompt |
|---|---|---|
| A | `83d59d9` | no lens |
| B | this branch | `disproportion` |

Six diffs, three trials per cell, 36 calls. The runaway cell is run from that repository's own
checkout so it gets **its own** conventions rather than another repo's house rules.

**The orchestrator is now `openai/gpt-5.6-luna`** (changed in #154). Numbers here are *not*
comparable to the `minimax-m3` figures in the prior evaluation.

### Corpus, and what each cell falsifies

| Diff | prod / test added | Should fire? | Tests |
|---|---|---|---|
| `sv708` | 10 / 309 (**30.9:1**) | **yes** | the real positive, ground truth from the author |
| `pr82` | 156 / 180 (1.2:1) | **no** | one-implementer port — the old lens false-fired here 3/3 |
| `pr84` | 199 / 270 (1.4:1) | no | a proportionate feature with normal test weight |
| `pr90`, `lean` | — | no | proportionate real and trivial diffs |
| `overbuilt` | 39 / 0 | *ambiguous, see below* | the old lens's positive probe |

## Result 1: it fires on the real positive, every time

| Diff | Arm A | Arm B |
|---|---|---|
| `sv708` | **0/3** | **3/3** |

Arm A's blindness is worth reading literally, because it is sharper than any synthetic probe:

| Trial | Arm A convened | Arm B convened |
|---|---|---|
| 1 | `hardware-state-coherence, source-guardrail-integrity` | `hardware-state-freshness, disproportion` |
| 2 | `hardware-state-freshness, static-guardrail-coverage` | `disproportion` |
| 3 | `hardware-state-safety, static-guardrail-integrity` | `hardware-state-safety, disproportion` |

Given a 343-line hand-rolled lexer, **arm A convened a reviewer to check that it works** —
`source-guardrail-integrity`, `static-guardrail-coverage` — on all three trials. It never once asked
whether the guardrail should be 343 lines, or whether Roslyn (already a dependency, already parsing
those files) would have removed the need for the lexer entirely. That is the blind spot stated
precisely: an orchestrator reading for failure modes reviews the machinery for correctness and
cannot see the machinery itself as the hazard.

## Result 2: the axis moved

| Diff | old `yagni` lens | new `disproportion` lens |
|---|---|---|
| `pr82` | **3/3 (wrong)** | **0/3** |

`pr82` is the diff that killed the previous attempt — a `Read` port with one real implementer plus a
test fake, which the old lens flagged on every trial while displacing a security lens on a diff whose
known defect is a path traversal. The explicit negative (`ABSTRACTION IS NOT THAT LENS AND IS NOT A
FINDING AT ALL`) holds: 0/3, and the security lenses are back
(`filesystem-boundary`, `path-traversal`, `context-disclosure`).

The ratio separation is clean and interpretable:

| Ratio | Fires |
|---|---|
| 30.9:1 (`sv708`) | 3/3 |
| 1.4:1 (`pr84`) | 1/3 |
| 1.2:1 (`pr82`) | 0/3 |

## Result 3: two costs, honestly

**`pr84` fired 1/3, and that is probably a false positive.** At 1.4:1 it is an ordinary feature with
this repo's ordinary test weight. This is the risk to watch: peanut-gallery is deliberately
heavily tested, and a lens reading test-to-production ratio can mistake house style for excess. One
firing in three is within the noise this corpus can resolve, but it is the failure mode to re-measure
against, not `pr82`.

**One crowd-out instance.** On `sv708` trial 2 arm B convened **only** `disproportion`, dropping the
`hardware-state-freshness` reviewer that the other two trials kept — and staleness of that guard
is the load-bearing safety judgement the PR author explicitly asked to have reviewed. The lens
displacing a *"is the regex correct"* reviewer is a good trade; displacing the safety reviewer is
not. It happened once in three.

**`overbuilt` is not the clean negative control this evaluation wanted.** It fired 2/3, which was
initially scored as a failure of recalibration. On inspection that is likely *correct*: the probe
wraps a one-line string transform in ~47 lines of interface, factory, registry dictionary, enum and
static event. That is abstraction, but it is also genuinely disproportionate machinery. The probe
sits on both axes, so it cannot falsify either — a corpus design flaw carried over from the previous
evaluation, where it was built to match tells that no longer apply. `pr82` is the cell that actually
discriminates, and it passes.

## Result 4: variance, again

Unchanged caveat from the prior evaluation: n=3 per cell, and last time `pr84` swung 0/3 to 2/3
between runs of the same arm. The findings that do not depend on precision are the `sv708`
0/3-vs-3/3 gap and the `pr82` 3/3-to-0/3 reversal between lenses. The `pr84` 1/3 rate specifically
should not be read as "33%".

## Recommendation

**Adopt.** The lens fires on the diff it was built for on every trial, stays off the diff that killed
its predecessor, and the ratio separation between them is an order of magnitude.

Before treating the false-positive rate as known:

1. **Re-measure `pr84` with more trials.** It is the only ambiguous real-diff cell and the only one
   that could indicate the lens reading house test-weight as excess.
2. **Build a real negative control for machinery.** Something with genuinely high test weight that is
   nonetheless proportionate — `overbuilt` cannot serve, and a synthetic probe built to match the
   tells would be circular in exactly the way the previous corpus was.
3. **Watch the crowd-out case.** If `disproportion` displaces a safety-critical lens more than
   occasionally, the fix is the panel cap, not the prompt.

## Reproducing

As [`ab-yagni-lens.md`](ab-yagni-lens.md#reproducing), with the runaway cell run from that
repository's own checkout so its conventions file is picked up.
