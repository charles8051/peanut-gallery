using System;
using System.Threading;
using System.Threading.Tasks;

namespace PeanutGallery.Engine;

/// <summary>
/// Runs an async operation under a hard timeout, distinguishing a self-imposed
/// timeout (the operation was too slow -> <see cref="TimeoutException"/>) from outer
/// cancellation (the whole run is being torn down -> the
/// <see cref="OperationCanceledException"/> propagates unchanged). This is what keeps
/// one slow or hung model call from stalling the entire review: the timeout surfaces
/// as a normal failure the caller turns into a finding, while real cancellation still
/// tears the process down cleanly.
/// </summary>
public static class TimeBox
{
	// One source for the timeout message so the two throw sites below cannot drift. It reads
	// "timed out" deliberately — that is what it is. Classification is structural (the shell tags
	// FailureClass.Timeout on the caught TimeoutException; see ReviewRunner), so nothing parses this
	// text; it is human-facing prose only, not a cross-layer contract.
	private static TimeoutException Timeout(TimeSpan timeout) =>
		new($"operation timed out after {timeout.TotalSeconds:F0}s");

	/// <summary>
	/// Runs <paramref name="work"/> under a <paramref name="timeout"/> deadline.
	/// </summary>
	/// <remarks>
	/// Cancellation contract — the dividing line is the CALLER's <paramref name="ct"/>,
	/// not which token fired:
	/// <list type="bullet">
	/// <item><description>
	/// If <paramref name="ct"/> is cancelled, an <see cref="OperationCanceledException"/>
	/// propagates unchanged — the caller asked to stop, so the run tears down cleanly.
	/// </description></item>
	/// <item><description>
	/// Otherwise ANY <see cref="OperationCanceledException"/> out of <paramref name="work"/>
	/// — our own deadline elapsing, an inner client's per-attempt network-timeout CTS, or
	/// even a bare <c>throw new OperationCanceledException()</c> — is treated as a
	/// self-timeout and rethrown as <see cref="TimeoutException"/>. This is deliberately
	/// broad: <paramref name="work"/> here is an IO call under a deadline, and a
	/// cancellation the caller did not request means that call aborted itself. Callers that
	/// need a bare OCE to propagate as cancellation must surface it through
	/// <paramref name="ct"/> rather than throwing it from inside <paramref name="work"/>.
	/// </description></item>
	/// </list>
	/// </remarks>
	public static async Task<T> RunAsync<T>(
		Func<CancellationToken, Task<T>> work, TimeSpan timeout, CancellationToken ct = default)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		cts.CancelAfter(timeout);

		// Start the work, then RACE it against the deadline rather than awaiting it directly.
		// Awaiting directly bounds the wall clock only if `work` HONOURS its token; a call that
		// ignores it — a stalled socket read some HTTP stacks never cancel — would run past the
		// deadline. In a review that meant one hung persona ran until the workflow's hard kill,
		// which destroyed every finished reviewer's work (#121). Task.WhenAny returns at the
		// deadline no matter what the work does, so a hung call can never outlive its budget.
		// A synchronous throw from work() is folded into a faulted task so the paths below are
		// uniform (this preserves the inner-OCE -> TimeoutException contract for a throwing work).
		var workTask = Invoke(work, cts.Token);
		var timer = Task.Delay(timeout, ct);

		if (await Task.WhenAny(workTask, timer) == workTask)
		{
			try
			{
				return await workTask;
			}
			// Any cancellation NOT attributable to the outer token is a self-timeout: our own
			// CancelAfter firing, or an OCE thrown from inside work by some other token (e.g. an
			// HTTP client's per-attempt network timeout, whose CTS is not our linked cts). Keying
			// only off `ct` means such an inner timeout becomes a TimeoutException the caller turns
			// into a failure finding, instead of masquerading as outer teardown and faulting the run.
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				throw Timeout(timeout);
			}
		}

		// The deadline (or an outer cancel) won the race. The work is ABANDONED — a call that never
		// honoured cancellation may keep running in the background until the process exits, so its
		// eventual fault is observed (Forget) to avoid an UnobservedTaskException. cts is already
		// cancelled by CancelAfter, giving cooperative work a last chance to stop.
		Forget(workTask);
		ct.ThrowIfCancellationRequested(); // outer teardown -> propagate cancellation unchanged
		throw Timeout(timeout);
	}

	// Turn a synchronous throw from work() into a faulted task so the caller sees one shape.
	private static Task<T> Invoke<T>(Func<CancellationToken, Task<T>> work, CancellationToken token)
	{
		try
		{
			return work(token);
		}
		catch (Exception e)
		{
			return Task.FromException<T>(e);
		}
	}

	// Observe an abandoned task's eventual exception so it never surfaces as UnobservedTaskException.
	private static void Forget(Task task) =>
		_ = task.ContinueWith(
			t => _ = t.Exception,
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
}
