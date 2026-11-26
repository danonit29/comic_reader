using System;
using Avalonia;
using Avalonia.Controls.Primitives;

namespace ComicReader.Helpers
{
    /// <summary>
    /// A UniformGrid that automatically computes its column count from the available width
    /// so cards stretch evenly edge-to-edge without leaving empty space on the right.
    /// </summary>
    public sealed class ResponsiveUniformGrid : UniformGrid
    {
        public static readonly StyledProperty<double> MinItemWidthProperty =
            AvaloniaProperty.Register<ResponsiveUniformGrid, double>(nameof(MinItemWidth), 200.0);

        public double MinItemWidth
        {
            get => GetValue(MinItemWidthProperty);
            set => SetValue(MinItemWidthProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (!double.IsInfinity(availableSize.Width) && availableSize.Width > 0 && MinItemWidth > 0)
            {
                int cols = Math.Max(1, (int)Math.Floor(availableSize.Width / MinItemWidth));
                if (Columns != cols)
                {
                    Columns = cols;
                }
            }

            return base.MeasureOverride(availableSize);
        }
    }
}
