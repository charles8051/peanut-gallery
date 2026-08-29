# Run metrics: persistent per-run telemetry + a dogfooding report

**Status:** Implemented · **Issue:** resolves [#62](https://github.com/charles8051/peanut-gallery/issues/62)

## Problem

Every substantial reliability decision this tool has made was driven by numbers hand-extracted from
ephemeral GitHub Actions run logs: empty-reply rate by diff size (#109), `finish_reason:"error"`
frequency (#113), the verification pass's 44–67% refutation rate (#101). `RunSummary` already renders
those facts **per run** into the Job Summary — but that surface is ephemeral (bounded by run-log
retention) and **not aggregatable**. Answering "how flaky is the panel this week, and which model is
the culprit?" meant grepping N run logs by hand. This feature persists the per-run facts and makes
them aggregatable, so dogfooding is a query, not archaeology.

## Design: the comment is the datastore (again)

Consistent with stateful sessions ([stateful-sessions](../stateful-sessions/spec.md)), the store is a
PR comment — no external backend, no new permissions (the review already has `pull-requests: write`),
no runner-side state (runners are ephemeral; artifact quota is exhausted).

- **One `metrics` comment per PR**, marked `<!-- peanut-gallery:metrics -->` so `CommentSync` upserts
  it in place. Unlike a session (one current state) it is **append-only**: each run adds one JSON
  line to a hidden, base64'd JSONL block.
- **Bounded by rendered size, not by line count (#189).** `GitHubCommentLimit` (65536) minus
  `Headroom` (4096) gives `BodyBudget` (61440), and the oldest lines roll off until the rendered body
  fits it. `DefaultCap` (250 lines) stays as a secondary ceiling for lines small enough that hundreds
  would fit the budget; on realistic lines it never binds. Measured: a 5-persona line is 1543 bytes,
  so the ledger holds ~29 runs in ~60,168 chars. Sizing is arithmetic (base64 length is a function of
  the raw byte count, and eviction only removes from the front, so the trailing summary is stable),
  not a re-render per candidate line.
- **Eviction is disclosed, and the newest line is never the one dropped.** Past the first roll-off the
  header reads "N runs shown; at least M older runs have rolled off … so this is a partial history",
  the count rides in a hidden `<!-- pg-metrics-evicted:M -->` marker so it stays cumulative across
  appends, and `MetricsLedger.EvictedCount` exposes it to the shell (`metrics` prints the total on
  stderr rather than folding windowed corpora in as whole ones). The count is a **lower bound**: a
  ledger written before the marker existed cannot say what it had already dropped. A line too large
  for the budget on its own is kept and the upsert fails visibly, rather than posting a ledger that
  successfully records nothing.
- Every run also prints a `PG_METRICS {json}` line to stdout — self-documenting logs, and the local
  `--preview` path (which posts nothing) still emits it.

## Layers (functional core / imperative shell)

**Core (pure, exhaustively tested):**
- `RunMetrics` / `PersonaMetric` / `RunContext` — the immutable value. Run-level totals (degraded,
  posted, refuted, tokens, slowest) are **derived getters**, so the record cannot disagree with itself.
- `FailureClassifier.Classify(reason)` → `FailureClass` (None / Timeout / FinishReasonError /
  EmptyReply / Transient / Config / Other) — buckets a failure by its reason text.
- `MetricsCodec` — one compact JSON line per run, reflection-free; forward-compatible reader drops
  unreadable lines rather than failing.
- `MetricsLedger` — append/extract over the comment body; carries the `CommentSync` marker; bounds the
  history by rendered size and discloses what rolled off.
- `MetricsReport` — the pure fold across runs into per-model and per-persona rows (failure rate, top
  failure class, refute rate, p50/p95 latency, token cost) + a plain-text renderer.

**Engine (pure fold reading result types, like `RunSummary`):**
- `MetricsCollector.From(ReviewRunResult, RunContext)` → `RunMetrics`.

**Shell (IO):**
- `review-pr` builds the value (shell stamps the timestamp — no clock in the core), emits the stdout
  line, and appends to the PR's ledger comment (best-effort; a metrics write never fails a review).
- `peanut-gallery metrics --slug <repo> [--since <days>] [--state open|closed|all] [--json]` scrapes
  the per-PR ledgers and prints the report.

## What is captured

Per persona, per run: id, name, lens, model, tier, outcome, latency, tokens (review + verify split),
the **raised → posted → refuted → suppressed** funnel, failure class, and **attempts** (total model
calls on the review path — >1 means the retry loop re-routed past a transient failure). These are
exactly the fields the session's reliability + verification-pass decisions turned on.

**Cached input tokens.** `PersonaMetric.CachedInputTokens` / `VerifyCachedInputTokens` capture
`Microsoft.Extensions.AI.UsageDetails.CachedInputTokenCount` (the SDK's typed mapping of the
OpenAI-compatible `usage.prompt_tokens_details.cached_tokens` wire field), read in
`ChatClientReviewer.Meter`. This is a **subset of `InputTokens`, not additional spend** — a
provider bills a cache hit at a discount but still counts it in `prompt_tokens`, so it is never
added into `ModelUsage.Total`. `RunMetrics.CacheHitRate` and `MetricsReport.Row.CacheHitRate`
derive the share (`null` when there are no input tokens to divide by, matching the codebase's
"nothing to report" convention elsewhere rather than a misleading 0%). Rendered as a `(N cached)`
suffix on the per-persona token cell and a "X% of input tokens were cache hits" clause on the
run-total spend line in the Job Summary, and as a `cache%` column in the CLI's `metrics` table.
Both fields default to 0 on read, so ledger lines written before this field existed parse as
"no cache hit reported" rather than failing — the same forward-compat contract as `Attempts`.

**What the first read of this metric found (and changed).** One production ledger, scraped over the
51 runs following the field shipping, reported **0.0% cache hits across 1.14M review-path input
tokens** against **95.5% on the verify path**. The asymmetry was structural, not a provider quirk:
the verify pass re-sends its own prompt seconds later, so its prefix is warm, while the review call
put the *persona's* system message at token zero — so a run's N personas produced N distinct
prefixes over one identical diff and nothing could ever be shared. `SessionPlanner` had a comment
claiming the persona prompt was kept first "so provider prompt-caching can warm it"; the measurement
showed that intent never materialised, because what a persona shares across turns (its own short
prompt) is below the provider's minimum cacheable prefix, while what a run shares across personas
(the diff) is far above it.

The fix was to invert the turn order — persona-independent block first, persona system message last
— in both folds. The diff **stays in the user turn**; moving it into the system message would also
have made the prefix shared, but repo-derived text must never hold the highest-authority position in
the prompt (see `ConventionsNote`), and reordering buys the same cache without touching that
boundary. A direct provider probe against `openai/gpt-5.6-luna` confirmed the ordering survives to
the cache: two personas over one shared block reported 0% under the old order and **99.8% on the
second call** under the new one.

**How far the invariant reaches.** The cache rests on a run's personas producing a byte-identical
user block, and that holds **on first turns**, where the builders take no `Persona` at all so the
compiler enforces it (`PromptAssembly.BuildUserPrompt`, `SessionPlanner.BuildFirstUser`; asserted by
`Two_personas_on_one_first_turn_produce_a_byte_identical_user_block` in both test classes). It does
**not** hold in general on continued turns: `BuildContinuedUser` takes the persona's `ReviewSession`
prior, and the running summary, open findings and dropped titles diverge across personas the moment
any finding or refutation lands — and they sit *ahead* of the shared delta diff in the block, so the
prefix splits early. Continued turns therefore keep only the same-persona warmth the verify pass
already had. Reordering the continued-turn block to put the shared delta first is the obvious
follow-up; it is deliberately not done here, because whether it is worth the churn depends on the
first-turn-vs-continued-turn mix in the ledger, which is now measurable.

**What the second read found: `MalformedResponse` (#158).** A week's ledgers across two production
repositories showed a 1.7% degrade rate whose `Other` bucket was 28 of 32 the *same* failure — an
`ArgumentOutOfRangeException` on parameter `index`, thrown while the SDK mapped the reply, before any
usage was metered (`in=0, out=0`, `attempts=1`). It cost whole panels: one PR reviewed once,
then reported nothing on four consecutive pushes and merged on a stale board. Reproduced against the
real client pipeline over canned wire JSON: the trigger is a completion carrying **no choices at
all**, which is how OpenRouter reports an upstream generation failure it could not route
(`{"error":{…},"choices":[]}`). A genuinely empty *reply* maps fine and still reaches the #109
shrink-retry ladder — the two must not be conflated, which is why this has its own class rather than
folding into `EmptyReply`.

This is #113's sibling — the same upstream failure, reported on the wire instead of as
`finish_reason:"error"` — and it was fatal for the same reason #113 once was: `TransientFailure` did
not recognise it, so the retry that would have re-routed never fired. The fix retries it and names
it. **The shape is recognised only at the model-call boundary**, where the SDK's mapping is the one
thing in scope, and re-raised as a `MalformedResponseException`; the retry predicate and the metrics
class then read a *type*. That bound matters more than it looks: a parameter name is not evidence of
origin, so a wider match would retry an out-of-range bug of our own and then report it as a provider
fault. `FailureClassifier` gets **no arm at all** for this class, for the same reason one layer up:
any substring it could match — including our own boundary wording — is text an unrelated failure may
also carry. Ambiguous text stays `Other`, which is the honest answer when origin is unknowable, and
pre-fix ledger lines stay where they were rather than being retroactively relabelled.
`SdkResponseMappingTests` holds the wire shape down so an SDK upgrade that changes it fails in CI
rather than in production a week later.

The general lesson for this feature: **an `Other` bucket that grows is a defect report, not a
long tail.** The report renders `(top: Other)` and stops; the ledger had the answer all along.

**Attempts → recovered vs exhausted.** `ModelReply.Attempts` (from `RetryingModelCall`) is summed
across the review path onto `PersonaObservability.Attempts`, folded into `PersonaMetric.Attempts`,
and the report derives *calls/review* and a *recovery rate* (of the reviews that needed a retry, the
share that then succeeded). This is the direct measure of whether the #111 / #114 retry fixes are
actually rescuing reviews or just burning calls — the question that motivated the whole telemetry.

**The author's verdict (#186).** Every other count here is the tool grading its own work: `raised`,
`posted`, `refuted`, `suppressed` are all decisions the panel made about itself. `PersonaMetric.Resolved`
and `Withdrawn` are the two that carry a human's judgement — titles the author fixed, and titles the
author explained away as intentional or wrong. Both were already computed each turn on
`PersonaContribution` and thrown away; `MetricsCollector` now reads them off the same contribution it
already reads `Posted`/`Refuted`/`Suppressed` from. Ledger keys `rs` / `wd`, appended at the tail with
defaults, same forward-compat contract as `Attempts` and `CachedInputTokens`.

They matter because `refute%` on its own is ambiguous: verification refuting more findings could mean
it is correctly killing false ones or over-refuting true ones, and nothing in the ledger could tell
those apart. #171, #173 and #181 all shipped to reduce false positives, into a ledger with no
false-positive signal at all.

**It is named `agree%`, not `precision%`.** `MetricsReport.Row.AgreementRate` is
`resolved / (resolved + withdrawn)` — of the findings the author *ruled on*, the share they acted on
rather than waved away. An author can explain away a finding that was right, and can "resolve" a title
by changing something unrelated to it, so neither verdict establishes truth. Calling it precision would
be exactly the overstatement [the `scope` A/B](../finding-scope/ab-finding-scope.md) cost 96 calls to
establish is expensive. The caveat is printed above the table by `AppendAuthorVerdicts`, beside the
number, not left in this file.

**A pre-schema line is not a zero.** `RunMetrics.Schema` moves to `2` and the parsed record carries the
version the *line* was written at (`RunMetrics.SchemaVersion`, read from the `v` key the codec already
wrote), so `RecordsAuthorVerdicts` separates "the author ruled on nothing" from "nobody wrote it down".
`MetricsReport` excludes pre-verdict persona-reviews from the ratio and states how many it excluded;
a row with no ruled-on finding renders `—`, never `0%`. This is the same failure `RunContext.Shape`
already documents, where collapsing "not recorded" into a factual zero silently disabled the whole
measurement. `WriteLine` emits `m.SchemaVersion` rather than the build constant, so re-writing a
historical line cannot launder its absences into recorded zeros.

## Deliberately out of scope

- **A severity histogram (#186's secondary, split out).** `c.Posted` is `IReadOnlyList<Finding>`, so
  severity sits at the same seam, and #171 moved the severity rubric with nothing able to see whether
  `major` counts shifted. It is not in the verdicts change, on measured grounds: a realistic 5-persona
  line is **1542 bytes**, and the ledger body at `MetricsLedger.DefaultCap` (250 lines) measures
  **514,657 chars — 7.8x GitHub's 65,536-char comment limit**, which the cap's own comment ("a metrics
  line is a few hundred bytes") assumes it is under. Verdicts cost 4.5% of a line; four severity counts
  would cost ~9% more of a budget that is already the binding constraint. The cap wants fixing (cap by
  bytes, not lines) before more per-persona keys are added — filed as #189, with #190 for the
  histogram itself, blocked on it. **#189 has since shipped** (see the byte bound above): the ledger no
  longer loses itself, but the budget is still what four more ints per persona row are spent from —
  ~9% of a line is ~2.5 runs of the ~29 that fit. #192 measured the obvious saving (the repeated
  `nm`/`ln`/`md`/`tr` values are 18% of a line, worth ~6 runs) and did not build it.
- **Verify-path attempts.** Only the review path's retries are counted; the adversarial pass's own
  call is not folded into the attempt total (it rarely retries in a way that changes the recovered-
  vs-exhausted question). Cheap to add if it proves interesting.
- A central store / dashboard. The per-PR ledger + `metrics` verb is the 90%; a scheduled job that
  posts the report to a tracking issue on a cadence is a cheap future add.
- A trimmed-diff / reduced-coverage flag (relates to #111's known limitation).
