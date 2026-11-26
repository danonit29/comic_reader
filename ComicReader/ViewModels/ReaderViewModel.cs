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
        private const int PreloadBehindCount = 3;
        private const int PreloadAheadCount  = 4;

        private readonly IComicReaderFactory _readerFactory;
        private readonly IStorageService _storageService;
        private readonly IBookmarkService _bookmarkService;
        private readonly ImageCacheManager _imageCache = new(keepBehind: 4, keepAhead: 6);
        private readonly SemaphoreSlim _preloadConcurrency = new(3, 3);
        private readonly ConcurrentDictionary<int, byte> _inFlightPages = new();
        private readonly object _windowStateLock = new();
        private long _windowGeneration;
        private int _activeCenterIndex = -1;
        private CancellationTokenSource? _loadPageCts;
        private CancellationTokenSource? _saveDebounceCts;
        private CancellationTokenSource? _toastCts;
        private ComicBook? _currentComic;
        private IComicReadSession? _readSession;
        private double _currentScrollRatio;
        private double _currentPageLocalRatio;
        private bool _hasRestoredInitialScroll;
        private volatile bool _isClosed;

        [ObservableProperty]
        private string _comicTitle = string.Empty;

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
        private bool _isBookmarkToastVisible;

        [ObservableProperty]
        private string _bookmarkToastMessage = "Đã lưu dấu trang";

        [ObservableProperty]
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
        /// Formatted zoom percentage text (e.g., "100%").
        /// </summary>
        public string ZoomPercentText => $"{ZoomLevel:P0}";

        /// <summary>
        /// Indicates whether the current page has a saved bookmark.
        /// </summary>
        public bool IsCurrentPageBookmarked => Bookmarks.Any(b => b.PageNumber == CurrentPage);

        partial void OnCurrentPageChanged(int value)
        {
            OnPropertyChanged(nameof(PageIndicatorText));
            OnPropertyChanged(nameof(IsCurrentPageBookmarked));
        }

        partial void OnTotalPagesChanged(int value) => OnPropertyChanged(nameof(PageIndicatorText));

        /// <summary>
        /// Opens a reusable read session, restores saved progress, and loads only the visible page window.
        /// </summary>
        public async Task LoadComicAsync(ComicBook comic)
        {
            CancelWindowLoading();
            DisposeReadSession();
            _isClosed = false;
            _hasRestoredInitialScroll = false;

            _currentComic = comic;
            ComicTitle = comic.DisplayName;

            IComicReaderService readerService = _readerFactory.GetReader(comic.Format);
            IComicReadSession openedSession = await readerService.OpenSessionAsync(comic.FilePath);
            if (_isClosed)
            {
                _ = Task.Run(openedSession.Dispose);
                return;
            }

            _readSession = openedSession;
            TotalPages = _readSession.PageCount;

            ReadingProgress? saved = await _storageService.GetProgressAsync(comic.Id);
            IReadOnlyList<Bookmark> savedBookmarks = await _bookmarkService.GetBookmarksAsync(comic.Id);
            if (_isClosed)
            {
                Cleanup();
                return;
            }

            Bookmarks = new ObservableCollection<Bookmark>(savedBookmarks);

            Bookmark? latestBookmark = savedBookmarks.OrderByDescending(b => b.CreatedAtUtc).FirstOrDefault();

            // Always prioritize the latest bookmark first; otherwise fall back to last reading progress / page 1.
            CurrentPage = latestBookmark is not null
                ? Math.Clamp(latestBookmark.PageNumber, 1, Math.Max(1, TotalPages))
                : (saved is not null
                    ? Math.Clamp(saved.CurrentPage, 1, Math.Max(1, TotalPages))
                    : Math.Clamp(comic.CurrentPage, 1, Math.Max(1, TotalPages)));

            // Always open at 100% (fit-to-width like standard PDF readers); user can zoom freely afterward.
            ZoomLevel = AppConstants.DefaultZoomLevel;

            _currentScrollRatio = saved?.ScrollOffsetRatio ?? 0.0;
            _currentPageLocalRatio = latestBookmark is not null
                ? latestBookmark.PageLocalOffsetRatio
                : (saved?.PageLocalOffsetRatio ?? 0.0);

            OnPropertyChanged(nameof(IsCurrentPageBookmarked));
            InitializePagePlaceholders();

            _ = EnsurePageWindowLoadedAsync(CurrentPage - 1);
        }

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
        /// Navigates to a saved bookmark's exact page and intra-page scroll position.
        /// </summary>
        [RelayCommand]
        private async Task NavigateToBookmarkAsync(Bookmark? bookmark)
        {
            if (bookmark is null || bookmark.PageNumber < 1 || bookmark.PageNumber > TotalPages) return;

            CancelWindowLoading();
            CurrentPage = bookmark.PageNumber;
            _currentPageLocalRatio = bookmark.PageLocalOffsetRatio;
            _hasRestoredInitialScroll = true;

            if (bookmark.PageLocalOffsetRatio > 0.001)
            {
                RestoreExactPagePositionRequested?.Invoke(bookmark.PageNumber, bookmark.PageLocalOffsetRatio);
            }
            else
            {
                ScrollToPageRequested?.Invoke(bookmark.PageNumber);
            }

            _ = EnsurePageWindowLoadedAsync(bookmark.PageNumber - 1, cancelPrevious: true);
            await SaveProgressAsync();
        }

        /// <summary>
        /// Navigates immediately to the given 1-based page number (e.g., after slider drag/click release).
        /// Cancels any previous in-flight page loading so the target page renders immediately.
        /// </summary>
        public void NavigateToPage(int pageNumber)
        {
            _ = GoToPageAsync(pageNumber);
        }

        /// <summary>
        /// Navigates to a specific 1-based page number, cancels stale loads, loads its window, and persists progress.
        /// </summary>
        [RelayCommand]
        private async Task GoToPageAsync(int pageNumber)
        {
            if (pageNumber < 1 || pageNumber > TotalPages) return;

            CancelWindowLoading();

            Bookmark? bookmark = Bookmarks.FirstOrDefault(b => b.PageNumber == pageNumber);
            double targetLocalRatio = bookmark?.PageLocalOffsetRatio ?? 0.0;

            CurrentPage = pageNumber;
            _currentPageLocalRatio = targetLocalRatio;
            _hasRestoredInitialScroll = true;

            if (targetLocalRatio > 0.001)
            {
                RestoreExactPagePositionRequested?.Invoke(pageNumber, targetLocalRatio);
            }
            else
            {
                ScrollToPageRequested?.Invoke(pageNumber);
            }

            _ = EnsurePageWindowLoadedAsync(pageNumber - 1, cancelPrevious: true);
            await SaveProgressAsync();
        }

        /// <summary>
        /// Increases the zoom factor up to the configured maximum.
        /// </summary>
        [RelayCommand]
        private void ZoomIn()
        {
            ApplyZoom(Math.Min(AppConstants.MaxZoomLevel, ZoomLevel + AppConstants.ZoomStep));
        }

        /// <summary>
        /// Decreases the zoom factor down to the configured minimum.
        /// </summary>
        [RelayCommand]
        private void ZoomOut()
        {
            ApplyZoom(Math.Max(AppConstants.MinZoomLevel, ZoomLevel - AppConstants.ZoomStep));
        }

        /// <summary>
        /// Resets zoom back to 100%.
        /// </summary>
        [RelayCommand]
        private void ResetZoom() => ApplyZoom(AppConstants.DefaultZoomLevel);

        private async void ApplyZoom(double newZoom)
        {
            double oldZoom = ZoomLevel;
            if (Math.Abs(newZoom - oldZoom) < 0.001) return;

            ZoomChanging?.Invoke(oldZoom, newZoom);
            ZoomLevel = newZoom;
            await SaveProgressAsync();
        }

        /// <summary>
        /// Saves a bookmark for the exact ComicId, ChapterId, and PageNumber, then updates the page badge.
        /// </summary>
        [RelayCommand]
        private async Task AddBookmarkAsync()
        {
            if (_currentComic is null) return;

            var bookmark = new Bookmark
            {
                ComicId = _currentComic.Id,
                ChapterId = _currentComic.ParentFolderPath ?? _currentComic.Id,
                PageNumber = CurrentPage,
                PageLocalOffsetRatio = _currentPageLocalRatio,
                Note = $"Page {CurrentPage}"
            };

            await _bookmarkService.SaveBookmarkAsync(bookmark);

            Bookmark? existing = Bookmarks.FirstOrDefault(b => b.PageNumber == CurrentPage);
            if (existing is not null)
            {
                Bookmarks.Remove(existing);
            }

            Bookmarks.Add(bookmark);
            if (CurrentPage >= 1 && CurrentPage <= Pages.Count)
            {
                Pages[CurrentPage - 1].IsBookmarked = true;
            }

            OnPropertyChanged(nameof(IsCurrentPageBookmarked));
            CancelDebouncedSave();
            await SaveProgressAsync();
            await ShowBookmarkToastAsync();
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
                _ = Task.Run(sessionToDispose.Dispose);
            }
        }

        private void InitializePagePlaceholders()
        {
            var items = new ObservableCollection<ComicPageItem>();
            for (int i = 1; i <= TotalPages; i++)
            {
                items.Add(new ComicPageItem
                {
                    PageNumber = i,
                    IsBookmarked = Bookmarks.Any(b => b.PageNumber == i)
                });
            }

            Pages = items;
        }

        private async Task EnsurePageWindowLoadedAsync(int centerIndex, bool cancelPrevious = false)
        {
            if (_readSession is null || Pages.Count == 0) return;

            int clampedCenter = Math.Clamp(centerIndex, 0, Pages.Count - 1);

            long generation;
            CancellationToken token;

            lock (_windowStateLock)
            {
                if (cancelPrevious || (_activeCenterIndex >= 0 && Math.Abs(clampedCenter - _activeCenterIndex) > 3))
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

            EvictDistantPages(clampedCenter);

            bool centerNeedsLoad = Pages[clampedCenter].Image is null;
            if (centerNeedsLoad)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation == Interlocked.Read(ref _windowGeneration))
                    {
                        IsLoadingPage = true;
                    }
                });
            }

            try
            {
                // Priority fast-lane: decode the target page immediately without waiting on background preload slots.
                if (centerNeedsLoad)
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

                // Preload nearby pages in background only after target page is ready.
                var preloadTasks = new List<Task>();
                foreach (int index in BuildWindowOrder(clampedCenter, Pages.Count))
                {
                    if (token.IsCancellationRequested || generation != Interlocked.Read(ref _windowGeneration))
                    {
                        break;
                    }

                    if (Pages[index].Image is null && !_inFlightPages.ContainsKey(index))
                    {
                        preloadTasks.Add(LoadPageAsync(index, token, usePreloadThrottle: true));
                    }
                }

                if (preloadTasks.Count > 0)
                {
                    await Task.WhenAll(preloadTasks);
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
            if (session is null || pageIndex < 0 || pageIndex >= Pages.Count || Pages[pageIndex].Image is not null) return;
            if (!_inFlightPages.TryAdd(pageIndex, 0)) return;

            bool acquired = false;
            try
            {
                if (usePreloadThrottle)
                {
                    await _preloadConcurrency.WaitAsync(token);
                    acquired = true;
                }

                if (token.IsCancellationRequested || pageIndex >= Pages.Count || Pages[pageIndex].Image is not null) return;

                Bitmap? rendered = await Task.Run(
                    () => session.RenderPageAsync(pageIndex, AppConstants.DefaultRenderDpi, token),
                    token);

                if (rendered is null) return;

                if (token.IsCancellationRequested)
                {
                    rendered.Dispose();
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested)
                    {
                        rendered.Dispose();
                        return;
                    }

                    if (pageIndex < 0 || pageIndex >= Pages.Count)
                    {
                        rendered.Dispose();
                        return;
                    }

                    Bitmap? oldBitmap = Pages[pageIndex].Image;
                    if (ReferenceEquals(oldBitmap, rendered))
                    {
                        return;
                    }

                    _imageCache.Set(pageIndex, rendered);
                    Pages[pageIndex].Image = rendered;
                    oldBitmap?.Dispose();
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
            _imageCache.EvictOutsideWindow(centerIndex, index =>
            {
                if (index >= 0 && index < Pages.Count)
                {
                    Pages[index].Image = null;
                }
            });
        }

        private static IEnumerable<int> BuildWindowOrder(int centerIndex, int totalCount)
        {
            int maxOffset = Math.Max(PreloadAheadCount, PreloadBehindCount);
            for (int offset = 1; offset <= maxOffset; offset++)
            {
                if (offset <= PreloadAheadCount && centerIndex + offset < totalCount)
                {
                    yield return centerIndex + offset;
                }

                if (offset <= PreloadBehindCount && centerIndex - offset >= 0)
                {
                    yield return centerIndex - offset;
                }
            }
        }

        private void CancelWindowLoading()
        {
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

        private async Task ShowBookmarkToastAsync()
        {
            _toastCts?.Cancel();
            _toastCts = new CancellationTokenSource();
            CancellationToken token = _toastCts.Token;

            Dispatcher.UIThread.Post(() =>
            {
                BookmarkToastMessage = "Đã lưu dấu trang";
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
