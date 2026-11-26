using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace ComicReader.Helpers.Converters
{
    /// <summary>
    /// Converts a local file path string into a cached, width-constrained Avalonia Bitmap.
    /// </summary>
    public sealed class PathToImageConverter : IValueConverter
    {
        private static readonly ConcurrentDictionary<string, WeakReference<Bitmap>> CoverCache = new(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            if (CoverCache.TryGetValue(path, out WeakReference<Bitmap>? weakRef) &&
                weakRef.TryGetTarget(out Bitmap? cached))
            {
                return cached;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Bitmap decoded = ImageHelper.DecodeFromStream(stream, AppConstants.CoverDecodeWidth);
            CoverCache[path] = new WeakReference<Bitmap>(decoded);
            return decoded;
        }

        /// <inheritdoc />
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return BindingOperations.DoNothing;
        }
    }
}
