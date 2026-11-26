using System;
using Avalonia;
using Avalonia.Controls;
using ComicReader.Models;

namespace ComicReader.Helpers
{
    /// <summary>
    /// Positions bookmark marker buttons horizontally along the reader page slider
    /// according to each bookmark's 1-based PageNumber relative to TotalPages.
    /// </summary>
    public sealed class BookmarkTrackPanel : Panel
    {
        public static readonly StyledProperty<int> TotalPagesProperty =
            AvaloniaProperty.Register<BookmarkTrackPanel, int>(nameof(TotalPages), 1);

        public static readonly AttachedProperty<int> PageNumberProperty =
            AvaloniaProperty.RegisterAttached<BookmarkTrackPanel, Control, int>("PageNumber", 1);

        static BookmarkTrackPanel()
        {
            AffectsArrange<BookmarkTrackPanel>(TotalPagesProperty);
        }

        public int TotalPages
        {
            get => GetValue(TotalPagesProperty);
            set => SetValue(TotalPagesProperty, value);
        }

        public static int GetPageNumber(Control element) => element.GetValue(PageNumberProperty);

        public static void SetPageNumber(Control element, int value) => element.SetValue(PageNumberProperty, value);

        protected override Size MeasureOverride(Size availableSize)
        {
            double maxHeight = 0;
            foreach (Control child in Children)
            {
                child.Measure(availableSize);
                if (child.DesiredSize.Height > maxHeight)
                {
                    maxHeight = child.DesiredSize.Height;
                }
            }

            double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
            return new Size(width, maxHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int total = Math.Max(1, TotalPages);

            foreach (Control child in Children)
            {
                int rawPage = child.DataContext is Bookmark bm ? bm.PageNumber : GetPageNumber(child);
                int page = Math.Clamp(rawPage, 1, total);
                double ratio = total > 1 ? (double)(page - 1) / (total - 1) : 0.0;
                double w = child.DesiredSize.Width;
                double h = child.DesiredSize.Height;
                double x = ratio * Math.Max(0, finalSize.Width - w);
                double y = Math.Max(0, (finalSize.Height - h) * 0.5);

                child.Arrange(new Rect(x, y, w, h));
            }

            return finalSize;
        }
    }
}
