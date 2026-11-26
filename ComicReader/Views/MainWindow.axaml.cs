using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ComicReader.Models;
using ComicReader.Services.Interfaces;
using ComicReader.ViewModels;

namespace ComicReader.Views
{
    /// <summary>
    /// Desktop window host providing custom title bar chrome, window size persistence,
    /// and hosting the cross-platform MainView workspace.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel? _viewModel;
        private readonly ISettingsService? _settingsService;
        private AppSettings _appSettings = new();

        /// <summary>
        /// Strongly-typed ViewModel accessor.
        /// </summary>
        public MainViewModel ViewModel => _viewModel ?? (DataContext as MainViewModel)!;

        public MainWindow()
        {
            InitializeComponent();
        }

        public MainWindow(MainViewModel viewModel, ISettingsService settingsService)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _settingsService = settingsService;
            DataContext = _viewModel;

            _viewModel.RequestRenamePrompt = ShowRenameDialogAsync;
            Opened += OnOpened;
            Closing += OnClosing;
        }

        private async void OnOpened(object? sender, EventArgs e)
        {
            if (_settingsService is null || _viewModel is null) return;

            _appSettings = await _settingsService.LoadAsync();
            if (_appSettings.WindowWidth >= MinWidth) Width = _appSettings.WindowWidth;
            if (_appSettings.WindowHeight >= MinHeight) Height = _appSettings.WindowHeight;
            WindowState = _appSettings.WindowState;

            await _viewModel.InitializeAsync();
        }

        private async void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            if (_settingsService is null) return;

            _appSettings.WindowState = WindowState;
            if (WindowState == WindowState.Normal)
            {
                _appSettings.WindowWidth = Bounds.Width > 0 ? Bounds.Width : Width;
                _appSettings.WindowHeight = Bounds.Height > 0 ? Bounds.Height : Height;
            }

            await _settingsService.SaveAsync(_appSettings);
        }

        private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            {
                return;
            }

            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }

        private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            {
                return;
            }

            ToggleMaximize();
        }

        private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnMaximizeRestoreClick(object? sender, RoutedEventArgs e) => ToggleMaximize();

        private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

        private void ToggleMaximize()
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private async Task<string?> ShowRenameDialogAsync(string currentName)
        {
            var dialog = new RenameDialog(currentName);
            return await dialog.ShowDialog<string?>(this);
        }
    }
}
