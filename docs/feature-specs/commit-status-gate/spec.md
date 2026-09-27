# Feature Spec: Commit-status gate

## Status
**Implemented**

| Field        | Value        |
|--------------|--------------|
| Created      | 2026-09-27   |
| Last Updated | 2026-09-27   |

## Purpose

GitHub auto-merge waits on required status checks and required approving reviews. It never
reads comments, so an advisory panel comment cannot hold a merge. This feature posts a
`peanut-gallery` commit status on the PR head that a repository can make a required check.
Auto-merge then waits until the panel has reported the head commit with nothing open.

## Affected layers

| Project / area              | Change type |
|-----------------------------|-------------|
| `PeanutGallery.Core`        | `ReviewGate`, `CommitStatus`, `CommitState`; `ReviewVerdict` and `PanelReadiness.Verdict` |
| `PeanutGallery.Cli`         | `review-pr` posts the status; `await-review` exits on `PanelReadiness.Verdict`; `GitHubClient.SetCommitStatusAsync` |
| `PeanutGallery.Engine`      | none |
| Action (`action.yml`)       | `commit-status` input, mapped to `PG_COMMIT_STATUS` |

## Requirements

- [x] Off by default. `commit-status: 'true'` (or `PG_COMMIT_STATUS=1`) turns it on.
- [x] `pending` before any model call, so a required check holds while the review runs.
- [x] After the panel comment is posted: `success` only for `ReviewVerdict.Clean`, `failure`
      for open findings, `error` for a partial panel or one that did not reach the head.
- [x] Updated by `issue_comment` runs, so a finding withdrawn in conversation clears it.
- [x] A skip label or marker posts `success`. A skipped draft posts `pending`.
- [x] A fork refusal and a superseded run post nothing.
- [x] `await-review` and the gate derive their outcome from the same `ReviewVerdict`.

## Why a status and not the job's check run

The review job's own check run belongs to the event that started it. An `issue_comment` run
is attached to the default branch, so its check never appears on the PR head. If the gate
were the `pull_request` job's exit code, a withdrawal made in conversation could not turn it
green. A commit status is written against a SHA, so either trigger can set it.

## States

| When | State | Description |
|---|---|---|
| Review starts | `pending` | `Reviewing <sha>` |
| Whole panel reported, board empty | `success` | `No findings at <sha>` |
| Board carries findings | `failure` | `Findings to address at <sha>` |
| Board empty, a reviewer did not report | `error` | `Partial review at <sha>: …` |
| No reviewer reached the head | `error` | `No reviewer reported <sha>` |
| Config uses `perPersona` comments | `error` | asks for `"comment": "panel"` |
| Skip label or marker | `success` | `Review skipped: <reason>` |
| Skipped while draft | `pending` | settles when the PR is marked ready |

A draft stays `pending` because marking it ready fires a review of the same SHA. A `success`
left over from the draft would let auto-merge go before that review posted anything.

Only panel mode can be judged. Per-persona mode has no single comment that speaks for the
whole panel.

## Core changes

`ReviewGate.After(CommentMode, panelBody, headSha)` reads the panel body the run itself
rendered, through `PanelReadiness.Read(...).Verdict`. The run's own body is used rather than
the thread, because any commenter can post a body carrying the panel marker.

## Shell changes

`review-pr` calls `SetCommitStatusAsync` at three points: the skip exit, after the
supersession check, and after the closing `PostAsync`. A throw between the pending post and
the verdict leaves the status `pending`, which holds the merge. A 403 names the missing
`statuses: write` permission.

## Setup

1. `commit-status: 'true'` on the action step.
2. `statuses: write` in the workflow's `permissions`.
3. `"comment": "panel"` in the config (the bundled default already has it).
4. A `concurrency` group keyed on the PR number, covering both triggers.
5. Add `peanut-gallery` to the branch's required status checks.

Every run writes the same context on the same head SHA, and GitHub keeps the latest status
per context. Two overlapping runs could land out of order, with an older run's verdict
posted after a newer run's `pending`. The concurrency group rules that out by running one
review per PR at a time. The panel comment already needs that to keep a single writer, so
the gate adds no requirement the comment did not have. The gate does not order statuses
itself.

`action.yml` pins a prebuilt image, and the pin can only name a build of an earlier commit.
The commit that adds the input therefore pins an image that ignores it. The input takes
effect from the pin bump after it. Until then a required `peanut-gallery` context never
appears and the merge stays blocked.

## Out of scope

- Removing a skip label does not trigger a run, so a skip's `success` stands until the next
  push or comment.
- A job killed by its `timeout-minutes` leaves the status `pending`. Re-running the job
  settles it.
- Fork PRs get no status: their token cannot write one, and they are not reviewed.

## Related
| Type | Link |
|------|------|
| Docs index | [`docs/INDEX.md`](../../INDEX.md) |
| The waiter that shares the verdict | [`await-review/spec.md`](../await-review/spec.md) |
| Degraded panels | [`degraded-panel-visibility/spec.md`](../degraded-panel-visibility/spec.md) |
