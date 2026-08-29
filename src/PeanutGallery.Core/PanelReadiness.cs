using System;
using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>Whether the panel comment on a PR speaks for the commit a caller is waiting on.</summary>
public enum PanelArrival
{
	/// <summary>No panel comment on the PR at all — this review has posted nothing yet.</summary>
	Absent,

	/// <summary>A panel comment is there, but its state blob is missing or unreadable.</summary>
	Unreadable,

	/// <summary>
	/// A panel comment is there and its blob parses, but it carries no reviewer at all. This is
	/// what a whole-panel outage on the first turn publishes: every reviewer failed, so none of
	/// them contributed a session. It is NOT fresh — nobody reviewed this commit — and the
	/// arithmetic that would say otherwise (0 reviewers at head out of 0 reviewers) is why it
	/// needs a name of its own rather than falling out of a comparison.
	/// </summary>
	NoReviewers,

	/// <summary>The panel is a PREVIOUS turn's, still standing because the comment is upserted in place.</summary>
	Stale,

	/// <summary>Some reviewers carrying a session have reported this commit and some have not.</summary>
	Partial,

	/// <summary>Every reviewer carrying a session has reported this commit.</summary>
	Fresh,
}

/// <summary>
/// Reads the one question a caller waiting on a review actually has: does the panel comment now on
/// this PR describe the commit I just pushed, or the one before it?
///
/// <para>The question exists because the panel comment is <b>upserted in place</b>. The previous
/// turn's body is present on the PR for the whole time the next review is running, so "is there a
/// panel comment?" answers yes instantly and hands the caller findings that were already addressed.
/// A poll that does not check the SHA is not a poll, it is a race the caller always loses.</para>
///
/// <para>The SHA comes from the embedded <c>pg-panel-state</c> blob (via
/// <see cref="PanelSessionCodec"/>), never from the rendered <c>_Reviewed through `sha`_</c> line.
/// That line is prose written for a human: it is short-form, it changes wording while a review is
/// in progress, and it has no reviewer-by-reviewer detail. The blob is the same fact as structured
/// data, and it is what the review itself reads back on the next turn.</para>
///
/// <para>Pure by construction — it is handed comment bodies and a SHA. The polling, the clock and
/// the HTTP belong to the shell that calls it.</para>
/// </summary>
/// <param name="Reviewers">How many personas carry a session in the panel's blob.</param>
/// <param name="ReviewersAtHead">How many of them last reviewed <c>headSha</c>. Fewer than
/// <paramref name="Reviewers"/> means the panel is mid-run, or that a reviewer failed this turn and
/// kept the session it had (a failed turn does not advance one).</param>
/// <param name="Degraded">What the hidden <c>pg-degraded</c> marker says was lost on a settled
/// panel — reviewers that never carried a session at all, which the counts above cannot see.</param>
/// <param name="HasFindings">The panel reports something to address. Read from the rendered board
/// (<see cref="PanelCommentRenderer.ReportsNoFindings"/>), NOT from the blob's open findings: the
/// blob keeps every finding the model raised, including the ones the confidence gate suppressed and
/// the adversarial pass refuted, and those are exactly the ones the author is not being asked to
/// answer.</param>
public sealed record PanelReadiness(
	PanelArrival Arrival,
	int Reviewers,
	int ReviewersAtHead,
	int Turn,
	string? ReviewedSha,
	bool HasFindings,
	int Degraded)
{
	/// <summary>No panel comment found.</summary>
	public static PanelReadiness None { get; } = new(PanelArrival.Absent, 0, 0, 0, null, false, 0);

	/// <summary>
	/// The panel now on the PR is this commit's. <see cref="PanelArrival.Partial"/> counts: after
	/// the review job has finished, a reviewer still on the old SHA is one that failed, and waiting
	/// on it further would wait forever.
	/// </summary>
	public bool Landed => Arrival is PanelArrival.Fresh or PanelArrival.Partial;

	/// <summary>
	/// The panel has settled for this commit — waiting longer cannot improve it. Landed, or a
	/// published panel that carries no reviewer at all, which is a whole-panel outage and not a
	/// state anything is going to advance out of.
	/// </summary>
	public bool Settled => Landed || Arrival is PanelArrival.NoReviewers;

	/// <summary>
	/// Every reviewer on the panel reported this commit and none went missing. The ONLY shape that
	/// may be reported as a clean review: a board with no findings on it says nothing about the
	/// lens that never got to look, and #130 exists because a panel that quietly shrank is
	/// indistinguishable from one that found nothing.
	/// </summary>
	public bool Complete => Arrival is PanelArrival.Fresh && Degraded == 0;

	public static PanelReadiness Read(IReadOnlyList<string> commentBodies, string headSha)
	{
		var marker = CommentRenderer.Marker(PanelCommentRenderer.PanelId);
		string? panel = null;
		foreach (var body in commentBodies)
		{
			if (!string.IsNullOrEmpty(body) && body.Contains(marker, StringComparison.Ordinal))
			{
				panel = body;
				break;
			}
		}

		if (panel is null)
		{
			return None;
		}

		var hasFindings = !PanelCommentRenderer.ReportsNoFindings(panel);
		var degraded = PanelCommentRenderer.DegradedCount(panel);

		// A panel comment whose blob will not parse is NOT "no review yet": something posted, and a
		// caller told Absent would keep waiting for a comment that is already there. Naming the two
		// apart is the difference between "wait longer" and "go look at it yourself".
		var session = PanelSessionCodec.Extract(panel);
		if (session is null)
		{
			return new PanelReadiness(PanelArrival.Unreadable, 0, 0, 0, null, hasFindings, degraded);
		}

		// Nobody in the blob. Every comparison below would be 0 == 0 and would call that agreement,
		// so the case is answered before the arithmetic can lie about it.
		if (session.ByPersona.Count == 0)
		{
			return new PanelReadiness(PanelArrival.NoReviewers, 0, 0, 0, null, hasFindings, degraded);
		}

		var reviewers = session.ByPersona.Count;
		var atHead = 0;
		var turn = 0;
		string? reviewedSha = null;
		foreach (var (_, s) in session.ByPersona)
		{
			turn = Math.Max(turn, s.Turn);
			if (Sha.SameCommit(s.LastReviewedSha, headSha))
			{
				atHead++;
				reviewedSha = s.LastReviewedSha;
			}
			else if (reviewedSha is null)
			{
				reviewedSha = s.LastReviewedSha;
			}
		}

		var arrival = atHead == 0
			? PanelArrival.Stale
			: atHead == reviewers ? PanelArrival.Fresh : PanelArrival.Partial;

		return new PanelReadiness(arrival, reviewers, atHead, turn, reviewedSha, hasFindings, degraded);
	}
}
