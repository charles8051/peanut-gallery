using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

public class PromptAssemblyTests
{
	private static readonly RepoTarget Repo = new("demo", "/repos/demo");
	private static readonly Diff Diff = Core.Diff.Parse("diff --git a/Foo.cs b/Foo.cs\n+added\n-removed\n");

	[Fact]
	public void System_message_is_the_persona_prompt_and_carries_the_model_and_temperature()
	{
		var req = PromptAssembly.Build(TestData.BugHunter, Repo, Diff);

		Assert.Equal(TestData.BugHunter.Model, req.Model);
		Assert.Equal(0.0, req.Temperature);
		// The persona's own prompt LEADS the system message - what follows is the shared
		// proportionality clause, appended to every persona. Asserting the lead rather than the
		// whole string keeps this test about "the persona's voice comes first" instead of
		// re-pinning shared text that has its own tests below.
		Assert.StartsWith("Find correctness bugs only.", Msg.System(req));
	}

	[Fact]
	public void User_message_embeds_the_repo_the_changed_files_and_the_raw_diff()
	{
		var req = PromptAssembly.Build(TestData.Architect, Repo, Diff);
		var user = Msg.User(req);

		Assert.Contains("'demo'", user);
		Assert.Contains("Foo.cs", user);
		Assert.Contains("findings", user);          // the JSON output instruction
		Assert.Contains(Diff.Raw, user);            // raw diff handed to the model verbatim
	}

	[Fact]
	public void Agent_tier_personas_are_told_about_read_only_tools()
	{
		var agent = Msg.System(PromptAssembly.Build(TestData.Contrarian, Repo, Diff));
		var diffTier = Msg.System(PromptAssembly.Build(TestData.Architect, Repo, Diff));

		Assert.Contains("read-only tools", agent);
		Assert.DoesNotContain("read-only tools", diffTier);
	}

	// The two assertions the prompt-prefix cache actually rests on. Both are about ORDER and
	// SHARING, so they index deliberately where the rest of the suite asks by role.
	[Fact]
	public void The_persona_independent_block_comes_first_so_a_runs_personas_share_a_prefix()
	{
		var req = PromptAssembly.Build(TestData.BugHunter, Repo, Diff);

		Assert.Equal(ChatRole.User, req.Messages[0].Role);
		Assert.Equal(ChatRole.System, req.Messages[^1].Role);
	}

	[Fact]
	public void Two_personas_reviewing_one_diff_produce_a_byte_identical_user_block()
	{
		// Different lens, different tier, different model — the shared block must not notice.
		var bugHunter = PromptAssembly.Build(TestData.BugHunter, Repo, Diff);
		var contrarian = PromptAssembly.Build(TestData.Contrarian, Repo, Diff);

		Assert.Equal(Msg.User(bugHunter), Msg.User(contrarian));
		Assert.NotEqual(Msg.System(bugHunter), Msg.System(contrarian));
	}

	[Fact]
	public void Every_persona_is_asked_whether_a_finding_is_worth_its_fix()
	{
		// Every guardrail in the system tests whether a finding is TRUE; none tested whether it was
		// WORTH IT, so a reviewer could demand unbounded machinery and nothing said no. On
		// in the calibration case a convened persona drove a lint's hand-rolled C# lexer from 102 to 343
		// lines across five turns. Asked of seed and generated personas alike - both can cause it.
		//
		// This covers the LOCAL one-shot composer only. Each composer assembles its own system
		// message, so each needs its own copy of this assertion: SessionTests has the PR path's
		// (#166, where that was missing), PanelOrchestrationTests a convened persona's.
		foreach (var persona in new[] { TestData.Architect, TestData.BugHunter, TestData.Contrarian })
		{
			var system = Msg.System(PromptAssembly.Build(persona, Repo, Diff));

			Assert.Contains("worth its fix", system);
			Assert.Contains("not commissioning machinery", system);
			Assert.Contains("past the risk that guard exists to reduce", system);
		}
	}

	[Fact]
	public void Severity_is_pinned_to_consequence_not_to_how_incomplete_a_mechanism_looks()
	{
		// Those findings were rated major on a TEST-ONLY file whose worst outcome is a false
		// negative in a lint, on a PR whose real risk was a stale sensor read.
		var system = Msg.System(PromptAssembly.Build(TestData.BugHunter, Repo, Diff));

		Assert.Contains("Severity is the consequence if you are right", system);
	}

	[Fact]
	public void The_proportionate_answer_may_be_a_smaller_mechanism_not_a_more_complete_one()
	{
		// The escalating case is a guard that is already the wrong mechanism: every individual gap
		// is real, so a reviewer judging gaps one at a time correctly asks for each to be closed and
		// the machinery only grows. Naming the alternative is the only exit.
		var system = Msg.System(PromptAssembly.Build(TestData.Architect, Repo, Diff));

		Assert.Contains("simpler mechanism than the one under review", system);
	}
}
