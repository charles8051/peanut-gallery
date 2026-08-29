# Feature Spec: Per-PR opt-out

## Status
**Implemented.**

| Field        | Value        |
|--------------|--------------|
| Author       | Charles Lee  |
| Created      | 2026-06-26   |

## Purpose

Let an author skip review on a *specific* PR without editing the workflow or removing
personas. Fills the per-PR gap (per-repo = delete the workflow; per-file = the diff
filter `ignoreGlobs`).

## How it works

`review-pr` checks the PR **before any model call** and skips (posts nothing, exits 0)
when any of these match:
- a skip **label** is present — default `peanut-gallery: skip` or `no-review`;
- a **marker** appears in the PR **title or body** — default `[skip-review]` or
  `[no-peanut-gallery]`;
- the PR is a **draft** and `drafts` is enabled (default **off** — drafts are reviewed).

Enforced **in the action** (not the consumer workflow), so it works on every repo via
`@main` and applies to both push- and comment-triggered runs. Pure `SkipPolicy.Evaluate`
in Core; the shell reads the PR (labels / title / body / draft) and exits early.

A label is the recommended primary control: **add it to pause**, **remove it to resume**
(review resumes on the next push or comment) — no commit needed.

Config (`peanut.json`, all optional; defaults apply when omitted):
```json
"skip": { "labels": ["peanut-gallery: skip"], "markers": ["[skip-review]"], "drafts": false }
```

## Affected layers

| Project / area              | Change |
|-----------------------------|--------|
| `PeanutGallery.Core`        | `SkipPolicy` + `PullRequestMeta`; `PeanutConfig.Skip` |
| `PeanutGallery.Cli`         | `GitHubClient` returns labels/title/body/draft; `review-pr` evaluates skip and exits early |

## Tests

Core: label (case-insensitive), title/body marker, draft on/off, no-signal, custom policy.

## Related
| Type | Link |
|------|------|
| Docs index | [`docs/INDEX.md`](../../INDEX.md) |
| Large-diff filter (per-file) | [`docs/feature-specs/large-diffs/spec.md`](../large-diffs/spec.md) |
