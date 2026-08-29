using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace PeanutGallery.Core;

/// <summary>
/// The shape of one run's diff: enough to tell "this change got bigger" from "this change got
/// bigger in its scaffolding".
///
/// <para><paramref name="TestAdded"/> is a SUBSET of <paramref name="Added"/>, split by a path
/// heuristic. Deliberately a heuristic: this is a metric, so a misfiled path skews a number that
/// nobody enforces on, and a wrong bucket costs a reader a raised eyebrow rather than costing an
/// author a rejected PR. It must never be promoted into a gate without being replaced by something
/// the compiler can check.</para>
/// </summary>
public sealed record DiffShape(int Files, int Added, int Removed, int TestAdded)
{
	public static readonly DiffShape Empty = new(0, 0, 0, 0);

	/// <summary>Added lines outside test paths — the part that changes what ships.</summary>
	public int ProductionAdded => Added - TestAdded;

	/// <summary>Added minus removed. Negative means the change is net SHRINKING, whatever its
	/// added-line count did — see <see cref="Trajectory.LooksLikeARabbitHole"/>, which refuses to
	/// call such a change a runaway.</summary>
	public int Net => Added - Removed;

	public static DiffShape Of(Diff diff)
	{
		var files = 0;
		var added = 0;
		var removed = 0;
		var testAdded = 0;
		foreach (var f in diff.Files)
		{
			files++;
			added += f.AddedLines;
			removed += f.RemovedLines;
			if (IsTestPath(f.Path))
			{
				testAdded += f.AddedLines;
			}
		}

		return new DiffShape(files, added, removed, testAdded);
	}

	/// <summary>A path segment that starts with "test" — covers <c>tests/</c>, <c>test/</c> and
	/// <c>Foo.Tests/</c>, which covers the common .NET layouts. Segment-wise rather than a
	/// substring so <c>src/Contest/</c> is not mistaken for a test.</summary>
	private static bool IsTestPath(string path)
	{
		foreach (var segment in path.Split('/', '\\'))
		{
			if (segment.StartsWith("test", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			// "PeanutGallery.Core.Tests" - the marker is a dotted component, not the whole segment.
			foreach (var part in segment.Split('.'))
			{
				if (part.Equals("tests", StringComparison.OrdinalIgnoreCase)
					|| part.Equals("test", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
		}

		return false;
	}
}

/// <summary>One pull request, identified the only way that is unique: repository plus number.</summary>
public sealed record PrRef(string Repo, int Pr)
{
	public override string ToString() => $"{Repo}#{Pr}";
}

/// <summary>
/// One turn as a trajectory sees it: the shape of that run's diff, plus how many findings each lens
/// on the panel raised on it. Both halves come off the same ledger line, so a trajectory stays a
/// fold over recorded facts.
///
/// <para><paramref name="RaisesByLens"/> is keyed by the persona ID rather than by
/// <see cref="PersonaMetric.Lens"/>, for the reason <see cref="MetricsReport"/> groups the same way:
/// a persona whose review FAILED carries no lens at all, so keying by lens would file that turn
/// under "" and split one lens's SILENT turns away from its raising ones. Those silent turns are the
/// denominator of <see cref="Trajectory.RepeatShare"/>, so losing them would inflate the exact
/// number this exists to read. The ID is always present, and for a convened persona it already IS
/// the lens slug.</para>
///
/// <para>A key being PRESENT means that lens sat on the panel that turn; its value is what it
/// raised, zero included. ABSENT means it did not sit at all — a real distinction under an
/// auto-convened panel, where the orchestrator picks a fresh panel per turn, and the distinction
/// <see cref="Trajectory.RepeatShare"/> is built on.</para>
///
/// <para><b>The dictionary is SNAPSHOT on the way in, not stored as handed over.</b>
/// <see cref="IReadOnlyDictionary{TKey,TValue}"/> is a read-only VIEW, not an immutable value: the
/// caller may still hold the <see cref="Dictionary{TKey,TValue}"/> behind it and write to it
/// afterwards. A value in this core that can change after it is constructed breaks "same input, same
/// output" — and it would break it invisibly here, since a trajectory reads each turn's panel twice
/// (once for the sat count, once for the raised count) and a write in between would produce a share
/// above 1.0 that no test could reproduce. The copy is <see cref="FrozenDictionary{TKey,TValue}"/>
/// rather than another <see cref="Dictionary{TKey,TValue}"/> so the guarantee is the type's and not
/// this file's: a frozen dictionary has no mutating API to downcast to.</para>
/// </summary>
public sealed record Turn(DiffShape Shape, IReadOnlyDictionary<string, int> RaisesByLens)
{
	private static readonly IReadOnlyDictionary<string, int> NoPanel =
		FrozenDictionary<string, int>.Empty;

	// Both construction paths are covered, and they are different paths: the primary constructor
	// runs this field initializer (which is why the positional parameter is read here and not
	// auto-assigned), and `with` goes through the init accessor. Neither can store an alias.
	private readonly IReadOnlyDictionary<string, int> raisesByLens = Snapshot(RaisesByLens);

	/// <inheritdoc cref="Turn"/>
	public IReadOnlyDictionary<string, int> RaisesByLens
	{
		get => this.raisesByLens;
		init => this.raisesByLens = Snapshot(value);
	}

	/// <summary>A turn with a shape and NO recorded panel — which is not "a panel that raised
	/// nothing": a turn with no lenses contributes no denominator, so it can neither trip nor
	/// suppress the repeat trigger. That is what a caller holding only shapes has.</summary>
	public static Turn Of(DiffShape shape) => new(shape, NoPanel);

	/// <summary>One ledger line's turn. Personas are folded by ID with their raise counts SUMMED, so
	/// a run that somehow lists the same persona twice still counts as one lens sitting one turn.</summary>
	public static Turn Of(DiffShape shape, IReadOnlyList<PersonaMetric> personas)
	{
		var raises = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var p in personas)
		{
			if (string.IsNullOrEmpty(p.Id))
			{
				continue;
			}

			raises[p.Id] = raises.GetValueOrDefault(p.Id) + p.Raised;
		}

		return new Turn(shape, raises);
	}

	private static IReadOnlyDictionary<string, int> Snapshot(IReadOnlyDictionary<string, int>? raises) =>
		raises is null || raises.Count == 0
			? NoPanel
			: raises.ToFrozenDictionary(StringComparer.Ordinal);
}

/// <summary>
/// What a PR's successive runs say about where the work is going.
///
/// <para>Pigeonholing is not visible in any single diff, which is why no reviewer in this system can
/// see it: every persona is handed one change and asked what is wrong with it. It is visible in the
/// TRAJECTORY. In the calibration case the production change sat flat at ten lines across four turns
/// while the diff went 153 → 200 → 306 → 394, each step justified by the previous step's findings.
/// That is what a rabbit hole looks like from the inside.</para>
///
/// <para>Two shapes are recognised, and they are MIRROR IMAGES rather than variants:
/// <see cref="LooksLikeARabbitHole"/> is a change that stopped moving while its scaffolding ran
/// away, and <see cref="LooksLikeARepeatClassLoop"/> is production code that keeps growing because
/// one lens keeps raising. They share no firing PR by construction and are reported as separate
/// diagnoses; do not collapse them.</para>
///
/// <para>Arithmetic, not judgement, and deliberately so: detection is cheap and needs no model,
/// so the expensive part can be spent only when this fires. Measurement first - the point is to
/// learn how often it would fire at all before anything is built on top of it.</para>
/// </summary>
/// <param name="Turns">Runs recorded for this PR.</param>
/// <param name="Growth">Last run's added lines over the first run's, or 1 when the first is empty.</param>
/// <param name="ProductionShare">Share of the growth that landed outside test paths, in [0, 1].
/// Near zero means the change stopped moving and its scaffolding did not.</param>
/// <param name="PeakProductionAdded">The most NON-TEST lines any single run carried — "production"
/// only in <see cref="DiffShape"/>'s sense of "not under a test path", which includes docs. Zero
/// across EVERY run means the PR is test-only, which is a different animal from a change whose
/// growth went into scaffolding. It does NOT mean docs-only: a docs change counts as non-test here,
/// so a docs-only PR whose growth is all in tests trips the trigger and the trigger means nothing
/// by it. Known and untuned — see <see cref="LooksLikeARabbitHole"/>.</param>
/// <param name="RepeatLens">The lens that kept coming back, as a persona ID (see <see cref="Turn"/>
/// for why the ID and not the lens string), or null when no lens raised anything on any turn. It is
/// the lens the repeat trigger fired on where one qualifies, and otherwise the one that raised on
/// the most turns, so a reader always sees the strongest repeat the PR has.</param>
/// <param name="RepeatRaiseTurns">Turns on which <paramref name="RepeatLens"/> raised at least one
/// finding. Turns, not findings: three findings in one turn is one reviewer doing its job in one
/// pass, and it is the RETURNING that this counts.</param>
/// <param name="RepeatLensTurns">Turns <paramref name="RepeatLens"/> sat on the panel at all — the
/// denominator, and never below <paramref name="RepeatRaiseTurns"/>.</param>
public sealed record Trajectory(
	int Turns,
	DiffShape First,
	DiffShape Last,
	double Growth,
	double ProductionShare,
	int PeakProductionAdded,
	string? RepeatLens = null,
	int RepeatRaiseTurns = 0,
	int RepeatLensTurns = 0)
{
	/// <summary>Turns before a trajectory can mean anything - two points are a line, not a trend.</summary>
	public const int MinTurns = 3;

	/// <summary>Growth multiple that counts as "the diff ran away".</summary>
	public const double GrowthTrigger = 2.0;

	/// <summary>
	/// Share of growth outside test paths below which the growth is essentially all scaffolding.
	/// </summary>
	public const double ProductionShareTrigger = 0.25;

	/// <summary>Turns one lens must have raised on before its returning is a pattern rather than a
	/// reviewer finding things. One above <see cref="MinTurns"/> on purpose: at three you can still
	/// tell an honest story about three different problems in a change that got three times bigger.
	/// Untuned beyond that — see <see cref="LooksLikeARepeatClassLoop"/>.</summary>
	public const int MinRepeatTurns = 4;

	/// <summary>Share of the turns a lens SAT on which it must also have raised. Half, so "most of
	/// the times it looked, it found something" is stated as arithmetic with an exact boundary.</summary>
	public const double RepeatShareTrigger = 0.5;

	/// <summary>Of the turns <see cref="RepeatLens"/> sat on the panel, the share it raised on; 0
	/// when no lens raised at all. Read together with <see cref="RepeatRaiseTurns"/>: the share alone
	/// says nothing, because a lens convened once and raising once is 100%.</summary>
	public double RepeatShare =>
		RepeatLensTurns == 0 ? 0 : (double)RepeatRaiseTurns / RepeatLensTurns;

	/// <summary>
	/// Still provisional, but no longer tuned by nothing: counted against 234 PRs with a trajectory
	/// across two large private repositories, where the first three clauses alone fired six times and
	/// three of those six were the same mistake (#161).
	///
	/// <para>The last two clauses are that correction. <b>A rabbit hole is a change whose growth
	/// went into scaffolding while the change itself stopped moving</b> — which presupposes there IS
	/// a change. On a test-only or docs-only PR every added line is a test line by construction, so
	/// <see cref="ProductionShare"/> is zero however well the work is going; requiring
	/// <see cref="PeakProductionAdded"/> above zero asks that production code exist before asking
	/// whether it stalled. And <see cref="Growth"/> counts added lines only, so a PR that reworks a
	/// deletion can double its added count while ending up smaller than it started: one calibration
	/// PR scored 2.0x at net −21, which is why the last run's <see cref="DiffShape.Net"/> must be
	/// positive too — a final snapshot that removes more than it adds is not a runaway. Between them
	/// these silenced all three false positives and kept all three of the other firings.</para>
	///
	/// <para>Not a gate, and must not become one without being replaced by something the compiler
	/// can check — the <see cref="DiffShape"/> test/production split is a path heuristic.</para>
	/// </summary>
	public bool LooksLikeARabbitHole =>
		Turns >= MinTurns
		&& Growth >= GrowthTrigger
		&& ProductionShare < ProductionShareTrigger
		// There is production code here at all — otherwise a share of zero is arithmetic, not a signal.
		&& PeakProductionAdded > 0
		// ...and the net grew between the ENDPOINTS. Both clauses, and no more than they say: the
		// final snapshot is net-additive (a change that ends up removing more than it adds is a
		// deletion being reworked - the 2.0x-at-net--21 case above), and the last net is at least the
		// first (a change smaller than it started did not run away). This compares two points and
		// says NOTHING about the path between them: a PR that dips to net +1 and rebounds to +101
		// satisfies both. That case is left alone deliberately. It is not obviously wrong to flag -
		// a diff that churns that hard while its production code sits still is arguably the very
		// thing this looks for - and a monotonicity or regression check would be a third heuristic
		// layer fitted to six examples, which is what #161 argued against and what the #162 review
		// kept, rightly, pushing back on. Endpoints are what the data supports; the path needs a
		// labeled corpus, and if that never arrives the honest end state is deleting the trigger
		// and reporting the raw trajectory instead (#161).
		&& Last.Net > 0
		&& Last.Net >= First.Net;

	/// <summary>
	/// The MIRROR IMAGE of <see cref="LooksLikeARabbitHole"/>, and the case it structurally cannot
	/// see: the production code is what keeps growing, because the panel keeps finding fresh
	/// instances of one class of problem. The calibration case ran 15 turns and 4094 -> 8120 added lines
	/// with 97% of that growth OUTSIDE tests, one lens raising again on turns 4, 6, 8, 12 and 14 —
	/// each answered with a patch, each patch handing the next turn a fresh diff to find the next
	/// instance in. The author's read afterwards was that three consecutive turns were the same
	/// finding with the operands rotated. The other trigger asks where the lines LANDED, so 97%
	/// production is precisely what keeps it quiet here; this one asks about REPETITION instead.
	///
	/// <para><b>The proxy is weak, and this is the honest statement of it.</b> Finding TITLES are not
	/// in the ledger — only per-persona counts are — so "the same class of problem, again" cannot be
	/// tested for. What is tested for is "the same LENS, again", which is a strictly weaker claim: a
	/// lens that finds five genuinely different real bugs across a change that doubled looks
	/// identical here to one rotating the operands on a single bug five times. No arithmetic over
	/// counts can separate those, and nothing here should be read as though it had. The most this
	/// says is "someone should check whether these were the same finding" — never "these were the
	/// same finding". Titles do exist in the session blob, so closing the gap is a schema question,
	/// and deliberately not answered here.</para>
	///
	/// <para>The production-share clause is the EXACT complement of the other trigger's, on the same
	/// constant, so no PR can ever trip both. That is deliberate: a test-bloat loop and a
	/// repeat-class loop are different diagnoses and a reader must never have to work out which one
	/// a line means. It also means the clause does very little work at 25% — it says "not a
	/// scaffolding runaway" rather than "mostly production" (that case sits at 97%, nowhere near
	/// the boundary) — and the repetition clauses are what carry the trigger.</para>
	///
	/// <para>The growth clauses are the other trigger's, unchanged and for its reasons: net-additive
	/// at the end, and no smaller than it started. Deliberately NO <see cref="GrowthTrigger"/>
	/// multiple — a change need not have run away to be stuck in a loop, and the multiple is the
	/// other trigger's whole subject. Two endpoints, saying nothing about the path between them.</para>
	///
	/// <para><b>Tuned by nothing, and reported rather than acted on.</b> <see cref="MinRepeatTurns"/>
	/// and <see cref="RepeatShareTrigger"/> are fitted to one example, which is not a calibration
	/// set; the first trigger earned its correction (#161, #162) only after running against 234 PRs,
	/// and this one has run against nothing. Detection ships first so the fire rate can be learned —
	/// it gates nothing and reaches no prompt, and must not until that backfill exists.</para>
	/// </summary>
	public bool LooksLikeARepeatClassLoop =>
		// One lens came back on four separate turns. No turn floor beside it: a lens cannot raise on
		// more turns than the PR has, so this already implies Turns >= MinRepeatTurns > MinTurns.
		RepeatRaiseTurns >= MinRepeatTurns
		// ...and it was not merely PRESENT a lot - most of the times it looked, it found something.
		// Without this clause a lens seeded on all fifteen turns and raising on four reads as a
		// repeat, when it is just a persona with a hit rate.
		&& RepeatShare >= RepeatShareTrigger
		&& ProductionShare >= ProductionShareTrigger
		&& Last.Net > 0
		&& Last.Net >= First.Net;

	/// <summary>Folds one PR's runs, oldest first, with no per-lens data: turns, growth and where
	/// the growth landed, which is everything <see cref="LooksLikeARabbitHole"/> reads. Fewer than
	/// two runs is no trajectory at all.</summary>
	public static Trajectory? Of(IReadOnlyList<DiffShape> runsOldestFirst)
	{
		var lifted = new List<Turn>(runsOldestFirst.Count);
		foreach (var shape in runsOldestFirst)
		{
			lifted.Add(Turn.Of(shape));
		}

		return OfTurns(lifted);
	}

	/// <summary>
	/// Folds one PR's turns, oldest first — shapes AND the panel that sat each one, so both triggers
	/// can be read off the result.
	///
	/// <para>NOT an <c>Of</c> overload, and this is load-bearing rather than a naming preference:
	/// an empty collection expression has no element type, so <c>Trajectory.Of([])</c> — the
	/// obvious way to write the fewer-than-two-runs case — would stop compiling with CS0121 for
	/// every existing caller the moment a second <c>Of</c> arrived. Verified against the compiler,
	/// not reasoned about; <c>Trajectory.Of([])</c> is pinned as a test so a future overload cannot
	/// quietly break it again.</para>
	/// </summary>
	public static Trajectory? OfTurns(IReadOnlyList<Turn> turnsOldestFirst)
	{
		if (turnsOldestFirst.Count < 2)
		{
			return null;
		}

		var runsOldestFirst = new List<DiffShape>(turnsOldestFirst.Count);
		foreach (var turn in turnsOldestFirst)
		{
			runsOldestFirst.Add(turn.Shape);
		}

		var first = runsOldestFirst[0];
		var last = runsOldestFirst[^1];

		// A first run with nothing in it has no baseline to have grown from; report no growth
		// rather than dividing by zero and calling every later run infinite.
		var growth = first.Added > 0 ? (double)last.Added / first.Added : 1.0;

        var addedGrowth = last.Added - first.Added;
		var productionGrowth = last.ProductionAdded - first.ProductionAdded;
		var share = addedGrowth > 0 ? Math.Clamp((double)productionGrowth / addedGrowth, 0, 1) : 1.0;

		var peakProduction = runsOldestFirst.Max(r => r.ProductionAdded);

		var (repeatLens, repeatRaiseTurns, repeatLensTurns) = RepeatOf(turnsOldestFirst);

		return new Trajectory(
			runsOldestFirst.Count, first, last, growth, share, peakProduction,
			repeatLens, repeatRaiseTurns, repeatLensTurns);
	}

	/// <summary>
	/// The lens that came back most, as (id, turns it raised on, turns it sat).
	///
	/// <para>Lenses that never raised are not candidates at all, so a PR whose panel found nothing
	/// reports no repeat rather than an arbitrary silent persona.</para>
	///
	/// <para>The pick is EXISTENTIAL, which is why the sustained set is preferred before ranking:
	/// "some lens raised on four turns and on most of the ones it sat" is the question, and ranking
	/// on raise count alone would let a lens that raised on 5 of 15 turns hide one that raised on 4
	/// of 4 and answer it wrongly. Filter on the share, rank on the count, and the winner satisfies
	/// <see cref="LooksLikeARepeatClassLoop"/> whenever any lens does. With nothing sustained the
	/// fallback is the most-repeating lens, which fires nothing and exists so a reader can still see
	/// the strongest repeat the PR has. Ties break on the ID so the fold is a function.</para>
	/// </summary>
	private static (string? Lens, int RaiseTurns, int LensTurns) RepeatOf(IReadOnlyList<Turn> turns)
	{
		var sat = new Dictionary<string, int>(StringComparer.Ordinal);
		var raised = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var turn in turns)
		{
			foreach (var (lens, count) in turn.RaisesByLens)
			{
				sat[lens] = sat.GetValueOrDefault(lens) + 1;
				if (count > 0)
				{
					raised[lens] = raised.GetValueOrDefault(lens) + 1;
				}
			}
		}

		var candidates = raised
			.Select(kv => (Lens: kv.Key, RaiseTurns: kv.Value, LensTurns: sat[kv.Key]))
			.ToList();
		if (candidates.Count == 0)
		{
			return (null, 0, 0);
		}

		var sustained = candidates
			.Where(c => (double)c.RaiseTurns / c.LensTurns >= RepeatShareTrigger)
			.ToList();

		return (sustained.Count > 0 ? sustained : candidates)
			.OrderByDescending(c => c.RaiseTurns)
			.ThenByDescending(c => (double)c.RaiseTurns / c.LensTurns)
			.ThenBy(c => c.Lens, StringComparer.Ordinal)
			.First();
	}

	/// <summary>
	/// Folds a ledger's runs into one trajectory per pull request, each ordered oldest first.
	///
	/// <para>Keyed by repo AND number: a PR number is only unique within a repository, and a window
	/// spanning two of them would otherwise fold <c>repo-a#12</c> together with <c>repo-b#12</c>
	/// into a turn history that never happened - and one long enough to trip the trigger on its
	/// own.</para>
	///
	/// <para>Runs with no recorded shape are SKIPPED, not defaulted. They are the ledger lines
	/// written before the field existed, and treating "not recorded" as "an empty diff" makes the
	/// first turn a zero baseline, which pins growth at 1.0 and silently disables the trigger for
	/// every PR that already has history.</para>
	/// </summary>
	public static IReadOnlyDictionary<PrRef, Trajectory> ByPr(IReadOnlyList<RunMetrics> runs)
	{
		var byPr = new Dictionary<PrRef, Trajectory>();
		foreach (var group in runs.GroupBy(r => new PrRef(r.Context.Repo, r.Context.Pr)))
		{
			// Ledger lines are appended in run order, so the file order IS the turn order - the
			// timestamp is a display string the shell stamps, not something to sort on.
			var turns = new List<Turn>();
			foreach (var run in group)
			{
				if (run.Context.Shape is { } shape)
				{
					turns.Add(Turn.Of(shape, run.Personas));
				}
			}

			if (OfTurns(turns) is { } t)
			{
				byPr[group.Key] = t;
			}
		}

		return byPr;
	}
}
