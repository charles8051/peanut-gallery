# Architecture Decision Records

Numbered, cross-cutting decisions for Peanut Gallery — the horizontal choices no
single feature owns and many parts of the tool cite.

One immutable, status-lifecycled decision per file: `NNNN-<slug>.md`, status
`Proposed` → `Accepted` → `Superseded by ADR-XXXX`. The number is a stable citation
handle (`see ADR-0001`); **assign it at merge, not at authoring** (draft under a
slug, number it when it lands) so parallel branches never collide.

Decisions that belong to a single feature live with that feature instead, under
`docs/feature-specs/<slug>/adr.md` as internally numbered `Decision 1..N`.

## Index

- [ADR-0001](0001-functional-core-multi-shell.md) — One pure core, projected to
  multiple shells: all review logic is pure and total; the CLI, headless server,
  and Native-AOT Avalonia desktop GUI are thin shells over the identical fold.
  **Accepted.**
- [ADR-0002](0002-review-executors-and-workflow-file-boundary.md) — Where a review
  runs is a choice layered on an executor-agnostic config, and no shell edits a
  consumer repo's `.github/` files behind the author's back. **Accepted.**
- [ADR-0003](0003-prompt-channel-trust-boundary.md) — The prompt channel is the
  trust boundary: operator text takes the system message, everything derived from
  the reviewed change takes the user turn, and a delimiter is not a substitute.
  **Accepted.**
