using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using ComicReader.Helpers;
using ComicReader.Models;

namespace ComicReader.Reader
{
    /// <summary>
    /// Layer 2 sliding-window cache &amp; eviction policy manager (single source of truth for Bitmap RAM).
    /// Keeps 3 behind + current + 4 ahead Full-Quality Bitmaps plus lightweight previews and raw compressed bytes.
    /// </summary>
    public sealed class ViewportManager
    {
        public const int FullLoadPageThreshold = 200;
        public const int LargeBookInitialPreloadCount = 100;
        public const int PreloadBehindCount = 3;
        public const int PreloadAheadCount = 4;
        public const int CompressedWindowRadius = 8;

        private readonly ImageCacheManager _cache = new(keepBehind: PreloadBehindCount, keepAhead: PreloadAheadCount);

        public void ConfigureDefaultWindow() => _cache.ConfigureWindow(PreloadBehindCount, PreloadAheadCount);

        public bool Contains(int pageIndex) => _cache.Contains(pageIndex);
        public bool ContainsFull(int pageIndex) => _cache.ContainsFull(pageIndex);
        public bool ContainsPreview(int pageIndex) => _cache.ContainsPreview(pageIndex);
        public bool ContainsCompressed(int pageIndex) => _cache.ContainsCompressed(pageIndex);

        public bool TryGetFull(int pageIndex, out Bitmap? bitmap) => _cache.TryGetFull(pageIndex, out bitmap);
        public bool TryGetPreview(int pageIndex, out Bitmap? bitmap) => _cache.TryGetPreview(pageIndex, out bitmap);
        public bool TryGetCompressed(int pageIndex, out byte[]? bytes) => _cache.TryGetCompressed(pageIndex, out bytes);

        public void SetFull(int pageIndex, Bitmap bitmap) => _cache.SetFull(pageIndex, bitmap);
        public void SetPreview(int pageIndex, Bitmap bitmap) => _cache.SetPreview(pageIndex, bitmap);
        public void SetCompressed(int pageIndex, byte[] bytes) => _cache.SetCompressed(pageIndex, bytes);

        public void EvictOutsideWindow(int centerIndex, IList<ComicPageItem> pages, bool trimLargeBookCaches)
        {
            _cache.EvictOutsideWindow(centerIndex, (index, previewFallback) =>
            {
                if (index >= 0 && index < pages.Count)
                {
                    pages[index].Image = previewFallback;
                }
            });

            if (trimLargeBookCaches)
            {
                _cache.EvictPreviewsOutsideWindow(centerIndex, 20, 30, (index, fullFallback) =>
                {
                    if (index >= 0 && index < pages.Count && fullFallback is null)
                    {
                        pages[index].Image = null;
                    }
                });
                _cache.EvictCompressedOutsideWindow(centerIndex, CompressedWindowRadius, CompressedWindowRadius);
            }
        }

        public void Clear(IList<ComicPageItem> pages)
        {
            _cache.Clear(index =>
            {
                if (index >= 0 && index < pages.Count)
                {
                    pages[index].Image = null;
                }
            });
        }

        public static IEnumerable<int> BuildWindowOrder(int centerIndex, int totalCount, int scrollDirection = 1)
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
    }
}
