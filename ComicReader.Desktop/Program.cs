using System;
using Avalonia;
using ComicReader.Desktop.Services;

namespace ComicReader;

/// <summary>
/// Application entry point configuring the cross-platform Avalonia host and Skia renderer.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.NativePdfRendererFactory = static () => new WindowsNativePdfRenderer();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
