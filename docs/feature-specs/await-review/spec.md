# Feature Spec: `await-review` — blocking on this push's review

## Status
**Implemented**

| Field        | Value        |
|--------------|--------------|
| Author       | Charles Lee  |
| Created      | 2026-08-25   |
| Last Updated | 2026-08-25   |

## Purpose

`CLAUDE.md` told every agent working in this repo to wait for the automated review
and gave them nothing to wait *with*. Across roughly seven PRs in one session, every
agent read the line and closed its turn with a variant of "the automated review lands
in a few minutes; I have not waited" — and the findings sat unaddressed (#181 reached
five panel turns with open findings nobody had answered). An instruction that names an
outcome and supplies no mechanism is not an instruction. `await-review` is the
mechanism: it blocks until this push's review has landed, prints what it found, and
exits with a code the caller can branch on.

## Affected layers

| Project / area              | Change type |
|-----------------------------|-------------|
| `PeanutGallery.Core`        | New pure value + fold: `PanelReadiness.Read`, `Sha.SameCommit`, `PanelCommentRenderer.DegradedCount` / `ReportsNoFindings`, `PanelSessionCodec.Visible` |
| `PeanutGallery.Cli`         | New verb `await-review`; `GitHubClient.ListCheckRunsAsync` |
| `PeanutGallery.Engine`      | none |
| Server shell (roadmap)      | none |
| Desktop shell (roadmap)     | none |

> The polling loop, the clock, the HTTP and the timeout are shell concerns and live in
> the CLI. The core received exactly one decision — *is this panel comment this
> commit's?* — plus the readers for markers whose writers it already owned.

## Requirements

- [x] Wait for the `review` status check to **appear** before waiting for it to conclude.
- [x] Verify the panel comment is **fresh for the head SHA** before reporting it.
- [x] Decide freshness from the embedded `pg-panel-state` blob, not from rendered prose.
- [x] Distinguish four outcomes by exit code: findings, clean, timed out, review did not happen.
- [x] Findings on stdout, progress on stderr, so the useful output stays pipeable.
- [x] Read-only: post nothing to the PR.
- [x] Never report a degraded or reviewer-less panel as a clean review.

## The two failure modes it exists to avoid

**A check that does not exist yet has not passed.** `autoreview.yml`'s job id is
`review` and it surfaces as a status check, but immediately after `gh pr create` the
check run has not been registered. Anything that asks "has it concluded?" gets a
confident, wrong answer in under a second. The wait therefore has two phases —
appearance, then conclusion — and `Conclusion()` returns null for both "no check yet"
and "still running", because neither is a verdict. The documented manual fallback has
the same edge from the other side: `gh pr checks <n> --watch` *errors* rather than
waits when no checks exist, and exits non-zero when **any** check fails, which is a
different event from `review` concluding.

**The panel comment is upserted in place.** The previous turn's findings sit on the PR
for the entire time the new review runs, so "is there a panel comment?" answers yes
instantly and hands back a board that was already addressed. Freshness is decided
against the head SHA.

## Core changes

`PanelReadiness.Read(commentBodies, headSha)` — total, IO-free, AOT-clean. It finds the
comment carrying the panel marker, reads its `pg-panel-state` blob through
`PanelSessionCodec`, and classifies:

| `PanelArrival` | Meaning | `Landed` | `Settled` | `Complete` |
|---|---|---|---|---|
| `Absent` | No panel comment on the PR | no | no | no |
| `Unreadable` | A panel comment, blob missing or malformed | no | no | no |
| `NoReviewers` | A published panel whose blob carries no reviewer — a whole-panel outage | no | **yes** | no |
| `Stale` | Every reviewer's last SHA is an older commit | no | no | no |
| `Partial` | Some reviewers reported this commit, some did not | yes | yes | no |
| `Fresh` | Every reviewer carrying a session reported this commit | yes | yes | iff not degraded |

Three distinctions the table exists to make:

- **`Unreadable` is not `Absent`.** A panel comment whose blob will not parse is not
  "no review yet"; a caller told `Absent` waits out the timeout for a comment already
  on the PR.
- **`NoReviewers` is not `Fresh`.** Zero reviewers at head out of zero reviewers is not
  agreement. It is `Settled` — nothing advances out of an outage — so the waiter stops
  rather than sitting out its timeout, but it never counts as landed.
- **`Partial` counts as landed.** A reviewer whose turn failed does not advance its
  session, so it stays on the old SHA forever. Once the check has concluded, waiting
  for it to catch up waits forever.

Freshness compares SHAs with `Sha.SameCommit`, which tolerates an abbreviation on
either side (7 characters minimum) so a SHA from `git rev-parse --short` is not read as
a mismatch.

`HasFindings` is read from the **rendered board**, via
`PanelCommentRenderer.ReportsNoFindings` — a whole-line match, placed beside the
renderer that writes the line. Not from the blob's open findings: the blob deliberately
keeps every finding the model raised, including the ones the confidence gate suppressed
and the adversarial pass refuted, and those are exactly the ones the author is not being
asked to answer. Not a substring match either — the panel reviewing this feature raised
a finding whose body quoted `_No findings._`, and a `Contains` read that five-finding
panel as clean.

The whole-line match rests on an invariant the renderer now holds and a test pins:
**no authored text ever begins a line of a rendered panel.** Every single-line authored
fragment — title, file, lens, persona name, model, non-reporting reason, resolved and
withdrawn titles — goes through `CommentRenderer.OneLine`, and the one field allowed to
be several lines, a finding body, has *every* line of it re-indented under its bullet.
Titles were the hole: they were appended raw, so a model-authored newline could open a
bold span on one line and close it on another (the defect `OneLine` was written for) and,
incidentally, plant a sentinel at column 0.

`Degraded` comes from the hidden `pg-degraded:N` marker (#130), whose doc comment says
it exists for "a merge-gate polling consumer". This is that consumer, so
`DegradedCount` now lives beside `DegradedMarker` rather than as a regex downstream.
It catches what the per-persona counts cannot: a reviewer that never reported carries
no session at all.

## Shell changes

`Commands.AwaitReviewAsync` owns the loop, the clock and the HTTP:

```
peanut-gallery await-review --pr <n> [--slug owner/name] [--token <t>]
                            [--timeout <s>] [--interval <s>] [--check <name>] [--sha <sha>]
```

Head SHA comes from the PR unless `--sha` pins it. Each tick lists the commit's check
runs (`GitHubClient.ListCheckRunsAsync`) and filters to `--check` (default `review`).
Several runs can share a name — a re-run adds one — so a failure among them wins. Once
a verdict exists, the PR's comments are read and `PanelReadiness` decides whether to
report or keep waiting. On timeout the head is re-read: a push that landed mid-wait
supersedes the run being watched (the action early-exits green on supersession), and a
bare "timed out" would send the caller into the same doomed wait again.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | The whole panel reported and found nothing |
| `1` | Usage or API error (as every other verb) |
| `2` | The review landed with findings |
| `3` | Timed out, or the job succeeded without ever publishing a panel for this SHA |
| `4` | The review did not happen: the job failed, or the panel lost a reviewer |

`4` covering degradation is deliberate. An empty board is only a clean review if the
whole panel was there to fill it; a reviewer that timed out found nothing in the same
sense that a closed eye sees nothing. Findings win over degradation for the code, since
the caller's next action is to address them, and the degradation is named on stderr.

## Config / contract changes

None. No `peanut.json` fields, no comment-format change. `NoFindingsLine`,
`ReportsNoFindings` and `DegradedCount` widen `PanelCommentRenderer`'s public surface
so its rendered contract has one writer and one reader.

## Out of scope

- Posting anything to the PR. `await-review` is read-only; replying to a finding is the
  author's job, by fix or by refutation.
- A general-purpose waiter. This is one polling loop with a timeout — no retry policy,
  no backoff schedule, no pluggable predicate.
- Blocking a merge. That is `PG_FAIL_ON_DEGRADED` (#130) and the branch protection rules,
  not a CLI a human runs.

## Related

| Type | Link |
|------|------|
| Docs index | [`docs/INDEX.md`](../../INDEX.md) |
| Founding ADR | [`docs/adr/0001-functional-core-multi-shell.md`](../../adr/0001-functional-core-multi-shell.md) |
| The degradation signal it reads | [`../degraded-panel-visibility/spec.md`](../degraded-panel-visibility/spec.md) |
| The state blob it reads | [`../stateful-sessions/spec.md`](../stateful-sessions/spec.md) |
| The workflow it waits on | [`../github-pr-review/spec.md`](../github-pr-review/spec.md) |
