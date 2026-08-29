using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using PeanutGallery.Core;

namespace PeanutGallery.Engine;

/// <summary>Raised when config JSON can't be parsed into a <see cref="PeanutConfig"/>.</summary>
public sealed class ConfigFormatException(string message) : Exception(message);

/// <summary>
/// Shared config (de)serialization. The core defines <see cref="PeanutConfig"/> as plain data
/// with no serialization concern; this shell-layer codec owns the JSON shape so every shell —
/// the CLI (from a file) and the desktop GUI (from bytes fetched over the GitHub API) — reads
/// the identical format. Reflection-based System.Text.Json today; a source-gen context is the
/// AOT swap later (the core stays untouched).
/// </summary>
public static class ConfigCodec
{
    // Private so the shared, mutable options object can't be reconfigured by a caller.
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static PeanutConfig Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PeanutConfig>(json, Options) ?? PeanutConfig.Empty;
        }
        catch (JsonException e)
        {
            throw new ConfigFormatException(e.Message);
        }
    }

    /// <summary>Deserialize any value from JSON using the shared config options (camelCase + enum names).</summary>
    public static T? Parse<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException e)
        {
            throw new ConfigFormatException(e.Message);
        }
    }

    /// <summary>Pretty-print a config (or any value) as JSON using the shared config options.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
