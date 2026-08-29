# Feature Spec: Repository review report

## Status
**Draft — future** (not scheduled; captured so the desktop-gui affordance has a home)

| Field        | Value        |
|--------------|--------------|
| Author       | Charles Lee  |
| Created      | 2026-07-02   |
| Last Updated | 2026-07-02   |

## Purpose

Review a whole repository (not just a single PR's diff) and emit the findings as a
**document artifact** — a markdown report — optionally opened as **its own pull request**
containing only the report. This is the third review action from the desktop GUI (after
subscribe and one-shot), and it is meant to interoperate with document-style review
skills/workflows rather than the inline-PR-comment model.

## Motivation

The existing review paths post inline, self-updating comments on a specific PR. Some
workflows want a different shape: a standalone review of the current codebase (a health
check, an onboarding audit, a pre-release sweep) captured as a durable document you can
read, diff, and commit — not comments scattered on a PR. Producing a `REVIEW.md`-style
artifact, and optionally a PR that adds just that file, makes Peanut Gallery a drop-in for
those document-review conventions.

## Affected layers

| Project / area | Change type |
|---|---|
| `PeanutGallery.Core` | a pure "report" rendering fold (findings → markdown document), separate from `CommentRenderer` |
| `PeanutGallery.Cli` | a new verb (e.g. `report`) that reviews a tree/target and writes the artifact |
| `PeanutGallery.Engine` | reuses the reviewer over a whole-tree input rather than a diff |
| Desktop shell | the "Review the whole repository → report" action (currently a `later` affordance in `../desktop-gui/mockups/repo.html`) |

## Requirements
- [ ] Review a repository/target and produce a markdown findings report (deterministic, renderable in the core).
- [ ] Write the artifact locally.
- [ ] Optionally open a pull request that adds only the report file (PR-shaped write, per [ADR-0002](../../adr/0002-review-executors-and-workflow-file-boundary.md)).
- [ ] Report format is compatible with popular document-style review skills.

## Core changes

A pure report renderer: `Findings` (+ per-persona sections, summary) → a markdown
document. Same finding values the comment path uses, a different projection. No IO.

## Out of scope

- Streaming/live progress UI for a whole-repo review (later).
- Chunking strategy for large repos (relates to `../large-diffs/spec.md`).

## Open questions
- [ ] **Artifact format / skill compatibility.** Target compatibility with the popular
  Matt Pocock review skill (and similar document-style review workflows). Need the skill's
  expected output shape — assumed to be a `REVIEW.md`-style markdown document rather than
  inline comments; confirm the exact structure (sections, severity encoding, file/line
  citation style) so the report renderer matches. — *Owner: Charles (needs a pointer to the skill)*
- [ ] Should the report PR live on a branch the tool manages, and update in place, or be a
  fresh PR each run? — *Owner: Charles*

## Related
| Type | Link |
|------|------|
| Docs index | [`docs/INDEX.md`](../../INDEX.md) |
| Executors ADR (PR-shaped writes) | [`adr/0002-review-executors-and-workflow-file-boundary.md`](../../adr/0002-review-executors-and-workflow-file-boundary.md) |
| Desktop GUI | [`../desktop-gui/spec.md`](../desktop-gui/spec.md) |
| Large diffs | [`../large-diffs/spec.md`](../large-diffs/spec.md) |
