# Feature Spec: Stateful PR review sessions

## Status
**Implemented.**

| Field        | Value        |
|--------------|--------------|
| Author       | Charles Lee  |
| Created      | 2026-06-25   |
| Last Updated | 2026-06-25   |

## Purpose

Make each reviewer **persistent across a PR's pushes**: it remembers what it already
flagged, comments on what changed since the last push, and confirms fixes — instead
of re-reviewing the whole diff cold every time. Faster, more thorough, cheaper.

## The key idea: session = persisted state, not a live process

The runner is ephemeral (a fresh job per push), so the "session" is **data we
persist and replay**, not a resident process. Each push: load the persona's saved
`ReviewSession`, send only the **delta since its last-reviewed SHA** plus its carried
summary + open findings, advance one turn, save the new state. No always-on infra.

Provider prompt-caching is a *bonus* speedup when pushes are close together (TTLs are
minutes); the durable win is the incremental conversation, which holds regardless of
cache state.

## Where state lives

Inside the persona's PR comment — the comment **is** the per-(PR, persona) datastore.
A base64-encoded `ReviewSession` (last SHA, turn, running summary, open findings) is
appended in a hidden `<!-- pg-state:1:… -->` blob after the visible review. No
external store; it dies with the PR. Extends the existing marker-based `CommentSync`.

## Affected layers

| Project / area              | Change type |
|-----------------------------|-------------|
| `PeanutGallery.Core`        | new pure `ReviewSession`/`SessionUpdate`, `SessionUpdateParser`, `SessionCodec`, `SessionPlanner` (Mealy step), `SessionCommentRenderer`; `FindingsParser`/`CommentRenderer` refactored to share helpers |
| `PeanutGallery.Engine`      | `IReviewer.CompleteAsync` (raw model call, throws on failure) on `ChatClientReviewer` + `StubReviewer` |
| `PeanutGallery.Cli`         | `review-pr` is now session-stateful; `GitHubClient` gains PR head/base + compare-diff |

> ADR-0001 held: the session logic is a pure Mealy core (`SessionPlanner.Advance` +
> `SessionUpdateParser`/`SessionCodec`); the shell owns the clock, GitHub IO, and the
> model call.

## Flow per push (`synchronize`)

1. Get the PR head SHA; read each persona's prior session from its comment.
2. If a persona already reviewed this exact head → skip (nothing new).
3. Diff = full PR diff on turn 1, else `compare(lastSHA...head)` (fall back to the
   full PR diff on a force-push).
4. `SessionPlanner.Advance(prior, delta)` → model call → `SessionUpdateParser` →
   new session + a living comment (current findings + "resolved since last push").
5. On a model/provider failure: render a failure note but **do not advance** the
   session, so the next push retries from the same SHA.
6. Embed the new state in the comment and upsert in place.

## Tests

Core: codec round-trip (incl. null SHA / no-state), update parser, planner first-vs-
continued + agent tool note, living-comment render (resolved only from turn 2).
Offline `review-pr --diff … --dry-run` exercises the wiring.

## Out of scope / follow-ups

- Explicit provider `cache_control` markers (Anthropic) / cache-aware prompt ordering
  to realize prompt-cache savings on rapid pushes — provider-specific, via
  `ChatOptions.AdditionalProperties`.
- A periodic full re-review every K turns as a safety net against incremental drift.
- Persisted agent-tier repo index for cross-push warmth.

## Related
| Type | Link |
|------|------|
| Docs index | [`docs/INDEX.md`](../../INDEX.md) |
| PR review shell | [`docs/feature-specs/github-pr-review/spec.md`](../github-pr-review/spec.md) |
| Founding ADR | [`docs/adr/0001-functional-core-multi-shell.md`](../../adr/0001-functional-core-multi-shell.md) |
