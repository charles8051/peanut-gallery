using System.Linq;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// The session deliberately carries the full finding set forward, so without this memory a model
/// never learns that a finding was suppressed or refuted - it re-emits the same one every push and
/// pays to have it dropped again.
/// </summary>
public class DroppedMemoryTests
{
	private static readonly RepoTarget Repo = new("demo", "/repos/demo");

	private static Finding F(string title) => new(Severity.Major, "a.cs", 1, title, "b");

	[Fact]
	public void Newly_dropped_titles_are_remembered()
	{
		var next = DroppedMemory.Next([], ["refuted one"], []);

		Assert.Equal(["refuted one"], next);
	}

	[Fact]
	public void Prior_memory_is_carried_forward()
	{
		var next = DroppedMemory.Next(["old"], ["new"], []);

		Assert.Equal(["new", "old"], next); // newest first
	}

	[Fact]
	public void A_title_that_survived_this_turn_is_forgotten()
	{
		// It earned its way back, so the model should not be discouraged from raising it.
		var next = DroppedMemory.Next(["was dropped"], [], [F("was dropped")]);

		Assert.Empty(next);
	}

	[Fact]
	public void Duplicates_collapse_case_insensitively()
	{
		var next = DroppedMemory.Next(["Null Deref"], ["null deref"], []);

		Assert.Single(next);
	}

	[Fact]
	public void Blank_titles_are_ignored()
	{
		var next = DroppedMemory.Next([], ["", "   ", "real"], []);

		Assert.Equal(["real"], next);
	}

	[Fact]
	public void Memory_is_capped_and_evicts_the_stalest()
	{
		var prior = Enumerable.Range(0, 30).Select(i => $"old{i}").ToList();

		var next = DroppedMemory.Next(prior, ["fresh"], []);

		Assert.Equal(DroppedMemory.MaxRemembered, next.Count);
		Assert.Equal("fresh", next[0]);
		Assert.DoesNotContain("old29", next); // the stalest fell off, not the newest
	}

	// ---- what is left on the board ----

	[Fact]
	public void Standing_is_the_open_set_when_nothing_was_dropped()
	{
		var open = new[] { F("a"), F("b") };

		Assert.Equal(open, DroppedMemory.Standing(open, []));
	}

	[Fact]
	public void A_dropped_title_is_not_standing()
	{
		// The session keeps the model's FULL working set, so a suppressed/refuted finding is still
		// in 'open'. Replaying it would undo the gate and the adversarial pass in one step.
		var standing = DroppedMemory.Standing([F("kept"), F("refuted")], ["refuted"]);

		Assert.Equal(["kept"], standing.Select(f => f.Title));
	}

	[Fact]
	public void Standing_matches_titles_case_and_whitespace_insensitively()
	{
		var standing = DroppedMemory.Standing([F("Null Deref")], ["  null deref "]);

		Assert.Empty(standing);
	}

	[Fact]
	public void Two_findings_whose_titles_differ_only_in_case_are_one_title()
	{
		// Pinning the decision, not describing an accident: a dropped title is matched
		// case-insensitively, so "Null Deref" and "null deref" are the SAME finding and both come
		// off the board. Next() already settles it that way (see the collapse test above), and
		// FindingSynthesis keys on a case-folded title too - a Standing() that disagreed would put
		// a finding back that the rest of the pipeline treats as already handled.
		var standing = DroppedMemory.Standing([F("Null Deref"), F("null deref")], ["Null Deref"]);

		Assert.Empty(standing);
	}

	[Fact]
	public void A_sessions_standing_set_reads_its_own_dropped_memory()
	{
		var session = new ReviewSession("sha", 2, "s", [F("kept"), F("hedged")], 0, ["hedged"]);

		Assert.Equal(["kept"], DroppedMemory.Standing(session).Select(f => f.Title));
	}

	// ---- session round-trip ----

	[Fact]
	public void Dropped_titles_survive_a_session_round_trip()
	{
		var session = new ReviewSession("sha", 2, "s", [], 0, ["refuted a", "hedged b"]);

		var back = SessionCodec.Extract(SessionCodec.Embed("x", session));

		Assert.Equal(["refuted a", "hedged b"], back!.DroppedTitles);
	}

	[Fact]
	public void A_session_stored_before_this_field_existed_reads_as_no_memory()
	{
		var legacy = new ReviewSession("sha", 2, "s", []);

		var back = SessionCodec.Extract(SessionCodec.Embed("x", legacy));

		Assert.Empty(back!.DroppedTitles);
	}

	// ---- the prompt ----

	[Fact]
	public void Continued_turns_tell_the_model_what_it_already_dropped()
	{
		var prior = new ReviewSession("old", 1, "running", [], 0, ["stylistic nit"]);

		var user = Msg.User(SessionPlanner.Advance(
			TestData.BugHunter, Repo, prior, Diff.Empty, "newsha"));

		Assert.Contains("You already dropped these findings", user);
		Assert.Contains("stylistic nit", user);
		Assert.Contains("Do NOT raise them again", user);
		Assert.Contains("unless the change since your last review", user); // but the door stays open
	}

	[Fact]
	public void No_dropped_memory_means_no_block_in_the_prompt()
	{
		var prior = new ReviewSession("old", 1, "running", []);

		var user = Msg.User(SessionPlanner.Advance(
			TestData.BugHunter, Repo, prior, Diff.Empty, "newsha"));

		Assert.DoesNotContain("You already dropped these findings", user);
	}

	[Fact]
	public void The_first_turn_has_no_dropped_block()
	{
		var user = Msg.User(SessionPlanner.Advance(
			TestData.BugHunter, Repo, ReviewSession.Initial, Diff.Empty, "sha"));

		Assert.DoesNotContain("You already dropped these findings", user);
	}
}
