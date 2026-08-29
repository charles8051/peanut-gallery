# Contributing to Peanut Gallery

Thanks for your interest. This is a small, opinionated codebase; a few conventions keep
it coherent.

## The one design rule: functional core, imperative shell

This is the highest principle in the repo, not a nicety. `PeanutGallery.Core` is a
**pure core** — immutable values and total functions, **no IO, no clock, no `Task`, no
mutable state across calls**. A clock, socket, `IChatClient`, or file handle in the core
is a bug: it belongs in a **shell** (the CLI, the Engine, PR-comment posting, the agentic
tool loop). New work must consume the existing core fold, not re-implement it. The core
is reflection-free and AOT-clean on purpose. See
[`docs/adr/0001-functional-core-multi-shell.md`](docs/adr/0001-functional-core-multi-shell.md).

## Build, test, run

Requires the **.NET 10** SDK.

```bash
dotnet build PeanutGallery.slnx -c Release
dotnet test  PeanutGallery.slnx -c Release
dotnet run --project src/PeanutGallery.Cli -- <command>   # e.g. init, validate, plan, review
```

- xUnit tests, central package management (`Directory.Packages.props`).
- `review --dry-run` uses an offline stub and needs **no API keys** — use it for local
  iteration.
- The core is exhaustively unit-tested with no keys and no network; new core logic should
  come with tests. CI runs the suite on Linux + Windows for every PR.

## Workflow

1. Branch from `main` (no direct pushes to `main`).
2. Keep the change a coherent, reviewable unit; open a PR.
3. CI (`Build and Test`) must be green, and Peanut Gallery reviews its own PRs — address
   the review feedback (or reply explaining why a finding is intentional; the reviewer
   will withdraw it).
4. Squash-free history is fine; write a clear PR description.

## Docs

- Feature work → `docs/feature-specs/<slug>/spec.md` (+ optional `adr.md`).
- Cross-cutting decisions → numbered `docs/adr/NNNN-<slug>.md` (number assigned at merge).
- Every new doc gets a row in [`docs/INDEX.md`](docs/INDEX.md).
- Work is tracked as GitHub Issues. `BACKLOG.md` is a pointer to that, not a place to add items.

## Releases

Versions come from `v*.*.*` git tags via MinVer — never hand-edit a version. The action's
container image is rebuilt and pushed on every `main` merge (moving `:main` tag).

## Reporting security issues

Do **not** open a public issue. See [`SECURITY.md`](SECURITY.md).
