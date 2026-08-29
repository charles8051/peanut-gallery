using System;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

public class TimeBoxTests
{
	[Fact]
	public async Task Returns_the_result_when_the_work_finishes_in_time()
	{
		var result = await TimeBox.RunAsync(_ => Task.FromResult("ok"), TimeSpan.FromSeconds(5));
		Assert.Equal("ok", result);
	}

	[Fact]
	public async Task Throws_TimeoutException_when_the_work_is_too_slow()
	{
		await Assert.ThrowsAsync<TimeoutException>(() => TimeBox.RunAsync(
			async token => { await Task.Delay(TimeSpan.FromSeconds(30), token); return "never"; },
			TimeSpan.FromMilliseconds(50)));
	}

	[Fact]
	public async Task Work_that_ignores_its_token_is_still_bounded_by_the_deadline()
	{
		// #121: the real failure mode. A hung model call whose socket read never honours the
		// cancellation token must NOT outlive its budget. Awaiting it directly would block for the
		// full 10s; the deadline race abandons it and returns a TimeoutException promptly.
		var sw = System.Diagnostics.Stopwatch.StartNew();

		await Assert.ThrowsAsync<TimeoutException>(() => TimeBox.RunAsync(
			async _ => { await Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None); return "never"; },
			TimeSpan.FromMilliseconds(100)));

		sw.Stop();
		Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
			$"the deadline must fire regardless of the work honouring cancellation (took {sw.Elapsed})");
	}

	[Fact]
	public async Task Outer_cancellation_propagates_as_OperationCanceledException_not_timeout()
	{
		using var outer = new CancellationTokenSource();
		await outer.CancelAsync();

		// A self-timeout would be a TimeoutException; an outer cancel must stay an OCE
		// so the run tears down cleanly rather than being mistaken for a slow model.
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TimeBox.RunAsync(
			async token => { await Task.Delay(TimeSpan.FromSeconds(30), token); return "never"; },
			TimeSpan.FromSeconds(30),
			outer.Token));
	}

	[Fact]
	public async Task Inner_token_cancellation_becomes_a_timeout_not_an_OperationCanceledException()
	{
		// Regression: the model client's own per-attempt network timeout cancels via an
		// internal token that is NOT our linked cts, so cts.IsCancellationRequested stays
		// false. With no outer cancellation in flight that OCE must be reclassified as a
		// TimeoutException - otherwise it masquerades as outer teardown and faults the
		// whole fan-out instead of degrading to a single-persona failure finding.
		using var inner = new CancellationTokenSource();
		await inner.CancelAsync();

		await Assert.ThrowsAsync<TimeoutException>(() => TimeBox.RunAsync<string>(
			_ => throw new OperationCanceledException(inner.Token),
			TimeSpan.FromSeconds(30)));
	}

	[Fact]
	public async Task Bare_OperationCanceledException_with_no_outer_cancel_becomes_a_timeout()
	{
		// Pin the documented contract for the edge cases: with no outer cancellation in
		// flight, a bare OCE (no token) and a default-token OCE are both self-timeouts and
		// surface as TimeoutException - not OperationCanceledException. A caller that needs
		// cancellation semantics must route it through the outer ct, not throw from work.
		await Assert.ThrowsAsync<TimeoutException>(() => TimeBox.RunAsync<string>(
			_ => throw new OperationCanceledException(),
			TimeSpan.FromSeconds(30)));

		await Assert.ThrowsAsync<TimeoutException>(() => TimeBox.RunAsync<string>(
			_ => throw new OperationCanceledException(CancellationToken.None),
			TimeSpan.FromSeconds(30)));
	}

	[Fact]
	public async Task Outer_cancellation_still_wins_over_an_inner_token_OCE()
	{
		// When the outer run IS being torn down, an OCE must propagate as cancellation even
		// if the exception happens to carry some unrelated inner token - the outer token is
		// the authority, not the token stamped on the exception.
		using var outer = new CancellationTokenSource();
		await outer.CancelAsync();
		using var inner = new CancellationTokenSource();
		await inner.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TimeBox.RunAsync<string>(
			_ => throw new OperationCanceledException(inner.Token),
			TimeSpan.FromSeconds(30),
			outer.Token));
	}
}
