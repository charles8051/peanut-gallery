using PeanutGallery.Core;
using Xunit;

namespace PeanutGallery.Core.Tests;

public class PersonaResolutionTests
{
    private static Persona P(string id, string name = "n") =>
        new(id, name, "lens", ReviewTier.Diff, new ModelRef("openrouter", "m"), 0.0, "prompt");

    [Fact]
    public void Merges_all_three_scopes_with_no_collisions()
    {
        var r = PersonaResolution.Resolve([P("a")], [P("b")], [P("c")]);
        Assert.Equal(3, r.Count);
        Assert.Contains(r, s => s.Persona.Id == "a" && s.Scope == PersonaScope.BuiltIn);
        Assert.Contains(r, s => s.Persona.Id == "b" && s.Scope == PersonaScope.Library);
        Assert.Contains(r, s => s.Persona.Id == "c" && s.Scope == PersonaScope.Repo);
    }

    [Fact]
    public void Library_overrides_builtin_on_id_collision()
    {
        var r = PersonaResolution.Resolve([P("x", "built-in")], [P("x", "library")], []);
        var x = Assert.Single(r);
        Assert.Equal(PersonaScope.Library, x.Scope);
        Assert.Equal("library", x.Persona.Name);
    }

    [Fact]
    public void Repo_overrides_library_and_builtin()
    {
        var r = PersonaResolution.Resolve([P("x", "b")], [P("x", "l")], [P("x", "r")]);
        var x = Assert.Single(r);
        Assert.Equal(PersonaScope.Repo, x.Scope);
        Assert.Equal("r", x.Persona.Name);
    }

    [Fact]
    public void Result_is_ordered_by_scope_then_id()
    {
        var r = PersonaResolution.Resolve([P("z"), P("a")], [P("m")], [P("b")]);
        Assert.Collection(r,
            s => Assert.Equal(("a", PersonaScope.BuiltIn), (s.Persona.Id, s.Scope)),
            s => Assert.Equal(("z", PersonaScope.BuiltIn), (s.Persona.Id, s.Scope)),
            s => Assert.Equal(("m", PersonaScope.Library), (s.Persona.Id, s.Scope)),
            s => Assert.Equal(("b", PersonaScope.Repo), (s.Persona.Id, s.Scope)));
    }
}
