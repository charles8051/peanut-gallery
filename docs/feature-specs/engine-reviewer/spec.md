# Feature Spec: Engine reviewer (real model-backed reviews)

## Status
**Implemented** (live verification against real provider keys pending).

| Field        | Value        |
|--------------|--------------|
| Author       | Charles Lee  |
| Created      | 2026-06-25   |
| Last Updated | 2026-06-25   |

## Purpose

Turn a planned `ReviewTask` into real findings by calling the persona's chosen
model, replacing the offline stub. Uses **Microsoft.Extensions.AI** directly — no
external agent harness — over OpenAI-compatible providers (OpenRouter / Fireworks).

## Affected layers

| Project / area              | Change type |
|-----------------------------|-------------|
| `PeanutGallery.Core`        | new pure `FindingsParser` (model text → `Finding[]`, reflection-free, AOT-clean) |
| `PeanutGallery.Engine`      | NEW project: `IReviewer` port, `ChatClientReviewer`, `ProviderClientFactory`, `RepoTools`, `StubReviewer` |
| `PeanutGallery.Cli`         | `review` uses the real reviewer by default; `--dry-run` selects the stub; `IReviewer`/stub moved out to Engine |

> ADR-0001 held: the only *new* logic in the core is a pure function
> (`FindingsParser`). Everything with IO — the `IChatClient` call, the file-system
> tools, the env-var key lookup — lives in the Engine shell.

## How it works

- **Provider client** — `ProviderClientFactory` builds one OpenAI `OpenAIClient`
  re-pointed at the provider `BaseUrl` (`OpenAIClientOptions.Endpoint`), wraps it as
  an `IChatClient`, and layers `.UseFunctionInvocation()`.
- **Diff-tier personas** — one `GetResponseAsync` call; the model is prompted (by the
  core's `PromptAssembly`) to return `{"findings":[…]}`, which the pure
  `FindingsParser` extracts from the reply.
- **Agent-tier personas** — the same call plus `ChatOptions.Tools = RepoTools` (read
  `read_file` / `grep` / `glob`, sandboxed to the repo root, output-capped). The
  `FunctionInvokingChatClient` runs the tool loop — this is what removes the need for
  an external harness.
- **Totality at the seam** — a missing key, unknown provider, or provider outage
  becomes a `Major` "review could not run" finding, never a throw, so one persona's
  trouble never sinks the concurrent fan-out.

## Secrets

Provider API keys are read from the environment variable named by each
`ProviderConfig.ApiKeyEnv` (`OPENROUTER_API_KEY`, `FIREWORKS_API_KEY`). Keys never
appear in config on disk. The env lookup is injected (`Func<string,string?>`) so the
reviewer is testable offline.

## Tests

- Core: `FindingsParser` (plain / fenced / prose-wrapped JSON, unknown severity,
  numeric-or-string line, dropped-empty, unparseable → empty).
- Engine: missing-key and unknown-provider → failure finding (env seam, no network);
  `RepoTools` sandbox escape refusal + glob/grep.

## Out of scope / follow-ups

- Live verification against real provider keys (BACKLOG).
- Provider-native structured output (`response_format` json_schema) as an
  alternative to prompt-and-parse, where supported.
- A review **status** on `PersonaReview` so the CLI can exit non-zero on
  infrastructure failure vs. a clean review, and a `--fail-on <severity>` gate.
- Per-task token/tool-call budget caps for agent-tier reviewers.

## Related
| Type | Link |
|------|------|
| Docs index | [`docs/INDEX.md`](../../INDEX.md) |
| Founding ADR | [`docs/adr/0001-functional-core-multi-shell.md`](../../adr/0001-functional-core-multi-shell.md) |
