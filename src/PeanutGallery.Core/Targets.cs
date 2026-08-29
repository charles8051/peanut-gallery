namespace PeanutGallery.Core;

/// <summary>
/// A repository a review can run against. <see cref="Path"/> is whatever the
/// shell needs to locate the checkout (a local path for the desktop GUI, a
/// clone URL for the headless server). The core never touches it.
/// </summary>
public sealed record RepoTarget(string Name, string Path);

/// <summary>
/// A persona assigned to a repo - the pure-value form of "drag a persona card
/// onto a repo" in the desktop GUI, or a row in the server's management page.
/// The set of assignments is the only thing that decides who reviews what.
/// </summary>
public sealed record Assignment(string PersonaId, string RepoName);
