# ADR — Auto panel (orchestrator-generated adversarial personas)

**Status:** Draft
**Date:** 2026-07-23
**Deciders:** Charles Lee
**Feature:** [Auto panel](spec.md)

---

## Context

The reviewer panel is static: the same hand-authored personas run on every PR regardless
of what the change touches. That under-covers domain-specific risk (a migration, a
concurrency change, a parser rewrite each want a different adversary) and costs per-repo
tuning. The proposal is an **orchestrator** that reads the PR and constructs a bespoke
adversarial panel — an "auto" mode.

Two forces shape the design. First, the tool's incremental review is **keyed on stable
persona identity**: one self-updating comment per persona, with the review session encoded
inside that comment ([stateful-sessions](spec.md)). A panel that changes its personas across
pushes would orphan comments and lose continuity. Second, the diff is **untrusted input** —
letting PR contents decide *what personas exist* is a (mild) prompt-injection surface.

---

## Decision 1 — Auto mode is an additive panel *source*, not a new review machinery

### Rationale

A config panel and an orchestrator-generated panel are two ways to produce the *same value*:
an `IReadOnlyList<Persona>`. Everything downstream — planning, fan-out, sessions, comment
rendering, resolve/withdraw — is identical. So auto mode is a new **shell** step that emits
personas, feeding the unchanged pure core. This keeps the core ignorant of orchestration
(ADR-0001): the nondeterministic model call lives in the shell; the persona list it returns
is an immutable value the core consumes exactly as if it came from `peanut.json`.

### Consequence

No change to `ReviewPlanner.Plan`, the session model, or the renderer. The new surface is one
shell port (`IPanelPlanner`) plus a config flag. `fixed` mode is byte-for-byte today's behavior.

---

## Decision 2 — Freeze the panel on PR-open; pin it in PR state; reuse across pushes

### Rationale

This is the load-bearing decision. Regenerating the panel per push would break the one-comment-
per-persona identity the whole stateful design depends on: turn 2's personas would not match
turn 1's comment markers, orphaning comments and losing resolve/withdraw tracking. Generating
the panel **once at PR-open** and pinning it removes the tension entirely — auto mode decides
*who reviews* at open time; after that it is the existing stateful flow, unchanged. The pinned
panel is persisted the same way sessions are: a `<!-- pg-panel:1:... -->` marker written into a
PR comment, decoded by a pure `PanelCodec` (mirror of `SessionCodec`). The orchestrator therefore
runs **at most once per PR**, which also bounds its cost and latency to PR-open.

### Consequence

A new pure `PanelCodec` (`Embed`/`Extract`) and a pinned-panel value carrying provenance (mode,
orchestrator model, pin sha). Each generated persona gets a **deterministic id derived from its
lens slug** (a pure function) so its marker is stable and reconstructable even if the encoded
panel is ever lost. A drastic mid-life scope change is handled by an explicit re-panel command
(open question), never by silent regeneration.

---

## Decision 3 — Fence the orchestrator: bounded, risk-anchored, conventions-aware, diff-as-data

### Rationale

An LLM told "make whatever personas you see fit" fails two predictable ways: it over-generates
overlapping personas, and it emits generic ones ("Code Quality Reviewer") that add noise without
coverage. And because the diff drives persona construction, a hostile diff could try to steer the
panel toward toothlessness. So the orchestrator is fenced: a hard **count cap (2–4)**; each persona
must be **orthogonal** and **risk-anchored** to a concrete hazard it found in the diff (no generic
lenses); it is **conventions-aware** (fed the repo's `copilot-instructions.md`/`CLAUDE.md` from the
head ref, so it prefers personas that enforce house rules); and it treats the diff strictly as
**data to analyze, never instructions** — the same posture the review prompts already take with
human comments.

### Consequence

The orchestrator meta-prompt encodes the cap, the orthogonality/risk-anchoring requirement, the
conventions block, and the injection posture. Conventions injection is built as its own slice
because it independently improves the fixed-panel path too. A generated persona that cannot name
a diff hazard is rejected.

---

## Decision 4 — Ship three modes; recommend `seed+auto` as the hybrid default to adopt

### Rationale

A full replacement of the curated panel throws away hand-tuned house knowledge (the Architect's
functional-core lens) and makes reviews less reproducible. A pure fixed panel under-covers. The
hybrid keeps a small curated **seed** (an always-on floor — the Bug Hunter is a good baseline) and
lets the orchestrator add 1–3 diff-specific adversaries on top. Three modes give a clean adoption
path and an A/B control: `fixed` (default, unchanged), `auto` (fully dynamic), `seed+auto` (hybrid).
Whether auto actually beats fixed *for us* is an empirical question, so the modes must be selectable
per repo and comparable on real PRs.

### Consequence

`PeanutConfig.panel` with three values; `seed+auto` reuses the existing persona list as the seed,
de-duplicated against generated personas by id. A/B evaluation (auto vs fixed on real PRs) is a
tracked slice, run first on peanut-gallery's own repo.

---

## Decision 5 — Dynamic finders, fixed judge: auto mode composes with the verification pass

### Rationale

The planned adversarial verification pass (refuter/judge that gates findings before posting) and
auto mode are the two halves of the "Refute-or-Promote" shape. Keeping the **verifier constant**
while the **finder panel varies per PR** gives the precision gate a stable contract and lets the
accusers specialize to the change. Coupling them would make both harder to reason about.

### Consequence

The verification pass (specced/tracked separately) consumes whatever panel produced the findings —
fixed or auto — without knowing which. This spec only guarantees the composition; it does not build
the verifier.

---

## Decision 6 — Present the panel as one unified, deduplicated, attributed comment (finding synthesis)

### Rationale

Today each persona owns its own self-updating PR comment. With `N` personas — and especially with a
*dynamic* panel — that has two failure modes: `N` personas reporting the same issue in `N` comments is
precisely the alert-fatigue a good reviewer avoids, and a reader of an auto-mode PR sees comments from
personas that will not exist on the next PR (provenance churn). Speaking for the panel with **one
comment** — the panel's findings deduplicated across personas, ranked, and posted once — reads like a
single coherent review (closer to how Copilot presents) and hides the persona churn that auto mode
introduces. The orchestrator that *convened* the panel is the natural agent to *speak for* it: convene
→ collect → dedup/synthesize → present (orchestrator-as-chair).

Crucially, this is a **presentation** change, not a finder change. The current design *fuses* two things
— the per-persona review session (state) and the per-persona comment (presentation). This decision
**separates** them: finders stay independent and stateful (each keeps its own session for cross-push
continuity), but the reader-facing surface collapses to one comment produced by a **reducer stage**. The
reducer and the verification-pass judge (Decision 5) are the same concern — "reduce the finding set
before posting" — so they are built together and **sequenced after verification**: verify → dedup →
post. Deduplicating unverified findings is wasted work.

### Consequence

- **Session state relocates.** Per-persona session state moves out of `N` per-persona comments (today's
  `SessionCodec` in each comment) into the single panel comment's hidden state block (or a dedicated
  state-only comment). This changes the [stateful-sessions](spec.md) datastore contract — the comment is
  still the datastore, but there is now one reader-facing comment carrying all finder sessions. This is
  the load-bearing sub-change, not a rendering tweak.
- **Attribution is preserved, not anonymized.** Each surviving finding is tagged with its originating
  lens ("the Architect flagged the layering violation"). Dedup the *issue*, keep the *voices* — the
  why-this-lens-cares signal is the point of having personas.
- **Over-merge is the risk to guard.** "Same finding" across personas is semantic, not string equality
  (different words, lines, severities for one root cause). A reducer will sometimes melt two genuinely
  distinct issues into one and silently drop the second. Verified findings stay traceable to their
  origin so an over-merge is detectable; this is the primary thing the slice's tests target.
- **Reply-to-withdraw reroutes through the reducer.** With per-persona comments, replying under a
  persona's comment lets *that* persona withdraw. With one comment, a human reply is routed by the
  reducer back to the finder(s) whose finding it addresses, which then reconcile. A feature that was
  free now costs routing logic.
- **A collect-all barrier replaces failure isolation.** Independent comments let a slow/failing persona
  degrade to its own failure finding without blocking others; a unified comment must collect the panel
  before reducing. Mitigate by reducing the findings on hand and **naming the personas that did not
  report**, so one dead finder cannot sink the whole comment.
- **A new nondeterministic cost/failure point.** One extra model call over all findings per turn, whose
  failure fails the whole review. The reducer prompt is kept separate from the panel-construction prompt
  (Decision 3) even when the same model backs both — different jobs.
- **Further target (not this slice): inline, line-anchored placement.** The fuller Copilot-shaped end
  state is *one review with `N` inline line-anchored comments* — unified in provenance, distributed in
  placement — deduped and attributed. The single panel comment is the stepping stone; the inline
  evolution ties into the deferred line-anchored-comments feature and is out of scope here.

---

## Decision 7 — Ship the unified comment with DETERMINISTIC dedup, not a model reducer

**Amends Decision 6, which assumed a reducer stage.**

### Rationale

Decision 6 described the reducer as a model call that dedups and synthesizes, and named over-merge
as the risk to guard. Building it made the asymmetry decisive: an over-merge **silently deletes a
real finding**, and the reader cannot tell it happened - the same class of defect as reporting a
clean review. An under-merge is two similar bullets, which a reader can see and judge for
themselves. A model reducer trades an invisible failure for a visible one in the wrong direction.

So the shipped `FindingSynthesis` is pure and conservative: two findings collapse only when they
name the same file, the same line, and the same title once case and punctuation are normalised.
It cannot fuse "null deref in the parser" with "parser crashes on empty input" - and it cannot fuse
two genuinely distinct bugs either. It also needs no extra model call, no failure path, and is
exhaustively testable.

Semantic dedup remains worth having, but it needs a model call, an evaluation, and a way to show
the reader what was merged. It is a follow-up rather than part of this slice.

### Consequence

`FindingSynthesis.Merge` is a pure fold. Duplicate reports of the same finding collapse into one
entry carrying **every** lens that raised it, so attribution survives. The merged count is disclosed
in the comment alongside confidence suppressions and verification refutations.

---

## Decision 8 — Panel comment is opt-in, and inherits per-persona history on the switch

### Rationale

This relocates where session state lives, which is the highest-risk change in the epic: get it
wrong and a PR's review history is silently reset. Every other slice here landed additive and
default-off (`panel`, `verify`, `PG_JSON_MODE`), and the same reasoning applies more strongly.

The migration is the load-bearing half. A PR reviewed before the switch has no panel-state blob,
so the runner falls back to the per-persona comments for each session. Without that, flipping the
mode would restart every in-flight review at turn one and re-raise everything already resolved or
withdrawn.

### Consequence

`CommentMode` (`perPersona` default, `panel`) on the config. Session lookup is panel-blob first,
then the legacy per-persona comment - that ordering IS the migration. A persona that failed or was
skipped keeps its carried session and is **named** in the comment, because with one comment its
absence is otherwise invisible; in per-persona mode it had its own failure comment to speak from.

Not addressed here: the now-orphaned per-persona comments on a migrated PR are left in place rather
than deleted. Deleting a human-visible comment is destructive and reversible only by hand, so it
wants an explicit decision rather than a side effect of a mode flip.

