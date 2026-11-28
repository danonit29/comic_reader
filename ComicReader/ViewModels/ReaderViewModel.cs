using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    /// ViewModel for the vertical continuous comic reader using single-open session caching,
    /// sliding-window lazy loading, and memory eviction for smooth 1000+ page performance.
    /// </summary>
    public partial class ReaderViewModel : ObservableObject
    {
        private const int FullLoadPageThreshold = 200;
        private const int LargeBookInitialPreloadCount = 100;
        // Keep only 3 behind + current + 4 ahead = max 8 decoded Full-Quality RGBA Bitmaps (~150 MB instead of 400-900 MB).
        private const int PreloadBehindCount = 3;
        private const int PreloadAheadCount  = 4;
        // Wider ±8 window keeps raw compressed JPEG/PNG bytes (~0.3-1 MB/page) in RAM for 10-25ms instant Full-Quality decode.
        private const int CompressedWindowRadius = 8;

        private readonly IComicReaderFactory _readerFactory;
        private readonly IStorageService _storageService;
        private readonly IBookmarkService _bookmarkService;
        private readonly ImageCacheManager _imageCache = new(keepBehind: PreloadBehindCount, keepAhead: PreloadAheadCount);
        private readonly SemaphoreSlim _preloadConcurrency = new(2, 2);
        private readonly ConcurrentDictionary<int, byte> _inFlightPages = new();
        private readonly ConcurrentDictionary<int, byte> _inFlightPreviews = new();
        private readonly ConcurrentDictionary<int, byte> _inFlightCompressed = new();
        private readonly object _windowStateLock = new();
        private long _windowGeneration;
        private int _activeCenterIndex = -1;
        private int _scrollDirection = 1; // +1 = forward/down, -1 = backward/up
        private int _lastPdfRenderDpi = AppConstants.DefaultRenderDpi;
        private CancellationTokenSource? _loadPageCts;
        private CancellationTokenSource? _zoomRerenderCts;
        private CancellationTokenSource? _bulkPreloadCts;
        private CancellationTokenSource? _saveDebounceCts;
        private CancellationTokenSource? _toastCts;
        private ComicBook? _currentComic;
        private IComicReadSession? _readSession;
        private double _currentScrollRatio;
        private double _currentPageLocalRatio;
        private bool _hasRestoredInitialScroll;
        private bool _isChangingChapterProgrammatically;
        private volatile bool _isClosed;

        [ObservableProperty]
        private string _comicTitle = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMultipleChapters))]
        [NotifyPropertyChangedFor(nameof(HasNextChapter))]
        [NotifyPropertyChangedFor(nameof(HasPreviousChapter))]
        private ObservableCollection<ChapterModel> _chapters = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMultipleChapters))]
        [NotifyPropertyChangedFor(nameof(HasNextChapter))]
        [NotifyPropertyChangedFor(nameof(HasPreviousChapter))]
        [NotifyPropertyChangedFor(nameof(CurrentChapterId))]
        [NotifyPropertyChangedFor(nameof(CurrentChapterBookmarks))]
        [NotifyPropertyChangedFor(nameof(IsCurrentPageBookmarked))]
        private ChapterModel? _selectedChapter;

        [ObservableProperty]
        private ObservableCollection<ComicPageItem> _pages = new();

        [ObservableProperty]
        private int _currentPage = 1;

        [ObservableProperty]
        private int _totalPages;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ZoomPercentText))]
        private double _zoomLevel = AppConstants.DefaultZoomLevel;

        [ObservableProperty]
        private bool _isLoadingPage;

        [ObservableProperty]
        private string _loadingStatusText = "Đang load...";

        [ObservableProperty]
        private bool _isBookmarkToastVisible;

        [ObservableProperty]
        private string _bookmarkToastMessage = "Đã lưu dấu trang";

        [ObservableProperty]
        private bool _isBookmarkPanelOpen;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CurrentChapterBookmarks))]
        [NotifyPropertyChangedFor(nameof(IsCurrentPageBookmarked))]
        private ObservableCollection<Bookmark> _bookmarks = new();

        /// <summary>
        /// Event raised when the user exits the reader back to the library.
        /// </summary>
        public event Action? CloseRequested;

        /// <summary>
        /// Event raised when the view should scroll to a specific 1-based page number.
        /// </summary>
        public event Action<int>? ScrollToPageRequested;

        /// <summary>
        /// Event raised when the view should restore the exact page and intra-page offset (0.0 - 1.0).
        /// </summary>
        public event Action<int, double>? RestoreExactPagePositionRequested;

        /// <summary>
        /// Event raised just before ZoomLevel changes so the view can preserve its focal point.
        /// </summary>
        public event Action<double, double>? ZoomChanging;

        /// <summary>
        /// Initializes a new instance of the <see cref="ReaderViewModel"/> class.
        /// </summary>
        public ReaderViewModel(
            IComicReaderFactory readerFactory,
            IStorageService storageService,
            IBookmarkService bookmarkService)
        {
            _readerFactory = readerFactory;
            _storageService = storageService;
            _bookmarkService = bookmarkService;
        }

        /// <summary>
        /// Formatted page indicator text (e.g., "Page 15 of 37").
        /// </summary>
        public string PageIndicatorText => $"Page {CurrentPage} of {TotalPages}";

        /// <summary>
        /// 1-based current page index alias for chapter/page synchronization.
        /// </summary>
        public int CurrentPageIndex => CurrentPage;

        /// <summary>
        /// Formatted zoom percentage text (e.g., "100%").
        /// </summary>
        public string ZoomPercentText => $"{ZoomLevel:P0}";

        /// <summary>
        /// True when the loaded comic (Folder, ZIP/CBZ, or PDF) has 2 or more chapters.
        /// </summary>
        public bool HasMultipleChapters => Chapters.Count > 1;

        /// <summary>
        /// True when there is a next chapter after <see cref="SelectedChapter"/>.
        /// </summary>
        public bool HasNextChapter
        {
            get
            {
                if (SelectedChapter is null || Chapters.Count <= 1) return false;
                int idx = Chapters.IndexOf(SelectedChapter);
                return idx >= 0 && idx < Chapters.Count - 1;
            }
        }

        /// <summary>
        /// True when there is a previous chapter before <see cref="SelectedChapter"/>.
        /// </summary>
        public bool HasPreviousChapter => SelectedChapter is not null && Chapters.IndexOf(SelectedChapter) > 0;

        /// <summary>
        /// Active chapter identifier used to isolate bookmarks and progress per chapter.
        /// </summary>
        public string CurrentChapterId => SelectedChapter?.ChapterId ?? _currentComic?.Id ?? string.Empty;

        /// <summary>
        /// Bookmarks belonging only to the currently open chapter (plotted on the page slider track).
        /// </summary>
        public ObservableCollection<Bookmark> CurrentChapterBookmarks =>
            new(Bookmarks.Where(b => string.Equals(GetNormalizedChapterId(b), CurrentChapterId, StringComparison.Ordinal)));

        /// <summary>
        /// Indicates whether the current page in the active chapter has a saved bookmark.
        /// </summary>
        public bool IsCurrentPageBookmarked =>
            Bookmarks.Any(b => string.Equals(GetNormalizedChapterId(b), CurrentChapterId, StringComparison.Ordinal) &&
                               b.PageNumber == CurrentPage);

        partial void OnCurrentPageChanged(int value)
        {
            OnPropertyChanged(nameof(CurrentPageIndex));
            OnPropertyChanged(nameof(PageIndicatorText));
            OnPropertyChanged(nameof(IsCurrentPageBookmarked));
        }

        partial void OnTotalPagesChanged(int value) => OnPropertyChanged(nameof(PageIndicatorText));

        partial void OnSelectedChapterChanged(ChapterModel? value)
        {
            if (_isChangingChapterProgrammatically || value is null || _currentComic is null || _isClosed)
            {
                return;
            }

            _ = SwitchToChapterFromUiAsync(value);
        }

        private async Task SwitchToChapterFromUiAsync(ChapterModel targetChapter)
        {
            await SaveProgressAsync();
            await LoadChapterSessionAsync(targetChapter, targetPage: 1, targetLocalRatio: 0.0);
            await SaveProgressAsync();
        }

        /// <summary>
        /// Discovers chapters (&lt; 200 pages each), restores saved progress/bookmarks,
        /// and opens the active chapter's reading session.
        /// </summary>
        public async Task LoadComicAsync(ComicBook comic)
        {
            CancelBulkPreload();
            CancelWindowLoading();
            DisposeReadSession();
            _isClosed = false;
            _hasRestoredInitialScroll = false;

            _currentComic = comic;
            ComicTitle = comic.DisplayName;

            IReadOnlyList<ChapterModel> discovered = await _readerFactory.DiscoverChaptersAsync(comic);
            if (_isClosed) return;

            if (discovered.Count == 0)
            {
                discovered = new[]
                {
                    new ChapterModel
                    {
                        ChapterId = comic.Id,
                        Title = comic.DisplayName,
                        OrderIndex = 0,
                        Format = comic.Format,
                        SourcePath = comic.FilePath,
                        PageCount = comic.TotalPages
                    }
                };
            }

            _isChangingChapterProgrammatically = true;
            try
            {
                Chapters = new ObservableCollection<ChapterModel>(discovered);
                OnPropertyChanged(nameof(HasMultipleChapters));
            }
            finally
            {
                _isChangingChapterProgrammatically = false;
            }

            ReadingProgress? saved = await _storageService.GetProgressAsync(comic.Id);
            IReadOnlyList<Bookmark> savedBookmarks = await _bookmarkService.GetBookmarksAsync(comic.Id);
            if (_isClosed)
            {
                Cleanup();
                return;
            }

            Bookmarks = new ObservableCollection<Bookmark>(savedBookmarks);

            Bookmark? latestBookmark = savedBookmarks.OrderByDescending(b => b.CreatedAtUtc).FirstOrDefault();

            ChapterModel initialChapter = Chapters[0];
            if (latestBookmark is not null)
            {
                ChapterModel? bmChapter = Chapters.FirstOrDefault(c =>
                    string.Equals(c.ChapterId, latestBookmark.ChapterId, StringComparison.Ordinal));
                if (bmChapter is not null)
                {
                    initialChapter = bmChapter;
                }
            }
            else if (saved is not null && !string.IsNullOrWhiteSpace(saved.CurrentChapterId))
            {
                ChapterModel? savedChapter = Chapters.FirstOrDefault(c =>
                    string.Equals(c.ChapterId, saved.CurrentChapterId, StringComparison.Ordinal));
                if (savedChapter is not null)
                {
                    initialChapter = savedChapter;
                }
            }

            int initialPage = latestBookmark is not null &&
                              string.Equals(GetNormalizedChapterId(latestBookmark), initialChapter.ChapterId, StringComparison.Ordinal)
                ? latestBookmark.PageNumber
                : (saved is not null ? saved.CurrentPage : comic.CurrentPage);

            double initialLocalRatio = latestBookmark is not null &&
                                       string.Equals(GetNormalizedChapterId(latestBookmark), initialChapter.ChapterId, StringComparison.Ordinal)
                ? latestBookmark.PageLocalOffsetRatio
                : (saved?.PageLocalOffsetRatio ?? 0.0);

            _currentScrollRatio = saved?.ScrollOffsetRatio ?? 0.0;

            _isChangingChapterProgrammatically = true;
            try
            {
                SelectedChapter = initialChapter;
            }
            finally
            {
                _isChangingChapterProgrammatically = false;
            }

            await LoadChapterSessionAsync(initialChapter, initialPage, initialLocalRatio);
        }

        private async Task LoadChapterSessionAsync(ChapterModel chapter, int targetPage, double targetLocalRatio)
        {
            if (_currentComic is null || _isClosed) return;

            CancelBulkPreload();
            CancelWindowLoading();
            _hasRestoredInitialScroll = false;

            _imageCache.Clear(index =>
            {
                if (index >= 0 && index < Pages.Count)
                {
                    Pages[index].Image = null;
                }
            });
            Pages.Clear();

            IComicReadSession? previousSession = _readSession;
            _readSession = null;
            if (previousSession is not null)
            {
                _ = Task.Run(previousSession.Dispose);
            }

            IComicReaderService readerService = _readerFactory.GetReader(chapter.Format != ComicFormat.Unknown ? chapter.Format : _currentComic.Format);
            IComicReadSession openedSession = await readerService.OpenChapterSessionAsync(
                string.IsNullOrWhiteSpace(chapter.SourcePath) ? _currentComic.FilePath : chapter.SourcePath,
                chapter);

            if (_isClosed)
            {
                _ = Task.Run(openedSession.Dispose);
                return;
            }

            _readSession = openedSession;
            TotalPages = _readSession.PageCount;

            ComicTitle = HasMultipleChapters
                ? $"{_currentComic.DisplayName} — {chapter.Title}"
                : _currentComic.DisplayName;

            CurrentPage = Math.Clamp(targetPage, 1, Math.Max(1, TotalPages));
            ZoomLevel = AppConstants.DefaultZoomLevel;
            _currentPageLocalRatio = Math.Clamp(targetLocalRatio, 0.0, 1.0);

            OnPropertyChanged(nameof(CurrentChapterId));
            OnPropertyChanged(nameof(CurrentChapterBookmarks));
            OnPropertyChanged(nameof(IsCurrentPageBookmarked));
            OnPropertyChanged(nameof(HasNextChapter));
            OnPropertyChanged(nameof(HasPreviousChapter));

            InitializePagePlaceholders();
            _imageCache.ConfigureWindow(PreloadBehindCount, PreloadAheadCount);

            Task initialWindowTask = EnsurePageWindowLoadedAsync(CurrentPage - 1, reanchorAfterLoad: true);

            if (TotalPages > 1)
            {
                if (TotalPages <= FullLoadPageThreshold)
                {
                    StartBulkPreviewPreload(TotalPages, CurrentPage - 1, initialWindowTask);
                }
                else if (CurrentPage <= LargeBookInitialPreloadCount)
                {
                    StartBulkPreviewPreload(LargeBookInitialPreloadCount, CurrentPage - 1, initialWindowTask);
                }
            }
        }

        [RelayCommand]
        private async Task NextChapterAsync()
        {
            if (SelectedChapter is null || Chapters.Count <= 1) return;
            int idx = Chapters.IndexOf(SelectedChapter);
            if (idx < 0 || idx >= Chapters.Count - 1) return;

            ChapterModel next = Chapters[idx + 1];
            _isChangingChapterProgrammatically = true;
            try
            {
                SelectedChapter = next;
            }
            finally
            {
                _isChangingChapterProgrammatically = false;
            }

            await SwitchToChapterFromUiAsync(next);
        }

        [RelayCommand]
        private async Task PreviousChapterAsync()
        {
            if (SelectedChapter is null || Chapters.Count <= 1) return;
            int idx = Chapters.IndexOf(SelectedChapter);
            if (idx <= 0) return;

            ChapterModel prev = Chapters[idx - 1];
            _isChangingChapterProgrammatically = true;
            try
            {
                SelectedChapter = prev;
            }
            finally
            {
                _isChangingChapterProgrammatically = false;
            }

            await SwitchToChapterFromUiAsync(prev);
        }

        [RelayCommand]
        private void ToggleBookmarkPanel() => IsBookmarkPanelOpen = !IsBookmarkPanelOpen;

        /// <summary>
        /// Updates the exact current page and intra-page scroll offset, loads nearby pages, and debounces progress saving.
        /// </summary>
        public void UpdateScrollPosition(int pageNumber, double scrollRatio, double pageLocalRatio)
        {
            // Do not overwrite saved page/offset before the initial scroll position has been restored.
            if (!_hasRestoredInitialScroll) return;

            _currentScrollRatio    = Math.Clamp(scrollRatio,    0.0, 1.0);
            _currentPageLocalRatio = Math.Clamp(pageLocalRatio, 0.0, 1.0);
            int clampedPage = Math.Clamp(pageNumber, 1, Math.Max(1, TotalPages));

            if (clampedPage != CurrentPage)
            {
                _scrollDirection = clampedPage >= CurrentPage ? 1 : -1;
                CurrentPage = clampedPage;
            }

            // Always ensure a preload window around the new center, whether or not page changed.
            _ = EnsurePageWindowLoadedAsync(clampedPage - 1);
            ScheduleDebouncedSaveProgress();
        }

        /// <summary>
        /// Scrolls to the next page if available.
        /// </summary>
        [RelayCommand]
        private async Task NextPageAsync()
        {
            if (CurrentPage >= TotalPages) return;
            await GoToPageAsync(CurrentPage + 1);
        }

        /// <summary>
        /// Scrolls to the previous page if available.
        /// </summary>
        [RelayCommand]
        private async Task PreviousPageAsync()
        {
            if (CurrentPage <= 1) return;
            await GoToPageAsync(CurrentPage - 1);
        }

        /// <summary>
        /// Navigates to a saved bookmark's exact ChapterId, PageNumber, and intra-page scroll position.
        /// </summary>
        [RelayCommand]
        private async Task NavigateToBookmarkAsync(Bookmark? bookmark)
        {
            if (bookmark is null) return;

            string targetChapterId = GetNormalizedChapterId(bookmark);
            if (!string.Equals(targetChapterId, CurrentChapterId, StringComparison.Ordinal))
            {
                ChapterModel? targetChapter = Chapters.FirstOrDefault(c =>
                    string.Equals(c.ChapterId, targetChapterId, StringComparison.Ordinal));

                if (targetChapter is not null)
                {
                    _isChangingChapterProgrammatically = true;
                    try
                    {
                        SelectedChapter = targetChapter;
                    }
                    finally
                    {
                        _isChangingChapterProgrammatically = false;
                    }

                    await LoadChapterSessionAsync(targetChapter, bookmark.PageNumber, bookmark.PageLocalOffsetRatio);
                    await SaveProgressAsync();
                    return;
                }
            }

            if (bookmark.PageNumber < 1 || bookmark.PageNumber > TotalPages) return;

            CancelWindowLoading();
            CurrentPage = bookmark.PageNumber;
            _currentPageLocalRatio = bookmark.PageLocalOffsetRatio;
            _hasRestoredInitialScroll = true;

            if (Pages[bookmark.PageNumber - 1].Image is null && !_imageCache.ContainsFull(bookmark.PageNumber - 1))
            {
                LoadingStatusText = $"Đang load trang {bookmark.PageNumber}...";
                IsLoadingPage = true;
            }

            if (bookmark.PageLocalOffsetRatio > 0.001)
            {
                RestoreExactPagePositionRequested?.Invoke(bookmark.PageNumber, bookmark.PageLocalOffsetRatio);
            }
            else
            {
                ScrollToPageRequested?.Invoke(bookmark.PageNumber);
            }

            _ = EnsurePageWindowLoadedAsync(bookmark.PageNumber - 1, cancelPrevious: true, reanchorAfterLoad: true);
            await SaveProgressAsync();
        }

        /// <summary>
        /// Scrolls the viewport immediately to the previewed page while dragging the slider,
        /// without triggering heavy background decoding for every intermediate page.
        /// </summary>
        public void PreviewScrollToPage(int pageNumber)
        {
            if (pageNumber < 1 || pageNumber > TotalPages) return;
            if (pageNumber == CurrentPage) return;

            _scrollDirection = pageNumber >= CurrentPage ? 1 : -1;
            CurrentPage = pageNumber;
            _hasRestoredInitialScroll = true;
            ScrollToPageRequested?.Invoke(pageNumber);
        }

        /// <summary>
        /// Loads only the single target page (no neighbor preloading) when the user pauses mid-drag.
        /// </summary>
        public void PreviewLoadSinglePage(int pageNumber)
        {
            if (_readSession is null || pageNumber < 1 || pageNumber > TotalPages) return;
            int idx = pageNumber - 1;
            if (_imageCache.ContainsFull(idx)) return;

            if (Pages[idx].Image is null)
            {
                LoadingStatusText = $"Đang load trang {pageNumber}...";
                IsLoadingPage = true;
            }

            CancelWindowLoading();
            CancellationToken token;
            lock (_windowStateLock)
            {
                _activeCenterIndex = idx;
                Interlocked.Increment(ref _windowGeneration);
                _loadPageCts = new CancellationTokenSource();
                token = _loadPageCts.Token;
            }

            _ = LoadSingleWithDeadlineAsync(idx, token);
        }

        private async Task LoadSingleWithDeadlineAsync(int pageIndex, CancellationToken token)
        {
            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadlineCts.CancelAfter(2000);
            try
            {
                await LoadPageAsync(pageIndex, deadlineCts.Token, usePreloadThrottle: false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled by newer request or 2s deadline.
            }
            finally
            {
                if (!token.IsCancellationRequested && _activeCenterIndex == pageIndex)
                {
                    Dispatcher.UIThread.Post(() => IsLoadingPage = false);
                }
            }
        }

        /// <summary>
        /// Unified navigation entry point (slider, Next/Prev, keyboard, direct page input).
        /// Cancels stale loads, displays cached page immediately or shows loading indicator, and loads window.
        /// </summary>
        public void JumpToPage(int pageNumber)
        {
            _ = GoToPageAsync(pageNumber);
        }

        /// <summary>
        /// Navigates immediately to the given 1-based page number.
        /// </summary>
        public void NavigateToPage(int pageNumber) => JumpToPage(pageNumber);

        /// <summary>
        /// Navigates to a specific 1-based page number, cancels stale loads, loads its window, and persists progress.
        /// </summary>
        [RelayCommand]
        private async Task GoToPageAsync(int pageNumber)
        {
            if (pageNumber < 1 || pageNumber > TotalPages) return;

            CancelWindowLoading();

            Bookmark? bookmark = Bookmarks.FirstOrDefault(b =>
                string.Equals(GetNormalizedChapterId(b), CurrentChapterId, StringComparison.Ordinal) &&
                b.PageNumber == pageNumber);
            double targetLocalRatio = bookmark?.PageLocalOffsetRatio ?? 0.0;

            CurrentPage = pageNumber;
            _currentPageLocalRatio = targetLocalRatio;
            _hasRestoredInitialScroll = true;

            if (Pages[pageNumber - 1].Image is null && !_imageCache.Contains(pageNumber - 1))
            {
                LoadingStatusText = $"Đang load trang {pageNumber}...";
                IsLoadingPage = true;
            }

            if (targetLocalRatio > 0.001)
            {
                RestoreExactPagePositionRequested?.Invoke(pageNumber, targetLocalRatio);
            }
            else
            {
                ScrollToPageRequested?.Invoke(pageNumber);
            }

            _ = EnsurePageWindowLoadedAsync(pageNumber - 1, cancelPrevious: true, reanchorAfterLoad: true);
            await SaveProgressAsync();
        }

        [ObservableProperty]
        private bool _isFitWidth = true;

        [ObservableProperty]
        private bool _isFitPage;

        public event Action? FitWidthRequested;
        public event Action? FitPageRequested;

        /// <summary>
        /// Increases the zoom factor up to the configured maximum.
        /// </summary>
        [RelayCommand]
        private void ZoomIn()
        {
            IsFitWidth = false;
            IsFitPage = false;
            ApplyZoom(Math.Min(AppConstants.MaxZoomLevel, ZoomLevel + AppConstants.ZoomStep));
        }

        /// <summary>
        /// Decreases the zoom factor down to the configured minimum.
        /// </summary>
        [RelayCommand]
        private void ZoomOut()
        {
            IsFitWidth = false;
            IsFitPage = false;
            ApplyZoom(Math.Max(AppConstants.MinZoomLevel, ZoomLevel - AppConstants.ZoomStep));
        }

        /// <summary>
        /// Resets zoom back to 100% (Fit Width).
        /// </summary>
        [RelayCommand]
        private void ResetZoom()
        {
            IsFitWidth = true;
            IsFitPage = false;
            ApplyZoom(AppConstants.DefaultZoomLevel);
        }

        [RelayCommand]
        private void FitWidth()
        {
            IsFitWidth = true;
            IsFitPage = false;
            FitWidthRequested?.Invoke();
        }

        [RelayCommand]
        private void FitPage()
        {
            IsFitWidth = false;
            IsFitPage = true;
            FitPageRequested?.Invoke();
        }

        public void SetZoomFromView(double newZoom, bool preserveFocal = true)
        {
            double clamped = Math.Clamp(newZoom, AppConstants.MinZoomLevel, AppConstants.MaxZoomLevel);
            double oldZoom = ZoomLevel;
            if (Math.Abs(clamped - oldZoom) < 0.001) return;

            if (preserveFocal)
                ZoomChanging?.Invoke(oldZoom, clamped);

            ZoomLevel = clamped;
        }

        private async void ApplyZoom(double newZoom)
        {
            double oldZoom = ZoomLevel;
            if (Math.Abs(newZoom - oldZoom) < 0.001) return;

            ZoomChanging?.Invoke(oldZoom, newZoom);
            ZoomLevel = newZoom;
            ScheduleDynamicZoomRerenderIfNeeded();
            await SaveProgressAsync();
        }

        private void ScheduleDynamicZoomRerenderIfNeeded()
        {
            if (_readSession is not { SupportsDynamicZoomDpi: true } || Pages.Count == 0) return;

            int targetDpi = Math.Clamp((int)Math.Round(150.0 * Math.Max(1.0, ZoomLevel)), AppConstants.DefaultRenderDpi, 450);
            if (Math.Abs(targetDpi - _lastPdfRenderDpi) < 40) return;

            _zoomRerenderCts?.Cancel();
            _zoomRerenderCts?.Dispose();
            _zoomRerenderCts = new CancellationTokenSource();
            CancellationToken token = _zoomRerenderCts.Token;
            int pageIndex = Math.Clamp(CurrentPage - 1, 0, Pages.Count - 1);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(180, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested || _isClosed || _readSession is null) return;

                    Bitmap? sharp = await _readSession.RenderPageAsync(pageIndex, targetDpi, token).ConfigureAwait(false);
                    if (sharp is null || token.IsCancellationRequested || _isClosed)
                    {
                        sharp?.Dispose();
                        return;
                    }

                    _lastPdfRenderDpi = targetDpi;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested || _isClosed || pageIndex >= Pages.Count)
                        {
                            sharp.Dispose();
                            return;
                        }

                        _imageCache.SetFull(pageIndex, sharp);
                        Pages[pageIndex].Image = sharp;
                    });
                }
                catch (OperationCanceledException)
                {
                    // Superseded by newer zoom step.
                }
            }, token);
        }

        /// <summary>
        /// Toggles a bookmark for the exact (ComicId, ChapterId, PageNumber) without overwriting other pages.
        /// </summary>
        [RelayCommand]
        private async Task AddBookmarkAsync()
        {
            if (_currentComic is null) return;

            string chapterId = CurrentChapterId;
            string chapterTitle = HasMultipleChapters
                ? (SelectedChapter?.Title ?? _currentComic.DisplayName)
                : string.Empty;

            var bookmark = new Bookmark
            {
                BookmarkId = Guid.NewGuid().ToString("N"),
                ComicId = _currentComic.Id,
                ChapterId = chapterId,
                ChapterTitle = chapterTitle,
                PageNumber = CurrentPage,
                PageLocalOffsetRatio = _currentPageLocalRatio,
                Note = string.IsNullOrWhiteSpace(chapterTitle)
                    ? $"Trang {CurrentPage}"
                    : $"{chapterTitle} - Trang {CurrentPage}"
            };

            bool added = await _bookmarkService.SaveBookmarkAsync(bookmark);

            Bookmark? existing = Bookmarks.FirstOrDefault(b =>
                string.Equals(GetNormalizedChapterId(b), chapterId, StringComparison.Ordinal) &&
                b.PageNumber == CurrentPage);

            if (added)
            {
                if (existing is not null)
                {
                    Bookmarks.Remove(existing);
                }
                Bookmarks.Add(bookmark);
            }
            else if (existing is not null)
            {
                Bookmarks.Remove(existing);
            }

            if (CurrentPage >= 1 && CurrentPage <= Pages.Count)
            {
                Pages[CurrentPage - 1].IsBookmarked = added;
            }

            OnPropertyChanged(nameof(CurrentChapterBookmarks));
            OnPropertyChanged(nameof(IsCurrentPageBookmarked));
            CancelDebouncedSave();
            await SaveProgressAsync();
            await ShowBookmarkToastAsync(added
                ? $"Đã lưu dấu trang (Trang {CurrentPage})"
                : $"Đã xóa dấu trang (Trang {CurrentPage})");
        }

        /// <summary>
        /// Removes a specific bookmark from the list and JSON storage.
        /// </summary>
        [RelayCommand]
        private async Task RemoveBookmarkAsync(Bookmark? bookmark)
        {
            if (bookmark is null || _currentComic is null) return;

            await _bookmarkService.RemoveBookmarkByIdAsync(_currentComic.Id, bookmark.BookmarkId);
            Bookmarks.Remove(bookmark);

            if (string.Equals(GetNormalizedChapterId(bookmark), CurrentChapterId, StringComparison.Ordinal) &&
                bookmark.PageNumber >= 1 && bookmark.PageNumber <= Pages.Count)
            {
                Pages[bookmark.PageNumber - 1].IsBookmarked = false;
            }

            OnPropertyChanged(nameof(CurrentChapterBookmarks));
            OnPropertyChanged(nameof(IsCurrentPageBookmarked));
        }

        /// <summary>
        /// Closes the reader instantaneously on the UI thread, releases cached resources,
        /// and persists reading progress in the background.
        /// </summary>
        [RelayCommand]
        private void CloseReader()
        {
            _isClosed = true;
            CancelWindowLoading();
            CancelDebouncedSave();

            ReadingProgress? snapshot = CreateProgressSnapshot();
            if (snapshot is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _storageService.UpdateProgressAsync(snapshot).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore background save failures on close.
                    }
                });
            }

            Cleanup();
            CloseRequested?.Invoke();
        }

        /// <summary>
        /// Cancels all background loading tasks, detaches and disposes cached Bitmaps,
        /// and closes the active PDF/ZIP read session off the UI thread.
        /// </summary>
        public void Cleanup()
        {
            _isClosed = true;
            CancelBulkPreload();
            CancelWindowLoading();
            CancelDebouncedSave();

            _toastCts?.Cancel();
            _toastCts?.Dispose();
            _toastCts = null;

            TotalPages = 0;

            _imageCache.Clear(index =>
            {
                if (index >= 0 && index < Pages.Count)
                {
                    Pages[index].Image = null;
                }
            });

            foreach (ComicPageItem page in Pages)
            {
                page.Image = null;
            }

            Pages.Clear();

            // Dispose native PDF/ZIP session on a background thread so UI never blocks on DocnetLock.
            IComicReadSession? sessionToDispose = _readSession;
            _readSession = null;
            if (sessionToDispose is not null)
            {
                _ = Task.Run(() =>
                {
                    sessionToDispose.Dispose();
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                });
            }
        }

        private void InitializePagePlaceholders()
        {
            var bookmarkedPages = Bookmarks
                .Where(b => string.Equals(GetNormalizedChapterId(b), CurrentChapterId, StringComparison.Ordinal))
                .Select(static b => b.PageNumber)
                .ToHashSet();

            var items = new ObservableCollection<ComicPageItem>();
            for (int i = 1; i <= TotalPages; i++)
            {
                items.Add(new ComicPageItem
                {
                    PageNumber = i,
                    IsBookmarked = bookmarkedPages.Contains(i)
                });
            }

            Pages = items;
        }

        private string GetNormalizedChapterId(Bookmark bookmark) =>
            string.IsNullOrWhiteSpace(bookmark.ChapterId)
                ? (_currentComic?.Id ?? bookmark.ComicId)
                : bookmark.ChapterId;

        private async Task EnsurePageWindowLoadedAsync(int centerIndex, bool cancelPrevious = false, bool reanchorAfterLoad = false)
        {
            if (_readSession is null || Pages.Count == 0) return;

            int clampedCenter = Math.Clamp(centerIndex, 0, Pages.Count - 1);
            bool isLargeBookPast100 = Pages.Count > FullLoadPageThreshold && clampedCenter >= LargeBookInitialPreloadCount;

            if (isLargeBookPast100)
            {
                // Once past page 100 on >200-page books, cancel initial 1..100 preview fill and trim distant previews.
                CancelBulkPreload();
            }

            long generation;
            CancellationToken token;

            lock (_windowStateLock)
            {
                if (cancelPrevious || (_activeCenterIndex >= 0 && Math.Abs(clampedCenter - _activeCenterIndex) > 2))
                {
                    _loadPageCts?.Cancel();
                    _loadPageCts?.Dispose();
                    _loadPageCts = null;
                    _inFlightPages.Clear();
                }

                _activeCenterIndex = clampedCenter;
                generation = Interlocked.Increment(ref _windowGeneration);

                if (_loadPageCts is null || _loadPageCts.IsCancellationRequested)
                {
                    _loadPageCts?.Dispose();
                    _loadPageCts = new CancellationTokenSource();
                }

                token = _loadPageCts.Token;
            }

            // Always enforce Tier 2 decoded RGBA window (max 8 Full-Quality Bitmaps: 3 behind + current + 4 ahead).
            // Evicted full-res pages downgrade seamlessly to their lightweight Preview Bitmap instead of blanking.
            EvictDistantPages(clampedCenter);
            if (isLargeBookPast100)
            {
                _imageCache.EvictPreviewsOutsideWindow(clampedCenter, 20, 30, (index, fullFallback) =>
                {
                    if (index >= 0 && index < Pages.Count && fullFallback is null)
                    {
                        Pages[index].Image = null;
                    }
                });
                _imageCache.EvictCompressedOutsideWindow(clampedCenter, CompressedWindowRadius, CompressedWindowRadius);
            }

            bool centerNeedsFull = !_imageCache.ContainsFull(clampedCenter);
            if (!centerNeedsFull && _imageCache.TryGetFull(clampedCenter, out Bitmap? cachedFull) && cachedFull is not null)
            {
                if (!ReferenceEquals(Pages[clampedCenter].Image, cachedFull))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (generation == Interlocked.Read(ref _windowGeneration) && clampedCenter < Pages.Count)
                        {
                            Pages[clampedCenter].Image = cachedFull;
                        }
                    });
                }
            }
            else if (Pages[clampedCenter].Image is null &&
                     _imageCache.TryGetPreview(clampedCenter, out Bitmap? cachedPreview) &&
                     cachedPreview is not null)
            {
                // Show lightweight preview immediately (0ms, no blank screen) while upgrading to full quality.
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation == Interlocked.Read(ref _windowGeneration) &&
                        clampedCenter < Pages.Count &&
                        Pages[clampedCenter].Image is null)
                    {
                        Pages[clampedCenter].Image = cachedPreview;
                    }
                });
            }

            bool showSpinner = centerNeedsFull && Pages[clampedCenter].Image is null && !_imageCache.ContainsPreview(clampedCenter);
            if (showSpinner)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation == Interlocked.Read(ref _windowGeneration))
                    {
                        LoadingStatusText = $"Đang load trang {clampedCenter + 1}...";
                        IsLoadingPage = true;
                    }
                });
            }

            try
            {
                // Priority fast-lane: always decode the target/bookmark page at 100% Full Original Quality without cutting it off.
                if (centerNeedsFull)
                {
                    await LoadPageAsync(clampedCenter, token, usePreloadThrottle: false);
                }

                if (token.IsCancellationRequested || generation != Interlocked.Read(ref _windowGeneration))
                {
                    return;
                }

                Dispatcher.UIThread.Post(() => IsLoadingPage = false);

                if (!_hasRestoredInitialScroll)
                {
                    _hasRestoredInitialScroll = true;
                    Dispatcher.UIThread.Post(() =>
                        RestoreExactPagePositionRequested?.Invoke(clampedCenter + 1, _currentPageLocalRatio));
                }
                else if (reanchorAfterLoad)
                {
                    // Re-anchor viewport onto the target page (and exact bookmark local ratio if any) after image load/layout
                    // so VirtualizingStackPanel never drifts onto an unloaded neighbor page.
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (generation == Interlocked.Read(ref _windowGeneration))
                        {
                            if (_currentPageLocalRatio > 0.001)
                                RestoreExactPagePositionRequested?.Invoke(clampedCenter + 1, _currentPageLocalRatio);
                            else
                                ScrollToPageRequested?.Invoke(clampedCenter + 1);
                        }
                    }, DispatcherPriority.Loaded);
                }

                // Preload nearby full-res pages: for page 101+ on >200-page books, prioritize 10 pages ahead first.
                int direction = isLargeBookPast100 ? 1 : _scrollDirection;
                var preloadTasks = new List<Task>();
                foreach (int index in BuildWindowOrder(clampedCenter, Pages.Count, direction))
                {
                    if (token.IsCancellationRequested || generation != Interlocked.Read(ref _windowGeneration))
                    {
                        break;
                    }

                    if (!_imageCache.ContainsFull(index) && !_inFlightPages.ContainsKey(index))
                    {
                        preloadTasks.Add(LoadPageAsync(index, token, usePreloadThrottle: true));
                    }
                }

                if (preloadTasks.Count > 0)
                {
                    await Task.WhenAll(preloadTasks);
                }

                // Cache raw compressed bytes for the outer ±8 surrounding pages (offsets 4..8 behind / 5..8 ahead)
                // so scrolling further decodes straight from RAM in ~10-25ms at 100% original quality.
                for (int offset = 1; offset <= CompressedWindowRadius; offset++)
                {
                    if (token.IsCancellationRequested || generation != Interlocked.Read(ref _windowGeneration))
                        break;

                    int aheadIdx = clampedCenter + offset;
                    if (aheadIdx < Pages.Count && !_imageCache.ContainsFull(aheadIdx) && !_imageCache.ContainsCompressed(aheadIdx))
                        _ = PreloadCompressedBytesAsync(aheadIdx, token);

                    int behindIdx = clampedCenter - offset;
                    if (behindIdx >= 0 && !_imageCache.ContainsFull(behindIdx) && !_imageCache.ContainsCompressed(behindIdx))
                        _ = PreloadCompressedBytesAsync(behindIdx, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when user jumps to another page.
            }
            finally
            {
                if (generation == Interlocked.Read(ref _windowGeneration))
                {
                    Dispatcher.UIThread.Post(() => IsLoadingPage = false);
                }
            }
        }

        private async Task LoadPageAsync(int pageIndex, CancellationToken token, bool usePreloadThrottle = true)
        {
            if (token.IsCancellationRequested) return;

            IComicReadSession? session = _readSession;
            if (session is null || pageIndex < 0 || pageIndex >= Pages.Count || _imageCache.ContainsFull(pageIndex)) return;
            if (!_inFlightPages.TryAdd(pageIndex, 0)) return;

            bool acquired = false;
            try
            {
                if (usePreloadThrottle)
                {
                    await _preloadConcurrency.WaitAsync(token);
                    acquired = true;
                }

                if (token.IsCancellationRequested || pageIndex >= Pages.Count || _imageCache.ContainsFull(pageIndex)) return;

                Bitmap? rendered = null;
                if (_imageCache.TryGetCompressed(pageIndex, out byte[]? cachedRawBytes) && cachedRawBytes is not null)
                {
                    rendered = await session.DecodeFromRawBytesAsync(cachedRawBytes, token);
                }

                if (rendered is null)
                {
                    int renderDpi = session.SupportsDynamicZoomDpi
                        ? Math.Clamp((int)Math.Round(150.0 * Math.Max(1.0, ZoomLevel)), AppConstants.DefaultRenderDpi, 450)
                        : AppConstants.DefaultRenderDpi;
                    rendered = await Task.Run(
                        () => session.RenderPageAsync(pageIndex, renderDpi, token),
                        token);
                }

                if (rendered is null) return;

                if (token.IsCancellationRequested)
                {
                    rendered.Dispose();
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested || pageIndex < 0 || pageIndex >= Pages.Count)
                    {
                        rendered.Dispose();
                        return;
                    }

                    // SetFull stores the full-res Bitmap and disposes any previous full-res Bitmap for this index,
                    // while keeping the lightweight preview Bitmap intact in _previewCache for fast downgrade fallback.
                    _imageCache.SetFull(pageIndex, rendered);
                    Pages[pageIndex].Image = rendered;
                });
            }
            catch (OperationCanceledException)
            {
                // Expected when user jumps to a new page.
            }
            finally
            {
                if (acquired)
                {
                    _preloadConcurrency.Release();
                }

                _inFlightPages.TryRemove(pageIndex, out _);
            }
        }

        private void EvictDistantPages(int centerIndex)
        {
            _imageCache.EvictOutsideWindow(centerIndex, (index, previewFallback) =>
            {
                if (index >= 0 && index < Pages.Count)
                {
                    Pages[index].Image = previewFallback;
                }
            });
        }

        private static IEnumerable<int> BuildWindowOrder(int centerIndex, int totalCount, int scrollDirection = 1)
        {
            if (scrollDirection >= 0)
            {
                for (int offset = 1; offset <= PreloadAheadCount; offset++)
                {
                    if (centerIndex + offset < totalCount)
                        yield return centerIndex + offset;
                }
                for (int offset = 1; offset <= PreloadBehindCount; offset++)
                {
                    if (centerIndex - offset >= 0)
                        yield return centerIndex - offset;
                }
            }
            else
            {
                for (int offset = 1; offset <= PreloadBehindCount; offset++)
                {
                    if (centerIndex - offset >= 0)
                        yield return centerIndex - offset;
                }
                for (int offset = 1; offset <= PreloadAheadCount; offset++)
                {
                    if (centerIndex + offset < totalCount)
                        yield return centerIndex + offset;
                }
            }
        }

        private void StartBulkPreviewPreload(int pageCountToPreload, int priorityCenterIndex, Task? waitForWindowTask = null)
        {
            CancelBulkPreload();
            if (_readSession is null || Pages.Count == 0 || pageCountToPreload <= 0) return;

            var cts = new CancellationTokenSource();
            _bulkPreloadCts = cts;
            CancellationToken token = cts.Token;
            int limit = Math.Min(pageCountToPreload, Pages.Count);

            _ = Task.Run(async () =>
            {
                try
                {
                    // Wait until the initial bookmark/entry page + 8 behind + 8 ahead finish at 100% Full Quality.
                    if (waitForWindowTask is not null)
                    {
                        await waitForWindowTask.WaitAsync(token).ConfigureAwait(false);
                    }

                    for (int i = 0; i < limit; i++)
                    {
                        if (token.IsCancellationRequested || _isClosed) break;
                        if (Math.Abs(i - priorityCenterIndex) <= PreloadBehindCount || _imageCache.ContainsFull(i) || _imageCache.ContainsPreview(i))
                            continue;

                        await LoadPreviewPageAsync(i, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancelled when user closes or moves past page 100 on >200-page books.
                }
            }, token);
        }

        private async Task PreloadCompressedBytesAsync(int pageIndex, CancellationToken token)
        {
            if (token.IsCancellationRequested || _isClosed) return;
            IComicReadSession? session = _readSession;
            if (session is null || pageIndex < 0 || pageIndex >= Pages.Count) return;
            if (_imageCache.ContainsCompressed(pageIndex) || !_inFlightCompressed.TryAdd(pageIndex, 0)) return;

            try
            {
                byte[]? raw = await session.ExtractRawBytesAsync(pageIndex, token).ConfigureAwait(false);
                if (raw is not null && !token.IsCancellationRequested && !_isClosed)
                {
                    _imageCache.SetCompressed(pageIndex, raw);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on cancel.
            }
            finally
            {
                _inFlightCompressed.TryRemove(pageIndex, out _);
            }
        }

        private async Task LoadPreviewPageAsync(int pageIndex, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;
            IComicReadSession? session = _readSession;
            if (session is null || pageIndex < 0 || pageIndex >= Pages.Count) return;
            if (_imageCache.ContainsPreview(pageIndex) || !_inFlightPreviews.TryAdd(pageIndex, 0)) return;

            bool acquired = false;
            try
            {
                await _preloadConcurrency.WaitAsync(token).ConfigureAwait(false);
                acquired = true;

                if (token.IsCancellationRequested || _isClosed || _imageCache.ContainsPreview(pageIndex)) return;

                // If ZIP/CBZ supports raw compressed bytes, extract once into Compressed Cache (~0.3-1 MB)
                // and decode the lightweight 240px preview from those bytes so future Full Quality decodes hit RAM directly!
                Bitmap? preview = null;
                if (!_imageCache.TryGetCompressed(pageIndex, out byte[]? rawBytes))
                {
                    rawBytes = await session.ExtractRawBytesAsync(pageIndex, token).ConfigureAwait(false);
                    if (rawBytes is not null && !token.IsCancellationRequested && !_isClosed)
                    {
                        _imageCache.SetCompressed(pageIndex, rawBytes);
                    }
                }

                if (rawBytes is not null)
                {
                    preview = await Task.Run(
                        () => ImageHelper.DecodeFromBytes(rawBytes, AppConstants.PreviewDecodeWidth),
                        token).ConfigureAwait(false);
                }
                else
                {
                    preview = await session.RenderPreviewAsync(pageIndex, AppConstants.PreviewDecodeWidth, token).ConfigureAwait(false);
                }
                if (preview is null) return;

                if (token.IsCancellationRequested || _isClosed)
                {
                    preview.Dispose();
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested || _isClosed || pageIndex < 0 || pageIndex >= Pages.Count)
                    {
                        preview.Dispose();
                        return;
                    }

                    _imageCache.SetPreview(pageIndex, preview);
                    if (!_imageCache.ContainsFull(pageIndex) && Pages[pageIndex].Image is null)
                    {
                        Pages[pageIndex].Image = preview;
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Expected on cancel.
            }
            finally
            {
                if (acquired)
                    _preloadConcurrency.Release();

                _inFlightPreviews.TryRemove(pageIndex, out _);
            }
        }

        private void CancelBulkPreload()
        {
            _bulkPreloadCts?.Cancel();
            _bulkPreloadCts?.Dispose();
            _bulkPreloadCts = null;
            _inFlightPreviews.Clear();
            _inFlightCompressed.Clear();
        }

        private void CancelWindowLoading()
        {
            _zoomRerenderCts?.Cancel();
            _zoomRerenderCts?.Dispose();
            _zoomRerenderCts = null;

            lock (_windowStateLock)
            {
                _loadPageCts?.Cancel();
                _loadPageCts?.Dispose();
                _loadPageCts = null;
                _inFlightPages.Clear();
            }
        }

        private void DisposeReadSession()
        {
            _readSession?.Dispose();
            _readSession = null;
        }

        private async Task ShowBookmarkToastAsync(string message = "Đã lưu dấu trang")
        {
            _toastCts?.Cancel();
            _toastCts = new CancellationTokenSource();
            CancellationToken token = _toastCts.Token;

            Dispatcher.UIThread.Post(() =>
            {
                BookmarkToastMessage = message;
                IsBookmarkToastVisible = true;
            });

            try
            {
                await Task.Delay(2200, token);
                Dispatcher.UIThread.Post(() => IsBookmarkToastVisible = false);
            }
            catch (OperationCanceledException)
            {
                // Reset by a newer bookmark notification.
            }
        }

        private void ScheduleDebouncedSaveProgress()
        {
            CancelDebouncedSave();
            _saveDebounceCts = new CancellationTokenSource();
            CancellationToken token = _saveDebounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, token);
                    if (!token.IsCancellationRequested)
                    {
                        await SaveProgressAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Superseded by newer scroll event or reader close.
                }
            }, token);
        }

        private void CancelDebouncedSave()
        {
            _saveDebounceCts?.Cancel();
            _saveDebounceCts?.Dispose();
            _saveDebounceCts = null;
        }

        private ReadingProgress? CreateProgressSnapshot()
        {
            if (_currentComic is null) return null;

            _currentComic.CurrentPage = CurrentPage;
            _currentComic.TotalPages = TotalPages;
            _currentComic.ZoomLevel = ZoomLevel;
            return new ReadingProgress
            {
                ComicId = _currentComic.Id,
                CurrentChapterId = CurrentChapterId,
                CurrentPage = CurrentPage,
                TotalPages = TotalPages,
                ScrollOffsetRatio = _currentScrollRatio,
                PageLocalOffsetRatio = _currentPageLocalRatio,
                ZoomLevel = ZoomLevel
            };
        }

        private async Task SaveProgressAsync()
        {
            ReadingProgress? progress = CreateProgressSnapshot();
            if (progress is null) return;

            await _storageService.UpdateProgressAsync(progress);
        }
    }
}
