using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ComicReader.Services;
using ComicReader.Services.Interfaces;
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
            services.AddSingleton<PdfService>();
            services.AddSingleton<ZipService>();
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
