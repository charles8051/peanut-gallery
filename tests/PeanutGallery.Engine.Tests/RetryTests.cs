using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

public class RetryScheduleTests
{
	private static readonly TimeSpan Budget = TimeSpan.FromSeconds(600);
	private static readonly TimeSpan First = TimeSpan.FromSeconds(240);

	[Fact]
	public void Two_attempts_fail_fast_then_full_budget()
	{
		var s = RetrySchedule.For(Budget, maxAttempts: 2, First);
		Assert.Equal(new[] { TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(600) }, s);
	}

	[Fact]
	public void Single_attempt_is_just_the_budget()
	{
		var s = RetrySchedule.For(Budget, maxAttempts: 1, First);
		Assert.Equal(new[] { Budget }, s);
	}

	[Fact]
	public void Escalates_but_last_is_always_the_full_budget()
	{
		var s = RetrySchedule.For(Budget, maxAttempts: 3, First);
		Assert.Equal(new[] { TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(480), TimeSpan.FromSeconds(600) }, s);
	}

	[Fact]
	public void Non_final_attempts_never_exceed_the_budget()
	{
		// first (240s) > budget (120s) -> clamped so no attempt waits longer than the whole budget.
		var s = RetrySchedule.For(TimeSpan.FromSeconds(120), maxAttempts: 2, First);
		Assert.All(s, t => Assert.True(t <= TimeSpan.FromSeconds(120)));
	}

	[Fact]
	public void Zero_or_negative_attempts_degrade_to_one()
	{
		Assert.Single(RetrySchedule.For(Budget, maxAttempts: 0, First));
	}

	[Fact]
	public void The_default_per_call_budget_fails_the_first_attempt_fast_then_gives_the_final_the_full_call_budget()
	{
		// The per-call budget (300s, raised so a legitimately long large-diff review is not cut off)
		// now sits ABOVE the 240s first-rung, so the schedule is [240s, 300s]: the first attempt still
		// fails fast at 240s, the final gets the full 300s per-call budget - and both stay well under
		// the 600s turn wall, which remains the outer backstop (never the final-attempt ceiling).
		var s = RetrySchedule.For(TimeSpan.FromSeconds(ReviewBudget.CallDefaultSeconds), maxAttempts: 2, First);
		Assert.Equal(new[] { TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(300) }, s);
	}
}

public class RetryingModelCallTests
{
	// A schedule long enough that the per-attempt TimeBox never fires in these logic tests.
	private static readonly IReadOnlyList<TimeSpan> TwoAttempts =
		new[] { TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30) };

	private static Task NoDelay(int _, CancellationToken __) => Task.CompletedTask;

	[Fact]
	public async Task Succeeds_on_the_first_attempt_without_delaying()
	{
		var delays = 0;
		var result = await RetryingModelCall.RunAsync(
			_ => Task.FromResult("ok"), TwoAttempts, TransientFailure.IsRetryable,
			(_, _) => { delays++; return Task.CompletedTask; });

		Assert.Equal("ok", result.Text);
		Assert.Equal(1, result.Attempts);
		Assert.Equal(0, delays);
	}

	[Fact]
	public async Task Retries_a_transient_failure_then_succeeds()
	{
		var calls = 0;
		var delays = 0;
		var result = await RetryingModelCall.RunAsync(
			_ =>
			{
				calls++;
				return calls == 1 ? throw new TimeoutException("slow route") : Task.FromResult("recovered");
			},
			TwoAttempts, TransientFailure.IsRetryable,
			(_, _) => { delays++; return Task.CompletedTask; });

		Assert.Equal("recovered", result.Text);
		Assert.Equal(2, result.Attempts);
		Assert.Equal(2, calls);
		Assert.Equal(1, delays);
	}

	[Fact]
	public async Task Retries_an_unknown_finish_reason_from_the_sdk_then_succeeds()
	{
		// End-to-end through the REAL predicate: a finish_reason:"error" (unknown ChatFinishReason)
		// on the first attempt re-issues, and OpenRouter's re-route lands a real answer. (#113)
		var calls = 0;
		var result = await RetryingModelCall.RunAsync(
			_ =>
			{
				calls++;
				return calls == 1
					? throw new ArgumentOutOfRangeException("value", "error", "Unknown ChatFinishReason value.")
					: Task.FromResult("""{"summary":"s","findings":[]}""");
			},
			TwoAttempts, TransientFailure.IsRetryable, NoDelay);

		Assert.Equal(2, result.Attempts);
		Assert.Equal(2, calls);
	}

	[Fact]
	public async Task Retries_a_reply_the_sdk_could_not_map_then_succeeds()
	{
		// The #113 sibling on a different wire form: a completion carrying NO CHOICES, raised at the
		// call boundary as a MalformedResponseException. Before #158 this was fatal, so a route that
		// returned nothing usable cost the persona its whole turn - four consecutive pushes on
		// Observed panel-wide, never retried once.
		var calls = 0;
		var result = await RetryingModelCall.RunAsync(
			_ =>
			{
				calls++;
				return calls == 1
					? throw new MalformedResponseException("no choices", new ArgumentOutOfRangeException("index"))
					: Task.FromResult("""{"summary":"s","findings":[]}""");
			},
			TwoAttempts, TransientFailure.IsRetryable, NoDelay);

		Assert.Equal(2, result.Attempts);
		Assert.Equal(2, calls);
	}

	[Fact]
	public async Task An_exhausted_malformed_reply_is_wrapped_but_still_reads_as_malformed()
	{
		// A route that keeps returning no choices runs out of attempts. The wrapper is what
		// ReviewRunner actually catches in that case, so IsMalformedResponse must see through it -
		// otherwise the very failure this fix names lands back in Other on its worst days. (#158)
		var ex = await Assert.ThrowsAsync<ModelCallException>(() => RetryingModelCall.RunAsync(
			_ => Task.FromException<string>(
				new MalformedResponseException("no choices", new ArgumentOutOfRangeException("index"))),
			TwoAttempts, TransientFailure.IsRetryable, NoDelay));

		Assert.Equal(2, ex.Attempts);
		Assert.True(TransientFailure.IsMalformedResponse(ex));
	}

	[Fact]
	public async Task Exhausted_retries_throw_with_the_attempt_count()
	{
		var calls = 0;
		var ex = await Assert.ThrowsAsync<ModelCallException>(() => RetryingModelCall.RunAsync(
			_ => { calls++; return Task.FromException<string>(new TimeoutException("still slow")); },
			TwoAttempts, TransientFailure.IsRetryable, NoDelay));

		Assert.Equal(2, calls);
		Assert.Contains("after 2 attempts", ex.Message);
		Assert.IsType<TimeoutException>(ex.InnerException);
	}

	[Fact]
	public async Task A_fatal_failure_short_circuits_on_the_first_attempt()
	{
		var calls = 0;
		var delays = 0;
		// Non-retryable -> throws the ORIGINAL exception, unwrapped, without retrying.
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RetryingModelCall.RunAsync(
			_ => { calls++; return Task.FromException<string>(new InvalidOperationException("missing key")); },
			TwoAttempts, TransientFailure.IsRetryable,
			(_, _) => { delays++; return Task.CompletedTask; }));

		Assert.Equal(1, calls);
		Assert.Equal(0, delays);
		Assert.Equal("missing key", ex.Message);
	}

	[Fact]
	public async Task Outer_cancellation_propagates_and_stops_retrying()
	{
		using var cts = new CancellationTokenSource();
		var calls = 0;
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RetryingModelCall.RunAsync(
			_ =>
			{
				calls++;
				cts.Cancel();
				throw new OperationCanceledException(cts.Token);
			},
			TwoAttempts, TransientFailure.IsRetryable, NoDelay, cts.Token));

		Assert.Equal(1, calls); // never retried after cancellation
	}

	[Fact]
	public async Task Per_attempt_timebox_fires_and_is_retried()
	{
		// A tiny per-attempt budget: attempt 1 overruns -> TimeBox TimeoutException -> retry; attempt 2 is fast.
		var schedule = new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5) };
		var calls = 0;
		var result = await RetryingModelCall.RunAsync(
			async token =>
			{
				calls++;
				if (calls == 1)
				{
					await Task.Delay(TimeSpan.FromSeconds(5), token); // overruns the 50ms box
				}

				return "fast";
			},
			schedule, TransientFailure.IsRetryable, NoDelay);

		Assert.Equal("fast", result.Text);
		Assert.Equal(2, result.Attempts);
	}
}
