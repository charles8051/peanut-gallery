using System.Linq;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// The distinction that matters: an understood reply with no findings is a clean review;
/// a reply we could not read is a failure. Collapsing them turns a malformed model reply
/// into a silent "looks good".
/// </summary>
public class StructuredOutputTests
{
	private static readonly RepoTarget Repo = new("demo", "/repos/demo");

	[Fact]
	public void A_well_formed_reply_is_parsed()
	{
		var r = SessionUpdateParser.ParseResult(
			"""{"summary":"s","findings":[{"severity":"major","title":"t","body":"b"}],"resolved":[],"withdrawn":[]}""");

		Assert.True(r.Parsed);
		Assert.Null(r.Reason);
		Assert.Single(r.Update.Findings);
	}

	[Fact]
	public void An_empty_findings_list_is_a_clean_review_not_a_failure()
	{
		var r = SessionUpdateParser.ParseResult("""{"summary":"looks good","findings":[]}""");

		Assert.True(r.Parsed);
		Assert.Empty(r.Update.Findings);
	}

	[Fact]
	public void Prose_with_no_json_is_unreadable()
	{
		var r = SessionUpdateParser.ParseResult("Looks good to me, no issues found!");

		Assert.False(r.Parsed);
		Assert.NotNull(r.Reason);
		Assert.Empty(r.Update.Findings);
	}

	[Fact]
	public void An_empty_reply_is_unreadable()
	{
		Assert.False(SessionUpdateParser.ParseResult("").Parsed);
		Assert.False(SessionUpdateParser.ParseResult(null).Parsed);
		Assert.False(SessionUpdateParser.ParseResult("   ").Parsed);
	}

	[Fact]
	public void An_empty_reply_is_flagged_WasEmpty_but_a_malformed_one_is_not()
	{
		// WasEmpty is the size signature the shell shrinks a prompt on. It must be true only when
		// the model returned nothing - a non-empty reply that ignored the contract is a format
		// problem the JSON re-ask handles, not a too-large prompt.
		Assert.True(SessionUpdateParser.ParseResult("").WasEmpty);
		Assert.True(SessionUpdateParser.ParseResult(null).WasEmpty);
		Assert.True(SessionUpdateParser.ParseResult("   ").WasEmpty);

		Assert.False(SessionUpdateParser.ParseResult("Looks good to me!").WasEmpty);
		Assert.False(SessionUpdateParser.ParseResult("""{"note":"fine"}""").WasEmpty);
	}

	[Fact]
	public void Malformed_json_is_unreadable()
	{
		var r = SessionUpdateParser.ParseResult("""{"summary":"s","findings":[{"title":}""");

		Assert.False(r.Parsed);
	}

	[Fact]
	public void A_json_object_with_none_of_the_protocol_keys_is_unreadable()
	{
		// A reply that ignored the contract is not an empty review.
		var r = SessionUpdateParser.ParseResult("""{"note":"I reviewed it and it is fine"}""");

		Assert.False(r.Parsed);
		Assert.Contains("expected keys", r.Reason);
	}

	[Fact]
	public void Json_wrapped_in_prose_or_a_fence_still_parses()
	{
		var r = SessionUpdateParser.ParseResult("Sure:\n```json\n{\"summary\":\"x\",\"findings\":[]}\n```\n");

		Assert.True(r.Parsed);
		Assert.Equal("x", r.Update.Summary);
	}

	[Fact]
	public void The_lossy_Parse_wrapper_still_yields_an_empty_update_for_garbage()
	{
		// Back-compat: callers that don't need the distinction behave as before.
		var u = SessionUpdateParser.Parse("not json at all");

		Assert.Empty(u.Findings);
		Assert.Equal(string.Empty, u.Summary);
	}

	// ---- Repair ----

	[Fact]
	public void Repair_appends_a_corrective_user_turn_and_keeps_the_original_messages()
	{
		var original = SessionPlanner.Advance(
			TestData.BugHunter, Repo, ReviewSession.Initial, Diff.Empty, "sha");

		var repair = SessionPlanner.Repair(original, "I think it looks fine.");

		Assert.Equal(original.Messages.Count + 1, repair.Messages.Count);
		Assert.Equal(Msg.System(original), Msg.System(repair)); // system/protocol untouched
		var last = repair.Messages[^1];
		Assert.Equal(ChatRole.User, last.Role);
		Assert.Contains("could not be parsed", last.Content);
		Assert.Contains("I think it looks fine.", last.Content); // shows the model what it sent
		Assert.Contains("ONLY that JSON object", last.Content);
	}

	[Fact]
	public void Repair_preserves_the_model_temperature_and_tier()
	{
		var original = SessionPlanner.Advance(
			TestData.BugHunter, Repo, ReviewSession.Initial, Diff.Empty, "sha");

		var repair = SessionPlanner.Repair(original, "junk");

		Assert.Equal(original.Model, repair.Model);
		Assert.Equal(original.Temperature, repair.Temperature);
		Assert.Equal(original.Tier, repair.Tier);
	}

	[Fact]
	public void Repair_truncates_a_huge_unreadable_reply()
	{
		var original = SessionPlanner.Advance(
			TestData.BugHunter, Repo, ReviewSession.Initial, Diff.Empty, "sha");

		var repair = SessionPlanner.Repair(original, new string('z', 5000));

		Assert.Contains("…", Msg.LastUser(repair));
		Assert.DoesNotContain(new string('z', 600), Msg.LastUser(repair));
	}

	[Fact]
	public void Repair_handles_an_empty_reply_without_quoting_nothing()
	{
		var original = SessionPlanner.Advance(
			TestData.BugHunter, Repo, ReviewSession.Initial, Diff.Empty, "sha");

		var content = Msg.LastUser(SessionPlanner.Repair(original, ""));

		Assert.Contains("could not be parsed", content);
		Assert.DoesNotContain("You replied:", content);
	}
}
