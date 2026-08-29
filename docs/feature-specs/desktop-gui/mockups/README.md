# Desktop GUI mockups

Durable, self-contained HTML mockups of `PeanutGallery.Desktop` — the design reference
to build the Avalonia shell against. Each file is standalone (inline CSS/JS, no external
dependencies, no build step): **open any `.html` directly in a browser**. They are visual
intent, not production code. All repo/PR/author data is **fictional** (`acme/*`).

See the roadmap entry: [Shell — desktop GUI](../../../roadmap.md) (P2).

## The model: repo-centric

The primary axis is the **repository**. You pick a repo from the list, see its open
pull requests and their review status, and manage how it's reviewed. Three distinct
review actions:

1. **Subscribe** one or more personas to a repo → every open and future PR is
   auto-reviewed (persistent; one self-updating comment per persona). Toggleable.
2. **One-shot** a review on a specific PR, on demand (no subscription needed) — the
   `Review now` row action / the `Review a PR` button.
3. **Review the whole repository** *(later)* → produce a findings **artifact** (a
   markdown report), optionally opened as **its own PR** containing just the report.
   Meant to interoperate with document-style review skills/workflows.

There is intentionally **no drag-and-drop** — subscribing is an explicit action, not a gesture.

## Executor & the workflow-file boundary

A subscription has an **executor** — *who actually runs the reviews* — surfaced as a
`Runs on` control:

- **This app** — reviews run on your machine while Peanut Gallery is open. No repo files;
  the subscription lives in the app's own state. Good for ad-hoc and repos you don't own.
- **GitHub Actions** — always-on, team-wide, runs even when the app is closed. This is the
  only executor that involves `.github/` files.
- *(future)* **My server** — the headless server executor; always-on, personal, still no repo files.

The GUI's relationship to the GitHub Actions workflow files is deliberately narrow:

- **Reads** them to *detect* state — the `CI` pill on a repo row means a committed
  `peanut-gallery.yml` + `.github/peanut-gallery.json` was found; that committed config is
  the source of truth for the CI subscription.
- **Writes** them only as a **one-shot, explicit pull request** — the "Set up always-on CI"
  action (and any later edit) *opens a PR* you review and merge. The GUI never edits your
  workflow files silently and never *owns* them continuously.

Same config value, different executors: the app can "promote" an app-run subscription to a
committed CI one by generating the workflow + config from the same `personas`/`assignments`.

## Screens

| File | Screen | Status |
|---|---|---|
| [`repo.html`](repo.html) | Repository view — repo list, PRs + status, persona subscriptions, one-shot review, repo-report (later) | drafted |
| [`review-detail.html`](review-detail.html) | A finished review — per-persona findings with severity + file:line, matching the PR-comment output | drafted |
| [`one-shot.html`](one-shot.html) | Fire a one-shot review — pick a PR (or paste a URL) + personas, post or preview | drafted |
| [`providers.html`](providers.html) | Providers + API keys — OpenAI-compatible endpoints, key set/unset state | drafted |
| Personas + editor | Persona library / scopes / import — own feature: [`../../persona-management/`](../../persona-management/spec.md) (prototypes [`personas.html`](../../persona-management/mockups/personas.html), [`persona-editor.html`](../../persona-management/mockups/persona-editor.html)) | drafted |

## Design language

- **Dark, dense, developer-tool.** Two text weights (400/500), sentence case, no title case.
- **Colour encodes meaning, not sequence.** Each persona owns one accent (Architect =
  purple, Bug Hunter = coral, Contrarian = amber); PR review status uses semantic dots:
  grey = not reviewed · blue (+ progress) = reviewing · green = clean · amber + severity
  dots = findings (red = high, amber = minor).
- Flat surfaces, hairline borders, 12px card corners — no gradients/shadows.

## Building against these

The layout maps to the config the core consumes: repo list ↔ `repos[]`, subscribed
persona chips ↔ `assignments[]`, the personas/providers screens ↔ `personas[]` /
`providers[]`. The GUI is a thin shell that edits those values and surfaces review
status + PR state; all review logic stays in `PeanutGallery.Core`. The one-shot and
repo-report actions map to the CLI's `review-pr` / a future repo-report command.
