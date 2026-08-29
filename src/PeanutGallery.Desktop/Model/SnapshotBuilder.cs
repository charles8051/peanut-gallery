using System;
using System.Collections.Generic;
using System.Linq;
using PeanutGallery.Core;

namespace PeanutGallery.Desktop.Model;

// Raw, IO-shaped inputs the GitHub shell fetches. The builder below is the pure fold
// that turns them into the immutable WorkspaceSnapshot the view renders.
public sealed record PrRaw(
    int Number, string Title, string Author, string Branch,
    DateTimeOffset Updated, string HeadSha, bool IsDraft);

public sealed record PrInput(PrRaw Pr, IReadOnlyList<ExistingComment> Comments);

public sealed record RepoInput(string Owner, string Name, IReadOnlyList<PrInput> Prs);

/// <summary>
/// Pure core: raw GitHub data in, an immutable WorkspaceSnapshot out. Same inputs always
/// yield the same snapshot — no IO, no ambient clock. Produces *semantic* values only
/// (instants, persona ids, counts); formatting them for display (relative time, persona
/// name/colour) is the view's job. Review status is derived from each persona's living
/// comment via the core's own <see cref="CommentSync.PersonaIdOf"/> + <see cref="SessionCodec.Extract"/>.
/// </summary>
public static class SnapshotBuilder
{
    public static WorkspaceSnapshot Build(
        IReadOnlyList<RepoInput> repos, string? selectedSlug, IReadOnlyList<string>? autoReviewSlugs = null)
    {
        if (repos.Count == 0)
        {
            return new WorkspaceSnapshot(Array.Empty<RepoRow>(), EmptyDetail);
        }

        var autoReview = new HashSet<string>(autoReviewSlugs ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        var selectedIdx = 0;
        for (var i = 0; i < repos.Count; i++)
        {
            if (Slug(repos[i]) == selectedSlug) { selectedIdx = i; break; }
        }

        var rows = new List<RepoRow>(repos.Count);
        for (var i = 0; i < repos.Count; i++)
        {
            var r = repos[i];
            rows.Add(new RepoRow(
                r.Owner, r.Name,
                Subscribed: PersonaIds(r).Count,
                OpenPrs: r.Prs.Count,
                CiEnabled: false,
                Selected: i == selectedIdx,
                AutoReview: autoReview.Contains(Slug(r))));
        }

        return new WorkspaceSnapshot(rows, DetailOf(repos[selectedIdx], autoReview.Contains(Slug(repos[selectedIdx]))));
    }

    private static RepoDetail DetailOf(RepoInput r, bool autoReview)
    {
        var cards = r.Prs
            .Select(CardOf)
            .OrderByDescending(c => c.Number)
            .ToList();

        // No per-review timestamp in the session blob; approximate "last reviewed" by the
        // most recently updated PR that carries any persona review. Null = never reviewed.
        DateTimeOffset? lastReviewed = r.Prs
            .Where(p => Sessions(p.Comments).Count > 0)
            .Select(p => (DateTimeOffset?)p.Pr.Updated)
            .DefaultIfEmpty(null)
            .Max();

        return new RepoDetail(
            r.Owner, r.Name, r.Prs.Count, lastReviewed, "This app", PersonaIds(r), cards, autoReview);

        PullRequestCard CardOf(PrInput p)
        {
            var (state, high, minor) = StatusOf(Sessions(p.Comments));
            return new PullRequestCard(
                p.Pr.Number, p.Pr.Title, p.Pr.Author, p.Pr.Branch, p.Pr.Updated, state, high, minor);
        }
    }

    // ---- status derivation (pure, over the persona living comments) -----------------

    private sealed record PersonaSession(string PersonaId, ReviewSession Session);

    private static IReadOnlyList<PersonaSession> Sessions(IReadOnlyList<ExistingComment> comments)
    {
        var list = new List<PersonaSession>();
        foreach (var c in comments)
        {
            // A marker plus a base64 blob is something any commenter can write, and this drives the
            // status the operator reads a PR's health off. Believe only authors who speak for the repo.
            if (!CommentTrust.CarriesState(c)) continue;
            var id = CommentSync.PersonaIdOf(c.Body);
            if (id is null) continue;
            var session = SessionCodec.Extract(c.Body);
            if (session is not null) list.Add(new PersonaSession(id, session));
        }

        return list;
    }

    private static (ReviewState State, int High, int Minor) StatusOf(IReadOnlyList<PersonaSession> sessions)
    {
        if (sessions.Count == 0) return (ReviewState.NotReviewed, 0, 0);

        var high = 0;
        var minor = 0;
        foreach (var s in sessions)
        {
            foreach (var f in s.Session.OpenFindings)
            {
                if (f.Severity >= Severity.Major) high++;
                else minor++;
            }
        }

        return high + minor > 0
            ? (ReviewState.Findings, high, minor)
            : (ReviewState.Clean, 0, 0);
    }

    private static IReadOnlyList<string> PersonaIds(RepoInput r) =>
        r.Prs
            .SelectMany(p => Sessions(p.Comments))
            .Select(s => s.PersonaId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

    private static string Slug(RepoInput r) => RepoSlug.Of(r.Owner, r.Name);

    private static readonly RepoDetail EmptyDetail =
        new("", "No repositories", 0, null, "This app",
            Array.Empty<string>(), Array.Empty<PullRequestCard>());
}
