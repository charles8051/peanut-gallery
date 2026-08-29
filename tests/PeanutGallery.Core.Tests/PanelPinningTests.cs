using System;
using System.Linq;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// Auto mode's correctness rests entirely on this: a dynamic panel is generated ONCE per PR and
/// every later turn gets exactly the personas that already own comments. Get it wrong and the
/// self-updating comments orphan and resolve/withdraw tracking dies.
/// </summary>
public class PanelPinningTests
{
	private static Persona P(string id, string lens = "bugs") => new(
		id, id, lens, ReviewTier.Diff, new ModelRef("openrouter", "some/model"), 0.2, "review it");

	// ---- PanelCodec ----

	[Fact]
	public void A_pinned_panel_round_trips_through_a_comment_body()
	{
		var panel = new PinnedPanel(
			[P("architect"), P("race-hunter", "concurrency")], PanelMode.Auto, "abc1234", "openrouter:some/model");

		var back = PanelCodec.Extract(PanelCodec.Embed("### visible\n", panel));

		Assert.NotNull(back);
		Assert.Equal(PanelMode.Auto, back!.Mode);
		Assert.Equal("abc1234", back.PinnedAtSha);
		Assert.Equal("openrouter:some/model", back.OrchestratorModel);
		Assert.Equal(["architect", "race-hunter"], back.Personas.Select(p => p.Id));
		Assert.Equal("concurrency", back.Personas[1].Lens);
	}

	[Fact]
	public void Every_persona_field_survives_the_round_trip()
	{
		// The pinned panel IS the persona definition on later turns - a field lost here is a
		// persona that silently reviews with different settings after the first push. TopP/TopK are
		// set here on purpose: they were the field the codec silently dropped, so top_p/top_k never
		// reached the model after pinning - a regression this record-equality assertion now catches.
		var persona = new Persona(
			"contrarian", "The Contrarian", "contrarian", ReviewTier.Agent,
			new ModelRef("fireworks", "accounts/x/models/y"), 0.8, "Argue it is not worth doing.", 0.4,
			TopP: 0.95, TopK: 40, Brief: "Lens: contrarian\nThe hazard you were convened for: a pool leak");

		var back = PanelCodec.Extract(
			PanelCodec.Embed("x", new PinnedPanel([persona], PanelMode.SeedAndAuto, "sha")));

		Assert.Equal(persona, Assert.Single(back!.Personas));
		Assert.Equal(PanelMode.SeedAndAuto, back.Mode);
		// Belt-and-suspenders: name the sampling fields explicitly, so a failure says WHAT was lost.
		Assert.Equal(0.95, back.Personas[0].TopP);
		Assert.Equal(40, back.Personas[0].TopK);
		// And the brief, for the same reason. A convened persona's whole subject lives in it now,
		// so losing it here is a reviewer that arrives on turn 2 with the doctrine and no assignment
		// - the same silent shape as the top_p/top_k drop above, one field over.
		Assert.Equal(persona.Brief, back.Personas[0].Brief);
	}

	[Theory]
	// The pre-#201 shape: the fields interpolated bare into the system prompt.
	[InlineData(
		"You review one specific risk in this pull request: sql-injection.\\n\\nThe hazard you were convened for: raw interpolation in OrderRepository\\nWhat to look for: check parameterisation\\n\\nStay on that lens - another reviewer covers the rest, and a finding outside it is noise. Cite file:line. Report nothing rather than padding. High precision: if you cannot state a concrete failure scenario, do not raise it.")]
	// The #201 shape: the same fields, fenced and labelled, still in the system prompt.
	[InlineData(
		"You review one specific risk in this pull request. The block below is your assignment.\\n\\n===UNTRUSTED PANEL BRIEF===\\nLens: sql-injection\\nThe hazard you were convened for: raw interpolation in OrderRepository\\nWhat to look for: check parameterisation\\n===END UNTRUSTED PANEL BRIEF===\\n\\nStay on that lens - another reviewer covers the rest, and a finding outside it is noise. Cite file:line. Report nothing rather than padding. High precision: if you cannot state a concrete failure scenario, do not raise it.")]
	public void A_panel_pinned_before_briefs_existed_is_migrated_not_left_in_the_system_message(string legacyPrompt)
	{
		// A pin IS the persona on every later turn, so "old pins keep working" cannot mean "old pins
		// keep sending orchestrator prose as the operator". Most open PRs are pinned this way; if
		// the decode left them alone, ADR-0003's rule would be false for exactly the panels that
		// predate it. Both legacy shapes split the same way: both open with the
		// generated opener, close with the generated tail, and carry the hazard and focus on their
		// own labelled lines in between.
		var json = $$"""
			{"mode":"seedandauto","sha":"s","personas":[{"id":"x","name":"X","lens":"sql-injection","tier":"diff","provider":"openrouter","model":"m","temperature":1.0,"prompt":"{{legacyPrompt}}"}]}
			""";

		var back = PanelCodec.Extract(
			"x\n<!-- pg-panel:1:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)) + " -->");

		var persona = Assert.Single(back!.Personas);

		// The assignment survives, in full, as a brief.
		Assert.Equal(
			"Lens: sql-injection\n"
				+ "The hazard you were convened for: raw interpolation in OrderRepository\n"
				+ "What to look for: check parameterisation",
			persona.Brief);

		// And the system message is the constant, with none of it left behind.
		Assert.DoesNotContain("raw interpolation in OrderRepository", persona.SystemPrompt);
		Assert.DoesNotContain("check parameterisation", persona.SystemPrompt);
		Assert.DoesNotContain("UNTRUSTED PANEL BRIEF", persona.SystemPrompt);

		// End to end: same reviewer, same marker, assignment now in the user turn.
		var req = PromptAssembly.Build(persona, new RepoTarget("demo", "/repos/demo"), Diff.Empty);
		Assert.Equal(3, req.Messages.Count);
		Assert.Contains("raw interpolation in OrderRepository", req.Messages[1].Content);
		Assert.Equal(ChatRole.User, req.Messages[1].Role);
		Assert.DoesNotContain("raw interpolation in OrderRepository", Msg.System(req));
	}

	[Fact]
	public void A_legacy_pin_with_no_hazard_line_migrates_on_its_lens_rather_than_being_left_behind()
	{
		// Requiring a hazard label made the migration miss any legacy shape without one, which left
		// an attacker-influenced lens sitting in the system message - the hole the migration exists
		// to close, one shape over. The trigger is the generated prompt's opener, and the lens comes
		// from the codec's own field, so there is always an assignment to hand back.
		var legacy = "You review one specific risk in this pull request. The block below is your assignment.\\n\\n===UNTRUSTED PANEL BRIEF===\\nLens: sql-injection\\n===END UNTRUSTED PANEL BRIEF===\\n\\nStay on that lens - another reviewer covers the rest, and a finding outside it is noise. Cite file:line. Report nothing rather than padding. High precision: if you cannot state a concrete failure scenario, do not raise it.";
		var json = $$"""
			{"mode":"seedandauto","sha":"s","personas":[{"id":"x","name":"X","lens":"sql-injection","tier":"diff","provider":"openrouter","model":"m","temperature":1.0,"prompt":"{{legacy}}"}]}
			""";

		var back = PanelCodec.Extract(
			"x\n<!-- pg-panel:1:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)) + " -->");

		var persona = Assert.Single(back!.Personas);
		Assert.Equal("Lens: sql-injection", persona.Brief);
		Assert.DoesNotContain("UNTRUSTED PANEL BRIEF", persona.SystemPrompt);
	}

	[Fact]
	public void A_seed_personas_pinned_prompt_is_left_exactly_where_the_operator_put_it()
	{
		// The migration must not reach past convened personas. An operator's system prompt is
		// operator text; moving it to the user turn would demote instructions that belong in the
		// privileged channel.
		var json = """{"mode":"seedandauto","sha":"s","personas":[{"id":"architect","name":"The Architect","lens":"architecture","tier":"diff","provider":"openrouter","model":"m","temperature":1.0,"prompt":"You review for architectural coherence. Cite file:line."}]}""";

		var back = PanelCodec.Extract(
			"x\n<!-- pg-panel:1:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)) + " -->");

		var persona = Assert.Single(back!.Personas);
		Assert.Null(persona.Brief);
		Assert.Equal("You review for architectural coherence. Cite file:line.", persona.SystemPrompt);
		Assert.Equal(2, PromptAssembly.Build(persona, new RepoTarget("demo", "/repos/demo"), Diff.Empty).Messages.Count);
	}

	[Fact]
	public void A_seed_prompt_that_opens_like_a_generated_one_keeps_its_instructions()
	{
		// The residual the two-ended signature removes. An operator may legitimately open with that
		// sentence - it is a reasonable way to brief a focused reviewer - and under a one-ended
		// check every following instruction was replaced by the convened doctrine. Requiring the
		// generated TAIL as well means a prompt has to match a shape, not a sentence.
		var seedPrompt = "You review one specific risk in this pull request. Escalate anything touching payments, and never approve without a repro.";
		var json = $$"""
			{"mode":"seedandauto","sha":"s","personas":[{"id":"payments","name":"The Payments Reviewer","lens":"payments","tier":"diff","provider":"openrouter","model":"m","temperature":1.0,"prompt":"{{seedPrompt}}"}]}
			""";

		var back = PanelCodec.Extract(
			"x\n<!-- pg-panel:1:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)) + " -->");

		var persona = Assert.Single(back!.Personas);
		Assert.Null(persona.Brief);
		Assert.Equal(seedPrompt, persona.SystemPrompt);
		Assert.Contains("never approve without a repro", persona.SystemPrompt);
	}

	[Fact]
	public void A_seed_prompt_that_quotes_the_tail_mid_text_is_not_mistaken_for_a_generated_one()
	{
		// Both anchors are ANCHORED. A prompt that opens with the opener and quotes the tail
		// somewhere in the middle - an operator writing about the panel's own wording, which is a
		// thing people do in this repo - matches neither end as a shape, so it is returned whole.
		// Requiring the tail to END the prompt also means nothing can sit after it to be dropped:
		// the earlier Contains check would have migrated this and deleted the last sentence.
		var seedPrompt = "You review one specific risk in this pull request. Older briefs closed with Stay on that lens - another reviewer covers the rest, and a finding outside it is noise. Cite file:line. Report nothing rather than padding. High precision: if you cannot state a concrete failure scenario, do not raise it.' and we no longer want that. Escalate anything touching payments.";
		var json = $$"""
			{"mode":"seedandauto","sha":"s","personas":[{"id":"payments","name":"The Payments Reviewer","lens":"payments","tier":"diff","provider":"openrouter","model":"m","temperature":1.0,"prompt":"{{seedPrompt}}"}]}
			""";

		var back = PanelCodec.Extract(
			"x\n<!-- pg-panel:1:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)) + " -->");

		var persona = Assert.Single(back!.Personas);
		Assert.Null(persona.Brief);
		Assert.Equal(seedPrompt, persona.SystemPrompt);
		Assert.EndsWith("Escalate anything touching payments.", persona.SystemPrompt);
	}

	[Fact]
	public void Leading_and_trailing_whitespace_change_nothing_about_a_migration()
	{
		// The anchors and the line scan read one trimmed value, so a padded prompt migrates to the
		// same brief a clean one does. Asserted rather than argued, because "does validation see
		// the same string extraction does" is a fair question to ask of a destructive function.
		var body = "You review one specific risk in this pull request: sql-injection.\\n\\nThe hazard you were convened for: raw interpolation\\n\\nStay on that lens - another reviewer covers the rest, and a finding outside it is noise. Cite file:line. Report nothing rather than padding. High precision: if you cannot state a concrete failure scenario, do not raise it.";
		string Pin(string prompt) => "x\n<!-- pg-panel:1:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
			$$"""
			{"mode":"seedandauto","sha":"s","personas":[{"id":"x","name":"X","lens":"sql-injection","tier":"diff","provider":"openrouter","model":"m","temperature":1.0,"prompt":"{{prompt}}"}]}
			""")) + " -->";

		var clean = Assert.Single(PanelCodec.Extract(Pin(body))!.Personas);
		var padded = Assert.Single(PanelCodec.Extract(Pin("\\n   " + body + "\\n  "))!.Personas);

		Assert.Equal("Lens: sql-injection\nThe hazard you were convened for: raw interpolation", clean.Brief);
		Assert.Equal(clean.Brief, padded.Brief);
		Assert.Equal(clean.SystemPrompt, padded.SystemPrompt);
	}

	[Fact]
	public void A_seed_prompt_matching_both_anchors_is_migrated_the_known_residual()
	{
		// The residual, pinned so it is visible rather than assumed away. Matching two frozen
		// strings is inference from text, not provenance, and no amount of anchoring turns it into
		// provenance: an operator who opens with the generated opener AND closes with the whole
		// generated paragraph is migrated, and the authored middle goes.
		//
		// The trade is deliberate. Someone who reproduces both ends of a generated prompt verbatim
		// loses authority over a prompt they can re-author; the alternative leaves attacker-influenced
		// text in the operator channel on pins nobody can re-author. A kind/version discriminator
		// would settle it properly and would only help pins written after it exists.
		//
		// If this ever stops being the intended behaviour, this test is the one to change first.
		var seedPrompt = "You review one specific risk in this pull request. Escalate anything touching payments. Stay on that lens - another reviewer covers the rest, and a finding outside it is noise. Cite file:line. Report nothing rather than padding. High precision: if you cannot state a concrete failure scenario, do not raise it.";
		var json = $$"""
			{"mode":"seedandauto","sha":"s","personas":[{"id":"payments","name":"The Payments Reviewer","lens":"payments","tier":"diff","provider":"openrouter","model":"m","temperature":1.0,"prompt":"{{seedPrompt}}"}]}
			""";

		var back = PanelCodec.Extract(
			"x\n<!-- pg-panel:1:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)) + " -->");

		var persona = Assert.Single(back!.Personas);
		Assert.Equal("Lens: payments", persona.Brief);
		Assert.DoesNotContain("Escalate anything touching payments.", persona.SystemPrompt);
	}

	[Fact]
	public void A_seed_prompt_that_merely_quotes_a_brief_label_keeps_all_of_its_instructions()
	{
		// The collision the migration must not cause. An operator writing about the panel's own
		// wording - plausible in this repo of all repos - would otherwise have every surrounding
		// instruction replaced by the convened doctrine and the quoted line moved to the user turn.
		// Matching the generated opener at position zero, rather than a label anywhere in the prose,
		// is what keeps this prompt whole.
		var seedPrompt = "You audit prompt wording. Findings often quote The hazard you were convened for: as an example. Never approve without evidence.";
		var json = $$"""
			{"mode":"seedandauto","sha":"s","personas":[{"id":"auditor","name":"The Auditor","lens":"prompts","tier":"diff","provider":"openrouter","model":"m","temperature":1.0,"prompt":"{{seedPrompt}}"}]}
			""";

		var back = PanelCodec.Extract(
			"x\n<!-- pg-panel:1:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)) + " -->");

		var persona = Assert.Single(back!.Personas);
		Assert.Null(persona.Brief);
		Assert.Equal(seedPrompt, persona.SystemPrompt);
		Assert.Contains("Never approve without evidence.", persona.SystemPrompt);
	}

	[Fact]
	public void A_persona_without_top_p_top_k_round_trips_as_null_not_a_forced_value()
	{
		// Absent stays absent (the provider default stands), and the codec must not emit the keys.
		var persona = new Persona(
			"seed", "Seed", "bugs", ReviewTier.Diff, new ModelRef("openrouter", "m"), 1.0, "review");

		var body = PanelCodec.Embed("x", new PinnedPanel([persona], PanelMode.SeedAndAuto, "sha"));
		var back = PanelCodec.Extract(body);

		Assert.Null(back!.Personas[0].TopP);
		Assert.Null(back.Personas[0].TopK);
		Assert.DoesNotContain("topP", body);
		Assert.DoesNotContain("topK", body);
	}

	[Fact]
	public void A_persona_blob_missing_temperature_decodes_to_the_non_greedy_default_not_zero()
	{
		// A panel blob that somehow lacks a persona's temperature must not silently sample at 0
		// (greedy), the reasoning-runaway mode. The codec no longer answers that question itself -
		// absent decodes to null and Persona.SamplingTemperature() resolves it, so this codec and
		// ConfigCodec cannot drift apart on what "absent" means, which is what #127 was.
		var json = """{"mode":"auto","sha":"s","personas":[{"id":"x","name":"X","lens":"l","tier":"Diff","provider":"openrouter","model":"m","prompt":"p"}]}""";
		var body = "<!-- pg-panel:1:" + System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)) + " -->";

		var back = PanelCodec.Extract(body);

		Assert.NotNull(back);
		var persona = Assert.Single(back!.Personas);
		Assert.Null(persona.Temperature); // absent stays absent - the codec does not invent a value
		Assert.Equal(PanelFence.DefaultTemperature, persona.SamplingTemperature());
		Assert.NotEqual(0.0, PanelFence.DefaultTemperature); // guards against a future 0 default
	}

	[Fact]
	public void A_pin_writes_the_resolved_temperature_so_it_freezes_what_the_panel_ran_at()
	{
		// A pin is the record of what this PR's panel actually ran at, frozen at pin time (#64).
		// Pinning a persona that never authored a temperature must therefore bake the resolved
		// value in, not re-resolve on every later turn against a default that may have moved.
		var persona = new Persona(
			"seed", "Seed", "bugs", ReviewTier.Diff, new ModelRef("openrouter", "m"), null, "review");

		var body = PanelCodec.Embed("x", new PinnedPanel([persona], PanelMode.SeedAndAuto, "sha"));
		var back = PanelCodec.Extract(body);

		Assert.Equal(PanelFence.DefaultTemperature, Assert.Single(back!.Personas).Temperature);
	}

	[Fact]
	public void An_explicit_temperature_of_zero_round_trips_unchanged_the_floor_is_not_in_the_codec()
	{
		// Only an ABSENT temperature defaults up; a persona whose temperature is explicitly 0 is
		// preserved as 0. The floor lives at the auto-derivation (PanelFence.AutoTemperature), not
		// the codec - so a seed persona legitimately pinned at 0 is not silently rewritten here.
		var persona = new Persona(
			"seed", "Seed", "bugs", ReviewTier.Diff, new ModelRef("openrouter", "m"), 0.0, "review");

		var back = PanelCodec.Extract(PanelCodec.Embed("x", new PinnedPanel([persona], PanelMode.SeedAndAuto, "sha")));

		Assert.Equal(0.0, Assert.Single(back!.Personas).Temperature);
	}

	[Theory]
	[InlineData(0.0, 1.0)]   // a seed at greedy 0 -> auto personas floored to the recommended default (#133: 1.0)
	[InlineData(0.25, 1.0)]  // below spec -> raised to the recommended temperature
	[InlineData(1.0, 1.0)]   // at the floor -> unchanged
	[InlineData(1.3, 1.3)]   // above the floor -> respected, not clamped down
	public void AutoTemperature_floors_the_inherited_seed_value(double seed, double expected) =>
		Assert.Equal(expected, PanelFence.AutoTemperature(seed));

	[Theory]
	[InlineData(0.9, 0.0, 0.9)]   // explicit wins over the seed it would otherwise inherit
	[InlineData(0.0, 0.7, 0.0)]   // an explicit, AUTHORED 0 stands - it is not floored like inheritance
	[InlineData(0.5, 0.5, 0.5)]   // explicit respected even when equal to the seed
	public void PersonaTemperature_prefers_the_explicit_value_unfloored(double @explicit, double seed, double expected) =>
		Assert.Equal(expected, PanelFence.PersonaTemperature(@explicit, seed));

	[Theory]
	[InlineData(0.0, 1.0)]        // no explicit -> falls back to the FLOORED seed inheritance (#127/#133)
	[InlineData(1.3, 1.3)]        // no explicit -> floored inheritance passes an above-floor seed through
	public void PersonaTemperature_falls_back_to_the_floored_seed_when_unset(double seed, double expected) =>
		Assert.Equal(expected, PanelFence.PersonaTemperature(null, seed));

	[Fact]
	public void The_visible_markdown_is_preserved_and_the_blob_appended()
	{
		var body = PanelCodec.Embed("### The Architect\n_No findings._", new PinnedPanel([P("a")], PanelMode.Auto, "sha"));

		Assert.StartsWith("### The Architect", body);
		Assert.True(PanelCodec.IsPinned(body));
	}

	[Fact]
	public void A_body_with_no_panel_extracts_to_null()
	{
		Assert.Null(PanelCodec.Extract("a normal human PR comment"));
		Assert.False(PanelCodec.IsPinned("a normal human PR comment"));
	}

	[Fact]
	public void A_corrupt_blob_extracts_to_null_rather_than_throwing()
	{
		Assert.Null(PanelCodec.Extract("<!-- pg-panel:1:not-valid-base64!! -->"));
		Assert.Null(PanelCodec.Extract("<!-- pg-panel:1:" + System.Convert.ToBase64String("{nope"u8) + " -->"));
	}

	[Fact]
	public void A_panel_with_no_usable_personas_reads_as_unpinned()
	{
		// Reviewing with nobody would render as a clean review, so refuse the pin instead.
		var json = System.Text.Encoding.UTF8.GetBytes("""{"mode":"auto","sha":"x","personas":[{"name":"no id"}]}""");
		var body = "<!-- pg-panel:1:" + System.Convert.ToBase64String(json) + " -->";

		Assert.Null(PanelCodec.Extract(body));
	}

	[Fact]
	public void The_panel_blob_coexists_with_a_session_blob()
	{
		// Different lifetimes, different markers: the session advances every turn, the panel
		// is written once and only read after.
		var session = new ReviewSession("sha", 2, "s", []);
		var body = PanelCodec.Embed(SessionCodec.Embed("### visible", session), new PinnedPanel([P("a")], PanelMode.Auto, "sha"));

		Assert.NotNull(SessionCodec.Extract(body));
		Assert.NotNull(PanelCodec.Extract(body));
	}

	// ---- PanelResolution ----

	[Fact]
	public void Fixed_mode_uses_the_config_and_ignores_any_pin()
	{
		// An operator editing the committed panel mid-PR must see it take effect.
		var pinned = new PinnedPanel([P("stale")], PanelMode.Fixed, "old");

		var d = PanelResolution.Resolve(PanelMode.Fixed, pinned, [P("current")]);

		Assert.Equal(PanelSource.Configured, d.Source);
		Assert.Equal(["current"], d.Panel.Select(p => p.Id));
	}

	[Fact]
	public void A_dynamic_mode_reuses_the_pinned_panel()
	{
		var pinned = new PinnedPanel([P("race-hunter")], PanelMode.Auto, "open-sha");

		var d = PanelResolution.Resolve(PanelMode.Auto, pinned, [P("something-else")]);

		Assert.Equal(PanelSource.Pinned, d.Source);
		Assert.Equal(["race-hunter"], d.Panel.Select(p => p.Id));
	}

	[Fact]
	public void A_dynamic_mode_with_no_pin_asks_for_generation()
	{
		var d = PanelResolution.Resolve(PanelMode.Auto, null, [P("configured")]);

		Assert.Equal(PanelSource.NeedsGeneration, d.Source);
		Assert.Empty(d.Panel);
	}

	// ---- Merge ----

	[Fact]
	public void Merge_keeps_the_seed_and_appends_the_generated()
	{
		var merged = PanelResolution.Merge([P("bug-hunter")], [P("race-hunter", "concurrency")]);

		Assert.Equal(["bug-hunter", "race-hunter"], merged.Select(p => p.Id));
	}

	[Fact]
	public void A_generated_persona_colliding_with_a_seed_is_renamed_not_dropped()
	{
		// The seed is hand-tuned house knowledge and wins the id; the generated persona still
		// gets to review, under its own marker.
		var merged = PanelResolution.Merge([P("bug-hunter")], [P("bug-hunter", "bugs")]);

		Assert.Equal(["bug-hunter", "bug-hunter-2"], merged.Select(p => p.Id));
		Assert.Equal(2, merged.Count);
	}

	[Fact]
	public void A_generated_persona_with_no_id_gets_one_from_its_lens()
	{
		var nameless = new Persona(
			"", "Race Hunter", "Race Condition Hunter", ReviewTier.Diff,
			new ModelRef("openrouter", "m"), 0.2, "p");

		var merged = PanelResolution.Merge([], [nameless]);

		Assert.Equal("race-condition-hunter", Assert.Single(merged).Id);
	}

	[Fact]
	public void Merge_bounds_the_generated_half_to_what_is_left_of_the_cap()
	{
		// The merged panel is what gets PINNED, and PanelCodec.Extract clamps on read - so an
		// oversized merge reviews at full size on the turn that planned it and silently shrinks on
		// every turn after, orphaning the comments its dropped members already own.
		var merged = PanelResolution.Merge(
			[P("architect"), P("bug-hunter")],
			[P("g1", "one"), P("g2", "two"), P("g3", "three"), P("g4", "four")],
			cap: 4);

		Assert.Equal(["architect", "bug-hunter", "g1", "g2"], merged.Select(p => p.Id));
	}

	[Fact]
	public void A_seed_over_the_cap_is_kept_whole_and_admits_nobody()
	{
		// A cap bounds what an ORCHESTRATOR may invent. An operator who configured five personas
		// said what they wanted, and silently dropping one would be a config edit nobody asked for.
		var seed = new[] { P("a"), P("b"), P("c"), P("d"), P("e") };

		var merged = PanelResolution.Merge(seed, [P("generated", "invented")], cap: 4);

		Assert.Equal(["a", "b", "c", "d", "e"], merged.Select(p => p.Id));
	}

	[Fact]
	public void Duplicate_seed_ids_collapse()
	{
		var merged = PanelResolution.Merge([P("a"), P("a")], []);

		Assert.Single(merged);
	}

	// ---- PersonaIdentity ----

	[Theory]
	[InlineData("Race Condition Hunter", "race-condition-hunter")]
	[InlineData("bug-hunter", "bug-hunter")]
	[InlineData("SQL Injection / Input Safety", "sql-injection-input-safety")]
	[InlineData("  padded  ", "padded")]
	[InlineData("Migration (backward-compat)", "migration-backward-compat")]
	public void A_lens_slugifies_to_a_stable_id(string lens, string expected) =>
		Assert.Equal(expected, PersonaIdentity.FromLens(lens));

	[Fact]
	public void An_unusable_lens_still_yields_a_usable_id()
	{
		Assert.Equal("reviewer", PersonaIdentity.FromLens(null));
		Assert.Equal("reviewer", PersonaIdentity.FromLens(""));
		Assert.Equal("reviewer", PersonaIdentity.FromLens("!!!"));
	}

	[Fact]
	public void Slugs_are_bounded_in_length()
	{
		var slug = PersonaIdentity.FromLens(new string('a', 200));

		Assert.Equal(PersonaIdentity.MaxLength, slug.Length);
	}

	[Fact]
	public void The_same_lens_always_derives_the_same_id()
	{
		// This is what lets a panel be reconstructed without orphaning the comment it owns.
		Assert.Equal(PersonaIdentity.FromLens("Concurrency"), PersonaIdentity.FromLens("concurrency"));
	}

	[Fact]
	public void MakeUnique_suffixes_until_free()
	{
		Assert.Equal("a", PersonaIdentity.MakeUnique([], "a"));
		Assert.Equal("a-2", PersonaIdentity.MakeUnique(["a"], "a"));
		Assert.Equal("a-3", PersonaIdentity.MakeUnique(["a", "a-2"], "a"));
		Assert.Equal("a-2", PersonaIdentity.MakeUnique(["A"], "a")); // ids are matched case-insensitively
	}

	// ---- pins are only as trustworthy as the comment they came from (#69 review) ----

	[Fact]
	public void An_oversized_pinned_panel_is_clamped_on_read()
	{
		// A pin bypasses PanelFence (the fence only ever saw the orchestrator's output), so a
		// forged or corrupted one must not be able to exceed what the fence would have allowed.
		var many = Enumerable.Range(0, 20).Select(i => P($"p{i}")).ToList();
		var body = PanelCodec.Embed("x", new PinnedPanel(many, PanelMode.Auto, "sha"));

		var back = PanelCodec.Extract(body);

		Assert.Equal(PanelFence.MaxPersonas, back!.Personas.Count);
	}

	[Fact]
	public void Tier_is_not_clamped_by_the_codec()
	{
		// The codec cannot tell a configured persona from an invented one, and a seed persona in
		// seedAndAuto may legitimately be agent-tier. Policing tier is the caller's job.
		var agent = new Persona(
			"seeded", "Seeded", "seeded", ReviewTier.Agent, new ModelRef("p", "m"), 0.2, "prompt");
		var back = PanelCodec.Extract(PanelCodec.Embed("x", new PinnedPanel([agent], PanelMode.Auto, "sha")));

		Assert.Equal(ReviewTier.Agent, Assert.Single(back!.Personas).Tier);
	}
}
