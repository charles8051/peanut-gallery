using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// End-to-end: what the gate suppresses and the adversarial pass refutes has to land in the
/// session, or the next push re-runs the same argument.
/// </summary>
public class DroppedMemoryWiringTests
{
    private const string Repo = "acme-api";

    private static PeanutConfig Config(bool? verify = null, double? minConfidence = null) => new(
        Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
        Personas:
        [
            new Persona("architect", "architect", "bugs", ReviewTier.Diff,
                new ModelRef("openrouter", "some/model"), 0.2, "review it"),
        ],
        Repos: [new RepoTarget(Repo, ".")],
        Assignments: [new Assignment("architect", Repo)],
        MinConfidence: minConfidence,
        Verify: verify);

    private static readonly Diff SampleDiff = Diff.Parse(
        "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n");

    private static Task<Diff> Delta(ReviewSession _, CancellationToken __) => Task.FromResult(SampleDiff);

    private static ReviewRunRequest Request(IReviewer reviewer, PeanutConfig config) =>
        new(config, Repo, "sha1", [], Delta, reviewer);

    private static IReadOnlyList<string> DroppedIn(ReviewRunResult result) =>
        SessionCodec.Extract(result.Personas[0].Body!)!.DroppedTitles;

    [Fact]
    public async Task A_refuted_finding_is_remembered_as_dropped()
    {
        var reviewer = new ScriptedReviewer(
            """{"summary":"s","findings":[{"title":"real"},{"title":"nit"}]}""",
            """{"verdicts":[{"title":"nit","verdict":"refuted"}]}""");

        var result = await ReviewRunner.RunAsync(Request(reviewer, Config()));

        Assert.Equal(["nit"], DroppedIn(result));
    }

    [Fact]
    public async Task A_low_confidence_finding_is_remembered_as_dropped()
    {
        var reviewer = new ScriptedReviewer(
            """{"summary":"s","findings":[{"title":"hedged","confidence":0.1}]}""");

        var result = await ReviewRunner.RunAsync(Request(reviewer, Config(verify: false)));

        Assert.Equal(["hedged"], DroppedIn(result));
    }

    [Fact]
    public async Task An_upheld_finding_is_not_remembered_as_dropped()
    {
        var reviewer = new ScriptedReviewer(
            """{"summary":"s","findings":[{"title":"real"}]}""",
            """{"verdicts":[{"title":"real","verdict":"upheld"}]}""");

        var result = await ReviewRunner.RunAsync(Request(reviewer, Config()));

        Assert.Empty(DroppedIn(result));
    }

    [Fact]
    public async Task Dropped_memory_reaches_the_next_turns_prompt()
    {
        var prior = new ReviewSession("older", 1, "running", [], 0, ["previously refuted"]);
        var existing = new[]
        {
            new ExistingComment(
                1,
                SessionCodec.Embed(CommentRenderer.Marker("architect") + "\n### A\n", prior),
                "github-actions",
                IsBot: true),
        };
        var reviewer = new ScriptedReviewer("""{"summary":"s","findings":[]}""");

        await ReviewRunner.RunAsync(
            new ReviewRunRequest(Config(), Repo, "sha1", existing, Delta, reviewer));

        Assert.Contains("previously refuted", Msg.User(reviewer.FirstRequest!));
        Assert.Contains("Do NOT raise them again", Msg.User(reviewer.FirstRequest));
    }

    private sealed class ScriptedReviewer(params string[] replies) : IReviewer
    {
        private int _calls;

        public ReviewRequest? FirstRequest { get; private set; }

        public Task<PersonaReview> ReviewAsync(ReviewTask task, CancellationToken ct = default) =>
            Task.FromResult(new PersonaReview(task.Persona, task.Repo, []));

        public Task<ModelReply> CompleteAsync(ReviewRequest request, string repoPath, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _calls);
            FirstRequest ??= request;
            return Task.FromResult(ModelReply.Untracked(replies[n - 1 < replies.Length ? n - 1 : replies.Length - 1]));
        }
    }
}
