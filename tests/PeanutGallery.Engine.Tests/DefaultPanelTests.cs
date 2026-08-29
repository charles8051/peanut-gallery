using System.IO;
using System.Linq;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

/// <summary>
/// The zero-config panel (#195). Two surfaces express it - <see cref="DefaultPanel"/> for the
/// desktop shell and <c>action/default.json</c> for the container action - and they must not
/// disagree, or the same PR gets a different panel depending on which one reviewed it.
/// </summary>
public class DefaultPanelTests
{
    private static PeanutConfig Bundled()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeanutGallery.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return ConfigCodec.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "action", "default.json")));
    }

    [Fact]
    public void The_default_panel_validates_clean()
    {
        Assert.Empty(ConfigValidation.Validate(DefaultPanel.For("acme-api")));
    }

    [Fact]
    public void The_bundled_action_config_validates_clean()
    {
        Assert.Empty(ConfigValidation.Validate(Bundled()));
    }

    [Fact]
    public void The_default_panel_seeds_so_a_failed_plan_still_leaves_reviewers()
    {
        // The floor. Under PanelMode.Auto with no configured personas, a planner that fails or
        // plans nothing resolves to an empty panel and ReviewRunner posts nothing at all - a green
        // check over a review that never ran. seedAndAuto makes the fallback two reviewers.
        var config = DefaultPanel.For("acme-api");

        Assert.Equal(PanelMode.SeedAndAuto, config.Panel);
        Assert.NotEmpty(config.Personas);
        Assert.All(config.Personas, p => Assert.Equal(ReviewTier.Diff, p.Tier));
    }

    [Fact]
    public void The_default_panel_speaks_with_one_comment_and_reconciles_behind_a_mention_gate()
    {
        var config = DefaultPanel.For("acme-api");

        Assert.Equal(CommentMode.Panel, config.Comment);
        Assert.NotNull(config.Conversation);
        Assert.Equal(ConversationMode.Reconcile, config.Conversation!.Mode);
        Assert.NotEmpty(config.Conversation.MentionTokens);
    }

    [Fact]
    public void The_seed_leaves_the_orchestrator_room_under_the_fence()
    {
        // A seed that filled the fence would make the mode auto in name only.
        var config = DefaultPanel.For("acme-api");

        Assert.True(
            config.Personas.Count < PanelFence.MaxPersonas,
            $"seed of {config.Personas.Count} leaves no slot under the cap of {PanelFence.MaxPersonas}");
    }

    [Fact]
    public void The_two_zero_config_surfaces_agree()
    {
        var bundled = Bundled();
        var built = DefaultPanel.For("repo");

        Assert.Equal(built.Panel, bundled.Panel);
        Assert.Equal(built.Comment, bundled.Comment);
        Assert.Equal(built.Orchestrator, bundled.Orchestrator);
        Assert.Equal(built.PersonaModel, bundled.PersonaModel);
        Assert.Equal(built.Conversation?.Mode, bundled.Conversation?.Mode);
        Assert.Equal(built.Conversation?.MentionTokens, bundled.Conversation?.MentionTokens);
        Assert.Equal(
            built.Personas.Select(p => p.Id).OrderBy(x => x),
            bundled.Personas.Select(p => p.Id).OrderBy(x => x));
        Assert.Equal(
            built.Personas.Select(p => p.Model.ToString()).Distinct(),
            bundled.Personas.Select(p => p.Model.ToString()).Distinct());

        // Prompts too, not just wiring. The instruction text IS the reviewer: two surfaces that
        // agree on ids and models while sending different prompts still review the same PR
        // differently, which is the drift this test exists to catch.
        Assert.Equal(
            built.Personas.OrderBy(p => p.Id).Select(p => (p.Id, p.SystemPrompt, p.SamplingTemperature())),
            bundled.Personas.OrderBy(p => p.Id).Select(p => (p.Id, p.SystemPrompt, p.SamplingTemperature())));
    }
}
