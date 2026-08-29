# ADR 0002: Review executors and the workflow-file boundary

## Status

Accepted. Cited by the desktop-gui, persona-management, and repo-report feature specs.
Extends [ADR-0001](0001-functional-core-multi-shell.md) (one pure core, many shells) to
the question of *where a review runs* and *how a shell may touch a consumer repo's files*.

## Context

A "review this repo's PRs with these personas" intent can be carried out from several
places: the CLI at a workstation, a GitHub Actions workflow committed to the repo, a
headless server, and (on the roadmap) the desktop GUI. These are not interchangeable —
they differ in who owns the schedule, whose credentials post the comments, whether they
run when your machine is off, and, critically, **whether they require files committed to
the consumer repo**.

Only the GitHub Actions path needs anything in the repo: a `peanut-gallery.yml` workflow
plus a committed `.github/peanut-gallery.json` panel. That coupling creates a hazard for
any shell — especially a desktop GUI — that wants to "manage subscriptions": the
temptation to silently edit `.github/` in someone's repository. That is invasive
(surprise diffs, merge conflicts, drift between the app's view and the committed reality),
assumes push access, and turns the GUI into a hidden dependency of the repo's CI.

This ADR fixes the model so every shell treats those files the same way.

## Decision 1 — The config value is executor-agnostic; the executor is a separate choice

`ReviewPlanner.Plan` and the `personas` / `providers` / `repos` / `assignments` values
(the core config, per ADR-0001) say **what** a review is. They say nothing about **who
runs it**. The executor is a property layered on top by a shell:

| Executor | Runs on | Always-on | Touches consumer repo files |
|---|---|---|---|
| **CLI / this app** | your machine, while invoked/open | no | no |
| **GitHub Actions** | GitHub's runners, on PR events | yes | **yes** (`peanut-gallery.yml` + committed config) |
| **Headless server** (roadmap) | your VM, webhook/poll | yes | no |

**Rationale.** The same `Assignment` value drives all three, so a subscription can move
between executors without changing what a review *is* — the core stays the single source
of truth. **Consequence.** A shell never needs to fork "what a review is" per executor; it
only decides where to run the shared value.

## Decision 2 — Only the GitHub Actions executor involves `.github/`

The CLI, this app, and the headless server hold their subscriptions in their own state
(config file, app store, server DB) and post via the GitHub API with a token. They write
**zero** files to the consumer repo. The GitHub Actions executor is the *only* one whose
subscription is expressed as committed repo files.

**Consequence.** "Manage subscriptions" is a repo-file operation **only** for the CI
executor; for every other executor it is purely local state.

## Decision 3 — A shell reads workflow files freely, writes them only via an explicit pull request, and never owns them continuously

For the CI executor specifically, a shell's relationship to `peanut-gallery.yml` /
`.github/peanut-gallery.json` is bounded to three moves:

- **Read to detect.** A shell may read those files to determine whether CI review is set
  up and to treat the committed config as the source of truth for that repo's CI
  subscription (e.g. the desktop GUI's per-repo `CI` badge). Reads are ambient and safe.
- **Write only via an explicit PR.** Enabling or changing CI review generates the workflow
  + config and **opens a pull request the user reviews and merges**. There is no silent or
  background write to `.github/`.
- **Never own continuously.** A shell does not keep rewriting the workflow to mirror its
  own state. If the committed config drifts from the shell's view, the shell surfaces the
  difference; it does not auto-reconcile.

**Rationale.** Workflow files are team-owned infrastructure-as-code; a review tool that
mutates them behind the user's back is a footgun and a trust violation. A one-shot,
PR-shaped write removes the enrollment friction (secret + runner + workflow) without
taking ownership. **Consequence.** The generated workflow is self-contained and the
repo's from the moment the PR merges; the shell can be uninstalled without breaking CI.

## Decision 4 — Promotion between scopes/executors is the same PR-shaped write

Moving a review "up" toward the repo — an app-run subscription becoming a committed CI
one, or a personal-library persona becoming a repo-committed one (see
[persona-management](../feature-specs/persona-management/spec.md)) — is generated from the
same config value and delivered as a pull request. Moving "down" (repo → local) is a free
local copy.

**Consequence.** There is exactly one way anything reaches a consumer repo: a pull request
the user approves. Everything below the repo boundary is local and reversible.

## Decision 5 — Always-on belongs to CI or the server, never the desktop app alone

The desktop app executor reviews only while the app is open. True always-on, team-wide
auto-review is the GitHub Actions executor (or the headless server for a personal
always-on). A shell must present the app executor honestly ("runs while open") and offer
CI/server as the always-on path, rather than implying the desktop app keeps watching after
it is closed.

## Consequences (summary)

- The desktop GUI's `Runs on` control and its "Set up always-on CI (opens a PR)" action
  are the visible form of Decisions 1–3.
- No shell scans or mutates a consumer repo except to open a PR.
- CI remains self-hosting after the shell that bootstrapped it is gone.
