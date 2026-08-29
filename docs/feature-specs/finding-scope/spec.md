# Finding scope: introduced by this change, or inherited from the code around it

**Status:** **NOT SHIPPED** — built, measured, and rejected on the evidence. No `Scope` field exists
in the codebase; this document describes an implementation that was never merged, kept so the idea is
not re-proposed as untried. ·
**Measurement:** [`ab-finding-scope.md`](ab-finding-scope.md) — 0 `pre-existing` verdicts in 48 trials
(67 of 67 findings came back `introduced`), inconclusive on the power floor, fails gate 3. ·
**Issue:** [#168](https://github.com/charles8051/peanut-gallery/issues/168) remains OPEN — the blind spot
is real; asking a model to self-report scope is what failed. ·
**Withdrawn PR:** [#174](https://github.com/charles8051/peanut-gallery/pull/174) ·
**Successor:** [#178](https://github.com/charles8051/peanut-gallery/issues/178) — derive scope from a
baseline rather than asking a model, the only design with evidence behind it.

## Problem

A reviewer is shown a diff. So it cannot tell **"this PR introduced this hazard"** from **"this
hazard is a property of the surrounding code that this PR happens to touch"** — and every finding it
writes is phrased as the former, because that is the only frame a diff supports.

The motivating case is the repeat-class PR, from the author's write-up after seven review rounds: a
decision taken from a stale snapshot and persisted in a separate step. Every instance the panel
reported **was genuinely in the author's diff**. The *class* was a property of the shared
read-decide-write path, present in three sibling policies (`OrderPolicy`, `RefundPolicy`,
`ShipmentPolicy`) that the diff never opened. The author's estimate: a turn-3 finding
saying "this is pre-existing across those three — file separately" would have saved **four rounds**.

Note what is *not* wrong here. The findings were true, they were well-evidenced, and the confidence
gate and the adversarial verification pass both correctly let them through — nothing in the existing
machinery is miscalibrated. The missing thing is a fact about the finding that neither pass asks
for: whose change it is.

## Design

One field on `Finding`, and one rule that keeps it honest.

### The field

```csharp
public enum FindingScope { Unknown, Introduced, PreExisting }

public sealed record Finding(
    Severity Severity, string File, int Line, string Title, string Body,
    double Confidence = 1.0,
    FindingScope Scope = FindingScope.Unknown,
    string? ScopeEvidence = null);
```

`Unknown` is first, so it is `default(FindingScope)` — a `Finding` constructed anywhere, by anyone,
without deciding scope gets the value that asserts nothing.

### The rule: `pre-existing` costs a citation

`FindingScopes.Read(scope, evidence)` **demotes a `pre-existing` that names no sibling code back to
`Unknown`**, and drops blank evidence. This is the entire design, not a validation nicety:

- `pre-existing` is the one value that argues for **closing a finding unread**. It is therefore the
  one value a reviewer can be wrong about *at the author's expense*.
- A reviewer that never saw the sibling files has no way to be right about it except by luck.
- A citation makes the claim checkable in the only way that matters — the reader opens the named
  file. Unlike a wrong severity, a wrong `pre-existing` is falsifiable in ten seconds.

The prompt asks for the citation too. But **a prompt is a request and a parse is a guarantee**, so
nothing downstream has to trust that the model complied.

### Defaulting, and why it points the other way from `Confidence`

`Confidence` defaults to `1.0` — absent means *fully trusted* — because there the dangerous
direction is silently suppressing a finding nobody doubted. `Scope` defaults to `Unknown` because
here the dangerous direction is the opposite: an absent field read as a claim the reviewer never
made. Both defaults obey the same rule (an absent field must never manufacture an assertion); they
point in opposite directions because the assertions are different.

### Rendering: mark `pre-existing`, disclose the convention once

Only `pre-existing` marks a bullet (` · _pre-existing_`). The convention is stated **once per
comment** in `CommentRenderer.ScopeLegend`:

> _Scope: a finding is marked `pre-existing` only when a reviewer checked untouched code and cited
> it. An unmarked finding carries no such citation — it may be one this change introduced, or one
> nobody checked._

The first cut tagged all three values, on the argument that an unmarked bullet reads as *"this PR
did this"*, so silence promotes `unknown` to `introduced` in the reader's head. That argument is
sound and is **not** abandoned — it is what the legend exists for. What changed is the price. A
per-bullet tag is a permanent tax on every finding in every comment, and with findings clustered
under area headings (#172) it is paid on every line of a nested list. Weigh that against what each
value buys a reader:

| Value | Does the tag change what the reader does? |
|---|---|
| `pre-existing` | **Yes** — the fix may belong in a separate PR. This is the whole point of the field. |
| `introduced` | No — it confirms the reading they already had. |
| `unknown` | No — per bullet. What it must prevent is a *systematic* misreading, which is a per-comment property. |

So: one tag where it changes a decision, one line to stop the silence being misread, and nothing on
the bullets that would have said nothing.

**The legend may only state what the absence of a mark establishes.** Its first wording said an
unmarked finding *"was not scope-checked"*. That was true while all three values were tagged and
became false the moment they were not: a reviewer answering `introduced` **did** check, and the
legend denied it — re-collapsing `introduced` into `unknown` at the legend, which is exactly the
collapse the per-bullet tag had been bought to prevent. Caught by this repo's own panel on
[#174](https://github.com/charles8051/peanut-gallery/pull/174) turn 2. Absence of a mark establishes
one thing only: no `pre-existing` citation, hence no license to close the finding unread. The legend
now says that and nothing more.

**`introduced` vs `unknown` is carried in the data and deliberately not surfaced per-bullet.** This
is a decision, not an oversight, and it is recorded here so the next reader does not "fix" it: a
reader does the same thing with an `introduced` finding as with an `unknown` one — treat it as
this change's problem — so the distinction changes no decision at the bullet and does not earn a
tag on every line. It is not discarded. It is parsed, merged, persisted through the session codec
and available to every consumer of a `Finding`: the A/B harness scores it, the desktop shell can
show it, and a future surface with room (a hover, a table, a details pane) can render it with no
schema change. Only the PR-comment projection drops it.

**Join with [#172](https://github.com/charles8051/peanut-gallery/issues/172) (clustered findings).**
#172 nests findings under area headings, and its own review noted that those headings would be
silent on scope — so a reader skimming headings gets the same collapse one level up. Whichever of
the two lands second owns the join. The contract this feature asks of it:

1. The legend is emitted **exactly once per comment**, after all clusters — not once per cluster.
2. A cluster heading must not imply a scope for its members. Proximity and scope are independent, so
   one `introduced` finding and two `pre-existing` ones a few lines apart in one file is an ordinary
   shape, with two opposite calls to action under one heading.
3. If a heading ever does carry a scope, it may only do so when every member agrees — a mixed
   cluster resolves to no mark, for the same reason a contradicted `ScopeTally` resolves to
   `Unknown`.

`ScopeEvidence` renders under the body as ``_Scope evidence:_ `…` ``, flattened through
`CommentRenderer.OneLine` and then fenced by `CommentRenderer.CodeSpan`.

### Rendering: the evidence is fenced, not just flattened

`ScopeEvidence` is model-authored **and** quotes a repository the PR author controls, and it is
displayed under the words "Scope evidence" — it is the one span in the comment a reader is invited
to trust as checkable. Interpolated raw, an evidence string of
`[untouched sibling](https://attacker.example)` renders as a plausible clickable citation, and `**`
or raw HTML restyles the surrounding bullet.

`CommentRenderer.CodeSpan` wraps it in a backtick fence one longer than the longest backtick run in
the text (padding both ends when either end is a backtick, per GFM's space-stripping rule). A code
span rather than backslash-escaping, because nothing inside one is Markdown — the guarantee needs no
argument about which delimiters are reachable in this position — and because the field holds file,
type and function names, which is what code formatting is for.

**Known wider gap.** Finding **titles and bodies** are interpolated raw here and in every other
renderer; this repo has never escaped model-authored Markdown anywhere. That is a larger hole than
this one and it is not this feature's to close, but it should not be left unstated: see the
[Open questions](#open-questions).

### Round trip

The verdict has to survive the turn or it is worthless: a finding established as pre-existing on
turn 2 that returns as `unknown` on turn 3 gets re-litigated, costing exactly the rounds the field
was added to save.

- **Write** — `SessionCodec.WriteSessionBody` emits `scope` (and `scopeEvidence`) only when the
  scope says something. An absent scope decodes to `Unknown`, which is what an absent scope *means*,
  so the frugal encoding is lossless — the same rule `dropped` already follows in that codec.
- **Read** — `SessionCodec.ReadSessionBody` delegates to `FindingsParser.ReadFindingsArray`, so the
  stored blob and a live model reply go through **one** scope decision, including the demotion.
- **Panel** — `PanelSessionCodec` reuses `SessionCodec`'s session shape rather than restating it, so
  the per-persona blob inherits the field with no change. `PanelCodec` (the *pinned panel* blob)
  carries personas, not findings, and needs none.

A session written before this field existed reads back as `Unknown`. Tested.

### Merge

`FindingSynthesis` dedups findings across personas by file + line + normalised title, and picks a
winner by severity then confidence — neither of which carries scope. So scope is **accumulated over
the whole dedup group** (`ScopeTally`) rather than riding along with that winner:

| The group's reports contain… | Merged scope |
|---|---|
| `pre-existing`, no `introduced` | `PreExisting`, with the first citation seen |
| `introduced`, no `pre-existing` | `Introduced` |
| both | `Unknown` — permanently |
| neither | `Unknown` |

A contradicted scope is an unestablished one. Picking a winner would manufacture agreement the panel
does not have, and it fails in both directions: preferring `pre-existing` hands out the dismissal
license, preferring `introduced` is the collapse the default exists to prevent.

**Why an accumulator and not a pairwise fold.** The first cut merged two findings at a time, which
cannot work: a contradiction is recorded as `Unknown`, and `Unknown` is indistinguishable from
"nobody answered". Three personas reporting `introduced`, `pre-existing`, `pre-existing` therefore
resolved the first two to `Unknown` and let the third overwrite it — the panel published the
dismissal-grade verdict *despite an explicit contradictory reviewer*, and which verdict came out
depended on persona enumeration order. Found by this repo's own panel on
[#174](https://github.com/charles8051/peanut-gallery/pull/174), by three lenses at once.

`ScopeTally`'s two booleans only ever go false → true, so resolution is monotone and
order-independent **by construction** — no arrival order can undo a contradiction. That is worth
more than a smarter rule: manufacturing a `pre-existing` nobody agreed on is the single worst thing
this field can do.

### Prompt

One clause, `PromptAssembly.ScopeProtocol`, used by **both** the one-shot path
(`PromptAssembly.BuildUserPrompt`) and the stateful path (`SessionPlanner.BuildSystem`) — two copies
of a protocol clause drift, and a drifted clause means the same word arrives meaning different
things on different turns. It pushes hard toward `unknown` ("that is the expected answer and nothing
is held against it") and tells the reviewer that a pre-existing class is still worth reporting, just
possibly in its own change.

## Affected layers

| Project / area | Change type |
|---|---|
| `PeanutGallery.Core` | `FindingScope` + `FindingScopes` (new pure values/functions); one field pair on `Finding`; parse, encode, render, merge |
| `PeanutGallery.Engine` | none — the shell is unchanged, it carries `Finding` values it does not inspect |
| `PeanutGallery.Cli` | none |
| Desktop shell | none (renders through the core renderers) |

Everything added is immutable values and total functions: no IO, no clock, no `Task`, reflection-free.

## Requirements

- [x] `scope` parses to `introduced` / `pre-existing` / `unknown`, tolerating spelling variants.
- [x] Absent scope defaults to `unknown`; so does the record itself.
- [x] An unreadable scope (wrong JSON type, unknown word, empty string) degrades to `unknown` and
      **never drops the finding** — the parsers here are total.
- [x] `pre-existing` with no evidence is demoted to `unknown`.
- [x] Rendered on every finding, `unknown` included, in both comment shapes.
- [x] Round-trips the session codec and the panel session codec; legacy blobs read as `unknown`.
- [x] **Measured** on the false-`pre-existing` rate — run, and it did not pass: the verdict
      was emitted 0 times in 48 trials, so precision has no denominator. See
      [`ab-finding-scope.md`](ab-finding-scope.md).

## Blocking dependency

**This must not merge before [#164](https://github.com/charles8051/peanut-gallery/issues/164).**

Today a changed file larger than the whole context budget is omitted from context *entirely*, so a
reviewer can be asked "does the surrounding code already do this?" having been shown no surrounding
code. A model asked a question it has no evidence for will answer it anyway, and the answer it
manufactures is precisely the one that costs the most: a wrong `pre-existing` gives an author license
to close a real finding.

The citation rule bounds this — an invented verdict must invent a filename with it, which a reader
can check — but it does not remove it, because a plausible-sounding sibling name is exactly what a
model with no evidence produces. #164 first; then the A/B; then this field is worth having.

## Pre-registered A/B

Written **before** any trial is run, and recorded here so it cannot be adjusted afterwards to fit
what came out. This exists because of the [`yagni` lens](../auto-panel/ab-yagni-lens.md): a change
to this same surface, merged on plausibility, A/B'd afterwards, and
[reverted](https://github.com/charles8051/peanut-gallery/pull/155). That write-up's own list of what
a second attempt needs is the checklist below — more trials per cell, and **a precision target set
in advance**, because "it fires sometimes" is not a result.

### What is measured

The primary quantity is **the precision of the `pre-existing` verdict**, not whether the field fires:

```
precision = (pre-existing verdicts whose citation checks out) / (pre-existing verdicts emitted)
```

**Computed on the raw model reply, not on parsed `Finding`s.** The demotion rule turns an uncited
`pre-existing` into `unknown`, so measuring the parsed output would let the parser launder exactly
the failures under test. An uncited attempt counts as an emitted verdict and as a miss.

### How a verdict is adjudicated — mechanically first

The `yagni` write-up named its own weakest step: it inferred false positives from the *absence* of
the constructs the prompt described, rather than capturing what the model actually cited. The
citation field removes that gap. Adjudication order:

1. **Does the cited path/symbol exist?** `git ls-files` / `grep`. No → **false**, no judgement call.
2. **Was the cited file in the diff?** Yes → **false**: citing changed code as evidence that
   something is pre-existing is a non-answer.
3. **Does the cited file actually have the property?** Read it. No → **false**.
4. Only what survives all three is scored **true**, and each surviving citation is recorded verbatim
   in the write-up so a reader can disagree with step 3.

### Corpus

Six cells. The two synthetic probes are the ones that discriminate; they are cheap to rebuild and
must not be dropped in favour of the real diffs alone.

| Diff | Shape | Correct answer |
|---|---|---|
| `novel` | synthetic: adds a policy whose hazard exists nowhere else; three clean siblings supplied as context | `introduced` |
| `inherited` | synthetic: touches one of four siblings that all share the hazard; the three untouched ones supplied as context | `pre-existing`, citing an untouched sibling |
| `blind` | `inherited` with the sibling context withheld (the #164 condition) | `unknown` |
| `pr82`, `pr84`, `pr90` | the prior evaluations' real diffs, reconstructed as first pushed | judged by hand per the ladder above |

`blind` is the cell that matters most and the one the reverted lens had no analogue of: it asks
whether the reviewer answers a question it has no evidence for.

### Arms and trials

| Arm | Build | Prompt |
|---|---|---|
| A | this branch's parent | no scope protocol |
| B | this branch | scope protocol |

Arms are two **builds**, because the protocol is a compiled string; verify the linked
`PeanutGallery.Core.dll` differs rather than trusting the build wiring. **Eight trials per cell**
(the reverted lens ran three, and its own conclusion was that run-to-run variance rivalled the
effect — `pr84` fired 0/3 in one run and 2/3 in another at temperature 0.2). One fixed diff-tier
persona, so the measurement isolates the scope verdict from panel-selection variance:
6 × 8 × 2 = **96 calls**.

Arm A emits no scopes at all, so it is not the precision comparison — it is the **crowd-out
control**, the analogue of the concern that sank the `yagni` lens. It answers: does asking for scope
change *which findings get reported*? Compared by finding count and by title overlap per cell.

### Targets, set now

> **Run on 2026-08-23. Result: [`ab-finding-scope.md`](ab-finding-scope.md).**
> Nothing below was changed after any trial.

**Ship gate (all three must hold):**

| # | Target | Why this number |
|---|---|---|
| 1 | **precision ≥ 0.90** over all `pre-existing` verdicts emitted | one wrong dismissal-license per ten is already generous for a claim whose whole purpose is to let a finding be closed |
| 2 | **zero** `pre-existing` on `novel` (0/8) | firing where the hazard is provably novel is the failure mode with no mitigation |
| 3 | **≥ 6/8 `unknown` on `blind`**, and **zero** `pre-existing` citing a file the reviewer was never shown | this is the #164 interaction; a reviewer that invents evidence here invalidates the field regardless of how it scores elsewhere |

**Power floor:** fewer than **20** emitted `pre-existing` verdicts across the corpus makes the
precision figure **inconclusive**, which is *not* a pass. Report it as inconclusive and extend trials
rather than shipping on a thin numerator.

**Secondary (necessary, not sufficient):** recall on `inherited` ≥ 6/8. Reported, and explicitly
**not** a ship gate on its own — "the field fires" is the result the reverted lens produced.

**Revert trigger after shipping:** any single false `pre-existing` observed on a real PR is written
up here, and two on distinct PRs revert the field. A wrong dismissal is not a nit; it is worse than
no field at all.

## Relationship to #178 (what a continued turn treats as established)

[#178](https://github.com/charles8051/peanut-gallery/issues/178) is the same question asked along
the time axis rather than the space axis. This feature asks *"is this hazard in code my diff added,
or in the untouched siblings beside it?"*; #178 asks *"is this API established, or did an earlier
turn of this very PR introduce it?"* Both fail the same way — a reviewer treats something as
pre-existing that the change under review is responsible for — and both are answered by the same
missing input: a baseline to compare against. Here the baseline is the untouched sibling files (and
why this is blocked on #164); there it is the PR's own merge-base.

They may well share a mechanism, and if #178 lands first the honest move is to derive `Scope` from
whatever baseline it establishes rather than asking a model to self-report it. Recorded so the two
are not designed twice, in opposite directions. This spec does **not** claim to solve #178.

## Out of scope

- **Making the reviewer go and look.** The field records an answer; nothing here fetches sibling
  files. That is #164's job, and it is why this is blocked on it.
- **Acting on scope** — no gating, no severity adjustment, no suppression of `pre-existing` findings.
  A first cut that hid them would be the dismissal license built directly into the tool. Scope is
  shown to the reader and to the next turn; humans decide.
- **Splitting a PR automatically** ("file these separately"), which the issue mentions as the value
  but which is a workflow feature, not a field.
- **Scope on the verification pass.** `SessionPlanner.Verify` judges whether a finding is *true*;
  whose change it is is a different question and mixing them would give the refuter a second reason
  to drop things.

## Open questions

- [ ] Should a `pre-existing` finding be sorted below `introduced` ones of the same severity? Cheap,
      but it is a soft form of "acting on scope" and wants the A/B first.
- [ ] Should the citation be *structurally* a path (validated against the repo) rather than free
      text? Stricter and mechanically checkable, but a reviewer's honest answer is often "the same
      shape in `Foo` and `Bar`", which a path field cannot hold.
- [ ] **Nothing in this repo escapes model-authored Markdown in finding titles or bodies** —
      now tracked as [#177](https://github.com/charles8051/peanut-gallery/issues/177) (security).
      They are interpolated raw into every comment renderer, so a title of
      `[click here](https://…)` renders as a link this tool appears to have written.
      `ScopeEvidence` is fenced (above) rather than left to match that precedent; the general fix
      (one shared fence helper at every model-authored interpolation, with hostile-output tests)
      belongs to #177, not here. Related mitigation already in place:
      `PanelCommentRenderer.AppendRefutations` deliberately avoids `<details>` for the same reason.

## Related

| Type | Link |
|---|---|
| Issue | [#168](https://github.com/charles8051/peanut-gallery/issues/168) |
| Blocking issue | [#164](https://github.com/charles8051/peanut-gallery/issues/164) (large changed files get zero context) |
| Adjacent question | [#178](https://github.com/charles8051/peanut-gallery/issues/178) (a continued turn cannot tell code its own PR introduced from established API) |
| Wider Markdown gap | [#177](https://github.com/charles8051/peanut-gallery/issues/177) (model-authored finding text rendered raw across all renderers) |
| Rendering join | [#172](https://github.com/charles8051/peanut-gallery/issues/172) (clustered findings — see [Rendering](#rendering-mark-pre-existing-disclose-the-convention-once)) |
| The precedent this must not repeat | [`auto-panel/ab-yagni-lens.md`](../auto-panel/ab-yagni-lens.md) |
| A/B harness shape | [`auto-panel/ab-evaluation.md`](../auto-panel/ab-evaluation.md) |
| Session persistence | [`stateful-sessions/spec.md`](../stateful-sessions/spec.md) |
| Docs index | [`docs/INDEX.md`](../../INDEX.md) |
