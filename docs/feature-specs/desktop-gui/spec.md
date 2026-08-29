# Feature Spec: Desktop GUI shell

## Status
**Draft**

| Field        | Value        |
|--------------|--------------|
| Author       | Charles Lee  |
| Created      | 2026-07-02   |
| Last Updated | 2026-07-02   |

## Purpose

A dead-simple local desktop app (`PeanutGallery.Desktop`) for driving reviews without
GitHub Actions: pick a repository, see its open pull requests and their review status,
subscribe personas to auto-review its PRs, and fire one-shot reviews on demand. It is a
thin shell over the same core config the CLI and (future) server consume — it edits
`personas` / `providers` / `repos` / `assignments` values and surfaces review status; it
holds no review logic of its own.

## Prototype

Durable, self-contained HTML mockups are the visual source of truth:
[`mockups/repo.html`](mockups/repo.html) (the repository view) and
[`mockups/README.md`](mockups/README.md) (design language + screen inventory). Open the
HTML directly in a browser — no build, no dependencies.

## Affected layers

| Project / area | Change type |
|---|---|
| `PeanutGallery.Core` | NONE — the shell consumes `ReviewPlanner.Plan` and the config values; no new core logic |
| `PeanutGallery.Cli` | none (the desktop app calls the same engine/API paths the CLI uses) |
| `PeanutGallery.Engine` | none new (reuses `ChatClientReviewer` for app-run reviews) |
| Desktop shell (new) | `PeanutGallery.Desktop` — Avalonia code-only, Native AOT; repo view, subscriptions, one-shot, status polling |
| GitHub Actions files | read-only detection + PR-shaped writes only, per [ADR-0002](../../adr/0002-review-executors-and-workflow-file-boundary.md) |

> Reminder (ADR-0001): new logic belongs in the pure core; the desktop shell consumes the
> fold, it does not re-implement it. IO / clock / `Task` / model client stay in the shell.

## Layout: repo-centric

The primary axis is the **repository**, not the persona. A repo list (left) selects a
repo; the main pane shows that repo's PRs, review status, and how it is reviewed. There is
deliberately **no drag-and-drop** — assignment is an explicit action, not a gesture (see
[`adr.md`](adr.md) Decision 1).

Repo view regions (see the prototype):
- **Repo list** — name, subscribed-persona count, open-PR count, a status pip, and a `CI`
  badge where a committed workflow is detected. Personas / Providers / Settings nav below.
- **Subscription card** — the personas auto-reviewing every PR, a `Runs on` executor
  control, and the "Set up always-on CI" affordance (see [Review actions](#review-actions)).
- **Open PRs list** — per PR: number, title, author · branch · updated, and a review-status
  cell (not reviewed · reviewing-with-progress · clean · findings-with-severity), plus a
  per-row action (`View` / `Review now`).
- **Repo report** — a later, secondary affordance; specced separately in
  [`../repo-report/spec.md`](../repo-report/spec.md).

## Review actions

Three distinct actions, each mapping to an executor (per [ADR-0002](../../adr/0002-review-executors-and-workflow-file-boundary.md)):

1. **Subscribe** one or more personas to a repo → every open and future PR is
   auto-reviewed. The subscription's **executor** is chosen via the `Runs on` control:
   *This app* (runs while open, local state) or *GitHub Actions* (always-on; the only
   executor that commits repo files — and only via the "Set up always-on CI" PR).
2. **One-shot** a review on a specific PR, on demand — the per-row `Review now` and the
   top-bar `Review a PR` picker. No subscription needed; maps to the CLI's `review-pr`.
3. **Repo report** *(later)* — review the whole repository into a findings artifact,
   optionally its own PR. See [`../repo-report/spec.md`](../repo-report/spec.md).

## Requirements
- [ ] Select a repo and see its open PRs with per-PR review status.
- [ ] Subscribe/unsubscribe personas to a repo; toggle auto-review on/off.
- [ ] Choose a subscription executor (`This app` / `GitHub Actions`); default `This app`.
- [ ] Detect and badge repos whose CI review is set up (read committed workflow + config).
- [ ] Enable CI review via a generated pull request — never a silent write to `.github/`.
- [ ] Fire a one-shot review on a chosen PR.
- [ ] Poll and display live review progress and results (per-persona), reusing the engine.
- [ ] Native AOT, instant cold start, no Electron.

## Core changes

None. The desktop shell reads and writes the existing `PeanutConfig` values and calls
`ReviewPlanner.Plan` / the engine. Any new value shape (e.g. persona scope) is specced in
[persona-management](../persona-management/spec.md), not here.

## Shell changes

- New `PeanutGallery.Desktop` project — Avalonia, code-only (no XAML), compiled bindings,
  Native AOT; immutable `WorkspaceSnapshot`-style values rendered by C# builder views; a
  `DispatcherTimer` owns polling and IO.
- GitHub API client for PR listing, comment/status reads, and PR creation (the CI setup
  and repo-report PRs).
- App-run executor reuses `PeanutGallery.Engine`'s reviewer.
- Reads `.github/peanut-gallery.json` / `peanut-gallery.yml` to detect CI state; writes to
  `.github/` only by opening a PR (ADR-0002).

## Config / contract changes

- No new core config fields for the base repo view. The subscription-executor choice is
  shell/local state (which executor runs a given assignment), not a change to the
  `Assignment` value itself.
- Persona **scope** (built-in / library / repo) is introduced in
  [persona-management](../persona-management/spec.md).

## Out of scope

- Drag-and-drop assignment (rejected — see `adr.md` Decision 1).
- Filters on which PRs a subscription reviews (path/label globs) — a later addition.
- The headless server executor (roadmap; this spec only lists it as an option).
- Persona editing/import UI — [persona-management](../persona-management/spec.md).
- The repo-report artifact/PR flow — [repo-report](../repo-report/spec.md).

## Open questions
- [ ] One-shot reach: should a one-shot accept an arbitrary PR URL (any repo you can
  comment on), not just a listed repo's PRs? — *Owner: Charles*
- [ ] Where does app-run subscription state live on disk, and how does it relate to the
  user persona library dir? — *Owner: Charles*

## Related
| Type | Link |
|------|------|
| Docs index | [`docs/INDEX.md`](../../INDEX.md) |
| Founding ADR | [`adr/0001-functional-core-multi-shell.md`](../../adr/0001-functional-core-multi-shell.md) |
| Executors ADR | [`adr/0002-review-executors-and-workflow-file-boundary.md`](../../adr/0002-review-executors-and-workflow-file-boundary.md) |
| Sibling ADR (the how) | [`adr.md`](adr.md) |
| Persona management | [`../persona-management/spec.md`](../persona-management/spec.md) |
| Repo report | [`../repo-report/spec.md`](../repo-report/spec.md) |
