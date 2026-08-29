using System;
using System.Collections.Generic;

namespace PeanutGallery.Desktop.Model;

// Immutable, semantic view values: instants and ids, not formatted strings or colours.
// The pure SnapshotBuilder produces these; the view (Shell) formats them for display via
// the RelativeTime / PersonaStyle helpers (docs/feature-specs/desktop-gui).
public enum ReviewState { NotReviewed, Reviewing, Clean, Findings }

public sealed record PullRequestCard(
    int Number, string Title, string Author, string Branch, DateTimeOffset Updated,
    ReviewState State, int High, int Minor);

public sealed record RepoRow(
    string Owner, string Name, int Subscribed, int OpenPrs, bool CiEnabled, bool Selected, bool AutoReview = false);

public sealed record RepoDetail(
    string Owner, string Name, int OpenPrs, DateTimeOffset? LastReviewed, string Executor,
    IReadOnlyList<string> SubscribedPersonaIds, IReadOnlyList<PullRequestCard> Prs, bool AutoReview = false);

public sealed record WorkspaceSnapshot(IReadOnlyList<RepoRow> Repos, RepoDetail Selected);

// Sample used before any GitHub/engine wiring, and as the fallback when the app is
// unconfigured. Parameterised by `now` so its relative timestamps stay fresh.
public static class SampleData
{
    public static WorkspaceSnapshot Snapshot(DateTimeOffset now) => new(
        new RepoRow[]
        {
            new("acme", "payments-api", 2, 6, CiEnabled: false, Selected: true),
            new("acme", "web-app", 1, 3, CiEnabled: false, Selected: false),
            new("acme", "ledger-core", 2, 2, CiEnabled: true, Selected: false),
            new("acme", "notify-worker", 1, 4, CiEnabled: true, Selected: false),
            new("acme", "edge-gateway", 0, 0, CiEnabled: false, Selected: false),
        },
        new RepoDetail("acme", "payments-api", 6, now.AddHours(-2), "This app",
            new[] { "the-architect", "the-bug-hunter" },
            new PullRequestCard[]
            {
                new(218, "Add idempotency keys to the charge endpoint", "dvora", "feature/idempotency", now.AddHours(-2), ReviewState.Findings, 1, 2),
                new(217, "Retry backoff: switch to full jitter", "sam", "fix/backoff", now.AddHours(-5), ReviewState.Reviewing, 0, 0),
                new(216, "Extract the webhook signer into its own module", "lena", "refactor/webhook-signer", now.AddDays(-1), ReviewState.Clean, 0, 0),
                new(214, "Bump payments SDK to 4.2", "dependabot", "deps/sdk-4.2", now.AddDays(-2), ReviewState.NotReviewed, 0, 0),
                new(211, "Docs: payment state machine diagram", "sam", "docs/state-machine", now.AddDays(-3), ReviewState.Clean, 0, 0),
            }));
}
