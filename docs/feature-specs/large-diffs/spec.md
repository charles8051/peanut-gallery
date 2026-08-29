# Feature Spec: Large-diff handling

## Status
**Phase 1 implemented** (relevance filter + size cap). Phase 2 (chunking) deferred.

| Field        | Value        |
|--------------|--------------|
| Author       | Charles Lee  |
| Created      | 2026-06-26   |
| Last Updated | 2026-06-26   |

## Purpose

Keep very large PRs reviewable: bound the diff sent to each model so big changes
don't blow latency, cost, the context window, or review quality. Surfaced by a
146-file / ~4,200-line rename PR that first stalled, then (after the
timeout fix) ran but slowly.

## Phase 1 — relevance filter + size cap (this slice)

Pure-core `Diff` transforms; no extra model calls.

- **Diff model** now captures each file's raw `Segment` plus `IsBinary` /
  `IsRenameOnly` flags, so a filtered diff can be rebuilt from a subset.
- **`DiffFilter.Apply(diff, policy)`** drops low-signal files — binary, rename-only,
  and anything matching the ignore globs — then enforces a byte budget by omitting
  the **largest** remaining files first (maximizing files reviewed). Returns the
  trimmed `Diff` + the list of `OmittedFile`s (path + reason).
- **Disclosure:** `SessionPlanner` appends a note to the prompt listing the omitted
  files so the model knows its view is partial (never a silent truncation). The CLI
  logs `[persona] reviewing … (N file(s), M omitted)`.
- **Config** (`peanut.json` `filter` block, all optional):
  ```json
  "filter": { "ignoreGlobs": ["*.lock", "**/obj/**", "*.min.js", ...], "maxBytes": 131072 }
  ```
  Omit it to use the defaults (`DiffFilterPolicy.Default`): lockfiles,
  `obj/`/`bin/`/`node_modules`/`dist`/`vendor`, `*.min.*`, `*.Designer.cs`/`*.g.cs`/
  `*.generated.cs`, and a 128 KB budget.

Applied on every turn (the incremental delta is usually small; this bites mainly on
the first-turn full diff or a large push).

## Affected layers

| Project / area              | Change |
|-----------------------------|--------|
| `PeanutGallery.Core`        | `Diff`/`DiffFile` (segments + flags), `DiffFilter`, `DiffFilterPolicy`, `OmittedFile`, `FilteredDiff`, `Glob`; `SessionPlanner` omission note; `PeanutConfig.Filter` |
| `PeanutGallery.Cli`         | `review-pr` / `review` apply the filter; progress logs omissions |

## Tests

Core: parse flags (binary/rename-only/segment), drop low-signal + keep substantive,
size-cap omits largest, under-budget keeps all, gitignore-style glob matching.

## Out of scope / follow-ups (issues filed)

- **Phase 2 — chunking (map-reduce):** split a large *substantive* diff into
  size-budgeted chunks, review each, dedupe-merge findings. For PRs where even the
  filtered diff overflows.
- **Agent-pull:** for `agent`-tier personas, hand the changed-file list + tools
  instead of the diff, so the agent reads only what it needs.

## Related
| Type | Link |
|------|------|
| Docs index | [`docs/INDEX.md`](../../INDEX.md) |
| Stateful sessions | [`docs/feature-specs/stateful-sessions/spec.md`](../stateful-sessions/spec.md) |
