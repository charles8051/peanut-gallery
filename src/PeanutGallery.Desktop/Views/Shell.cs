using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using PeanutGallery.Desktop.Model;

namespace PeanutGallery.Desktop.Views;

/// <summary>The interactions the shell surfaces back to the app; all optional (null = inert).</summary>
internal sealed record ShellCallbacks(
    Action<ReviewTarget>? OnReview = null,
    Action<string>? OnSelectRepo = null,
    Action<string>? OnAddRepo = null,
    Action<string>? OnRemoveRepo = null,
    Action<string, bool>? OnToggleAutoReview = null,
    Action? OnOpenPersonas = null);

// Pure-ish builder: a WorkspaceSnapshot in, a control tree out. No IO, no mutable state.
internal static class Shell
{
    public static Control Build(WorkspaceSnapshot s, DateTimeOffset now, ShellCallbacks? cb = null)
    {
        cb ??= new ShellCallbacks();
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("288,*") };
        var left = RepoListPane(s, cb);
        var right = RepoDetailPane(s.Selected, now, cb);
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        root.Children.Add(left);
        root.Children.Add(right);
        return root;
    }

    // ---- left: repo list ------------------------------------------------------------

    private static Control RepoListPane(WorkspaceSnapshot s, ShellCallbacks cb)
    {
        var dock = new DockPanel { LastChildFill = true };

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 9,
            Margin = new Thickness(15, 15, 15, 12),
            Children =
            {
                new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(7), Background = Palette.Accent },
                Tb("Peanut Gallery", 14.5, Palette.Text, FontWeight.Medium),
            },
        };
        DockPanel.SetDock(brand, Dock.Top);

        var nav = new StackPanel
        {
            Margin = new Thickness(8), Spacing = 2,
            Children = { NavItem("Personas", cb.OnOpenPersonas), NavItem("Providers"), NavItem("Settings") },
        };
        nav.Children.Insert(0, new Border { Height = 1, Background = Palette.Border, Margin = new Thickness(0, 4, 0, 6) });
        DockPanel.SetDock(nav, Dock.Bottom);

        var list = new StackPanel { Margin = new Thickness(8, 4, 8, 8), Spacing = 1 };
        list.Children.Add(SectionLabel("Repositories"));
        foreach (var r in s.Repos) list.Children.Add(RepoRowView(r, cb));
        if (cb.OnAddRepo is not null) list.Children.Add(AddRepoRow(cb.OnAddRepo));

        dock.Children.Add(brand);
        dock.Children.Add(nav);
        dock.Children.Add(new ScrollViewer { Content = list });

        return new Border { Background = Palette.Surface, BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 1, 0), Child = dock };
    }

    private static Control RepoRowView(RepoRow r, ShellCallbacks cb)
    {
        var slug = RepoSlug.Of(r.Owner, r.Name);
        var text = new StackPanel { Spacing = 2 };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        nameRow.Children.Add(Tb(r.Name, 13, r.Selected ? Palette.Text : Palette.Text2, r.Selected ? FontWeight.Medium : FontWeight.Normal));
        if (r.AutoReview) nameRow.Children.Add(Dot(Palette.Green, 6)); // subscribed to auto-review
        text.Children.Add(nameRow);
        var sub = (r.AutoReview ? "auto · " : "") + $"{r.Subscribed} reviewed · {r.OpenPrs} open";
        text.Children.Add(Tb(sub, 11, Palette.Text3));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);
        if (cb.OnRemoveRepo is not null)
        {
            var remove = new Button
            {
                Content = "✕", Background = Brushes.Transparent, Foreground = Palette.Text3,
                BorderThickness = new Thickness(0), Padding = new Thickness(6, 2), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            remove.Click += (_, e) => { cb.OnRemoveRepo(slug); e.Handled = true; };
            Grid.SetColumn(remove, 1);
            grid.Children.Add(remove);
        }

        var row = new Border
        {
            Background = r.Selected ? Palette.Surface2 : Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 8),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = grid,
        };
        if (cb.OnSelectRepo is not null)
        {
            row.PointerPressed += (_, _) => cb.OnSelectRepo(slug);
        }

        return row;
    }

    private static Control AddRepoRow(Action<string> onAdd)
    {
        var input = new TextBox
        {
            Watermark = "owner/repo", FontSize = 12.5, Height = 30,
            Background = Palette.Surface2, Foreground = Palette.Text, BorderBrush = Palette.Border,
        };
        void Add()
        {
            var slug = input.Text?.Trim();
            if (!string.IsNullOrEmpty(slug)) { onAdd(slug); input.Text = string.Empty; }
        }
        input.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Add(); e.Handled = true; } };

        var add = MiniButton("Add");
        add.Click += (_, _) => Add();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(1, 6, 1, 0) };
        Grid.SetColumn(input, 0);
        input.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(add, 1);
        grid.Children.Add(input);
        grid.Children.Add(add);
        return grid;
    }

    private static Control NavItem(string label, Action? onClick = null)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8),
            Child = Tb(label, 13.5, onClick is null ? Palette.Text3 : Palette.Text2),
        };
        if (onClick is not null)
        {
            border.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            border.PointerPressed += (_, _) => onClick();
        }

        return border;
    }

    // ---- right: selected repo -------------------------------------------------------

    private static Control RepoDetailPane(RepoDetail d, DateTimeOffset now, ShellCallbacks cb)
    {
        var onReview = cb.OnReview;
        var dock = new DockPanel { LastChildFill = true };

        var lastReviewed = d.LastReviewed is { } lr ? RelativeTime.Format(lr, now) : "never";
        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var titleStack = new StackPanel
        {
            Children =
            {
                Tb(d.Name, 19, Palette.Text, FontWeight.Medium),
                Tb($"{d.OpenPrs} open pull requests · last reviewed {lastReviewed}", 12.5, Palette.Text3),
            },
        };
        // Map a card to a repo-anchored ReviewTarget here, where the repo context lives.
        Action<PullRequestCard>? review = onReview is null
            ? null
            : card => onReview(new ReviewTarget(d.Owner, d.Name, card.Number, card.Title));

        var reviewBtn = PrimaryButton("Review a PR");
        reviewBtn.VerticalAlignment = VerticalAlignment.Center;
        // The header button reviews the most recent open PR; per-row buttons target a specific PR.
        var topPr = d.Prs.Count > 0 ? d.Prs[0] : null;
        reviewBtn.IsEnabled = review is not null && topPr is not null;
        if (topPr is not null) reviewBtn.Click += (_, _) => review?.Invoke(topPr);
        Grid.SetColumn(titleStack, 0);
        Grid.SetColumn(reviewBtn, 1);
        titleRow.Children.Add(titleStack);
        titleRow.Children.Add(reviewBtn);

        var header = new Border
        {
            BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(26, 18, 26, 16),
            Child = new StackPanel { Children = { Tb(d.Owner, 12.5, Palette.Text3), titleRow } },
        };
        DockPanel.SetDock(header, Dock.Top);

        var body = new StackPanel { Margin = new Thickness(26, 18, 26, 40), Spacing = 0, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(SubscriptionCard(d, cb, d.AutoReview));
        body.Children.Add(new TextBlock { Text = "OPEN PULL REQUESTS", FontSize = 11, Foreground = Palette.Text3, Margin = new Thickness(2, 22, 0, 10) });
        foreach (var pr in d.Prs) body.Children.Add(PrRowView(pr, now, review));

        dock.Children.Add(header);
        dock.Children.Add(new ScrollViewer { Content = body });
        return new Border { Background = Palette.Bg, Child = dock };
    }

    private static Control SubscriptionCard(RepoDetail d, ShellCallbacks cb, bool autoReview)
    {
        var slug = RepoSlug.Of(d.Owner, d.Name);

        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var id in d.SubscribedPersonaIds) chips.Children.Add(PersonaChipView(PersonaStyle.Chip(id)));
        if (d.SubscribedPersonaIds.Count == 0)
        {
            chips.Children.Add(Tb("No persona has reviewed a PR here yet.", 12, Palette.Text3));
        }

        // Auto-review toggle: subscribe this repo so new/changed PRs are reviewed while the app is
        // open, using the repo's committed panel (the same panel CI uses).
        var toggleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4, 0, 10) };
        var label = new StackPanel
        {
            Children =
            {
                Tb("Auto-review new PRs while this app is open", 13, Palette.Text2),
                Tb("Uses this repo's committed panel; runs on this machine, skips PRs already reviewed.", 12, Palette.Text3),
            },
        };
        var toggle = new ToggleSwitch { IsChecked = autoReview, VerticalAlignment = VerticalAlignment.Center, IsEnabled = cb.OnToggleAutoReview is not null };
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (toggle.IsChecked is { } on) cb.OnToggleAutoReview?.Invoke(slug, on);
        };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(toggle, 1);
        toggleRow.Children.Add(label);
        toggleRow.Children.Add(toggle);

        var inner = new StackPanel
        {
            Children =
            {
                toggleRow,
                new Border { Height = 1, Background = Palette.Border, Margin = new Thickness(0, 0, 0, 10) },
                Tb("Reviewed here by", 12.5, Palette.Text3),
                new Border { Height = 6 },
                chips,
            },
        };

        return new Border
        {
            Background = Palette.Surface, BorderBrush = autoReview ? Palette.Green : Palette.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14),
            Child = inner,
        };
    }

    private static Control PersonaChipView(PersonaChip p) => new Border
    {
        Background = Palette.Surface2, BorderBrush = Palette.Border, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 5), Margin = new Thickness(0, 0, 8, 8),
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7,
            Children = { Dot(Palette.Hex(p.AccentHex), 7), Tb(p.Name, 12.5, Palette.Text) },
        },
    };

    private static Control PrRowView(PullRequestCard pr, DateTimeOffset now, Action<PullRequestCard>? onReview)
    {
        var titleStack = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        Tb($"#{pr.Number}", 13, Palette.Text3),
                        new TextBlock { Text = pr.Title, FontSize = 13.5, Foreground = Palette.Text, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
                Tb($"{pr.Author} · {pr.Branch} · updated {RelativeTime.Format(pr.Updated, now)}", 12, Palette.Text3),
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var status = StatusView(pr);
        status.Margin = new Thickness(14, 0);
        var action = MiniButton(pr.State == ReviewState.NotReviewed ? "Review now" : "Re-review");
        action.IsEnabled = onReview is not null;
        action.Click += (_, _) => onReview?.Invoke(pr);
        Grid.SetColumn(titleStack, 0);
        Grid.SetColumn(status, 1);
        Grid.SetColumn(action, 2);
        grid.Children.Add(titleStack);
        grid.Children.Add(status);
        grid.Children.Add(action);

        return new Border
        {
            BorderBrush = Palette.Border, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(6, 13),
            Child = grid,
        };
    }

    private static StackPanel StatusView(PullRequestCard pr)
    {
        var (dot, text) = pr.State switch
        {
            ReviewState.Findings => (Palette.Amber, $"{pr.High + pr.Minor} findings"),
            ReviewState.Reviewing => (Palette.Blue, "Reviewing…"),
            ReviewState.Clean => (Palette.Green, "Reviewed · no findings"),
            _ => (Palette.Text3, "Not reviewed"),
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(Dot(dot, 7));
        sp.Children.Add(Tb(text, 12.5, Palette.Text2));
        if (pr.State == ReviewState.Findings)
        {
            if (pr.High > 0) sp.Children.Add(SevBadge(Palette.Red, pr.High));
            if (pr.Minor > 0) sp.Children.Add(SevBadge(Palette.Amber, pr.Minor));
        }
        return sp;
    }

    private static Control SevBadge(IBrush color, int n) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
        Children = { Dot(color, 6), Tb(n.ToString(), 12, Palette.Text2) },
    };

    // ---- primitives -----------------------------------------------------------------

    private static TextBlock Tb(string text, double size, IBrush fg, FontWeight weight = FontWeight.Normal) =>
        new() { Text = text, FontSize = size, Foreground = fg, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center };

    private static Control SectionLabel(string text) =>
        new TextBlock { Text = text.ToUpperInvariant(), FontSize = 11, Foreground = Palette.Text3, Margin = new Thickness(8, 6, 0, 4) };

    private static Ellipse Dot(IBrush fill, double d) =>
        new() { Width = d, Height = d, Fill = fill, VerticalAlignment = VerticalAlignment.Center };

    internal static Button PrimaryButton(string label) => new()
    {
        Content = label, Background = Palette.Accent, Foreground = Palette.AccentInk,
        BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(13, 8), FontWeight = FontWeight.Medium,
    };

    internal static Button MiniButton(string label) => new()
    {
        Content = label, Background = Brushes.Transparent, Foreground = Palette.Text2,
        BorderBrush = Palette.BorderStrong, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(12, 5), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
    };
}
