using System;
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
    /// Cross-platform primary view hosting the library workspace, folders view,
    /// reader overlay, and notifications.
    /// </summary>
    public partial class MainView : UserControl
    {
        private readonly MainViewModel? _viewModel;
        private readonly ISettingsService? _settingsService;
        private bool _isInitialized;
        private TopLevel? _hookedTopLevel;
        private TaskCompletionSource<string?>? _renamePromptTcs;

        /// <summary>
        /// True when running on Android so touch-friendly card action buttons stay visible without mouse hover.
        /// </summary>
        public bool IsAndroidPlatform { get; } = OperatingSystem.IsAndroid();

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
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is not null && !ReferenceEquals(_hookedTopLevel, topLevel))
            {
                if (_hookedTopLevel is not null)
                {
                    _hookedTopLevel.BackRequested -= OnTopLevelBackRequested;
                }
                _hookedTopLevel = topLevel;
                _hookedTopLevel.BackRequested += OnTopLevelBackRequested;
            }

            if (IsAndroidPlatform)
            {
                SafeAreaPadding = App.AndroidSafeAreaPadding;
                App.AndroidSafeAreaChanged -= OnAndroidSafeAreaChanged;
                App.AndroidSafeAreaChanged += OnAndroidSafeAreaChanged;
            }
            else if (topLevel?.InsetsManager is { } insets)
            {
                SafeAreaPadding = insets.SafeAreaPadding;
                insets.SafeAreaChanged += (_, args) => SafeAreaPadding = args.SafeAreaPadding;
            }

            if (ViewModel is not null)
            {
                ViewModel.RequestRenamePrompt ??= ShowInlineRenameDialogAsync;
            }

            if (_isInitialized || ViewModel is null) return;
            _isInitialized = true;
            await ViewModel.InitializeAsync();
        }

        private void OnAndroidSafeAreaChanged(Thickness padding)
        {
            SafeAreaPadding = padding;
        }

        private Task<string?> ShowInlineRenameDialogAsync(string currentName)
        {
            _renamePromptTcs?.TrySetResult(null);
            _renamePromptTcs = new TaskCompletionSource<string?>();

            RenameInputTextBox.Text = currentName;
            RenameModalOverlay.IsVisible = true;
            RenameInputTextBox.Focus();
            RenameInputTextBox.SelectAll();

            return _renamePromptTcs.Task;
        }

        private void ResolveInlineRename(string? result)
        {
            RenameModalOverlay.IsVisible = false;
            TaskCompletionSource<string?>? tcs = _renamePromptTcs;
            _renamePromptTcs = null;
            tcs?.TrySetResult(result);
        }

        private void OnRenameCancelClick(object? sender, RoutedEventArgs e)
            => ResolveInlineRename(null);

        private void OnRenameConfirmClick(object? sender, RoutedEventArgs e)
            => ResolveInlineRename(RenameInputTextBox.Text);

        private void OnRenameInputKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ResolveInlineRename(RenameInputTextBox.Text);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ResolveInlineRename(null);
                e.Handled = true;
            }
        }

        private void OnTopLevelBackRequested(object? sender, RoutedEventArgs e)
        {
            if (RenameModalOverlay.IsVisible)
            {
                ResolveInlineRename(null);
                e.Handled = true;
                return;
            }

            if (ViewModel is null) return;

            if (ViewModel.IsThumbnailPickerOpen)
            {
                ViewModel.CloseThumbnailPickerCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (ViewModel.IsReaderOpen && ViewModel.ActiveReader is { } reader)
            {
                if (reader.IsBookmarkPanelOpen)
                {
                    reader.IsBookmarkPanelOpen = false;
                }
                else
                {
                    reader.CloseReaderCommand.Execute(null);
                }
                e.Handled = true;
                return;
            }

            if (ViewModel.IsFoldersSection && ViewModel.FolderVm.IsInsideFolder)
            {
                ViewModel.FolderVm.BackToFoldersCommand.Execute(null);
                e.Handled = true;
            }
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
