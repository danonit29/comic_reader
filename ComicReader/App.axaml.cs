using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ComicReader.Services;
using ComicReader.Services.Interfaces;
using ComicReader.Services.Native;
using ComicReader.ViewModels;
using ComicReader.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ComicReader
{
    /// <summary>
    /// Application entry point configuring dependency injection, cross-platform
    /// lifetime routing (Desktop Window vs Mobile SingleView), and theme initialization.
    /// </summary>
    public partial class App : Application
    {
        private IServiceProvider? _serviceProvider;

        /// <summary>
        /// Optional platform-provided native PDF renderer (Windows.Data.Pdf on Windows, PdfRenderer on Android).
        /// </summary>
        public static Func<INativePdfRenderer?>? NativePdfRendererFactory { get; set; }

        /// <summary>
        /// True while the comic reader overlay is open.
        /// </summary>
        public static bool IsReaderOpen { get; private set; }

        /// <summary>
        /// Raised when the reader overlay opens or closes (used on Android to toggle KeepScreenOn).
        /// </summary>
        public static event Action<bool>? ReaderOpenStateChanged;

        /// <summary>
        /// Notifies platform listeners whether the reader view is currently open.
        /// </summary>
        public static void NotifyReaderOpenState(bool isOpen)
        {
            IsReaderOpen = isOpen;
            ReaderOpenStateChanged?.Invoke(isOpen);
        }

        /// <summary>
        /// Raised from Android native DispatchTouchEvent when a 2-finger pinch starts (midX_dp, midY_dp).
        /// </summary>
        public static event Action<double, double>? AndroidPinchStarted;

        /// <summary>
        /// Raised from Android native DispatchTouchEvent as 2 fingers pinch/pan (spanRatio, midX_dp, midY_dp).
        /// </summary>
        public static event Action<double, double, double>? AndroidPinchUpdated;

        /// <summary>
        /// Raised from Android native DispatchTouchEvent when the 2-finger pinch ends.
        /// </summary>
        public static event Action? AndroidPinchEnded;

        public static void NotifyAndroidPinchStarted(double midXDip, double midYDip)
            => AndroidPinchStarted?.Invoke(midXDip, midYDip);

        public static void NotifyAndroidPinchUpdated(double spanRatio, double midXDip, double midYDip)
            => AndroidPinchUpdated?.Invoke(spanRatio, midXDip, midYDip);

        public static void NotifyAndroidPinchEnded()
            => AndroidPinchEnded?.Invoke();

        /// <summary>
        /// Latest Android system bar insets (status bar, cutout, navigation bar) in DIPs.
        /// </summary>
        public static Thickness AndroidSafeAreaPadding { get; private set; }

        /// <summary>
        /// Raised when Android native system bar insets change.
        /// </summary>
        public static event Action<Thickness>? AndroidSafeAreaChanged;

        /// <summary>
        /// Updates cached Android system bar insets and notifies views on the UI thread.
        /// </summary>
        public static void NotifyAndroidSafeAreaChanged(Thickness padding)
        {
            if (AndroidSafeAreaPadding == padding) return;
            AndroidSafeAreaPadding = padding;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AndroidSafeAreaChanged?.Invoke(padding));
        }

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            }
            else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            {
                singleView.MainView = _serviceProvider.GetRequiredService<MainView>();
            }

            base.OnFrameworkInitializationCompleted();
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IComicEngine, RustComicEngine>();
            services.AddSingleton(_ => new PdfService(NativePdfRendererFactory?.Invoke()));
            services.AddSingleton<ZipService>();
            services.AddSingleton<FolderImageReaderService>();
            services.AddSingleton<IComicReaderFactory, ComicReaderFactory>();
            services.AddSingleton<IFileService>(sp => new FileService(() =>
            {
                if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow is { } win)
                {
                    return TopLevel.GetTopLevel(win);
                }
                if (Current?.ApplicationLifetime is ISingleViewApplicationLifetime singleView && singleView.MainView is { } view)
                {
                    return TopLevel.GetTopLevel(view);
                }
                return null;
            }));
            services.AddSingleton<IStorageService, StorageService>();
            services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<IBookmarkService, BookmarkService>();

            services.AddSingleton<FolderViewModel>();
            services.AddSingleton<MainViewModel>();
            services.AddTransient<ReaderViewModel>();

            services.AddSingleton<MainWindow>();
            services.AddSingleton<MainView>();
        }
    }
}
