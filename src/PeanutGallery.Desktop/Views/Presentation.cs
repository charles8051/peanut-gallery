using System;
using System.Globalization;

namespace PeanutGallery.Desktop.Views;

// View-layer presentation: turn the snapshot's *semantic* values (instants, persona ids)
// into human-facing strings and colours. Kept pure and Avalonia-free so they stay
// unit-testable; the view (Shell) calls them, and the pure fold (SnapshotBuilder) does not.

/// <summary>A persona rendered for display: prettified name + accent colour.</summary>
public sealed record PersonaChip(string Name, string AccentHex);

public static class RelativeTime
{
    public static string Format(DateTimeOffset when, DateTimeOffset now)
    {
        var d = now - when;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 2) return "yesterday";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        return $"{(int)(d.TotalDays / 7)}w ago";
    }
}

public static class PersonaStyle
{
    // Deterministic accent so a persona keeps its colour across renders (no config yet).
    private static readonly string[] Accents =
        ["#8f83d8", "#d97e5c", "#e0a83c", "#5b9bd8", "#57b98a", "#c26fb0"];

    public static PersonaChip Chip(string personaId) => new(DisplayName(personaId), Accent(personaId));

    public static string DisplayName(string id)
    {
        var words = id.Replace('-', ' ').Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = char.ToUpper(words[i][0], CultureInfo.InvariantCulture) + words[i][1..];
        }

        var name = string.Join(' ', words);
        return name.StartsWith("The ", StringComparison.Ordinal) ? name : "The " + name;
    }

    public static string Accent(string id)
    {
        var h = 0;
        foreach (var ch in id) h = unchecked(h * 31 + ch);
        return Accents[(h & int.MaxValue) % Accents.Length];
    }
}
