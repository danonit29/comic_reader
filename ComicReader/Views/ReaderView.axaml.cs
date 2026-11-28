using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ComicReader.Reader;
using ComicReader.ViewModels;

namespace ComicReader.Views
{
    /// <summary>
    /// Code-behind for ReaderView: handles scroll tracking, pan-drag, middle-mouse
    /// auto-scroll, focal-point zoom preservation, and slider debounce navigation.
    /// </summary>
    public partial class ReaderView : UserControl
    {
        // ── constants ──────────────────────────────────────────────────────────
        private const double AutoScrollDeadZone       = 8.0;
        private const double AutoScrollSpeedFactor    = 0.28;
        private const int    SliderDebounceMs         = 80;
        private const int    ScrollThrottleMs         = 80;
        private const double CenterClickMoveThreshold = 8.0;
        private const int    CenterClickMaxDurationMs = 400;
        private const double ToolbarTopClearancePx    = 54.0;
        private const int    DoubleTapMaxDelayMs      = 240;
        private const double DoubleTapMaxDistancePx   = 32.0;

        // ── private state ──────────────────────────────────────────────────────
        private ScrollViewer?      _scrollViewer;
        private DispatcherTimer?   _autoScrollTimer;
        private DispatcherTimer?   _sliderDebounceTimer;
        private DispatcherTimer?   _scrollDebounceTimer;
        private DispatcherTimer?   _singleTapToolbarTimer;
        private DateTime           _lastScrollProcessUtc = DateTime.MinValue;
        private DateTime           _suppressScrollUntilUtc = DateTime.MinValue;
        private Point              _lastDragPoint;
        private Point?             _pendingZoomAnchor;
        private Point              _autoScrollOrigin;
        private Point              _autoScrollCurrent;
        private Point              _pointerPressOrigin;
        private DateTime           _pointerPressUtc;
        private DateTime           _lastTapUtc = DateTime.MinValue;
        private Point              _lastTapOrigin;
        private bool               _isPotentialCenterClick;
        private bool               _isDragging;
        private bool               _isAutoScrolling;
        private bool               _isPinching;
        private double             _currentScale = 1.0;
        private double             _currentTranslateX = 0.0;
        private double             _currentTranslateY = 0.0;
        private double             _pinchStartScale = 1.0;
        private double             _pinchStartTx = 0.0;
        private double             _pinchStartTy = 0.0;
        private Point              _pinchContentPoint;
        private ScaleTransform?    _pinchScaleTransform;
        private TranslateTransform? _pinchTranslateTransform;
        private bool               _isSuppressingScrollTracking;
        private bool               _isResizeRestorePending;
        // Tracks whether the slider thumb is currently being dragged by the user.
        // Reset to false only after the debounce timer fires, ensuring no premature navigation.
        private bool               _isDraggingSlider;
        private ReaderViewModel?   _subscribedViewModel;
        private WindowState        _windowStateBeforeFullScreen = WindowState.Normal;
        private Window?            _hostWindow;

        public static readonly StyledProperty<bool> IsFullScreenProperty =
            AvaloniaProperty.Register<ReaderView, bool>(nameof(IsFullScreen), false);

        public bool IsFullScreen
        {
            get => GetValue(IsFullScreenProperty);
            set => SetValue(IsFullScreenProperty, value);
        }

        // ── styled properties exposed to DataTemplates ─────────────────────────
        /// <summary>Zoom scale for page images, bound via RelativeSource to ScaleTransform.</summary>
        public static readonly StyledProperty<double> ZoomLevelProperty =
            AvaloniaProperty.Register<ReaderView, double>(nameof(ZoomLevel), 1.0);

        public double ZoomLevel
        {
            get => GetValue(ZoomLevelProperty);
            set => SetValue(ZoomLevelProperty, value);
        }

        /// <summary>Max pixel width of each page image at 100% zoom.</summary>
        public static readonly StyledProperty<double> BasePageWidthProperty =
            AvaloniaProperty.Register<ReaderView, double>(nameof(BasePageWidth), 900.0);

        public double BasePageWidth
        {
            get => GetValue(BasePageWidthProperty);
            set => SetValue(BasePageWidthProperty, value);
        }

        public static readonly StyledProperty<bool> IsCompactLayoutProperty =
            AvaloniaProperty.Register<ReaderView, bool>(nameof(IsCompactLayout), false);

        public bool IsCompactLayout
        {
            get => GetValue(IsCompactLayoutProperty);
            set => SetValue(IsCompactLayoutProperty, value);
        }

        public static readonly StyledProperty<Thickness> SafeAreaPaddingProperty =
            AvaloniaProperty.Register<ReaderView, Thickness>(nameof(SafeAreaPadding), default);

        public Thickness SafeAreaPadding
        {
            get => GetValue(SafeAreaPaddingProperty);
            set => SetValue(SafeAreaPaddingProperty, value);
        }

        /// <summary>Strongly-typed ViewModel for use in DataTemplate CompiledBindings.</summary>
        public ReaderViewModel? ViewModel => DataContext as ReaderViewModel;

        /// <summary>True when running on Android so mobile-only layout &amp; touch gestures apply.</summary>
        public bool IsAndroidPlatform { get; } = OperatingSystem.IsAndroid();

        /// <summary>Zero gap between pages on Android; subtle 6px margin on Desktop.</summary>
        public Thickness PageItemMargin { get; } = OperatingSystem.IsAndroid()
            ? new Thickness(0)
            : new Thickness(0, 6, 0, 6);

        /// <summary>Responsive bookmark drawer width (280 on Android phones, 320 on Desktop).</summary>
        public double BookmarkDrawerWidth { get; } = OperatingSystem.IsAndroid() ? 280.0 : 320.0;

        // ── constructor ────────────────────────────────────────────────────────
        public ReaderView()
        {
            InitializeComponent();

            DataContextChanged += OnDataContextChanged;
            Loaded             += OnLoaded;
            Unloaded           += OnUnloaded;
            KeyDown            += OnReaderKeyDown;
            SizeChanged        += (_, e) =>
            {
                bool compact = e.NewSize.Width > 0 && e.NewSize.Width < 760;
                if (IsCompactLayout != compact)
                    IsCompactLayout = compact;
            };

            // Attach scroll-viewer after template is applied.
            PagesListBox.TemplateApplied += (_, _) => AttachScrollViewer();
            PagesListBox.SizeChanged     += OnPagesListBoxSizeChanged;

            // Tunnel handlers on PagesListBox for pan-drag and wheel zoom.
            // We handle these before Avalonia's internal ScrollViewer so we can consume them.
            PagesListBox.AddHandler(PointerPressedEvent,     OnScrollAreaPointerPressed,     RoutingStrategies.Tunnel);
            PagesListBox.AddHandler(PointerMovedEvent,       OnScrollAreaPointerMoved,       RoutingStrategies.Tunnel);
            PagesListBox.AddHandler(PointerReleasedEvent,    OnScrollAreaPointerReleased,    RoutingStrategies.Tunnel);
            PagesListBox.AddHandler(PointerCaptureLostEvent, OnScrollAreaPointerCaptureLost, RoutingStrategies.Tunnel);
            PagesListBox.AddHandler(PointerWheelChangedEvent, OnScrollAreaPointerWheelChanged, RoutingStrategies.Tunnel);

            // Slider handlers:
            //  • PointerPressed  (Tunnel, handledEventsToo=true): set _isDraggingSlider before ListBox sees it.
            //  • DragStarted     (Bubble, handledEventsToo=true): confirm drag started from Thumb.
            //  • DragCompleted   (Bubble, handledEventsToo=true): thumb release → start debounce.
            //  • PointerReleased (Bubble+Tunnel, handledEventsToo=true): track-click release (DragCompleted not raised).
            //  • PointerCaptureLost (Bubble, handledEventsToo=true): fallback if capture lost mid-drag.
            PageSlider.AddHandler(PointerPressedEvent,     OnPageSliderPointerPressed,     RoutingStrategies.Tunnel, handledEventsToo: true);
            PageSlider.AddHandler(PointerReleasedEvent,    OnPageSliderPointerReleased,    RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);
            PageSlider.AddHandler(PointerCaptureLostEvent, OnPageSliderPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
            PageSlider.AddHandler(Thumb.DragStartedEvent,  OnPageSliderDragStarted,  RoutingStrategies.Bubble, handledEventsToo: true);
            PageSlider.AddHandler(Thumb.DragCompletedEvent, OnPageSliderDragCompleted, RoutingStrategies.Bubble, handledEventsToo: true);
            PageSlider.PropertyChanged += OnPageSliderPropertyChanged;

            _sliderDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SliderDebounceMs) };
            _sliderDebounceTimer.Tick += OnSliderDebounceTick;

            _scrollDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ScrollThrottleMs) };
            _scrollDebounceTimer.Tick += OnScrollDebounceTick;

            if (IsAndroidPlatform)
            {
                _singleTapToolbarTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DoubleTapMaxDelayMs) };
                _singleTapToolbarTimer.Tick += OnSingleTapToolbarTimerTick;

                _pinchScaleTransform = new ScaleTransform(1.0, 1.0);
                _pinchTranslateTransform = new TranslateTransform(0.0, 0.0);
                PagesListBox.RenderTransformOrigin = RelativePoint.TopLeft;
                PagesListBox.RenderTransform = new TransformGroup
                {
                    Children = { _pinchScaleTransform, _pinchTranslateTransform }
                };
            }
        }

        // ── lifecycle ──────────────────────────────────────────────────────────
        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            AttachScrollViewer();
            IsCompactLayout = Bounds.Width > 0 && Bounds.Width < 760;
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (IsAndroidPlatform)
            {
                SafeAreaPadding = App.AndroidSafeAreaPadding;
                App.AndroidSafeAreaChanged -= OnAndroidSafeAreaChanged;
                App.AndroidSafeAreaChanged += OnAndroidSafeAreaChanged;

                App.AndroidPinchStarted -= OnAndroidPinchStarted;
                App.AndroidPinchStarted += OnAndroidPinchStarted;
                App.AndroidPinchUpdated -= OnAndroidPinchUpdated;
                App.AndroidPinchUpdated += OnAndroidPinchUpdated;
                App.AndroidPinchEnded -= OnAndroidPinchEnded;
                App.AndroidPinchEnded += OnAndroidPinchEnded;
            }
            else if (topLevel?.InsetsManager is { } insets)
            {
                SafeAreaPadding = insets.SafeAreaPadding;
                insets.SafeAreaChanged += (_, args) => SafeAreaPadding = args.SafeAreaPadding;
            }

            if (topLevel is Window window && !ReferenceEquals(_hostWindow, window))
            {
                if (_hostWindow is not null)
                {
                    _hostWindow.PropertyChanged -= OnHostWindowPropertyChanged;
                }
                _hostWindow = window;
                _hostWindow.PropertyChanged += OnHostWindowPropertyChanged;
                IsFullScreen = window.WindowState == WindowState.FullScreen;
            }
        }

        private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Window.WindowStateProperty && _hostWindow is not null)
            {
                IsFullScreen = _hostWindow.WindowState == WindowState.FullScreen;
            }
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (_subscribedViewModel is not null)
            {
                _subscribedViewModel.ScrollToPageRequested              -= OnScrollToPageRequested;
                _subscribedViewModel.RestoreExactPagePositionRequested  -= OnRestoreExactPagePositionRequested;
                _subscribedViewModel.ZoomChanging                       -= OnZoomChanging;
                _subscribedViewModel.FitWidthRequested                  -= OnFitWidthRequested;
                _subscribedViewModel.FitPageRequested                   -= OnFitPageRequested;
                _subscribedViewModel.PropertyChanged                    -= OnViewModelPropertyChanged;
            }

            _subscribedViewModel = DataContext as ReaderViewModel;
            _sliderDebounceTimer?.Stop();
            _scrollDebounceTimer?.Stop();
            _singleTapToolbarTimer?.Stop();
            StopAutoScroll();
            ResetPinchVisualTransform();
            _isPinching             = false;
            _isDragging             = false;
            _isPotentialCenterClick = false;
            _isDraggingSlider       = false;
            _isSuppressingScrollTracking = false;

            if (_scrollViewer is not null)
                _scrollViewer.Offset = default;

            if (_subscribedViewModel is not null)
            {
                ZoomLevel = _subscribedViewModel.ZoomLevel;
                _subscribedViewModel.ScrollToPageRequested             += OnScrollToPageRequested;
                _subscribedViewModel.RestoreExactPagePositionRequested += OnRestoreExactPagePositionRequested;
                _subscribedViewModel.ZoomChanging                      += OnZoomChanging;
                _subscribedViewModel.FitWidthRequested                 += OnFitWidthRequested;
                _subscribedViewModel.FitPageRequested                  += OnFitPageRequested;
                _subscribedViewModel.PropertyChanged                   += OnViewModelPropertyChanged;
            }
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ReaderViewModel.ZoomLevel) && _subscribedViewModel is not null)
            {
                ZoomLevel = _subscribedViewModel.ZoomLevel;
                _scrollViewer?.InvalidateMeasure();
            }
        }

        private void OnReaderKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && IsFullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }

            if (_subscribedViewModel is null || _subscribedViewModel.TotalPages <= 0) return;

            switch (e.Key)
            {
                case Key.Left:
                case Key.PageUp:
                    _subscribedViewModel.PreviousPageCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Right:
                case Key.PageDown:
                    _subscribedViewModel.NextPageCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Home:
                    _subscribedViewModel.JumpToPage(1);
                    e.Handled = true;
                    break;
                case Key.End:
                    _subscribedViewModel.JumpToPage(_subscribedViewModel.TotalPages);
                    e.Handled = true;
                    break;
            }
        }

        private void OnUnloaded(object? sender, RoutedEventArgs e)
        {
            if (_hostWindow is not null)
            {
                _hostWindow.PropertyChanged -= OnHostWindowPropertyChanged;
                if (_hostWindow.WindowState == WindowState.FullScreen)
                {
                    _hostWindow.WindowState = _windowStateBeforeFullScreen == WindowState.FullScreen
                        ? WindowState.Normal
                        : _windowStateBeforeFullScreen;
                }
                _hostWindow = null;
            }
            if (IsAndroidPlatform)
            {
                App.AndroidSafeAreaChanged -= OnAndroidSafeAreaChanged;
                App.AndroidPinchStarted -= OnAndroidPinchStarted;
                App.AndroidPinchUpdated -= OnAndroidPinchUpdated;
                App.AndroidPinchEnded -= OnAndroidPinchEnded;
            }
            IsFullScreen = false;
            _isPotentialCenterClick = false;
            ResetPinchVisualTransform();
            _isPinching = false;
            _singleTapToolbarTimer?.Stop();
            _sliderDebounceTimer?.Stop();
            _scrollDebounceTimer?.Stop();
            StopAutoScroll();
            DetachScrollViewer();
        }

        private void OnToggleFullScreenClick(object? sender, RoutedEventArgs e) => ToggleFullScreen();

        private void ToggleFullScreen()
        {
            if (TopLevel.GetTopLevel(this) is not Window window) return;

            if (window.WindowState == WindowState.FullScreen)
            {
                window.WindowState = _windowStateBeforeFullScreen == WindowState.FullScreen
                    ? WindowState.Normal
                    : _windowStateBeforeFullScreen;
                IsFullScreen = false;
            }
            else
            {
                _windowStateBeforeFullScreen = window.WindowState;
                window.WindowState = WindowState.FullScreen;
                IsFullScreen = true;
            }
        }

        // ── scroll-viewer attachment ───────────────────────────────────────────
        private ScrollViewer? AttachScrollViewer()
        {
            ScrollViewer? found = PagesListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (ReferenceEquals(found, _scrollViewer)) return _scrollViewer;

            DetachScrollViewer();
            _scrollViewer = found;
            if (_scrollViewer is not null)
                _scrollViewer.ScrollChanged += OnScrollViewerScrollChanged;

            UpdateBasePageWidth();
            return _scrollViewer;
        }

        private void DetachScrollViewer()
        {
            if (_scrollViewer is not null)
            {
                _scrollViewer.ScrollChanged -= OnScrollViewerScrollChanged;
                _scrollViewer = null;
            }
        }

        // ── page-width calculation & window resize preservation ─────────────────
        private void OnPagesListBoxSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            bool compact = Bounds.Width > 0 && Bounds.Width < 760;
            if (IsCompactLayout != compact)
                IsCompactLayout = compact;

            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 1.0 &&
                Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 1.0)
            {
                return;
            }

            ScrollViewer? sv = AttachScrollViewer();
            (int savedPage, double savedLocalRatio) = (_subscribedViewModel is not null && sv is not null)
                ? DetectCurrentPage(sv)
                : (Math.Max(1, _subscribedViewModel?.CurrentPage ?? 1), 0.0);

            // Update BasePageWidth synchronously during SizeChanged so layout settles in one pass.
            UpdateBasePageWidth();

            if (_isResizeRestorePending || _subscribedViewModel is null || _subscribedViewModel.TotalPages <= 0)
            {
                return;
            }

            _isResizeRestorePending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _isResizeRestorePending = false;
                if (_subscribedViewModel is null || _subscribedViewModel.TotalPages <= 0) return;

                if (_subscribedViewModel.IsFitPage)
                    OnFitPageRequested();

                if (savedPage >= 1 && savedPage <= _subscribedViewModel.TotalPages)
                {
                    OnRestoreExactPagePositionRequested(savedPage, savedLocalRatio);
                }
            }, DispatcherPriority.Loaded);
        }

        private void OnFitWidthRequested()
        {
            if (IsAndroidPlatform && _currentScale > 1.05)
            {
                ResetPinchVisualTransform();
            }
            if (_subscribedViewModel is null) return;
            _subscribedViewModel.SetZoomFromView(Helpers.AppConstants.DefaultZoomLevel, preserveFocal: true);
        }

        private void OnFitPageRequested()
        {
            if (IsAndroidPlatform && _currentScale > 1.05)
            {
                ResetPinchVisualTransform();
            }
            if (_subscribedViewModel is null) return;
            ScrollViewer? sv = AttachScrollViewer();
            if (sv is null || sv.Viewport.Height <= 0 || BasePageWidth <= 0) return;

            int idx = Math.Clamp(_subscribedViewModel.CurrentPage - 1, 0, Math.Max(0, _subscribedViewModel.Pages.Count - 1));
            double pageHeight = idx < _subscribedViewModel.Pages.Count
                ? _subscribedViewModel.Pages[idx].PlaceholderHeight
                : 900.0;
            if (pageHeight <= 0) pageHeight = 900.0;

            double fitZoom = Math.Clamp((sv.Viewport.Height - 24.0) / pageHeight, Helpers.AppConstants.MinZoomLevel, Helpers.AppConstants.MaxZoomLevel);
            _subscribedViewModel.SetZoomFromView(fitZoom, preserveFocal: true);
        }

        private void UpdateBasePageWidth()
        {
            double width = PagesListBox.Bounds.Width;
            if (width <= 0) return;
            double sidePadding = IsAndroidPlatform ? 0.0 : 32.0;
            double minWidth = IsAndroidPlatform ? 200.0 : 320.0;
            double target = Math.Max(minWidth, width - sidePadding);
            if (Math.Abs(BasePageWidth - target) >= 1.0)
            {
                BasePageWidth = target;
            }
        }

        // ── zoom focal-point preservation ──────────────────────────────────────
        private void OnZoomChanging(double oldZoom, double newZoom)
        {
            ScrollViewer? sv = AttachScrollViewer();
            if (sv is null || oldZoom <= 0 || newZoom <= 0) return;

            Point anchor = _pendingZoomAnchor ?? new Point(sv.Viewport.Width * 0.5, sv.Viewport.Height * 0.5);
            _pendingZoomAnchor = null;

            (int anchorPageIndex, double ratioInPage) = ScrollAnchor.FindZoomAnchor(
                PagesListBox.GetRealizedContainers(),
                sv,
                anchor.Y,
                _subscribedViewModel?.TotalPages ?? 0,
                _subscribedViewModel?.CurrentPage ?? 1);

            double contentX = (sv.Offset.X + anchor.X) / oldZoom;

            _isSuppressingScrollTracking = true;
            sv.InvalidateMeasure();
            Dispatcher.UIThread.Post(() =>
            {
                sv.InvalidateMeasure();

                double targetX = Math.Max(0, contentX * newZoom - anchor.X);
                double targetY = sv.Offset.Y;

                if (anchorPageIndex >= 0 && _subscribedViewModel is not null && anchorPageIndex < _subscribedViewModel.Pages.Count)
                {
                    Control? container = PagesListBox.ContainerFromIndex(anchorPageIndex);
                    if (container is null)
                    {
                        PagesListBox.ScrollIntoView(_subscribedViewModel.Pages[anchorPageIndex]);
                        container = PagesListBox.ContainerFromIndex(anchorPageIndex);
                    }

                    if (container is not null)
                    {
                        Point? newTl = container.TranslatePoint(new Point(0, 0), sv);
                        if (newTl.HasValue)
                        {
                            double newSpotY = newTl.Value.Y + ratioInPage * container.Bounds.Height;
                            targetY = Math.Max(0, sv.Offset.Y + (newSpotY - anchor.Y));
                        }
                    }
                }

                sv.Offset = new Vector(targetX, targetY);
                ReleaseScrollSuppression();
            }, DispatcherPriority.Loaded);
        }

        // ── scroll-to-page ─────────────────────────────────────────────────────
        private void OnScrollToPageRequested(int pageNumber)
            => ScrollToPage(pageNumber - 1);

        /// <summary>Scrolls the reader to the specified 0-based page index.</summary>
        public void ScrollToPage(int pageIndex)
        {
            if (_subscribedViewModel is null || pageIndex < 0 || pageIndex >= _subscribedViewModel.Pages.Count) return;

            if (IsAndroidPlatform && _currentScale > 1.05)
            {
                ResetPinchVisualTransform();
            }

            _isSuppressingScrollTracking = true;
            _scrollDebounceTimer?.Stop();

            var item = _subscribedViewModel.Pages[pageIndex];
            PagesListBox.ScrollIntoView(item);

            Dispatcher.UIThread.Post(() =>
            {
                ScrollViewer? sv = AttachScrollViewer();
                if (sv is not null)
                {
                    Control? container = PagesListBox.ContainerFromIndex(pageIndex);
                    if (container is null)
                    {
                        PagesListBox.ScrollIntoView(item);
                        container = PagesListBox.ContainerFromIndex(pageIndex);
                    }
                    if (container is not null)
                    {
                        double topClearance = (_subscribedViewModel?.AreToolbarsVisible ?? true) ? ToolbarTopClearancePx : 0.0;
                        Point? pt = container.TranslatePoint(new Point(0, 0), sv);
                        if (pt.HasValue && Math.Abs(pt.Value.Y - topClearance) > 2.0)
                            sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y + pt.Value.Y - topClearance));
                    }
                    else
                    {
                        // Retry one frame later if container was still virtualized during a far jump.
                        Dispatcher.UIThread.Post(() =>
                        {
                            PagesListBox.ScrollIntoView(item);
                            Control? c2 = PagesListBox.ContainerFromIndex(pageIndex);
                            if (c2 is not null && AttachScrollViewer() is { } sv2)
                            {
                                double topClearance = (_subscribedViewModel?.AreToolbarsVisible ?? true) ? ToolbarTopClearancePx : 0.0;
                                Point? pt2 = c2.TranslatePoint(new Point(0, 0), sv2);
                                if (pt2.HasValue)
                                    sv2.Offset = new Vector(sv2.Offset.X, Math.Max(0, sv2.Offset.Y + pt2.Value.Y - topClearance));
                            }
                        }, DispatcherPriority.Background);
                    }
                }
                ReleaseScrollSuppression();
            }, DispatcherPriority.Loaded);
        }

        private void OnRestoreExactPagePositionRequested(int pageNumber, double pageLocalRatio)
        {
            if (_subscribedViewModel is null || pageNumber < 1 || pageNumber > _subscribedViewModel.Pages.Count) return;

            _isSuppressingScrollTracking = true;
            _scrollDebounceTimer?.Stop();

            PagesListBox.ScrollIntoView(_subscribedViewModel.Pages[pageNumber - 1]);

            Dispatcher.UIThread.Post(() =>
            {
                ScrollViewer? sv = AttachScrollViewer();
                if (sv is not null)
                {
                    Control? container = PagesListBox.ContainerFromIndex(pageNumber - 1);
                    if (container is not null)
                    {
                        double topClearance = (_subscribedViewModel?.AreToolbarsVisible ?? true) ? ToolbarTopClearancePx : 0.0;
                        Point? pt = container.TranslatePoint(new Point(0, 0), sv);
                        if (pt.HasValue)
                        {
                            double localOffset = Math.Clamp(pageLocalRatio, 0.0, 1.0) * container.Bounds.Height;
                            sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y + pt.Value.Y + localOffset - topClearance));
                        }
                    }
                }
                ReleaseScrollSuppression();
            }, DispatcherPriority.Loaded);
        }

        private void ReleaseScrollSuppression(bool extendWindow = true)
        {
            if (extendWindow)
                _suppressScrollUntilUtc = DateTime.UtcNow.AddMilliseconds(200);
            Dispatcher.UIThread.Post(() => _isSuppressingScrollTracking = false, DispatcherPriority.Background);
        }

        // ── container size change (keeps placeholder height stable) ───────────
        private void OnPageContainerSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (sender is not Control element) return;
            if (element.DataContext is not Models.ComicPageItem pageItem) return;

            if (pageItem.Image is not null && e.NewSize.Height > 10.0)
            {
                if (IsAndroidPlatform)
                {
                    pageItem.PlaceholderHeight = e.NewSize.Height;
                }
                else if (e.NewSize.Height > pageItem.PlaceholderHeight)
                {
                    pageItem.PlaceholderHeight = e.NewSize.Height;
                }
            }

            // Ignore initial realization/measure (PreviousSize == 0) to prevent runaway scroll-to-bottom cascades.
            if (_isSuppressingScrollTracking || e.PreviousSize.Height < 1.0 || pageItem.Image is null) return;

            double delta = e.NewSize.Height - e.PreviousSize.Height;
            if (delta < 0.5) return;

            ScrollViewer? sv = AttachScrollViewer();
            if (sv is null) return;

            Point? topInViewport = element.TranslatePoint(new Point(0, 0), sv);
            if (topInViewport is { Y: < 0 })
            {
                sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y + delta * ZoomLevel));
            }
        }

        // ── pointer events on scroll area (pan-drag + middle-auto-scroll) ──────
        private void OnScrollAreaPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // Don't interfere if the event originates from the slider or its children.
            if (IsDescendantOf(e.Source as Visual, PageSlider)) return;

            ScrollViewer? sv = AttachScrollViewer();
            if (sv is null) return;

            // When zoomed in on Android, single-finger drag pans the zoomed view smoothly like photo gallery
            if (IsAndroidPlatform && _currentScale > 1.05)
            {
                _lastDragPoint = e.GetPosition(this);
                _isDragging = true;
                _isPotentialCenterClick = true;
                _pointerPressOrigin = _lastDragPoint;
                _pointerPressUtc = DateTime.UtcNow;
                e.Handled = true;
                return;
            }

            PointerPointProperties props = e.GetCurrentPoint(sv).Properties;

            if (props.IsMiddleButtonPressed)
            {
                _isPotentialCenterClick = false;
                ToggleAutoScroll(e.GetPosition(sv));
                e.Handled = true;
                return;
            }

            if (_isAutoScrolling)
            {
                _isPotentialCenterClick = false;
                StopAutoScroll();
                e.Handled = true;
                return;
            }

            if ((props.IsLeftButtonPressed || e.Pointer.Type == PointerType.Touch) && !IsPointerOverScrollBar(e.Source as Visual))
            {
                _lastDragPoint = e.GetPosition(sv);
                _isDragging    = !(IsAndroidPlatform && e.Pointer.Type == PointerType.Touch);

                Point viewportPos = e.GetPosition(PagesListBox);
                bool isInteractiveControl = e.Source is Visual src && src.FindAncestorOfType<Button>(includeSelf: true) is not null;
                _isPotentialCenterClick = !isInteractiveControl
                    && (IsAndroidPlatform || e.ClickCount == 1);

                if (_isPotentialCenterClick)
                {
                    _pointerPressOrigin = viewportPos;
                    _pointerPressUtc = DateTime.UtcNow;
                }
            }
        }

        private void OnScrollAreaPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_isPinching)
            {
                return;
            }

            if (IsAndroidPlatform && _currentScale > 1.05 && _isDragging)
            {
                Point current = e.GetPosition(this);
                double dX = current.X - _lastDragPoint.X;
                double dY = current.Y - _lastDragPoint.Y;

                if ((dX * dX + dY * dY) > (CenterClickMoveThreshold * CenterClickMoveThreshold))
                {
                    _isPotentialCenterClick = false;
                }

                _currentTranslateX += dX;
                _currentTranslateY += dY;
                ClampTranslation(ref _currentTranslateX, ref _currentTranslateY, _currentScale);
                ApplyVisualTransform();
                _lastDragPoint = current;
                e.Handled = true;
                return;
            }

            ScrollViewer? sv = AttachScrollViewer();
            if (sv is null) return;

            if (_isAutoScrolling)
            {
                _autoScrollCurrent = e.GetPosition(sv);
                return;
            }

            if (_isPotentialCenterClick)
            {
                Point viewportPos = e.GetPosition(PagesListBox);
                double dxMove = viewportPos.X - _pointerPressOrigin.X;
                double dyMove = viewportPos.Y - _pointerPressOrigin.Y;
                if ((dxMove * dxMove + dyMove * dyMove) > (CenterClickMoveThreshold * CenterClickMoveThreshold))
                {
                    _isPotentialCenterClick = false;
                }
            }

            if (!_isDragging) return;

            if (!e.GetCurrentPoint(sv).Properties.IsLeftButtonPressed && e.Pointer.Type != PointerType.Touch)
            {
                _isDragging = false;
                return;
            }

            Point cur = e.GetPosition(sv);
            double deltaX = cur.X - _lastDragPoint.X;
            double deltaY = cur.Y - _lastDragPoint.Y;

            sv.Offset = new Vector(
                Math.Max(0, sv.Offset.X - deltaX),
                Math.Max(0, sv.Offset.Y - deltaY));

            _lastDragPoint = cur;
        }

        private void OnScrollAreaPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_isPinching)
            {
                _isDragging = false;
                _isPotentialCenterClick = false;
                return;
            }

            if (IsAndroidPlatform && _currentScale > 1.05)
            {
                _isDragging = false;
                if (_isPotentialCenterClick)
                {
                    _isPotentialCenterClick = false;
                    Point releasePos = e.GetPosition(this);
                    double dx = releasePos.X - _pointerPressOrigin.X;
                    double dy = releasePos.Y - _pointerPressOrigin.Y;
                    double elapsedMs = (DateTime.UtcNow - _pointerPressUtc).TotalMilliseconds;

                    if ((dx * dx + dy * dy) <= (CenterClickMoveThreshold * CenterClickMoveThreshold)
                        && elapsedMs <= CenterClickMaxDurationMs)
                    {
                        DateTime now = DateTime.UtcNow;
                        double sinceLastTap = (now - _lastTapUtc).TotalMilliseconds;
                        double tapDx = releasePos.X - _lastTapOrigin.X;
                        double tapDy = releasePos.Y - _lastTapOrigin.Y;
                        bool isDoubleTap = sinceLastTap <= DoubleTapMaxDelayMs
                            && (tapDx * tapDx + tapDy * tapDy) <= (DoubleTapMaxDistancePx * DoubleTapMaxDistancePx);

                        if (isDoubleTap)
                        {
                            _singleTapToolbarTimer?.Stop();
                            _lastTapUtc = DateTime.MinValue;
                            ResetPinchVisualTransform();
                            e.Handled = true;
                            return;
                        }

                        _lastTapUtc = now;
                        _lastTapOrigin = releasePos;
                        if (IsInCenterClickZone(releasePos))
                        {
                            _singleTapToolbarTimer?.Stop();
                            _singleTapToolbarTimer?.Start();
                        }
                    }
                }
                e.Handled = true;
                return;
            }

            ScrollViewer? sv = AttachScrollViewer();

            if (e.InitialPressMouseButton == MouseButton.Middle && _isAutoScrolling && sv is not null)
            {
                if (Math.Abs(e.GetPosition(sv).Y - _autoScrollOrigin.Y) > AutoScrollDeadZone * 2)
                    StopAutoScroll();
            }

            if (_isDragging && (e.InitialPressMouseButton == MouseButton.Left || e.Pointer.Type == PointerType.Touch))
            {
                _isDragging = false;
                if (Equals(e.Pointer.Captured, PagesListBox))
                    e.Pointer.Capture(null);
            }

            bool canHandleTap = _isPotentialCenterClick
                && (e.InitialPressMouseButton == MouseButton.Left || e.Pointer.Type == PointerType.Touch);
            _isPotentialCenterClick = false;

            if (canHandleTap && _subscribedViewModel is not null)
            {
                Point releasePos = e.GetPosition(PagesListBox);
                double dx = releasePos.X - _pointerPressOrigin.X;
                double dy = releasePos.Y - _pointerPressOrigin.Y;
                double elapsedMs = (DateTime.UtcNow - _pointerPressUtc).TotalMilliseconds;

                if ((dx * dx + dy * dy) <= (CenterClickMoveThreshold * CenterClickMoveThreshold)
                    && elapsedMs <= CenterClickMaxDurationMs)
                {
                    if (IsAndroidPlatform)
                    {
                        DateTime now = DateTime.UtcNow;
                        double sinceLastTap = (now - _lastTapUtc).TotalMilliseconds;
                        double tapDx = releasePos.X - _lastTapOrigin.X;
                        double tapDy = releasePos.Y - _lastTapOrigin.Y;
                        bool isDoubleTap = sinceLastTap <= DoubleTapMaxDelayMs
                            && (tapDx * tapDx + tapDy * tapDy) <= (DoubleTapMaxDistancePx * DoubleTapMaxDistancePx);

                        if (isDoubleTap)
                        {
                            _singleTapToolbarTimer?.Stop();
                            _lastTapUtc = DateTime.MinValue;
                            double targetScale = 2.0;
                            Point tapPos = e.GetPosition(this);
                            double targetTx = tapPos.X - targetScale * tapPos.X;
                            double targetTy = tapPos.Y - targetScale * tapPos.Y;
                            ClampTranslation(ref targetTx, ref targetTy, targetScale);
                            _currentScale = targetScale;
                            _currentTranslateX = targetTx;
                            _currentTranslateY = targetTy;
                            ApplyVisualTransform();
                            return;
                        }

                        _lastTapUtc = now;
                        _lastTapOrigin = releasePos;
                        if (IsInCenterClickZone(releasePos))
                        {
                            _singleTapToolbarTimer?.Stop();
                            _singleTapToolbarTimer?.Start();
                        }
                    }
                    else if (IsInCenterClickZone(releasePos))
                    {
                        _subscribedViewModel.ToggleToolbarsCommand.Execute(null);
                    }
                }
            }
        }

        private void OnScrollAreaPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            _isDragging = false;
            _isPotentialCenterClick = false;
        }

        private void OnAndroidPinchStarted(double winXDip, double winYDip)
        {
            if (_subscribedViewModel is null || _subscribedViewModel.IsBookmarkPanelOpen) return;

            _isPinching = true;
            _isDragging = false;
            _isPotentialCenterClick = false;
            _singleTapToolbarTimer?.Stop();

            Point focal = MapTopLevelDipToReaderView(winXDip, winYDip);
            _pinchStartScale = _currentScale;
            _pinchStartTx = _currentTranslateX;
            _pinchStartTy = _currentTranslateY;

            // Content position under the initial focal point: C = (F - T) / S
            _pinchContentPoint = new Point(
                (focal.X - _pinchStartTx) / _pinchStartScale,
                (focal.Y - _pinchStartTy) / _pinchStartScale);
        }

        private void OnAndroidPinchUpdated(double spanRatio, double winXDip, double winYDip)
        {
            if (!_isPinching || _subscribedViewModel is null || _pinchStartScale <= 0.0) return;

            Point currentFocal = MapTopLevelDipToReaderView(winXDip, winYDip);
            double targetScale = Math.Clamp(_pinchStartScale * spanRatio, 0.85, Helpers.AppConstants.MaxZoomLevel);

            // Exact focal point preservation: T' = F' - S' * C
            double targetTx = currentFocal.X - targetScale * _pinchContentPoint.X;
            double targetTy = currentFocal.Y - targetScale * _pinchContentPoint.Y;

            if (targetScale >= 1.0)
            {
                ClampTranslation(ref targetTx, ref targetTy, targetScale);
            }

            _currentScale = targetScale;
            _currentTranslateX = targetTx;
            _currentTranslateY = targetTy;

            ApplyVisualTransform();
        }

        private void OnAndroidPinchEnded()
        {
            if (!_isPinching) return;
            _isPinching = false;

            if (_currentScale <= 1.05)
            {
                _currentScale = 1.0;
                _currentTranslateX = 0.0;
                _currentTranslateY = 0.0;
            }
            else
            {
                ClampTranslation(ref _currentTranslateX, ref _currentTranslateY, _currentScale);
            }
            ApplyVisualTransform();
        }

        private void ApplyVisualTransform()
        {
            if (_pinchScaleTransform is not null)
            {
                _pinchScaleTransform.ScaleX = _currentScale;
                _pinchScaleTransform.ScaleY = _currentScale;
            }
            if (_pinchTranslateTransform is not null)
            {
                _pinchTranslateTransform.X = _currentTranslateX;
                _pinchTranslateTransform.Y = _currentTranslateY;
            }
        }

        private void ClampTranslation(ref double tx, ref double ty, double scale)
        {
            double vw = Bounds.Width > 0 ? Bounds.Width : PagesListBox.Bounds.Width;
            double vh = Bounds.Height > 0 ? Bounds.Height : PagesListBox.Bounds.Height;
            if (vw <= 0 || vh <= 0 || scale <= 1.0)
            {
                tx = 0.0;
                ty = 0.0;
                return;
            }

            double minX = vw - scale * vw;
            double maxX = 0.0;
            tx = Math.Clamp(tx, minX, maxX);

            double minY = vh - scale * vh;
            double maxY = 0.0;
            ty = Math.Clamp(ty, minY, maxY);
        }

        private Point MapTopLevelDipToReaderView(double winXDip, double winYDip)
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is not null)
            {
                // 'this' (ReaderView) has no RenderTransform, avoiding any positive feedback translation corruption
                Point? origin = this.TranslatePoint(new Point(0, 0), topLevel);
                if (origin.HasValue)
                {
                    return new Point(winXDip - origin.Value.X, winYDip - origin.Value.Y);
                }
            }
            return new Point(winXDip, winYDip);
        }

        private void ResetPinchVisualTransform()
        {
            _currentScale = 1.0;
            _currentTranslateX = 0.0;
            _currentTranslateY = 0.0;
            ApplyVisualTransform();
        }

        private void OnAndroidSafeAreaChanged(Thickness padding) => SafeAreaPadding = padding;

        private void OnSingleTapToolbarTimerTick(object? sender, EventArgs e)
        {
            _singleTapToolbarTimer?.Stop();
            _subscribedViewModel?.ToggleToolbarsCommand.Execute(null);
        }

        private void OnScrollAreaPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            if (DataContext is not ReaderViewModel vm) return;

            ScrollViewer? sv = AttachScrollViewer();
            if (sv is not null)
                _pendingZoomAnchor = e.GetPosition(sv);

            if (e.Delta.Y > 0) vm.ZoomInCommand.Execute(null);
            else if (e.Delta.Y < 0) vm.ZoomOutCommand.Execute(null);

            e.Handled = true;
        }

        // ── scroll tracking → update VM current page ───────────────────────────
        private void OnScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (_isSuppressingScrollTracking || _isDraggingSlider) return;
            if (DateTime.UtcNow < _suppressScrollUntilUtc) return;
            if (Math.Abs(e.OffsetDelta.Y) < 0.5 && Math.Abs(e.OffsetDelta.X) < 0.5) return;
            if (DataContext is not ReaderViewModel vm || vm.TotalPages <= 0) return;

            DateTime now = DateTime.UtcNow;
            if ((now - _lastScrollProcessUtc).TotalMilliseconds >= ScrollThrottleMs)
            {
                ProcessScrollPosition(vm);
            }
            else if (_scrollDebounceTimer is not null && !_scrollDebounceTimer.IsEnabled)
            {
                _scrollDebounceTimer.Start();
            }
        }

        private void OnScrollDebounceTick(object? sender, EventArgs e)
        {
            _scrollDebounceTimer?.Stop();
            if (_isSuppressingScrollTracking || _isDraggingSlider) return;
            if (DateTime.UtcNow < _suppressScrollUntilUtc) return;
            if (DataContext is not ReaderViewModel vm || vm.TotalPages <= 0) return;

            ProcessScrollPosition(vm);
        }

        private void ProcessScrollPosition(ReaderViewModel vm)
        {
            ScrollViewer? sv = AttachScrollViewer();
            if (sv is null) return;

            _lastScrollProcessUtc = DateTime.UtcNow;
            (int page, double localRatio) = DetectCurrentPage(sv);
            double scrollable = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            double globalRatio = scrollable > 0 ? sv.Offset.Y / scrollable : 0.0;

            vm.UpdateScrollPosition(page, globalRatio, localRatio);
        }

        private (int Page, double LocalRatio) DetectCurrentPage(ScrollViewer sv) =>
            ScrollAnchor.DetectCurrentPage(
                PagesListBox.GetRealizedContainers(),
                sv,
                sv.Viewport.Height,
                _subscribedViewModel?.CurrentPage ?? 1);

        // ── middle-mouse auto-scroll ───────────────────────────────────────────
        private void ToggleAutoScroll(Point origin)
        {
            if (_isAutoScrolling) { StopAutoScroll(); return; }

            _autoScrollOrigin  = origin;
            _autoScrollCurrent = origin;
            _isAutoScrolling   = true;
            PagesListBox.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);

            _autoScrollTimer ??= new DispatcherTimer(
                TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnAutoScrollTick);
            _autoScrollTimer.Start();
        }

        private void StopAutoScroll()
        {
            if (!_isAutoScrolling) return;
            _isAutoScrolling   = false;
            PagesListBox.Cursor = Cursor.Default;
            _autoScrollTimer?.Stop();
        }

        private void OnAutoScrollTick(object? sender, EventArgs e)
        {
            ScrollViewer? sv = AttachScrollViewer();
            if (sv is null) return;

            double dY = _autoScrollCurrent.Y - _autoScrollOrigin.Y;
            if (Math.Abs(dY) <= AutoScrollDeadZone) return;

            double step = (dY - Math.Sign(dY) * AutoScrollDeadZone) * AutoScrollSpeedFactor;
            sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y + step));
        }

        // ── slider interaction ─────────────────────────────────────────────────
        private void OnPageSliderPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_subscribedViewModel is null || _subscribedViewModel.TotalPages <= 0) return;

            bool isThumb = e.Source is Visual src && src.FindAncestorOfType<Thumb>(includeSelf: true) is not null;
            if (isThumb)
            {
                _isDraggingSlider = true;
                _sliderDebounceTimer?.Stop();
                return;
            }

            // Track click: compute exact target page from click X position and navigate immediately.
            Visual relativeTarget = PageSlider;
            Track? track = PageSlider.GetVisualDescendants().OfType<Track>().FirstOrDefault();
            if (track is not null && track.Bounds.Width > 0)
            {
                relativeTarget = track;
            }

            double width = relativeTarget.Bounds.Width;
            if (width <= 0) return;

            double ratio = Math.Clamp(e.GetPosition(relativeTarget).X / width, 0.0, 1.0);
            double min = PageSlider.Minimum;
            double max = Math.Max(min, PageSlider.Maximum);
            int targetPage = Math.Clamp((int)Math.Round(min + ratio * (max - min)), 1, _subscribedViewModel.TotalPages);

            _isDraggingSlider = false;
            _sliderDebounceTimer?.Stop();
            PageSlider.SetCurrentValue(RangeBase.ValueProperty, (double)targetPage);
            _subscribedViewModel.JumpToPage(targetPage);
            e.Handled = true;
        }

        private void OnPageSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != RangeBase.ValueProperty || !_isDraggingSlider) return;
            if (_subscribedViewModel is null || _subscribedViewModel.TotalPages <= 0) return;

            int target = Math.Clamp(
                (int)Math.Round(PageSlider.Value),
                1,
                _subscribedViewModel.TotalPages);

            // Scroll viewport in real-time while dragging (like the right scrollbar),
            // and debounce heavy page decoding until drag pauses/ends.
            _subscribedViewModel.PreviewScrollToPage(target);
            ScheduleSliderDebounce();
        }

        private void OnPageSliderDragStarted(object? sender, VectorEventArgs e)
        {
            _isDraggingSlider = true;
            _sliderDebounceTimer?.Stop();
        }

        private void OnPageSliderDragCompleted(object? sender, VectorEventArgs e)
        {
            // Thumb drag released → navigate immediately with zero extra delay.
            _sliderDebounceTimer?.Stop();
            _isDraggingSlider = false;
            CommitSliderNavigation();
        }

        private void OnPageSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // Fires for BOTH track-click and thumb-release → navigate immediately on release.
            if (_isDraggingSlider)
            {
                _sliderDebounceTimer?.Stop();
                _isDraggingSlider = false;
                CommitSliderNavigation();
            }
        }

        private void OnPageSliderPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (_isDraggingSlider)
            {
                _sliderDebounceTimer?.Stop();
                _isDraggingSlider = false;
                CommitSliderNavigation();
            }
        }

        private void ScheduleSliderDebounce()
        {
            if (_sliderDebounceTimer is null) return;
            _sliderDebounceTimer.Stop();
            _sliderDebounceTimer.Start();
        }

        private void OnSliderDebounceTick(object? sender, EventArgs e)
        {
            _sliderDebounceTimer?.Stop();
            if (_subscribedViewModel is null) return;

            int target = Math.Clamp(
                (int)Math.Round(PageSlider.Value),
                1,
                Math.Max(1, _subscribedViewModel.TotalPages));

            if (_isDraggingSlider)
            {
                // User paused briefly mid-drag: pre-load just the single target page so release feels instant.
                _subscribedViewModel.PreviewLoadSinglePage(target);
            }
            else
            {
                _subscribedViewModel.JumpToPage(target);
            }
        }

        /// <summary>
        /// Reads the slider's current Value and navigates the ViewModel to that page.
        /// Called only after debounce; safe to call if no change occurred.
        /// </summary>
        private void CommitSliderNavigation()
        {
            if (_subscribedViewModel is null) return;

            int target = Math.Clamp(
                (int)Math.Round(PageSlider.Value),
                1,
                Math.Max(1, _subscribedViewModel.TotalPages));

            // Always navigate via unified JumpToPage so scroll-position is restored even if page number matches.
            _subscribedViewModel.JumpToPage(target);
        }

        // ── helpers ────────────────────────────────────────────────────────────
        private bool IsInCenterClickZone(Point pos)
        {
            double w = PagesListBox.Bounds.Width;
            double h = PagesListBox.Bounds.Height;
            if (w <= 0 || h <= 0) return false;

            return pos.X >= w * 0.20 && pos.X <= w * 0.80
                && pos.Y >= h * 0.15 && pos.Y <= h * 0.85;
        }

        private static bool IsPointerOverScrollBar(Visual? source)
            => source?.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null;

        private static bool IsDescendantOf(Visual? source, Visual? ancestor)
        {
            if (ancestor is null) return false;
            Visual? cur = source;
            while (cur is not null)
            {
                if (ReferenceEquals(cur, ancestor)) return true;
                cur = cur.GetVisualParent();
            }
            return false;
        }
    }
}
