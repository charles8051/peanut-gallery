using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// An unreadable model reply must never be posted as a clean review. The runner gets one
/// corrective re-ask; if that also fails the persona fails (and retries next push) rather
/// than reporting "no findings".
/// </summary>
public class UnreadableReplyTests
{
    private const string Repo = "acme-api";

    private static Persona Persona(string id) => new(
        id, id, "bugs", ReviewTier.Diff, new ModelRef("openrouter", "some/model"), 0.2, "review it");

    // Verification off: these tests are about the repair path, and counting calls only means
    // something if the adversarial pass isn't adding its own on top.
    private static PeanutConfig Config(params string[] ids) => new(
        Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
        Personas: ids.Select(Persona).ToList(),
        Repos: [new RepoTarget(Repo, ".")],
        Assignments: ids.Select(id => new Assignment(id, Repo)).ToList(),
        Verify: false);

    private static readonly Diff SampleDiff = Diff.Parse(
        "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n");

    private static Task<Diff> Delta(ReviewSession _, CancellationToken __) => Task.FromResult(SampleDiff);

    private static ReviewRunRequest Request(IReviewer reviewer) =>
        new(Config("architect"), Repo, "sha1", [], Delta, reviewer);

    [Fact]
    public async Task An_unreadable_reply_is_repaired_and_the_repaired_answer_is_used()
    {
        var reviewer = new ScriptedReviewer(
            "Looks good to me!",                                     // unreadable
            """{"summary":"s","findings":[{"title":"real bug","body":"b"}]}"""); // repaired

        var result = await ReviewRunner.RunAsync(Request(reviewer));

        var p = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Reviewed, p.Outcome);
        Assert.Equal(1, p.FindingCount);
        Assert.Equal(2, reviewer.Calls); // original + one repair
        Assert.Contains("could not be parsed", Msg.LastUser(reviewer.LastRequest!));
    }

    [Fact]
    public async Task A_reply_that_stays_unreadable_fails_the_persona_instead_of_reporting_clean()
    {
        var reviewer = new ScriptedReviewer("still prose", "more prose");

        var result = await ReviewRunner.RunAsync(Request(reviewer));

        var p = Assert.Single(result.Personas);
        Assert.Equal(PersonaOutcome.Failed, p.Outcome);
        Assert.Equal(0, p.FindingCount);
        Assert.Contains("could not run", p.Body);
        Assert.Equal(2, reviewer.Calls); // it does not keep re-asking
    }

    [Fact]
    public async Task A_readable_reply_is_not_re_asked()
    {
        var reviewer = new ScriptedReviewer("""{"summary":"s","findings":[]}""");

        var result = await ReviewRunner.RunAsync(Request(reviewer));

        Assert.Equal(PersonaOutcome.Reviewed, Assert.Single(result.Personas).Outcome);
        Assert.Equal(1, reviewer.Calls);
    }

    [Fact]
    public async Task A_failed_repair_preserves_the_prior_session_for_the_next_push()
    {
        var reviewer = new ScriptedReviewer("prose", "prose again");

        var result = await ReviewRunner.RunAsync(Request(reviewer));

        // The failure comment still carries state, and it is the *prior* (un-advanced) session.
        var body = Assert.Single(result.Personas).Body;
        var session = SessionCodec.Extract(body!);
        Assert.NotNull(session);
        Assert.True(session!.IsFirstTurn);
    }

    [Fact]
    public async Task The_repair_attempt_is_logged()
    {
        var lines = new List<string>();
        await ReviewRunner.RunAsync(
            Request(new ScriptedReviewer("prose", """{"summary":"s","findings":[]}""")),
            log: m => { lock (lines) lines.Add(m); });

        Assert.Contains(lines, l => l.Contains("unreadable reply") && l.Contains("re-asking"));
    }

    /// <summary>Returns the scripted replies in order; repeats the last one once exhausted.</summary>
    private sealed class ScriptedReviewer(params string[] replies) : IReviewer
    {
        private int _calls;

        public int Calls => _calls;

        public ReviewRequest? LastRequest { get; private set; }

        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            LastRequest = request;
            var i = Interlocked.Increment(ref _calls) - 1;
            return Task.FromResult(ModelReply.Untracked(replies[i < replies.Length ? i : replies.Length - 1]));
        }
    }
}
