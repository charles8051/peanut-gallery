# Feature Spec: Repo conventions

## Status
**Implemented**

| Field        | Value        |
|--------------|--------------|
| Author       | Charles Lee  |
| Created      | 2026-09-19   |
| Last Updated | 2026-09-19   |

## Purpose

Feed reviewers the repo's own written rules, so feedback reflects how the team actually builds
rather than generic best practice. This is the highest-value grounding lever available: it turns
"you should use dependency injection here" into "this violates the functional core in ADR-0001".

Discovery originally read the repo root only. A repo that keeps its rules one directory down —
`tests/CLAUDE.md` for the test tree, a service's own `AGENTS.md` beside it — was reviewed with no
house rules at all, which is indistinguishable from a repo that has none. `frame-flow` is exactly
that shape: no root conventions file, one at `tests/CLAUDE.md`.

## Affected layers

| Project / area              | Change type |
|-----------------------------|-------------|
| `PeanutGallery.Core`        | `ConventionsDiscovery` (pure walk), `ConventionsFile` / `RepoConventions` (value + renderer) |
| `PeanutGallery.Cli`         | `ReadConventionsAsync` probes a checkout |
| `PeanutGallery.Engine`      | `ConventionsReader` — the fold both shells share |
| Server shell (roadmap)      | none |
| Desktop shell (roadmap)     | `RemoteRepoContext.ReadConventionsAsync` probes GitHub at the PR head |

## Requirements

- [x] The repo-wide file at the root applies to every review, as before.
- [x] For each directory the change touches, the nearest conventions file above it also applies.
- [x] Within one directory's ancestry the nearest file wins; the ones above it are not sent.
- [x] A file reachable from two changed directories is sent once.
- [x] Every file is named in the prompt, and a subtree file states the subtree it governs.
- [x] A repo with no conventions anywhere reviews exactly as it did before.
- [x] Conventions never occupy the system message (ADR-0003).

## Core changes

`ConventionsDiscovery` is pure: it turns changed-file paths into the paths worth probing and
touches no filesystem and no network.

- `RootCandidates` — `.github/copilot-instructions.md`, `.github/peanut-gallery-instructions.md`,
  `CLAUDE.md`, `AGENTS.md`, most specific first. The Copilot file wins because it is unambiguously
  written for a code reviewer.
- `ScopedCandidates` — `CLAUDE.md`, `AGENTS.md`. The two `.github/` files are repo-wide by
  convention and are not looked for in a subdirectory.
- `ScopeChains(changedFiles, maxScopes)` — one chain per directory the changed files live in, each
  ordered nearest-first from that directory up to but excluding the root. The root is deliberately
  absent: `RootCandidates` covers it, with a wider list, and it applies whether or not a changed
  file sits in a subdirectory.

Diff paths are attacker-controlled on any PR, so a path that could climb out of the repo (a `..`
segment, an absolute or drive-qualified path) is dropped in the core rather than left for a shell
to notice. Directories are ordinal-sorted, so the `maxScopes` cap truncates deterministically and
the same change always probes the same paths in the same order.

`RepoConventions` now carries a list of `ConventionsFile(Path, Text, Scope)`. `PromptBlock` renders
them root-first and spends `maxChars` in order: each file takes an equal share of what is left, and
a file shorter than its share rolls the surplus forward. A single file renders exactly as it did
when only one could ever apply — no per-file header for an audience of one.

`maxChars` budgets everything that scales with how many files apply: their text, the source list in
the opening sentence, each file's heading, and the newlines wrapping it. What sits outside the
budget is the framing, which is fixed, and the source list, which names every file that applies and
is therefore bounded by `DefaultMaxScopes` paths. So a change touching eight subtrees renders no
longer a block than one touching a single file. The block rides every turn of every persona, so
that bound is what keeps the feature's cost flat.

A budget too small to give a file even one character stops the rendering rather than emitting a
heading and a bare ellipsis per file, which would grow the block with the file count.

## Shell changes

`ConventionsReader.CollectAsync` (Engine) is the fold both shells share, so they cannot drift on
which files win. It takes a probe — "give me this path's text, or null" — and owns the memo,
ordering, dedupe, and the nearest-ancestor rule. Chains overlap heavily (every file under `src/`
shares `src/`), so each path is probed at most once.

- **CLI**: the probe is a file read under `FileSystemSafety.ResolvesInsideRoot`, bounded at 64 KiB.
  Probes are unbounded — each is a local stat, not a round trip.
- **Desktop**: the probe is a GitHub contents fetch at the PR head, capped at 24 paths total.
  Hitting the cap ends the subtree pass and keeps whatever was found; partial house rules beat none.

Both shells read conventions *after* the cumulative diff resolves, because which files apply
depends on what the change touches. A failed baseline resolve costs the subtree rules and leaves
the repo-wide ones, which is the pre-existing behaviour.

## Config / contract changes

None. Discovery is by convention, not configuration.

## Out of scope

- **Every ancestor, not just the nearest.** Two levels of subtree rules would give the reviewer two
  answers to the same question, and the prompt budget is already the binding constraint.
- **A configurable candidate list.** No repo has asked for one, and the four names cover the
  agent-instruction file formats in use.
- **Reading conventions for files the change does not touch.** The whole point is that scope
  follows the diff.

## Related

| Type | Link |
|------|------|
| Docs index | [`docs/INDEX.md`](../../INDEX.md) |
| Founding ADR | [`docs/adr/0001-functional-core-multi-shell.md`](../../adr/0001-functional-core-multi-shell.md) |
| Trust boundary (why this rides the user turn) | [`docs/adr/0003-prompt-channel-trust-boundary.md`](../../adr/0003-prompt-channel-trust-boundary.md) |
