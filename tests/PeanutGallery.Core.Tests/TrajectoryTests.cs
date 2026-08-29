using System.Collections.Generic;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

/// <summary>
/// Pigeonholing is invisible in any single diff - every persona is handed one change and asked what
/// is wrong with it - and visible in the trajectory. These are the arithmetic, deliberately with no
/// model in sight: detection is cheap, so the expensive part can be spent only when it fires.
/// </summary>
public class TrajectoryTests
{
	private static Diff Parse(params (string Path, int Added)[] files)
	{
		var sb = new System.Text.StringBuilder();
		foreach (var (path, added) in files)
		{
			sb.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n')
				.Append("--- a/").Append(path).Append('\n')
				.Append("+++ b/").Append(path).Append('\n')
				.Append("@@ -0,0 +1,").Append(added).Append(" @@\n");
			for (var i = 0; i < added; i++)
			{
				sb.Append("+line").Append(i).Append('\n');
			}
		}

		return Diff.Parse(sb.ToString());
	}

	// ---- DiffShape ----

	[Fact]
	public void Shape_splits_added_lines_by_test_path()
	{
		var shape = DiffShape.Of(Parse(
			("src/PeanutGallery.Core/Thing.cs", 10),
			("tests/PeanutGallery.Core.Tests/ThingTests.cs", 40)));

		Assert.Equal(2, shape.Files);
		Assert.Equal(50, shape.Added);
		Assert.Equal(40, shape.TestAdded);
		Assert.Equal(10, shape.ProductionAdded);
	}

	[Theory]
	[InlineData("tests/Foo.cs", true)]
	[InlineData("test/Foo.cs", true)]
	[InlineData("src/PeanutGallery.Core.Tests/Foo.cs", true)]
	[InlineData("Acme.Api.Tests/Services/Foo.cs", true)]
	[InlineData("src/Contest/Foo.cs", false)]      // substring matching would call this a test
	[InlineData("src/Latest/Foo.cs", false)]
	[InlineData("src/PeanutGallery.Core/Foo.cs", false)]
	public void Test_paths_are_recognised_by_segment_not_substring(string path, bool isTest)
	{
		var shape = DiffShape.Of(Parse((path, 5)));

		Assert.Equal(isTest ? 5 : 0, shape.TestAdded);
	}

	// ---- the fold ----

	[Fact]
	public void One_run_is_not_a_trajectory()
	{
		// Two points are a line, not a trend; one point is not even that.
		Assert.Null(Trajectory.Of([new DiffShape(1, 10, 0, 0)]));
	}

	[Fact]
	public void An_untyped_empty_collection_expression_still_compiles()
	{
		// A COMPILE-TIME regression guard, and the assertion is almost incidental. `Of([])` is the
		// obvious way to write the no-runs case, and an empty collection expression has no element
		// type to resolve an overload with - so a second `Of` taking a different element type makes
		// this line CS0121 for every existing and downstream caller. That happened once (#175
		// review) and the turn-taking fold is named OfTurns because of it. If someone adds an `Of`
		// overload later, this file stops building rather than their callers'.
		Assert.Null(Trajectory.Of([]));
	}

	[Fact]
	public void A_scaffolding_runaway_trips_the_trigger()
	{
		// The motivating case, with its real numbers (git diff --numstat against the merge-base at
		// each of the PR's four commits): production flat at 10 throughout while the guardrail went
		// 102 -> 149 -> 255 -> 343. Every step was justified by the previous step's findings.
		var t = Trajectory.Of([
			new DiffShape(3, 112, 6, 102),
			new DiffShape(3, 159, 6, 149),
			new DiffShape(3, 265, 6, 255),
			new DiffShape(3, 353, 6, 343),
		]);

		Assert.NotNull(t);
		Assert.Equal(4, t!.Turns);
		Assert.True(t.Growth > 3, $"growth was {t.Growth}");
		Assert.Equal(0, t.ProductionShare);
		Assert.True(t.LooksLikeARabbitHole);
	}

	[Fact]
	public void A_scaffolding_runaway_still_trips_it_once_production_must_exist()
	{
		// The #161 refinement must not cost the motivating case, which has production code (flat at
		// 10) and is net-additive (+347), so both new clauses pass and it still fires. This is the
		// only true positive the refinement had to protect, and it is protected.
		var t = Trajectory.Of([
			new DiffShape(3, 112, 6, 102),
			new DiffShape(3, 159, 6, 149),
			new DiffShape(3, 265, 6, 255),
			new DiffShape(3, 353, 6, 343),
		]);

		Assert.Equal(10, t!.PeakProductionAdded);
		Assert.Equal(347, t.Last.Net);
		Assert.True(t.LooksLikeARabbitHole);
	}

	[Fact]
	public void A_test_only_fix_no_longer_trips_it()
	{
		// #161's first false positive, with its real numbers: one test file, 22 -> 54 -> 103 added,
		// every line of it test code. The first three clauses all pass - 5 turns, 4.7x, share 0 -
		// and they always will on a test-only PR, because a share of zero is arithmetic there rather
		// than a signal. There is no production code to have stalled, so this is not a rabbit hole.
		var t = Trajectory.Of([
			new DiffShape(1, 22, 2, 22),
			new DiffShape(1, 54, 4, 54),
			new DiffShape(1, 103, 4, 103),
		]);

		Assert.True(t!.Growth >= Trajectory.GrowthTrigger, $"growth was {t.Growth}");
		Assert.Equal(0, t.ProductionShare);
		Assert.Equal(0, t.PeakProductionAdded);
		Assert.False(t.LooksLikeARabbitHole);
	}

	[Fact]
	public void A_net_shrinking_change_does_not_trip_it_however_its_added_count_moved()
	{
		// #161's sharpest false positive: a PR that DELETES a flaky test. Added went 9 -> 18
		// as the deletion was reworked, which is 2.0x, while Removed sat at 39 the whole time - so
		// the PR scored a runaway while going net -21. Growth counts added lines only; Net is what
		// says whether the change is growing at all.
		var t = Trajectory.Of([
			new DiffShape(1, 9, 39, 9),
			new DiffShape(1, 18, 39, 18),
			new DiffShape(1, 18, 39, 18),
		]);

		Assert.Equal(Trajectory.GrowthTrigger, t!.Growth);
		Assert.Equal(-21, t.Last.Net);
		Assert.False(t.LooksLikeARabbitHole);
	}

	[Fact]
	public void A_net_shrinking_change_with_real_production_code_is_still_spared()
	{
		// The two new clauses are independent, and this pins the second one on its own: production
		// code is present and flat (so PeakProductionAdded passes), the growth is all scaffolding
		// (so ProductionShare passes), but the change is net negative - a large deletion being
		// reworked, not a diff running away.
		var t = Trajectory.Of([
			new DiffShape(2, 40, 200, 30),
			new DiffShape(2, 90, 200, 80),
			new DiffShape(2, 140, 200, 130),
		]);

		Assert.Equal(10, t!.PeakProductionAdded);
		Assert.Equal(0, t.ProductionShare);
		Assert.True(t.Last.Net < 0);
		Assert.False(t.LooksLikeARabbitHole);
	}

	[Fact]
	public void A_trajectory_whose_net_shrank_does_not_trip_it()
	{
		// The #162 review's case, and it fires under an earlier revision of this trigger: net falls
		// from +100 to +40 while added doubles and every added line of the growth is scaffolding.
		// Last.Net > 0 alone cannot reject it - that clause is a property of ONE snapshot, while
		// "the diff ran away" is a claim about a trend, so the trend is now checked too.
		var t = Trajectory.Of([
			new DiffShape(2, 100, 0, 90),
			new DiffShape(2, 160, 90, 150),
			new DiffShape(2, 220, 180, 210),
		]);

		Assert.True(t!.Growth >= Trajectory.GrowthTrigger);
		Assert.Equal(0, t.ProductionShare);
		Assert.True(t.PeakProductionAdded > 0);
		Assert.True(t.Last.Net > 0, "the final snapshot is still net-additive...");
		Assert.True(t.Last.Net < t.First.Net, "...but the change shrank across the trajectory");
		Assert.False(t.LooksLikeARabbitHole);
	}

	[Fact]
	public void The_three_real_firings_still_fire()
	{
		// The claim the new clauses rest on, asserted properly (#162 review, turn 3: the previous
		// version of this test compared integers and never called the predicate, so it would have
		// passed with the trigger disabled entirely). Real per-run shapes from the ledger.

		// A `test: drain ThreadSleep to zero` change. Note run 1 is ALL test (66/66), so its
		// ProductionAdded is 0: this is the case PeakProductionAdded exists to read across every
		// run rather than off the last one.
		var m1886 = Trajectory.Of([
			new DiffShape(5, 66, 15, 66),
			new DiffShape(6, 123, 19, 114),
			new DiffShape(6, 146, 22, 137),
		]);
		Assert.Equal(9, m1886!.PeakProductionAdded);
		Assert.True(m1886.LooksLikeARabbitHole);

		// A 58-file feature whose growth went 90% into tests.
		var s793 = Trajectory.Of([
			new DiffShape(24, 5129, 2, 2132),
			new DiffShape(52, 14374, 2, 10900),
			new DiffShape(58, 15560, 6, 11543),
		]);
		Assert.True(s793!.LooksLikeARabbitHole);

		// The docs PR. Fires, and the trigger means nothing by it: see #163 and
		// A_docs_change_whose_growth_is_all_tests_still_trips_it_today below.
		var s798 = Trajectory.Of([
			new DiffShape(2, 118, 42, 46),
			new DiffShape(2, 271, 42, 199),
			new DiffShape(2, 299, 42, 227),
		]);
		Assert.True(s798!.LooksLikeARabbitHole);
	}

	[Fact]
	public void A_docs_change_whose_growth_is_all_tests_still_trips_it_today()
	{
		// A KNOWN DEFECT, pinned so it is visible rather than surprising, and tracked in #163.
		// DiffShape splits added lines two ways - test, and everything else - and calls everything
		// else "production", so prose counts. A docs change whose growth lands in tests therefore
		// has production lines that never move and a share near zero: the rabbit-hole shape, made
		// of documentation. This is the docs PR, one of only three surviving firings.
		//
		// NOT fixed here: the fix needs a DocsAdded field on DiffShape, which is serialized into the
		// metrics ledger, so it is a schema change with a forward-compat contract - and historical
		// lines carry no docs data, so the docs PR keeps firing for any window covering its existing runs
		// regardless. When #163 lands, this test flips to Assert.False.
		var t = Trajectory.Of([
			new DiffShape(2, 118, 42, 46),    // 72 non-test lines - all prose
			new DiffShape(2, 271, 42, 199),   // ...flat at 72 while the tests grow
			new DiffShape(2, 299, 42, 227),
		]);

		Assert.Equal(72, t!.PeakProductionAdded);
		Assert.Equal(0, t.ProductionShare);
		Assert.True(t.LooksLikeARabbitHole, "known defect - see #163");
	}

	[Fact]
	public void A_docs_only_change_cannot_trip_it_because_its_growth_is_not_scaffolding()
	{
		// #162's review read PeakProductionAdded as letting a docs-only PR through. It does count
		// docs as non-test - the XML doc now says so plainly - but a docs-only PR still cannot
		// fire, and not because of this clause: with no test files, ALL of its growth is non-test,
		// so ProductionShare is 1.0 and the third clause rejects it long before the fourth is
		// reached. The real docs exposure is a docs PR whose growth is in TESTS (the case above),
		// which is a different shape and is documented as known.
		var t = Trajectory.Of([
			new DiffShape(1, 10, 0, 0),
			new DiffShape(1, 20, 0, 0),
			new DiffShape(1, 25, 0, 0),
		]);

		Assert.True(t!.Growth >= Trajectory.GrowthTrigger);
		Assert.True(t.PeakProductionAdded > 0, "docs count as non-test");
		Assert.Equal(1.0, t.ProductionShare);
		Assert.False(t.LooksLikeARabbitHole);
	}

	[Fact]
	public void The_pr_that_built_this_trigger_does_not_trip_it()
	{
		// Dogfood, and a deliberately awkward one. PR #156 ran five turns and grew 3.4x, which is
		// the shape of a rabbit hole - but 63% of that growth was source and prose, not scaffolding,
		// so the trigger stays quiet. Pinned as a NEGATIVE case rather than tuned until it fires:
		// two data points is not a calibration set, and a threshold moved to catch its own author
		// measures nothing.
		var t = Trajectory.Of([
			new DiffShape(3, 244, 0, 49),
			new DiffShape(4, 442, 0, 92),
			new DiffShape(6, 696, 0, 195),
			new DiffShape(7, 795, 0, 247),
			new DiffShape(7, 822, 0, 263),
		]);

		Assert.True(t!.Turns >= Trajectory.MinTurns);
		Assert.True(t.Growth >= Trajectory.GrowthTrigger);
		Assert.True(t.ProductionShare > 0.5, $"production share was {t.ProductionShare}");
		Assert.False(t.LooksLikeARabbitHole);
	}

	[Fact]
	public void A_feature_growing_in_production_does_not_trip_it()
	{
		// The false positive that matters: a PR that gets bigger because the FEATURE got bigger is
		// doing exactly what it should. Only growth that is all scaffolding is the signal.
		var t = Trajectory.Of([
			new DiffShape(2, 100, 0, 50),
			new DiffShape(4, 300, 0, 150),
		]);

		Assert.NotNull(t);
		Assert.Equal(0.5, t!.ProductionShare);
		Assert.False(t.LooksLikeARabbitHole);
	}

	[Fact]
	public void Two_turns_of_runaway_growth_is_still_too_early_to_call()
	{
		var t = Trajectory.Of([new DiffShape(1, 10, 0, 0), new DiffShape(1, 500, 0, 500)]);

		Assert.NotNull(t);
		Assert.True(t!.Growth >= Trajectory.GrowthTrigger);
		Assert.False(t.LooksLikeARabbitHole, "under MinTurns, however dramatic the growth");
	}

	[Fact]
	public void A_stable_pr_does_not_trip_it_however_many_turns_it_takes()
	{
		var t = Trajectory.Of([
			new DiffShape(2, 100, 10, 40),
			new DiffShape(2, 104, 10, 42),
			new DiffShape(2, 108, 12, 44),
			new DiffShape(2, 110, 12, 45),
		]);

		Assert.False(t!.LooksLikeARabbitHole);
	}

	[Fact]
	public void An_empty_first_run_reports_no_growth_rather_than_infinity()
	{
		// Dividing by a zero baseline would make every later run look infinitely runaway.
		var t = Trajectory.Of([DiffShape.Empty, new DiffShape(1, 400, 0, 400)]);

		Assert.Equal(1.0, t!.Growth);
		Assert.False(t.LooksLikeARabbitHole);
	}

	[Fact]
	public void A_shrinking_pr_has_a_defined_production_share()
	{
		// Nothing grew, so there is no growth to attribute; the share must not divide by zero or go
		// negative and accidentally read as "all scaffolding".
		var t = Trajectory.Of([new DiffShape(2, 200, 0, 100), new DiffShape(1, 50, 0, 25)]);

		Assert.Equal(1.0, t!.ProductionShare);
		Assert.False(t.LooksLikeARabbitHole);
	}

	// ---- grouping across a ledger ----

	[Fact]
	public void Runs_are_folded_per_pr_in_ledger_order()
	{
		// Ledger lines are appended in run order, so file order IS turn order - the timestamp is a
		// display string the shell stamps, not something to sort on.
		var runs = new List<RunMetrics>
		{
			Run(1, new DiffShape(1, 100, 0, 90)),
			Run(2, new DiffShape(1, 10, 0, 0)),
			Run(1, new DiffShape(1, 400, 0, 390)),
			Run(1, new DiffShape(1, 900, 0, 890)),
		};

		var byPr = Trajectory.ByPr(runs);

		Assert.Equal(3, byPr[new PrRef("acme/api", 1)].Turns);
		Assert.True(byPr[new PrRef("acme/api", 1)].LooksLikeARabbitHole);
		// one run is not a trajectory
		Assert.DoesNotContain(new PrRef("acme/api", 2), byPr.Keys);
	}

	[Fact]
	public void Runs_with_no_recorded_shape_are_skipped_not_treated_as_empty()
	{
		// The bug this replaced silently disabled the whole measurement: every ledger line predating
		// the field became a factual zero-added baseline, which pinned growth at 1.0 and meant the
		// trigger could never fire on any PR that already had history - which is all of them. A
		// measurement that reports "nothing ever trips" because it erased its own inputs is worse
		// than no measurement.
		var runs = new List<RunMetrics>
		{
			Run(1, null),                        // written before the field existed
			Run(1, new DiffShape(1, 100, 0, 90)),
			Run(1, new DiffShape(1, 400, 0, 390)),
			Run(1, new DiffShape(1, 900, 0, 890)),
		};

		var t = Trajectory.ByPr(runs)[new PrRef("acme/api", 1)];

		Assert.Equal(3, t.Turns); // the unshaped run is not a turn we can say anything about
		Assert.Equal(9.0, t.Growth);
		Assert.True(t.LooksLikeARabbitHole);
	}

	[Fact]
	public void A_pr_with_no_shaped_runs_at_all_has_no_trajectory()
	{
		var runs = new List<RunMetrics> { Run(1, null), Run(1, null) };

		Assert.Empty(Trajectory.ByPr(runs));
	}

	[Fact]
	public void Pr_numbers_do_not_collide_across_repositories()
	{
		// A PR number is only unique within a repository. Folding a window that spans two of them on
		// the number alone invents a turn history that never happened - and one long enough to trip
		// the trigger on its own.
		var runs = new List<RunMetrics>
		{
			Run(12, new DiffShape(1, 100, 0, 90), "acme/api"),
			Run(12, new DiffShape(1, 900, 0, 890), "acme/web"),
			Run(12, new DiffShape(1, 900, 0, 890), "acme/api"),
		};

		var byPr = Trajectory.ByPr(runs);

		Assert.Equal(2, byPr[new PrRef("acme/api", 12)].Turns);
		Assert.DoesNotContain(new PrRef("acme/web", 12), byPr.Keys); // one run, no trajectory
	}

	// ---- the repeat-class trigger ----

	[Fact]
	public void A_repeat_class_loop_trips_the_repeat_trigger_and_not_the_rabbit_hole_one()
	{
		// The motivating miss, with the ledger's endpoints: 15 turns, 4094 -> 8120 added, test-added
		// 226 -> 347, so 3905 of the 4026 added lines of growth - 97% - landed OUTSIDE tests. That
		// is exactly why the first trigger stays quiet (it wants <25%), and 8120/4094 is 1.98x, so
		// it would fail on growth as well. Meanwhile `heartbeat-concurrency` raised again on turns
		// 4, 6, 8, 12 and 14, each answered with a patch, each patch handing the next turn a fresh
		// diff to find the next instance in.
		//
		// The per-turn panel is RECONSTRUCTED from the issue's account rather than read off the
		// ledger: the raising turns are the five it names, and the lens is modelled as
		// auto-convened, so it sits only on the turns the orchestrator called it for. The shapes
		// here are recorded facts; the panel is the shape of the account.
		var turns = new List<Turn>();
		for (var i = 0; i < 15; i++)
		{
			// Only the endpoints enter the arithmetic - the turns between them are interpolated so
			// that there are fifteen of them.
			var added = 4094 + ((8120 - 4094) * i / 14);
			var testAdded = 226 + ((347 - 226) * i / 14);

			// A seed lens that sat every turn and raised on six of them: MORE raising turns than the
			// repeat lens has, and still not the repeat - 6 of 15 is a hit rate.
			var panel = new List<(string, int)> { ("correctness", i is 0 or 1 or 2 or 4 or 6 or 8 ? 2 : 0) };

			// Convened on turns 4, 6, 8, 10, 12 and 14; found something on all but turn 10.
			if (i is 3 or 5 or 7 or 9 or 11 or 13)
			{
				panel.Add(("heartbeat-concurrency", i == 9 ? 0 : 1));
			}

			turns.Add(Sat(new DiffShape(60, added, 400, testAdded), panel.ToArray()));
		}

		var t = Trajectory.OfTurns(turns);

		Assert.Equal(15, t!.Turns);
		Assert.Equal(4094, t.First.Added);
		Assert.Equal(8120, t.Last.Added);
		Assert.Equal(226, t.First.TestAdded);
		Assert.Equal(347, t.Last.TestAdded);
		Assert.True(t.ProductionShare > 0.96, $"production share was {t.ProductionShare}");
		Assert.Equal("heartbeat-concurrency", t.RepeatLens);
		Assert.Equal(5, t.RepeatRaiseTurns);
		Assert.Equal(6, t.RepeatLensTurns);
		Assert.True(t.LooksLikeARepeatClassLoop);
		Assert.False(t.LooksLikeARabbitHole, "97% of the growth is production - the first trigger cannot see this");
	}

	[Fact]
	public void The_scaffolding_runaway_cannot_also_be_a_repeat_class_loop()
	{
		// the calibration case's shapes, with a lens raising on every one of its four turns, so the
		// repetition clauses are fully satisfied. It still cannot trip the repeat trigger: none of
		// its growth is production, and the two triggers' production-share clauses are exact
		// complements on the same constant, so no PR is ever both diagnoses at once.
		var t = Trajectory.OfTurns([
			Sat(new DiffShape(3, 112, 6, 102), ("guardrails", 2)),
			Sat(new DiffShape(3, 159, 6, 149), ("guardrails", 1)),
			Sat(new DiffShape(3, 265, 6, 255), ("guardrails", 3)),
			Sat(new DiffShape(3, 353, 6, 343), ("guardrails", 1)),
		]);

		Assert.Equal(4, t!.RepeatRaiseTurns);
		Assert.Equal(1.0, t.RepeatShare);
		Assert.True(t.LooksLikeARabbitHole);
		Assert.False(t.LooksLikeARepeatClassLoop, "0% of the growth is production");
	}

	[Theory]
	[InlineData(3, false)] // a lens finding something on three turns is a lens finding things
	[InlineData(4, true)]  // four separate returns is where it stops looking like coincidence
	public void The_repeat_trigger_needs_four_turns_of_the_same_lens_raising(int raisingTurns, bool trips)
	{
		// Six turns throughout, so the share clause passes either way (3/6 is the boundary) and the
		// count is the only thing under test.
		var turns = new List<Turn>();
		for (var i = 0; i < 6; i++)
		{
			turns.Add(Sat(Growing(i), ("perf", i < raisingTurns ? 1 : 0)));
		}

		var t = Trajectory.OfTurns(turns);

		Assert.Equal(raisingTurns, t!.RepeatRaiseTurns);
		Assert.True(t.RepeatShare >= Trajectory.RepeatShareTrigger, "the share clause is not what is under test");
		Assert.Equal(trips, t.LooksLikeARepeatClassLoop);
	}

	[Theory]
	[InlineData(8, true)]  // raised on 4 of 8 - exactly half, the boundary, and it counts
	[InlineData(9, false)] // raised on 4 of 9 - it sat more often than it found, which is a hit rate
	public void The_repeat_lens_must_raise_on_most_of_the_turns_it_actually_sat(int satTurns, bool trips)
	{
		// The denominator is turns the LENS SAT, not turns the PR had. Without it a lens seeded on
		// every turn of a long PR reads as a repeat on the strength of four scattered findings.
		var turns = new List<Turn>();
		for (var i = 0; i < satTurns; i++)
		{
			turns.Add(Sat(Growing(i), ("perf", i < 4 ? 1 : 0)));
		}

		var t = Trajectory.OfTurns(turns);

		Assert.Equal(4, t!.RepeatRaiseTurns);
		Assert.Equal(satTurns, t.RepeatLensTurns);
		Assert.Equal(trips, t.LooksLikeARepeatClassLoop);
	}

	[Theory]
	[InlineData(25, true)]  // exactly the boundary, and the complement of the other trigger's "<25%"
	[InlineData(24, false)] // below it - which makes this a scaffolding runaway, the other diagnosis
	public void The_two_triggers_hand_over_at_the_same_production_share(int percentOfGrowth, bool repeats)
	{
		// 400 added lines of growth, split at the boundary. The same trajectory trips exactly one of
		// the two triggers on either side of it, which is the property that lets a reader take the
		// label at face value.
		var lastTestAdded = 50 + (400 - (percentOfGrowth * 4));
		var t = Trajectory.OfTurns([
			Sat(new DiffShape(2, 100, 10, 50), ("perf", 1)),
			Sat(new DiffShape(2, 250, 10, 200), ("perf", 1)),
			Sat(new DiffShape(2, 400, 10, 300), ("perf", 1)),
			Sat(new DiffShape(2, 500, 10, lastTestAdded), ("perf", 1)),
		]);

		Assert.Equal(percentOfGrowth / 100.0, t!.ProductionShare);
		Assert.Equal(repeats, t.LooksLikeARepeatClassLoop);
		Assert.Equal(!repeats, t.LooksLikeARabbitHole);
	}

	[Theory]
	[InlineData(300, false)] // net -40: a rework, not a loop
	[InlineData(240, false)] // net +20, down from +100: still growing, but smaller than it started
	[InlineData(100, true)]  // net +160: growing
	public void A_repeat_on_a_change_that_is_not_growing_is_not_a_loop(int lastRemoved, bool trips)
	{
		// The same two endpoint clauses the first trigger uses, for the same reasons, and no more
		// than they say.
		var t = Trajectory.OfTurns([
			Sat(new DiffShape(2, 200, 100, 0), ("perf", 1)),
			Sat(new DiffShape(2, 220, 100, 0), ("perf", 1)),
			Sat(new DiffShape(2, 240, 100, 0), ("perf", 1)),
			Sat(new DiffShape(2, 260, lastRemoved, 0), ("perf", 1)),
		]);

		Assert.Equal(4, t!.RepeatRaiseTurns);
		Assert.Equal(1.0, t.ProductionShare);
		Assert.Equal(trips, t.LooksLikeARepeatClassLoop);
	}

	[Fact]
	public void Raises_are_counted_by_turn_not_by_finding()
	{
		// Nine findings in one pass is one reviewer doing its job once. It is the RETURNING that
		// this counts, so one loud turn is not a repeat however loud it was.
		var t = Trajectory.OfTurns([
			Sat(Growing(0), ("perf", 9)),
			Sat(Growing(1), ("perf", 0)),
			Sat(Growing(2), ("perf", 0)),
			Sat(Growing(3), ("perf", 0)),
			Sat(Growing(4), ("perf", 0)),
		]);

		Assert.Equal(1, t!.RepeatRaiseTurns);
		Assert.False(t.LooksLikeARepeatClassLoop);
	}

	[Fact]
	public void The_repeat_lens_is_the_one_that_qualifies_not_the_one_with_the_most_raises()
	{
		// Ranking on raise count alone answers the wrong question. `broad` raised on five of the
		// fifteen turns it sat - a hit rate - and `narrow` on all four it was convened for. The
		// trigger asks whether SOME lens keeps coming back, so the share filters and the count only
		// ranks what survives; the other way round, `broad` would hide `narrow` and nothing fires.
		var turns = new List<Turn>();
		for (var i = 0; i < 15; i++)
		{
			var panel = new List<(string, int)> { ("broad", i < 5 ? 1 : 0) };
			if (i is 8 or 10 or 12 or 14)
			{
				panel.Add(("narrow", 1));
			}

			turns.Add(Sat(Growing(i), panel.ToArray()));
		}

		var t = Trajectory.OfTurns(turns);

		Assert.Equal("narrow", t!.RepeatLens);
		Assert.Equal(4, t.RepeatRaiseTurns);
		Assert.Equal(4, t.RepeatLensTurns);
		Assert.True(t.LooksLikeARepeatClassLoop);
	}

	[Fact]
	public void Ties_between_equally_repeating_lenses_break_on_the_id()
	{
		// The fold is a function: same input, same output, down to which lens it names.
		var turns = new List<Turn>();
		for (var i = 0; i < 4; i++)
		{
			turns.Add(Sat(Growing(i), ("zeal", 1), ("alpha", 1)));
		}

		Assert.Equal("alpha", Trajectory.OfTurns(turns)!.RepeatLens);
	}

	[Fact]
	public void Turns_with_no_recorded_panel_neither_trip_nor_suppress_the_repeat_trigger()
	{
		// Every ledger line predating per-lens counts is a shape with no panel. No lens sat, so
		// there is no denominator and no repeat - which is not the same as a panel that raised
		// nothing, and neither reading can fire.
		var t = Trajectory.Of([Growing(0), Growing(1), Growing(2), Growing(3), Growing(4)]);

		Assert.Null(t!.RepeatLens);
		Assert.Equal(0, t.RepeatRaiseTurns);
		Assert.Equal(0, t.RepeatShare);
		Assert.False(t.LooksLikeARepeatClassLoop);
	}

	[Fact]
	public void A_panel_that_raised_nothing_at_all_names_no_repeat_lens()
	{
		// A silent lens is not a candidate, so the record reports no repeat rather than an arbitrary
		// persona at a share of zero.
		var t = Trajectory.OfTurns([
			Sat(Growing(0), ("perf", 0), ("correctness", 0)),
			Sat(Growing(1), ("perf", 0), ("correctness", 0)),
			Sat(Growing(2), ("perf", 0), ("correctness", 0)),
			Sat(Growing(3), ("perf", 0), ("correctness", 0)),
		]);

		Assert.Null(t!.RepeatLens);
		Assert.False(t.LooksLikeARepeatClassLoop);
	}

	[Fact]
	public void A_failed_review_still_counts_as_a_turn_the_lens_sat()
	{
		// Why the panel is keyed by persona ID and not by the Lens string: a failed review carries
		// no lens, so keying on the lens would drop that turn out of the denominator - inflating the
		// exact share the trigger reads - and file it under a phantom "" lens besides. Here `perf`
		// sat five turns and raised on four, which is 80% and fires; by lens it would be 4 of 4.
		var runs = new List<RunMetrics>
		{
			RunWith(1, Growing(0), P("perf", "concurrency", 1)),
			RunWith(1, Growing(1), P("perf", "concurrency", 1)),
			RunWith(1, Growing(2), P("perf", "", 0, FailureClass.Timeout)),
			RunWith(1, Growing(3), P("perf", "concurrency", 1)),
			RunWith(1, Growing(4), P("perf", "concurrency", 1)),
		};

		var t = Trajectory.ByPr(runs)[new PrRef("acme/api", 1)];

		Assert.Equal("perf", t.RepeatLens);
		Assert.Equal(4, t.RepeatRaiseTurns);
		Assert.Equal(5, t.RepeatLensTurns);
		Assert.True(t.LooksLikeARepeatClassLoop);
	}

	[Fact]
	public void One_persona_listed_twice_in_a_run_is_still_one_turn_it_sat()
	{
		var turn = Turn.Of(new DiffShape(1, 10, 0, 0), [P("perf", "concurrency", 2), P("perf", "concurrency", 3)]);

		Assert.Equal(5, Assert.Single(turn.RaisesByLens).Value);
	}

	[Fact]
	public void A_turn_snapshots_its_panel_so_a_later_write_cannot_change_it()
	{
		// IReadOnlyDictionary is a read-only VIEW, not an immutable value - the caller still holds
		// the Dictionary behind it. A turn that kept the alias would not be a value: the same
		// apparent input would fold to different answers, and worse, RepeatOf reads each panel twice
		// (once to count the turn as sat, once to count it as raising), so a write landing between
		// those two reads could produce a share above 1.0 that nothing could reproduce. #175 review.
		var live = new Dictionary<string, int> { ["perf"] = 1 };
		var turn = new Turn(new DiffShape(1, 10, 0, 0), live);

		live["perf"] = 0;
		live["smuggled"] = 7;

		Assert.Equal(1, Assert.Single(turn.RaisesByLens).Value);
	}

	[Fact]
	public void The_snapshot_survives_a_with_expression()
	{
		// `with` is the other construction path, and it goes through the same init accessor - so
		// there is no route by which an aliased dictionary reaches a stored turn.
		var live = new Dictionary<string, int> { ["perf"] = 1 };
		var turn = Turn.Of(new DiffShape(1, 10, 0, 0)) with { RaisesByLens = live };

		live["perf"] = 99;

		Assert.Equal(1, Assert.Single(turn.RaisesByLens).Value);
	}

	[Fact]
	public void A_panel_that_kept_a_live_dictionary_would_change_the_fold_and_does_not()
	{
		// The property stated end to end rather than on one turn: build a trajectory, then write to
		// every dictionary that went into it. Same input, same output.
		var live = new List<Dictionary<string, int>>();
		var turns = new List<Turn>();
		for (var i = 0; i < 4; i++)
		{
			var panel = new Dictionary<string, int> { ["perf"] = 1 };
			live.Add(panel);
			turns.Add(new Turn(Growing(i), panel));
		}

		var before = Trajectory.OfTurns(turns);
		foreach (var panel in live)
		{
			panel["perf"] = 0;
			panel["late-arrival"] = 5;
		}

		Assert.Equal(before, Trajectory.OfTurns(turns));
		Assert.True(Trajectory.OfTurns(turns)!.LooksLikeARepeatClassLoop);
	}

	// ---- reporting ----

	[Fact]
	public void The_two_loop_kinds_are_reported_as_separate_diagnoses()
	{
		// A test-bloat loop and a repeat-class loop must never share a line: a reader who has to
		// work out which one a line means has been told nothing.
		var runs = new List<RunMetrics>
		{
			// the calibration case's shape - the scaffolding runaway.
			RunWith(4101, new DiffShape(3, 112, 6, 102), P("guardrails", "guardrails", 0)),
			RunWith(4101, new DiffShape(3, 159, 6, 149), P("guardrails", "guardrails", 0)),
			RunWith(4101, new DiffShape(3, 265, 6, 255), P("guardrails", "guardrails", 0)),
			RunWith(4101, new DiffShape(3, 353, 6, 343), P("guardrails", "guardrails", 0)),
			// ...and a production-churn loop, one lens raising every turn it sat.
			RunWith(4102, Growing(0), P("perf", "concurrency", 1)),
			RunWith(4102, Growing(1), P("perf", "concurrency", 1)),
			RunWith(4102, Growing(2), P("perf", "concurrency", 1)),
			RunWith(4102, Growing(3), P("perf", "concurrency", 1)),
		};

		var report = MetricsReport.From(runs);
		var text = MetricsReport.Render(report);

		Assert.Equal(new PrRef("acme/api", 4101), Assert.Single(report.RabbitHoles).Key);
		Assert.Equal(new PrRef("acme/api", 4102), Assert.Single(report.RepeatClassLoops).Key);
		Assert.Contains("Rabbit hole (scaffolding ran away, change stalled): 1 PR(s)", text);
		Assert.Contains("Repeat class (production grew, one lens kept raising): 1 PR(s)", text);
		Assert.Contains("acme/api#4102: 4 turns", text);
		Assert.Contains("perf raised on 4 of 4 turn(s) it sat", text);
		// The proxy is named where it is read, not only where it is defined.
		Assert.Contains("SAME LENS, not same finding", text);
	}

	/// <summary>A turn whose panel is spelled out: every entry is a lens that SAT that turn, and
	/// what it raised - zero included, because sitting silently is what the share divides by.</summary>
	private static Turn Sat(DiffShape shape, params (string Lens, int Raised)[] panel)
	{
		var raises = new Dictionary<string, int>();
		foreach (var (lens, raised) in panel)
		{
			raises[lens] = raised;
		}

		return new Turn(shape, raises);
	}

	/// <summary>Turn <paramref name="i"/> of a change growing entirely in production - so the repeat
	/// trigger's production and net clauses are satisfied and never the thing under test.</summary>
	private static DiffShape Growing(int i) => new(2, 100 + (50 * i), 10, 0);

	private static PersonaMetric P(string id, string lens, int raised, FailureClass failure = FailureClass.None) =>
		new(id, id, lens, "m", "Diff", failure == FailureClass.None ? "Reviewed" : "Failed",
			1, 0, 0, 0, 0, raised, 0, 0, 0, failure);

	private static RunMetrics Run(int pr, DiffShape? shape, string repo = "acme/api") =>
		new(new RunContext(repo, pr, "sha", "2026-08-07T00:00:00Z", "seedAndAuto", shape), []);

	private static RunMetrics RunWith(int pr, DiffShape shape, params PersonaMetric[] personas) =>
		new(new RunContext("acme/api", pr, "sha", "2026-08-07T00:00:00Z", "seedAndAuto", shape), personas);
}
