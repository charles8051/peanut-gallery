using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PeanutGallery.Core;
using PeanutGallery.Engine;

namespace PeanutGallery.Desktop.Services;

/// <summary>
/// The user's personal persona library on disk: one JSON file per persona under
/// %APPDATA%\peanut-gallery\personas\ (or ~/.config/... elsewhere), named &lt;id&gt;.json.
/// One-file-per-persona keeps add/remove/import atomic and diff-friendly. Shell-layer file IO
/// around the persona value + the shared <see cref="ConfigCodec"/> shape; degrades gracefully
/// (a bad file is skipped, not fatal).
/// </summary>
public sealed class PersonaLibraryStore
{
    private readonly string _dir;

    public PersonaLibraryStore(string? dir = null) => _dir = dir ?? DefaultDir();

    /// <summary>All valid personas in the library, sorted by id. Missing dir → empty.</summary>
    public IReadOnlyList<Persona> Load()
    {
        if (!Directory.Exists(_dir))
        {
            return Array.Empty<Persona>();
        }

        var personas = new List<Persona>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var persona = ConfigCodec.Parse<Persona>(File.ReadAllText(file));
                if (persona is not null && !string.IsNullOrEmpty(persona.Id))
                {
                    personas.Add(persona);
                }
            }
            catch (Exception e) when (e is IOException or ConfigFormatException or UnauthorizedAccessException)
            {
                // Skip an unreadable/corrupt persona file rather than failing the whole library.
            }
        }

        personas.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return personas;
    }

    /// <summary>Write a persona to &lt;id&gt;.json (atomic replace). Overwrites an existing id.</summary>
    public void Save(Persona persona)
    {
        Directory.CreateDirectory(_dir);
        var path = PathFor(persona.Id);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ConfigCodec.Serialize(persona) + "\n");
        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(string id)
    {
        var path = PathFor(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public bool Contains(string id) => File.Exists(PathFor(id));

    // Map an id to its file, refusing anything that would escape the library directory.
    private string PathFor(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("persona id is empty", nameof(id));
        }

        var full = Path.GetFullPath(Path.Combine(_dir, SafeFileName(id) + ".json"));
        var root = Path.GetFullPath(_dir);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.Ordinal))
        {
            throw new ArgumentException($"unsafe persona id '{id}'", nameof(id));
        }

        return full;
    }

    // Neutralize path separators / invalid filename chars so an id can't reach outside the dir.
    private static string SafeFileName(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            id = id.Replace(c, '_');
        }

        return id.Replace('/', '_').Replace('\\', '_');
    }

    private static string DefaultDir()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            return Path.Combine(appData, "peanut-gallery", "personas");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "peanut-gallery", "personas");
    }
}
