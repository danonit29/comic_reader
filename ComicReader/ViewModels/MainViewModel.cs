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
        private CancellationTokenSource? _thumbnailLoadCts;
        private bool _isSettingThumbnailChapterProgrammatically;

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

        [ObservableProperty]
        private bool _isThumbnailPickerOpen;

        [ObservableProperty]
        private ComicBook? _thumbnailTargetComic;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMultipleThumbnailChapters))]
        private ObservableCollection<ChapterModel> _thumbnailChapters = new();

        [ObservableProperty]
        private ChapterModel? _selectedThumbnailChapter;

        [ObservableProperty]
        private ObservableCollection<ThumbnailPageOption> _thumbnailPageOptions = new();

        [ObservableProperty]
        private bool _isLoadingThumbnailPages;

        public bool HasMultipleThumbnailChapters => ThumbnailChapters.Count > 1;

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
            FolderVm.FolderImported += folder =>
            {
                ApplyFilter();
                ActiveSection = AppConstants.SectionFolders;
                FolderVm.OpenFolderCommand.Execute(folder);
            };
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
            int failedCount = 0;
            string? lastError = null;

            try
            {
                foreach (string filePath in files)
                {
                    try
                    {
                        if (await TryImportSingleFileAsync(filePath))
                        {
                            importedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        lastError = $"{Path.GetFileName(filePath)}: {ex.Message}";
                    }
                }

                await _storageService.SaveAsync(_libraryStore);

                FolderVm.SyncFromStore(_libraryStore);
                ApplyFilter();
                StatusMessage = failedCount > 0
                    ? $"Imported {importedCount} comic(s), {failedCount} failed"
                    : $"Imported {importedCount} comic(s)";

                if (failedCount > 0)
                {
                    await ShowNotificationAsync($"Lỗi import ({lastError})", isError: true);
                }
                else if (importedCount > 0)
                {
                    await ShowNotificationAsync("Đã import xong comic");
                }
                else
                {
                    await ShowNotificationAsync("Truyện này đã có trong thư viện");
                }
            }
            catch (Exception ex)
            {
                await ShowNotificationAsync($"Lỗi import: {ex.Message}", isError: true);
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
        /// Opens the 2-option Change Thumbnail picker (from inside the comic/chapter or from Explorer/Library).
        /// </summary>
        [RelayCommand]
        private async Task SelectThumbnailAsync(ComicBook? comic)
        {
            if (comic is null) return;

            CancelThumbnailPreviewLoad();
            ThumbnailTargetComic = comic;
            ThumbnailPageOptions = new ObservableCollection<ThumbnailPageOption>();
            IsThumbnailPickerOpen = true;
            IsLoadingThumbnailPages = true;

            try
            {
                IReadOnlyList<ChapterModel> chapters = await _readerFactory.DiscoverChaptersAsync(comic);
                if (chapters.Count == 0)
                {
                    chapters = new[]
                    {
                        new ChapterModel
                        {
                            ChapterId = comic.Id,
                            Title = comic.DisplayName,
                            Format = comic.Format,
                            SourcePath = comic.FilePath,
                            PageCount = comic.TotalPages
                        }
                    };
                }

                _isSettingThumbnailChapterProgrammatically = true;
                try
                {
                    ThumbnailChapters = new ObservableCollection<ChapterModel>(chapters);
                    SelectedThumbnailChapter = ThumbnailChapters[0];
                }
                finally
                {
                    _isSettingThumbnailChapterProgrammatically = false;
                }

                await LoadThumbnailChapterPagesAsync(ThumbnailChapters[0]);
            }
            catch (Exception ex)
            {
                IsLoadingThumbnailPages = false;
                await ShowNotificationAsync($"Không thể tải danh sách trang: {ex.Message}", isError: true);
            }
        }

        partial void OnSelectedThumbnailChapterChanged(ChapterModel? value)
        {
            if (_isSettingThumbnailChapterProgrammatically || value is null || !IsThumbnailPickerOpen)
            {
                return;
            }

            _ = LoadThumbnailChapterPagesAsync(value);
        }

        [RelayCommand]
        private async Task PickThumbnailFromDeviceAsync()
        {
            ComicBook? comic = ThumbnailTargetComic;
            if (comic is null) return;

            string? imagePath = await _fileService.PickImageFileAsync();
            if (string.IsNullOrWhiteSpace(imagePath)) return;

            CloseThumbnailPicker();
            IsBusy = true;
            StatusMessage = $"Updating thumbnail for {comic.DisplayName}...";

            try
            {
                string cachedPath = await _fileService.SaveCustomThumbnailAsync(comic.Id, imagePath);
                comic.CoverImagePath = cachedPath;
                await _storageService.SaveAsync(_libraryStore);
                StatusMessage = $"Updated thumbnail for {comic.DisplayName}";
                await ShowNotificationAsync("Đã cập nhật ảnh bìa");
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task SelectThumbnailPageAsync(ThumbnailPageOption? option)
        {
            ComicBook? comic = ThumbnailTargetComic;
            ChapterModel? chapter = SelectedThumbnailChapter;
            if (comic is null || option is null) return;

            IsBusy = true;
            StatusMessage = $"Đang đặt trang {option.PageNumber} làm ảnh bìa...";

            try
            {
                Bitmap? coverBitmap = option.Preview;
                if (coverBitmap is null && chapter is not null)
                {
                    IComicReaderService reader = _readerFactory.GetReader(
                        chapter.Format != ComicFormat.Unknown ? chapter.Format : comic.Format);
                    using IComicReadSession session = await reader.OpenChapterSessionAsync(
                        string.IsNullOrWhiteSpace(chapter.SourcePath) ? comic.FilePath : chapter.SourcePath,
                        chapter);
                    coverBitmap = await session.RenderPreviewAsync(option.PageNumber - 1, AppConstants.CoverDecodeWidth);
                }

                if (coverBitmap is not null)
                {
                    string cachedPath = await _fileService.SaveCustomThumbnailBitmapAsync(comic.Id, coverBitmap);
                    comic.CoverImagePath = cachedPath;
                    await _storageService.SaveAsync(_libraryStore);
                    StatusMessage = $"Updated thumbnail for {comic.DisplayName}";
                    CloseThumbnailPicker();
                    await ShowNotificationAsync($"Đã chọn trang {option.PageNumber} làm ảnh bìa");
                }
            }
            finally
            {
                IsBusy = false;
                if (IsThumbnailPickerOpen)
                {
                    CloseThumbnailPicker();
                }
            }
        }

        [RelayCommand]
        private void CloseThumbnailPicker()
        {
            CancelThumbnailPreviewLoad();
            IsThumbnailPickerOpen = false;
            IsLoadingThumbnailPages = false;
            ThumbnailTargetComic = null;
            ThumbnailPageOptions.Clear();
            ThumbnailChapters.Clear();
        }

        private void CancelThumbnailPreviewLoad()
        {
            _thumbnailLoadCts?.Cancel();
            _thumbnailLoadCts?.Dispose();
            _thumbnailLoadCts = null;
        }

        private async Task LoadThumbnailChapterPagesAsync(ChapterModel chapter)
        {
            ComicBook? comic = ThumbnailTargetComic;
            if (comic is null) return;

            CancelThumbnailPreviewLoad();
            var cts = new CancellationTokenSource();
            _thumbnailLoadCts = cts;
            CancellationToken token = cts.Token;

            IsLoadingThumbnailPages = true;
            ThumbnailPageOptions = new ObservableCollection<ThumbnailPageOption>();

            try
            {
                IComicReaderService reader = _readerFactory.GetReader(
                    chapter.Format != ComicFormat.Unknown ? chapter.Format : comic.Format);
                using IComicReadSession session = await reader.OpenChapterSessionAsync(
                    string.IsNullOrWhiteSpace(chapter.SourcePath) ? comic.FilePath : chapter.SourcePath,
                    chapter,
                    token);

                int maxPreviewCount = Math.Min(session.PageCount, 60);
                var options = new ObservableCollection<ThumbnailPageOption>();
                for (int i = 1; i <= maxPreviewCount; i++)
                {
                    options.Add(new ThumbnailPageOption { PageNumber = i });
                }
                ThumbnailPageOptions = options;
                IsLoadingThumbnailPages = false;

                for (int i = 0; i < maxPreviewCount; i++)
                {
                    if (token.IsCancellationRequested) break;
                    Bitmap? preview = await session.RenderPreviewAsync(i, AppConstants.CoverDecodeWidth, token);
                    if (preview is not null && !token.IsCancellationRequested)
                    {
                        options[i].Preview = preview;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Replaced by another chapter or closed.
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    IsLoadingThumbnailPages = false;
                }
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
            ComicBook? existing = _libraryStore.Comics.FirstOrDefault(c =>
                c.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.LastAccessedTime = DateTime.UtcNow;
                return true;
            }

            ComicFormat format = _fileService.DetectFormat(filePath);
            if (format == ComicFormat.Unknown) return false;

            Dispatcher.UIThread.Post(() =>
                StatusMessage = $"Importing {Path.GetFileName(filePath)}...");

            IComicReaderService reader = _readerFactory.GetReader(format);

            int totalPages = await reader.GetPageCountAsync(filePath);

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

            try
            {
                using Bitmap? cover = await reader.ExtractCoverAsync(filePath);
                if (cover is not null)
                {
                    comic.CoverImagePath = await _fileService.SaveCoverImageAsync(comic.Id, cover);
                }
            }
            catch
            {
                // Cover extraction failure should never block importing the comic.
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
