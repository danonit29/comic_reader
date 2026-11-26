using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        private const double AutoScrollDeadZone   = 8.0;
        private const double AutoScrollSpeedFactor = 0.28;
        private const int    SliderDebounceMs      = 180;
        private const int    ScrollDebounceMs      = 150;

        // ── private state ──────────────────────────────────────────────────────
        private ScrollViewer?      _scrollViewer;
        private DispatcherTimer?   _autoScrollTimer;
        private DispatcherTimer?   _sliderDebounceTimer;
        private DispatcherTimer?   _scrollDebounceTimer;
        private Point              _lastDragPoint;
        private Point              _autoScrollOrigin;
        private Point              _autoScrollCurrent;
        private bool               _isDragging;
        private bool               _isAutoScrolling;
        private bool               _isSuppressingScrollTracking;
        // Tracks whether the slider thumb is currently being dragged by the user.
        // Reset to false only after the debounce timer fires, ensuring no premature navigation.
        private bool               _isDraggingSlider;
        private ReaderViewModel?   _subscribedViewModel;

        // ── styled properties exposed to DataTemplates ─────────────────────────
        /// <summary>Zoom scale for page images, bound via RelativeSource inside DataTemplate.</summary>
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

        /// <summary>Effective page width after applying ZoomLevel (avoids LayoutTransformControl overhead).</summary>
        public static readonly StyledProperty<double> ScaledPageWidthProperty =
            AvaloniaProperty.Register<ReaderView, double>(nameof(ScaledPageWidth), 900.0);

        public double ScaledPageWidth
        {
            get => GetValue(ScaledPageWidthProperty);
            set => SetValue(ScaledPageWidthProperty, value);
        }

        /// <summary>Strongly-typed ViewModel for use in DataTemplate CompiledBindings.</summary>
        public ReaderViewModel? ViewModel => DataContext as ReaderViewModel;

        // ── constructor ────────────────────────────────────────────────────────
        public ReaderView()
        {
            InitializeComponent();

            DataContextChanged += OnDataContextChanged;
            Loaded             += OnLoaded;
            Unloaded           += OnUnloaded;

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

            _sliderDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SliderDebounceMs) };
            _sliderDebounceTimer.Tick += OnSliderDebounceTick;

            _scrollDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ScrollDebounceMs) };
            _scrollDebounceTimer.Tick += OnScrollDebounceTick;
        }

        // ── lifecycle ──────────────────────────────────────────────────────────
        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            AttachScrollViewer();
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (_subscribedViewModel is not null)
            {
                _subscribedViewModel.ScrollToPageRequested              -= OnScrollToPageRequested;
                _subscribedViewModel.RestoreExactPagePositionRequested  -= OnRestoreExactPagePositionRequested;
                _subscribedViewModel.ZoomChanging                       -= OnZoomChanging;
                _subscribedViewModel.PropertyChanged                    -= OnViewModelPropertyChanged;
            }

            _subscribedViewModel = DataContext as ReaderViewModel;
            _sliderDebounceTimer?.Stop();
            _scrollDebounceTimer?.Stop();
            StopAutoScroll();
            _isDragging       = false;
            _isDraggingSlider = false;
            _isSuppressingScrollTracking = false;

            if (_scrollViewer is not null)
                _scrollViewer.Offset = default;

            if (_subscribedViewModel is not null)
            {
                ZoomLevel = _subscribedViewModel.ZoomLevel;
                UpdateScaledPageWidth();
                _subscribedViewModel.ScrollToPageRequested             += OnScrollToPageRequested;
                _subscribedViewModel.RestoreExactPagePositionRequested += OnRestoreExactPagePositionRequested;
                _subscribedViewModel.ZoomChanging                      += OnZoomChanging;
                _subscribedViewModel.PropertyChanged                   += OnViewModelPropertyChanged;
            }
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ReaderViewModel.ZoomLevel) && _subscribedViewModel is not null)
            {
                ZoomLevel = _subscribedViewModel.ZoomLevel;
                UpdateScaledPageWidth();
            }
        }

        private void OnUnloaded(object? sender, RoutedEventArgs e)
        {
            _sliderDebounceTimer?.Stop();
            _scrollDebounceTimer?.Stop();
            StopAutoScroll();
            DetachScrollViewer();
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

        // ── page-width calculation ─────────────────────────────────────────────
        private void OnPagesListBoxSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) >= 1.0)
                UpdateBasePageWidth();
        }

        private void UpdateBasePageWidth()
        {
            double width = PagesListBox.Bounds.Width;
            if (width <= 0) return;
            double target = Math.Max(320.0, width - 32.0);
            if (Math.Abs(BasePageWidth - target) >= 1.0)
            {
                BasePageWidth = target;
                UpdateScaledPageWidth();
            }
        }

        private void UpdateScaledPageWidth()
        {
            double target = Math.Max(200.0, BasePageWidth * ZoomLevel);
            if (Math.Abs(ScaledPageWidth - target) >= 1.0)
                ScaledPageWidth = target;
        }

        // ── zoom focal-point preservation ──────────────────────────────────────
        private void OnZoomChanging(double oldZoom, double newZoom)
        {
            ScrollViewer? sv = AttachScrollViewer();
            if (sv is null || oldZoom <= 0 || newZoom <= 0) return;

            double centerY = (sv.Offset.Y + sv.Viewport.Height * 0.5) / oldZoom;
            double centerX = (sv.Offset.X + sv.Viewport.Width  * 0.5) / oldZoom;

            _isSuppressingScrollTracking = true;
            Dispatcher.UIThread.Post(() =>
            {
                double targetY = Math.Max(0, centerY * newZoom - sv.Viewport.Height * 0.5);
                double targetX = Math.Max(0, centerX * newZoom - sv.Viewport.Width  * 0.5);
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
                    if (container is not null)
                    {
                        Point? pt = container.TranslatePoint(new Point(0, 0), sv);
                        if (pt.HasValue)
                            sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y + pt.Value.Y));
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
                        Point? pt = container.TranslatePoint(new Point(0, 0), sv);
                        if (pt.HasValue)
                        {
                            double localOffset = Math.Clamp(pageLocalRatio, 0.0, 1.0) * container.Bounds.Height;
                            sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y + pt.Value.Y + localOffset));
                        }
                    }
                }
                ReleaseScrollSuppression();
            }, DispatcherPriority.Loaded);
        }

        private void ReleaseScrollSuppression()
        {
            Dispatcher.UIThread.Post(() => _isSuppressingScrollTracking = false, DispatcherPriority.Background);
        }

        // ── container size change (keeps placeholder height stable) ───────────
        private void OnPageContainerSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (sender is not Control element) return;

            if (element.DataContext is Models.ComicPageItem pageItem &&
                pageItem.Image is not null &&
                e.NewSize.Height > pageItem.PlaceholderHeight)
            {
                pageItem.PlaceholderHeight = e.NewSize.Height;
            }

            if (_isSuppressingScrollTracking) return;
            if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.5) return;

            ScrollViewer? sv = AttachScrollViewer();
            if (sv is null) return;

            Point? topInViewport = element.TranslatePoint(new Point(0, 0), sv);
            if (topInViewport is { Y: < 0 })
            {
                double delta = e.NewSize.Height - e.PreviousSize.Height;
                _isSuppressingScrollTracking = true;
                sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y + delta));
                ReleaseScrollSuppression();
            }
        }

        // ── pointer events on scroll area (pan-drag + middle-auto-scroll) ──────
        private void OnScrollAreaPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // Don't interfere if the event originates from the slider or its children.
            if (IsDescendantOf(e.Source as Visual, PageSlider)) return;

            ScrollViewer? sv = AttachScrollViewer();
            if (sv is null) return;

            PointerPointProperties props = e.GetCurrentPoint(sv).Properties;

            if (props.IsMiddleButtonPressed)
            {
                ToggleAutoScroll(e.GetPosition(sv));
                e.Handled = true;
                return;
            }

            if (_isAutoScrolling)
            {
                StopAutoScroll();
                e.Handled = true;
                return;
            }

            if (props.IsLeftButtonPressed && !IsPointerOverScrollBar(e.Source as Visual))
            {
                _lastDragPoint = e.GetPosition(sv);
                _isDragging    = true;
            }
        }

        private void OnScrollAreaPointerMoved(object? sender, PointerEventArgs e)
        {
            ScrollViewer? sv = AttachScrollViewer();
            if (sv is null) return;

            if (_isAutoScrolling)
            {
                _autoScrollCurrent = e.GetPosition(sv);
                return;
            }

            if (!_isDragging) return;

            if (!e.GetCurrentPoint(sv).Properties.IsLeftButtonPressed)
            {
                _isDragging = false;
                return;
            }

            Point current = e.GetPosition(sv);
            double dX = current.X - _lastDragPoint.X;
            double dY = current.Y - _lastDragPoint.Y;

            sv.Offset = new Vector(
                Math.Max(0, sv.Offset.X - dX),
                Math.Max(0, sv.Offset.Y - dY));

            _lastDragPoint = current;
        }

        private void OnScrollAreaPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            ScrollViewer? sv = AttachScrollViewer();

            if (e.InitialPressMouseButton == MouseButton.Middle && _isAutoScrolling && sv is not null)
            {
                if (Math.Abs(e.GetPosition(sv).Y - _autoScrollOrigin.Y) > AutoScrollDeadZone * 2)
                    StopAutoScroll();
            }

            if (_isDragging && e.InitialPressMouseButton == MouseButton.Left)
            {
                _isDragging = false;
                if (Equals(e.Pointer.Captured, PagesListBox))
                    e.Pointer.Capture(null);
            }
        }

        private void OnScrollAreaPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
            => _isDragging = false;

        private void OnScrollAreaPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            if (DataContext is not ReaderViewModel vm) return;

            if (e.Delta.Y > 0) vm.ZoomInCommand.Execute(null);
            else if (e.Delta.Y < 0) vm.ZoomOutCommand.Execute(null);

            e.Handled = true;
        }

        // ── scroll tracking → update VM current page ───────────────────────────
        private void OnScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (_isSuppressingScrollTracking || _isDraggingSlider) return;
            if (DataContext is not ReaderViewModel vm || vm.TotalPages <= 0) return;

            _scrollDebounceTimer?.Stop();
            _scrollDebounceTimer?.Start();
        }

        private void OnScrollDebounceTick(object? sender, EventArgs e)
        {
            _scrollDebounceTimer?.Stop();
            if (_isSuppressingScrollTracking || _isDraggingSlider) return;
            if (DataContext is not ReaderViewModel vm || vm.TotalPages <= 0) return;

            ScrollViewer? sv = AttachScrollViewer();
            if (sv is null) return;

            (int page, double localRatio) = DetectCurrentPage(sv);
            double scrollable = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            double globalRatio = scrollable > 0 ? sv.Offset.Y / scrollable : 0.0;

            vm.UpdateScrollPosition(page, globalRatio, localRatio);
        }

        private (int Page, double LocalRatio) DetectCurrentPage(ScrollViewer sv)
        {
            int    fallback = Math.Max(1, _subscribedViewModel?.CurrentPage ?? 1);
            int    best     = -1;
            double bestRatio = 0.0;

            foreach (Control container in PagesListBox.GetRealizedContainers())
            {
                if (container.DataContext is not Models.ComicPageItem item) continue;
                Point? tl = container.TranslatePoint(new Point(0, 0), sv);
                if (!tl.HasValue) continue;

                double h      = container.Bounds.Height;
                double bottom = tl.Value.Y + h;
                if (bottom < 0) continue;

                if (best == -1 || item.PageNumber < best)
                {
                    best      = item.PageNumber;
                    bestRatio = h > 0 ? Math.Clamp(-tl.Value.Y / h, 0.0, 1.0) : 0.0;
                }
            }

            return best > 0 ? (best, bestRatio) : (fallback, 0.0);
        }

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
            _isDraggingSlider = true;
            _sliderDebounceTimer?.Stop();
        }

        private void OnPageSliderDragStarted(object? sender, VectorEventArgs e)
        {
            _isDraggingSlider = true;
            _sliderDebounceTimer?.Stop();
        }

        private void OnPageSliderDragCompleted(object? sender, VectorEventArgs e)
        {
            // Thumb drag released → start debounce countdown.
            ScheduleSliderDebounce();
        }

        private void OnPageSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // Fires for BOTH track-click (DragCompleted not raised) and thumb-release (DragCompleted already raised).
            // ScheduleSliderDebounce is idempotent: calling it twice just resets the timer.
            if (_isDraggingSlider)
                ScheduleSliderDebounce();
        }

        private void OnPageSliderPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (_isDraggingSlider)
                ScheduleSliderDebounce();
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
            _isDraggingSlider = false;
            CommitSliderNavigation();
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

            // Always navigate so scroll-position is restored even if page number matches
            // (e.g., user clicked same page bookmark on the track).
            _subscribedViewModel.GoToPageCommand.Execute(target);
        }

        // ── helpers ────────────────────────────────────────────────────────────
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
