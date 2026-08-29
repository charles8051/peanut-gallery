# Feature Spec: GitHub PR review (the CI/workflow shell)

## Status
**Implemented.**

| Field        | Value        |
|--------------|--------------|
| Author       | Charles Lee  |
| Created      | 2026-06-25   |
| Last Updated | 2026-06-25   |

## Purpose

Make Peanut Gallery run automatically on a pull request: fetch the PR diff, run the
persona panel, and post one self-updating comment per persona. This is the
"fresh-per-PR" invocation model — a stateless process per PR on an ephemeral
self-hosted runner.

## Affected layers

| Project / area              | Change type |
|-----------------------------|-------------|
| `PeanutGallery.Core`        | new pure `CommentSync` (reconcile rendered comments vs. existing by marker) |
| `PeanutGallery.Cli`         | new `GitHubClient` (REST shell) + `review-pr` verb |
| repo root                   | `peanut.json` (the panel, shared by local CLI + CI), `.github/workflows/autoreview.yml` |

> ADR-0001 held: the only new *core* logic is `CommentSync.Plan`, a pure
> create-vs-update decision. All GitHub IO is in the `GitHubClient` shell.

## How it works

1. The workflow triggers `on: pull_request` (same-repo only — fork PRs get no
   secrets), runs on `[self-hosted, linux, x64, docker]` in a `dotnet/sdk:10.0`
   container, and `dotnet run`s the tool against the PR's own source.
2. `review-pr --pr N`: reads `GITHUB_REPOSITORY` / `GITHUB_TOKEN` / `GITHUB_API_URL`
   from the Actions env, fetches the PR's unified diff
   (`Accept: application/vnd.github.diff`), runs `ReviewPlanner.Plan` + the real
   `ChatClientReviewer` fan-out, renders comments, then `CommentSync.Plan` decides
   create-vs-update against the comments already on the PR (matched by the
   `<!-- peanut-gallery:<id> -->` marker) so each persona keeps one comment, updated
   in place across pushes.
3. `--diff <file>` + `--dry-run` runs the whole thing offline (no token, no posting)
   for local testing.

## Invocation model

Fresh process per PR; no shared state (the runner is ephemeral). The persona panel
and models are `peanut.json` (default `openai/gpt-4o-mini` over OpenRouter, diff
tier, to keep PR reviews fast and cheap).

## Secrets

`OPENROUTER_API_KEY` (and `FIREWORKS_API_KEY`) as repo secrets → env. `GITHUB_TOKEN`
is the Actions-provided token (`permissions: pull-requests: write`).

## Deploy (one-time)

- Any runner can pull the action image from GHCR once the package is public; a
  self-hosted scope is a cost choice, not a requirement.
- Set the provider key secret: `gh secret set OPENROUTER_API_KEY -R charles8051/peanut-gallery`.

## Tests

- Core: `CommentSync` (create when new, update in place by marker, no cross-persona
  match, no-marker → create).
- Verb wiring exercised offline via `review-pr --diff … --dry-run`.

## Out of scope / follow-ups

- Inline per-line review comments (this posts conversation-level issue comments).
- Build caching on the runner (each PR currently restores + builds from source).
- A synthesizer comment that ranks across personas.
- Cross-repo reuse: a reusable workflow + thin caller workflows (needs the tool
  published to nuget.org first).

## Related
| Type | Link |
|------|------|
| Docs index | [`docs/INDEX.md`](../../INDEX.md) |
| Engine | [`docs/feature-specs/engine-reviewer/spec.md`](../engine-reviewer/spec.md) |
| Founding ADR | [`docs/adr/0001-functional-core-multi-shell.md`](../../adr/0001-functional-core-multi-shell.md) |
