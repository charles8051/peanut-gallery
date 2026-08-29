using System;
using System.Text;
using PeanutGallery.Core;
using PeanutGallery.Engine;
using Xunit;

namespace PeanutGallery.Engine.Tests;

public class ConfigCodecTests
{
    private const string Json = """
        {
          "providers": [ { "name": "openrouter", "baseUrl": "https://x", "apiKeyEnv": "OPENROUTER_API_KEY" } ],
          "personas": [
            { "id": "bug-hunter", "name": "The Bug Hunter", "lens": "bugs", "tier": "diff",
              "model": { "provider": "openrouter", "modelId": "deepseek/deepseek-chat" },
              "temperature": 0.0, "systemPrompt": "find bugs" }
          ],
          "repos": [ { "name": "api", "path": "." } ],
          "assignments": [ { "personaId": "bug-hunter", "repoName": "api" } ]
        }
        """;

    [Fact]
    public void Parses_the_camelCase_shape_including_enum_and_nested_model()
    {
        var config = ConfigCodec.Parse(Json);
        var persona = Assert.Single(config.Personas);
        Assert.Equal("bug-hunter", persona.Id);
        Assert.Equal(ReviewTier.Diff, persona.Tier);
        Assert.Equal("openrouter", persona.Model.Provider);
        Assert.Equal("deepseek/deepseek-chat", persona.Model.ModelId);
        Assert.Equal("api", Assert.Single(config.Repos).Name);
        Assert.Equal("bug-hunter", Assert.Single(config.Assignments).PersonaId);
    }

    [Fact]
    public void Round_trips_through_serialize_and_parse()
    {
        // PeanutConfig has list members (reference equality), so compare the stable serialized form.
        var once = ConfigCodec.Serialize(ConfigCodec.Parse(Json));
        var twice = ConfigCodec.Serialize(ConfigCodec.Parse(once));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void Malformed_json_throws_ConfigFormatException() =>
        Assert.Throws<ConfigFormatException>(() => ConfigCodec.Parse("{ not json"));

    [Fact]
    public void An_explicit_personaTemperature_round_trips_through_the_camelCase_key()
    {
        // The auto-persona temperature is a legible config key (#129), so it must survive the
        // codec both ways: parsed off 'personaTemperature' and written back to the same key.
        var withKey = ConfigCodec.Parse(Json) with { PersonaTemperature = 0.35 };
        var reparsed = ConfigCodec.Parse(ConfigCodec.Serialize(withKey));

        Assert.Equal(0.35, reparsed.PersonaTemperature);
        Assert.Contains("\"personaTemperature\": 0.35", ConfigCodec.Serialize(withKey));
    }

    [Fact]
    public void An_absent_personaTemperature_stays_null_and_is_not_written()
    {
        // Existing configs never mention the key; it must decode to null (fall back to the floored
        // seed inheritance) and, being null, must not be emitted on serialize.
        var config = ConfigCodec.Parse(Json);
        Assert.Null(config.PersonaTemperature);
        Assert.DoesNotContain("personaTemperature", ConfigCodec.Serialize(config));
    }

    [Fact]
    public void Persona_and_auto_top_p_top_k_round_trip_through_their_camelCase_keys()
    {
        var config = ConfigCodec.Parse(Json) with
        {
            PersonaTopP = 0.95, PersonaTopK = 40,
            Personas = [ConfigCodec.Parse(Json).Personas[0] with { TopP = 0.95, TopK = 40 }],
        };
        var s = ConfigCodec.Serialize(config);
        Assert.Contains("\"personaTopP\": 0.95", s);
        Assert.Contains("\"personaTopK\": 40", s);
        Assert.Contains("\"topP\": 0.95", s);
        Assert.Contains("\"topK\": 40", s);

        var back = ConfigCodec.Parse(s);
        Assert.Equal(0.95, back.PersonaTopP);
        Assert.Equal(40, back.PersonaTopK);
        Assert.Equal(0.95, back.Personas[0].TopP);
        Assert.Equal(40, back.Personas[0].TopK);
    }

    [Fact]
    public void Absent_top_p_top_k_stay_null_and_are_not_written()
    {
        var config = ConfigCodec.Parse(Json);
        Assert.Null(config.PersonaTopP);
        Assert.Null(config.PersonaTopK);
        Assert.Null(config.Personas[0].TopP);
        var s = ConfigCodec.Serialize(config);
        Assert.DoesNotContain("topP", s);
        Assert.DoesNotContain("topK", s);
    }

    // A persona entry with NO 'temperature' key at all - the shape that silently decoded to
    // default(double) = 0, greedy, before #127.
    private const string NoTemperatureJson = """
        {
          "providers": [ { "name": "openrouter", "baseUrl": "https://x", "apiKeyEnv": "OPENROUTER_API_KEY" } ],
          "personas": [
            { "id": "bug-hunter", "name": "The Bug Hunter", "lens": "bugs", "tier": "diff",
              "model": { "provider": "openrouter", "modelId": "deepseek/deepseek-chat" },
              "systemPrompt": "find bugs" }
          ],
          "repos": [ { "name": "api", "path": "." } ],
          "assignments": [ { "personaId": "bug-hunter", "repoName": "api" } ]
        }
        """;

    [Fact]
    public void A_persona_omitting_temperature_decodes_to_absent_not_to_greedy_zero()
    {
        // The defect in one assertion. Reflection-based deserialization into a non-nullable double
        // turned "the operator did not say" into "the operator said 0" - the value ReviewBudget
        // documents as the cause of the 65k-148k-token reasoning runaway.
        var persona = Assert.Single(ConfigCodec.Parse(NoTemperatureJson).Personas);

        Assert.Null(persona.Temperature);
        Assert.Equal(PanelFence.DefaultTemperature, persona.SamplingTemperature());
        Assert.NotEqual(0.0, persona.SamplingTemperature());
    }

    [Fact]
    public void An_omitted_temperature_stays_omitted_on_write_and_survives_a_round_trip()
    {
        // Absence has to survive serialize->parse too, or `peanut-gallery init`-style rewrites would
        // materialise today's default into every config and freeze it there.
        var serialized = ConfigCodec.Serialize(ConfigCodec.Parse(NoTemperatureJson));

        // Case-INSENSITIVE, and checked for the resolution's own name too: the camelCase policy would
        // have written a computed `samplingTemperature` key that a lowercase "temperature" check sails
        // straight past, re-materialising the resolved default into every file a user hand-edits.
        Assert.DoesNotContain("temperature", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Assert.Single(ConfigCodec.Parse(serialized).Personas).Temperature);
    }

    [Fact]
    public void The_resolved_temperature_is_never_written_to_a_library_persona_file_either()
    {
        // PersonaLibraryStore.Save serializes a bare Persona (not a whole config) to <id>.json, so the
        // persona path needs its own guard: it is a second write path onto disk, and the file it writes
        // is one a user edits by hand.
        var persona = new Persona(
            "architect", "The Architect", "architecture", ReviewTier.Diff,
            new ModelRef("openrouter", "anthropic/claude-opus-4.1"), null, "review it");

        var serialized = ConfigCodec.Serialize(persona);

        Assert.DoesNotContain("temperature", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Null(ConfigCodec.Parse<Persona>(serialized)!.Temperature);
        Assert.Equal(PanelFence.DefaultTemperature, ConfigCodec.Parse<Persona>(serialized)!.SamplingTemperature());
    }

    [Fact]
    public void An_explicit_zero_temperature_survives_the_round_trip_as_zero()
    {
        // Not a validation bug: an operator who writes 0 gets 0. The fix must distinguish the two
        // cases, not blanket-raise them. (The shared Json fixture above is exactly this shape - a
        // config predating this change, with the key present, loading unchanged.)
        var persona = Assert.Single(ConfigCodec.Parse(Json).Personas);
        Assert.Equal(0.0, persona.Temperature);
        Assert.Equal(0.0, persona.SamplingTemperature());

        var back = Assert.Single(ConfigCodec.Parse(ConfigCodec.Serialize(ConfigCodec.Parse(Json))).Personas);
        Assert.Equal(0.0, back.Temperature);
        Assert.Equal(0.0, back.SamplingTemperature());
        Assert.Contains("\"temperature\": 0", ConfigCodec.Serialize(ConfigCodec.Parse(Json)));
    }

    [Theory]
    [InlineData(1.4)]
    [InlineData(0.0)]
    public void A_config_predating_this_change_loads_with_its_temperature_intact(double authored)
    {
        // Every committed peanut.json in the wild writes the key. Making it nullable must not
        // change what any of them decode to.
        var json = Json.Replace(
            "\"temperature\": 0.0",
            "\"temperature\": " + authored.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(authored, Assert.Single(ConfigCodec.Parse(json).Personas).SamplingTemperature());
    }

    [Fact]
    public void Both_codecs_agree_on_what_an_absent_temperature_means()
    {
        // THE regression test. #127 was not a bad default, it was two decode paths each choosing
        // their own: PanelCodec said "the recommended default", ConfigCodec's non-nullable double
        // said 0. Asserting them against each other - rather than each against a constant - is what
        // keeps a third codec from inventing a third answer.
        var fromConfig = Assert.Single(ConfigCodec.Parse(NoTemperatureJson).Personas);

        var blob = """{"mode":"auto","sha":"s","personas":[{"id":"bug-hunter","name":"The Bug Hunter","lens":"bugs","tier":"diff","provider":"openrouter","model":"deepseek/deepseek-chat","prompt":"find bugs"}]}""";
        var body = "<!-- pg-panel:1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(blob)) + " -->";
        var fromPanel = Assert.Single(PanelCodec.Extract(body)!.Personas);

        Assert.Equal(fromConfig.Temperature, fromPanel.Temperature);
        Assert.Equal(fromConfig.SamplingTemperature(), fromPanel.SamplingTemperature());
        Assert.NotEqual(0.0, fromConfig.SamplingTemperature());
    }

    [Fact]
    public void Default_panel_assigns_its_personas_to_the_named_repo()
    {
        var config = DefaultPanel.For("my-repo");
        Assert.All(config.Assignments, a => Assert.Equal("my-repo", a.RepoName));
        Assert.Equal("my-repo", Assert.Single(config.Repos).Name);
        Assert.NotEmpty(config.Personas);
    }

    [Fact]
    public void An_empty_object_decodes_to_a_config_with_four_empty_collections()
    {
        // #194: System.Text.Json passes null to the constructor for every omitted key. Nothing
        // downstream may ever see that null.
        var config = ConfigCodec.Parse("{}");

        Assert.Empty(config.Providers);
        Assert.Empty(config.Personas);
        Assert.Empty(config.Repos);
        Assert.Empty(config.Assignments);
    }

    [Fact]
    public void The_minimal_auto_mode_config_validates_instead_of_crashing()
    {
        // The natural shape under panel: auto - there are no personas to declare, so the user
        // does not declare any. This threw an unhandled NullReferenceException out of `validate`.
        var config = ConfigCodec.Parse("""
            {
              "panel": "auto",
              "orchestrator": { "provider": "openrouter", "modelId": "openai/gpt-5.6-luna" },
              "personaModel": { "provider": "openrouter", "modelId": "openai/gpt-5.6-luna" },
              "providers": [ { "name": "openrouter", "baseUrl": "https://x", "apiKeyEnv": "OPENROUTER_API_KEY" } ]
            }
            """);

        Assert.Empty(config.Personas);
        Assert.Empty(config.Assignments);
        Assert.Null(Persona.UnsetTemperatureNotice(config.Personas));
        Assert.Empty(ConfigValidation.Validate(config));
    }

    [Fact]
    public void A_partial_skip_block_decodes_with_empty_label_and_marker_lists()
    {
        var config = ConfigCodec.Parse("""{ "skip": { "drafts": true } }""");

        Assert.NotNull(config.Skip);
        Assert.Empty(config.Skip!.Labels);
        Assert.Empty(config.Skip.Markers);
        Assert.True(config.Skip.Drafts);
    }

    [Fact]
    public void A_partial_filter_block_decodes_with_an_empty_glob_list()
    {
        var config = ConfigCodec.Parse("""{ "filter": { "maxBytes": 200000 } }""");

        Assert.NotNull(config.Filter);
        Assert.Empty(config.Filter!.IgnoreGlobs);
        Assert.Equal(200_000, config.Filter.ByteBudget());
    }

    [Fact]
    public void A_filter_block_that_omits_maxBytes_decodes_to_the_default_budget()
    {
        var config = ConfigCodec.Parse("""{ "filter": { "ignoreGlobs": ["*.log"] } }""");

        Assert.NotNull(config.Filter);
        Assert.Null(config.Filter!.MaxBytes);
        Assert.Equal(DiffFilterPolicy.DefaultMaxBytes, config.Filter.ByteBudget());
    }
}
