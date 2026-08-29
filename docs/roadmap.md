# Roadmap

Peanut Gallery grows by adding **shells** around a frozen pure core, and by
deepening the **engine** that turns a `ReviewTask` into real findings. The core
(`PeanutGallery.Core`) is the foundation and is meant to stay small and stable;
nearly all new work is a shell or an engine slice that consumes it.

Each item carries a `**Priority:**` P0–P3 tag. Living
document — re-triage freely.

## Done — the foundation

- **Pure core.** Personas, providers, `Diff.Parse`, `ReviewPlanner.Plan`,
  `PromptAssembly`, `CommentRenderer`, `ConfigValidation` — immutable values +
  total functions, `IsAotCompatible`, 20 unit tests, zero IO.
- **CLI shell.** `init` / `personas` / `validate` / `plan` / `review`, hand-rolled
  arg parsing (reflection-free, AOT-friendly), reflection-based JSON config IO,
  concurrent persona fan-out, and a deterministic offline **stub reviewer** so the
  whole pipeline runs with no API keys.

## Done — the Engine (real reviews)

Shipped in `PeanutGallery.Engine` (Microsoft.Extensions.AI 10.7.0 +
Microsoft.Extensions.AI.OpenAI + OpenAI 2.11.0, over OpenRouter/Fireworks, no
external harness). **Live verification against real provider keys is still pending**
— no keys are wired in this environment; the offline paths (dry-run + graceful
missing-key failures) are tested. The shape:

- One `IChatClient` per provider/model, OpenAI-compatible `OpenAIClient` pointed at
  the OpenRouter / Fireworks `BaseUrl`, key read from the persona's
  `ProviderConfig.ApiKeyEnv`.
- **Diff-tier** personas → a single typed `GetResponseAsync<Findings>` call (JSON
  schema in, deserialized findings out — no prose scraping).
- **Agent-tier** personas (the contrarian) → the same client with
  `.UseFunctionInvocation()` plus three read-only `AIFunction`s (`read_file`,
  `grep`, `glob`) sandboxed to the checkout. `FunctionInvokingChatClient` runs the
  tool loop — this is what makes a separate agent harness unnecessary.
- Findings map straight onto the core's `Finding` / `PersonaReview`, rendered by the
  existing `CommentRenderer`.
- The pure `FindingsParser` (in the core) turns the model's text into `Finding`s;
  `Core` stays dependency-free, the Engine owns the IO.

## Next — synthesizer / judge pass

**Priority: P2** — a synthesizer/judge pass that dedupes and ranks findings across
personas into one summary comment (a "judge panel" pattern).

## Done — GitHub PR review (the CI/workflow shell)

Shipped: `review-pr` fetches the PR diff, runs the panel, and posts one
self-updating comment per persona (in-place via the `<!-- peanut-gallery:<id> -->`
marker, reconciled by the pure `CommentSync`). The `autoreview.yml` workflow runs it
`on: pull_request`, fresh per PR, on a self-hosted ephemeral runner (same-repo
only). Persona panel = `peanut.json` (shared by the local CLI and CI). See
[`feature-specs/github-pr-review/spec.md`](feature-specs/github-pr-review/spec.md).
**Deploy is one-time:** register a self-hosted runner scope and set the
`OPENROUTER_API_KEY` repo secret (see the spec).

Next here: inline per-line comments; build caching on the runner; a reusable
workflow + thin callers so it can review *other* repos (needs the tool published to
nuget.org first).

## Done — packaged as a Docker GitHub Action

Peanut Gallery is a Docker container action (`action.yml` + `Dockerfile` at the repo
root): any repo adds `uses: charles8051/peanut-gallery@main` with a provider key and a
config (or the bundled default panel). Runner-agnostic, versioned, stateful across
pushes. This repo dogfoods it via `uses: ./`. Kept private (no Marketplace). Next: a
`--fail-on` gate exposed as an action output
([#4](https://github.com/charles8051/peanut-gallery/issues/4)).

## Done — prebuilt GHCR image (sub-build-time cold starts)

GitHub used to rebuild the container action from the `Dockerfile` at the start of every
review job (a full `dotnet restore`/`publish`). It now pulls a prebuilt image instead:

- **`.github/workflows/image.yml`** builds the image on every push to `main` and pushes
  it to `ghcr.io/charles8051/peanut-gallery` under a **moving `:main` tag** (plus an
  immutable `:<sha>`), with a buildkit **registry cache** (`:buildcache`). The `Dockerfile`
  splits the `dotnet restore` into its own layer so a source-only change reuses the
  cached restore.
- **`action.yml`** uses `image: docker://ghcr.io/charles8051/peanut-gallery:main`, so
  consumers (and this repo's dogfood) `docker pull` a ready ~runtime image instead of
  building it per job.
- **Image visibility is the consumer gate.** A public GHCR package is pulled by any
  runner with no credential and no per-workflow change; a private one requires every
  consumer to supply a runner already `docker login`'d to GHCR. Nothing in this repo can
  set that - it is a package setting with no API - so making it public is a release
  prerequisite rather than a nicety.
- **Dogfood note:** this repo's own review (`uses: ./`) now pulls `:main` rather than
  building the PR's source, so a PR is reviewed by the last-merged reviewer version (a
  stable reviewer, not the unreviewed PR code) — acceptable, revisit if PR fidelity is
  wanted.
- **Follow-up:** cut the same image on `v*.*.*` release tags for immutable pinning
  alongside the moving `:main`.

## Done — per-PR opt-out

Add a `peanut-gallery: skip` (or `no-review`) **label** to a PR — or a `[skip-review]`
marker in its title/body — and the reviewers skip it (remove the label to resume). Draft
PRs are reviewed by default (`skip.drafts` opts out). Enforced in the action (pure
`SkipPolicy`), so it works on every consumer repo and on both push + comment triggers.
Config via `peanut.json` `skip`. See
[`feature-specs/opt-out/spec.md`](feature-specs/opt-out/spec.md).

## Done — conversational reviewer

Reviewers now ingest the PR author's (and human reviewers') comments since their last
review and can **withdraw** a finding the author explains as intentional / a false
positive (distinct from **resolving** one fixed in code). Watermarked by comment id in
the session; bots + own-marker comments excluded (no loop). Consumer workflows also
trigger `on: issue_comment` (human-only guard), so a reply gets a response without a
push. See [`feature-specs/conversational-reviewer/spec.md`](feature-specs/conversational-reviewer/spec.md).
Follow-up: inline (review-comment) ingestion.

## Done — large-diff handling (Phase 1)

`review-pr` now filters the diff before review: drops low-signal files (binary,
rename-only, ignore-glob: lockfiles/generated/minified/vendored) and caps total size
(default 128 KB), omitting the largest files first and disclosing the omissions in the
prompt. Pure-core `DiffFilter`; config via the `peanut.json` `filter` block. Also
shipped alongside: a review time budget (env `PG_REVIEW_TIMEOUT_SECONDS` for the turn,
`PG_CALL_TIMEOUT_SECONDS` for a single attempt — see the flake-resilience note below),
parallel personas, per-persona progress logging, and a `timeout-minutes` job backstop —
so a slow/hung model degrades to a failure finding instead of stalling the run. See
[`feature-specs/large-diffs/spec.md`](feature-specs/large-diffs/spec.md). Phase 2
(chunking) + agent-pull are filed as issues.

## Done — flake resilience + run observability

A transient flake (a slow/hung OpenRouter route hitting the per-attempt deadline, a
5xx/429, or a dropped connection) no longer needs a human to re-push. The model call
now runs under a **bounded in-process retry** (`RetryingModelCall` + pure
`RetrySchedule`/`TransientFailure`) with **two nested budgets** (issue #133): a **per-call
ceiling** (`PG_CALL_TIMEOUT_SECONDS`, default 180s) bounds a single attempt, split from the
**whole-turn budget** (`PG_REVIEW_TIMEOUT_SECONDS`, default 600s) the runner's per-persona
`TimeBox` enforces over all attempts. Fusing them was the shape of the minimax-m3 timeouts —
one runaway call spending the entire 600s — so the per-call ceiling now bounds *every*
attempt: a runaway dies in ~180s and the retry gets a fresh, fail-fast shot (new sampling)
inside the same turn budget. The 180s ceiling sits ~2x above a reasoning review's observed
success latency (~90s), so a legitimately-slow call is not cut off. Default two attempts (one
retry), env `PG_REVIEW_MAX_ATTEMPTS`; the SDK `NetworkTimeout` is the outer backstop so the
per-attempt `TimeBox` always owns the deadline. (Sampling: auto-persona temperature floors at
`PanelFence.DefaultTemperature`, raised 0.2 → 0.25 in #133 — a touch more entropy against the
runaway, atop the per-call timeout, since 0.2 alone did not tame minimax-m3.) Observability: a degraded
persona is now legible without archaeology — a **structured failure log line** (model,
diff size, latency, exception, attempt count) and a durable per-run **Job Summary** table
+ a `::warning::` **annotation** (pure `RunSummary`), neither of which changes the run's
green conclusion. A weekly flake-rate aggregate across runs is filed as an issue.

## Done — a hung reviewer costs only itself

Two defects found together on one production PR, where the job hit its
`timeout-minutes: 15` backstop twice in a row and posted **nothing** both times, while
three of the four personas had finished with 13 findings between them.

- **Comments are published as each persona lands** ([#116]), not batched into one
  end-of-run write. `ReviewRunRequest.Publish` is the seam; the runner serializes it
  (rendering happens inside the lock, so concurrent completions cannot post out of
  order) and swallows a failed intermediate write. In panel mode the shared comment is
  re-rendered from whoever has reported so far and says **"still running"** instead of
  "Reviewed through `<sha>`" — a partial panel must not print the marker humans and
  polling agents read as *complete*. Posting twice for one marker is made safe by the
  pure `CommentLedger`, which records this run's own writes so the second write updates
  in place (and skips a body identical to what is already there) instead of duplicating.
  No cancellation-time flush is needed: with incremental posting the only work not yet
  on the PR at kill time is the work that had not finished.
- **One deadline per turn** ([#117]). `PG_REVIEW_TIMEOUT_SECONDS` bounded a single model
  *attempt*: `RetrySchedule` gives the final attempt the full budget, and a persona turn
  issues several calls (review, shrink ladder, JSON repair, adversarial pass), each with
  a fresh one — so a persona was observed still running at 616s under a 600s setting.
  `ReviewRunner` now runs each persona's whole turn under one `TimeBox` of that length
  (and the orchestrator and reconciler calls under the same ceiling — the failing run
  spent 278s planning the panel before a reviewer started). Exceeding it is a normal
  failed persona: comment preserved, retried next push, siblings untouched. The per-call
  budget is unchanged underneath, so a single legitimately-slow call still gets the lot.
  Both knobs are now parsed by the pure `ReviewBudget`, one place for both shells.

[#116]: https://github.com/charles8051/peanut-gallery/issues/116
[#117]: https://github.com/charles8051/peanut-gallery/issues/117

## Done — stateful PR review sessions

`review-pr` is now stateful: each persona keeps a session (last SHA, running summary,
open findings) persisted **inside its own PR comment**, so a push sends only the delta
since its last review, the reviewer remembers what it flagged, and it reports what was
resolved. Persisted-state on the ephemeral runner — no resident process. See
[`feature-specs/stateful-sessions/spec.md`](feature-specs/stateful-sessions/spec.md).
Follow-up: explicit provider `cache_control` to realize prompt-cache savings on rapid
pushes.

## Shell — headless server

**Priority: P2** — `PeanutGallery.Server`, an ASP.NET Core service that runs on a
VM and kicks off reviews triggered from the workstation or from webhooks.

- REST/SignalR over the same core fold; long-running reviews as background work.
- A **web management page** to CRUD personas, providers, repos, and assignments
  (the config the core consumes), plus live review status. This is the remote
  counterpart to the desktop GUI's drag-and-drop.
- Auth + secret storage for provider keys are server (shell) concerns; the core
  is untouched.

## Shell — desktop GUI (Native AOT)

**Priority: P2** — `PeanutGallery.Desktop`, a dead-simple local app: install it,
OAuth with GitHub, plug in API keys, then **drag persona cards onto repo cards** to
build the assignment set and start reviews. Modeled on an earlier instant-spawn
board by the same author:

- **Avalonia, code-only (no XAML)**, `AvaloniaUseCompiledBindingsByDefault` — no
  runtime reflection, no markup parsing.
- **Native AOT** via a `win-x64-aot.pubxml` (`PublishAot` + `SelfContained` +
  `OptimizationPreference=Size` + `StripSymbols` + `InvariantGlobalization`),
  published from a VS dev shell so the MSVC linker is on PATH. Instant cold start,
  no Electron.
- Immutable `WorkspaceSnapshot`-style values; a `DispatcherTimer` shell owns the
  poll/IO; views are C# builders that render snapshots. The "drag persona onto
  repo" gesture just appends an `Assignment` value — the same value the CLI and
  server already use.
- Pins the Avalonia 12.x line already validated as AOT-clean.

## Cross-cutting hardening (when shells exist)

- **Priority: P2** — source-generated `JsonSerializerContext` so the CLI (and any
  shell) can publish Native AOT; swap the reflection-based `ConfigIo` over.
- **Priority: P2** — secret handling: keys stay in env/secret stores, never in
  committed config; redact in logs.
- **Priority: P3** — prompt-injection posture for agent-tier reviewers (read-only,
  checkout-sandboxed tools; no network/secret access from tools).
