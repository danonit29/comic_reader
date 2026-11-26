using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ComicReader.Helpers
{
    /// <summary>
    /// Sliding-window in-memory cache for decoded comic page Bitmaps.
    /// Keeps only the current page, <see cref="KeepBehind"/> previous pages, and <see cref="KeepAhead"/> next pages,
    /// safely detaching and disposing older Bitmaps to prevent OutOfMemoryException.
    /// </summary>
    public sealed class ImageCacheManager
    {
        private readonly Dictionary<int, Bitmap> _cache = new();
        private readonly object _syncLock = new();

        public int KeepBehind { get; }
        public int KeepAhead { get; }

        public ImageCacheManager(int keepBehind = 2, int keepAhead = 2)
        {
            KeepBehind = Math.Max(0, keepBehind);
            KeepAhead = Math.Max(0, keepAhead);
        }

        public bool Contains(int pageIndex)
        {
            lock (_syncLock)
            {
                return _cache.ContainsKey(pageIndex);
            }
        }

        public void Set(int pageIndex, Bitmap bitmap)
        {
            Bitmap? replaced = null;
            lock (_syncLock)
            {
                if (_cache.TryGetValue(pageIndex, out Bitmap? existing) && !ReferenceEquals(existing, bitmap))
                {
                    replaced = existing;
                }

                _cache[pageIndex] = bitmap;
            }

            if (replaced is not null)
            {
                DisposeSafelyOnBackground(new[] { replaced });
            }
        }

        /// <summary>
        /// Evicts all pages outside [centerIndex - KeepBehind, centerIndex + KeepAhead].
        /// Invokes <paramref name="onBeforeEvict"/> on the UI thread before disposing each Bitmap.
        /// </summary>
        public void EvictOutsideWindow(int centerIndex, Action<int>? onBeforeEvict = null)
        {
            int minKeep = centerIndex - KeepBehind;
            int maxKeep = centerIndex + KeepAhead;
            var toEvict = new List<KeyValuePair<int, Bitmap>>();

            lock (_syncLock)
            {
                foreach (KeyValuePair<int, Bitmap> kvp in _cache)
                {
                    if (kvp.Key < minKeep || kvp.Key > maxKeep)
                    {
                        toEvict.Add(kvp);
                    }
                }

                foreach (KeyValuePair<int, Bitmap> kvp in toEvict)
                {
                    _cache.Remove(kvp.Key);
                }
            }

            if (toEvict.Count == 0) return;

            var bitmaps = new List<Bitmap>(toEvict.Count);
            foreach (KeyValuePair<int, Bitmap> kvp in toEvict)
            {
                onBeforeEvict?.Invoke(kvp.Key);
                bitmaps.Add(kvp.Value);
            }

            DisposeSafelyOnBackground(bitmaps);
        }

        /// <summary>
        /// Clears all cached Bitmaps, invoking <paramref name="onBeforeEvict"/> before disposing.
        /// </summary>
        public void Clear(Action<int>? onBeforeEvict = null)
        {
            List<KeyValuePair<int, Bitmap>> snapshot;
            lock (_syncLock)
            {
                if (_cache.Count == 0) return;
                snapshot = new List<KeyValuePair<int, Bitmap>>(_cache);
                _cache.Clear();
            }

            var bitmaps = new List<Bitmap>(snapshot.Count);
            foreach (KeyValuePair<int, Bitmap> kvp in snapshot)
            {
                onBeforeEvict?.Invoke(kvp.Key);
                bitmaps.Add(kvp.Value);
            }

            DisposeSafelyOnBackground(bitmaps);
        }

        private static void DisposeSafelyOnBackground(IReadOnlyList<Bitmap> bitmaps)
        {
            if (bitmaps.Count == 0) return;

            // Delay slightly at Background priority so Avalonia/Skia finishes any in-flight render frame
            // before native Bitmap handles are disposed.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(150).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (Bitmap bmp in bitmaps)
                    {
                        try
                        {
                            bmp.Dispose();
                        }
                        catch
                        {
                            // Ignore already-disposed bitmaps during shutdown.
                        }
                    }
                }, DispatcherPriority.Background);
            });
        }
    }
}
