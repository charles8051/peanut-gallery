# Review budget: two nested time budgets + the non-greedy temperature floor

**Status:** Implemented · **Issue:** resolves [#133](https://github.com/charles8051/peanut-gallery/issues/133) (builds on #117, #127/#128)

## Problem

`minimax/minimax-m3` — a reasoning model — is prone to a **reasoning runaway**: at low temperature its
reasoning loops until it hits either the output-token cap (a `Truncated` finding) or the wall clock (a
`Timeout`). Across 25 recent production PRs (295 persona-runs) **15.3% of runs failed, 100% of them
minimax-m3**, and 76% of those were the runaway. Early temperature *lowering* (#127/#128, 0 → 0.2 → 0.25)
did not eliminate it — and, it turned out, pointed the wrong way (see the temperature section below):
minimax-m3 is tuned for 1.0, so the low temperature was feeding the loop. A direct probe also found
OpenRouter's `reasoning.max_tokens` is a **no-op** on minimax-m3 (a `max_tokens: 200` request produced
2093 reasoning tokens). The levers that actually moved it were bounding the wasted time (the per-call
timeout below), switching the provider (OpenRouter → Fireworks), and running the model at its recommended
sampling (temperature 1.0, top_p 0.95, top_k 40).

## Design

### Two nested time budgets

Before #133, `PG_REVIEW_TIMEOUT_SECONDS` was **both** the per-attempt and the whole-turn ceiling (they
were fused in #117 to give the turn a single owned clock). `RetrySchedule` hands the final attempt the
full budget, so one runaway call spent the entire 600s with no room to retry.

`#133` splits them into a nesting:

| Knob | Env | Default | Bounds | Enforced by |
|---|---|---|---|---|
| **Turn budget** | `PG_REVIEW_TIMEOUT_SECONDS` | 600s | a whole persona turn (all attempts + backoff) | `ReviewRunner`'s per-persona `TimeBox` |
| **Per-call budget** | `PG_CALL_TIMEOUT_SECONDS` | 300s | a single model attempt | `RetrySchedule` → `ChatClientReviewer` |
| **Output cap** | `PG_MAX_OUTPUT_TOKENS` | 40000 | total completion tokens (reasoning + content) per call | `ChatClientReviewer` → `ChatOptions` |

The per-call ceiling bounds **every** attempt, so a hung call is abandoned and the retry gets a fresh
shot inside the same turn budget. With the 300s default, `RetrySchedule.For(300, 2, 240)` yields
`[240s, 300s]`: the first attempt fails fast at 240s, the final gets the full per-call budget. The turn
budget remains the outer backstop (and the `timeout-minutes` job step is the backstop beneath *that*).

**Why 300s / 40000 (raised from 180s / 24576).** The two ceilings must be sized *together*: at
minimax-m3's ~190 tok/s on Fireworks, a full 40k-token review takes ~210s, so a 180s call ceiling would
have converted a legitimately long review into a *timeout* before it finished. 300s keeps the **output
cap** — not the clock — the real ceiling on a real review. And the cap moved to 40000 after inspecting
the `Truncated` failures on Fireworks + temp 1.0: they were **not loops** — a large diff (e.g. 23 files)
produces a coherent, progressing review that genuinely runs ~20–25k+ tokens (85% unique reasoning, all
files referenced, real findings emitted), and the old 24576 cap chopped the higher-variance runs
mid-review, discarding real reviews as "flakes". The durable fix for very large diffs is **chunking**
(backlog) so no single review needs this many tokens; until then the wider cap stops the truncation. All
three are env-tunable and read once, totally, through `ReviewBudget` (a garbage value → the default,
never a crash).

### Temperature default: the recommended 1.0 (a corrected direction)

`PanelFence.DefaultTemperature` — the auto-persona floor and the value **every** unspecified temperature
resolves to — is **1.0**, MiniMax-M3's recommended setting.

This value moved 0 → 0.2 → 0.25 → **1.0**, and the final move reversed the earlier direction. The first
raises were on the theory that a *low* temperature damps the runaway; that was **backwards**. minimax-m3
is tuned for 1.0, so running it at 0.25 was *starving* it into the low-temperature reasoning loops. The
durable fixes were the **provider** (OpenRouter → Fireworks — its pool loops where Fireworks completes)
and **matching the recommended temperature**; at 1.0 the reasoning is longer but converges (0/7 runaway
on the two hardest diffs vs runaway at 0.25). The only surviving piece of the old rationale is that 0
(greedy decoding) is a bad default — 1.0 is both non-greedy and correct.

- **Absent is absent** (#127). `Persona.Temperature` is `double?`, like its `TopP`/`TopK` siblings, and
  `Persona.SamplingTemperature()` — *the* single resolution point — turns null into the default. It had to
  be nullable: a `double` cannot tell an omitted `peanut.json` key from a deliberate `0`, so `ConfigCodec`'s
  reflection-based deserialization silently decoded "unspecified" to `default(double)` = greedy, while
  `PanelCodec` decoded the same absence to 1.0. Two decode paths, two answers, and the config one was the
  hazard. Neither codec answers it now; both hand `null` (or, for a pin, the frozen resolved value) to the
  one resolution. An **authored** `0` is still honoured — the issue is the value nobody chose, not the one
  somebody did — and the shells log `Persona.UnsetTemperatureNotice` where they log their other config
  decisions, so a config sampling at an unwritten value says so.
  <br>The resolution is a **method, not a computed property**, and that is load-bearing: `ConfigCodec`
  serializes `Persona` by reflection over its public properties, and both `ConfigIo.Save` and the desktop's
  `PersonaLibraryStore.Save` write those files to disk. A property would have emitted a derived
  `samplingTemperature` key — ignored on read — into every file a user hand-edits, re-materialising the
  very default this fix keeps out of them. A method leaves the serializer nothing to find, without
  teaching a core value type about a shell's JSON shape (`[JsonIgnore]`) or adding a blanket
  drop-read-only-properties rule to the codec, which would have eaten the positional record parameters
  that *are* the config schema.
- **The floor** (`AutoTemperature` = `max(seed, 1.0)`) keeps orchestrator-convened personas at ≥ the
  recommended temperature; an operator who wants lower sets `personaTemperature` explicitly (bypasses it).
- **Bundled built-ins** run *other* models (claude/deepseek/grok) at their own sane temperatures
  (0.25–0.8); the `BuiltInPersonasTests` guard therefore pins only non-greedy (> 0), not ≥ the minimax
  default.
- The orchestrator's **panel-selection** temperature (`PanelPlanner`, 0.2) is still intentionally *not*
  raised — selecting the panel wants near-determinism, a separate concern from review sampling.
- Enrolled repos set `temperature`/`personaTemperature` to **1.0** explicitly (bypassing the floor).

### top_p / top_k sampling controls

MiniMax-M3's recommended sampling is temperature 1.0 **plus top_p 0.95, top_k 40**, but the config
schema originally carried only `temperature`. These are now first-class controls: added to `Persona`
(per-persona, for seeds) and `PersonaTopP`/`PersonaTopK` (auto reviewers, mirroring `personaTemperature`),
plumbed through `ReviewRequest` to `ChatOptions.TopP`/`TopK`. They're unbounded-safe (no greedy hazard
like temperature 0), so the rule is plain explicit-or-inherit-from-seed with **no floor**; absent → the
provider default (never a forced 0). Validated `top_p ∈ (0, 1]`, `top_k ≥ 1`. Enrolled configs set
`top_p: 0.95` / `top_k: 40` to complete the recommended sampling profile.

## What was ruled out

`reasoning.max_tokens` / `reasoning.effort` (OpenRouter) — probed and non-binding on minimax-m3, so no
reliable in-band reasoning cap exists. A less runaway-prone reviewer model (an A/B for the seed persona)
is the deeper alternative if the timeout-split + temperature don't get the rate low enough — tracked on
#133.

## Affected layers

| Project / area | Change |
|---|---|
| `PeanutGallery.Engine` | `ReviewBudget`: `PG_CALL_TIMEOUT_SECONDS` + `CallTimeout`, `FromEnvironment` returns the call budget; `RetrySchedule` doc; `BuiltInPersonas` guard is non-greedy (> 0) |
| `PeanutGallery.Core` | `PanelFence.DefaultTemperature` → 1.0; the token+call budgets raised (this change) |
| `PeanutGallery.Cli` | passes `callTimeout` to the reviewer, `timeout` to `PersonaBudget` |

## Tests

- `ReviewBudgetTests`: per-call env parse split from the turn budget (turn untouched).
- `RetryScheduleTests`: the default per-call budget yields the `[240,300]` schedule.
- `PanelPinningTests`: floor theories at 1.0.
- `BuiltInPersonasTests`: no built-in persona samples at greedy 0.
