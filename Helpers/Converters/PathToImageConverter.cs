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
        private static readonly ConcurrentDictionary<string, Bitmap> CoverCache = new(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            return CoverCache.GetOrAdd(path, static filePath =>
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return ImageHelper.DecodeFromStream(stream, AppConstants.CoverDecodeWidth);
            });
        }

        /// <inheritdoc />
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return BindingOperations.DoNothing;
        }
    }
}
