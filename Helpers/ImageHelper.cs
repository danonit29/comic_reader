using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ComicReader.Helpers
{
    /// <summary>
    /// Utility methods for creating and decoding Skia-backed Avalonia Bitmap images.
    /// </summary>
    public static class ImageHelper
    {
        /// <summary>
        /// Creates a WriteableBitmap directly from raw BGRA32 pixel bytes without intermediate PNG re-encoding.
        /// </summary>
        public static Bitmap CreateFromBgra32(byte[] bgraBytes, int width, int height, int dpi = AppConstants.DefaultRenderDpi)
        {
            // Use 96 DIPs so Avalonia displays the rendered bitmap at 1:1 pixel size (matching WPF)
            // instead of shrinking it by 96 / dpi.
            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using ILockedFramebuffer locked = bitmap.Lock();
            int expectedStride = width * 4;
            if (locked.RowBytes == expectedStride)
            {
                Marshal.Copy(bgraBytes, 0, locked.Address, Math.Min(bgraBytes.Length, expectedStride * height));
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    IntPtr destRow = IntPtr.Add(locked.Address, y * locked.RowBytes);
                    Marshal.Copy(bgraBytes, y * expectedStride, destRow, expectedStride);
                }
            }

            return bitmap;
        }

        /// <summary>
        /// Decodes an image stream into an Avalonia Bitmap, optionally downscaling to a target width.
        /// </summary>
        public static Bitmap DecodeFromStream(Stream stream, int? decodePixelWidth = null)
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            return decodePixelWidth.HasValue
                ? Bitmap.DecodeToWidth(stream, decodePixelWidth.Value, BitmapInterpolationMode.HighQuality)
                : new Bitmap(stream);
        }

        /// <summary>
        /// Decodes a byte buffer into an Avalonia Bitmap, optionally downscaling to a target width.
        /// </summary>
        public static Bitmap DecodeFromBytes(byte[] bytes, int? decodePixelWidth = null)
        {
            using var memoryStream = new MemoryStream(bytes, writable: false);
            return DecodeFromStream(memoryStream, decodePixelWidth);
        }

        /// <summary>
        /// Decodes a reader page without upscaling small images, while downscaling oversized scans
        /// above <paramref name="maxPixelWidth"/> so Skia rendering stays smooth and memory stays bounded.
        /// </summary>
        public static Bitmap DecodeForReader(byte[] bytes, int maxPixelWidth = AppConstants.MaxReaderDecodeWidth)
        {
            using var memoryStream = new MemoryStream(bytes, writable: false);
            int? originalWidth = TryReadImageWidth(bytes);
            int? targetWidth = originalWidth.HasValue && originalWidth.Value > maxPixelWidth
                ? maxPixelWidth
                : null;

            return DecodeFromStream(memoryStream, targetWidth);
        }

        private static int? TryReadImageWidth(byte[] bytes)
        {
            if (bytes.Length >= 24 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                // PNG IHDR width is big-endian at bytes 16..19.
                return (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            }

            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                // JPEG SOF0/SOF1/SOF2 marker scan.
                int i = 2;
                while (i + 9 < bytes.Length)
                {
                    if (bytes[i] != 0xFF) break;
                    byte marker = bytes[i + 1];
                    if (marker is 0xC0 or 0xC1 or 0xC2)
                    {
                        return (bytes[i + 7] << 8) | bytes[i + 8];
                    }

                    if (marker is 0xD8 or 0xD9 or 0x00 || (marker >= 0xD0 && marker <= 0xD7))
                    {
                        i += 2;
                        continue;
                    }

                    int segmentLen = (bytes[i + 2] << 8) | bytes[i + 3];
                    if (segmentLen < 2) break;
                    i += 2 + segmentLen;
                }
            }

            return null;
        }
    }
}
