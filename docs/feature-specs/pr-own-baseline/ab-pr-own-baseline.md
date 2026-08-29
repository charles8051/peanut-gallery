# The pull request's own baseline: A/B evaluation (stages 1, 2, 2R and the crowd-out cell)

**Date:** 2026-08-23 · **Change under test:** [`claude/pg-pr-own-baseline`](https://github.com/charles8051/peanut-gallery/tree/claude/pg-pr-own-baseline) ·
**Feature:** [The pull request's own baseline](spec.md) · **Protocol:** [pre-registered in that spec](spec.md#pre-registered-ab) ·
**Issue:** [#178](https://github.com/charles8051/peanut-gallery/issues/178) ·
**Predecessor:** [`ab-finding-scope.md`](../finding-scope/ab-finding-scope.md), whose "if this is
proposed again" section is why this measurement is staged at all

> **Outcome: SHIPPED BY OVERRULE.** Gate 4 was failed and the decision to ship overrode it —
> see [The overrule](#the-overrule) for who decided, on what reasoning, and what it does not
> establish. The analysis below is unchanged and was written before that decision.
>
> **On the pre-registered protocol: DO NOT SHIP — but the worry behind the one unmet gate has
> been measured and is absent.** Two sentences, both true, and the gap between them is what a decider
> is choosing in.
>
> **Gate 4 cannot now be met.** `pr172` was the pre-registered crowd-out control; arm A filed 1
> finding and arm B 0, and zero is not within the required ±20% of one. A minimum-yield rule would
> have saved it, but pre-registering one after seeing that result was closed off by the same
> discipline that makes the rest of this readable.
>
> **The question gate 4 asks has now been answered.** A second crowd-out cell — chosen on historical
> finding yield from the repo's own metrics ledger, carrying a 27-line block, and clearing a
> pre-registered floor of 5 findings with 12 — returned **12 findings in arm B against 12 in arm A**,
> equal class by class (8/3/1 against 8/3/1). **No crowd-out.**
>
> | Stage | Cell | Result |
> |---|---|---|
> | 1 | `rename-own` | **PASS** — arm A 5/8, arm B 0/8, p = 0.026 |
> | 2 | `real-break` | no data; cell later **deleted as a provable no-op** |
> | 2 | `mixed` (v1) | no data — 0/8 in both arms; probe did not reach the persona |
> | 2 | `pr172` | 1 vs 0 — **gate 4's ±20% fails here, permanently** |
> | 2R | `mixed` (redesigned) | **PASS** — precondition 6/8; gate 3a 5 ≥ 5; gate 3b 0/8 |
> | — | stage-0 recheck on `bba3595` | **attribution unchanged** — 9 lines / 2 files; stages 1 and 2R stand |
> | **new** | **`pr173t3` crowd-out** | **12 vs 12** — no crowd-out; gate 4 still unmet |
>
> Every substantive question this protocol posed is answered favourably; one pre-registered criterion
> is permanently unsatisfiable because the cell chosen to test it could never have satisfied it.
> Shipping anyway is a defensible judgement — but it is a judgement to overrule a stated gate, and it
> belongs to the owner, not to an adjudicator relabelling a FAIL. Margins are not rounded away: gate
> 3a passed with **zero slack** and its power claim was
> [corrected after the run](spec.md#amendment-1c--gate-3s-established-symbol-threshold-re-derived).
> See [Decision](#decision).

This evaluation was run by an agent that did **not** write the feature, and that was told to try to
break it rather than to confirm it — the same arrangement, and for the same reason, as
[`ab-finding-scope.md`](../finding-scope/ab-finding-scope.md).

## Stage 1 — what was measured

The pre-registered stage-1 cell, unchanged:

| Cell | Prior session | Delta | Baseline | Correct behaviour |
|---|---|---|---|---|
| `rename-own` | #175's turn-1 session, verbatim | `56cb411..e79abb9` | `6a2fbe5..e79abb9` | **no** breaking-change finding against `Trajectory.Of` |

Eight trials per arm, two arms, **16 calls**. One fixed diff-tier persona, `bug-hunter` — the
persona whose #175 finding the issue quotes. Nothing about the cell, the trial count, the persona,
the probe or the scoring rule was changed after any result was seen.

**Scoring is on `reply.Text`.** `FindingsParser` was never called: a parser must not be able to
launder the failure under test. Every scored reply is quoted verbatim
[below](#every-reply-verbatim).

### The corpus is the real #175, reconstructed

Nothing here is synthetic. The pieces and where each came from:

| Input | Source |
|---|---|
| prior session | the panel comment's **turn-1 revision**, recovered from `userContentEdits` (the version stamped `Reviewed through 56cb411 · turn 1`, edited 18:22:19Z, immediately before the turn-2 run overwrote it). Decoded with `PanelSessionCodec.Extract`; `bug-hunter` is on turn 1 with **one** open finding, `LastSeenCommentId = 0` |
| persona | `PanelCodec.Extract` on that same revision — the pinned panel as it actually ran (`openrouter:openai/gpt-5.6-luna`, temperature 1.0), not a re-resolved one |
| delta | `GET /compare/56cb411...e79abb9` with `application/vnd.github.diff`, the exact call `ResolveDeltaAsync` makes — 2 files, 11.7 KB, 0 omitted after `DiffFilter` |
| baseline | `GET /compare/6a2fbe5...e79abb9`, same media type — 4 files, 44.5 KB |
| intent | PR #175's title and body |
| conventions | `CLAUDE.md` **at `e79abb9`** (the head the run checked out; no `.github/copilot-instructions.md` existed then) |
| new comments | none — the author's reply to the panel landed at 20:39Z, hours after the turn under test |

Recovering the turn-1 session mattered: the panel comment carries the session inline and is
overwritten in place, so the live comment holds the **turn-2** state — the one containing the very
findings under test. Reading it would have fed each arm its own conclusion.

### The arms are two builds, verified in the DLL

The block is a compiled string, so the arms are two builds, and the claim was checked against the
linked `PeanutGallery.Core.dll` rather than trusting the build wiring — the
[`disproportion`](../auto-panel/ab-disproportion-lens.md) precaution,
[kept by finding-scope](../finding-scope/ab-finding-scope.md), kept again:

| Arm | Build | `Core.dll` contains `added by an EARLIER TURN` (UTF-16LE, the `#US` heap) |
|---|---|---|
| A | `b8776e2` — this branch's merge base, which is current `main` | **no** (`find` → −1) |
| B | `8fcada2` — this branch | **yes** (`find` → offset 233032) |

No rebase was needed: the branch's merge base *is* `main`'s tip, so arm A is current `main` and the
two builds differ by this feature's commit alone.

**Arm A is run with the baseline supplied.** The driver resolves and parses the cumulative diff
identically in both arms and hands it to the planner; arm A's `SessionPlanner.Advance` simply has no
parameter to take it. That is the whole difference, and it is visible in the assembled prompts:

```
$ diff prompt-A.txt prompt-B.txt
284a285,297
> These lines, removed in the changes above, were added by an EARLIER TURN of this same pull
> request. …
>   src/PeanutGallery.Core/Trajectory.cs:
>     - new Dictionary<string, int>(StringComparer.Ordinal);
>     - return Of(lifted);
>     - /// <summary>Folds one PR's turns, oldest first — shapes AND the panel that sat each one, so both
>     - /// triggers can be read off the result.</summary>
>     - public static Trajectory? Of(IReadOnlyList<Turn> turnsOldestFirst)
>     - if (Of(turns) is { } t)
>   tests/PeanutGallery.Core.Tests/TrajectoryTests.cs:
>     - var t = Trajectory.Of(turns);
>     - var t = Trajectory.Of([
>     - Assert.Equal("alpha", Trajectory.Of(turns)!.RepeatLens);
```

Arm B's prompt is **+855 characters** and differs in nothing else. 17,806 → 18,661 characters.

### Stage 0 reproduced on the way through

The driver printed `OwnRemovals.Of` over the real diffs before the first call:
`IsKnown = true`, **9 lines across 2 files** — exactly what the spec's stage-0 record claims,
including the declaration `bug-hunter` quoted and the three call sites the personas named as
"existing callers". The stage-0 record stands as written.

### Deviations, both authorised and both stated

1. **A throwaway driver.** The one-shot `pg review` path cannot supply a prior session or a
   baseline, so a scratch driver called `SessionPlanner.Advance` directly with the corpus above and
   `ChatClientReviewer.CompleteAsync` against the real provider, writing `reply.Text` verbatim per
   trial. It was **deleted** when the measurement finished, as
   [finding-scope's](../finding-scope/ab-finding-scope.md) was. No harness code ships.
2. **Per-call timeout 600 s, not the CLI's 300 s default.** `new ChatClientReviewer(config.Providers)`
   uses the constructor default. Everything else matches a production run: 2 attempts, JSON mode off,
   40,000 max output tokens, `openai/gpt-5.6-luna` at temperature 1.0. Immaterial in the event — all
   16 calls returned on the **first** attempt, none truncated.

One pre-flight call (arm A, into a separate directory) was spent before the matrix to confirm the
driver captured raw text and that the DLL probe distinguished the arms. It filed the false finding.
It is **not** counted in the 8.

**Total spend across all stages: 100 calls** — 16 (stage 1 matrix) + 1 (stage-1 pre-flight) + 48
(stage 2 matrix) + 2 (stage-2 turn-1 scaffolding, one per probe revision) + 16 (stage 2R matrix) + 1
(one stage-2R call that failed in transport with no reply body and was re-issued) + 16 (the
`pr173t3` crowd-out matrix). The pre-registered budget was 64 for stages 1-2, 16 for the amended
stage 2R and 16 for the crowd-out cell; the four extra are the pre-flight, the scaffolding and the
transport retry, each named where it was spent and none of them scored. **The stage-0 recheck on the
fixed build cost nothing** - it is a pure function over diffs.

### Stage 1 result: arm A 5/8, arm B 0/8

A trial counts as a *false breaking-change finding* iff the reply contains a finding that (a)
asserts a break to callers, consumers, compilation or compatibility, **and** (b) names a line in the
stage-0 attributed set.

| Trial | Arm A | Arm B |
|---|---|---|
| 1 | **filed** | — |
| 2 | **filed** | — |
| 3 | — | — |
| 4 | **filed** | — |
| 5 | — | — |
| 6 | **filed** | — |
| 7 | — | — |
| 8 | **filed** | — |
| **Total** | **5/8** | **0/8** |

| # | Gate | Target | Observed | |
|---|---|---|---|---|
| 1a | arm A reproduces the defect | ≥ 5/8 | **5/8** | **met, exactly** |
| 1b | arm B does not file it | ≤ 1/8 | **0/8** | **met** |

Fisher exact on the 2 × 2 (5 filed / 3 not, vs 0 filed / 8 not): two-tailed **p = 0.026**,
one-sided p = 0.013.

**No borderline calls.** Every arm-A filer named `Trajectory.Of(IReadOnlyList<Turn>)` — which is in
the attributed set verbatim as `public static Trajectory? Of(IReadOnlyList<Turn> turnsOldestFirst)` —
and most also named `Trajectory.Of(turns)`, which is in the set as
`var t = Trajectory.Of(turns);`. Clause (b) is satisfied by quoted text, not by inference. Arm B
returned an empty findings list in all 8 trials, so clause (a) has nothing to test.

### Mentions that are not claims: 3 in arm A, 8 in arm B

The distinction a lenient scorer would blur. A reply that *mentions* the rename without claiming
breakage is **not** filing:

- **Arm A, 3 trials** (A-03, A-05, A-07) mention the rename in the summary and file nothing —
  e.g. A-03: *"The empty collection-expression overload ambiguity is fixed by renaming the
  turn-folding overload to OfTurns; no additional correctness bugs found in this update."*
  The defect is **not deterministic**: this reviewer already got it right 3 times in 8 without any
  help.
- **Arm B, 8 trials of 8** mention the rename and none claims breakage — e.g. B-06: *"The collection-expression
  overload ambiguity is fixed by renaming the turn-folding API to OfTurns; the snapshot
  implementation also prevents later panel mutations from affecting turns. No open correctness
  findings remain."* Arm B is not blind to the rename; it declines to call it a break.

That arm B still *sees* and *describes* the rename is the more informative half of the result. A
block that had simply pushed the whole subject out of the reply would look identical in the counts
and mean something quite different.

### The turn's other job was done in 16/16

Every reply in both arms moved the turn-1 finding (`The new Of overload makes empty
collection-expression calls ambiguous`) into `resolved` — 8/8 in arm A, 8/8 in arm B. The block did
not disturb the resolve/carry-forward behaviour it sits next to. This is a free control, not a
substitute for gate 4.

### What stage 1 cannot say

**Arm B filed zero findings of any kind.** So did arm A on its 3 non-filing trials, and arm A's
total finding count across the cell is 5 — all of them the false one. So on this cell the two
readings

- "the block silenced exactly the false finding", and
- "the block silenced findings",

**produce identical numbers**, and stage 1 cannot separate them. That is by design: it is precisely
what gate 2 (`real-break`), gate 3 (`mixed`) and gate 4 (crowd-out) are for, and the spec already
says gate 2 **outranks** gate 1 because failing it means shipping a tool that hides real breaks.
Nothing here should be read as evidence about them.

Two further limits worth naming:

1. **Clause 1a is met with zero margin.** 5/8 is 62.5%; the exact binomial 95% interval is roughly
   24–91%. Had one arm-A trial gone the other way the whole result would have been INCONCLUSIVE.
   The probe is adequate, not comfortable — and the pre-flight call (a 9th arm-A sample, excluded by
   protocol) also filed, which is reassuring but is *not* a license to fold it in.
2. **One cell, one persona, one PR.** `contrarian` filed the same false `major` on the real #175 and
   was not measured here; the protocol fixes `bug-hunter` and that was honoured.

## Stage 2 — the direction that costs more than the bug

Authorised and run after stage 1, as its own decision. **3 cells × 8 trials × 2 arms = 48 calls**,
same fixed persona (`bug-hunter`), same scoring surface (`reply.Text`), same arms — re-verified in
the DLL before spending anything:

```
arm A  PeanutGallery.Core.dll  251392 bytes  "added by an EARLIER TURN" (UTF-16LE) → not found
arm B  PeanutGallery.Core.dll  259072 bytes  "added by an EARLIER TURN" (UTF-16LE) → offset 233032
```

### The corpus, and how each cell was checked before any call was spent

| Cell | Shape | Pre-registered correct behaviour | Block emitted in arm B? |
|---|---|---|---|
| `real-break` | continued turn removing a genuinely established public method (present at the merge base) | **both** arms still file the breaking-change finding | **no** — `OwnRemovals` = `None`, prompts byte-identical |
| `mixed` | one delta removing two symbols: one this branch introduced, one established | file on the established one, not on the introduced one | **yes** — 8 lines, all the introduced symbol's |
| `pr172` | PR #172's real turn-2 delta with its real baseline, as the crowd-out control | judged by finding count and title overlap | **yes** — 40 lines shown of 130 attributed |

`real-break` and `mixed` are built on the **real repository at the same merge base as stage 1**
(`6a2fbe5`), as a two-commit branch, both commits compiling (`dotnet build PeanutGallery.slnx -c
Release`, 0 errors on each):

- **turn 1** adds `Sha.IsShort`, the counterpart to the long-established `Sha.Short`, and uses it in
  `Supersession.SupersededReason` so an abbreviated trigger SHA is not read as a moved head; plus
  `ShaTests` and one `SupersessionTests` case. Deliberately adds **no line containing `Sha.Short`**.
- **turn 2, `real-break`** renames the **established** `Sha.Short` → `Abbrev`, updating all 14 call
  sites across 8 files. Turn 1's own work is untouched.
- **turn 2, `mixed`** renames **both**: the established `Sha.Short` → `Abbrev` *and* the branch's own
  `Sha.IsShort` → `LooksShort`. 10 files.

The two cells therefore share a base, a turn 1 and a prior session, and differ only in whether the
branch's own symbol is also removed. That makes `mixed` a clean discrimination test: two renames of
identical shape in the same file, distinguished by nothing except baseline membership.

**The prior session was not composed by the evaluator.** Turn 1 was run once against the real
provider and its reply parsed into a `ReviewSession` (`SessionUpdateParser` + `SessionCodec`), so
the continued turn starts from a session a model actually wrote. It came back clean —
*"No correctness bugs found in the reviewed changes."*, zero open findings — and was used as-is. Two
setup calls in total (one per probe revision); neither is in the 48. Parsing here builds a session,
it scores nothing.

`pr172`'s prior session is PR #172's own turn-1 panel revision (`Reviewed through 33b4049 · turn 1`,
edited 18:19:16Z), recovered from `userContentEdits` exactly as stage 1 did; delta
`33b4049...efe8e5e`, baseline `6a2fbe5...efe8e5e`, both from the GitHub compare endpoint with
`application/vnd.github.diff`.

The `OwnRemovals` output was printed for every cell **before** the matrix, and the assembled prompts
diffed:

| Cell | prompt A | prompt B | difference |
|---|---|---|---|
| `real-break` | 14,490 | 14,490 | **none — the files are identical** |
| `mixed` | 16,760 | 17,744 | +984, the block, naming only `IsShort` lines |
| `pr172` | 33,572 | 36,790 | +3,218, the block, 40 lines + `(and 90 further line(s) …)` |

`mixed`'s block names `public static bool IsShort(string sha) => sha.Length <= 7;` and its call
sites, and does **not** name `public static string Short(string sha) => …`. The mechanism is
pointed at exactly the right half of the delta.

### One probe was rebuilt before the matrix, and why

The first draft of turn 1 put two `Sha.Short` assertions in the new `ShaTests.cs`. Turn 2 then
removed those two lines, and because turn 1 had added them, `OwnRemovals` correctly attributed
them — so `real-break` emitted a block. The pre-registered cell is defined as the one where
"`OwnRemovals` attributes **nothing** and no block is emitted", so that draft was not the
pre-registered cell. Turn 1 was rebuilt to test only `IsShort`, and `real-break` then attributed
nothing, as required.

This is a corpus-construction fix to a probe that did not implement its cell, made **before any
trial**, exactly as [`ab-finding-scope.md`](../finding-scope/ab-finding-scope.md#what-the-pre-flight-changed-and-what-it-did-not)
did. **No cell, gate, trial count, arm, persona or scoring rule was altered**, before or after any
result.

### Result: 48 calls, one finding

Raw counts, read off `reply.Text`. 47 of the 48 replies returned `"findings": []`.

| Cell | Arm A: files the break | Arm B: files the break | Arm A findings (any) | Arm B findings (any) |
|---|---|---|---|---|
| `real-break` | **0/8** | **0/8** | 0 | 0 |
| `mixed` (established symbol) | **0/8** | **0/8** | 0 | 0 |
| `mixed` (introduced symbol) | 0/8 | 0/8 | — | — |
| `pr172` | n/a | n/a | **1** | **0** |

### Gate 2 (`real-break`): not met, and the cell explains why

> **Superseded.** This cell was [deleted by amendment 1a](spec.md#amendment-1a--real-break-is-deleted-as-an-ab-cell-because-it-is-a-provable-no-op)
> on the structural argument developed below — it is a provable no-op, not merely an unlucky probe.
> The analysis is kept as written because it is what produced that argument.


**Target: arm B files the breaking-change finding in ≥ 7/8, and never in fewer trials than arm A − 1.
Observed: arm B 0/8.** On the literal text of the gate, **gate 2 is not met.**

But the reason is not the feature. Arm A files it **0/8 as well, from a byte-identical prompt** —
`diff prompt-A.txt prompt-B.txt` on this cell returns nothing at all. `bug-hunter` simply does not
file a breaking-change finding on this removal, in either build. All sixteen replies are variations
on one sentence; three of the eight arm-B replies name the rename explicitly and still report
nothing:

> **real-break B-01** — *"No correctness bugs found in the reviewed changes; the SHA helper rename is
> applied consistently in the shown call sites."*
>
> **real-break B-06** — *"No correctness bugs found in the reviewed changes; the SHA helper rename is
> applied consistently in the shown call sites and preserves behavior."*
>
> **real-break A-05** — *"The SHA helper rename to Abbrev is consistently reflected in the reviewed
> call sites; no correctness bugs found and no findings remain open."*

The remaining thirteen are *"No correctness bugs found in the reviewed changes."* verbatim.

So the cell produced **no data**, and the honest reading has two parts, both of which must be
stated:

1. **Gate 2 is not passed.** The spec's rule for a precondition that does not fire is explicit —
   INCONCLUSIVE is *not* a pass — and gate 2 outranks gate 1. Nothing here licenses shipping.
2. **Gate 2 did not *fail on the feature* either.** Arm B behaved identically to a build that has
   never heard of the baseline, because its prompt was identical. This corpus cannot show that the
   block hides real breaks, and it cannot show that it does not.

There is a further structural point worth recording, because it is a property of the
**pre-registration**, not of the run: on `real-break` the block is *by definition* absent, so the
two arms' prompts are identical by construction and the cell can only ever compare a build against
itself. It is a base-rate check on the persona wearing an A/B's clothes. That is a fine thing to
check — the base rate turned out to be zero — but it was never going to attribute anything to the
feature, and a future protocol should say so out loud.

### Gate 3 (`mixed`): half met, half unevaluable

**Target: arm B files on the established symbol ≥ 6/8, and on the introduced symbol ≤ 1/8.**

| Clause | Target | Arm B | Arm A | |
|---|---|---|---|---|
| files on the **established** symbol (`Sha.Short`) | ≥ 6/8 | **0/8** | **0/8** | **not met — and arm A is 0/8 too** |
| files on the **introduced** symbol (`Sha.IsShort`) | ≤ 1/8 | **0/8** | 0/8 | met, but vacuously |

The second clause is satisfied by a reviewer that files nothing at all, which is precisely the
reading stage 2 exists to exclude — and with arm A silent on the established symbol as well, this
cell cannot separate "discriminated correctly" from "said nothing". The discrimination the feature
claims is **not demonstrated**, and it is **not refuted**. Every one of the sixteen replies is a
clean report; four name the renames and still find nothing:

> **mixed B-04** — *"No correctness bugs found in the reviewed changes; the SHA helper rename is
> applied consistently, including supersession logic and tests."*
>
> **mixed B-06** — *"No correctness bugs found in the reviewed changes; SHA helper renames are
> applied consistently."*
>
> **mixed A-01** — *"No correctness bugs found in the reviewed changes; the SHA helper rename is
> applied consistently in the shown production and test code."*
>
> **mixed A-07** — *"No correctness bugs found in the reviewed changes; the SHA helper rename is
> consistently applied in the shown production and test usages."*

Note what these say: the reviewer **read** the renames and judged them fine. In arm A that judgement
was reached with no block at all. Whatever silenced the finding on this cell, it was not the block.

### Gate 4 (crowd-out): one finding in the whole corpus

**Target: arm B's total finding count within ±20% of arm A's per cell, and no title class present in
A and absent in B across two or more cells.**

| Cell | Arm A findings | Arm B findings | Within ±20%? |
|---|---|---|---|
| `real-break` | 0 | 0 | equal (0 vs 0) |
| `mixed` | 0 | 0 | equal (0 vs 0) |
| `pr172` | **1** | **0** | **no — 100% below** |

The one finding in 48 calls is `pr172` arm A trial 7, quoted in full:

> `minor` · `src/PeanutGallery.Core/PanelCommentRenderer.cs:378` · *Singleton clusters bypass lens
> deduplication* — "The no-area path passes `af.Lenses` directly, whereas the previous implementation
> used `LensesOf([af])`. If an `AttributedFinding` contains duplicate lens names, a standalone/file-wide
> finding now renders repeated attribution (for example `_(bugs, bugs)_`) while multi-finding clusters
> still deduplicate via `Build`. Use `LensesOf([af])` here to preserve the renderer's normalization
> invariant."

Its title class ("Singleton clusters bypass lens deduplication") is present in A and absent in B on
**one** cell; the pre-registered trigger is **two or more**, so that clause is not tripped. The ±20%
clause is violated on `pr172` — but by a single finding that appeared in **one of eight** arm-A
trials, which is indistinguishable from sampling noise at this power. `pr172` is also the cell with
the **largest** block (40 lines shown of 130 attributed, +3,218 characters), so it is the cell where
crowd-out would show most clearly if it were real. One finding cannot tell us either way.

**Gate 4 is unevaluable at this power.** It is not evidence of crowd-out, and it is not a clean bill
of health.

### What stage 2 did establish

Not nothing, and worth separating from the gates:

- **The arithmetic points at the right lines on a genuinely mixed delta.** On `mixed`, `OwnRemovals`
  attributed all 8 lines belonging to `Sha.IsShort` and **zero** belonging to `Sha.Short`, from two
  renames of identical shape in the same file. Whatever the reviewer then does with it, the fact the
  block states is correct. That is the `OwnRemovals` half of the feature, verified on a second,
  independent corpus.
- **It degrades to silence when it should.** On `real-break` the answer was `None`, no block was
  emitted, and the prompt was byte-for-byte what `main` produces. The "fails towards saying nothing"
  claim in the spec holds here.
- **The truncation is honest and it engages.** `pr172` attributed 130 lines and rendered 40 plus
  `(and 90 further line(s) this pull request introduced)`. The spec calls 40 "not the operating
  regime"; on a real rework-heavy turn-2 it plainly is, and the cap does most of the work.

None of these is a ship gate. Two of them are about the pure function, which was never the part in
doubt.

## Stage 2R — the redesigned `mixed` cell

The [amended protocol](spec.md#amendment-1-to-the-stage-2-protocol-2026-08-24-written-and-committed-before-any-call)
was **committed and pushed before the first call** — `0065595`, 2026-08-24T02:52:15-05:00 — and fixed
the cell, the probe, the precondition, the re-derived gate 3 threshold and the stopping rule in
advance. Nothing below was adjusted afterwards.

**16 calls.** Same fixed persona, same scoring surface, arms re-verified in the DLL:

```
arm A  PeanutGallery.Core.dll  251392 bytes  "added by an EARLIER TURN" (UTF-16LE) → not found
arm B  PeanutGallery.Core.dll  259072 bytes  "added by an EARLIER TURN" (UTF-16LE) → offset 233032
```

### The probe: two renames, one diff, distinguished only by the baseline

Built on stage 1's shape, as the amendment requires — PR #175's real turns and its real turn-1
session, with the established removal folded into the same delta:

| | Symbol | Provenance | In arm B's block? |
|---|---|---|---|
| **introduced** | `Trajectory.Of(IReadOnlyList<Turn>)` → `OfTurns` | added by turn 1 of this branch | **named** |
| **established** | `Trajectory.Of(IReadOnlyList<DiffShape>)` → `OfShapes` | on `main` at the merge base; production caller `Trajectory.ByPr`, 20 test call sites | **not named** |

Both are public statics on the same type in the same file, removed in the same delta, of identical
shape. The prior session is PR #175's real turn-1 `bug-hunter` session — turn 1, one open finding
(*"The new Of overload makes empty collection-expression calls ambiguous"*), about the introduced
symbol.

Arm B's prompt is **+946 characters**: the block, naming
`public static Trajectory? Of(IReadOnlyList<Turn> turnsOldestFirst)` and its call sites, and naming
neither `Of(IReadOnlyList<DiffShape>)` nor `OfShapes`. Nothing else differs.

One call failed in transport (`JsonReaderException` from the provider, no reply body) and was
re-issued; a call that returns no text is not a trial outcome. All 16 scored replies came back on
the first attempt, none truncated.

### Precondition (amended clause, `mixed`): MET at 6/8

**Arm A files the breaking-change finding against the ESTABLISHED symbol in 6/8 trials** (target
≥ 5/8). The cell produces data. This is what stage 2 lacked and what the amendment added.

### Result: the discrimination, in the reviewer's own words

| Quantity | Arm A | Arm B | |
|---|---|---|---|
| files on the **established** symbol (`Of(IReadOnlyList<DiffShape>)`) | **6/8** | **5/8** | Fisher two-tailed p = 1.00 — indistinguishable |
| files on the **introduced** symbol (`Of(IReadOnlyList<Turn>)`) | **4/8** | **0/8** | Fisher two-tailed p = 0.077, one-sided p = 0.038 |
| total findings (any kind) | 7 | 6 | B at 85.7% of A |

Arm A conflates the two renames: **four of its six filers claim *both* removals break callers.**

> **A-05** — "The change removes the existing public `Trajectory.Of(IReadOnlyList<DiffShape>)` **and**
> `Trajectory.Of(IReadOnlyList<Turn>)` methods in favor of `OfShapes` and `OfTurns`. Any downstream
> code using the previously published `Trajectory.Of(...)` API now fails to compile…"

Arm B separates them, and **says why**, unprompted — the block states base-branch membership and the
reviewer reasons from it:

> **B-02** — "`Of(IReadOnlyList<DiffShape>)` existed on the base branch and has been renamed to
> `OfShapes`. **Unlike the newly added turn overload, this is an established public API**, so every
> external consumer calling `Trajectory.Of(runs)` now fails to compile."
>
> **B-06** — "`Trajectory.Of(IReadOnlyList<DiffShape>)` existed before this change, but it has been
> renamed to `OfShapes`. Downstream consumers compiled against the base API will now fail to compile,
> **even though the newly introduced turn overload was the only API that needed a distinct name.**"
>
> **B-03** — "…keep the existing `Of(IReadOnlyList<DiffShape>)` and expose only the new turn overload
> under `OfTurns` **(as this change already does)**."

**All five arm-B filers name the turn overload and treat its rename as legitimate**, while filing on
the shape overload. That is the product the feature claims, and stage 1 structurally could not show
it: there, arm B's silence was consistent with "silenced findings". Here arm B is not silent — it
files a `major` breaking-change finding on the same diff, in the same reply, about the symbol the
block does not name.

### Gate 3 (amended): PASS on both clauses

| Clause | Rule | Observed | |
|---|---|---|---|
| **3a** established, non-inferiority | arm B ≥ arm A − 1 | **5 ≥ 6 − 1 = 5** | **PASS, exactly at the margin** |
| **3b** introduced | arm B ≤ 1/8 | **0/8** | **PASS** |

3a passes with **zero slack**: one fewer arm-B filer and it would have failed.

How much a pass is worth needs the rule's real sampling distribution, and the amendment's
pre-declared power table did not have it. That table fixed arm A at 5, the minimum the precondition
admits; but stage 2R conditions on arm A reaching the precondition first, so arm A is random over
{5, 6, 7, 8} and the failure threshold A − 2 moves with it. The `contrarian` on this write-up's own
review caught that. **The rule is untouched and is what was applied** — only its power claim is
[corrected](spec.md#amendment-1c--gate-3s-established-symbol-threshold-re-derived), recomputed as
P(B ≤ A − 2 | A ≥ 5) with A ~ Bin(8, p<sub>A</sub>) and B ~ Bin(8, p<sub>B</sub>):

| p<sub>A</sub> down / p<sub>B</sub> across | 0.750 | 0.625 | 0.500 | 0.375 | 0.250 | 0.000 |
|---|---|---|---|---|---|---|
| **0.875** | 0.372 | 0.628 | 0.822 | 0.937 | 0.986 | 1.000 |
| **0.750** | 0.215 | 0.442 | 0.676 | 0.859 | 0.962 | 1.000 |
| **0.625** | 0.131 | 0.324 | 0.568 | 0.793 | 0.940 | 1.000 |
| **0.500** | 0.085 | 0.251 | 0.494 | 0.746 | 0.923 | 1.000 |

p<sub>A</sub> is **not identified** by this design — arm A's observed 6/8 is itself conditioned on
A ≥ 5 — so no single row is "the" answer. Across the plausible range the false-alarm rate (arms truly
identical, the diagonal) is **0.13–0.37**, and a true 25-point suppression is caught **0.68** of the
time at p<sub>A</sub> = 0.75. So *"3a passed"* means **"no gross suppression was observed"**, and a
pass still leaves roughly one chance in three that a 25-point suppression is present and was missed.
It never means "the block does not suppress".

### Mentions that are not claims

Counted separately, as required:

- **Arm A: 2** (A-03, A-07) mention the renames and file nothing — e.g. A-07: *"The overload
  ambiguity is fixed by separating the shape and turn folds; no remaining correctness bugs found in
  this change."*
- **Arm B: 3** (B-01, B-05, B-08) do the same — e.g. B-01: *"The previously reported
  collection-expression overload ambiguity is fixed by renaming the APIs to OfShapes and OfTurns. No
  remaining correctness bugs found in this increment."*

Both arms name both renames in every reply. Neither arm is blind to the diff; they differ in what
they conclude about it.

### Gate 4 (crowd-out): UNMET

| Cell | Arm A findings | Arm B findings | ±20% clause |
|---|---|---|---|
| `mixed` (2R) | **7** | **6** | **PASS** — B at 85.7% of A |
| `pr172` | 1 | 0 | **FAIL** — 0 is not within ±20% of 1 |
| `mixed` (stage 2) | 0 | 0 | equal |
| `real-break` (stage 2) | 0 | 0 | equal |

**Title classes:** on `mixed` (2R) both arms produce the same two classes — a `major`
breaking-change-on-`Of` finding (6 in A, 5 in B) and a `minor` finding about `Turn`'s snapshot at
line 147 (A-02 *"Snapshotting changes record equality for equivalent panels"*; B-03 *"Snapshotting
can expose a partially copied panel when the source is concurrently mutated"*). **No class is
present in A and absent in B on this cell.** The only A-only class in the whole corpus is `pr172`'s
*"Singleton clusters bypass lens deduplication"*, on one cell; the pre-registered trigger is **two or
more**, so that clause **passes**.

**Verdict on gate 4: UNMET.** The gate requires arm B within ±20% of arm A **per cell**, and on
`pr172` arm B is 0 against arm A's 1. That criterion was pre-registered, that cell was run, and it
fails. The title-class clause passes; both clauses were required, so the gate does not hold.

An earlier revision of this write-up recorded this as "PASS with one n = 1 violation". That was the
softening this exercise has refused everywhere else, appearing in the write-up itself, and the panel
filed a `major` against it with three lenses agreeing. **A small sample makes a failed criterion weak
evidence; it does not make it a pass.** Nor is the route through an amendment open: exempting
low-yield cells now would be amending *after* seeing the result, which is exactly what commit
`0065595` was structured to avoid.

**What is true, and more useful than either label: crowd-out remains unmeasured.** A ±20% band cannot
be evaluated against counts of 0, 1, 6 and 7 — on `pr172` it is arithmetically unsatisfiable unless
arm B matches arm A exactly, and on the zero-yield cells it is vacuous. Only `mixed` (2R) carries
enough findings to compare at all, and 13 findings across 16 calls support that comparison weakly:
6 vs 7 is a 14% difference in the safe direction, on the cell carrying the largest block over the
branch's own work. So the corpus neither detects crowd-out nor rules it out. The gate that exists to
settle the question is unmet, and the question is open.

## Stage 0 recheck on the fixed build (0 calls)

Run **after** stages 1 and 2R, because the implementation changed underneath them. The author's
`bba3595` fixed two ways `OwnRemovals` could manufacture a claim, and both touch *when* a claim is
made:

1. **The baseline is now anchored to the immutable head.** It was resolved from the pull-request
   endpoint, which answers for whatever the head is *at fetch time*, while every persona's delta is
   anchored to the head SHA captured at run start. The `count(head)` terms only cancel if both diffs
   end at the same commit. Now resolved as `compare/{baseRef}...{headSha}`.
2. **The arithmetic derives from the RAW delta and narrows afterwards** via a new
   `OwnRemovals.OnlyIn`. `DiffFilter` drops whole files, so a line the delta removes from a kept file
   and adds to a dropped one would lose its cancelling addition and be manufactured.

**Both A/B runs measured a build without those fixes.** A measurement whose artifact changed after
the fact and was never re-verified is not a measurement, so this recheck re-runs stage 0 against the
fixed build on the real #175 diffs and asks whether attribution moved.

**Result: it did not. Both halves of the fix are provably inert on this corpus.**

| Check | Method | Outcome |
|---|---|---|
| does the anchoring fix change the baseline? | fetch `pulls/175` (old path) and `compare/6a2fbe5...e79abb9` (new path), byte-compare | **identical**, 44,594 bytes each — #175 is merged, so its head cannot move |
| does raw-vs-filtered change the delta? | `DiffFilter.Apply` on every cell | **omitted = 0 everywhere**, and `raw.Raw == filtered.Raw` is `true` |
| does attribution change? | run both call paths on the fixed build and compare line-by-line | **identical** |

Attribution on the fixed build, real #175 diffs, both call paths:

```
FIXED path   Of(rawDelta, baseline).OnlyIn(filteredDelta)   IsKnown=True  LineCount=9  Files=2
AS MEASURED  Of(filteredDelta, baseline)                    IsKnown=True  LineCount=9  Files=2
>>> IDENTICAL
```

**9 lines across 2 files**, including `public static Trajectory? Of(IReadOnlyList<Turn> turnsOldestFirst)` —
the exact declaration `bug-hunter` quoted on #175, and the exact figure the original stage-0 record
claims. The stage-2R cell was rechecked the same way: **10 lines across 2 files**, unchanged, with
the established `Of(IReadOnlyList<DiffShape>)` still excluded.

**Stages 1 and 2R therefore stand.** The reason the fixes are inert here is structural rather than
lucky: every probe used a merged pull request (immutable head, so nothing to anchor against) and no
cell ever exceeded the filter budget (so nothing was dropped). Those are precisely the two conditions
the fixes exist to handle, and neither was in play — which is also why the A/B could not have caught
either defect, and did not.

## The crowd-out cell: `pr173t3`

Authorised after gate 4 was reported unmet, and pre-registered in
[amendment 2](spec.md#amendment-2-a-crowd-out-cell-that-can-answer-2026-08-24-written-and-committed-before-any-call),
**committed and pushed before the first call** — `d9cd9ba`, 2026-08-24T11:31:38-05:00.

**16 calls.** Arms re-verified in the DLL, with arm B now the branch tip (the shipping candidate),
justified by the recheck above:

```
arm A  b8975... (b8776e2)  PeanutGallery.Core.dll  251392 bytes  probe → not found
arm B  bba3595              PeanutGallery.Core.dll  259072 bytes  probe → offset 233340
```

### What this cell can and cannot do — fixed in advance

The amendment fixes this before any number was seen, and it is narrow: **the cell cannot repair gate
4.** `pr172` was the pre-registered crowd-out control, it was run, it failed the ±20% clause, and
that result stands. Exempting it now on a minimum-yield rule invented after seeing its outcome is
the move `0065595` exists to prevent. So in **every** branch of the pre-registration, gate 4 remains
unmet and the ship criterion stays false. What the calls buy is the difference between *"the control
was never evaluated"* and *"the control was evaluated and says X"*.

### Why this cell, chosen on a property of the pull request

The repo's own `pg-metrics` ledger records how many findings each persona raised on every historical
run — written months before this evaluation, and unrelated to anything either arm did here:

| PR | `bug-hunter` raised, per turn | Files / added |
|---|---|---|
| **#173** | **3, 4, 2, 2** | 8 / 677–884 |
| #181 | 1, 1, 1, 1, 2, 3, 3 | 10–12 / 1256–2888 |
| #175 | 1, 1 | 4 / 638–742 |
| #172 | **0, 0, 0, 0, 0** | 3 / 498–577 |
| #171 | 0, 0, 0 | 6 / 135–177 |
| #170 | 0, 0, 0, 0, 0 | 2 / 121–257 |

#173 is the highest and most consistent yield in the repository. The same table is also the epitaph
for the earlier attempt: **`pr172` raised zero findings on all five of its real runs**, so its failure
as a crowd-out cell was foreseeable from data that already existed. Consulting this ledger costs
nothing and would have saved those 16 calls. The result still stands as failed; the lesson is that
the ledger should be consulted first.

**Turn selection**, by the rule declared before attribution was inspected — highest recorded yield,
subject to the block actually being emitted, ties broken by larger delta:

- turn 2 (yield **4**, highest) is **structurally excluded**: its turn-1 head `946beb7` was
  force-pushed away and is not an ancestor of `518a5e4`, so the delta coincides with the cumulative
  diff, nothing can be attributed, and no block is emitted;
- turns 3 and 5 tie at 2; **turn 3** has the larger delta.

| Piece | Value |
|---|---|
| delta | `518a5e4...38a5661` — 3 files, 13.7 KB |
| baseline | `6a2fbe5...38a5661` — 8 files, 42.7 KB |
| prior session | #173's real turn-2 `bug-hunter` session, verbatim — **4 open findings**, one `major` |
| block | **27 lines across 2 files**; prompts differ by **+2,299 characters** and nothing else |

### Minimum arm-A yield: MET at 12 (floor: 5)

The pre-registered floor is 5, derived arithmetically: a one-finding difference trips a ±20% band
whenever `1 > 0.2N`, i.e. whenever `N < 5`, so below five the clause is not a band but a demand for
exact equality. **Arm A produced 12 findings across its 8 trials**, so the cell is informative and the
band `[9.6, 14.4]` admits integer arm-B totals of 10 through 14.

### Result: 12 and 12

| Quantity | Arm A | Arm B |
|---|---|---|
| total findings across 8 trials | **12** | **12** |
| trials filing ≥ 1 finding | 8/8 | 8/8 |
| B as a percentage of A | — | **100.0%** |

**The ±20% clause passes on this cell**, with the largest possible margin: the two totals are equal.

**Title classes — three, all present in both arms:**

| Class | Arm A | Arm B |
|---|---|---|
| `RemoteRepoContext.cs:93` — concurrent personas re-fetch the same uncached path | **8** (every trial) | **8** (every trial) |
| `FileContext.cs` — greedy window selection cannot reconsider an accepted window | **3** (A-02, A-03, A-07) | **3** (B-03, B-05, B-08) |
| `FileContext.cs` — the cached `Cheapest` value is not a true lower bound | **1** (A-05) | **1** (B-07) |

**No class is present in arm A and absent in arm B on this cell.** The distribution is not merely
equal in total, it is equal class by class — 8/3/1 against 8/3/1. Two of the three classes are also
findings the prior session did *not* carry, so arm B is still discovering new problems at arm A's
rate while carrying a 27-line block.

The two arms even converge on the same wording. Arm A's rarest finding and arm B's are the same
defect, found once each:

> **A-05** — *"Windowed files are ordered by a value that is not their cheapest usable shape"*:
> "`Cheapest` is computed from the whole file and the rendering containing all windows, but `Choose`
> can produce a substantially smaller rendering by selecting only a later window… The ordering key
> needs to account for the smallest usable windowed rendering."
>
> **B-07** — *"Cached Cheapest size still prevents fitting windows from being considered"*:
> "`Cheapest` is computed as the smaller of the whole-file text and the rendering containing all
> windows. After `Choose` was changed to skip oversized windows, that is no longer the minimum size
> the file can occupy… `Fit` uses this cached value to reject candidates before calling `Choose`, so
> the new skip behavior is bypassed."

### Gate 4: still UNMET, and now for a reason the evidence does not share

Exactly as pre-registered:

| | |
|---|---|
| **the crowd-out question** | **measured, on a cell able to answer it — no crowd-out detected.** 12 vs 12, equal class by class, on the cell with the second-largest block in the corpus (27 attributed lines) |
| **gate 4 as pre-registered** | **remains UNMET.** `pr172` failed the per-cell ±20% clause, that cell was the pre-registered control, and its result stands |

Both rows are true at once, and the write-up refuses to collapse them. The gate is a conjunction over
the cells actually run, and one of those cells failed on a criterion that, at one finding, was
arithmetically a demand for exact equality. That was a badly-specified criterion — but a
badly-specified criterion that has been run is not repaired by running a better one afterwards, only
by pre-registering the fix before the fact, which is no longer possible here.

So the honest summary is: **the substantive worry behind gate 4 has been checked and found absent on
the best cell available; the gate itself cannot now be met.**

## Decision

**Verdict across all stages: DO NOT SHIP on the pre-registered protocol — gate 4 cannot now be met.
But the worry behind it has been measured and is absent.** Those are two different sentences and the
gap between them is the whole of what a decider is choosing in.

### Every gate, individually

| # | Gate | Target | Observed | Verdict |
|---|---|---|---|---|
| 1 | stage 1 `rename-own`: A ≥ 5/8 **and** B ≤ 1/8 | — | A **5/8**, B **0/8**, p = 0.026 | **PASS** (1a met exactly) |
| 2 | `real-break`: arm B still files the genuine break | ≥ 7/8 | **cell deleted by [amendment 1a](spec.md#amendment-1a--real-break-is-deleted-as-an-ab-cell-because-it-is-a-provable-no-op)** as a provable no-op; replaced by two passing unit tests | **SATISFIED BY PROOF, not by measurement** |
| — | `mixed` precondition (amendment 1b) | A ≥ 5/8 | **6/8** | **MET** |
| 3a | `mixed` established, non-inferiority vs arm A | B ≥ A − 1 | **5 ≥ 5** | **PASS, zero slack** |
| 3b | `mixed` introduced | ≤ 1/8 | **0/8** | **PASS** |
| — | `pr173t3` minimum yield (amendment 2) | A ≥ 5 findings | **12** | **MET** |
| 4 | crowd-out: **±20% per cell**; no title class in A and absent in B across ≥ 2 cells | both clauses | `pr173t3` **12 vs 12** (pass); `mixed` 6 vs 7 (pass); **`pr172` 1 vs 0 FAIL**; title clause pass | **UNMET** |
| — | stage-0 recheck on the fixed build | attribution unchanged | **9 lines / 2 files, identical** | **MET** |

### Gate 4, stated once more without softening

The gate requires arm B within ±20% of arm A **per cell**. `pr172` was the pre-registered crowd-out
control; it was run; arm A filed 1 finding and arm B 0; zero is not within ±20% of one. That is a
failed pre-registered criterion and it stays failed. Nothing measured afterwards repairs it, and
amending to exempt low-yield cells after seeing that outcome was closed off by the same discipline
that makes the rest of this document worth reading.

**What did become possible was measuring the question.** `pr173t3` was selected on historical finding
yield recorded in the repo's own ledger, carries a 27-line block, and cleared a floor of 5 findings
with 12. Arm B returned **12 findings against arm A's 12** — equal in total and equal class by class,
8/3/1 against 8/3/1. On the best available evidence there is **no crowd-out**.

So the ship decision is not "the evidence is bad". It is: *every substantive question this protocol
posed has been answered favourably, and one pre-registered criterion is permanently unsatisfiable
because the cell chosen to test it could never have satisfied it.* Shipping anyway is a defensible
judgement — but it is a judgement to overrule a stated gate, and it must be recorded as that, by the
owner, not smuggled in by an adjudicator relabelling a FAIL.

### The results that stand

- **Stage 1** — the defect is real and the block removes it: arm A files the false breaking-change
  finding 5/8, arm B 0/8.
- **Stage 2R** — the discrimination is real, in the reviewer's own words. One diff, two public methods
  of identical shape: arm B files the `major` on the **established** one 5/8 (arm A 6/8,
  indistinguishable) and on the **branch's own** one 0/8 (arm A 4/8). All five arm-B filers name the
  turn overload as legitimately renamed; three assert the distinction outright. Arm A conflates them
  in four of six.
- **`pr173t3`** — no crowd-out: 12 vs 12, equal class by class.
- **Stage-0 recheck** — the author's two manufacturing fixes leave attribution byte-identical on
  every corpus used here, so none of the above was invalidated by the code moving underneath it.

### What the evidence does NOT establish

- **It does not repair gate 4**, and no further calls can. The only route was a minimum-yield rule
  pre-registered before `pr172` ran.
- **It does not establish that the block never hides a real break.** Gate 3a passed with zero slack
  (5 against a floor of 5). Its power, [corrected after the run](spec.md#amendment-1c--gate-3s-established-symbol-threshold-re-derived)
  to condition on the precondition, gives a false-alarm rate of 0.13–0.37 and catches a true 25-point
  suppression about 0.68 of the time at p<sub>A</sub> = 0.75 — so a pass leaves roughly one chance in
  three of missing a suppression that size.
- **It does not establish absence of crowd-out in general.** One informative cell, 24 findings total.
  `pr173t3`'s 12-vs-12 is a strong signal for that cell and says nothing about a repo with different
  yield or a much larger block.
- **It does not establish behaviour on a blockless later turn.** The feature provably cannot act
  there, but a diffuse "this reviewer has learned to distrust removal findings" effect carrying
  forward is tested by nothing here.
- **It does not generalise past one persona.** `bug-hunter` was fixed by protocol throughout. On #175
  the `contrarian` filed the same false `major` and has never been measured.
- **It does not cover the truncated regime.** `pr172` attributed 130 lines and showed 40. The blocks
  in every cell that produced findings were 27 lines or fewer.
- **It could not have caught the two defects `bba3595` fixed.** Every probe used a merged pull request
  (immutable head) and no cell exceeded the filter budget, so neither the anchoring bug nor the
  filtered-delta bug was ever in play. The A/B tests what the block *says*, not how the baseline is
  *fetched*; the panel review caught those, and that division of labour is worth noting.

### Recommendations (no code change)

**No implementation code was touched across any stage, and none is recommended.** `OwnRemovals`
attributed correctly on every corpus: 9 lines on #175, 10 on stage 2R with the established
declaration excluded, 27 on `pr173t3`, nothing on `real-break`, and an honest truncation at 40 of 130
on `pr172`.

1. **Put a minimum-yield rule in the A/B template, for every count-based gate.** A ±20% band is
   meaningless below 5 findings — it is a demand for exact equality — and nothing said so before
   `pr172` was run. The general form: *any criterion comparing counts must declare, in advance, the
   count below which the cell is uninformative rather than failed.*
2. **Consult the `pg-metrics` ledger when choosing any cell.** It records per-persona finding yield
   for every historical run. `pr172` had raised zero findings on all five of its runs; that was
   knowable for free and would have predicted the wasted cell.
3. **Measure the `contrarian`.** It filed the same false `major` on #175, runs at temperature 0.8,
   and its prompt licenses questioning the premise. One cell, 16 calls, the `mixed` probe unchanged.
   This is now the largest un-probed risk.
4. **Prefer non-inferiority to absolute thresholds for suppression gates, and compute power under the
   actual sampling design** — including any conditioning the protocol imposes. Gate 3's original
   threshold could demand the blocked build outperform the unblocked one; its replacement was right
   but its power table was computed for a design that was not being run.
5. **Do not re-run `pr172` or `pr173t3` alone.** They are controls and produce no verdict of their own.

### If a further stage is bought

In order: (1) the `contrarian` cell — the only unmeasured persona known to have produced the defect;
(2) a second `mixed`-shaped probe on a different file and symbol pair, so the discrimination is not an
artefact of `Trajectory`; (3) a truncated-block cell, since rework-heavy turns are the operating
regime and nothing has measured 40-of-130.

## The overrule

**Gate 4 was failed, and the decision to ship overrules it.** This section exists so that is
recorded as a judgement rather than absorbed into a clean sweep. A reader who finds this feature
misbehaving should start here.

**Decided by:** the repository owner, 2026-08-24, on the recommendation of the agent running the
work, with the adjudicator explicitly declining to relabel the failure and referring the call
upward.

**What was overruled.** Gate 4 requires arm B within +/-20% of arm A per cell. On the
pre-registered control `pr172`, arm A filed 1 finding and arm B 0. That is a failure of a stated
ship gate and it stands as one.

**The reasoning for shipping anyway**, in the order it carried weight:

1. **The dangerous direction is satisfied by proof, not by sampling.** `AppendOwnRemovals`
   early-returns unless something was attributed, so on a diff with no attributable removals the
   prompts are byte-identical and the feature cannot change behaviour. Suppression of a genuine
   breaking change is structurally impossible outside mixed diffs, and on a mixed diff it was
   measured absent (arm B files the established break 5/8 against arm A's 6/8, p = 1.00).
2. **The benefit is measured twice.** Stage 1: 5/8 to 0/8, p = 0.026. Stage 2R: the false finding
   0/8 against arm A's 4/8, p = 0.038, with arm B asserting the distinction in its own words.
3. **The question gate 4 asks is answered.** `pr173t3`, chosen on ledger yield and clearing a
   pre-registered floor, returned 12 findings against 12, class for class.
4. **Gate 4's failure was a specification defect, not a signal.** A +/-20% band on N = 1 demands
   exact equality; it is not a band. The `pg-metrics` ledger already showed `pr172` raising zero
   findings on all five of its real runs, so the cell was foreseeably uninformative.
5. **Nothing acts on the fact.** No suppression, no severity change, no persistence, no rendering.
   The blast radius is one prompt block on continued turns.

**The counter-argument, which did not win but is not weak.** Pre-registration means precisely that
you do not get to decide, after seeing the results, that a gate was badly specified. Accepted
freely, that reasoning would let any gate fail upward. It is recorded here because the next person
weighing a similar call should meet it rather than rediscover it.

**What this does not establish**, unchanged by the decision: the `contrarian` persona is unmeasured
though it produced the original defect; the truncated-block regime (40 of 130 lines) is unmeasured
though rework-heavy turns are the operating case; the discrimination is demonstrated on one file and
symbol pair; and gate 3a passed with zero slack under a design that catches a true 25-point
suppression only about two times in three.

**The revert trigger is the load-bearing safeguard and is now live**, not a formality. It is stated
in [`spec.md`](spec.md) and it is what this decision leans on in place of a met gate.

## Every reply verbatim

Whitespace collapsed for width; the `summary` / `body` text is character-exact.

### Stage 1 (`rename-own`)

#### Arm A — files the false finding (5)

**A-01** · `major` · `src/PeanutGallery.Core/Trajectory.cs:340` · *Renaming Of to OfTurns is a
source-breaking public API change*

> `Trajectory.Of(IReadOnlyList<Turn>)` was previously public, but it has been removed and replaced
> with `OfTurns`. Any downstream consumer that passes a typed turn list—such as
> `Trajectory.Of(turns)`—now fails to compile, even though the underlying operation is unchanged.
> The repository tests were updated, so this break is not caught internally. Preserve a
> compatibility entry point or otherwise provide a migration-compatible overload/API shape while
> solving the empty collection-expression ambiguity.

**A-02** · `major` · `Trajectory.cs:339` · *Renaming Trajectory.Of breaks existing callers of the
public turn-fold API*

> The previous public `Trajectory.Of(IReadOnlyList<Turn>)` method has been removed and replaced with
> `OfTurns`. Any downstream code that passes a typed turn list to `Trajectory.Of(...)` now fails to
> compile, even though the new overload is unrelated to those callers. Avoiding ambiguity for
> `Trajectory.Of([])` fixes one source-compatibility issue by introducing another; preserve a
> compatibility entry point or otherwise provide a migration-compatible API design.

**A-04** · `major` · `Trajectory.cs:338` · *Renaming Of to OfTurns breaks existing typed callers*

> The previous public `Trajectory.Of(IReadOnlyList<Turn>)` API has been removed and replaced with
> `OfTurns`. Any existing caller holding a typed `IReadOnlyList<Turn>`, `List<Turn>`, or similar and
> calling `Trajectory.Of(turns)` now fails to compile; the new compile-time guard only protects the
> special untyped `Trajectory.Of([])` case. Unless this is an explicitly planned breaking API change
> with corresponding versioning/migration, preserve source compatibility or choose an overload
> arrangement that avoids the empty-expression ambiguity without removing the existing method.

**A-06** · `major` · `Trajectory.cs:340` · *Renaming the existing turn-fold overload breaks all
callers*

> Changing `Trajectory.Of(IReadOnlyList<Turn>)` to `Trajectory.OfTurns(IReadOnlyList<Turn>)` removes
> the existing public API. Downstream callers with non-empty turn collections, such as
> `Trajectory.Of(turns)` or `Trajectory.Of([turn1, turn2])`, now fail to compile; the ambiguity fix
> therefore breaks substantially more callers than the empty-expression case. Preserve the existing
> API where compatibility is required, or make the breaking API change explicit and versioned.

**A-08** · `major` · `Trajectory.cs:337` · *Renaming the public turn-fold method breaks existing
callers*

> `Trajectory.Of(IReadOnlyList<Turn>)` was a public API, but it has been renamed to `OfTurns`. This
> is a source-breaking change for every existing caller with a typed turn list, not just the empty
> collection-expression case; those callers now fail to compile after upgrading. The ambiguity can
> be avoided without breaking this API by giving the new run-metrics entry point a distinct name
> instead of renaming the existing method, unless this is explicitly intended as a major-version
> breaking change.

#### Arm A — files nothing (3)

Each returned `"findings": []`. Summaries:

- **A-03** — *"The empty collection-expression overload ambiguity is fixed by renaming the
  turn-folding overload to OfTurns; no additional correctness bugs found in this update."*
- **A-05** — *"The empty collection-expression overload ambiguity has been fixed by renaming the
  turn-folding API to OfTurns; no additional correctness bugs found in this increment."*
- **A-07** — *"The previous overload-ambiguity finding is fixed by renaming the turn-folding method
  to OfTurns; no new correctness bugs are confirmed in this diff."*

#### Arm B — all 8

Every one returned `"findings": []` and resolved the turn-1 finding. Summaries in full:

- **B-01** — *"The collection-expression overload ambiguity is fixed by renaming the turn-folding
  API to OfTurns; no remaining correctness findings identified."*
- **B-02** — *"The collection-expression overload ambiguity is fixed by renaming the turn-folding
  method to OfTurns; no current correctness findings remain."*
- **B-03** — *"The empty collection-expression ambiguity is fixed by renaming the turn-fold
  overload; no new correctness bugs found in this increment."*
- **B-04** — *"The collection-expression overload ambiguity is fixed by renaming the turn-folding
  method, and the dictionary snapshot paths are covered. No correctness findings remain."*
- **B-05** — *"The empty collection-expression ambiguity is fixed by renaming the turn-fold overload
  to OfTurns. The dictionary snapshot implementation and updated call sites introduce no additional
  verified correctness issues."*
- **B-06** — *"The collection-expression overload ambiguity is fixed by renaming the turn-folding
  API to OfTurns; the snapshot implementation also prevents later panel mutations from affecting
  turns. No open correctness findings remain."*
- **B-07** — *"The empty-collection-expression ambiguity is fixed by renaming the turn-folding
  overload to OfTurns; snapshotting now protects Turn values from later dictionary mutation. No
  current correctness findings."*
- **B-08** — *"The collection-expression overload ambiguity is fixed by renaming the turn-folding
  API to OfTurns; the new dictionary snapshot paths are covered and no correctness bugs remain
  open."*

### Stage 2 — all 48

Forty-seven of the forty-eight returned `"findings": []`. Only the `summary` distinguishes them, so
every distinct summary is listed with the trials that produced it.

#### `real-break` — arm A (0 findings) and arm B (0 findings)

Arm A and arm B received **byte-identical prompts** on this cell, so the two columns are two samples
of the same request.

| Summary (verbatim) | Arm A trials | Arm B trials |
|---|---|---|
| *"No correctness bugs found in the reviewed changes."* | 1, 2, 3, 7, 8 | 2, 3, 4, 7, 8 |
| *"No correctness bugs found in the reviewed changes; the SHA helper rename is applied consistently in the shown call sites."* | 4 | 1 |
| *"The SHA helper rename to Abbrev is consistently reflected in the reviewed call sites; no correctness bugs found and no findings remain open."* | 5 | — |
| *"No correctness bugs found in the reviewed changes; the SHA helper rename preserves behavior and all shown call sites use the new method."* | 6 | — |
| *"No correctness bugs found in the reviewed changes; the SHA helper rename preserves existing behavior."* | — | 5 |
| *"No correctness bugs found in the reviewed changes; the SHA helper rename is applied consistently in the shown call sites and preserves behavior."* | — | 6 |

**0/8 file a breaking-change finding in either arm.** Six of sixteen replies name the rename and
report nothing anyway.

#### `mixed` — arm A (0 findings) and arm B (0 findings)

| Summary (verbatim) | Arm A trials | Arm B trials |
|---|---|---|
| *"No correctness bugs found in the reviewed changes."* | 2, 4 | 1, 2, 3, 5, 7, 8 |
| *"No correctness bugs found in the reviewed changes; the SHA helper rename is applied consistently in the shown production and test code."* | 1 | — |
| *"No correctness bugs found in the reviewed changes; the SHA helper rename and call-site updates are behavior-preserving."* | 3 | — |
| *"No correctness bugs found in the reviewed changes; the SHA helper rename is applied consistently in the shown call sites and tests."* | 5, 6 | — |
| *"No correctness bugs found in the reviewed changes; the SHA helper rename is consistently applied in the shown production and test usages."* | 7 | — |
| *"No correctness bugs found in the reviewed changes; the SHA helper rename and updated call sites are consistent."* | 8 | — |
| *"No correctness bugs found in the reviewed changes; the SHA helper rename is applied consistently, including supersession logic and tests."* | — | 4 |
| *"No correctness bugs found in the reviewed changes; SHA helper renames are applied consistently."* | — | 6 |

**0/8 on the established symbol and 0/8 on the introduced symbol, in both arms.** Note arm A: six of
its eight replies name the renames and clear them, with no block anywhere in the prompt.

#### `pr172` — arm A (1 finding) and arm B (0 findings)

| Summary (verbatim) | Arm A trials | Arm B trials |
|---|---|---|
| *"No correctness bugs found in the reviewed diff."* | 2, 3, 4, 6 | 1, 2, 3, 4, 5, 6, 7, 8 |
| *"No correctness bugs found in the reviewed changes."* | 5, 8 | — |
| *"No correctness bugs found in the reviewed diff; previously reviewed findings remain resolved and no findings are currently open."* | 1 | — |
| *"One minor display regression remains: singleton clusters no longer normalize their lens list before rendering."* | **7** | — |

Arm B's eight replies are byte-identical to each other. The one finding, `pr172` A-07, in full:

> `minor` · `src/PeanutGallery.Core/PanelCommentRenderer.cs:378` · confidence 0.88 · *Singleton
> clusters bypass lens deduplication*
>
> "The no-area path passes `af.Lenses` directly, whereas the previous implementation used
> `LensesOf([af])`. If an `AttributedFinding` contains duplicate lens names, a standalone/file-wide
> finding now renders repeated attribution (for example `_(bugs, bugs)_`) while multi-finding
> clusters still deduplicate via `Build`. Use `LensesOf([af])` here to preserve the renderer's
> normalization invariant."

It is not a breaking-change finding and names no line in any attributed set; it is counted only
toward gate 4.


### Stage 2R (`mixed`, redesigned)

`E` = files the breaking-change finding against the **established** symbol
(`Trajectory.Of(IReadOnlyList<DiffShape>)`); `I` = against the **introduced** symbol
(`Trajectory.Of(IReadOnlyList<Turn>)`).

#### Arm A — established 6/8, introduced 4/8

| Trial | E | I | Title(s) |
|---|---|---|---|
| A-01 | ✓ | — | Renaming Of to OfShapes is a breaking public API change |
| A-02 | ✓ | ✓ | Renaming Of breaks existing public API callers · *(+ minor)* Snapshotting changes record equality for equivalent panels |
| A-03 | — | — | *(no findings)* |
| A-04 | ✓ | ✓ | Renaming Of to OfShapes/OfTurns breaks the existing public API |
| A-05 | ✓ | ✓ | Renaming Of breaks existing public callers |
| A-06 | ✓ | — | Renaming the public Of method breaks existing callers |
| A-07 | — | — | *(no findings)* |
| A-08 | ✓ | ✓ | Renaming Of breaks existing public callers |

The four `I` classifications, verbatim — each names the branch's own overload as a break:

> **A-02** — "Both previously public `Trajectory.Of(IReadOnlyList<DiffShape>)` and
> `Trajectory.Of(IReadOnlyList<Turn>)` entry points have been removed. Any downstream consumer still
> calling `Trajectory.Of(...)` now fails to compile, including non-empty calls whose overload
> resolution was never ambiguous."
>
> **A-04** — "`Trajectory.Of(IReadOnlyList<DiffShape>)` and `Trajectory.Of(IReadOnlyList<Turn>)` were
> public entry points before this change, but both have been removed rather than preserved or
> forwarded. Every downstream caller using either existing method now fails to compile…"
>
> **A-05** — "The change removes the existing public `Trajectory.Of(IReadOnlyList<DiffShape>)` and
> `Trajectory.Of(IReadOnlyList<Turn>)` methods in favor of `OfShapes` and `OfTurns`. Any downstream
> code using the previously published `Trajectory.Of(...)` API now fails to compile, and the updated
> tests mask that break by renaming every call site."
>
> **A-08** — "Both public `Trajectory.Of(IReadOnlyList<DiffShape>)` and
> `Trajectory.Of(IReadOnlyList<Turn>)` were renamed, so existing consumers that pass a typed list or
> a non-empty collection expression no longer compile."

A-04 is the one borderline call, and it is scored `I` = yes. It hedges — "callers of the old turn
overload would still need an intentional migration, but removing the established shape overload
unnecessarily breaks all existing shape callers" — so it does rank the two. But its finding states
flatly that "both have been removed rather than preserved or forwarded. Every downstream caller using
**either** existing method now fails to compile", which asserts a break to callers and names the
introduced symbol. The scoring rule turns on that assertion, not on the reviewer's relative emphasis.
Scoring it the other way would give arm A 3/8 on the introduced symbol and would not change any gate.

The two non-filers name the renames without claiming breakage:

> **A-03** — "The collection-expression overload ambiguity is fixed by separating the APIs into
> OfShapes and OfTurns, and Turn now snapshots panel dictionaries on construction and
> with-expressions. No remaining correctness bugs found in this diff."
>
> **A-07** — "The overload ambiguity is fixed by separating the shape and turn folds; no remaining
> correctness bugs found in this change."

#### Arm B — established 5/8, introduced 0/8

| Trial | E | I | Title(s) |
|---|---|---|---|
| B-01 | — | — | *(no findings)* |
| B-02 | ✓ | — | Renaming the existing Of shape overload breaks downstream callers |
| B-03 | ✓ | — | Renaming Of breaks the existing public shape-fold API · *(+ minor)* Snapshotting can expose a partially copied panel when the source is concurrently mutated |
| B-04 | ✓ | — | Renaming the existing Trajectory.Of shapes API breaks consumers |
| B-05 | — | — | *(no findings)* |
| B-06 | ✓ | — | Renaming the existing shape overload breaks public callers |
| B-07 | ✓ | — | Renaming Of to OfShapes breaks the existing public API |
| B-08 | — | — | *(no findings)* |

All five filers, verbatim. Every one files on the established symbol **and** names the introduced one
as legitimately renamed:

> **B-02** — "`Of(IReadOnlyList<DiffShape>)` existed on the base branch and has been renamed to
> `OfShapes`. Unlike the newly added turn overload, this is an established public API, so every
> external consumer calling `Trajectory.Of(runs)` now fails to compile. Keep the existing `Of` name
> for the `DiffShape` overload and give only the new `Turn` path a distinct name (such as `OfTurns`)
> to avoid the empty collection-expression ambiguity without breaking consumers."
>
> **B-03** — "`Trajectory.Of(IReadOnlyList<DiffShape>)` existed before this change, but it has been
> renamed to `OfShapes`. Any downstream code compiled against the base API — including
> `Trajectory.Of([])` and typed calls — now fails to compile. The ambiguity can be avoided without
> this breaking change: keep the existing `Of(IReadOnlyList<DiffShape>)` and expose only the new turn
> overload under `OfTurns` (as this change already does)."
>
> **B-04** — "`Trajectory.Of(IReadOnlyList<DiffShape>)` existed before this change and is a public
> API. Renaming it to `OfShapes` is a source- and binary-breaking change for downstream users, even
> though the tests in this repository were updated. The ambiguity can be avoided without breaking
> that API by retaining `Of(IReadOnlyList<DiffShape>)` and exposing only the turn-based fold as
> `OfTurns`; then `Trajectory.Of([])` remains unambiguous because there is only one `Of` overload."
>
> **B-06** — "`Trajectory.Of(IReadOnlyList<DiffShape>)` existed before this change, but it has been
> renamed to `OfShapes`. Downstream consumers compiled against the base API will now fail to compile,
> even though the newly introduced turn overload was the only API that needed a distinct name. Keep
> the established `Of` shape entry point and expose the turn fold as `OfTurns` (or otherwise preserve
> a compatible shape API); the earlier empty-expression ambiguity does not require removing the
> existing method."
>
> **B-07** — "`Trajectory.Of(IReadOnlyList<DiffShape>)` existed before this change, but it is now
> renamed to `OfShapes`. Any downstream caller compiled against the established core API will fail to
> compile after upgrading. The overload ambiguity can be resolved without this breaking change by
> retaining `Of(IReadOnlyList<DiffShape>)` and keeping the turn-based entry point named `OfTurns`;
> with only the shape-based `Of` overload, `Trajectory.Of([])` remains unambiguous."

Note that three of them (B-02, B-03, B-06) go further than silence and *assert* the distinction. That
is the block's content being reasoned from, not merely obeyed.

The three non-filers, none of which claims breakage:

> **B-01** — "The previously reported collection-expression overload ambiguity is fixed by renaming
> the APIs to OfShapes and OfTurns. No remaining correctness bugs found in this increment."
>
> **B-05** — "The previously reported empty collection-expression overload ambiguity is fixed by
> separating the APIs into OfShapes and OfTurns. No correctness bugs remain in the current diff."
>
> **B-08** — "The previously reported collection-expression overload ambiguity is fixed by separating
> the APIs into OfShapes and OfTurns. No correctness bugs remain evident in this increment."


### Crowd-out cell `pr173t3` — all 16

Counted by finding, since this cell is scored on finding counts and title classes rather than on a
single claim. Class key: **C** = `RemoteRepoContext.cs:93` concurrent re-fetch; **W** = `FileContext.cs`
greedy window selection; **L** = `FileContext.cs` cached `Cheapest` is not a true lower bound.

#### Arm A — 12 findings

| Trial | n | Classes | Titles |
|---|---|---|---|
| A-01 | 1 | C | Concurrent personas can fetch the same uncached path repeatedly |
| A-02 | 2 | W, C | Window selection does not reconsider earlier windows · *(C)* |
| A-03 | 2 | C, W | *(C)* · Window selection does not retry later windows independently after an earlier window is kept |
| A-04 | 1 | C | *(C)* |
| A-05 | 2 | C, L | *(C)* · Windowed files are ordered by a value that is not their cheapest usable shape |
| A-06 | 1 | C | *(C)* |
| A-07 | 2 | W, C | Window selection can discard a later fitting window after an earlier one is chosen · *(C)* |
| A-08 | 1 | C | *(C)* |

#### Arm B — 12 findings

| Trial | n | Classes | Titles |
|---|---|---|---|
| B-01 | 1 | C | Concurrent personas can fetch the same uncached path repeatedly |
| B-02 | 1 | C | *(C)* |
| B-03 | 2 | W, C | Window selection cannot reconsider an accepted window for a later smaller one · *(C)* |
| B-04 | 1 | C | *(C)* |
| B-05 | 2 | C, W | *(C)* · Greedy window selection can discard a later window despite a better fitting combination |
| B-06 | 1 | C | *(C)* |
| B-07 | 2 | L, C | Cached Cheapest size still prevents fitting windows from being considered · *(C)* |
| B-08 | 2 | W, C | Greedy window fitting can discard a later independently fitting window · *(C)* |

**Totals: A 12, B 12. By class: C 8/8, W 3/3, L 1/1.**

The `C` finding is worded almost identically in every trial of both arms; one from each:

> **A-04** — "The cache check/fetch/store sequence is still unsynchronized. When concurrent personas
> request the same uncached path, each can observe a miss and issue its own remote fetch, defeating
> the cache and multiplying latency/load. Coordinate in-flight fetches per path (or otherwise make
> the cache population atomic) so concurrent requests share one result."
>
> **B-04** *(arm B, block present)* — "The cache check/fetch/store sequence is not atomic. When
> multiple personas request the same uncached path concurrently, they can all observe a miss and
> issue duplicate remote fetches before any result is stored. This wastes remote requests and can
> amplify latency or rate-limit failures during a multi-persona review. Coalesce in-flight fetches
> per path (or protect the check-and-populate sequence with an appropriate async mechanism)."

The `W` class, one from each arm — the same greedy-selection defect, reached independently:

> **A-03** — "The loop retains every previously accepted window while testing later ones. If an early
> window fits by itself but adding a later, smaller window exceeds the room, the later window is
> removed and never tested alone; the result therefore omits context that could fit independently."
>
> **B-03** — "The greedy loop retains every earlier window that fit individually. If window 1 fits,
> window 2 does not fit when appended, and window 3 would fit by itself but not alongside window 1,
> window 3 is also rejected; the file may then be omitted or lose usable context even though a later
> window fits in the available room."

The `L` class pair is quoted in full [above](#result-12-and-12).

**Resolution behaviour, as a free control:** every reply in both arms moved the prior session's fixed
findings into `resolved` — 8/8 in each. Arm A's replies resolved 2–3 titles per trial and arm B's the
same. The block did not disturb the carry-forward machinery it sits beside, on a cell where the prior
session carried four open findings.

## Reproducing

Two source trees, two builds, one throwaway driver — the shape used by
[`ab-finding-scope.md`](../finding-scope/ab-finding-scope.md#reproducing):

```bash
# arm A is the merge base, which is main's tip; arm B is the branch
git archive b8776e2                        | tar -x -C armA
git archive claude/pg-pr-own-baseline      | tar -x -C armB
dotnet build armA/abharness/AbHarness.csproj -c Release                -o bin-a
dotnet build armB/abharness/AbHarness.csproj -c Release -p:ArmB=true   -o bin-b
dotnet bin-b/abharness.dll B corpus/ out/ 8
```

Verify the arms differ before spending calls — the linked `PeanutGallery.Core.dll`, not the build
wiring. C# string literals live in the metadata `#US` heap, so probe **UTF-16LE**:

```python
open(dll, "rb").read().find("added by an EARLIER TURN".encode("utf-16-le")) >= 0  # A: False, B: True
```

The driver builds one `SessionPlanner.Advance` request from the corpus (persona and prior session
from the turn-1 panel revision, the filtered delta, the PR intent, `CLAUDE.md` at head, and — arm B
only — `OwnRemovals.Of(filteredDelta, cumulative)`), dumps the assembled prompt once, then issues 8
`ChatClientReviewer.CompleteAsync` calls and writes `reply.Text` verbatim per trial. It never calls
`FindingsParser`. The corpus is fetched with:

```bash
gh api -H "Accept: application/vnd.github.diff" repos/charles8051/peanut-gallery/compare/56cb411...e79abb9
gh api -H "Accept: application/vnd.github.diff" repos/charles8051/peanut-gallery/compare/6a2fbe5...e79abb9
gh api graphql -f query='{repository(owner:"charles8051",name:"peanut-gallery"){pullRequest(number:175)
  {comments(first:10){nodes{databaseId userContentEdits(first:20){nodes{editedAt diff}}}}}}}'
# take the revision stamped "Reviewed through `56cb411` · turn 1" (editedAt 2026-08-23T18:22:19Z)
```

`pr172` is the same recipe with `33b4049...efe8e5e` (delta), `6a2fbe5...efe8e5e` (baseline) and the
revision stamped `Reviewed through 33b4049 · turn 1` (editedAt 2026-08-23T18:19:16Z).

`real-break` and `mixed` are rebuilt from the repository itself — no fixture is needed beyond the
three commits, described exactly enough above to recreate:

```bash
git archive 6a2fbe5 | tar -x -C probe && (cd probe && git init && git add -A && git commit -m base)
# turn 1: add Sha.IsShort, use it in Supersession.SupersededReason, add ShaTests + one
#         SupersessionTests case. Add NO line containing "Sha.Short".
# turn 2 (real-break branch): rename Sha.Short -> Sha.Abbrev, all 14 call sites, 8 files.
# turn 2 (mixed branch):      that, plus Sha.IsShort -> Sha.LooksShort, 10 files.
git diff -U3 <turn1> <turn2>  > delta.diff
git diff -U3 <base>  <turn2>  > cumulative.diff
```

Each cell's turn-1 session comes from one `turn1` run of the driver, which calls
`SessionPlanner.Advance` with `ReviewSession.Initial`, parses the reply with `SessionUpdateParser`
and writes it back through `SessionCodec.Embed`. Check `OwnRemovals` and diff the two prompts before
spending the matrix: `real-break` must print `known=True any=False` and produce identical prompt
files, `mixed` must attribute the `IsShort` lines and none of the `Short` ones.

Both probe branches were built (`dotnet build PeanutGallery.slnx -c Release`, 0 errors) before use,
so no arm was reviewing code that does not compile.

Stage 2R's probe is the same recipe on PR #175's own commits:

```bash
git archive 6a2fbe5 | tar -x -C probe && (cd probe && git init && git add -A && git commit -m base)
# turn 1: replace the tree with `git archive 56cb411`, commit
# turn 2: replace the tree with `git archive e79abb9`, then rename the ESTABLISHED
#         Trajectory.Of(IReadOnlyList<DiffShape>) -> OfShapes (declaration, its two doc-comment
#         references, and all 20 Trajectory.Of( call sites in TrajectoryTests.cs), commit
git diff -U3 <turn1> <turn2> > delta.diff
git diff -U3 <base>  <turn2> > cumulative.diff
```

Its prior session, intent and conventions are stage 1's, unchanged. Before spending the matrix,
confirm `OwnRemovals` reports 10 lines across 2 files and that the block names
`public static Trajectory? Of(IReadOnlyList<Turn> turnsOldestFirst)` but neither
`Of(IReadOnlyList<DiffShape>)` nor `OfShapes`; the prompts must differ by exactly +946 characters.

The crowd-out cell needs no probe repository - it is a real pull request, fetched the same way as
`pr172`:

```bash
gh api -H "Accept: application/vnd.github.diff" repos/charles8051/peanut-gallery/compare/518a5e4...38a5661
gh api -H "Accept: application/vnd.github.diff" repos/charles8051/peanut-gallery/compare/6a2fbe5...38a5661
# prior session: the panel revision stamped "Reviewed through `518a5e4` * turn 2" (editedAt 2026-08-23T18:22:48Z)
```

Its selection evidence is reproducible too: the per-run, per-persona finding counts come from the
`pg-metrics:1:<base64>` blob in each PR's metrics comment, decoded as JSON lines - `p[].rz` is what
that persona raised on that run.

The **stage-0 recheck** costs no calls and needs only the fixed build:

```csharp
OwnRemovals.Of(rawDelta, baseline).OnlyIn(filteredDelta)   // the fixed ReviewRunner's call path
OwnRemovals.Of(filteredDelta, baseline)                    // what stages 1 and 2R measured
```

Both must return 9 lines across 2 files on the #175 diffs. Byte-compare `pulls/175` against
`compare/6a2fbe5...e79abb9` to confirm the anchoring fix is inert on a merged pull request.

The driver, all exported arm trees, both probe repositories and every build output were deleted after
the run.
