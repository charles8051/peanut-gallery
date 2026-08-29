# Finding scope: A/B evaluation (the `scope` field)

**Date:** 2026-08-23 · **Change under test:** [`claude/pg-finding-scope`](https://github.com/charles8051/peanut-gallery/pull/174) ·
**Feature:** [Finding scope](spec.md) · **Protocol:** [pre-registered in that spec](spec.md#pre-registered-ab) ·
**Predecessors:** [the reverted `yagni` lens](../auto-panel/ab-yagni-lens.md) ·
[`disproportion`](../auto-panel/ab-disproportion-lens.md)

> **Outcome: do not ship as it stands.** Across 48 arm-B trials the reviewer emitted the
> `pre-existing` verdict **zero times**. Precision therefore has **no denominator** (0/0), and the
> pre-registered power floor of 20 emitted verdicts is not met, so the primary result is
> **INCONCLUSIVE — which the protocol states explicitly is *not* a pass**. The one ship gate that can
> be evaluated on its own terms, gate 3 (`blind`), **fails**: 0/8 `unknown` against a target of ≥ 6/8.
> The field is not silent — it is **constant**: 67 of 67 findings came back `introduced`.
> See [Decision](#decision).

This evaluation was run by an agent that did **not** write the feature, and that was told to try to
break it rather than to confirm it — a model asked to justify a claim is markedly more forgiving than
one asked to falsify it, and the author of a change is the worst-placed reader of its evidence.

## Question

The spec pre-registered one primary quantity and three ship gates. Restated, unchanged:

```
precision = (pre-existing verdicts whose citation checks out) / (pre-existing verdicts emitted)
```

**Computed on the raw model reply, not on parsed `Finding`s.** `FindingScopes.Read` demotes an
uncited `pre-existing` to `Unknown`, so scoring parsed output would let the parser launder the exact
failures under test. An uncited attempt counts as an emitted verdict **and** as a miss. This
evaluation never calls `FindingsParser`; it reads `reply.Text` and scores the `scope` string the
model actually wrote, normalised by the same rule `FindingScopes.Normalize` uses (letters and digits
only, lowercased).

## Method

Six cells × 8 trials × 2 arms = **96 calls**, one fixed diff-tier persona. Nothing about the targets,
the cells, the trial counts or the adjudication ladder was changed after any result was seen.

### Arms are two builds

The scope protocol is a compiled string, so the arms are two builds and the claim was verified
against the linked `PeanutGallery.Core.dll` rather than trusting the build wiring — the
[`disproportion`](../auto-panel/ab-disproportion-lens.md) evaluation's precaution, kept:

| Arm | Build | `Core.dll` contains `scopeEvidence` (UTF-16LE, the `#US` heap) |
|---|---|---|
| A | `e39382d` — current `main` | **no** |
| B | this branch rebased onto `e39382d` | **yes** |

Arm A's assembled prompt contains **no `scope` schema field and no protocol clause** in any cell (the
only occurrences of the word anywhere in arm A are inside the repo's own source text, carried in by
the `pr84` and `pr90` diffs); arm B's contains the clause once. Arm B's prompt is exactly **+824
bytes** in every one of the six cells — the two schema fields plus `PromptAssembly.ScopeProtocol`,
and nothing else. The two arms differ in that clause and in nothing else.

### Authorised deviation 1: arm A is current `main`, not the branch's parent

The branch was cut at `6a2fbe5`. Five PRs merged since, including
[#173](https://github.com/charles8051/peanut-gallery/pull/173) (windowed context, issue
[#164](https://github.com/charles8051/peanut-gallery/issues/164)) — which this feature's own spec
names as its **blocker**, and which changes what context a reviewer receives on exactly the
`inherited` and `blind` cells. Measuring against the stale parent would have measured two changes at
once. So the branch was rebased onto `e39382d` first and the rebased branch used as arm B.

The rebase took two conflicts, both from work that landed after the branch was cut, and both
resolved by keeping `main`'s newer structure and re-applying the scope change on top of it:

- `PromptAssembly.cs` — [#171](https://github.com/charles8051/peanut-gallery/pull/171) moved
  `Proportionality` to `PersonaPrompt`; only `ScopeProtocol` was kept here.
- `PanelCommentRenderer.cs` — [#172](https://github.com/charles8051/peanut-gallery/pull/172)
  factored bullet emission into `AppendFindingBullet`; the scope tag and evidence calls moved into
  that one method rather than staying at the (now-deleted) inline site.

`dotnet test PeanutGallery.slnx -c Release` on the rebased tree: **904 passed, 0 failed** (578 core,
262 engine, 64 desktop). The rebase is sound.

> The rebase was performed **locally, for the measurement only**. It has not been force-pushed: the
> published branch is still based on `6a2fbe5`, and needs the same rebase before it can merge.

### Authorised deviation 2: a throwaway driver, because the one-shot path cannot supply context

`Commands.ReviewAsync` → `ReviewPlanner.Plan` → `PromptAssembly.Build` takes no `ContextSelection`;
only the stateful path (`SessionPlanner.Advance`) accepts one. The `inherited` and `blind` cells are
*defined* by the presence and absence of sibling context, so `pg review` could not have run them.
A throwaway driver called `SessionPlanner.Advance` directly with an explicit `ContextSelection`
against a real `ChatClientReviewer`, capturing `reply.Text` per trial. It was **deleted** when the
measurement finished, as #173's agent did with its perf benchmark. No harness code is shipped.

Everything else is a production run: real provider, real `peanut.json`, `openai/gpt-5.6-luna`,
`architect` at its configured temperature of 1.0, `ContextBudget.Fit` at the default 64 KB budget.

### What the pre-flight changed, and what it did not

Six calls were spent before the matrix, to confirm the harness captured raw text and that the two
builds differed. They also revealed two defects in the **first draft of the synthetic probes**, both
of which made a cell produce no data at all rather than producing the wrong answer:

1. The `bug-hunter` persona returned an empty findings list on the pre-flight cell. A persona that
   reports nothing produces no verdicts to score, so `architect` was fixed as the one diff-tier
   persona before the matrix began.
2. The first `inherited` diff changed only a threshold, so the hazard sat entirely outside the added
   lines and the reviewer reported nothing. It was rebuilt so the diff's own added lines carry the
   shared read-decide-write shape — the repeat-class shape, where every reported instance *was* in
   the author's diff and the **class** was shared with untouched siblings. A second draft
   accidentally introduced a genuinely-novel bug (a duration fetched and never used) that dominated
   the cell; that was removed too.

These are corpus-construction fixes to probes that yielded no measurement. **No target, cell,
trial count, arm or ladder step was altered**, before or after any result.

## Corpus

| Cell | Diff | Context supplied | Pre-registered correct answer |
|---|---|---|---|
| `novel` | synthetic: adds `PayoutPolicy` (36 lines) whose unguarded `SaveAsync` write exists nowhere else | 3 clean siblings, all using `SaveIfUnchangedAsync(…, snapshot.Version, …)` (3.8 KB) | `introduced` |
| `inherited` | synthetic: `ChargebackPolicy` (+14/−1), adding a partial-refund branch with the same unguarded `SaveAsync` | the 3 **untouched** siblings, all sharing that unguarded write (2.9 KB) | `pre-existing`, citing an untouched sibling |
| `blind` | the `inherited` diff | **none** (the #164 condition) | `unknown` |
| `pr82` | 6 files, 18.8 KB — file context / `ContextBudget` | changed files at the first-pushed commit (47.9 KB) | judged per the ladder |
| `pr84` | 8 files, 27.0 KB — the adversarial verification pass | changed files (50.5 KB) | judged per the ladder |
| `pr90` | 6 files, 22.0 KB — `auto` panel mode + pinning | changed files (54.8 KB) | judged per the ladder |

The real diffs are reconstructed as first pushed, per
[`ab-evaluation.md`](../auto-panel/ab-evaluation.md#reproducing), and get `CLAUDE.md` as conventions
because they are this repo; the synthetic probes get none, because they are not.

Every context file fitted **whole** on all three real cells (6/6, 8/8, 6/6 kept; nothing omitted, no
window elided). So #173's windowing never engaged on this corpus — the rebase was still the right
call, because it changes the code path either way, but no result here depends on it.

## Result 1: the verdict under test was never emitted

Findings are counted from the raw replies. Arm A is not asked for scope, so it emits none; its column
is the crowd-out control, not a comparison.

| Arm | Cell | Trials | Empty findings | Unreadable | Findings | `introduced` | `pre-existing` | `unknown` |
|---|---|---|---|---|---|---|---|---|
| A | `novel` | 8 | 0 | 0 | 8 | — | — | — |
| A | `inherited` | 8 | 5 | 0 | 3 | — | — | — |
| A | `blind` | 8 | 2 | 1 | 5 | — | — | — |
| A | `pr82` | 8 | 0 | 0 | 19 | — | — | — |
| A | `pr84` | 8 | 0 | 0 | 15 | — | — | — |
| A | `pr90` | 8 | 0 | 0 | 17 | — | — | — |
| B | `novel` | 8 | 0 | 0 | 8 | **8** | **0** | **0** |
| B | `inherited` | 8 | 2 | 0 | 6 | **6** | **0** | **0** |
| B | `blind` | 8 | 2 | 0 | 6 | **6** | **0** | **0** |
| B | `pr82` | 8 | 0 | 0 | 16 | **16** | **0** | **0** |
| B | `pr84` | 8 | 0 | 0 | 17 | **17** | **0** | **0** |
| B | `pr90` | 8 | 0 | 0 | 14 | **14** | **0** | **0** |

Arm B: **67 findings, 67 `introduced`, 0 `pre-existing`, 0 `unknown`.** Verified independently of the
scoring script by grepping the 48 reply files for the raw field:

```bash
grep -oh '"scope"[[:space:]]*:[[:space:]]*"[^"]*"' B-*-*.txt | sort | uniq -c
#   38 "scope": "introduced"
#   29 "scope":"introduced"
grep -c "pre-exist" B-*-*.txt | grep -v ":0"      # no output: zero replies mention it at all
```

The string `pre-exist` does not occur in **any** of the 48 arm-B replies — not in a scope value, not
in a body, not in an evidence field. The prompt asks for one of three values and receives one.

## Result 2: the ladder had nothing to climb

The adjudication ladder — (1) does the cited path/symbol exist, (2) was the cited file in the diff,
(3) does the cited file actually have the property — is defined over emitted `pre-existing` verdicts.
There were none, so no step ran and there is no surviving citation to record verbatim. This is
recorded as an absence, not as a clean sheet.

## Result 3: the citations that *were* produced are all discarded

Every one of arm B's 67 findings carried a non-empty `scopeEvidence` (67/67, mean 155 characters).
All 67 were paired with `introduced`, and `FindingScopes.Read` carries evidence **only** on
`pre-existing`:

```csharp
if (scope != FindingScope.PreExisting) { return (scope, null); }
```

So ≈10 KB of model-authored citation across this corpus was generated, billed, and dropped at the
parse boundary. That is the design working as specified — evidence means nothing on the other two
values — but on the observed distribution it is the *only* thing the evidence field does.

Downstream, on this distribution, the shipped rendering emits **no scope mark on any bullet** (only
`pre-existing` is tagged) while still emitting `CommentRenderer.ScopeLegend` once per comment. Every
review would carry a line explaining a mark that never appears.

## Result 4: the sibling context is read — but only in the direction that says `introduced`

This is the substantive finding, and it is sharper than the tallies.

On `novel`, where the three siblings **disagree** with the changed file, the reviewer reads them,
names them, and uses them — 16 mentions of `SaveIfUnchangedAsync` across 8 trials, with citations like:

> `This new file calls SaveAsync directly; the existing OrderPolicy, RefundPolicy, and ShipmentPolicy all call SaveIfUnchangedAsync with snapshot.Version.`

That is a correct, checkable, well-grounded `introduced` verdict, and the same context is in front of
the reviewer on `inherited`.

On `inherited`, where the three siblings **agree** with the changed file, the same reviewer mentions
them essentially once across 8 trials — the sole instance being a subordinate clause:

> `…the surrounding policies perform only one state read per evaluation.`

— and never reports the shared class at all. It reports a diff-local architectural concern instead
(state/IO fused with a temporal decision) and scopes it `introduced`.

The asymmetry is the point. Sibling context is not being ignored: it is being used **as a contrast
detector**. A sibling that differs from the diff is evidence the reviewer volunteers. A sibling that
matches the diff is invisible, because nothing about it stands out — and "the siblings all do this
too" is precisely the observation the field exists to capture.

## The three ship gates, evaluated individually

| # | Target | Observed | Verdict |
|---|---|---|---|
| 1 | precision ≥ 0.90 over all emitted `pre-existing` verdicts | **0 / 0** — undefined | **Not evaluable.** No denominator. Per the protocol this is not a pass. |
| 2 | **zero** `pre-existing` on `novel` (0/8) | **0/8** | **Holds — vacuously.** The verdict is emitted nowhere, so it is emitted here too. This gate cannot distinguish a well-calibrated field from an inert one, and on this evidence it is measuring the second. |
| 3 | **≥ 6/8 `unknown` on `blind`**, and **zero** `pre-existing` citing an unseen file | **0/8 `unknown`**; 6/8 trials answered `introduced`, 2/8 reported nothing. Second clause holds vacuously. | **FAILS.** |

A trial that reports no findings emits no scope, so the two empty `blind` trials could not have
contributed an `unknown` either way — the ceiling was exactly 6/8. Read on the verdicts actually
emitted, the gate asked for 6 of 6 and got **0 of 6**.

**Power floor:** fewer than 20 emitted `pre-existing` verdicts across the corpus makes precision
inconclusive. Observed: **0 of a required 20**. The primary result is **INCONCLUSIVE**, which the
protocol states is not a pass.

**Secondary (necessary, not sufficient):** recall on `inherited` ≥ 6/8. Observed **0/8**.

### On gate 3, fairly

Gate 3 fails as written, and the failure should be read with its rationale. The gate exists because a
reviewer with no sibling evidence "will answer anyway, and the answer it manufactures is precisely
the one that costs the most: a wrong `pre-existing`". That specific harm **did not occur** — on
`blind` the reviewer manufactured `introduced`, not `pre-existing`, so no dismissal license was
handed out and no unseen file was cited.

What did occur is the other half of the same defect: asked a question it had no evidence for, the
reviewer answered it rather than declining. The prompt tells it plainly that `unknown` "is the
expected answer and nothing is held against it", and in 67 opportunities it used that answer zero
times. A field whose escape hatch is never taken is a field that will also not decline in the
direction that *does* cost the author.

## Result 5: no crowd-out

Arm A is the control the `yagni` lens's revert makes mandatory: does asking for scope change *which*
findings get reported?

**Finding counts are identical: 67 in each arm.** Arm A had 7 empty-finding trials and 1 unreadable
reply (a model-side malformed JSON on `blind` trial 7); arm B had 4 empty and 0 unreadable.

Title classes match cell by cell. On `pr82` both arms independently report the same five: repository
boundary/path traversal, context-source failures escaping the per-persona catch, context read from an
unanchored checkout, byte budget counted in characters, and silent omission when nothing fits. On
`pr90` both report planner failures bypassing the documented fallback, panel pins accepted from
untrusted comments, and auto mode with no configured personas. On `pr84` both report refuted findings
persisting and verification latency being excluded; arm B additionally raised title-only verdict
matching, which arm A did not.

No displacement was observed in either direction. Whatever else is wrong here, the clause is not
crowding anything out.

## Cost

| | Arm A | Arm B | Δ |
|---|---|---|---|
| Input tokens (48 calls) | 512,504 | 520,712 | +8,208 (+1.6%) |
| Output tokens (48 calls) | 60,667 | 64,700 | +4,033 (+6.6%) |

Per call: +171 input, +84 output. The output delta is the `scopeEvidence` strings — the ones the
parser drops.

## What this does and does not establish

**Establishes.** On this repo's production model and configuration, with sibling context supplied,
the `scope` field is a constant: 67/67 `introduced`. Precision cannot be estimated. The `blind` gate
fails. The evidence field's entire output is discarded. There is no crowd-out.

**Does not establish.** That the field is unmeasurable in principle. One model
(`openai/gpt-5.6-luna`), one persona (`architect`), one corpus. The
[`disproportion`](../auto-panel/ab-disproportion-lens.md) evaluation's caveat applies and is
sharpened here: a result of exactly zero across 48 trials has no variance to worry about, but it also
cannot say whether a different persona, a differently-worded clause, or a model less inclined to
attribute everything to the diff in front of it would behave differently.

**Does not establish** that the plumbing is wrong. It is not. `ScopeTally`'s monotone accumulator,
the demotion rule, the codec round-trip and the fenced evidence span are all careful, well-tested
work, and the branch's own review caught two real defects in it. None of that is evidence that the
field earns its place, and it should not be allowed to become evidence: the question this evaluation
asked is whether a reviewer can answer the question, and on this corpus it does not answer it at all.

## Decision

**Do not ship.** The result is **inconclusive on the power floor** — 0 emitted `pre-existing`
verdicts against a required 20 — and **fails gate 3** outright at 0/8 `unknown` on `blind`. Gate 2
holds only vacuously. Per the pre-registered protocol, inconclusive is not a pass, and extending
trials is the named remedy for a thin numerator; but extending trials cannot help a numerator of
exactly zero across 48. Something has to change before more calls are worth buying.

This is the outcome the [`yagni` write-up](../auto-panel/ab-yagni-lens.md) exists to make possible.
It is a success for the process: the field was measured before it shipped, on a protocol written
before any trial ran, by someone who did not build it.

### If this is proposed again

The blind spot is real — the repeat-class case cost four review rounds — and nothing here contradicts that.
What a second attempt needs that this one did not have:

1. **A cell that separates "did not check" from "checked and found agreement".** Result 4 is the
   whole finding: sibling context is used as a contrast detector, so a matching sibling is invisible.
   Any second attempt has to test whether the reviewer can be made to *look for agreement*, not just
   be handed the files.
2. **Ask the question separately, or not of a model at all.** Every finding here arrived
   `introduced` in the same breath as the finding itself. A reviewer generating a claim is the worst
   position from which to ask whether the claim is its own change's fault. The spec's own
   [relationship to #178](spec.md#relationship-to-178-what-a-continued-turn-treats-as-established)
   already names the better mechanism: derive scope from a baseline rather than asking a model to
   self-report it. On this evidence that is not an alternative design — it is the only one with
   evidence behind it.
3. **A recall target that has to be met before precision is worth measuring.** Precision was the
   right primary quantity and it was unmeasurable, because the field never fired. A staged gate
   ("does it ever answer `pre-existing` on the cell built for it?") would have cost 8 calls instead
   of 96.
4. **Decide what the legend costs.** If `pre-existing` is rare, `ScopeLegend` is a per-comment line
   explaining a mark the reader will never see. Cheap, but not free, and currently unearned.

**Do not re-run the real-PR cells alone.** `novel`, `inherited` and `blind` are the probes that
discriminate; `pr82`/`pr84`/`pr90` produced 47 findings and not one scope value other than
`introduced`.

## Reproducing

Two worktrees, two builds, one throwaway driver — the shape used by
[`ab-yagni-lens.md`](../auto-panel/ab-yagni-lens.md#reproducing):

```bash
git worktree add ../arm-b claude/pg-finding-scope && (cd ../arm-b && git rebase origin/main)
dotnet build AbHarness.csproj -c Release -p:PgRoot=<main-worktree> -o bin-a
dotnet build AbHarness.csproj -c Release -p:PgRoot=../arm-b       -o bin-b
dotnet bin-b/abharness.dll B peanut.json architect corpus/ out/ 8
```

The driver builds one `SessionPlanner.Advance` request per cell (persona, `ReviewSession.Initial`,
the parsed patch, and a `ContextSelection` from `ContextBudget.Fit`), dumps that prompt once, then
issues 8 `ChatClientReviewer.CompleteAsync` calls and writes `reply.Text` verbatim per trial. Scoring
reads those files and never touches `FindingsParser`. Both the driver and the corpus were deleted
after the run; the synthetic probes are described precisely enough above to be rebuilt, and the real
diffs are reconstructed from `refs/pull/<N>/head` as
[`ab-evaluation.md`](../auto-panel/ab-evaluation.md#reproducing) describes.

Verify the arms differ before spending calls — the linked `PeanutGallery.Core.dll`, not the build
wiring. C# string literals live in the metadata `#US` heap, so probe **UTF-16LE**:

```python
open(dll, "rb").read().find("scopeEvidence".encode("utf-16-le")) >= 0   # arm A: False, arm B: True
```
