using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// Whole-file context is spent only where it buys something: the diff tier (which cannot read
/// files itself) and the first turn (later turns carry a session that already read the code).
/// </summary>
public class ContextSourceTests
{
    private const string Repo = "acme-api";

    private static Persona Persona(string id, ReviewTier tier = ReviewTier.Diff) => new(
        id, id, "bugs", tier, new ModelRef("openrouter", "some/model"), 0.2, "review it");

    private static PeanutConfig Config(params Persona[] personas) => new(
        Providers: [new ProviderConfig("openrouter", "https://x", "OPENROUTER_API_KEY")],
        Personas: personas.ToList(),
        Repos: [new RepoTarget(Repo, ".")],
        Assignments: personas.Select(p => new Assignment(p.Id, Repo)).ToList());

    private static readonly Diff SampleDiff = Diff.Parse(
        "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n");

    private static Task<Diff> Delta(ReviewSession _, CancellationToken __) => Task.FromResult(SampleDiff);

    private sealed class CountingContext
    {
        public int Calls;

        public IReadOnlyList<string>? LastPaths;

        public Task<IReadOnlyList<FileContext>> Read(IReadOnlyList<string> paths, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastPaths = paths;
            return Task.FromResult<IReadOnlyList<FileContext>>([new FileContext("x.cs", "class X { }")]);
        }
    }

    private static ReviewRunRequest Request(
        PeanutConfig config, CountingContext ctx, IReadOnlyList<ExistingComment>? existing = null) =>
        new(config, Repo, "sha1", existing ?? [], Delta, new StubReviewer(), ContextSource: ctx.Read);

    [Fact]
    public async Task Context_is_requested_for_the_changed_files_on_a_first_turn()
    {
        var ctx = new CountingContext();

        await ReviewRunner.RunAsync(Request(Config(Persona("architect")), ctx));

        Assert.Equal(1, ctx.Calls);
        Assert.Equal(["x.cs"], ctx.LastPaths);
    }

    [Fact]
    public async Task Agent_tier_personas_do_not_spend_tokens_on_context()
    {
        // They have RepoTools and can go read whatever they need.
        var ctx = new CountingContext();

        await ReviewRunner.RunAsync(Request(Config(Persona("contrarian", ReviewTier.Agent)), ctx));

        Assert.Equal(0, ctx.Calls);
    }

    [Fact]
    public async Task Continued_turns_do_not_re_send_whole_file_context()
    {
        var ctx = new CountingContext();
        var prior = new ReviewSession("older", 1, "running", []);
        var existing = new[]
        {
            new ExistingComment(
                1,
                SessionCodec.Embed(CommentRenderer.Marker("architect") + "\n### A\n", prior),
                "github-actions",
                IsBot: true),
        };

        await ReviewRunner.RunAsync(Request(Config(Persona("architect")), ctx, existing));

        Assert.Equal(0, ctx.Calls);
    }

    [Fact]
    public async Task A_run_with_no_context_source_still_works()
    {
        var req = new ReviewRunRequest(
            Config(Persona("architect")), Repo, "sha1", [], Delta, new StubReviewer());

        var result = await ReviewRunner.RunAsync(req);

        Assert.Equal(PersonaOutcome.Reviewed, Assert.Single(result.Personas).Outcome);
    }
}
