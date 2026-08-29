using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PeanutGallery.Desktop.Model;

namespace PeanutGallery.Desktop.Services;

/// <summary>
/// Loads and saves <see cref="DesktopState"/> to %APPDATA%\peanut-gallery\desktop-state.json
/// (or ~/.config/... elsewhere). Shell-layer file IO around the pure state value; degrades to
/// <see cref="DesktopState.Empty"/> on a missing/corrupt file rather than throwing.
/// </summary>
public sealed class DesktopStateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public DesktopStateStore(string? path = null) => _path = path ?? DefaultPath();

    public bool Exists => File.Exists(_path);

    public DesktopState Load()
    {
        if (!File.Exists(_path))
        {
            return DesktopState.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path));
            var root = doc.RootElement;
            var selected = root.TryGetProperty("selected", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
            return new DesktopState(StringArray(root, "repos"), selected, StringArray(root, "autoReview"));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return DesktopState.Empty;
        }
    }

    public void Save(DesktopState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(
                new { repos = state.Repos, selected = state.Selected, autoReview = state.AutoReview }, Options);
            // Write to a temp file then replace, so a crash/concurrent read never sees a half-written file.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json + "\n");
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Persistence is best-effort; a failed save must not crash the app.
        }
    }

    private static IReadOnlyList<string> StringArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!).Where(s => s.Contains('/')).ToList()
            : new List<string>();

    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            return Path.Combine(appData, "peanut-gallery", "desktop-state.json");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "peanut-gallery", "desktop-state.json");
    }
}
