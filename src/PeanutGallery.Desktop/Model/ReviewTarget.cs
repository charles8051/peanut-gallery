namespace PeanutGallery.Desktop.Model;

/// <summary>A concrete PR to review — carries its own repo context so the handler never has to
/// re-derive which repo a card belongs to.</summary>
public sealed record ReviewTarget(string Owner, string Repo, int Number, string Title);
