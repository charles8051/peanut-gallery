using System.Collections.Generic;
using System.Linq;
using System.Text;
using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

public class FailureClassifierTests
{
	[Theory]
	[InlineData(null, FailureClass.None)]
	[InlineData("", FailureClass.None)]
	[InlineData("Unknown ChatFinishReason value. (Parameter 'value')", FailureClass.FinishReasonError)]
	// #158: MalformedResponse is NEVER reached from text - not from the SDK's exception wording, and
	// not from our own boundary wording either, since an unrelated failure can carry the same words
	// ("the parser could not map this value"). Only the boundary knows, and it tags structurally.
	[InlineData("Specified argument was out of the range of valid values. (Parameter 'index')", FailureClass.Other)]
	[InlineData("the provider returned a reply the SDK could not map (no choices)", FailureClass.Other)]
	[InlineData("the model returned an empty reply", FailureClass.EmptyReply)]
	[InlineData("could not be parsed after a repair attempt: the model returned an empty reply", FailureClass.EmptyReply)]
	[InlineData("the review timed out after 600s", FailureClass.Timeout)]
	// NB: both TimeBox timeouts (per-persona, and per-call since #133) are classified STRUCTURALLY by
	// the shell — it tags FailureClass.Timeout on the caught TimeoutException, so the core never has
	// to parse their wording (that structural path is tested in the engine, ReviewRunnerTimeout*).
	// This generic-substring fallback stays only for reasons that arrive with no structural kind.
	[InlineData("the review did not finish within its 600s budget", FailureClass.Other)]
	[InlineData("missing API key: environment variable OPENROUTER_API_KEY is not set", FailureClass.Config)]
	[InlineData("no provider 'openrouter' is configured", FailureClass.Config)]
	[InlineData("the reply could not be parsed", FailureClass.Other)]
	[InlineData("something else entirely", FailureClass.Other)]
	public void Classifies_reason_text_into_the_right_bucket(string? reason, FailureClass expected) =>
		Assert.Equal(expected, FailureClassifier.Classify(reason));

	[Fact]
	public void Empty_reply_beats_the_generic_parse_message_order_matters()
	{
		// The empty-reply message also contains "could not be parsed"; the more specific class wins.
		Assert.Equal(FailureClass.EmptyReply,
			FailureClassifier.Classify("reply could not be parsed after a repair attempt: the model returned an empty reply"));
	}
}

public class MetricsCodecTests
{
	private static PersonaMetric Persona(string id, FailureClass fail = FailureClass.None, int posted = 0) =>
		new(id, id + " name", "bugs", "openrouter:minimax/minimax-m3", "Diff",
			fail == FailureClass.None ? "Reviewed" : "Failed",
			1200, 30000, 400, 5000, 200, posted + 1 + 2, posted, 2, 1, fail);

	private static RunMetrics Sample() => new(
		new RunContext("acme/api", 42, "abc1234", "2026-07-27T06:00:00.0000000+00:00", "seedAndAuto"),
		[Persona("architect", posted: 3), Persona("bug-hunter", FailureClass.FinishReasonError)]);

	[Fact]
	public void Round_trips_through_one_json_line()
	{
		var line = MetricsCodec.WriteLine(Sample());
		Assert.DoesNotContain('\n', line); // one line, appendable to JSONL
		var back = MetricsCodec.ReadLine(line);

		Assert.NotNull(back);
		Assert.Equal("acme/api", back!.Context.Repo);
		Assert.Equal(42, back.Context.Pr);
		Assert.Equal("seedAndAuto", back.Context.Panel);
		Assert.Equal(2, back.Personas.Count);
		Assert.Equal(FailureClass.FinishReasonError, back.Personas[1].Failure);
		Assert.Equal(3, back.Personas[0].Posted);
	}

	[Fact]
	public void Derived_run_totals_come_from_the_persona_rows()
	{
		var m = Sample();
		Assert.Equal(1, m.Degraded);                 // one FinishReasonError
		Assert.Equal(3, m.PostedTotal);              // architect posted 3, bug-hunter 0
		Assert.Equal(4, m.RefutedTotal);             // 2 each
		Assert.Equal(1200, m.SlowestMs);             // both 1200, concurrent
		Assert.Equal((30000 + 5000) * 2, m.InputTokens); // review + verify input, both personas
	}

	[Fact]
	public void Cache_hit_rate_is_null_with_no_input_tokens_and_a_share_otherwise()
	{
		Assert.Null(new RunMetrics(Sample().Context, []).CacheHitRate);

		var m = Sample() with
		{
			Personas = [Sample().Personas[0] with { InputTokens = 1000, VerifyInputTokens = 0, CachedInputTokens = 600 }],
		};
		Assert.Equal(1000, m.InputTokens);
		Assert.Equal(600, m.CacheHitRate * m.InputTokens);
	}

	[Fact]
	public void Attempts_round_trips_and_a_pre_field_line_defaults_to_one()
	{
		var withAttempts = Sample() with
		{
			Personas = [Sample().Personas[0] with { Attempts = 3 }],
		};
		Assert.Equal(3, MetricsCodec.ReadLine(MetricsCodec.WriteLine(withAttempts))!.Personas[0].Attempts);

		// A line written before the 'at' field existed has no "at" key -> reads as 1 (one call).
		var legacy = """{"v":1,"repo":"a/b","pr":1,"sha":"s","ts":"t","panel":"fixed","p":[{"id":"x","md":"m","oc":"Reviewed"}]}""";
		Assert.Equal(1, MetricsCodec.ReadLine(legacy)!.Personas[0].Attempts);
	}

	[Fact]
	public void Cached_input_tokens_round_trip_and_a_pre_cache_line_defaults_to_zero()
	{
		var withCache = Sample() with
		{
			Personas = [Sample().Personas[0] with { CachedInputTokens = 18000, VerifyCachedInputTokens = 1000 }],
		};
		var back = MetricsCodec.ReadLine(MetricsCodec.WriteLine(withCache))!.Personas[0];
		Assert.Equal(18000, back.CachedInputTokens);
		Assert.Equal(1000, back.VerifyCachedInputTokens);

		// A line written before "cin"/"vcin" existed has neither key -> reads as 0 (no hit reported),
		// same forward-compat contract as the "at" field.
		var legacy = """{"v":1,"repo":"a/b","pr":1,"sha":"s","ts":"t","panel":"fixed","p":[{"id":"x","md":"m","oc":"Reviewed"}]}""";
		var legacyPersona = MetricsCodec.ReadLine(legacy)!.Personas[0];
		Assert.Equal(0, legacyPersona.CachedInputTokens);
		Assert.Equal(0, legacyPersona.VerifyCachedInputTokens);
	}

	// A line as it was written before "rs"/"wd" existed: no verdict keys, and a "v" below
	// RunMetrics.VerdictSchema saying so.
	private const string PreVerdictLine =
		"""{"v":1,"repo":"a/b","pr":1,"sha":"s","ts":"t","panel":"fixed","p":[{"id":"x","md":"m","oc":"Reviewed","rz":5,"po":5}]}""";

	[Fact]
	public void Author_verdicts_round_trip()
	{
		var m = Sample() with
		{
			Personas = [Sample().Personas[0] with { Resolved = 3, Withdrawn = 1 }],
		};
		var back = MetricsCodec.ReadLine(MetricsCodec.WriteLine(m))!;

		Assert.Equal(3, back.Personas[0].Resolved);
		Assert.Equal(1, back.Personas[0].Withdrawn);
		Assert.True(back.RecordsAuthorVerdicts);
	}

	[Fact]
	public void A_pre_verdict_line_reads_as_zero_but_is_not_a_recorded_zero()
	{
		var old = MetricsCodec.ReadLine(PreVerdictLine)!;
		var current = MetricsCodec.ReadLine(MetricsCodec.WriteLine(Sample()))!; // genuine 0/0

		// Both carry 0. Only the schema stamp tells them apart, and that is the whole point: an
		// average over both would report agreement across runs where nobody ever wrote it down.
		Assert.Equal(0, old.ResolvedTotal);
		Assert.Equal(0, current.ResolvedTotal);
		Assert.False(old.RecordsAuthorVerdicts);
		Assert.True(current.RecordsAuthorVerdicts);
		Assert.Equal(5, old.Personas[0].Raised); // the rest of the old line still parses
	}

	[Fact]
	public void Rewriting_a_pre_verdict_line_keeps_its_version_rather_than_laundering_it()
	{
		// Round-tripping must not upgrade the stamp. If WriteLine emitted this build's constant, the
		// first re-write of the ledger would turn every historical absence into a recorded zero.
		var rewritten = MetricsCodec.ReadLine(MetricsCodec.WriteLine(MetricsCodec.ReadLine(PreVerdictLine)!))!;
		Assert.False(rewritten.RecordsAuthorVerdicts);
		Assert.Equal(1, rewritten.SchemaVersion);
	}

	[Fact]
	public void A_line_with_no_version_at_all_claims_nothing_was_recorded()
	{
		var noVersion = MetricsCodec.ReadLine(
			"""{"repo":"a/b","pr":1,"sha":"s","ts":"t","panel":"fixed","p":[{"id":"x","md":"m","oc":"Reviewed"}]}""")!;
		Assert.Equal(0, noVersion.SchemaVersion);
		Assert.False(noVersion.RecordsAuthorVerdicts); // the safe direction for an unknown line
	}

	[Fact]
	public void An_unreadable_line_is_null_and_a_batch_drops_only_the_bad_ones()
	{
		Assert.Null(MetricsCodec.ReadLine(""));
		Assert.Null(MetricsCodec.ReadLine("not json"));
		Assert.Null(MetricsCodec.ReadLine("[1,2,3]")); // not an object

		var good = MetricsCodec.WriteLine(Sample());
		var parsed = MetricsCodec.ReadLines([good, "garbage", "", good]);
		Assert.Equal(2, parsed.Count); // two good, two dropped
	}
}

public class MetricsLedgerTests
{
	private static string Line(int pr) => MetricsCodec.WriteLine(new RunMetrics(
		new RunContext("acme/api", pr, "sha" + pr, "2026-07-27T00:00:00Z", "fixed"),
		[new PersonaMetric("a", "A", "bugs", "m", "Diff", "Reviewed", 1, 1, 1, 0, 0, 1, 1, 0, 0, FailureClass.None)]));

	[Fact]
	public void Append_then_extract_preserves_every_run_in_order()
	{
		var body = MetricsLedger.Append(null, Line(1));
		body = MetricsLedger.Append(body, Line(2));
		body = MetricsLedger.Append(body, Line(3));

		var lines = MetricsLedger.Extract(body);
		Assert.Equal(3, lines.Count);
		Assert.All(MetricsCodec.ReadLines(lines).Select((r, i) => (r, i)),
			t => Assert.Equal(t.i + 1, t.r.Context.Pr));
	}

	[Fact]
	public void The_body_carries_the_comment_sync_marker_so_it_upserts_as_its_own_comment()
	{
		var body = MetricsLedger.Append(null, Line(1));
		Assert.True(MetricsLedger.IsLedger(body));
		Assert.Equal("metrics", CommentSync.PersonaIdOf(body)); // matched/updated in place across runs
	}

	[Fact]
	public void The_cap_rolls_the_oldest_runs_off()
	{
		string? body = null;
		for (var i = 1; i <= 5; i++)
		{
			body = MetricsLedger.Append(body, Line(i), cap: 3);
		}

		var prs = MetricsCodec.ReadLines(MetricsLedger.Extract(body!)).Select(r => r.Context.Pr).ToList();
		Assert.Equal([3, 4, 5], prs); // last 3 only, oldest dropped
	}

	[Fact]
	public void Extract_of_a_non_ledger_body_is_empty()
	{
		Assert.Empty(MetricsLedger.Extract("just a normal review comment"));
		Assert.Empty(MetricsLedger.Extract(""));
	}

	// A line as review-pr actually writes one: five personas with real ids, names and lenses, a real
	// model slug, real token counts, and a diff shape. This is the unit the byte bound is calibrated
	// against — Line() above is a one-persona toy an order of magnitude smaller.
	private static string RealisticLine(int pr)
	{
		static PersonaMetric P(string id, string name, string lens) => new(
			id, name, lens, "openrouter:openai/gpt-5.6-luna", "Diff", "Reviewed",
			184_233, 62_418, 3_907, 41_002, 812, 7, 4, 2, 1, FailureClass.None, 1, 38_400, 20_100, 2, 1);

		return MetricsCodec.WriteLine(new RunMetrics(
			new RunContext("charles8051/peanut-gallery", pr, "9f2c1ab4d5e6f708192a3b4c5d6e7f8091a2b3c4",
				"2026-08-25T09:14:27.1234567+00:00", "seedAndAuto", new DiffShape(14, 612, 208, 244)),
			[
				P("bug-hunter", "Bug Hunter", "bugs"),
				P("architect", "Architect", "architecture"),
				P("skeptic", "Skeptic", "verification"),
				P("disproportion", "Disproportion", "proportionality"),
				P("contrarian", "Contrarian", "contrarian"),
			]));
	}

	[Fact]
	public void A_realistic_line_is_fifteen_hundred_bytes_not_a_few_hundred()
	{
		// Pins the measurement from #189 so the doc comment on DefaultCap cannot drift back into
		// "a metrics line is a few hundred bytes". Wide enough to survive another key being added,
		// tight enough to fail if the line changes size by an order of magnitude either way.
		var bytes = Encoding.UTF8.GetByteCount(RealisticLine(189));
		Assert.InRange(bytes, 1_200, 2_000);

		// And the arithmetic that makes a LINE cap the wrong quantity: DefaultCap lines of this size
		// base64 to several times a comment GitHub will accept, so the line cap never binds first.
		Assert.True((long)bytes * MetricsLedger.DefaultCap * 4 / 3 > MetricsLedger.GitHubCommentLimit * 5,
			$"{bytes}-byte lines x {MetricsLedger.DefaultCap} should still dwarf {MetricsLedger.GitHubCommentLimit} chars");
	}

	[Fact]
	public void A_ledger_of_realistic_runs_stays_inside_the_budget_and_drops_the_oldest_first()
	{
		string? body = null;
		for (var i = 1; i <= 80; i++)
		{
			body = MetricsLedger.Append(body, RealisticLine(i));
		}

		Assert.True(body!.Length <= MetricsLedger.BodyBudget, $"rendered {body.Length} chars");

		var prs = MetricsCodec.ReadLines(MetricsLedger.Extract(body)).Select(r => r.Context.Pr).ToList();
		Assert.Equal(80, prs[^1]);                              // the newest run is always kept
		Assert.True(prs.Count < 80, "the byte bound has to bite well before 250 lines");
		Assert.Equal(Enumerable.Range(prs[0], prs.Count), prs); // a contiguous window: oldest-first
		Assert.Equal(80 - prs.Count, MetricsLedger.EvictedCount(body));
	}

	[Fact]
	public void The_eviction_is_disclosed_in_the_rendered_text()
	{
		var whole = MetricsLedger.Append(null, RealisticLine(1));
		Assert.Contains("every review run", whole);
		Assert.DoesNotContain("rolled off", whole);
		Assert.Equal(0, MetricsLedger.EvictedCount(whole));

		string? body = null;
		for (var i = 1; i <= 60; i++)
		{
			body = MetricsLedger.Append(body, RealisticLine(i));
		}

		var evicted = MetricsLedger.EvictedCount(body!);
		Assert.True(evicted > 1);
		Assert.Contains($"({MetricsLedger.Extract(body!).Count} runs shown; at least {evicted} older runs have rolled off",
			body);
		Assert.Contains("partial history", body);
		Assert.DoesNotContain("every review run", body); // the two words that would be the lie

		// The count is a lower bound, and the sentence says so rather than naming one of the two
		// bounds: a ledger written before the marker existed cannot report what its line cap already
		// dropped, and a caller passing a small cap evicts for a reason that is not GitHub's limit.
		Assert.DoesNotContain(MetricsLedger.GitHubCommentLimit.ToString(), body);
	}

	[Fact]
	public void A_ledger_from_before_the_marker_reports_a_lower_bound_not_a_reset()
	{
		// Migration: a body the old code wrote carries lines but no eviction marker, so whatever its
		// line cap had already rolled off is unknowable. The count must not claim to be a total.
		string? legacy = null;
		for (var i = 1; i <= 5; i++)
		{
			legacy = MetricsLedger.Append(legacy, Line(i));
		}

		legacy = legacy!.Replace("<!-- pg-metrics-evicted:", "<!-- pg-nothing-here:");
		Assert.Equal(0, MetricsLedger.EvictedCount(legacy));

		var next = MetricsLedger.Append(legacy, Line(6), cap: 4);
		Assert.Equal(2, MetricsLedger.EvictedCount(next)); // only what THIS call could see
		Assert.Contains("at least 2 older runs have rolled off", next);
	}

	[Fact]
	public void The_disclosure_survives_a_later_append_that_evicts_nothing()
	{
		string? body = null;
		for (var i = 1; i <= 5; i++)
		{
			body = MetricsLedger.Append(body, Line(i), cap: 3);
		}

		Assert.Equal(2, MetricsLedger.EvictedCount(body!));

		body = MetricsLedger.Append(body, Line(6)); // roomy cap, tiny lines: nothing rolls off here
		Assert.Equal(4, MetricsLedger.Extract(body).Count);
		Assert.Equal(2, MetricsLedger.EvictedCount(body));
		Assert.Contains("at least 2 older runs have rolled off", body);
	}

	[Fact]
	public void The_newest_line_survives_even_when_it_alone_exceeds_the_budget()
	{
		// The documented choice: keep the run we were called to record and let the upsert fail (the
		// shell prints that failure), rather than post a ledger that successfully records nothing.
		var huge = MetricsCodec.WriteLine(new RunMetrics(
			new RunContext("acme/api", 7, "sha7", "2026-07-27T00:00:00Z", "auto"),
			[.. Enumerable.Range(0, 400).Select(i => new PersonaMetric(
				"persona-" + i, "Persona " + i, "lens-" + i, "openrouter:openai/gpt-5.6-luna", "Diff",
				"Reviewed", 1, 1, 1, 0, 0, 1, 1, 0, 0, FailureClass.None))]));
		Assert.True(Encoding.UTF8.GetByteCount(huge) > MetricsLedger.BodyBudget);

		string? body = null;
		for (var i = 1; i <= 5; i++)
		{
			body = MetricsLedger.Append(body, RealisticLine(i));
		}

		body = MetricsLedger.Append(body, huge);

		var kept = MetricsLedger.Extract(body!);
		Assert.Equal([huge], kept);                            // the newest, and only the newest
		Assert.True(body!.Length > MetricsLedger.BodyBudget);  // the write will fail, and visibly
		Assert.Contains("at least 5 older runs have rolled off", body); // still disclosed
	}

	[Fact]
	public void Extract_round_trips_the_window_that_survived_eviction()
	{
		string? body = null;
		for (var i = 1; i <= 50; i++)
		{
			body = MetricsLedger.Append(body, RealisticLine(i));
		}

		var runs = MetricsCodec.ReadLines(MetricsLedger.Extract(body!));
		Assert.NotEmpty(runs);
		Assert.Equal(50, runs[^1].Context.Pr);
		Assert.All(runs, r =>
		{
			Assert.Equal(5, r.Personas.Count);
			Assert.Equal("openrouter:openai/gpt-5.6-luna", r.Personas[0].Model);
			Assert.Equal(new DiffShape(14, 612, 208, 244), r.Context.Shape);
			Assert.True(r.RecordsAuthorVerdicts);
		});
	}
}

public class MetricsReportTests
{
	private static PersonaMetric P(string lens, string model, FailureClass fail, int raised, int refuted, long ms,
		int attempts = 1) =>
		new("id-" + lens, lens, lens, model, "Diff", fail == FailureClass.None ? "Reviewed" : "Failed",
			ms, 1000, 100, 0, 0, raised, raised - refuted, refuted, 0, fail, attempts);

	private static RunMetrics Run(params PersonaMetric[] ps) =>
		new(new RunContext("acme/api", 1, "s", "2026-07-27T00:00:00Z", "fixed"), ps);

	[Fact]
	public void A_personas_failures_and_successes_group_into_one_row_by_id_not_split_by_lens()
	{
		// The seed reviewer: when it reviews it carries its lens ("general"); when it TIMES OUT it has
		// no contribution and so no lens. Grouping by lens split those into two rows and hid the true
		// failure rate. Grouping by the (always-present) id merges them. Regression for the metrics
		// scrape that made "reviewer-minimax" look like a separate 100%-failing persona.
		PersonaMetric Seed(FailureClass fail, string lens) => new(
			"reviewer-minimax", "General Reviewer", lens, "minimax", "Diff",
			fail == FailureClass.None ? "Reviewed" : "Failed",
			1, 1, 1, 0, 0, 0, 0, 0, 0, fail);

		var runs = new[]
		{
			Run(Seed(FailureClass.None, "general")),
			Run(Seed(FailureClass.None, "general")),
			Run(Seed(FailureClass.None, "general")),
			Run(Seed(FailureClass.Timeout, "")),   // timed out -> no lens
		};

		var row = Assert.Single(MetricsReport.From(runs).ByPersona);
		Assert.Equal("reviewer-minimax", row.Key);
		Assert.Equal(4, row.Reviews);
		Assert.Equal(1, row.Failures);
		Assert.Equal(0.25, row.FailureRate);   // the true rate, not a split 0% / 100%
	}

	[Fact]
	public void Aggregates_failure_and_refute_rates_per_model()
	{
		var runs = new[]
		{
			Run(P("bugs", "minimax", FailureClass.None, raised: 4, refuted: 2, ms: 1000)),
			Run(P("bugs", "minimax", FailureClass.FinishReasonError, raised: 0, refuted: 0, ms: 500)),
			Run(P("arch", "deepseek", FailureClass.None, raised: 2, refuted: 0, ms: 2000)),
		};

		var report = MetricsReport.From(runs);
		Assert.Equal(3, report.Runs);
		Assert.Equal(3, report.PersonaReviews);
		Assert.Equal(1, report.Failures);

		var minimax = report.ByModel.Single(r => r.Key == "minimax");
		Assert.Equal(2, minimax.Reviews);
		Assert.Equal(0.5, minimax.FailureRate);           // 1 of 2 failed
		Assert.Equal(0.5, minimax.RefuteRate);            // 4 raised across the 2 runs, 2 refuted
		Assert.Equal("FinishReasonError", minimax.TopFailure);
	}

	[Fact]
	public void Cache_hit_tokens_aggregate_into_the_model_row_and_the_rendered_table()
	{
		var cached = P("bugs", "openai/gpt-5.6-luna", FailureClass.None, raised: 1, refuted: 0, ms: 1) with
		{
			InputTokens = 1000,
			CachedInputTokens = 750,
		};
		var report = MetricsReport.From([Run(cached)]);

		var row = report.ByModel.Single();
		Assert.Equal(750, row.CachedInputTokens);
		Assert.Equal(0.75, row.CacheHitRate);
		Assert.Contains("75%", MetricsReport.Render(report));
	}

	[Fact]
	public void Refute_rate_is_refuted_over_raised()
	{
		var report = MetricsReport.From([Run(P("bugs", "m", FailureClass.None, raised: 10, refuted: 6, ms: 1))]);
		Assert.Equal(0.6, report.ByModel.Single().RefuteRate);
	}

	[Fact]
	public void Calls_per_review_and_recovery_rate_come_from_attempts()
	{
		var runs = new[]
		{
			// Retried and recovered (2 calls, reviewed).
			Run(P("a", "minimax", FailureClass.None, raised: 1, refuted: 0, ms: 1, attempts: 2)),
			// Retried and exhausted (3 calls, still failed).
			Run(P("b", "minimax", FailureClass.FinishReasonError, raised: 0, refuted: 0, ms: 1, attempts: 3)),
			// First-try success (1 call).
			Run(P("c", "minimax", FailureClass.None, raised: 1, refuted: 0, ms: 1, attempts: 1)),
		};

		var row = MetricsReport.From(runs).ByModel.Single();
		Assert.Equal((2 + 3 + 1) / 3.0, row.CallsPerReview);   // 6 calls over 3 reviews
		Assert.Equal(2, row.MultiCall);                        // two needed more than one call
		Assert.Equal(0.5, row.RecoveryRate);                   // one of the two then succeeded

		Assert.Contains("multi-call 2, recovered 50%", MetricsReport.Render(MetricsReport.From(runs)));
	}

	[Fact]
	public void Failure_classes_are_tallied()
	{
		var runs = new[]
		{
			Run(P("a", "m", FailureClass.FinishReasonError, 0, 0, 1)),
			Run(P("b", "m", FailureClass.FinishReasonError, 0, 0, 1)),
			Run(P("c", "m", FailureClass.EmptyReply, 0, 0, 1)),
		};
		var report = MetricsReport.From(runs);
		Assert.Equal(2, report.FailureClasses["FinishReasonError"]);
		Assert.Equal(1, report.FailureClasses["EmptyReply"]);
	}

	[Fact]
	public void Agreement_is_resolved_over_the_findings_the_author_ruled_on()
	{
		var p = P("bugs", "m", FailureClass.None, raised: 10, refuted: 0, ms: 1) with
		{
			Resolved = 6,
			Withdrawn = 2,
		};
		var row = MetricsReport.From([Run(p)]).ByModel.Single();

		Assert.Equal(6, row.Resolved);
		Assert.Equal(2, row.Withdrawn);
		Assert.Equal(8, row.Judged);           // the denominator is what was RULED ON, not what was raised
		Assert.Equal(0.75, row.AgreementRate);
	}

	[Fact]
	public void A_row_nobody_ruled_on_has_no_agreement_figure_rather_than_zero()
	{
		var row = MetricsReport.From([Run(P("bugs", "m", FailureClass.None, raised: 4, refuted: 0, ms: 1))])
			.ByModel.Single();

		Assert.Equal(0, row.Judged);
		Assert.Null(row.AgreementRate); // 0/0 is unknown, and the table renders it as a dash
	}

	[Fact]
	public void Pre_verdict_runs_are_excluded_from_the_ratio_not_averaged_in_as_zero()
	{
		// Nine persona-reviews on ledger lines written before the field existed, and one that
		// recorded 3 resolved / 1 withdrawn. Counting the old nine as zero-agreement would report
		// something far below 75% over a corpus where nine tenths of it was never measured.
		var old = Run(P("bugs", "m", FailureClass.None, raised: 4, refuted: 0, ms: 1))
			with { SchemaVersion = RunMetrics.VerdictSchema - 1 };
		var recorded = Run(P("bugs", "m", FailureClass.None, raised: 4, refuted: 0, ms: 1) with
		{
			Resolved = 3,
			Withdrawn = 1,
		});

		var report = MetricsReport.From([old, old, old, old, old, old, old, old, old, recorded]);

		Assert.Equal(10, report.PersonaReviews);
		Assert.Equal(9, report.PreVerdictReviews);
		Assert.Equal(1, report.VerdictReviews);
		Assert.Equal(0.75, report.AgreementRate); // 3 of 4, over the one run that recorded anything

		var row = report.ByModel.Single();
		Assert.Equal(10, row.Reviews);
		Assert.Equal(9, row.PreVerdictReviews);
		Assert.Equal(0.75, row.AgreementRate);

		var text = MetricsReport.Render(report);
		Assert.Contains("3 resolved, 1 withdrawn across 1 of 10 persona-review(s)", text);
		Assert.Contains("EXCLUDED from the ratio rather than counted as zero", text);
		Assert.Contains("9 of 10 predate verdicts", text);
	}

	[Fact]
	public void A_window_of_only_pre_verdict_lines_reports_no_figure_at_all()
	{
		var old = Run(P("bugs", "m", FailureClass.None, raised: 4, refuted: 0, ms: 1))
			with { SchemaVersion = RunMetrics.VerdictSchema - 1 };
		var report = MetricsReport.From([old, old]);

		Assert.Equal(2, report.PreVerdictReviews);
		Assert.Equal(0, report.VerdictReviews);
		Assert.Null(report.AgreementRate);

		var text = MetricsReport.Render(report);
		Assert.Contains("NOT RECORDED on any of the 2 persona-review(s)", text);
		Assert.Contains("not\n  the same as an agreement of zero", text);
	}

	[Fact]
	public void The_ratio_is_named_agreement_and_never_precision()
	{
		var report = MetricsReport.From([Run(P("bugs", "m", FailureClass.None, raised: 4, refuted: 0, ms: 1) with
		{
			Resolved = 3,
			Withdrawn = 1,
		})]);
		var text = MetricsReport.Render(report);

		Assert.Contains("agree%", text);
		Assert.Contains("AGREEMENT, not precision", text);
		// An author can wave away a correct finding; calling this precision would claim otherwise.
		// See docs/feature-specs/finding-scope/ab-finding-scope.md for what that overstatement cost.
		Assert.DoesNotContain("precision%", text);
	}

	[Theory]
	[InlineData(0.5, 50)]   // 10..100 (10 items): rank ceil(0.5*10)=5 -> index 4 -> 50
	[InlineData(0.95, 100)] // rank ceil(0.95*10)=10 -> index 9 -> 100
	public void Percentile_is_nearest_rank(double p, long expected)
	{
		var sorted = new long[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
		Assert.Equal(expected, MetricsReport.Percentile(sorted, p));
	}

	[Fact]
	public void Percentile_of_empty_is_zero_and_renders_without_throwing()
	{
		Assert.Equal(0, MetricsReport.Percentile([], 0.5));
		var text = MetricsReport.Render(MetricsReport.From([]));
		Assert.Contains("0 run(s)", text);
	}
}
