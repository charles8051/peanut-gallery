using Avalonia.Media;

namespace PeanutGallery.Desktop;

// Design tokens from docs/feature-specs/desktop-gui/mockups (dark, developer-tool).
internal static class Palette
{
    public static IBrush Hex(string hex) => new SolidColorBrush(Color.Parse(hex));

    public static readonly IBrush Bg = Hex("#0f0f11");
    public static readonly IBrush Surface = Hex("#17171a");
    public static readonly IBrush Surface2 = Hex("#1e1e22");
    public static readonly IBrush Raised = Hex("#242429");
    public static readonly IBrush Border = Hex("#2a2a30");
    public static readonly IBrush BorderStrong = Hex("#3a3a42");
    public static readonly IBrush Text = Hex("#e8e8ec");
    public static readonly IBrush Text2 = Hex("#a2a2ab");
    public static readonly IBrush Text3 = Hex("#6c6c76");
    public static readonly IBrush Accent = Hex("#e0a24a");
    public static readonly IBrush AccentInk = Hex("#3a2a10");
    public static readonly IBrush Green = Hex("#57b98a");
    public static readonly IBrush Amber = Hex("#e0a83c");
    public static readonly IBrush Blue = Hex("#5b9bd8");
    public static readonly IBrush Red = Hex("#e26a63");
}
