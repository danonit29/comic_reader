using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ComicReader.Helpers;
using ComicReader.Models;
using ComicReader.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ComicReader.ViewModels
{
    /// <summary>
    /// Main ViewModel coordinating the Library (recent/active comics), Folders, Search, and Reader state.
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        private readonly IFileService _fileService;
        private readonly IStorageService _storageService;
        private readonly IComicReaderFactory _readerFactory;
        private readonly IBookmarkService _bookmarkService;
        private LibraryStore _libraryStore = new();
        private CancellationTokenSource? _notificationCts;

        [ObservableProperty]
        private ObservableCollection<ComicBook> _filteredComics = new();

        [ObservableProperty]
        private string _searchQuery = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsFoldersSection))]
        [NotifyPropertyChangedFor(nameof(IsLibrarySection))]
        [NotifyPropertyChangedFor(nameof(IsLibraryNavActive))]
        [NotifyPropertyChangedFor(nameof(IsSearchNavActive))]
        [NotifyPropertyChangedFor(nameof(IsSettingsNavActive))]
        private string _activeSection = AppConstants.SectionLibrary;

        [ObservableProperty]
        private string _statusMessage = AppConstants.DefaultStatusReady;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsReaderOpen))]
        private ReaderViewModel? _activeReader;

        [ObservableProperty]
        private string _notificationMessage = string.Empty;

        [ObservableProperty]
        private bool _isNotificationVisible;

        [ObservableProperty]
        private bool _isNotificationError;

        /// <summary>
        /// Sub-ViewModel managing the Folders navigation section.
        /// </summary>
        public FolderViewModel FolderVm { get; }

        private Func<string, Task<string?>>? _requestRenamePrompt;

        /// <summary>
        /// Async callback provided by the View to prompt the user for a new Display Name.
        /// </summary>
        public Func<string, Task<string?>>? RequestRenamePrompt
        {
            get => _requestRenamePrompt;
            set
            {
                _requestRenamePrompt = value;
                FolderVm.RequestRenamePrompt = value;
            }
        }

        /// <summary>
        /// True when the Folders tab is active.
        /// </summary>
        public bool IsFoldersSection => ActiveSection == AppConstants.SectionFolders;

        /// <summary>
        /// True when the Library (or Search) tab is active.
        /// </summary>
        public bool IsLibrarySection => !IsFoldersSection;

        public bool IsLibraryNavActive => ActiveSection == AppConstants.SectionLibrary;

        public bool IsSearchNavActive => ActiveSection == AppConstants.SectionSearch;

        public bool IsSettingsNavActive => ActiveSection == AppConstants.SectionSettings;

        /// <summary>
        /// True when a comic is actively open in the integrated ReaderView overlay.
        /// </summary>
        public bool IsReaderOpen => ActiveReader is not null;

        /// <summary>
        /// Initializes a new instance of the <see cref="MainViewModel"/> class.
        /// </summary>
        public MainViewModel(
            IFileService fileService,
            IStorageService storageService,
            IComicReaderFactory readerFactory,
            IBookmarkService bookmarkService,
            FolderViewModel folderViewModel)
        {
            _fileService = fileService;
            _storageService = storageService;
            _readerFactory = readerFactory;
            _bookmarkService = bookmarkService;
            FolderVm = folderViewModel;
            FolderVm.StatusChanged += msg => Dispatcher.UIThread.Post(() => StatusMessage = msg);
            FolderVm.NotificationRequested += async (msg, isError) => await ShowNotificationAsync(msg, isError);
        }

        partial void OnSearchQueryChanged(string value) => ApplyFilter();

        partial void OnActiveSectionChanged(string value) => ApplyFilter();

        /// <summary>
        /// Loads persisted library data, reading progress, and folders on startup.
        /// </summary>
        [RelayCommand]
        public async Task InitializeAsync()
        {
            IsBusy = true;
            StatusMessage = "Loading library...";

            try
            {
                _libraryStore = await _storageService.LoadAsync();
                SyncProgressToComics();
                FolderVm.SyncFromStore(_libraryStore);
                ApplyFilter();
                StatusMessage = $"{FilteredComics.Count} recent comic(s)";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Imports selected standalone PDF, ZIP, or CBZ files into the library via StorageProvider.
        /// </summary>
        [RelayCommand]
        private async Task ImportComicAsync()
        {
            IReadOnlyList<string> files = await _fileService.PickComicFilesAsync();
            if (files.Count == 0) return;

            IsBusy = true;
            int importedCount = 0;

            try
            {
                foreach (string filePath in files)
                {
                    if (await TryImportSingleFileAsync(filePath))
                    {
                        importedCount++;
                    }
                }

                await _storageService.SaveAsync(_libraryStore);

                Dispatcher.UIThread.Post(() =>
                {
                    FolderVm.SyncFromStore(_libraryStore);
                    ApplyFilter();
                    StatusMessage = $"Imported {importedCount} comic(s)";
                });

                if (importedCount > 0)
                {
                    await ShowNotificationAsync("Đã import xong comic");
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Displays a non-blocking toast notification for 3 seconds.
        /// </summary>
        public async Task ShowNotificationAsync(string message, bool isError = false)
        {
            _notificationCts?.Cancel();
            _notificationCts = new CancellationTokenSource();
            CancellationToken token = _notificationCts.Token;

            Dispatcher.UIThread.Post(() =>
            {
                NotificationMessage = message;
                IsNotificationError = isError;
                IsNotificationVisible = true;
            });

            try
            {
                await Task.Delay(3000, token);
                Dispatcher.UIThread.Post(() => IsNotificationVisible = false);
            }
            catch (OperationCanceledException)
            {
                // Replaced by a newer notification.
            }
        }

        /// <summary>
        /// Switches the active sidebar section.
        /// </summary>
        [RelayCommand]
        private void Navigate(string section)
        {
            if (string.IsNullOrWhiteSpace(section)) return;

            ActiveSection = section;
            StatusMessage = $"Viewing {section}";
        }

        /// <summary>
        /// Allows the user to rename a comic's DisplayName in JSON without touching the physical file.
        /// </summary>
        [RelayCommand]
        private async Task RenameComicAsync(ComicBook? comic)
        {
            if (comic is null || RequestRenamePrompt is null) return;

            string? newName = await RequestRenamePrompt(comic.DisplayName);
            if (string.IsNullOrWhiteSpace(newName) || newName == comic.DisplayName) return;

            comic.DisplayName = newName.Trim();
            await _storageService.SaveAsync(_libraryStore);
            StatusMessage = $"Renamed to '{comic.DisplayName}'";
        }

        /// <summary>
        /// Allows the user to select a custom thumbnail image for a comic book.
        /// </summary>
        [RelayCommand]
        private async Task SelectThumbnailAsync(ComicBook? comic)
        {
            if (comic is null) return;

            string? imagePath = await _fileService.PickImageFileAsync();
            if (string.IsNullOrWhiteSpace(imagePath)) return;

            IsBusy = true;
            StatusMessage = $"Updating thumbnail for {comic.DisplayName}...";

            try
            {
                string cachedPath = await _fileService.SaveCustomThumbnailAsync(comic.Id, imagePath);
                comic.CoverImagePath = cachedPath;
                await _storageService.SaveAsync(_libraryStore);
                StatusMessage = $"Updated thumbnail for {comic.DisplayName}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Opens the selected comic, pushes it to the top of Recent Library, and launches the reader.
        /// </summary>
        [RelayCommand]
        private async Task OpenComicAsync(ComicBook? comic)
        {
            if (comic is null) return;

            comic.LastAccessedTime = DateTime.UtcNow;
            await _storageService.SaveAsync(_libraryStore);
            ApplyFilter();

            var readerVm = new ReaderViewModel(_readerFactory, _storageService, _bookmarkService);
            readerVm.CloseRequested += OnReaderClosed;
            ActiveReader = readerVm;

            await readerVm.LoadComicAsync(comic);
        }

        private void OnReaderClosed()
        {
            if (ActiveReader is not null)
            {
                ActiveReader.CloseRequested -= OnReaderClosed;
                ActiveReader = null;
            }

            // ComicBook properties (CurrentPage, TotalPages, ProgressPercentage) are already updated
            // in-memory via INotifyPropertyChanged, avoiding an expensive disk reload and UI rebuild.
            StatusMessage = $"{FilteredComics.Count} recent comic(s)";
        }

        private async Task<bool> TryImportSingleFileAsync(string filePath)
        {
            bool alreadyExists = _libraryStore.Comics.Any(c =>
                c.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            if (alreadyExists) return false;

            ComicFormat format = _fileService.DetectFormat(filePath);
            if (format == ComicFormat.Unknown) return false;

            Dispatcher.UIThread.Post(() =>
                StatusMessage = $"Importing {Path.GetFileName(filePath)}...");

            IComicReaderService reader = _readerFactory.GetReader(format);

            int totalPages = await reader.GetPageCountAsync(filePath);
            Bitmap? cover = await reader.ExtractCoverAsync(filePath);

            var comic = new ComicBook
            {
                DisplayName = Path.GetFileNameWithoutExtension(filePath),
                FilePath = filePath,
                ParentFolderName = null,
                Format = format,
                TotalPages = totalPages,
                CurrentPage = 1,
                ZoomLevel = AppConstants.DefaultZoomLevel,
                LastAccessedTime = DateTime.UtcNow
            };

            if (cover is not null)
            {
                comic.CoverImagePath = await _fileService.SaveCoverImageAsync(comic.Id, cover);
            }

            _libraryStore.Comics.Add(comic);
            return true;
        }

        private void SyncProgressToComics()
        {
            foreach (ComicBook comic in _libraryStore.Comics)
            {
                if (string.IsNullOrWhiteSpace(comic.DisplayName) && !string.IsNullOrWhiteSpace(comic.FilePath))
                {
                    comic.DisplayName = Path.GetFileNameWithoutExtension(comic.FilePath);
                }

                if (_libraryStore.ProgressByComicId.TryGetValue(comic.Id, out ReadingProgress? progress))
                {
                    comic.CurrentPage = progress.CurrentPage;
                    comic.ZoomLevel = progress.ZoomLevel > 0 ? progress.ZoomLevel : AppConstants.DefaultZoomLevel;
                }
            }
        }

        private void ApplyFilter()
        {
            IEnumerable<ComicBook> query = _libraryStore.Comics
                .Where(c => string.IsNullOrEmpty(c.ParentFolderPath)
                            || c.LastAccessedTime > DateTime.MinValue
                            || c.CurrentPage > 1)
                .OrderByDescending(c => c.LastAccessedTime);

            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                query = _libraryStore.Comics
                    .Where(c => c.DisplayName.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(c => c.LastAccessedTime);
            }

            FilteredComics = new ObservableCollection<ComicBook>(query);
        }
    }
}
