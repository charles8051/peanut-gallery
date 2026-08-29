using System.Collections.Generic;

namespace PeanutGallery.Core;

/// <summary>Where a persona comes from — its precedence layer.</summary>
public enum PersonaScope
{
    /// <summary>Ships with the tool; read-only.</summary>
    BuiltIn,

    /// <summary>The user's personal library on disk; usable by app/one-shot reviews.</summary>
    Library,

    /// <summary>Committed in a repo; the only scope CI can use.</summary>
    Repo,
}

/// <summary>A persona tagged with the scope it was resolved from.</summary>
public sealed record ScopedPersona(Persona Persona, PersonaScope Scope);

/// <summary>
/// Pure resolution of the three persona scopes into one effective, de-duplicated set.
/// Precedence is Repo &gt; Library &gt; Built-in: a persona id defined in a higher scope
/// overrides the same id in a lower one (so a repo can shadow a library or built-in persona,
/// and a library persona can shadow a built-in). No IO — a shell reads the files; this only
/// folds the already-loaded lists (ADR-0001 / persona-management ADR Decision 1).
/// </summary>
public static class PersonaResolution
{
    public static IReadOnlyList<ScopedPersona> Resolve(
        IReadOnlyList<Persona> builtIn, IReadOnlyList<Persona> library, IReadOnlyList<Persona> repo)
    {
        // Winner per id, applied low precedence first so higher scopes overwrite.
        var winner = new Dictionary<string, ScopedPersona>();
        foreach (var p in builtIn) winner[p.Id] = new ScopedPersona(p, PersonaScope.BuiltIn);
        foreach (var p in library) winner[p.Id] = new ScopedPersona(p, PersonaScope.Library);
        foreach (var p in repo) winner[p.Id] = new ScopedPersona(p, PersonaScope.Repo);

        // Stable, readable order: by scope (built-in, library, repo) then id.
        var result = new List<ScopedPersona>(winner.Values);
        result.Sort((a, b) =>
        {
            var byScope = a.Scope.CompareTo(b.Scope);
            return byScope != 0 ? byScope : string.CompareOrdinal(a.Persona.Id, b.Persona.Id);
        });
        return result;
    }
}
