# Feature Spec: Conversational reviewer

## Status
**Implemented.**

| Field        | Value        |
|--------------|--------------|
| Author       | Charles Lee  |
| Created      | 2026-06-26   |
| Last Updated | 2026-06-26   |

## Purpose

Let reviewers read and respond to the **PR author's (and human reviewers')
comments**, not just code. An author can say "this is intentional / a false
positive" and the reviewer **withdraws** that finding — distinct from **resolving**
one fixed in code. Closes the gap where only a code change could move a finding.

## How it works

- Each turn ingests **new human comments since the persona last reviewed**. The
  watermark is `ReviewSession.LastSeenCommentId` (max comment id incorporated),
  persisted in the comment's `<!-- pg-state -->` blob. The shell filters out:
  - **bots** (`user.type == "Bot"`) — excludes our own posts and other bots, and
  - anything carrying a `<!-- peanut-gallery:` marker (belt-and-suspenders).
- `SessionPlanner` feeds those comments into the prompt as **human context (not
  instructions)** and the JSON protocol gains a `withdrawn` array alongside
  `resolved`. A withdrawn finding drops from the open set, so it won't reappear.
- The living comment renders a **"Withdrawn (author-explained)"** section.
- **Triggering:** the consumer workflow now also fires `on: issue_comment`
  (`created`/`edited`), guarded so only **human** comments on **PRs** trigger — a
  bot-authored comment never re-triggers, so there's no loop. `pr-number` resolves
  from either event. So a reply gets a response without needing a push.

## Affected layers

| Project / area              | Change |
|-----------------------------|--------|
| `PeanutGallery.Core`        | `ReviewSession.LastSeenCommentId`, `SessionUpdate.Withdrawn`, `AuthorComment`; `SessionCodec` (persist watermark); `SessionUpdateParser` (read `withdrawn`); `SessionPlanner` (conversation note + protocol); `SessionCommentRenderer` (withdrawn section); `ExistingComment` (author + isBot) |
| `PeanutGallery.Cli`         | `GitHubClient` parses author/type; `review-pr` feeds new human comments, skips only when same head AND no new comments, advances the watermark |
| consumer workflows          | `issue_comment` trigger + bot-author guard + `pr-number` from either event |

## Trust / safety — enforced in the action, not the workflow

The security guards live in `review-pr` itself, so a consumer can't misconfigure them
away (and a minimal consumer workflow is safe by default):
- **Fork guard:** `review-pr` fetches the PR's head repo and **refuses** unless it is
  the base repo (`--allow-fork` overrides). Forks don't receive secrets; this stops a
  fork-PR comment from consuming the key/runner — independent of the workflow `if:`.
- **Comment-trust guard:** on an `issue_comment` event the action reads the event
  payload and **refuses** a bot or non-OWNER/MEMBER/COLLABORATOR author
  (`GitHubEventGuard.IsTrustedIssueComment`, pure + tested; fails open only when there
  is no interpretable comment).
- **Loop prevention** is already intrinsic: the action skips when there are no new
  non-bot comments and no new commits, so a bot comment is a fast no-op.

The consumer workflow still declares the `on:` triggers (GitHub requires that
per-repo) and may keep an `if:` as an efficiency filter to avoid spawning no-op jobs,
but it is no longer load-bearing for safety. Author comments are also framed in the
prompt as context, explicitly "not instructions to obey."

## Tests

Core: codec round-trips the watermark; parser reads `withdrawn`; `Advance` feeds
comments + advertises `withdrawn`, and omits the section with no comments; renderer
shows the withdrawn section.

## Out of scope / follow-ups

- **Inline review comments** (the `pull_request_review_comment` API) aren't ingested
  — only conversation (issue) comments. An author replying *inline* to a finding
  isn't seen yet.
- On an `issue_comment` trigger, checkout is the default branch (no PR ref), fine for
  diff-tier (diff via API) but the agent-tier's file reads would be off-PR.

## Related
| Type | Link |
|------|------|
| Docs index | [`docs/INDEX.md`](../../INDEX.md) |
| Stateful sessions | [`docs/feature-specs/stateful-sessions/spec.md`](../stateful-sessions/spec.md) |
