namespace PeanutGallery.Desktop.Model;

/// <summary>The "owner/repo" slug format, in one place, so shells don't each re-parse or re-build it.</summary>
public static class RepoSlug
{
    /// <summary>Join an owner and repo into the canonical "owner/repo" slug.</summary>
    public static string Of(string owner, string name) => $"{owner}/{name}";

    /// <summary>Split "owner/repo" into its parts, or null unless it is exactly one non-empty owner and repo.</summary>
    public static (string Owner, string Name)? Split(string? slug)
    {
        if (string.IsNullOrEmpty(slug))
        {
            return null;
        }

        var i = slug.IndexOf('/');
        if (i <= 0 || i >= slug.Length - 1 || slug.IndexOf('/', i + 1) >= 0)
        {
            return null;
        }

        return (slug[..i], slug[(i + 1)..]);
    }
}
