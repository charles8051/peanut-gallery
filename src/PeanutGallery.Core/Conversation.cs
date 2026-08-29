using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PeanutGallery.Core;

/// <summary>What a comment addressed to the panel costs.</summary>
public enum ConversationMode
{
	/// <summary>Today's behaviour: every persona takes a full turn. N calls, plus verification.</summary>
	Panel,

	/// <summary>One pass over the whole panel's board, and it can only remove findings. 1 call.</summary>
	Reconcile,

	/// <summary>Comments never trigger a turn. 0 calls.</summary>
	Off,
}

/// <summary>
/// How the panel spends on conversation, as two independent dials: which comments count
/// (<see cref="Mentions"/>) and what an addressed comment causes (<see cref="Mode"/>).
///
/// <para>A null policy on the config is exactly the behaviour that shipped before this existed -
/// <see cref="ConversationMode.Panel"/> with no gate - so this is additive and opt-in.</para>
/// </summary>
/// <param name="Mentions">Tokens that mark a comment as addressed to the panel, matched
/// case-insensitively. EMPTY MEANS EVERY human comment counts, which is the historical default;
/// it does not mean "none", because a gate that silently swallowed every comment would turn an
/// unset config into a mute reviewer.</param>
/// <param name="Model">Which model reconciles. Null falls back to the config's persona model, so a
/// repo does not have to name one twice.</param>
public sealed record ConversationPolicy(
	ConversationMode Mode = ConversationMode.Panel,
	IReadOnlyList<string>? Mentions = null,
	ModelRef? Model = null)
{
	public static ConversationPolicy Default { get; } = new();

	public IReadOnlyList<string> MentionTokens => Mentions ?? [];
}

/// <summary>
/// Pure decision: of the new human comments on this PR, which are actually talking to the panel?
///
/// <para>Separate from the trust guard on purpose, and both are needed. The trust guard answers
/// <em>may this person direct the reviewers at all</em>; this answers <em>were they trying to</em>.
/// Two collaborators arguing about a migration in the PR thread pass the first and should fail the
/// second - waking four reviewers to read a conversation that was never addressed to them is the
/// waste this exists to remove.</para>
/// </summary>
public static class ConversationGate
{
	/// <summary>
	/// The comments that address the panel. With no configured mentions every comment does, which
	/// keeps an unset config behaving exactly as it did before the gate existed.
	/// </summary>
	public static IReadOnlyList<AuthorComment> Addressed(
		IReadOnlyList<AuthorComment> comments, ConversationPolicy? policy)
	{
		var mentions = (policy ?? ConversationPolicy.Default).MentionTokens;
		if (mentions.Count == 0 || comments.Count == 0)
		{
			return comments;
		}

		var kept = new List<AuthorComment>(comments.Count);
		foreach (var c in comments)
		{
			var prose = Prose(c.Body);
			foreach (var token in mentions)
			{
				if (!string.IsNullOrWhiteSpace(token) && Mentions(prose, token.Trim()))
				{
					kept.Add(c);
					break;
				}
			}
		}

		return kept;
	}

	/// <summary>
	/// The part of a comment that is someone speaking, with the parts that are merely SHOWING text
	/// removed: fenced blocks, inline code spans, and quoted lines.
	///
	/// <para>Naming the token inside a code fence is how you write documentation about the gate, and
	/// a quoted line is someone repeating what another person said. Neither is an address, and both
	/// would otherwise let a comment trigger a turn - including the annoying case where quoting an
	/// earlier mention re-triggers it forever.</para>
	/// </summary>
	private static string Prose(string body)
	{
		var sb = new StringBuilder(body.Length);
		var inFence = false;
		foreach (var rawLine in body.Split('\n'))
		{
			var line = rawLine.TrimEnd('\r');
			var trimmed = line.TrimStart();
			if (trimmed.StartsWith("```", StringComparison.Ordinal)
				|| trimmed.StartsWith("~~~", StringComparison.Ordinal))
			{
				inFence = !inFence;
				continue;
			}

			if (inFence || trimmed.StartsWith('>'))
			{
				continue;
			}

			// Inline code spans, same reasoning as a fence at line scale.
			var span = false;
			foreach (var ch in line)
			{
				if (ch == '`')
				{
					span = !span;
					continue;
				}

				sb.Append(span ? ' ' : ch);
			}

			sb.Append('\n');
		}

		return sb.ToString();
	}

	/// <summary>
	/// Whether the token appears as its own word. A bare substring match would count
	/// <c>@peanut-gallery-bot</c> (a different account) and <c>@@peanut-gallery</c> as an address,
	/// which lets someone trigger a turn with a comment that never spoke to the panel.
	///
	/// <para>The <c>@</c> rule is deliberately conditional rather than blanket. Treating <c>@</c> as
	/// a word character unconditionally blocks the doubled-up case, but it also means a token
	/// configured WITHOUT a leading <c>@</c> (plain <c>peanut-gallery</c>) stops matching the
	/// obvious way to write it — <c>@peanut-gallery</c> — because the <c>@</c> in front reads as
	/// part of a longer word. Silently ignoring the address someone actually typed is a worse
	/// failure than an occasional wasted call, so <c>@</c> only closes the boundary for a token that
	/// starts with one, where it can mean nothing but a different handle.</para>
	/// </summary>
	private static bool Mentions(string text, string token)
	{
		if (token.Length == 0)
		{
			return false;
		}

		var atPrefixed = token[0] == '@';
		var at = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
		while (at >= 0)
		{
			var before = at == 0 ? '\0' : text[at - 1];
			var beforeOk = at == 0 || (!IsWordChar(before) && !(atPrefixed && before == '@'));
			var end = at + token.Length;
			var afterOk = end >= text.Length || !IsWordChar(text[end]);
			if (beforeOk && afterOk)
			{
				return true;
			}

			at = text.IndexOf(token, at + 1, StringComparison.OrdinalIgnoreCase);
		}

		return false;
	}

	// '-' counts, so a handle that merely STARTS with ours (@peanut-gallery-bot) is not ours.
	private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';
}

/// <summary>
/// One reconciliation turn's reply: titles a human explained away, and titles a human says are
/// fixed. Deliberately has NO findings list - see <see cref="Reconciliation"/>.
/// </summary>
public sealed record ReconcileVerdicts(IReadOnlyList<string> Withdrawn, IReadOnlyList<string> Resolved)
{
	public static ReconcileVerdicts Empty { get; } = new([], []);

	public bool IsEmpty => Withdrawn.Count == 0 && Resolved.Count == 0;
}

/// <summary>
/// Reads a reconciliation reply. Total: anything unreadable is <see cref="ReconcileVerdicts.Empty"/>,
/// which leaves the board exactly as it was - the safe direction, because this pass only ever
/// removes and a failure to parse should never remove anything.
///
/// <para>A <c>findings</c> array in the reply is <b>ignored</b>, not merged. The subtractive
/// invariant is enforced here at the boundary rather than trusted to the prompt: a model that
/// decides to volunteer a new finding during a conversation turn must not be able to smuggle one
/// onto the board through the reply it was asked for.</para>
/// </summary>
public static class ReconcileParser
{
	public static ReconcileVerdicts Parse(string? reply)
	{
		if (string.IsNullOrWhiteSpace(reply))
		{
			return ReconcileVerdicts.Empty;
		}

		var json = FindingsParser.ExtractJsonObject(reply);
		if (json is null)
		{
			return ReconcileVerdicts.Empty;
		}

		try
		{
			using var doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object)
			{
				return ReconcileVerdicts.Empty;
			}

			return new ReconcileVerdicts(
				ReadTitles(doc.RootElement, "withdrawn"),
				ReadTitles(doc.RootElement, "resolved"));
		}
		catch (JsonException)
		{
			return ReconcileVerdicts.Empty;
		}
	}

	private static IReadOnlyList<string> ReadTitles(JsonElement root, string name)
	{
		if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
		{
			return [];
		}

		var titles = new List<string>();
		foreach (var el in arr.EnumerateArray())
		{
			if (el.ValueKind == JsonValueKind.String && el.GetString() is { } s && s.Trim().Length > 0)
			{
				titles.Add(s.Trim());
			}
		}

		return titles;
	}
}

/// <summary>What a reconciliation did to one persona's session.</summary>
public sealed record ReconciledSession(ReviewSession Session, IReadOnlyList<string> Removed);

/// <summary>
/// Applies a reconciliation to a persona's session. STRICTLY SUBTRACTIVE: it removes titles from
/// the open set and remembers them as dropped, and it has no path that adds a finding, changes a
/// severity, or edits a body.
///
/// <para>That is the whole safety property of a conversation turn. The input driving it is human
/// comment text, which the prompt frames as context rather than instructions - but framing is a
/// request, and this is a guarantee. Bounded blast radius also bounds cost: a turn that cannot grow
/// the board cannot cascade into a verification pass.</para>
///
/// <para>Pure: session + verdicts in, session out.</para>
/// </summary>
public static class Reconciliation
{
	/// <summary>
	/// The session with every named title taken off the board, and a record of which ones this
	/// persona actually had. A title no persona holds is simply not found - a reconciler naming a
	/// finding that does not exist changes nothing rather than erroring.
	/// </summary>
	public static ReconciledSession Apply(
		ReviewSession session, ReconcileVerdicts verdicts, long lastSeenCommentId)
	{
		var advanced = session with
		{
			LastSeenCommentId = Math.Max(session.LastSeenCommentId, lastSeenCommentId),
		};

		if (verdicts.IsEmpty || session.OpenFindings.Count == 0)
		{
			return new ReconciledSession(advanced, []);
		}

		var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var t in verdicts.Withdrawn)
		{
			off.Add(t.Trim());
		}

		foreach (var t in verdicts.Resolved)
		{
			off.Add(t.Trim());
		}

		var kept = new List<Finding>(session.OpenFindings.Count);
		var removed = new List<string>();
		foreach (var f in session.OpenFindings)
		{
			if (off.Contains(f.Title.Trim()))
			{
				removed.Add(f.Title.Trim());
			}
			else
			{
				kept.Add(f);
			}
		}

		if (removed.Count == 0)
		{
			return new ReconciledSession(advanced, []);
		}

		// Remembered as dropped so the next push does not re-raise what a human just explained.
		// 'posted' is the surviving set: a title still on the board has not been dropped.
		return new ReconciledSession(
			advanced with
			{
				OpenFindings = kept,
				Dropped = DroppedMemory.Next(advanced.DroppedTitles, removed, kept),
			},
			removed);
	}
}
