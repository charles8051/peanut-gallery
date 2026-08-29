using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PeanutGallery.Desktop.Services;

/// <summary>
/// The desktop's GitHub credentials — a token and the API base — discovered from the
/// environment. No secret is ever stored in the app's own state. The set of tracked repos is
/// no longer discovered here; it lives in the persisted <see cref="Model.DesktopState"/> and is
/// only *seeded* from env/desktop.json on first run via <see cref="DiscoverSeedRepos"/>.
/// </summary>
public sealed record DesktopConfig(string? Token, string ApiBaseUrl)
{
    public bool HasToken => Token is not null;

    public static DesktopConfig Discover()
    {
        var token = Env("GITHUB_TOKEN") ?? Env("GITHUB_PAT");
        var apiBase = Env("GITHUB_API_URL") ?? "https://api.github.com";
        return new DesktopConfig(token, apiBase);
    }

    // First-run seed: PG_DESKTOP_REPOS (comma-separated) wins, else desktop.json "repos", else none.
    public static IReadOnlyList<string> DiscoverSeedRepos()
    {
        var fromEnv = Env("PG_DESKTOP_REPOS");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Split(fromEnv);
        }

        var path = ConfigFilePath();
        if (path is not null && File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("repos", out var repos) && repos.ValueKind == JsonValueKind.Array)
                {
                    return repos.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString() ?? string.Empty)
                        .Where(s => s.Contains('/'))
                        .ToList();
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // Malformed / unreadable config falls through to "no repos" (sample data).
            }
        }

        return Array.Empty<string>();
    }

    /// <summary>%APPDATA%\peanut-gallery\desktop.json (Windows) or ~/.config/peanut-gallery/desktop.json.</summary>
    public static string? ConfigFilePath()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(dir))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return null;
            return Path.Combine(home, ".config", "peanut-gallery", "desktop.json");
        }

        return Path.Combine(dir, "peanut-gallery", "desktop.json");
    }

    private static IReadOnlyList<string> Split(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Contains('/'))
            .ToList();

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
}
