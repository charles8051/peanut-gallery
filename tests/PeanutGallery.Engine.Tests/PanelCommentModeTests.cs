using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// One comment for the whole panel. The personas still run independently and still hold
/// independent sessions - only the reader-facing surface is unified.
/// </summary>
public class PanelCommentModeTests
{
    private const string Repo = "acme-api";

    private static readonly ModelRef Model = new("openrouter", "some/model");

    // The system prompt carries the id so the fixture reviewer can tell the personas apart -
    // that is the only handle it has on which persona a request belongs to.
    private static Persona P(string id) => new(
        id, id, id, ReviewTier.Diff, Model, 0.2, $"review it, you are {id}");

    private static PeanutConfig Config(CommentMode? comment, params string[] ids) => new(
        Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
        Personas: ids.Select(P).ToList(),
        Repos: [new RepoTarget(Repo, ".")],
        Assignments: ids.Select(id => new Assignment(id, Repo)).ToList(),
        Verify: false,
        Comment: comment);

    private static readonly Diff SampleDiff = Diff.Parse(
        "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n");

    private static Task<Diff> Delta(ReviewSession _, CancellationToken __) => Task.FromResult(SampleDiff);

    private static ReviewRunRequest Request(
        PeanutConfig config, IReviewer reviewer,
        IReadOnlyList<ExistingComment>? existing = null, string headSha = "sha1") =>
        new(config, Repo, headSha, existing ?? [], Delta, reviewer);

    private const string OneFinding =
        """{"summary":"s","findings":[{"title":"null deref","file":"a.cs","line":7,"severity":"major"}]}""";

    [Fact]
    public async Task Per_persona_remains_the_default()
    {
        var result = await ReviewRunner.RunAsync(
            Request(Config(null, "architect", "bug-hunter"), new ScriptedReviewer(OneFinding)));

        Assert.Equal(2, result.RenderedBodies.Count);
    }

    [Fact]
    public async Task Panel_mode_posts_exactly_one_comment()
    {
        var result = await ReviewRunner.RunAsync(
            Request(Config(CommentMode.Panel, "architect", "bug-hunter"), new ScriptedReviewer(OneFinding)));

        var body = Assert.Single(result.RenderedBodies);
        Assert.Equal(CommentRenderer.Marker(PanelCommentRenderer.PanelId), CommentSync.MarkerOf(body));
    }

    [Fact]
    public async Task Two_personas_reporting_one_issue_produce_one_attributed_entry()
    {
        var result = await ReviewRunner.RunAsync(
            Request(Config(CommentMode.Panel, "architect", "bug-hunter"), new ScriptedReviewer(OneFinding)));

        var body = result.RenderedBodies[0];
        Assert.Single(body.Split("null deref").Skip(1)); // reported once, not twice
        Assert.Contains("(architect, bug-hunter)", body);
        Assert.Contains("1 duplicate report(s) merged", body);
    }

    [Fact]
    public async Task Every_personas_session_is_carried_in_the_one_comment()
    {
        var result = await ReviewRunner.RunAsync(
            Request(Config(CommentMode.Panel, "architect", "bug-hunter"), new ScriptedReviewer(OneFinding)));

        var session = PanelSessionCodec.Extract(result.RenderedBodies[0]);

        Assert.NotNull(session);
        Assert.Equal(1, session!.For("architect").Turn);
        Assert.Equal(1, session.For("bug-hunter").Turn);
    }

    [Fact]
    public async Task A_later_turn_advances_from_the_panel_state()
    {
        var config = Config(CommentMode.Panel, "architect");
        var first = await ReviewRunner.RunAsync(Request(config, new ScriptedReviewer(OneFinding)));
        var existing = new[] { new ExistingComment(1, first.RenderedBodies[0], "github-actions", IsBot: true) };

        var second = await ReviewRunner.RunAsync(
            Request(config, new ScriptedReviewer(OneFinding), existing, headSha: "sha2"));

        Assert.Equal(2, PanelSessionCodec.Extract(second.RenderedBodies[0])!.For("architect").Turn);
    }

    [Fact]
    public async Task Switching_to_panel_mode_inherits_the_per_persona_history()
    {
        // The migration path: a PR reviewed before the switch has no panel blob, so its existing
        // per-persona comments still supply each session. Losing them would silently reset the
        // review to turn one and re-raise everything already resolved.
        var prior = new ReviewSession("older", 4, "carried summary", []);
        var legacy = new[]
        {
            new ExistingComment(
                1,
                SessionCodec.Embed(CommentRenderer.Marker("architect") + "\n### A\n", prior),
                "github-actions",
                IsBot: true),
        };

        var result = await ReviewRunner.RunAsync(
            Request(Config(CommentMode.Panel, "architect"), new ScriptedReviewer(OneFinding), legacy, "sha2"));

        var carried = PanelSessionCodec.Extract(result.RenderedBodies[0])!.For("architect");
        Assert.Equal(5, carried.Turn); // advanced from 4, not restarted at 1
    }

    [Fact]
    public async Task A_failed_persona_is_named_rather_than_silently_missing()
    {
        // In per-persona mode a failure had its own comment; with one comment it must be called
        // out there or its absence is invisible.
        var reviewer = new ScriptedReviewer(OneFinding) { ThrowForPersona = "bug-hunter" };

        var result = await ReviewRunner.RunAsync(
            Request(Config(CommentMode.Panel, "architect", "bug-hunter"), reviewer));

        var body = result.RenderedBodies[0];
        Assert.Contains("Did not report", body);
        Assert.Contains("bug-hunter", body);
        Assert.Contains("null deref", body); // and the healthy persona still reported
    }

    [Fact]
    public async Task The_degradation_banner_and_the_gate_count_agree_one_runner_computed_fact()
    {
        // #130 has two degradation surfaces at two layers: the banner reads PanelMember.Reported
        // (Core projection) and the opt-in gate reads PersonaOutcome.Failed (Engine, RunSummary
        // .DegradedCount). Both are downstream of the SAME per-persona outcome the runner computes
        // once - Core cannot import PersonaOutcome, so the projection is correct, not duplicative.
        // Pin here that a real run cannot let them drift: the banner's pg-degraded:N marker must
        // carry exactly the gate's count.
        var reviewer = new ScriptedReviewer(OneFinding) { ThrowForPersona = "bug-hunter" };

        var result = await ReviewRunner.RunAsync(
            Request(Config(CommentMode.Panel, "architect", "bug-hunter"), reviewer));

        var body = result.RenderedBodies[0];
        var gateCount = RunSummary.DegradedCount(result.Personas);

        Assert.Equal(1, gateCount);
        Assert.Contains(PanelCommentRenderer.DegradedMarker(gateCount), body);
        Assert.Contains("[!WARNING]", body);
    }

    [Fact]
    public async Task A_failed_personas_history_is_still_carried_forward()
    {
        var config = Config(CommentMode.Panel, "architect", "bug-hunter");
        var first = await ReviewRunner.RunAsync(Request(config, new ScriptedReviewer(OneFinding)));
        var existing = new[] { new ExistingComment(1, first.RenderedBodies[0], "github-actions", IsBot: true) };

        var second = await ReviewRunner.RunAsync(
            Request(config, new ScriptedReviewer(OneFinding) { ThrowForPersona = "bug-hunter" }, existing, "sha2"));

        // Its session survives the failure, so the next successful turn resumes rather than resets.
        Assert.Equal(1, PanelSessionCodec.Extract(second.RenderedBodies[0])!.For("bug-hunter").Turn);
    }

    [Fact]
    public async Task A_clean_panel_review_says_so_once()
    {
        var result = await ReviewRunner.RunAsync(
            Request(Config(CommentMode.Panel, "architect", "bug-hunter"),
                new ScriptedReviewer("""{"summary":"all good","findings":[]}""")));

        Assert.Contains("_No findings._", Assert.Single(result.RenderedBodies));
    }

    // ---- the comment is the panel's standing state, not this turn's changelog (#102) ----

    [Fact]
    public async Task An_unchanged_personas_findings_survive_a_re_render()
    {
        // Observed live: a plain re-run of an already-successful run skipped every persona as
        // unchanged, re-rendered the one shared comment from scratch, and replaced seven findings
        // with "No findings" - while they sat intact in the hidden state blob.
        var config = Config(CommentMode.Panel, "architect", "bug-hunter");
        var first = await ReviewRunner.RunAsync(Request(config, new ScriptedReviewer(OneFinding)));
        var existing = new[] { new ExistingComment(1, first.RenderedBodies[0], "github-actions", IsBot: true) };

        // Same head, no new comments: every persona short-circuits as unchanged.
        var rerun = await ReviewRunner.RunAsync(
            Request(config, new ScriptedReviewer(OneFinding), existing, headSha: "sha1"));

        Assert.Equal(2, rerun.Unchanged);
        var body = rerun.RenderedBodies[0];
        Assert.Contains("null deref", body);
        Assert.DoesNotContain("_No findings._", body);
    }

    [Fact]
    public async Task An_unchanged_persona_is_not_degraded_no_banner_no_marker_no_gate_count()
    {
        // The Reported<->Failed projection has two branches. The Failed branch is pinned by the
        // banner/gate agreement test above; pin the OTHER (#130 turn-3 review): an Unchanged persona
        // is Reported (its standing review still holds - ReviewRunner keeps it on the panel line), so
        // it must NOT trip the degradation banner, the pg-degraded marker, or the gate count. If
        // ReviewRunner ever derived Reported from something narrower than "not Failed" (e.g. flipped
        // it to "== Reviewed"), an all-unchanged re-run would wrongly read as fully degraded - this
        // catches that.
        var config = Config(CommentMode.Panel, "architect", "bug-hunter");
        var first = await ReviewRunner.RunAsync(Request(config, new ScriptedReviewer(OneFinding)));
        var existing = new[] { new ExistingComment(1, first.RenderedBodies[0], "github-actions", IsBot: true) };

        var rerun = await ReviewRunner.RunAsync(
            Request(config, new ScriptedReviewer(OneFinding), existing, headSha: "sha1"));

        Assert.Equal(2, rerun.Unchanged);
        Assert.Equal(0, RunSummary.DegradedCount(rerun.Personas));
        var body = rerun.RenderedBodies[0];
        Assert.DoesNotContain("[!WARNING]", body);
        Assert.DoesNotContain(PanelCommentRenderer.DegradedMarkerPrefix, body);
    }

    [Fact]
    public async Task An_unchanged_persona_stays_on_the_panel_line_rather_than_reading_as_absent()
    {
        // Nothing moved since it reviewed, so its review still stands - per-persona mode says the
        // same thing by leaving its comment untouched. "Did not report" made a fully-reviewed PR
        // read as never reviewed, which is how #102 was reported.
        var config = Config(CommentMode.Panel, "architect");
        var first = await ReviewRunner.RunAsync(Request(config, new ScriptedReviewer(OneFinding)));
        var existing = new[] { new ExistingComment(1, first.RenderedBodies[0], "github-actions", IsBot: true) };

        var rerun = await ReviewRunner.RunAsync(
            Request(config, new ScriptedReviewer(OneFinding), existing, headSha: "sha1"));

        var body = rerun.RenderedBodies[0];
        Assert.Contains("_Panel: architect", body);
        Assert.DoesNotContain("Did not report", body);
    }

    [Fact]
    public async Task A_failed_personas_earlier_findings_are_not_wiped_from_the_panel()
    {
        var config = Config(CommentMode.Panel, "bug-hunter");
        var first = await ReviewRunner.RunAsync(Request(config, new ScriptedReviewer(OneFinding)));
        var existing = new[] { new ExistingComment(1, first.RenderedBodies[0], "github-actions", IsBot: true) };

        var second = await ReviewRunner.RunAsync(
            Request(config, new ScriptedReviewer(OneFinding) { ThrowForPersona = "bug-hunter" }, existing, "sha2"));

        var body = second.RenderedBodies[0];
        Assert.Contains("null deref", body);                    // still on the board
        Assert.Contains("Did not report", body);                // the outage is still disclosed
        Assert.Contains("earlier finding(s) still stand", body); // and dated, so it doesn't read as fresh
    }

    [Fact]
    public async Task A_carried_finding_that_was_already_dropped_does_not_resurface()
    {
        // The session holds the model's FULL working set, so re-rendering it verbatim would undo
        // the confidence gate and the adversarial pass in one step.
        var carried = new ReviewSession(
            "sha1", 3, "s",
            [new Finding(Severity.Major, "a.cs", 7, "still open", "b"),
             new Finding(Severity.Minor, "a.cs", 9, "was refuted", "b")],
            0,
            ["was refuted"]);
        var blob = PanelSessionCodec.Embed(
            CommentRenderer.Marker(PanelCommentRenderer.PanelId) + "\n### Peanut Gallery\n",
            new PanelSession(new Dictionary<string, ReviewSession> { ["architect"] = carried }));
        var existing = new[] { new ExistingComment(1, blob, "github-actions", IsBot: true) };

        var rerun = await ReviewRunner.RunAsync(
            Request(Config(CommentMode.Panel, "architect"), new ScriptedReviewer(OneFinding), existing, "sha1"));

        var body = rerun.RenderedBodies[0];
        Assert.Contains("still open", body);
        Assert.DoesNotContain("was refuted", body);
    }

    [Fact]
    public async Task A_transient_failure_never_marks_the_diff_as_reviewed()
    {
        // The dedup must key off a COMPLETED review, never a scheduled one: a 402/429/timeout turn
        // that reported nothing has to be retried, not short-circuited as unchanged (#102).
        var config = Config(CommentMode.Panel, "architect");
        var failed = await ReviewRunner.RunAsync(
            Request(config, new ScriptedReviewer(OneFinding) { ThrowForPersona = "architect" }));
        var existing = new[] { new ExistingComment(1, failed.RenderedBodies[0], "github-actions", IsBot: true) };

        var session = PanelSessionCodec.Extract(failed.RenderedBodies[0])!.For("architect");
        Assert.True(session.IsFirstTurn); // the diff was never marked seen

        // Same head SHA: it must still run rather than skip.
        var retry = await ReviewRunner.RunAsync(
            Request(config, new ScriptedReviewer(OneFinding), existing, headSha: "sha1"));

        Assert.Equal(0, retry.Unchanged);
        Assert.Contains("null deref", retry.RenderedBodies[0]);
    }

    [Fact]
    public async Task A_whole_panel_outage_does_not_render_an_empty_panel_line()
    {
        var result = await ReviewRunner.RunAsync(
            Request(Config(CommentMode.Panel, "architect"),
                new ScriptedReviewer(OneFinding) { ThrowForPersona = "architect" }));

        var body = result.RenderedBodies[0];
        Assert.DoesNotContain("_Panel: ._", body);
        Assert.Contains("Did not report", body);
    }

    [Fact]
    public async Task A_persona_missing_from_the_panel_blob_still_inherits_its_legacy_history()
    {
        // The blob existing does not mean it covers everyone. A persona carried in a legacy
        // per-persona comment but absent from the blob must still fall back to it - otherwise a
        // partially-migrated PR silently restarts that reviewer at turn one.
        var blob = PanelSessionCodec.Embed(
            CommentRenderer.Marker(PanelCommentRenderer.PanelId) + "\n### Peanut Gallery\n",
            new PanelSession(new Dictionary<string, ReviewSession>
            {
                ["architect"] = new("older", 6, "architect summary", []),
            }));

        var legacyForBugHunter = SessionCodec.Embed(
            CommentRenderer.Marker("bug-hunter") + "\n### B\n",
            new ReviewSession("older", 4, "bug-hunter summary", []));

        var existing = new[]
        {
            new ExistingComment(1, blob, "github-actions", IsBot: true),
            new ExistingComment(2, legacyForBugHunter, "github-actions", IsBot: true),
        };

        var result = await ReviewRunner.RunAsync(
            Request(Config(CommentMode.Panel, "architect", "bug-hunter"),
                new ScriptedReviewer(OneFinding), existing, "sha2"));

        var sessions = PanelSessionCodec.Extract(result.RenderedBodies[0])!;
        Assert.Equal(7, sessions.For("architect").Turn);   // from the blob
        Assert.Equal(5, sessions.For("bug-hunter").Turn);  // from the legacy comment, not restarted
    }

    private sealed class ScriptedReviewer(string reply) : IReviewer
    {
        public string? ThrowForPersona { get; init; }

        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            // The persona is identifiable from its system prompt in these fixtures.
            if (ThrowForPersona is { } id && Msg.System(request).Contains(id, System.StringComparison.Ordinal))
            {
                throw new System.InvalidOperationException("provider exploded");
            }

            return Task.FromResult(ModelReply.Untracked(reply));
        }
    }
}
