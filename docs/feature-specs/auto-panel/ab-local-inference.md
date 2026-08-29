# A/B evaluation: cloud finders vs a local V100

**Date:** 2026-07-24 · **Feature:** [Auto panel](spec.md) · [ADR](adr.md) ·
**Companion:** [fixed vs auto (#70)](ab-evaluation.md)

## Question

Can a self-hosted GPU (a Tesla V100-32GB serving Ollama behind LiteLLM) replace paid hosted
models as the panel's **finders**, while `minimax-m3` keeps doing the orchestration? Reviews are
free and unmetered on hardware you already own, so if the quality holds this removes the tool's
per-review cost entirely.

## Setup

The tool needed **no code change**. A provider block and the existing `personaModel` knob were
enough:

```json
{"name": "local-v100", "baseUrl": "http://<litellm-host>:4000/v1", "apiKeyEnv": "LITELLM_API_KEY"}
```

A LiteLLM virtual key scoped to the chat models you want gives per-key token accounting.
`ChatClientPanelPlanner` already takes `orchestratorModel` and `personaModel` separately, so
"one model convenes, the local GPU reviews" is pure configuration.

**Reachability from CI is worth checking before you start.** Reviews run **inside a Docker
container**, which does not necessarily resolve names the way its host does. In *this* setup the
host used a `127.0.0.53` systemd stub resolver that the container did not inherit; Docker's
embedded resolver forwarded to a different upstream, and a name that failed to resolve on the
runner resolved inside the container. That is an observation about one configuration, not a rule:
Docker's embedded DNS forwards to whatever the daemon or container is configured with, which on
another machine may preserve split-horizon, VPN, or private DNS exactly as the host sees it.

Two cautions if you go looking for the same effect. Resolution is not connectivity - a name that
resolves still has to be routable from the container. And a resolver difference is not a network
policy: if your runner cannot reach the inference host by design, the fix is a route, not a DNS
quirk. A direct route from runner to inference host is the better arrangement anyway - private,
lower latency, one less dependency.

## Method

Same three diffs as [#70](ab-evaluation.md), reconstructed **as first pushed** (`refs/pull/N/head`,
each branch's first commit) so the known bugs are still present: #82 `1a4bddf` (path traversal),
#84 `b990b76`, #90 `0b4f8e5` (forged pin trusted).

**36 runs**: 2 panel modes (`fixed`, `seedAndAuto`) x 2 arms x 3 diffs x **3 repetitions**. The
repetition count is deliberate - #70's central caveat was that run-to-run variance rivalled the
effect it measured, so n=1 could not support a claim.

Everything is held constant except the finder model. `minimax-m3` orchestrates **both** arms.

| | cloud arm | V100 arm |
|---|---|---|
| Architect | `minimax/minimax-m3` | `qwen3-coder-30b` |
| Bug Hunter | `deepseek/deepseek-chat` | `qwen3-coder-30b` |
| Convened personas (`personaModel`) | `deepseek/deepseek-chat` | `qwen3-coder-30b` |
| Orchestrator | `minimax/minimax-m3` | `minimax/minimax-m3` |

Run with `review-pr --preview` (real models, nothing posted).

## Result 1: the orchestration half works perfectly

`minimax-m3` reads the diff and convenes exactly the right adversaries, and those personas run on
the V100 without complaint. On #82 it independently chose `path-containment` / `path-traversal` /
`prompt-injection` / `secret-disclosure` across reps; on #90, `pin-injection`, `trust-boundary`,
`llm-supply-chain`, `external-state-trust`.

The orchestrator is **one cheap call per PR** and it is not the weak link. Nothing below is a
criticism of the auto-panel design.

## Result 2: aggregate counts, which mislead on their own

| mode | arm | n | raised | posted | refuted | judge kill-rate | s/run |
|---|---|---:|---:|---:|---:|---:|---:|
| fixed | cloud | 9 | 18 | 15 | 3 | 16.7% | 407 |
| fixed | V100 | 9 | 19 | 12 | 7 | 36.8% | **38** |
| auto | cloud | 9 | 32 | 24 | 8 | 25.0% | 163 |
| auto | V100 | 9 | 34 | 17 | 17 | **50.0%** | 215 |

By volume the arms look comparable. **Volume is the wrong metric** - the arms are not finding the
same class of thing.

## Result 3: the cloud arm finds bugs; the V100 arm applies labels

Posted findings, cloud (representative, all specific and checkable):

- `FindPin honours a forged pin from any comment, not just our bot's` **(the real #90 security hole)**
- `ReadFileContextAsync uses unsafe StartsWith for path containment (sibling-directory traversal)`
  **(#82's actual root cause, named precisely - not just "there is a traversal here")**
- `Prompt injection and symlink traversal risk in ReadFileContextAsync` **(this is #91, the second
  traversal vector that #82's fix did not close)**
- `ResolvePanelAsync is not total: planner/DeltaSource exceptions sink the run` (this is #92)
- `budgetBytes is measured in chars, not bytes`
- `ContextNote silently drops Omitted when Kept is empty, breaking the 'disclosed, never silent' contract`
- `Latency test expects a log line the implementation never emits`
- `Verdict.Why is parsed but never surfaced`
- `personaModel silently falls back to the orchestrator model`
- `No tier demotion for invented personas in a pin`

Posted findings, V100 - the same nine runs:

- `Layering violation: IO in pure core function` (x2), `Layering Violation: IO in Core Layer`,
  `Layering Violation: IO in Functional Core`, `Layering violation: IO in functional core context`,
  `State fusion: mixing IO, state and timing in verification flow`, `Leaky abstraction: direct
  async call in core logic` ...
- `Off-by-one error in pluralization for refuted findings disclosure` (x3)
- `Missing INDEX.md entry for adversarial verification feature` - rated **critical**

Two failure modes, both verified against source rather than asserted:

**The layering findings are backwards.** Every one of them points at `ReviewRunner.cs` or
`Commands.cs` - which live in `PeanutGallery.Engine` and `PeanutGallery.Cli`, i.e. **the shells**,
where IO is not merely allowed but mandatory. The model read the injected repo conventions
(functional core / imperative shell), learned the vocabulary, and applied the rule inverted. Several
bodies visibly waver mid-sentence ("it's not clear if this is truly pure... however, more
importantly...") and then assert the finding at **confidence 1.0** anyway.

**The bug-hunter fills in a template.** On all three diffs it produced exactly one finding titled
"Off-by-one error in X", where X is whatever could carry the label. The persona prompt opens its
list of bug classes with "off-by-one", and the weaker model treats the list as a form:

| diff | claim | verdict |
|---|---|---|
| #82 | `info.Length > MaxContextFileBytes` should be `>=` | **False.** The doc comment three lines above the constant says "past this the diff has to speak for itself" - `<=32KB` passing is the documented intent. |
| #84 | `Refuted.Count == 1 ? "finding" : "findings"` mishandles count 0 | **False on both premise and claim.** Line 33 guards `Refuted.Count: > 0`, so count 0 is unreachable; and the ternary is correct. |
| #90 | "Off-by-one error in persona model selection" | **Miscategorised.** The body describes a fallback-precedence concern. Not an off-by-one at all - the title is a category label bolted onto an unrelated observation. |

All three at confidence 1.0, all three reproduced in 3/3 reps.

**Confidence is not usable as a gate here.** The V100 self-rates essentially everything 0.95-1.0,
so the `minConfidence` gate (default 0.6) suppressed **nothing** in 36 runs. The cloud arm spread
itself 0.7-0.9 on the same diffs.

## Result 4: the local judge is anti-correlated with truth (the important one)

On #82 with `seedAndAuto`, the V100 arm posted **zero** findings in all three reps. It was not
failing to find the bug. Decoding the session state:

| rep | convened lens | raised | posted |
|---|---|---|---|
| r1 | `path-containment` | 3 | **0** |
| r2 | `path-traversal` - raised `Path traversal vulnerability via insufficient containment check` @1.0 | 3 | **0** |
| r3 | `secret-disclosure` | 2 | **0** |

**A 100% refutation rate, and the finding it destroyed in r2 was the real bug the PR existed to
introduce.**

Re-running the identical configuration with `verify: false` confirms the finder was never the
problem. Every rep surfaces the traversal, several times over:

```
Path Traversal Vulnerability in ReadFileContextAsync
Potential Path Traversal Vulnerability in ReadFileContextAsync        (x2)
Potential directory traversal vulnerability in ReadFileContextAsync
Path Traversal via Insufficient Separator Boundary Check
```

That last phrasing is sharper than the label the human review used - the string-prefix boundary was
#82's actual root cause.

So the local judge is worse than no judge: it destroyed the correct security finding in 3/3 reps
while passing `Off-by-one error in pluralization` (verifiably false) on the fixed panel. The cloud
arm refuted 0 findings across nine `fixed` runs.

> **Correction (2026-07-24).** That last sentence is true of this sample and **misleading as a
> generalisation** - it framed over-refutation as a weak-local-model pathology. Production data says
> otherwise. Across production runs on **three** larger private codebases, two per-repository
> measures both ran high:
>
> - **refutation rate** — refuted findings over findings raised — **46.2% to 66.7%**;
> - **whole-output wipe rate** — verification passes that discarded a persona's entire output,
>   over verification passes — **60.9%** on the highest of the three.
>
> Different denominators, so the two are not comparable with each other; both are per-repository,
> not aggregates across the three. Run counts and raw ledgers are on
> [#101](https://github.com/charles8051/peanut-gallery/issues/101), which also carries a per-model
> breakdown this document does not reproduce. The reason this evaluation did
> not see it is that the `fixed` arm only
> ran the two **seed** personas, whose correctness/architecture lenses clear the upheld-bar far more
> often; the wipes concentrate in the orchestrator-convened non-correctness lenses, which cannot
> clear it by construction. The V100's judge is worse in degree, not different in kind.

The verification pass reuses **the finder persona's own model** - `VerifyAsync` builds its request
from that persona's `ReviewRequest`, and there is no `verifierModel` knob. The auto-panel ADR's
Decision 5 already specced the opposite ("**dynamic finders, fixed judge**"); that half was never
built. Filed as [#100](https://github.com/charles8051/peanut-gallery/issues/100).

## Result 5: it is not this model's fault specifically

`qwen2.5-coder-32b` on the same #82 diff is **worse**: it emitted `Potential off-by-one in path
resolution` **four times** and `Error handling in file reading` twice in a single run, alongside
three near-identical `Potential leaky abstraction` findings. Duplicate emission within one persona
is a degenerate mode the cloud models never showed. `qwen3-coder-30b` is the better of the two
local coders, and it is the one measured above.

(This probe was cut short at 7 of 12 planned runs once the direction was unambiguous, to stop
occupying the GPU. Treat it as indicative, not measured.)

## Result 6: speed, and what actually causes it

The `fixed` V100 arm averaged **38 s/run** against the cloud arm's **407 s/run** - but attributing
that to "local vs cloud" would be wrong. The long pole is one model: `minimax-m3` took 300-460 s per
review, while `deepseek-chat` finished in 4-16 s and `qwen3-coder-30b` in 4-50 s. Ollama runs
`OLLAMA_NUM_PARALLEL=1`, so the V100 *serialises* a 4-persona auto panel where the cloud fans out -
which is why the V100's auto-mode advantage (215 s vs 137 s) inverts.

The honest version: **the V100 is competitive on latency and free, and the cloud arm's slowness is a
model choice we could fix independently.**

## Conclusion

**Do not move the finders to the V100.** The quality gap is not marginal. On the two diffs with the
sharpest ground truth (#82, #90) the V100 arm posted nothing usable across 12 runs. On the *same*
#82 diff the cloud auto arm named the string-prefix root cause and the symlink vector (#91) that
#82's fix did not close, and on #90 it found the forged-pin hole and the totality bug. What the
V100 does produce is
confident, reproducible, and wrong - which is worse than silence, because a reviewer that files
`Layering violation` against the shell layer three times a week trains people to ignore it.

**What is worth doing:**

1. **[#100](https://github.com/charles8051/peanut-gallery/issues/100) - add `verifierModel`.** This
   is the finding with real value. The local finders demonstrably surface real bugs (Result 4); it
   is the local *judge* that destroys them. `N` free local finder calls plus **one** strong cloud
   judge call is a genuinely attractive shape, and today it is not expressible.
2. **Keep the box wired up as a provider.** It costs nothing to leave configured, the CI path is
   verified working, and it is the substrate any re-test needs.
3. **Re-test when the weights change.** The finder signal is there. The gap is calibration and
   instruction-following, which is exactly what improves between model generations.

## Caveats

- n=3 per cell on 3 diffs, one repo, one local model. Better than #70's n=1, still small.
- Ground truth is "what the panel found on the real PR, since confirmed" - it under-counts bugs
  nobody has found yet, so a novel true finding could be scored as noise. The adjudications in
  Result 3 were checked against the source at those commits, not assumed.
- The V100 arm was measured while the box was otherwise idle for the `fixed` runs; the `nv` and
  `alt` probes overlapped and contended, so their **latencies** are not comparable. Their
  **findings** are unaffected.
