using System;
using System.Globalization;

namespace PeanutGallery.Engine;

/// <summary>
/// Pure: how long a review may take, and how many times a call may be re-issued. One place, so
/// every shell (CLI, desktop, a future server) reads the same knobs the same way and the defaults
/// cannot drift apart.
///
/// <para><b>Two nested budgets.</b> The turn budget (<c>PG_REVIEW_TIMEOUT_SECONDS</c>, default 600s)
/// is the ceiling on a whole persona turn — all its attempts and backoff — enforced by the
/// <see cref="ReviewRunner"/>'s per-persona <see cref="TimeBox"/> (issue #117: before that, nothing
/// shared a clock and a persona was observed still running at 616s under a 600s setting). Nested
/// inside it, the per-call budget (<c>PG_CALL_TIMEOUT_SECONDS</c>, default 300s) is the ceiling on a
/// single model attempt, fed to <see cref="RetrySchedule"/>.</para>
///
/// <para><b>Why they are split</b> (issue #133). They used to be the same value, so <see cref="RetrySchedule"/>
/// handed the final attempt the full 600s and one hung call spent the entire turn with no room to retry.
/// A smaller per-call ceiling bounds <i>every</i> attempt, so a hung call is abandoned and the retry gets
/// a fresh shot (new sampling) inside the same turn budget. The 300s ceiling is sized WITH the output cap
/// (<see cref="DefaultMaxOutputTokens"/>): a full 40k-token review runs ~210s, so 300s keeps the token
/// cap — not the clock — the real ceiling on a legitimately long review; the turn budget remains the
/// outer backstop.</para>
/// </summary>
public static class ReviewBudget
{
    /// <summary>Turn budget when <c>PG_REVIEW_TIMEOUT_SECONDS</c> is unset or unusable. This is the
    /// ceiling on a whole persona turn (all attempts + backoff), enforced by the runner's per-persona
    /// <c>TimeBox</c>. It is the outer backstop, NOT the per-attempt ceiling — see
    /// <see cref="CallDefaultSeconds"/>.</summary>
    public const int DefaultSeconds = 600;

    /// <summary>Per-call budget when <c>PG_CALL_TIMEOUT_SECONDS</c> is unset or unusable. This is the
    /// ceiling on a SINGLE model attempt, split from the turn budget so a runaway attempt is abandoned
    /// early and a fresh attempt (new sampling) runs inside the same turn budget rather than one hung
    /// call burning the whole thing.
    /// <para>Raised 180s → 300s so it stays ABOVE the token cap, not below it: at minimax-m3's ~190
    /// tok/s on Fireworks a full 40k-token review takes ~210s, so a 180s call ceiling would have
    /// converted a legitimately long review into a timeout before it could finish. 300s lets the
    /// output cap (<see cref="DefaultMaxOutputTokens"/>) be the real ceiling on a real review, while
    /// still bounding a genuinely hung call well under the 600s turn budget.</para></summary>
    public const int CallDefaultSeconds = 300;

    /// <summary>Attempts when <c>PG_REVIEW_MAX_ATTEMPTS</c> is unset or unusable (1 retry).</summary>
    public const int DefaultMaxAttempts = 2;

    /// <summary>Output-token cap when <c>PG_MAX_OUTPUT_TOKENS</c> is unset or unusable. max_tokens
    /// bounds the TOTAL completion (reasoning + content) for a reasoning model, so this is a ceiling on
    /// how long a single review may be, NOT a runaway guard (the runaway was the provider + sub-spec
    /// temperature, now fixed — see the temperature section of the review-budget spec).
    /// <para>Raised 24576 → 40000 after inspecting the "Truncated" failures on Fireworks + temp 1.0:
    /// they were NOT loops. A large diff (e.g. 23 files) produces a coherent, progressing review whose
    /// reasoning+content genuinely runs ~20-25k+ tokens; the old 24576 cap chopped the higher-variance
    /// runs mid-review, discarding real reviews as "flakes". 40000 fits a thorough large-diff review
    /// with headroom. (The durable fix for very large diffs is chunking — backlog — so no single
    /// review needs this many tokens; until then, the wider cap stops the truncation.)</para></summary>
    public const int DefaultMaxOutputTokens = 40000;

    /// <summary>The env var that sets the whole-turn budget.</summary>
    public const string TimeoutVariable = "PG_REVIEW_TIMEOUT_SECONDS";

    /// <summary>The env var that sets the per-attempt (single model call) budget.</summary>
    public const string CallTimeoutVariable = "PG_CALL_TIMEOUT_SECONDS";

    /// <summary>The env var that sets the attempt count.</summary>
    public const string AttemptsVariable = "PG_REVIEW_MAX_ATTEMPTS";

    /// <summary>The env var that sets the output-token cap.</summary>
    public const string MaxOutputVariable = "PG_MAX_OUTPUT_TOKENS";

    /// <summary>Opt-in: when truthy, a run that degraded any reviewer exits non-zero so the CI
    /// <c>review</c> check goes red (#130). Off by default — reviews stay advisory (a degraded
    /// persona is disclosed but the run stays green); a repo that wants a partial panel to block
    /// its merge gate sets this. Only <c>1</c>/<c>true</c>/<c>yes</c> (any case) enables it.</summary>
    public const string FailOnDegradedVariable = "PG_FAIL_ON_DEGRADED";

    /// <summary>Total: a missing, blank, non-numeric, zero, or negative value all mean "the default".</summary>
    public static TimeSpan Parse(string? raw) => TimeSpan.FromSeconds(Positive(raw, DefaultSeconds));

    /// <summary>Total, same rule as <see cref="Parse"/>, for the per-call budget.</summary>
    public static TimeSpan CallTimeout(string? raw) => TimeSpan.FromSeconds(Positive(raw, CallDefaultSeconds));

    /// <summary>Total, same rule as <see cref="Parse"/>.</summary>
    public static int Attempts(string? raw) => Positive(raw, DefaultMaxAttempts);

    /// <summary>Total, same rule as <see cref="Parse"/>.</summary>
    public static int MaxOutputTokens(string? raw) => Positive(raw, DefaultMaxOutputTokens);

    /// <summary>Total: only <c>1</c>/<c>true</c>/<c>yes</c> (any case, trimmed) enable it; anything
    /// else — unset, blank, <c>0</c>, <c>false</c>, junk — is off, the safe default.</summary>
    public static bool FailOnDegraded(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" => true,
        _ => false,
    };

    /// <summary>Read the knobs from an env lookup (injected, so this stays testable and total).
    /// <c>Timeout</c> is the whole-turn budget; <c>CallTimeout</c> is the per-attempt budget nested
    /// inside it.</summary>
    public static (TimeSpan Timeout, TimeSpan CallTimeout, int MaxAttempts, int MaxOutputTokens) FromEnvironment(Func<string, string?> env) =>
        (Parse(env(TimeoutVariable)), CallTimeout(env(CallTimeoutVariable)), Attempts(env(AttemptsVariable)), MaxOutputTokens(env(MaxOutputVariable)));

    private static int Positive(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;
}
