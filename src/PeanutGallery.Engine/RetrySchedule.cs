using System;
using System.Collections.Generic;

namespace PeanutGallery.Engine;

/// <summary>
/// Pure: the per-attempt timeout schedule for a retried model call. The key property is that
/// the <b>last</b> attempt always gets the full <paramref name="budget"/>, so a legitimately
/// slow-but-valid call (a big diff a model genuinely needs the whole budget for) never
/// regresses relative to a single un-retried call. Earlier attempts get a shorter, escalating
/// deadline (<paramref name="firstAttempt"/>, doubling) so a hung route is abandoned quickly
/// and cheaply — the common flake — and re-issued before the budget is spent. The
/// <paramref name="budget"/> here is the PER-CALL ceiling (<c>PG_CALL_TIMEOUT_SECONDS</c>, default
/// 300s), split from the whole-turn budget (issue #133). With the default two attempts and the 300s
/// per-call budget this yields <c>[240s, 300s]</c>: the first attempt still fails fast at the 240s
/// first-rung, the final gets the full per-call budget so a legitimately long review is not cut off —
/// and both stay under the 600s turn wall, which is the outer backstop, never the final-attempt ceiling.
/// </summary>
public static class RetrySchedule
{
	public static IReadOnlyList<TimeSpan> For(TimeSpan budget, int maxAttempts, TimeSpan firstAttempt)
	{
		if (maxAttempts < 1)
		{
			maxAttempts = 1;
		}

		if (budget <= TimeSpan.Zero)
		{
			budget = firstAttempt > TimeSpan.Zero ? firstAttempt : TimeSpan.FromMinutes(1);
		}

		var schedule = new TimeSpan[maxAttempts];
		for (var i = 0; i < maxAttempts; i++)
		{
			if (i == maxAttempts - 1)
			{
				// Final attempt: the full budget — no legitimately-slow call regresses.
				schedule[i] = budget;
				continue;
			}

			// Escalating short deadline for the fail-fast attempts, never exceeding the budget.
			var escalated = TimeSpan.FromTicks(firstAttempt.Ticks << i);
			schedule[i] = escalated < budget ? escalated : budget;
		}

		return schedule;
	}
}
