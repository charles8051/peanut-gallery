using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PeanutGallery.Desktop.Views;

// Full-window centered message for the load lifecycle (loading / no-config / error).
internal static class Placeholder
{
    public static Control Centered(string title, string detail)
    {
        var stack = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 460,
            Children =
            {
                new TextBlock
                {
                    Text = title, FontSize = 15, Foreground = Palette.Text,
                    FontWeight = FontWeight.Medium, HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock
                {
                    Text = detail, FontSize = 12.5, Foreground = Palette.Text3,
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                },
            },
        };

        return new Border { Background = Palette.Bg, Child = stack };
    }

    // Wraps a built shell with a thin banner strip along the top (e.g. sample-data or error).
    public static Control WithBanner(Control content, string message, IBrush accent)
    {
        var banner = new Border
        {
            Background = Palette.Surface2,
            BorderBrush = accent, BorderThickness = new Thickness(0, 0, 0, 2),
            Padding = new Thickness(16, 7),
            Child = new TextBlock { Text = message, FontSize = 12, Foreground = Palette.Text2 },
        };
        DockPanel.SetDock(banner, Dock.Top);

        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(banner);
        dock.Children.Add(content);
        return dock;
    }
}
