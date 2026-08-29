# Panel feedback loop: the proportionality clause, A/B'd against the scaffolding-runaway case

**Date:** 2026-08-07 · **Feature:** [Auto panel](spec.md) ·
**Sibling:** [`ab-disproportion-lens.md`](ab-disproportion-lens.md)

## The loop

In the **scaffolding-runaway case** the review tool caused the over-engineering it was later used
as an example of. The PR is on a private repository, so it is referred to by shape throughout.

The issue it closes asked for a
**cheap** guardrail test. Peanut Gallery's orchestrator read the opening diff and convened a
`guardrail-test-reliability` persona whose `focus`, recovered verbatim from the pinned panel blob,
reads:

> Check the regex's read-versus-write detection, **coverage of actual C# syntax and all relevant
> input names**, repository-root discovery under test runners/CI, and whether the hard-coded
> hard-coded path and match count provide a stable, meaningful enforcement test…

It then did exactly that for five turns:

| Turn | Guardrail | Message |
|---|---|---|
| 1 | **102** | the refactor |
| 2 | 149 | harden the vocabulary scan, and pin its own behaviour |
| 3 | 255 | mask trivia and exclude every assignment operator |
| 4 | **343** | handle every literal form, and keep interpolation holes visible |

Ten lines of production change; a 343-line hand-rolled C# lexer to protect them.

**Every finding was probably true.** Interpolated raw strings really were masked wrong. The
verification pass refuted none of them, because every check in the system asks whether a finding is
*real* and none asks whether it is *worth it*. A tool that only tests truth will ratchet a lint into
a compiler, one correct finding at a time.

Panel pinning made it permanent: the panel is frozen at PR-open, so "go make the regex complete" was
the standing brief for the PR's whole life, and no later turn could convene a reviewer to ask whether
343 lines was the right shape.

## The intervention under test

A proportionality clause appended in `PromptAssembly.BuildSystemPrompt` — the seam **every** persona
passes through, seed and generated alike, because both kinds can cause this. It asks four things:
weigh the remedy against the risk; you are reviewing a change, not commissioning machinery; do not
push a guard's completeness past the risk it exists to reduce; and severity is the consequence in
production, not how incomplete a mechanism looks. Plus the exit: **if the proportionate answer is a
simpler mechanism than the one under review, say that rather than asking for the current one to be
extended.**

## Method

Unusually good ground truth. The persona is loaded **verbatim from that PR's pinned panel blob** — the
reviewer that really drove the escalation, at its real model and temperature — and replayed against
the four actual states of the PR. The harness runs a real `IReviewer.ReviewAsync`, so the measured
quantity is what a reviewer *files*. Arms are two builds; verified the linked
`PeanutGallery.Core.dll` differs. Four diffs × 3 trials × 2 arms = 24 reviews, 65 findings.

## Result 1: the clause is a damper, not a fix

| | No clause | With clause |
|---|---|---|
| Findings per trial | 3.17 | **2.25** (−29%) |
| `major` findings | **9** | **3** (−67%) |
| Escalating findings per trial | 1.67 | **1.25** (−25%) |
| Escalating as a *share* of findings | 53% | **56%** (unchanged) |

The severity half worked well: `major` on a test-only file's regex nuance mostly stopped. Volume
dropped meaningfully. But the **share** of findings whose remedy is "make the machinery bigger" did
not move at all. With the clause the persona still files *"Regex misses valid multiline raw-input
reads"*, *"Trivia masker does not handle C# raw string literals"*, *"Input names outside the
ASCII-letter pattern are silently excluded"*. It escalates less loudly and slightly less often. It
still escalates.

## Result 2: the exit clause produced nothing at all

Across **24 trials and 65 findings, in neither arm did a single finding propose a different
mechanism.** Not one said "this should be a Roslyn analyzer" — Roslyn being already a dependency,
already parsing those files, and the reason `MaskTrivia` need not exist. Not one said the guardrail
was disproportionate to the rule it enforces.

The clause asked for exactly this in as many words and got zero instances. That is the clearest
result here, and it is negative.

The likely reason is structural: the persona's own commission *is* the escalation
("coverage of actual C# syntax and all relevant input names"). A general instruction cannot override
a specific brief the same prompt hands it two paragraphs earlier. **The intervention point is
wrong** — by the time a reviewer with that focus exists, the loop is already closed.

## Result 3: the lens fires at PR-open, before any of it

Running the panel-selection harness against the **turn-1** diff — the 102-line guardrail, ~10:1 —
with the [`disproportion` lens](ab-disproportion-lens.md):

| Trial | Convened |
|---|---|
| 1 | `guardrail-validity`, **`disproportion`** |
| 2 | `hardware-state-coherence`, **`disproportion`** |
| 3 | **`disproportion`**, `guardrail-integrity` |

**3/3, at PR-open, before a single line of escalation.** And because pinning freezes the panel, that
reviewer would have been a standing voice for the PR's whole life — arguing against the machinery on
every turn that `guardrail-validity` argued to extend it.

That is the counterweight. It works at convening time, which is where this loop is actually closed.

## Conclusion

Keep the clause; do not credit it with fixing this. It is cheap, prompt-only, and buys a 29% volume
cut and a 67% cut in `major` severity across every persona in the system. It does not break the
escalation loop and the evidence says it cannot, because it acts after the reviewer has already been
commissioned to escalate.

The loop is closed at the orchestrator. Both follow-ups below were subsequently built and measured -
see [Follow-up](#follow-up-closing-the-loop-at-the-orchestrator):

1. **Do not commission completeness.** A convened persona's `focus` should name a hazard in the
   change, not the exhaustiveness of a mechanism the change introduces. "Coverage of actual C#
   syntax" is a brief to grow machinery, written by us.
2. **Pair the reviewers.** Trials 1 and 3 above convened `disproportion` *alongside*
   `guardrail-validity` — the two arguing opposite directions on one panel. That is probably the
   right shape, but it is an accident of this run, not a rule.

## Follow-up: closing the loop at the orchestrator

Both follow-ups this document recommended were then built and measured. Arms: `53bbe3a` (lens +
clause) against the same tree plus the two changes below. Two diffs — the case's turn-1 state, where the
loop began, and its final state — three trials each.

### Stop commissioning completeness (prompt)

A rule in `PanelPlanner`: *a risk is a hazard the CHANGE carries, not the incompleteness of a
mechanism the change introduces*, naming the anti-pattern outright and redirecting to what breaks in
production if the guard is imperfect.

This half is prompt-only on purpose. Enforcing it in code means classifying "is this brief about
completeness?" from free prose — keyword matching over English, a brittle guard for a rule the
compiler cannot check. That is the case's own mistake, and committing it to enforce a rule *against*
that mistake would be a poor trade.

**Completeness briefs went 4 → 0 across six trials.** Before, the orchestrator was writing exactly
the escalation:

> `guardrail-integrity` — "raw input reads can evade enforcement through formatting, aliases,
> multiline expressions…"
> `guardrail-correctness` — "its incomplete parsing can silently miss real reads (for example
> interpolated raw strings…)"
> `guardrail-soundness` — "Its handling of interpolation holes, raw/verbatim strings, comments, line
> wrapping and assignment…"

Those are the briefs that drove 102 → 343, written by us at PR-open. Afterwards, every trial framed
the guardrail as a ratio question instead, and the freed slot went to `hardware-state-safety` — the
production risk the PR author called the load-bearing judgement.

### Pair the reviewers (code)

`PanelCandidate` gains `reviewsIntroducedMechanism`: the orchestrator declares whether a reviewer's
subject is a mechanism the change introduces, and `PanelFence` pairs one with a `disproportion`
reviewer deterministically. Model classifies, code enforces — the split this repo already uses,
because a panel that silently lost its counterweight looks exactly like a panel that never needed
one.

The paired candidate is inserted *before* the accept loop, directly after the reviewer it
counterweights, so it takes its chances with the cap, blocklist and dedup rather than bypassing
them.

### A bug the A/B caught

The first run paired a panel that did not need it. An orchestrator that has understood the point
tends to *qualify* the lens — it proposed `guardrail-disproportion` — and the "already proposed"
check matched on exact slug equality, so it injected a second, near-identical reviewer. On `sv708`
trial 2 that twin displaced `hardware-state-safety`.

A counterweight that crowds out the lens it exists to protect is worse than none. The check is now a
substring match; re-running `sv708` gives no duplicate and the safety reviewer is back. Recorded
because it is the same failure mode this whole document is about, committed by the fix for it.

## Caveats

n=3 per cell, 12 trials per arm. The volume and severity deltas are the kind of number that moves
between runs; the finding that does not depend on precision is the **zero** — no alternative
mechanism proposed in either arm, across every trial. Orchestrator and reviewers are
`openai/gpt-5.6-luna` (#154).

## Reproducing

The harness is throwaway and lives outside the repo — ~70 lines that construct a persona and call
`IReviewer.ReviewAsync`, not worth carrying as a fixture. What is needed to rebuild it is recorded
here, because a claim nobody can re-derive is one that quietly rots.

**Arms.** Two builds, selected by pointing a `PgRoot` MSBuild property at two worktrees:

| Section | Arm A | Arm B |
|---|---|---|
| The clause (Results 1–2) | `83d59d9` | `53bbe3a` |
| The follow-up | `53bbe3a` | `c8bb3a9` |

Verify the arms differ by checking the linked `PeanutGallery.Core.dll` for the clause text rather
than trusting the build wiring — getting that wrong silently produces a null result.

**The persona** is decoded from the `pg-panel:1:` marker on that PR's panel comment (base64
JSON; take the `guardrail-test-reliability` entry whole, with its model and temperature).
Reconstructing it by hand loses the `focus` field, which is the thing under test.

**The corpus** is the four successive guardrail-test commits on that PR, each diffed against the
merge base, giving the 102/149/255/343-line states. Run from that repository's checkout so its own
conventions file is picked up.

**Provenance, and what a public reader can and cannot check.** The corpus is identified by shape,
not by URL: four successive states of one guardrail test at **102 / 149 / 255 / 343** lines, over a
production change that stayed flat at ten lines, with the persona taken whole from the `pg-panel:1:`
marker on that PR's panel comment. Those five numbers pin the corpus exactly for anyone with the
checkout. Nobody else can re-derive it, and no manifest published here would change that, because
the repository is private and will stay private. Treat the quantitative results as reported rather
than reproduced, and weigh the qualitative ones - which are reproducible against any repository -
accordingly.

**Known limit.** Rebuilding this needs the private checkout and a paid model, so a
clean peanut-gallery checkout cannot re-derive it alone. That is a real ceiling on how much weight
these numbers carry, and it is the reason the conclusions lean on the qualitative results (the zero
in Result 2, completeness briefs 4 → 0) rather than on the percentages.

**Classification.** "Escalating" means the remedy is to extend the mechanism (regex, masker,
assignment or identifier coverage); "simplifying" means dropping the arbitrary match-count
threshold. Counted by regex over finding titles, so treat that split as indicative — the number
carrying the argument is the zero in Result 2, which needed no classifier.
