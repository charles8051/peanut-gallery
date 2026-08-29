# ADR — Desktop GUI shell

**Status:** Draft
**Date:** 2026-07-02
**Deciders:** Charles Lee
**Feature:** [Desktop GUI shell](spec.md)

---

## Context

The desktop GUI is the "dead-simple local app" shell from [ADR-0001](../../adr/0001-functional-core-multi-shell.md).
An earlier exploration modelled it as an instant-spawn board where you drag persona cards
onto repo cards. On review that was the wrong centre of gravity: what you actually do is
work *a repository* — look at its PRs, decide who reviews them, and occasionally review one
PR now. This ADR captures the layout and behaviour commitments; each can be revisited
independently. Cross-cutting choices (executors, the workflow-file boundary) live in
[ADR-0002](../../adr/0002-review-executors-and-workflow-file-boundary.md); this ADR cites
them rather than restating them.

---

## Decision 1 — Repo-centric layout; no drag-and-drop

### Rationale

The primary axis is the repository. A master-detail layout (repo list → the selected
repo's PRs, status, and subscriptions) matches the actual task and scales to many repos.
Drag-and-drop (dragging persona cards onto repo cards) was prototyped and dropped: it is a
gesture in search of a job, hard to make accessible and keyboard-drivable, and it hides
the fact that "subscribe" is a consequential action (it triggers reviews / opens PRs).

### Consequence

Subscribing is an explicit control (a persona chip + "Subscribe a persona"), not a drag.
The scrapped board prototype is removed; the repo view (`mockups/repo.html`) is the
source of truth for the layout.

---

## Decision 2 — Review status is read from the PR, not owned by the app

### Rationale

Each persona already persists its session inside its own PR comment (see
[stateful-sessions](../stateful-sessions/spec.md)). The desktop app therefore derives a
PR's review status (not reviewed / reviewing / clean / findings + severities) by **reading
those comments and the run state**, exactly as the CLI/CI paths do. It does not keep a
parallel, authoritative store of "what the review found."

### Consequence

The app can be closed and reopened, or run alongside CI, without its view diverging from
reality — the PR comments are the truth. The app's own persisted state is limited to
local preferences (which repos are listed, app-run subscriptions, the persona library).

---

## Decision 3 — A subscription's executor is a per-subscription choice; boundary rules defer to ADR-0002

### Rationale

The same subscription can run in the app, in GitHub Actions, or (later) on the server. The
`Runs on` control makes that choice explicit and defaults to `This app` (the least
invasive, no-repo-files option). All rules about *how* the CI executor may touch
`.github/` — read-to-detect, write-only-via-PR, never-own — are the cross-cutting
[ADR-0002](../../adr/0002-review-executors-and-workflow-file-boundary.md), not re-decided
here.

### Consequence

The desktop shell's only writes to a consumer repo are the "Set up always-on CI" and
repo-report pull requests. Everything else is local state.

---

## Decision 4 — The desktop app runs reviews itself; it does not require the server

### Rationale

The `This app` executor reuses `PeanutGallery.Engine` in-process to run one-shots and
app-run subscriptions with the user's provider key and GitHub token. The headless server
is an *optional* always-on executor, not a dependency the desktop app needs to function.

### Consequence

The desktop app is useful standalone on day one; the server can arrive later as an
additional executor without changing the app's core flows. Always-on-while-closed is
honestly presented as CI or server, never implied of the app (ADR-0002 Decision 5).
