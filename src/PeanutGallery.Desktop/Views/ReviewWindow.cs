using System;
using System.Threading;
using System.Threading.Tasks;
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
/// One-shot review dialog: runs the shared ReviewRunner in-process against a PR, shows each
/// persona's findings, and posts them to the PR only when the user clicks Post (preview → post).
/// </summary>
public sealed class ReviewWindow : Window
{
    private readonly GitHubClient _gh;
    private readonly string _owner;
    private readonly string _repo;
    private readonly int _prNumber;
    private readonly Func<PeanutConfig, IReviewer> _reviewerFactory;

    private readonly StackPanel _log = new() { Spacing = 2 };
    private readonly StackPanel _body;
    private readonly Button _postButton;
    private readonly Button _closeButton;
    private readonly TextBlock _status;
    private ReviewPreview? _preview;

    /// <summary>True once the review was posted to the PR — the caller refreshes on close.</summary>
    public bool Posted { get; private set; }

    public ReviewWindow(
        string token, string apiBaseUrl, string owner, string repo, int prNumber, string prTitle,
        Func<PeanutConfig, IReviewer> reviewerFactory)
    {
        _gh = new GitHubClient(token, apiBaseUrl);
        _owner = owner;
        _repo = repo;
        _prNumber = prNumber;
        _reviewerFactory = reviewerFactory;

        Title = $"Review {owner}/{repo} #{prNumber}";
        Width = 720;
        Height = 640;
        Background = Palette.Bg;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _status = new TextBlock { Text = "Reviewing…", FontSize = 12.5, Foreground = Palette.Text3 };
        _postButton = Shell.PrimaryButton("Post to PR");
        _postButton.IsEnabled = false;
        _postButton.Click += async (_, _) => await OnPostAsync();
        _closeButton = Shell.MiniButton("Close");
        _closeButton.Click += (_, _) => Close();
        var close = _closeButton;

        var header = new Border
        {
            BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(20, 16),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = $"#{prNumber} · {prTitle}", FontSize = 15, Foreground = Palette.Text, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"{owner}/{repo}", FontSize = 12, Foreground = Palette.Text3 },
                },
            },
        };
        DockPanel.SetDock(header, Dock.Top);

        var footer = new Border
        {
            BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 12),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Children = { _status },
            },
        };
        Grid.SetColumn(_status, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        var btns = (Grid)footer.Child!;
        Grid.SetColumn(close, 1);
        close.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(_postButton, 2);
        btns.Children.Add(close);
        btns.Children.Add(_postButton);
        DockPanel.SetDock(footer, Dock.Bottom);

        _body = new StackPanel { Margin = new Thickness(20, 14), Spacing = 12 };
        _body.Children.Add(new Border
        {
            Background = Palette.Surface, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12), Child = _log,
        });

        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(new ScrollViewer { Content = _body });
        Content = dock;

        Opened += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        void Log(string m) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            _log.Children.Add(new TextBlock { Text = m, FontSize = 11.5, Foreground = Palette.Text3, FontFamily = new FontFamily("Consolas,monospace") }));

        try
        {
            _preview = await ReviewOrchestrator.PreviewAsync(_gh, _owner, _repo, _prNumber, _reviewerFactory, Log);
            RenderResults(_preview);
        }
        catch (Exception e)
        {
            _status.Text = "Review failed.";
            _status.Foreground = Palette.Red;
            _body.Children.Add(Card(Palette.Red, "Review failed", e.Message));
        }
    }

    private void RenderResults(ReviewPreview preview)
    {
        _body.Children.Clear();

        if (preview.UsedDefaultPanel)
        {
            _body.Children.Add(Note("This repo has no committed config — reviewed with the default panel."));
        }

        var totalFindings = 0;
        foreach (var p in preview.Result.Personas)
        {
            _body.Children.Add(PersonaCard(p, out var count));
            totalFindings += count;
        }

        if (preview.Result.Personas.Count == 0)
        {
            _body.Children.Add(Note("No personas are assigned to this repo in its config."));
            _status.Text = "Nothing to post.";
            return;
        }

        _postButton.IsEnabled = preview.Result.RenderedBodies.Count > 0;
        _status.Text = $"{preview.Result.Personas.Count} persona(s), {totalFindings} finding(s). Review before posting.";
        _status.Foreground = Palette.Text2;
    }

    private Control PersonaCard(PersonaResult p, out int findingCount)
    {
        findingCount = 0;
        var inner = new StackPanel { Spacing = 6 };
        var (dot, label) = p.Outcome switch
        {
            PersonaOutcome.Failed => (Palette.Red, "could not run"),
            PersonaOutcome.Unchanged => (Palette.Text3, "unchanged"),
            _ => (Palette.Green, "reviewed"),
        };
        inner.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                new Ellipse { Width = 8, Height = 8, Fill = dot, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = p.PersonaName, FontSize = 13.5, Foreground = Palette.Text, FontWeight = FontWeight.Medium },
                new TextBlock { Text = label, FontSize = 11.5, Foreground = Palette.Text3, VerticalAlignment = VerticalAlignment.Center },
            },
        });

        var session = p.Body is null ? null : SessionCodec.Extract(p.Body);
        if (session is not null && session.OpenFindings.Count > 0)
        {
            findingCount = session.OpenFindings.Count;
            foreach (var f in session.OpenFindings)
            {
                inner.Children.Add(FindingRow(f));
            }
        }
        else if (p.Outcome == PersonaOutcome.Reviewed)
        {
            inner.Children.Add(new TextBlock { Text = "No findings.", FontSize = 12, Foreground = Palette.Text3, Margin = new Thickness(16, 0, 0, 0) });
        }
        else if (p.Outcome == PersonaOutcome.Failed)
        {
            inner.Children.Add(new TextBlock { Text = "The model call failed; nothing will be posted for this persona until it succeeds.", FontSize = 12, Foreground = Palette.Text3, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 0, 0) });
        }

        return new Border
        {
            Background = Palette.Surface, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12), Child = inner,
        };
    }

    private static Control FindingRow(Finding f)
    {
        var color = f.Severity switch
        {
            Severity.Critical or Severity.Major => Palette.Red,
            Severity.Minor => Palette.Amber,
            _ => Palette.Text3,
        };
        var where = f.Line > 0 ? $"{f.File}:{f.Line}" : f.File;
        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                new Ellipse { Width = 7, Height = 7, Fill = color, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 0) },
                new TextBlock { Text = f.Severity.ToString().ToLowerInvariant(), FontSize = 11, Foreground = color, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = where, FontSize = 11.5, Foreground = Palette.Text3, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas,monospace") },
            },
        };
        return new StackPanel
        {
            Margin = new Thickness(16, 4, 0, 0), Spacing = 2,
            Children =
            {
                head,
                new TextBlock { Text = f.Title, FontSize = 12.5, Foreground = Palette.Text, TextWrapping = TextWrapping.Wrap },
            },
        };
    }

    private async Task OnPostAsync()
    {
        if (_preview is null) return;
        _postButton.IsEnabled = false;
        _closeButton.IsEnabled = false; // block the close-during-post race so the caller's refresh isn't missed
        _status.Text = "Posting…";
        _status.Foreground = Palette.Text2;
        try
        {
            var (created, updated) = await ReviewOrchestrator.PostAsync(_gh, _owner, _repo, _prNumber, _preview);
            Posted = true;
            _status.Text = $"Posted: {created} new, {updated} updated.";
            _status.Foreground = Palette.Green;
        }
        catch (Exception e)
        {
            _postButton.IsEnabled = true;
            _status.Text = "Post failed: " + e.Message;
            _status.Foreground = Palette.Red;
        }
        finally
        {
            _closeButton.IsEnabled = true;
        }
    }

    private static Control Note(string text) => new Border
    {
        Background = Palette.Surface2, BorderBrush = Palette.BorderStrong, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8),
        Child = new TextBlock { Text = text, FontSize = 12, Foreground = Palette.Text2, TextWrapping = TextWrapping.Wrap },
    };

    private static Control Card(IBrush accent, string title, string detail) => new Border
    {
        Background = Palette.Surface, BorderBrush = accent, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12),
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = title, FontSize = 13.5, Foreground = Palette.Text, FontWeight = FontWeight.Medium },
                new TextBlock { Text = detail, FontSize = 12, Foreground = Palette.Text3, TextWrapping = TextWrapping.Wrap },
            },
        },
    };

    protected override void OnClosed(EventArgs e)
    {
        _gh.Dispose();
        base.OnClosed(e);
    }
}
