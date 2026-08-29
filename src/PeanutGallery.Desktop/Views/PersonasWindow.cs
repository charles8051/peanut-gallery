using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using PeanutGallery.Core;
using PeanutGallery.Desktop.Services;
using PeanutGallery.Engine;

namespace PeanutGallery.Desktop.Views;

/// <summary>
/// The persona library: built-in personas (read-only) and the user's on-disk library, grouped by
/// scope. You can copy a built-in persona into your library and delete a library persona. Editing,
/// import, and repo-scope personas land in a later slice.
/// </summary>
public sealed class PersonasWindow : Window
{
    private readonly PersonaLibraryStore _store;
    private readonly StackPanel _body = new() { Margin = new Thickness(24, 18, 24, 32), Spacing = 22, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };

    public PersonasWindow(PersonaLibraryStore? store = null)
    {
        _store = store ?? new PersonaLibraryStore();

        Title = "Personas";
        Width = 820;
        Height = 720;
        Background = Palette.Bg;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var header = new Border
        {
            BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(24, 18),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = "Personas", FontSize = 17, Foreground = Palette.Text, FontWeight = FontWeight.Medium },
                    new TextBlock { Text = "Built-in reviewers and your personal library.", FontSize = 12.5, Foreground = Palette.Text3 },
                },
            },
        };
        DockPanel.SetDock(header, Dock.Top);

        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(header);
        dock.Children.Add(new ScrollViewer { Content = _body });
        Content = dock;

        Render();
    }

    private void Render()
    {
        _body.Children.Clear();

        var library = _store.Load();
        var libraryIds = new HashSet<string>(library.Select(p => p.Id), System.StringComparer.Ordinal);

        _body.Children.Add(Section("Built-in", "Ship with the tool · read-only",
            BuiltInPersonas.All.Select(p => Card(p, PersonaScope.BuiltIn, libraryIds.Contains(p.Id)))));

        _body.Children.Add(Section("My library", $"On disk · usable by app and one-shot reviews · {library.Count} persona(s)",
            library.Count == 0
                ? new[] { EmptyNote("Copy a built-in persona here to start your library.") }
                : library.Select(p => Card(p, PersonaScope.Library, alreadyInLibrary: true))));

        _body.Children.Add(Section("This repo", "Committed in a repo · the only scope CI can use",
            new[] { EmptyNote("Repo-scoped personas (from a repo's committed config) and import arrive in a later slice.") }));
    }

    private Control Section(string title, string subtitle, IEnumerable<Control> cards)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, Foreground = Palette.Text3 });
        stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = Palette.Text3, Margin = new Thickness(0, -4, 0, 4) });
        foreach (var c in cards) stack.Children.Add(c);
        return stack;
    }

    private Control Card(Persona p, PersonaScope scope, bool alreadyInLibrary)
    {
        var badge = ScopeBadge(scope);
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = p.Name, FontSize = 14, Foreground = Palette.Text, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center },
                badge,
            },
        };
        if (scope == PersonaScope.Library && BuiltInPersonas.All.Any(b => b.Id == p.Id))
        {
            titleRow.Children.Add(new TextBlock { Text = "overrides built-in", FontSize = 11, Foreground = Palette.Text3, VerticalAlignment = VerticalAlignment.Center });
        }

        var meta = $"{p.Lens} · {p.Tier.ToString().ToLowerInvariant()} tier · {p.Model.Provider}:{p.Model.ModelId} · temp {p.SamplingTemperature():0.0#}";
        var left = new StackPanel
        {
            Spacing = 5,
            Children =
            {
                titleRow,
                new TextBlock { Text = meta, FontSize = 12, Foreground = Palette.Text3, FontFamily = new FontFamily("Consolas,monospace") },
                new TextBlock { Text = Truncate(p.SystemPrompt, 160), FontSize = 12.5, Foreground = Palette.Text2, TextWrapping = TextWrapping.Wrap },
            },
        };

        var action = ActionFor(p, scope, alreadyInLibrary);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);
        if (action is not null)
        {
            action.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(action, 1);
            grid.Children.Add(action);
        }

        return new Border
        {
            Background = Palette.Surface, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(15, 13), Child = grid,
        };
    }

    private Control? ActionFor(Persona p, PersonaScope scope, bool alreadyInLibrary)
    {
        if (scope == PersonaScope.BuiltIn)
        {
            var copy = Shell.MiniButton(alreadyInLibrary ? "In library" : "Copy to library");
            copy.IsEnabled = !alreadyInLibrary;
            copy.Click += (_, _) => { _store.Save(p); Render(); };
            return copy;
        }

        if (scope == PersonaScope.Library)
        {
            var delete = Shell.MiniButton("Delete");
            delete.Click += (_, _) => { _store.Delete(p.Id); Render(); };
            return delete;
        }

        return null;
    }

    private static Control ScopeBadge(PersonaScope scope)
    {
        var (text, color) = scope switch
        {
            PersonaScope.BuiltIn => ("built-in", Palette.Blue),
            PersonaScope.Library => ("library", Palette.Green),
            _ => ("repo", Palette.Accent),
        };
        return new Border
        {
            Background = Palette.Surface2, BorderBrush = color, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 10.5, Foreground = color },
        };
    }

    private static Control EmptyNote(string text) => new Border
    {
        Background = Palette.Surface, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(15, 13),
        Child = new TextBlock { Text = text, FontSize = 12.5, Foreground = Palette.Text3, TextWrapping = TextWrapping.Wrap },
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
