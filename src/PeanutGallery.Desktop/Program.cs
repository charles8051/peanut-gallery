using System;
using Avalonia;

namespace PeanutGallery.Desktop;

internal static class Program
{
    // Avalonia entry point. Kept minimal and reflection-free so the shell stays
    // Native-AOT-publishable (docs/adr/0001).
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
