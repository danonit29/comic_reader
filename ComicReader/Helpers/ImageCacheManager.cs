using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ComicReader.Helpers
{
    /// <summary>
    /// 2-Tier sliding-window cache for comic page Bitmaps:
    /// - Tier 1 (Preview): lightweight ~360px Bitmaps (~0.3-0.5 MB each) for instant slider/scroll display.
    /// - Tier 2 (Full Quality): max 16 full-res Bitmaps (current + KeepBehind + KeepAhead, ~150 MB RAM),
    ///   automatically downgrading evicted pages back to their lightweight preview and disposing full Bitmaps.
    /// </summary>
    public sealed class ImageCacheManager
    {
        private readonly Dictionary<int, Bitmap> _fullCache = new();
        private readonly Dictionary<int, Bitmap> _previewCache = new();
        private readonly Dictionary<int, byte[]> _compressedCache = new();
        private readonly object _syncLock = new();

        public int KeepBehind { get; private set; }
        public int KeepAhead { get; private set; }

        public ImageCacheManager(int keepBehind = 5, int keepAhead = 10)
        {
            KeepBehind = Math.Max(0, keepBehind);
            KeepAhead = Math.Max(0, keepAhead);
        }

        public void ConfigureWindow(int keepBehind, int keepAhead)
        {
            lock (_syncLock)
            {
                KeepBehind = Math.Max(0, keepBehind);
                KeepAhead = Math.Max(0, keepAhead);
            }
        }

        public bool Contains(int pageIndex) => ContainsFull(pageIndex);

        public bool ContainsFull(int pageIndex)
        {
            lock (_syncLock)
            {
                return _fullCache.ContainsKey(pageIndex);
            }
        }

        public bool ContainsPreview(int pageIndex)
        {
            lock (_syncLock)
            {
                return _previewCache.ContainsKey(pageIndex);
            }
        }

        public bool TryGet(int pageIndex, out Bitmap? bitmap) => TryGetFull(pageIndex, out bitmap);

        public bool TryGetFull(int pageIndex, out Bitmap? bitmap)
        {
            lock (_syncLock)
            {
                return _fullCache.TryGetValue(pageIndex, out bitmap);
            }
        }

        public bool TryGetPreview(int pageIndex, out Bitmap? bitmap)
        {
            lock (_syncLock)
            {
                return _previewCache.TryGetValue(pageIndex, out bitmap);
            }
        }

        public bool ContainsCompressed(int pageIndex)
        {
            lock (_syncLock)
            {
                return _compressedCache.ContainsKey(pageIndex);
            }
        }

        public bool TryGetCompressed(int pageIndex, out byte[]? bytes)
        {
            lock (_syncLock)
            {
                return _compressedCache.TryGetValue(pageIndex, out bytes);
            }
        }

        public void SetCompressed(int pageIndex, byte[] bytes)
        {
            lock (_syncLock)
            {
                _compressedCache[pageIndex] = bytes;
            }
        }

        public void EvictCompressedOutsideWindow(int centerIndex, int keepBehind, int keepAhead)
        {
            int minKeep = centerIndex - keepBehind;
            int maxKeep = centerIndex + keepAhead;
            lock (_syncLock)
            {
                if (_compressedCache.Count == 0) return;
                var keysToRemove = new List<int>();
                foreach (int key in _compressedCache.Keys)
                {
                    if (key < minKeep || key > maxKeep)
                        keysToRemove.Add(key);
                }
                foreach (int key in keysToRemove)
                {
                    _compressedCache.Remove(key);
                }
            }
        }

        public void Set(int pageIndex, Bitmap bitmap) => SetFull(pageIndex, bitmap);

        public void SetFull(int pageIndex, Bitmap bitmap)
        {
            Bitmap? replaced = null;
            lock (_syncLock)
            {
                if (_fullCache.TryGetValue(pageIndex, out Bitmap? existing) && !ReferenceEquals(existing, bitmap))
                {
                    replaced = existing;
                }

                _fullCache[pageIndex] = bitmap;
            }

            if (replaced is not null)
            {
                DisposeSafelyOnBackground(new[] { replaced });
            }
        }

        public void SetPreview(int pageIndex, Bitmap bitmap)
        {
            Bitmap? replaced = null;
            lock (_syncLock)
            {
                if (_previewCache.TryGetValue(pageIndex, out Bitmap? existing) && !ReferenceEquals(existing, bitmap))
                {
                    replaced = existing;
                }

                _previewCache[pageIndex] = bitmap;
            }

            if (replaced is not null)
            {
                DisposeSafelyOnBackground(new[] { replaced });
            }
        }

        /// <summary>
        /// Evicts full-res Bitmaps outside [centerIndex - KeepBehind, centerIndex + KeepAhead].
        /// Passes each evicted index and its lightweight preview fallback (or null) to <paramref name="onFullEvicted"/>
        /// on the UI thread before disposing the full-res Bitmap.
        /// </summary>
        public void EvictOutsideWindow(int centerIndex, Action<int, Bitmap?>? onFullEvicted = null)
        {
            int minKeep = centerIndex - KeepBehind;
            int maxKeep = centerIndex + KeepAhead;
            var toEvict = new List<(int Index, Bitmap Full, Bitmap? PreviewFallback)>();

            lock (_syncLock)
            {
                foreach (KeyValuePair<int, Bitmap> kvp in _fullCache)
                {
                    if (kvp.Key < minKeep || kvp.Key > maxKeep)
                    {
                        _previewCache.TryGetValue(kvp.Key, out Bitmap? previewFallback);
                        toEvict.Add((kvp.Key, kvp.Value, previewFallback));
                    }
                }

                foreach (var item in toEvict)
                {
                    _fullCache.Remove(item.Index);
                }
            }

            if (toEvict.Count == 0) return;

            var bitmaps = new List<Bitmap>(toEvict.Count);
            foreach (var item in toEvict)
            {
                onFullEvicted?.Invoke(item.Index, item.PreviewFallback);
                bitmaps.Add(item.Full);
            }

            DisposeSafelyOnBackground(bitmaps);
        }

        /// <summary>
        /// Evicts preview Bitmaps outside [centerIndex - keepBehind, centerIndex + keepAhead] for huge (>200 page) books.
        /// </summary>
        public void EvictPreviewsOutsideWindow(int centerIndex, int keepBehind, int keepAhead, Action<int, Bitmap?>? onPreviewEvicted = null)
        {
            int minKeep = centerIndex - keepBehind;
            int maxKeep = centerIndex + keepAhead;
            var toEvict = new List<(int Index, Bitmap Preview, Bitmap? FullFallback)>();

            lock (_syncLock)
            {
                foreach (KeyValuePair<int, Bitmap> kvp in _previewCache)
                {
                    if (kvp.Key < minKeep || kvp.Key > maxKeep)
                    {
                        _fullCache.TryGetValue(kvp.Key, out Bitmap? fullFallback);
                        toEvict.Add((kvp.Key, kvp.Value, fullFallback));
                    }
                }

                foreach (var item in toEvict)
                {
                    _previewCache.Remove(item.Index);
                }
            }

            if (toEvict.Count == 0) return;

            var bitmaps = new List<Bitmap>(toEvict.Count);
            foreach (var item in toEvict)
            {
                onPreviewEvicted?.Invoke(item.Index, item.FullFallback);
                bitmaps.Add(item.Preview);
            }

            DisposeSafelyOnBackground(bitmaps);
        }

        /// <summary>
        /// Clears all cached Full and Preview Bitmaps, invoking <paramref name="onBeforeEvict"/> before disposing.
        /// </summary>
        public void Clear(Action<int>? onBeforeEvict = null)
        {
            var indices = new HashSet<int>();
            var bitmaps = new List<Bitmap>();

            lock (_syncLock)
            {
                foreach (KeyValuePair<int, Bitmap> kvp in _fullCache)
                {
                    indices.Add(kvp.Key);
                    bitmaps.Add(kvp.Value);
                }
                foreach (KeyValuePair<int, Bitmap> kvp in _previewCache)
                {
                    indices.Add(kvp.Key);
                    bitmaps.Add(kvp.Value);
                }

                _fullCache.Clear();
                _previewCache.Clear();
                _compressedCache.Clear();
            }

            if (indices.Count == 0 && bitmaps.Count == 0) return;

            foreach (int idx in indices)
            {
                onBeforeEvict?.Invoke(idx);
            }

            DisposeSafelyOnBackground(bitmaps);
        }

        private static int _disposedSinceGc;

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

                    if (System.Threading.Interlocked.Add(ref _disposedSinceGc, bitmaps.Count) >= 16)
                    {
                        System.Threading.Interlocked.Exchange(ref _disposedSinceGc, 0);
                        GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                    }
                }, DispatcherPriority.Background);
            });
        }
    }
}
