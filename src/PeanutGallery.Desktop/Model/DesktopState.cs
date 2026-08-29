using System;
using System.Collections.Generic;
using System.Linq;

namespace PeanutGallery.Desktop.Model;

/// <summary>
/// The desktop's own persisted state: which repos to track, which one is selected, and which
/// are subscribed to auto-review (their new/changed PRs get reviewed while the app is open).
/// Pure value with total transforms — the store owns the file IO. Never holds secrets; the
/// token still comes from the environment.
/// </summary>
public sealed record DesktopState(
    IReadOnlyList<string> Repos, string? Selected, IReadOnlyList<string> AutoReview)
{
    public static DesktopState Empty { get; } = new(Array.Empty<string>(), null, Array.Empty<string>());

    /// <summary>Add a normalized "owner/repo" slug (dedup, case-insensitive); no-op if invalid.</summary>
    public DesktopState AddRepo(string slug)
    {
        var normalized = Normalize(slug);
        if (normalized is null || Repos.Any(r => r.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return this;
        }

        var repos = Repos.Append(normalized).ToList();
        // First repo added becomes the selection if nothing is selected yet.
        return this with { Repos = repos, Selected = Selected ?? normalized };
    }

    public DesktopState RemoveRepo(string slug)
    {
        var repos = Repos.Where(r => !r.Equals(slug, StringComparison.OrdinalIgnoreCase)).ToList();
        if (repos.Count == Repos.Count)
        {
            return this;
        }

        // Keep the selection if it still exists; otherwise fall back to the first (or none).
        var stillSelected = Selected is not null
            && repos.Any(r => r.Equals(Selected, StringComparison.OrdinalIgnoreCase));
        var selected = stillSelected ? Selected : repos.FirstOrDefault();
        // A removed repo can no longer be subscribed.
        var autoReview = AutoReview.Where(a => !a.Equals(slug, StringComparison.OrdinalIgnoreCase)).ToList();
        return this with { Repos = repos, Selected = selected, AutoReview = autoReview };
    }

    // Select by the *stored* casing so Selected always matches an entry in Repos exactly.
    public DesktopState Select(string slug)
    {
        var match = Repos.FirstOrDefault(r => r.Equals(slug, StringComparison.OrdinalIgnoreCase));
        return match is null ? this : this with { Selected = match };
    }

    public bool IsAutoReview(string slug) => AutoReview.Any(a => a.Equals(slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>Turn auto-review on/off for a tracked repo (stored by canonical casing); no-op otherwise.</summary>
    public DesktopState SetAutoReview(string slug, bool on)
    {
        var match = Repos.FirstOrDefault(r => r.Equals(slug, StringComparison.OrdinalIgnoreCase));
        if (match is null || IsAutoReview(match) == on)
        {
            return this;
        }

        var autoReview = on
            ? AutoReview.Append(match).ToList()
            : AutoReview.Where(a => !a.Equals(match, StringComparison.OrdinalIgnoreCase)).ToList();
        return this with { AutoReview = autoReview };
    }

    /// <summary>Seed tracked repos on first run (no persisted state yet) from env/desktop.json discovery.</summary>
    public static DesktopState Seed(IReadOnlyList<string> repos)
    {
        var normalized = repos.Select(Normalize).Where(s => s is not null).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new DesktopState(normalized, normalized.FirstOrDefault(), Array.Empty<string>());
    }

    private static string? Normalize(string slug)
    {
        var s = slug.Trim();
        var i = s.IndexOf('/');
        return i > 0 && i < s.Length - 1 && s.IndexOf('/', i + 1) < 0 ? s : null;
    }
}
