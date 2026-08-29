# The pull request's own baseline: what a continued turn may call established

**Status:** implemented and **measured; NOT cleared to ship on the protocol as written — gate 4
cannot now be met — though the worry behind it has been measured and is absent**
([`ab-pr-own-baseline.md`](ab-pr-own-baseline.md)). Gates 1, 3a and 3b pass and the central claim is
demonstrated: on one diff removing two public methods of identical shape, arm B files on the
established one 5/8 (arm A 6/8) and on the branch's own one 0/8 (arm A 4/8). **Gate 4 is unmet** —
`pr172`, the pre-registered crowd-out control, failed the ±20% clause at 1 vs 0 and that result
stands. A [second crowd-out cell](#amendment-2-a-crowd-out-cell-that-can-answer-2026-08-24-written-and-committed-before-any-call),
pre-registered before it ran, then answered the underlying question: **12 findings in each arm, equal
class by class — no crowd-out.** Shipping is therefore a judgement to overrule a stated gate, and it
belongs to the owner. ·
**Issue:** [#178](https://github.com/charles8051/peanut-gallery/issues/178) ·
**Predecessor:** [`finding-scope`](../finding-scope/spec.md), built, measured and
[rejected](../finding-scope/ab-finding-scope.md) — its conclusion is this feature's premise ·
**Related:** [#168](https://github.com/charles8051/peanut-gallery/issues/168) (the same question
along the space axis)

## Problem

Turn 1 of a stateful session is given the whole pull request diff. Turn 2+ is given only the delta
since the last reviewed SHA (`Commands.ResolveDeltaAsync` → `GetCompareDiffAsync`), which is the
design and is right for cost. But it means code an **earlier turn of the same pull request**
introduced is, in the reviewer's view, indistinguishable from code that has been on the base branch
for years. So when a later turn renames or reworks the branch's own work, the delta shows the
deletion of an apparently long-established symbol and the panel reports a breaking API change.

On [#175](https://github.com/charles8051/peanut-gallery/pull/175), turn 1 added
`Trajectory.Of(IReadOnlyList<Turn>)`. Acting on a turn-1 finding, turn 2 renamed it to `OfTurns`.
Two personas then filed `major`, and the adversarial pass upheld both:

> `bug-hunter`: "The previously public `Trajectory.Of(IReadOnlyList<Turn>)` has been renamed to
> `OfTurns`. Any existing or downstream caller … now fails to compile, and compiled consumers lose
> the referenced method."

Neither `Turn` nor that overload has ever existed on `main` — `git show
origin/main:src/PeanutGallery.Core/Trajectory.cs` at the time, and `git log -S`, both say so. There
are no downstream callers and no compiled consumers; the "mass test changes" the second persona
cited as evidence of the break are tests the same pull request added.

**This defect specifically punishes acting on review feedback, and it compounds** — each correction
creates a fresh delta in which the previous correction reads as an unexplained deletion. That is a
plausible contributor to the long-tail turn counts behind #167.

## The constraint this design starts from

[`ab-finding-scope.md`](../finding-scope/ab-finding-scope.md) asked a model to self-report whether a
hazard was introduced by the diff or inherited from around it. Across 48 arm-B trials it emitted the
`pre-existing` verdict **zero times**; 67 of 67 findings came back `introduced`. Its Result 4 is the
part that binds this feature:

> Sibling context is not being ignored: it is being used **as a contrast detector**. A sibling that
> differs from the diff is evidence the reviewer volunteers. A sibling that matches the diff is
> invisible … Asking a model to self-report scope is the wrong mechanism, not a wrong prompt. Derive
> scope from a **baseline** rather than asking a model.

The issue's own option (1) — "tell it plainly … that symbols removed in this delta *may* have been
introduced by an earlier turn" — is exactly the shape that has already been measured and cost 96
calls. **A prompt sentence inviting the model to consider a question is not what this ships.** What
ships is a fact the model does not have to work out.

## What is built

### The fact: `OwnRemovals` (pure core)

`OwnRemovals.Of(Diff delta, Diff? cumulative)` — diffs in, a set of lines out. No model, no clock,
no IO, no repository access, total.

Write `count(L, F, R)` for the number of times trimmed line text `L` occurs in file `F` at revision
`R`. The two diffs supply the two differences directly:

| Diff | What its `+`/`−` lines are | Difference it supplies |
|---|---|---|
| delta (last-reviewed → head) | removals − additions | `count(lastReviewed) − count(head)` |
| cumulative (merge base → head) | additions − removals | `count(head) − count(base)` |

Summed, `count(head)` cancels and what remains is `count(lastReviewed) − count(base)`. **Strictly
positive means the last-reviewed tree held more copies of that line than the base did**, and the
surplus can only have come from this pull request — so removing one of them cannot break anything
that predates it.

The sum is exact even though a diff shows only its hunks: every region a diff does not show is
byte-identical on both sides, so it contributes equally to both counts and cancels. That is what
makes the answer decidable from two diffs alone.

The claim is made only when **both** the per-file and the repo-wide sums are positive. Each closes a
hole the other leaves open, and a conjunction can only drop claims:

- **Per-file alone** mis-attributes code the pull request *moved between files* (an earlier turn
  moves a pre-existing method from `A` to `B`; a later turn deletes it from `B`, whose own
  arithmetic sees a surplus because `B` never held it at the base), and mis-attributes a rename
  whose detection differs between the two diffs, where the base-side evidence lands under a path the
  delta never names.
- **Repo-wide alone** is blind to a line text that is genuinely this branch's in one file while a
  different file removes an established copy of the same text.

### The statement: one block in the continued-turn prompt

`SessionPlanner.BuildContinuedUser` appends, after the delta and before the update instruction:

```
These lines, removed in the changes above, were added by an EARLIER TURN of this same pull
request. They are not on the base branch, so no established caller, compiled consumer or
downstream user can ever have depended on them, and removing or renaming them is not a
breaking change:
  src/PeanutGallery.Core/Trajectory.cs:
    - public static Trajectory? Of(IReadOnlyList<Turn> turnsOldestFirst)
    - return Of(lifted);
```

Capped at 40 lines and 160 characters per line, with an honest `(and N further line(s) …)` when it
truncates. **Nothing is emitted** when the answer is `Unknown` or empty — silence is the pre-#178
prompt exactly, and a hedge would be the question form creeping back in through the renderer.

### The shell: one cumulative fetch per run

`ReviewRunRequest.Baseline` is a `Diff?` the shell resolves **once per run**, because it is
persona-independent: every persona's delta differs, the pull request's own baseline does not.
`ReviewRunner` derives `OwnRemovals` per persona from the **filtered** delta (what the model
actually receives, re-derived for each rung of the shrink ladder) and hands it to `SessionPlanner`.

## The proportionality call

The [proportionality clause](../../../src/PeanutGallery.Core/PersonaPrompt.cs) says that if a smaller
mechanism gets most of the benefit, say that rather than building the larger one. Applied here, four
times:

**1. The cheapest option that would work is not available.** The smallest possible change is a
prompt sentence asking the reviewer to consider the possibility. That is option (1) in the issue,
and it is the mechanism the finding-scope A/B already spent 96 calls falsifying. "Smaller" does not
outrank "measured not to work".

**2. The baseline is fetched, not carried — and in the CLI it costs nothing.** The issue's option
(3) is to carry the pull request's introduced set forward inside `ReviewSession`. Rejected: the
session is persisted inside a **size-capped PR comment**, once per persona in the panel blob, so the
set would be duplicated N times in the one place with a hard budget; it drifts on a force-push or a
rebased base; and it needs codec work plus a migration for sessions written before it existed.

Against that, `GetPullRequestDiffAsync` is one call whose answer is exactly right by construction.
And the CLI **was already making it**: `Commands.ReviewAsync` resolved the cumulative diff after the
run to compute `DiffShape` for the metrics line. That fetch is hoisted above the run and spent
twice, so the baseline costs this shell **zero additional API calls** — only the hoist. The desktop
shell pays one extra call per run, which is a GUI action, not a CI loop.

**3. The block is not a second diff.** It could have named every removed line, or reported per-file
counts, or restated the whole cumulative diff. It names distinct line texts, filtered to lines that
carry an identifier (a `}` cannot be mistaken for established API), capped at 40. #175 turn 2 — the
real delta, against the real merge base — attributes **9 lines across 2 files**, so the cap is not
the operating regime; it is there so a mechanical rework cannot turn the block into a token sink.

**4. Nothing acts on the fact.** No gating, no severity adjustment, no suppression of findings, no
new field on `Finding`, no persistence, no rendering in the PR comment. The reviewer is told a true
thing and decides. Building a suppression path would be the dismissal license wired directly into
the tool, and it is also the thing that could not be measured cheaply.

## Decisions worth defending

### Identical line text: a multiset, not identities

A diff cannot say *which* physical occurrence of an identical line it removed, and no prompt wording
recovers that. So the question asked is only ever about counts. One copy at the base and one removal
in the delta is **not** attributed; two removals against one base copy is a surplus of exactly one
and the text **is** reported. The prompt's claim is therefore about the *text*, which is the right
grain: what the reviewer is about to call established API is a text it read in the diff.

### Trimmed comparison, and why it can only lose claims

Line texts are compared trimmed. This is load-bearing rather than cosmetic: if an earlier turn
reindented an established line and this turn deletes it, the delta removes the reindented text while
the cumulative diff removes the base text, and a raw comparison finds no match, sees a surplus, and
attributes an established line to the branch.

Trimming merges texts into coarser classes. Every class's value is the **sum** of its members' —
all three counts are additive — and a sum of non-positives is non-positive, so merging can turn a
positive into a non-positive but never the reverse. The coarser comparison drops claims and cannot
invent one.

### Totality: which way it must fail

A wrong "this pull request introduced it" tells a reviewer that a genuine breaking change cannot
break anything. That is strictly worse than the bug being fixed, so every degradation points at
silence:

| Input | Result |
|---|---|
| no cumulative diff (fetch failed, offline `--diff`, first turn) | `Unknown` — no block |
| empty or whitespace cumulative diff | `Unknown` — no block |
| cumulative text that parsed to **no files** (an error page, a JSON error body, a truncation notice) | `Unknown` — no block |
| a file the cumulative diff never mentions | attributed — that file is byte-identical at base and head, so a line removed from it existed only between two turns of this branch |

The third row is the one worth spelling out. `Diff.Parse` is total, so text that is not a diff comes
back non-empty with an empty file list — and an empty file list is indistinguishable, to the
arithmetic, from "base and head are identical", under which **every** removal is attributed. That is
rejected explicitly rather than believed.

**Residual risk, stated:** a cumulative diff that is *silently truncated* would under-report base-side
removals and could manufacture a claim. GitHub's `.diff` media type answers an over-large diff with
an error rather than a partial body, so this path degrades to `Unknown` today; a future source that
truncates silently would need a completeness signal before it could be used as a baseline.

### The delta is the filtered one

`OwnRemovals` is derived from the diff the model actually receives — after `DiffFilter`, and again
for each rung of the shrink ladder — never from one it was not shown. A fact about lines the
reviewer cannot see would be worse than no fact.

## Pre-registered A/B

Written **before any trial**, and recorded here so it cannot be adjusted afterwards to fit what came
out. **This has not been run.** The structure is the finding-scope write-up's own recommendation,
taken literally:

> A staged gate. "Does it ever answer correctly on the cell built for it?" costs 8 calls and would
> have stopped this at trial 8 instead of trial 96.

So the corpus is bought in stages, and **the wider corpus is only bought if the cheap gate passes**.

### Stage 0 — the arithmetic, before a single call (0 calls)

Run `OwnRemovals.Of` over the **real** #175 diffs — delta `56cb411..e79abb9`, cumulative
`6a2fbe5..e79abb9` (its merge base is `6a2fbe5`) — and check that the attributed set contains the
declaration the panel called established. If the derivation does not attribute it, no prompt can
help and no calls are worth spending.

> **Already run, and recorded here as the pre-condition it is.** `IsKnown = true`, **9 lines across
> 2 files**: `src/PeanutGallery.Core/Trajectory.cs` and
> `tests/PeanutGallery.Core.Tests/TrajectoryTests.cs`. The set contains
> `public static Trajectory? Of(IReadOnlyList<Turn> turnsOldestFirst)` — the exact declaration
> `bug-hunter` quoted — together with `return Of(lifted);`, `if (Of(turns) is { } t)` and
> `var t = Trajectory.Of(turns);`, which are precisely the "existing callers" the personas claimed
> would break. Stage 0 passes.

### Arms

Two **builds**, because the block is a compiled string; verify the linked `PeanutGallery.Core.dll`
differs rather than trusting the build wiring, as
[`ab-disproportion-lens.md`](../auto-panel/ab-disproportion-lens.md) established and
[`ab-finding-scope.md`](../finding-scope/ab-finding-scope.md) kept:

| Arm | Build | Probe (`#US` heap, UTF-16LE) |
|---|---|---|
| A | this branch's merge base on `main` | `added by an EARLIER TURN` **absent** |
| B | this branch | `added by an EARLIER TURN` **present** |

Arm A is also run with `Baseline` supplied, so the arms differ in the block and in nothing else.

One fixed diff-tier persona: **`bug-hunter`**, the persona whose #175 finding is quoted in the issue.
Findings are scored on `reply.Text`, never on parsed `Finding`s — a parser must not be able to
launder the failure under test.

### Stage 1 — the cell the feature was built for (16 calls)

| Cell | Prior session | Delta | Baseline | Correct behaviour |
|---|---|---|---|---|
| `rename-own` | #175's turn-1 session, verbatim | `56cb411..e79abb9` | `6a2fbe5..e79abb9` | **no** breaking-change finding against `Trajectory.Of` |

**8 trials per arm.** A trial counts as a *false breaking-change finding* iff the reply contains a
finding that (a) asserts a break to callers, consumers, compilation or compatibility, **and** (b)
names a line in the stage-0 attributed set. Every scored reply is quoted verbatim in the write-up.

**Gate 1 — both clauses, or nothing further is bought:**

| # | Target | Why this number |
|---|---|---|
| 1a | **arm A ≥ 5/8** files the false finding | the probe must reproduce the defect before arm B's behaviour on it means anything. The finding-scope A/B's most expensive lesson was cells that produced no data; this checks that first. |
| 1b | **arm B ≤ 1/8** files it | the fact is stated flatly and names the exact symbol; if the reviewer still calls it a break more than once in eight, stating facts is not the mechanism either and the same trap has been walked into twice. |

**If 1a fails the result is INCONCLUSIVE, not a pass, and stage 2 is not bought.** If 1a holds and 1b
fails, **do not ship** — write it up and stop, at a cost of 16 calls.

> **Run. Both clauses hold: arm A 5/8, arm B 0/8** — recorded in full in
> [`ab-pr-own-baseline.md`](ab-pr-own-baseline.md) (Fisher exact, two-tailed, p = 0.026). 16 calls plus one
> pre-flight, `bug-hunter`, scored on `reply.Text`, arms verified to differ in the `#US` heap of the
> linked `PeanutGallery.Core.dll` and in exactly +855 prompt characters. Clause 1a is met with **zero
> margin** — one trial the other way and this would have been INCONCLUSIVE.
>
> Arm B still *names* the rename in 8/8 summaries and declines to call it a break, so the block is
> not simply pushing the subject out of the reply. But arm B filed **no findings at all** on this
> cell, and arm A's only findings were the false one, so "silenced the false finding" and "silenced
> findings" are numerically indistinguishable here. That is what stage 2's gates 2–4 exist to
> separate, and **stage 1 passing is not a ship verdict**. Stage 2 was deliberately not run.

### Stage 2 — the direction that costs more than the bug (48 calls), only if stage 1 passes

Stage 1 can only show the block silences a finding. The question that decides shipping is whether it
silences the *right* ones. A block that teaches a reviewer to stop calling breaking changes is worse
than #178.

| Cell | Shape | Correct behaviour |
|---|---|---|
| `real-break` | a continued turn whose delta removes a genuinely established public method — one present at the merge base, so `OwnRemovals` attributes **nothing** and no block is emitted | **both** arms still file the breaking-change finding |
| `mixed` | one delta removing two symbols: one this branch introduced (attributed) and one established (not attributed) | file on the established one, not on the introduced one |
| `pr172` | a real prior PR's turn-2 delta with its real baseline, as the crowd-out control | judged by finding count and title overlap |

3 × 8 × 2 = **48 calls**. Total if both stages run: **64**.

**Ship gates (all must hold):**

| # | Target | Why |
|---|---|---|
| 1 | stage 1: A ≥ 5/8 **and** B ≤ 1/8 | the defect is reproduced and fixed |
| 2 | `real-break`: arm B files the finding in **≥ 7/8**, and never in fewer trials than arm A − 1 | the fix must not generalise into "removals are fine". This gate outranks gate 1: failing it means shipping a tool that hides real breaks. |
| 3 | `mixed`: arm B files on the established symbol **≥ 6/8** and on the introduced symbol **≤ 1/8** | the discrimination is the product; a reviewer that goes quiet on both has not learned anything |
| 4 | crowd-out: arm B's total finding count within **±20%** of arm A's per cell, with no title class present in A and absent in B across two or more cells | the control the [`yagni` revert](../auto-panel/ab-yagni-lens.md) makes mandatory |

> **Run, and it produced no data. Recorded in
> [`ab-pr-own-baseline.md`](ab-pr-own-baseline.md).** 48 calls yielded **one** finding in total.
>
> | Gate | Target | Observed | |
> |---|---|---|---|
> | 2 · `real-break` | arm B ≥ 7/8 | arm B **0/8** — and arm A **0/8**, from a byte-identical prompt | **not met** |
> | 3 · `mixed` | B on established ≥ 6/8; on introduced ≤ 1/8 | **0/8** and **0/8**; arm A also 0/8 on the established symbol | **not met** on the clause that matters |
> | 4 · crowd-out | B within ±20% of A per cell | 0-0, 0-0, **1-0** on `pr172` | **unevaluable** at this power |
>
> `bug-hunter` never filed a breaking-change finding on the stage-2 probes in **either** arm, so the
> corpus cannot show that the block hides real breaks *or* that it does not. **Gate 2 is not passed,
> it outranks gate 1, and INCONCLUSIVE is not a pass — so this does not ship on this evidence.**
> Nothing measured indicts the implementation: `OwnRemovals` attributed the right lines on `mixed`
> (all 8 of the branch's own, none of the established symbol's) and correctly attributed nothing on
> `real-break`.
>
> The structural lesson, and the thing to fix before any re-run: **stage 2 had no equivalent of
> stage 1's clause 1a.** Stage 1 could not be misread because it required arm A to reproduce the
> defect first. Gates 2 and 3 required no such precondition, and that is why 48 calls bought one
> finding. Note also that on `real-break` no block is emitted *by definition*, so its two arms are
> the same build compared against itself — a base-rate check, not an A/B.

**Revert trigger after shipping:** one observed real-PR case where a genuine breaking change went
unreported while the block named the symbol is written up here; two on distinct PRs revert the
feature. A hidden break is not a nit.

### Amendment 1 to the stage-2 protocol (2026-08-24), written and committed BEFORE any call

Authorised after [stage 2 returned no data](ab-pr-own-baseline.md#stage-2--the-direction-that-costs-more-than-the-bug):
across 48 calls `bug-hunter` filed a breaking-change finding **zero times, in both arms**, so gates 2
and 3 were unevaluable and gate 4 had one finding to work with. This amendment redesigns the
measurement. **It is committed before the first call of the amended run**, and the commit timestamp
is the evidence — a pre-registration written after the data is not one, and the rest of this
document's authority depends on that distinction holding.

Everything above stays as written. Nothing here is deleted or edited; the original stage-2
pre-registration remains the record of what was promised and what was spent. **No threshold below is
selected by any stage-2 number.** Each is derived from a stated argument, and where a threshold is
re-used it is re-used from text that predates the data.

#### Amendment 1a — `real-break` is deleted as an A/B cell, because it is a provable no-op

Not because it produced no data. Because it **cannot** produce data, and that was true before the
first call was spent on it.

`SessionPlanner.AppendOwnRemovals` opens with:

```csharp
if (own is not { IsKnown: true, HasAny: true })
{
    return;
}
```

`real-break` is *defined* as a delta whose removals all predate the branch, so `OwnRemovals.Of`
returns `None` (`IsKnown: true`, `Files: []`, therefore `HasAny: false`) and the block is never
appended. The arms are byte-identical by construction — confirmed empirically in the stage-2 run,
where `diff prompt-A.txt prompt-B.txt` on that cell returned nothing at all. **A cell in which the
two arms are the same build compared against itself cannot attribute an effect to the feature, at
any trial count, for any persona, whatever the reviewer does.** Keeping it would buy 16 calls of
sampling noise.

The distinction matters and is kept explicit: a cell that *cannot* show an effect is a design error,
and is removed on the argument. A cell that *failed to* show one is a measurement outcome, and is
reported. `real-break` is the former; `mixed` at stage 2 was the latter.

**Replacement, at zero calls: two unit tests already on this branch**, which together assert the
whole of what the cell could have asserted:

| Link in the argument | Test |
|---|---|
| a genuine-break delta yields `None` | `OwnRemovalsTests.A_line_that_predates_the_pull_request_is_not_attributed_to_it` — `Assert.Same(OwnRemovals.None, own)` |
| `None` yields no block | `SessionTests.Nothing_is_said_when_nothing_could_be_established` — `Assert.DoesNotContain("EARLIER TURN", …)` for `null`, `Unknown` and `None` alike |

Nothing needs to be written. The claim "this feature cannot change behaviour on a pure genuine-break
diff" is a theorem about the early return, and these two tests pin both of its premises.

**What this costs, stated honestly:** the *risk* `real-break` was meant to cover — a reviewer taught
by the block to stop calling breaking changes in general — is now covered only where the block is
actually present, which is `mixed`. That is the correct place to test it, because it is the only
place the feature exists. But a diffuse "the reviewer has learned to distrust removals" effect that
persisted into a later, blockless turn would not be caught by anything here, and is not claimed to
be.

#### Amendment 1b — `mixed` gets a stage-1-style precondition

**Arm A must file the breaking-change finding against the ESTABLISHED symbol in ≥ 5/8 trials before
arm B's number on that symbol means anything.** If it does not, the result is INCONCLUSIVE, the
arm-B trials are **not bought**, and the run stops.

The argument is the one stage 1 already makes and stage 2 omitted: a cell where the correct
behaviour is "file this finding" tells you nothing about a suppression mechanism unless the
un-suppressed arm actually files it. Stage 1 carries clause 1a for exactly this reason; gates 2 and
3 carried no equivalent. The threshold **5/8 is copied from clause 1a**, which was fixed before any
call of any stage — it is not a new number chosen now.

**Order of operations, binding:** the 8 arm-A trials run **first**, and the arm-B build is not
invoked until the precondition is evaluated.

The 8 arm-A precondition trials **are** arm A's 8 trials for gate 3a; they are not re-drawn. This
introduces a selection effect — arm A's count is conditioned on being ≥ 5 — which inflates arm A's
observed rate and therefore makes the non-inferiority rule below **harder** for arm B to pass. The
bias runs against the feature, which is the direction a safety gate should err in, so it is accepted
rather than paid for with 8 more calls.

#### Amendment 1c — gate 3's established-symbol threshold, re-derived

The pre-registered gate 3 asks arm B for **≥ 6/8** on the established symbol. That threshold is
withdrawn, on an argument that does not depend on any observed number:

**It asks the wrong question.** Gate 3 exists to detect *suppression* — the block causing a finding
not to be filed that otherwise would have been. Suppression is a claim about a **counterfactual**,
and the counterfactual is arm A on the same cell, not a constant. An absolute 6/8 conflates two
different failures: a block that suppresses, and a reviewer that was never going to file at 6/8 in
the first place. Worse, it can demand that arm B **outperform** arm A — if the un-suppressed arm
reaches only 5/8, a 6/8 bar requires the *blocked* build to file more often than the unblocked one,
which is not what non-suppression means and is not something a fix could be expected to deliver.
Note this defect is visible from clause 1a alone: 1a admits an arm A as low as 5/8, and 5 < 6. It
needed no stage-2 data to see, and the stage-2 data is not what shows it.

**Replacement — gate 3a, non-inferiority against arm A on the same cell:**

> **arm B's count on the established symbol ≥ (arm A's count on the established symbol) − 1**

**Why a margin of exactly one trial.** It is not invented here. The pre-registered **gate 2 already
fixed this margin, for this purpose, before any stage-2 call**: "and never in fewer trials than arm
A − 1". Amendment 1c generalises that clause from gate 2 to gate 3 rather than choosing a new
number, which is the strongest available guarantee that no result selected it.

It is also the only defensible margin at this trial count. At n = 8, requiring exact equality
(B ≥ A) would fail on sampling noise alone: with two arms drawing from the same true rate near 0.6,
a one-trial gap is ordinary. A margin of 2 would admit a 25-point true suppression as a pass. One
trial is the smallest margin that is not noise-dominated.

**And its power, stated in advance rather than discovered afterwards.** The rule fails iff
B ≤ A − 2. Taking arm A at the minimum the precondition admits (5/8, so the rule fails at B ≤ 3),
and arm B drawing from a true rate p:

| Arm B's true rate | Interpretation | P(gate 3a fails) |
|---|---|---|
| 0.625 | identical to arm A | 0.14 (false alarm) |
| 0.500 | 12.5-point suppression | 0.36 |
| 0.375 | 25-point suppression | 0.65 |
| 0.250 | 37.5-point suppression | 0.89 |
| 0.000 | fully silenced | 1.00 |

**This gate detects gross suppression and nothing subtler.** A real 12.5-point suppression passes it
about two times in three. That is a property of 8 trials, not of the rule, and it must be carried
into any conclusion drawn from a pass: *"gate 3a passed"* means *"no gross suppression was
observed"*, never *"the block does not suppress"*.

> **Correction (2026-08-24, after the run — an arithmetic fix, not an amendment).** The table above
> is **wrong**, and the panel's `contrarian` caught it. It fixes arm A at 5, the minimum the
> precondition admits, but the rule's sampling distribution has arm A *random* and conditioned on
> A >= 5 — so the failure threshold A - 2 lands on 3, 4, 5 or 6, not always on 3. Conditioning raises
> the threshold on average, so the tabulated figures understate **both** the detection rate and the
> false-alarm rate.
>
> **The rule is untouched** — `arm B >= arm A - 1`, exactly as pre-registered, and it is the rule
> that was applied. Only the descriptive power claim attached to it is corrected. Fixing a
> miscomputation is not the same act as moving a threshold, and no threshold moves here.
>
> Recomputed as P(B <= A - 2 | A >= 5), with A ~ Bin(8, pA), B ~ Bin(8, pB), independent. **pA is a
> nuisance parameter this design does not identify** — arm A's observed 6/8 is itself conditioned on
> A >= 5 — so it is shown as a range rather than estimated:
>
> | pA down / pB across | 0.750 | 0.625 | 0.500 | 0.375 | 0.250 | 0.000 |
> |---|---|---|---|---|---|---|
> | **0.875** | 0.372 | 0.628 | 0.822 | 0.937 | 0.986 | 1.000 |
> | **0.750** | 0.215 | 0.442 | 0.676 | 0.859 | 0.962 | 1.000 |
> | **0.625** | 0.131 | 0.324 | 0.568 | 0.793 | 0.940 | 1.000 |
> | **0.500** | 0.085 | 0.251 | 0.494 | 0.746 | 0.923 | 1.000 |
>
> The diagonal is the false-alarm rate (arms truly identical): **0.13 to 0.37**, against the 0.14
> claimed above. Across a row is detection: at pA = 0.750 a true 25-point suppression is caught 0.68
> of the time, so a **pass** still leaves roughly one chance in three that such a suppression is
> present and was missed.
>
> The qualitative conclusion is unchanged and, if anything, firmer: at n = 8 this gate speaks only
> about gross suppression, and its pass is weak evidence in both directions. **No ship decision in
> this document rests on a figure from either table** — the verdict turns on gate 4, which is unmet.

**Gate 3b is unchanged:** arm B files on the **introduced** symbol in **≤ 1/8**. It needs no
counterfactual — the correct answer is an absolute zero, and stage 1 already showed arm B reaching
0/8 on this exact rename.

**Gate 3 passes only if 3a and 3b both hold**, and only if the precondition held first.

#### The redesigned `mixed` probe, fixed here before it is run

Stage 2's `mixed` renamed an incidental display helper (`Sha.Short`) from a prior session with no
open findings, and neither arm ever filed. Stage 1's cell worked. So this probe is built on stage
1's shape, with the established removal added into the same diff — that co-occurrence being the
entire risk gate 3 exists to test.

| Piece | Value |
|---|---|
| base | `6a2fbe5` (as stage 1) |
| turn 1 | `56cb411` — PR #175's real turn 1, which added `Trajectory.Of(IReadOnlyList<Turn>)` |
| turn 2 | `e79abb9` (PR #175's real turn 2, renaming the branch's own `Of` → `OfTurns`) **plus** a rename of the **established** `Trajectory.Of(IReadOnlyList<DiffShape>)` → `OfShapes`, all 20 call sites and its doc comments. Builds clean (`dotnet build PeanutGallery.slnx -c Release`, 0 errors) |
| delta | turn 1 → turn 2, 2 files, 19.9 KB |
| baseline | base → turn 2, 4 files, 52.1 KB |
| prior session | PR #175's real turn-1 `bug-hunter` session, verbatim from `userContentEdits` — turn 1, **one open finding**, about the introduced symbol |
| persona | `bug-hunter`, from the pinned panel, unchanged |

- **The introduced symbol** is `Trajectory.Of(IReadOnlyList<Turn>)` — the exact rename stage 1
  measured, where arm A filed 5/8 and arm B 0/8.
- **The established symbol** is `Trajectory.Of(IReadOnlyList<DiffShape>)` — present on `main` at the
  merge base, with an in-repo production caller (`Trajectory.ByPr`) and 20 test call sites. Removing
  it is a genuine breaking change.

Both are public statics on the same type, in the same file, removed in the same delta, distinguished
by nothing except baseline membership.

**Verified before writing this, at zero calls:** `OwnRemovals.Of` over the two real diffs returns
`IsKnown = true` with **10 lines across 2 files**, and the emitted block names
`public static Trajectory? Of(IReadOnlyList<Turn> turnsOldestFirst)` and its call sites while naming
**neither** `public static Trajectory? Of(IReadOnlyList<DiffShape> runsOldestFirst)` nor `OfShapes`.
The two prompts differ by **+946 characters** and nothing else.

**One confound, declared in advance.** One attributed line is the bare opener
`var t = Trajectory.Of([`, whose text is shared between the branch's new `Turn` tests (5 copies
added by turn 1) and 11 pre-existing `DiffShape` tests. The multiset arithmetic reports it correctly
— the branch really did add a surplus of five — and the same line appeared in stage 1's block. But
its *text* is one a reviewer could read as touching the established API. This makes the block
slightly **more** likely to suppress the established-symbol finding than a perfectly clean block
would be, i.e. it biases the run toward failing gate 3a. That is the conservative direction for a
safety gate, so the probe is used as built rather than being sanitised into something the shipping
feature would not actually produce.

#### Trial counts, spend and stopping rule

| Step | Calls | Stop condition |
|---|---|---|
| `mixed` arm A | 8 | if < 5 file on the established symbol → **INCONCLUSIVE, stop**, arm B unbought |
| `mixed` arm B | 8 | — |
| **Total** | **16** | |

#### What is unchanged and still binding

Scoring on `reply.Text`, never on parsed `Finding`s. The arms verified in the `#US` heap of the
linked `PeanutGallery.Core.dll` before any call. Every classified reply quoted verbatim, with
mentions-that-are-not-claims counted separately. `bug-hunter` fixed. Gate 4 (crowd-out) as written —
evaluated for its ±20% clause on `mixed`, with its "two or more cells" clause drawing on `mixed` and
the already-run `pr172`. Gates 1 and 2 as already recorded. The harness deleted afterwards.


### Amendment 2: a crowd-out cell that can answer (2026-08-24), written and committed BEFORE any call

Authorised after gate 4 was reported [unmet](ab-pr-own-baseline.md#gate-4-crowd-out-unmet). Committed
before the first call of the cell it describes, on the same discipline as
[amendment 1](#amendment-1-to-the-stage-2-protocol-2026-08-24-written-and-committed-before-any-call):
thresholds justified by reasoning, never by a number already seen. As before, nothing above is
deleted or edited.

#### What this amendment does NOT do

**It does not repair gate 4, and it is not a do-over of `pr172`.** `pr172` was the pre-registered
crowd-out control, it was run, it failed the ±20% clause (arm A 1, arm B 0), and **that result stands
and is not reinterpreted.** Exempting it now — on a minimum-yield rule invented after seeing its
outcome — is exactly the move commit `0065595` was built to prevent, and it stays closed.

So the conclusion this cell can license is fixed here, in advance, and it is narrow:

| Outcome of the new cell | Gate 4 | The crowd-out question |
|---|---|---|
| arm A < 5 findings | **remains UNMET** | **unmeasured**, and probably unmeasurable on this repo's corpus |
| arm A ≥ 5, ±20% holds, title clause holds | **remains UNMET** (`pr172` stands) | **measured; no crowd-out detected** |
| arm A ≥ 5, ±20% fails | **remains UNMET** | **measured; crowd-out detected** |

**In every branch the pre-registered ship criterion — "all gates hold" — is false, so the verdict
remains do-not-ship on the protocol as written.** What 16 calls buy is not a pass. They buy the
difference between *"the control was never evaluated"* and *"the control was evaluated and says X"*,
which is what a decider is actually choosing between. That is worth the calls; a gate repair would
not have been available at any price.

#### The cell: PR #173, turn 3

**Both structural constraints have to hold at once**, and the second is what `pr172` failed:

1. **The block must actually be emitted.** If nothing is attributed, `AppendOwnRemovals` returns
   early, the arms are byte-identical, and the cell is the same provable no-op that removed
   `real-break`. A crowd-out cell with no block measures nothing.
2. **Arm A must produce enough findings for a ±20% band to mean anything** (see the minimum below).

**Selection criterion, a property of the pull request and nothing else:** the repo's own metrics
ledger records how many findings `bug-hunter` raised on every historical run. That is
finding *yield*, it is recorded in `pg-metrics` blobs written months before this evaluation existed,
and it has no relationship to anything either arm did here. Surveying every multi-turn PR available:

| PR | `bug-hunter` raised, per turn | Files / added |
|---|---|---|
| **#173** | **3, 4, 2, 2** | 8 / 677–884 |
| #181 | 1, 1, 1, 1, 2, 3, 3 | 10–12 / 1256–2888 |
| #175 | 1, 1 | 4 / 638–742 |
| #172 | **0, 0, 0, 0, 0** | 3 / 498–577 |
| #171 | 0, 0, 0 | 6 / 135–177 |
| #170 | 0, 0, 0, 0, 0 | 2 / 121–257 |

**#173 has the highest and most consistent yield in the repository, and it is the only PR where this
persona reliably raises more than one finding per turn.** It is chosen on that basis.

Note what the same table says about `pr172`: `bug-hunter` raised **zero** findings on every one of
its five real runs. Its failure as a crowd-out cell was foreseeable from data that already existed,
and consulting this ledger before spending those 16 calls would have predicted it. That is recorded
as a lesson, not as an excuse — the result still stands.

**Which turn.** Declared before inspecting attribution: *the delta whose recorded `bug-hunter` yield
is highest, subject to constraint 1; ties broken by the larger delta.*

- Turn 2 (yield **4**, the highest) is **structurally excluded**: its turn-1 head `946beb7` was
  force-pushed away and is not an ancestor of `518a5e4`, so `base...head` resolves to the merge base
  and the delta coincides with the cumulative diff. Attribution is then empty by construction and no
  block is emitted — constraint 1 fails. (The spec's [force-push note](#out-of-scope) already says
  this is the correct behaviour there.)
- Turns 3 and 5 tie at yield **2**. Turn 3's delta is larger (13.7 KB / 3 files vs 9.9 KB / 2 files),
  so **turn 3** is the cell.

| Piece | Value |
|---|---|
| delta | `518a5e4...38a5661` — 3 files, 13.7 KB |
| baseline | `6a2fbe5...38a5661` — 8 files, 42.7 KB (merge base to the immutable head SHA) |
| prior session | #173's real turn-2 `bug-hunter` session, verbatim from `userContentEdits` (`Reviewed through 518a5e4 · turn 2`, edited 18:22:48Z) — **4 open findings**, one of them `major` |
| head | `38a5661ad02ad1ff8bb5c072caf08696128a7a69` |
| persona | `bug-hunter`, unchanged |

**Verified at zero calls, before this was written:** `OwnRemovals` attributes **27 lines across 2
files**, the block is emitted, and the two prompts differ by **+2,299 characters** and nothing else.
Constraint 1 holds.

#### The minimum arm-A finding count: 5

**Declared in advance, and derived arithmetically rather than from any observed count.**

Gate 4 asks whether arm B's total is within ±20% of arm A's. With arm A at N findings, arm B fails
when it differs by more than `0.2N`. The smallest difference two integer counts can have is **1**. So:

> a one-finding difference trips the gate whenever `1 > 0.2N`, i.e. whenever **N < 5**.

Below five, "±20%" is not a band at all — it is a demand for **exact equality**, since the minimum
observable perturbation already fails it. At `N = 5` the band `[4, 6]` finally tolerates the smallest
real difference, and the clause starts testing what it was written to test. Five is therefore the
smallest N at which the criterion is *interpretable*, and that is the whole justification. It does
not depend on any result, and it would have been the same number written before stage 2.

**Below 5, the cell is declared UNINFORMATIVE, not failed** — the distinction amendment 1a drew
between a cell that *cannot* show an effect and one that *failed to*. An uninformative cell is
reported as such and licenses no conclusion in either direction.

**Stated honestly alongside it:** a minimum of 5 makes the clause *interpretable*, not *powerful*. At
N = 5 only a drop of 2 or more findings fails, so this remains a coarse instrument, and a pass means
"no large difference in total yield", never "no crowd-out". No power figure is quoted here because
none has been computed for finding counts, and an uncomputed figure must not qualify a decision in
either direction.

#### Unchanged

The **±20% band** and the **title-class clause** ("no title class present in A and absent in B across
two or more cells") are exactly as pre-registered. Scoring on `reply.Text`. Arms verified in the
linked `PeanutGallery.Core.dll` `#US` heap. Every classified reply quoted verbatim.

#### Arms, and why arm B moves to the branch tip

| Arm | Build | `added by an EARLIER TURN` in `Core.dll` |
|---|---|---|
| A | `b8776e2` — the merge base, unchanged from every prior stage | absent |
| B | **`bba3595`** — the current branch tip, i.e. the shipping candidate | present (offset 233340) |

Stages 1 and 2R measured `8fcada2`, before the author's two manufacturing fixes. Arm B moves to the
tip because a crowd-out control should measure what would actually merge — and because the
[stage-0 recheck](ab-pr-own-baseline.md#stage-0-recheck-on-the-fixed-build-0-calls) proved the fixes
leave attribution byte-identical on every corpus in this evaluation, so the move costs no
comparability. Arm B derives the fact the way the fixed `ReviewRunner` now does:
`OwnRemovals.Of(rawDelta, baseline).OnlyIn(filteredDelta)`.

#### Trial counts and stopping rule

| Step | Calls | Stop condition |
|---|---|---|
| `pr173t3` arm A | 8 | if total findings < 5 → **UNINFORMATIVE, stop**, arm B unbought |
| `pr173t3` arm B | 8 | — |
| **Total** | **16** | |

Arm A runs first. Its 8 trials serve as both the minimum-yield check and arm A of the comparison;
they are not re-drawn. Unlike stage 2R's precondition this conditioning does not bias the comparison
in a known direction — it conditions on arm A's *total*, which is the denominator of the band rather
than a rate being compared — and it is recorded here so a reader can weigh it.

### What the A/B must not do

- **Do not re-run stage 2 alone.** `rename-own` is the cell that discriminates; the real-PR cell is
  a control and produces no verdict of its own.
- **Do not extend trials to rescue a failed 1b.** Extending trials is the remedy for a thin
  numerator, not for a mechanism that does not fire — the finding-scope write-up says so and its
  numerator was exactly zero across 48.
- **Do not score parsed `Finding`s.**

## Out of scope

- **Acting on the fact.** No suppression, no severity adjustment, no `Scope` field, nothing
  persisted, nothing rendered in the PR comment. See the proportionality call.
- **The one-shot path.** `PromptAssembly.Build` has no prior turn, so it has nothing to be confused
  about.
- **The verification pass.** `SessionPlanner.Verify` asks whether a finding is *true*; the skeptic
  reasons over the same conversation and therefore already carries the block in its prefix. It is not
  given a second, differently-worded copy.
- **Hazards rather than symbols** — [#168](https://github.com/charles8051/peanut-gallery/issues/168)
  and [`finding-scope`](../finding-scope/spec.md). The baseline established here is the mechanism
  that write-up says such a feature would need, but a hazard is a property of code rather than a line
  of it, and this feature does not claim to answer it.
- **Force-push and rebased-base handling.** `ResolveDeltaAsync` already falls back to the full PR
  diff when the compare has no common ancestor; on that path delta and cumulative coincide and the
  arithmetic attributes nothing, which is correct — the turn is reviewing the whole PR anyway.

## Open questions

- [ ] Should a *known-and-empty* baseline say so? Today `None` renders as silence, identical to
  `Unknown`. Stating "every line removed here was on the base branch" is equally decidable and would
  tell a reviewer a breaking-change claim is live — but that is the over-firing direction, and it is
  not what #178 is about. Deliberately not built; revisit only with evidence.
- [ ] Does the block belong in the **panel comment** as a reader-facing note? It would explain why a
  reviewer did *not* file something, which is normally invisible. Cost: a line per comment for a
  thing most readers never wondered about.

## Related

| Type | Link |
|---|---|
| Issue | [#178](https://github.com/charles8051/peanut-gallery/issues/178) |
| Stage-1 measurement | [`ab-pr-own-baseline.md`](ab-pr-own-baseline.md) |
| Predecessor (rejected) | [`finding-scope/spec.md`](../finding-scope/spec.md), [`ab-finding-scope.md`](../finding-scope/ab-finding-scope.md) |
| Process precedent | [`auto-panel/ab-yagni-lens.md`](../auto-panel/ab-yagni-lens.md) |
| The session this extends | [`stateful-sessions/spec.md`](../stateful-sessions/spec.md) |
| Core | [`OwnRemovals.cs`](../../../src/PeanutGallery.Core/OwnRemovals.cs), [`SessionPlanner.cs`](../../../src/PeanutGallery.Core/SessionPlanner.cs) |
