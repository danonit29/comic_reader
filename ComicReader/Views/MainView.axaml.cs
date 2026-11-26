using System;
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
    /// Cross-platform primary view hosting the library workspace, folders view,
    /// reader overlay, and notifications.
    /// </summary>
    public partial class MainView : UserControl
    {
        private readonly MainViewModel? _viewModel;
        private readonly ISettingsService? _settingsService;
        private bool _isInitialized;

        public static readonly StyledProperty<bool> IsCompactLayoutProperty =
            AvaloniaProperty.Register<MainView, bool>(nameof(IsCompactLayout), false);

        public bool IsCompactLayout
        {
            get => GetValue(IsCompactLayoutProperty);
            set => SetValue(IsCompactLayoutProperty, value);
        }

        public static readonly StyledProperty<Thickness> SafeAreaPaddingProperty =
            AvaloniaProperty.Register<MainView, Thickness>(nameof(SafeAreaPadding), default);

        public Thickness SafeAreaPadding
        {
            get => GetValue(SafeAreaPaddingProperty);
            set => SetValue(SafeAreaPaddingProperty, value);
        }

        /// <summary>
        /// Strongly-typed ViewModel accessor for CompiledBindings inside DataTemplates and ContextMenus.
        /// </summary>
        public MainViewModel ViewModel => _viewModel ?? (DataContext as MainViewModel)!;

        public MainView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
        }

        public MainView(MainViewModel viewModel, ISettingsService settingsService)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _settingsService = settingsService;
            DataContext = _viewModel;

            Loaded += OnLoaded;
            SizeChanged += OnSizeChanged;
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            bool compact = e.NewSize.Width > 0 && e.NewSize.Width < 760;
            if (IsCompactLayout != compact)
            {
                IsCompactLayout = compact;
            }
        }

        private async void OnLoaded(object? sender, RoutedEventArgs e)
        {
            if (TopLevel.GetTopLevel(this)?.InsetsManager is { } insets)
            {
                SafeAreaPadding = insets.SafeAreaPadding;
                insets.SafeAreaChanged += (_, args) => SafeAreaPadding = args.SafeAreaPadding;
            }

            if (_isInitialized || ViewModel is null) return;
            _isInitialized = true;
            await ViewModel.InitializeAsync();
        }

        private void OnComicCardTapped(object? sender, TappedEventArgs e)
        {
            if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            {
                return;
            }

            if (sender is StyledElement { DataContext: ComicBook comic } && ViewModel is not null)
            {
                ViewModel.OpenComicCommand.Execute(comic);
            }
        }

        private void OnFolderCardTapped(object? sender, TappedEventArgs e)
        {
            if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            {
                return;
            }

            if (sender is StyledElement { DataContext: ComicFolder folder } && ViewModel is not null)
            {
                ViewModel.FolderVm.OpenFolderCommand.Execute(folder);
            }
        }
    }
}
