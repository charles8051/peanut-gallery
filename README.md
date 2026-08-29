# Peanut Gallery

**A persona-driven, multi-model code-review tool. One pure core, many shells.**

Peanut Gallery runs a panel of opinionated reviewer *personas* over a change — an
architect, a bug-hunter, a divergent contrarian who wants to delete your whole
subsystem — each on the model you pick (any OpenRouter or Fireworks model), each
posting its own self-updating verdict. Bring your own API keys; pick your own
models; assign your own personas to your own repos.

It is the flexible successor to a single-reviewer autoreview bot:
key-based instead of an interactive OAuth dance, multi-model instead of one, and a
fan-out of distinct lenses instead of one generic pass.

## The shape: functional core, imperative shell

The whole tool is one **pure core** ([`PeanutGallery.Core`](src/PeanutGallery.Core))
of immutable values and total functions — personas, providers, the diff model, the
review *plan* fold, prompt assembly, and PR-comment rendering. No IO, no clock, no
`Task`. Every interface (a CLI now; a headless server and an Avalonia desktop GUI
on the roadmap) is a thin **shell** that projects that one core onto a surface. The
shells can never disagree about what a review *is*, because they all run the same
`ReviewPlanner.Plan` fold.

```
                       PeanutGallery.Core  (pure, AOT-clean, fully unit-tested)
   personas · providers · Diff.Parse · ReviewPlanner.Plan · PromptAssembly · CommentRenderer
                                       │  values in / values out
        ┌──────────────────────────────┼───────────────────────────────┐
   PeanutGallery.Cli              (roadmap) Server               (roadmap) Desktop
   kick off a review         headless reviews on a VM +      drag-persona-onto-repo
   from your workstation       a web management page          Avalonia GUI, Native AOT
```

## Quickstart

Requires the **.NET 10 SDK**.

```bash
# build + test
dotnet test PeanutGallery.slnx -c Release

# scaffold a config, then drive a review from a diff
dotnet run --project src/PeanutGallery.Cli -- init
git diff origin/main... | dotnet run --project src/PeanutGallery.Cli -- \
  review --repo <name> --diff -
```

`init` writes a `peanut.json` with the three archetype personas, all on one provider and
one model (see [`examples/peanut.json`](examples/peanut.json)). Its repo target is the
directory you ran it in, named after that directory: the name it prints is the `--repo
<name>` every later command takes. Set your provider key
(`OPENROUTER_API_KEY`) as an environment variable — the config
only ever stores the *name* of the env var, never the secret. Other providers (Fireworks,
or anything OpenAI-compatible) are a `providers` entry away.

### Commands

| Command | Does |
|---|---|
| `init [path]` | Write a sample `peanut.json` |
| `personas` | List the configured reviewer personas |
| `validate` | Structurally check the config |
| `plan --repo <name> --diff <file\|->` | Show which personas would review the diff |
| `review --repo <name> --diff <file\|->` | Run the review and render the comments |
| `review-pr --repo <name> --pr <n>` | Review a GitHub PR and post one self-updating comment per persona |
| `await-review --pr <n>` | Block until this push's review lands, then print its findings (exit `0` complete and clean, `2` findings, `3` timed out, `4` the review did not happen) |
| `metrics --slug <owner/repo> [--since <days>]` | Aggregate the per-PR run-metrics ledgers into a flake/refute/author-agreement/latency/cost report |

## Use it as a GitHub Action

Peanut Gallery ships as a Docker container action — drop it into any repo's PR workflow:

```yaml
# .github/workflows/review.yml
on:
  pull_request:
    types: [opened, reopened, synchronize, ready_for_review]
permissions:
  contents: read
  pull-requests: write
jobs:
  review:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v6
      - uses: charles8051/peanut-gallery@main
        with:
          openrouter-api-key: ${{ secrets.OPENROUTER_API_KEY }}
          # config: peanut.json   # optional; omit to use the bundled default panel
```

> `@main` tracks the default branch and can change under you. Pin a release tag — or a
> commit SHA, which is what GitHub's own hardening guidance recommends for third-party
> actions — once you depend on it.

Inputs: `pr-number` (defaults to the triggering PR), `config` (a repo-relative config
path; empty = the bundled default panel described below), `github-token` (defaults to
the workflow token), `openrouter-api-key` / `fireworks-api-key`, and `provider-keys`
(see below). Reviewers
are stateful across pushes — state lives in each persona's PR comment, so re-runs
continue rather than restart.

**The default panel, if you commit no config.** Two diff-tier reviewers — an architect and
a bug-hunter — plus an orchestrator that reads the diff at PR-open and convenes up to two
more reviewers aimed at what this change actually risks, pinned for the life of the PR.
They speak with one deduplicated comment, and they answer PR comments addressed to
`@peanut-gallery` with a single reconciliation call rather than a full panel turn. It is
`seedAndAuto` rather than `auto` on purpose: the two seeded reviewers run whatever the
orchestrator does, so a planner that fails leaves a review rather than an empty board.
Commit a [`peanut.json`](examples/peanut.json) when you want to pin your own lenses.

**Any provider via `provider-keys`.** A config provider block only names the *env var*
its key lives in (`apiKeyEnv`), so to use a provider without a dedicated input, pass the
key(s) through `provider-keys` — one `KEY=VALUE` per line, exported before the review runs:

```yaml
      - uses: charles8051/peanut-gallery@main   # pin a tag or SHA — see the note above
        with:
          config: .github/peanut-gallery.json
          provider-keys: |
            NVIDIA_API_KEY=${{ secrets.NVIDIA_API_KEY }}
            TOGETHER_API_KEY=${{ secrets.TOGETHER_API_KEY }}
```

`openrouter-api-key` / `fireworks-api-key` remain as convenience inputs; `provider-keys`
is the general path for everything else.

> **Runner note:** the action runs a prebuilt image from GHCR rather than building the
> Dockerfile per job. `ubuntu-latest` needs no setup **provided the GHCR package is
> public**; if `docker pull` fails with an authentication error, it is not, and that is a
> package setting the maintainer changes rather than something a consumer can work
> around. Cross-repo `uses:` also needs
> Settings → Actions → "Allow access from other repositories".

## Status

**Core + CLI + Engine + GitHub PR review.** The full pipeline runs —
config → plan → concurrent fan-out → rendered comments. `review` calls real models
by default via the Engine (Microsoft.Extensions.AI `IChatClient` over
OpenRouter/Fireworks; `.UseFunctionInvocation()` + sandboxed read-only tools for the
agentic contrarian); `review --dry-run` uses an offline stub needing no keys.
`review-pr` posts one self-updating comment per persona — wired to run fresh per PR via
[`autoreview.yml`](.github/workflows/autoreview.yml).
Reviewers are **stateful across pushes**: each persona's session (last SHA, running
summary, open findings) is persisted inside its own PR comment, so a new push sends
only the delta and the reviewer reports what changed and what was resolved. Provider keys are read from the
env vars named in the config (`OPENROUTER_API_KEY` / `FIREWORKS_API_KEY`). The server
and desktop shells are on the [roadmap](docs/roadmap.md).

**When a review runs.** On a push to the PR, and on a **new** PR comment — a comment is
how you talk back to the panel (explain a finding is intentional and the reviewer moves
it to *withdrawn*). **Editing an existing comment does not trigger a review**; post a new
one instead. What a comment costs is configurable via
[`conversation`](docs/feature-specs/conversation-modes/spec.md): a `mentions` gate so only
comments addressed to the panel count at all, and a mode of `panel` (every persona takes a
full turn — what an unset `conversation` key means), `reconcile` (one call decides what
comes off the board), or `off`. The bundled default config and this repo both run
`reconcile` with `@peanut-gallery`, so two humans talking in the PR thread cost nothing
and a question for the panel costs one call rather than four.

## Documentation

- [`docs/INDEX.md`](docs/INDEX.md) — the question → document map (read this first)
- [`docs/roadmap.md`](docs/roadmap.md) — the shells and slices ahead
- [`docs/adr/0001-functional-core-multi-shell.md`](docs/adr/0001-functional-core-multi-shell.md)
  — why one pure core projected to many shells
- [`CONTRIBUTING.md`](CONTRIBUTING.md) — the one design rule, build and test, workflow,
  docs layout. **Read this before opening a PR**, whether you are a person or an agent.
- [`SECURITY.md`](SECURITY.md) — threat model and how to report a vulnerability privately

## License

[MIT](LICENSE). Copyright (c) 2026 Charles Lee.
