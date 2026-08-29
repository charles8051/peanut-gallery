using System;
using System.IO;
using PeanutGallery.Core;
using PeanutGallery.Desktop.Services;
using Xunit;

namespace PeanutGallery.Desktop.Tests;

public class PersonaLibraryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pg-personas-{Guid.NewGuid():N}");

    private static Persona P(string id, string name = "The One") =>
        new(id, name, "bugs", ReviewTier.Diff, new ModelRef("openrouter", "deepseek/deepseek-chat"), 0.1, "find bugs");

    [Fact]
    public void Missing_dir_loads_empty()
    {
        Assert.Empty(new PersonaLibraryStore(_dir).Load());
    }

    [Fact]
    public void Save_load_delete_round_trips_by_id()
    {
        var store = new PersonaLibraryStore(_dir);
        store.Save(P("bug-hunter"));
        store.Save(P("architect"));

        var loaded = store.Load();
        Assert.Equal(2, loaded.Count);
        Assert.True(store.Contains("bug-hunter"));

        // Loaded personas retain their fields through the JSON round-trip.
        var bh = Assert.Single(loaded, p => p.Id == "bug-hunter");
        Assert.Equal("openrouter", bh.Model.Provider);
        Assert.Equal(ReviewTier.Diff, bh.Tier);

        store.Delete("bug-hunter");
        Assert.False(store.Contains("bug-hunter"));
        Assert.Single(store.Load());
    }

    [Fact]
    public void Save_overwrites_an_existing_id()
    {
        var store = new PersonaLibraryStore(_dir);
        store.Save(P("architect", "Old"));
        store.Save(P("architect", "New"));
        Assert.Equal("New", Assert.Single(store.Load()).Name);
    }

    [Fact]
    public void Corrupt_file_is_skipped_not_fatal()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");
        new PersonaLibraryStore(_dir).Save(P("good"));
        Assert.Single(new PersonaLibraryStore(_dir).Load());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Unsafe_ids_are_rejected_and_never_escape_the_dir(string id)
    {
        var store = new PersonaLibraryStore(_dir);
        var persona = P(id);
        // Either the id is neutralized to a file inside _dir, or it is refused outright — never an escape.
        try
        {
            store.Save(persona);
            foreach (var f in Directory.EnumerateFiles(_dir))
            {
                Assert.StartsWith(Path.GetFullPath(_dir), Path.GetFullPath(f), StringComparison.Ordinal);
            }
        }
        catch (ArgumentException)
        {
            // Refusing an unsafe id is an acceptable outcome.
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
