using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ComicReader.Helpers;
using ComicReader.Models;
using ComicReader.Services.Interfaces;
using ComicReader.Services.Native;

namespace ComicReader.Services
{
    /// <summary>
    /// Reads comic pages in-memory from ZIP/CBZ archives and discovers chapters (&lt; 200 pages each).
    /// </summary>
    public sealed class ZipService : IComicReaderService
    {
        private static readonly NaturalStringComparer NaturalComparer = new();
        private readonly IComicEngine _engine;

        public ZipService(IComicEngine? engine = null)
        {
            _engine = engine ?? new RustComicEngine();
        }

        /// <summary>
        /// Inspects a ZIP/CBZ archive and splits it into chapters by internal subfolder structure
        /// and by the strict &lt; 200 pages-per-chapter limit.
        /// </summary>
        public static List<ChapterModel> DiscoverChapters(
            string filePath,
            ComicFormat format = ComicFormat.Zip,
            List<string>? warnings = null,
            int maxPagesPerChapter = AppConstants.MaxPagesPerChapter)
        {
            var chapters = new List<ChapterModel>();
            if (!File.Exists(filePath)) return chapters;

            using ZipArchive archive = ZipFile.OpenRead(filePath);
            List<ZipArchiveEntry> entries = GetSortedImageEntries(archive);
            if (entries.Count == 0) return chapters;

            // Group entries by normalized internal directory path.
            var grouped = entries
                .GroupBy(e => GetNormalizedEntryDirectory(e.FullName), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, NaturalComparer)
                .ToList();

            string archiveName = Path.GetFileNameWithoutExtension(filePath);
            int orderIndex = 0;

            if (grouped.Count <= 1)
            {
                string key = grouped[0].Key;
                string baseTitle = string.IsNullOrEmpty(key) ? archiveName : GetReadableGroupTitle(key, string.Empty);
                List<string> entryNames = grouped[0].Select(static e => e.FullName).ToList();

                chapters.AddRange(ChapterSplitter.SplitItemsIntoChapters(
                    filePath,
                    baseTitle,
                    string.IsNullOrEmpty(key) ? archiveName : key,
                    entryNames,
                    format,
                    static (ch, names) => ch.ZipEntryNames = names,
                    ref orderIndex,
                    warnings,
                    maxPagesPerChapter));

                return chapters;
            }

            // Strip common top-level directory prefix if all groups live under one root folder in the ZIP.
            string commonPrefix = FindCommonTopPrefix(grouped.Select(static g => g.Key).ToList());

            foreach (IGrouping<string, ZipArchiveEntry> group in grouped)
            {
                string title = string.IsNullOrEmpty(group.Key)
                    ? $"{archiveName} (Root)"
                    : GetReadableGroupTitle(group.Key, commonPrefix);

                List<string> entryNames = group.Select(static e => e.FullName).ToList();
                string chapterKey = string.IsNullOrEmpty(group.Key) ? "__root__" : group.Key;

                chapters.AddRange(ChapterSplitter.SplitItemsIntoChapters(
                    filePath,
                    title,
                    chapterKey,
                    entryNames,
                    format,
                    static (ch, names) => ch.ZipEntryNames = names,
                    ref orderIndex,
                    warnings,
                    maxPagesPerChapter));
            }

            return chapters;
        }

        /// <inheritdoc />
        public Task<IComicReadSession> OpenSessionAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.Run<IComicReadSession>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ZipReadSession(filePath, allowedEntryNames: null, _engine);
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<IComicReadSession> OpenChapterSessionAsync(
            string sourcePath,
            ChapterModel chapter,
            CancellationToken cancellationToken = default)
        {
            return Task.Run<IComicReadSession>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<string>? filter = chapter.ZipEntryNames.Count > 0
                    ? chapter.ZipEntryNames
                    : null;
                return new ZipReadSession(sourcePath, filter, _engine);
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<int> GetPageCountAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using ZipArchive archive = ZipFile.OpenRead(filePath);
                return GetSortedImageEntries(archive).Count;
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<Bitmap?> RenderPageAsync(
            string filePath,
            int pageIndex,
            int dpi = AppConstants.DefaultRenderDpi,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using ZipArchive archive = ZipFile.OpenRead(filePath);
                List<ZipArchiveEntry> entries = GetSortedImageEntries(archive);
                if (pageIndex < 0 || pageIndex >= entries.Count) return null;

                byte[] imageBytes = ReadEntryBytes(entries[pageIndex]);
                return ImageHelper.DecodeForReader(imageBytes);
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<Bitmap?> ExtractCoverAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using ZipArchive archive = ZipFile.OpenRead(filePath);
                List<ZipArchiveEntry> entries = GetSortedImageEntries(archive);
                if (entries.Count == 0) return null;

                byte[] imageBytes = ReadEntryBytes(entries[0]);
                return ImageHelper.DecodeFromBytes(imageBytes, AppConstants.CoverDecodeWidth);
            }, cancellationToken);
        }

        private static string GetNormalizedEntryDirectory(string fullName)
        {
            string normalized = fullName.Replace('\\', '/');
            int lastSlash = normalized.LastIndexOf('/');
            return lastSlash <= 0 ? string.Empty : normalized[..lastSlash];
        }

        private static string FindCommonTopPrefix(List<string> keys)
        {
            if (keys.Count == 0 || keys.Any(string.IsNullOrEmpty)) return string.Empty;

            string first = keys[0];
            int firstSlash = first.IndexOf('/');
            if (firstSlash <= 0) return string.Empty;

            string candidate = first[..(firstSlash + 1)];
            return keys.All(k => k.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                ? candidate
                : string.Empty;
        }

        private static string GetReadableGroupTitle(string key, string commonPrefix)
        {
            string trimmed = !string.IsNullOrEmpty(commonPrefix) &&
                             key.StartsWith(commonPrefix, StringComparison.OrdinalIgnoreCase)
                ? key[commonPrefix.Length..]
                : key;

            return string.IsNullOrWhiteSpace(trimmed)
                ? key
                : trimmed.Replace("/", " / ");
        }

        private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
        {
            using Stream entryStream = entry.Open();
            using var memoryStream = new MemoryStream();
            entryStream.CopyTo(memoryStream);
            return memoryStream.ToArray();
        }

        private static List<ZipArchiveEntry> GetSortedImageEntries(ZipArchive archive)
        {
            return archive.Entries
                .Where(IsSupportedImageEntry)
                .OrderBy(entry => entry.FullName, NaturalComparer)
                .ToList();
        }

        private static bool IsSupportedImageEntry(ZipArchiveEntry entry)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                return false;
            }

            string extension = Path.GetExtension(entry.Name);
            return AppConstants.SupportedImageExtensions.Contains(extension);
        }

        private sealed class ZipReadSession : IComicReadSession
        {
            private readonly IComicEngine _engine;
            private readonly ZipArchive? _archive;
            private readonly List<ZipArchiveEntry>? _entries;
            private readonly object _archiveLock = new();
            private SafeDocumentHandle? _nativeHandle;
            private bool _disposed;

            public ZipReadSession(string filePath, IReadOnlyList<string>? allowedEntryNames, IComicEngine engine)
            {
                _engine = engine;

                if (_engine.IsAvailable)
                {
                    _nativeHandle = _engine.OpenZipDocument(filePath, allowedEntryNames);
                    if (_nativeHandle is not null && !_nativeHandle.IsInvalid)
                    {
                        PageCount = _engine.GetPageCount(_nativeHandle);
                        return;
                    }

                    _nativeHandle?.Dispose();
                    _nativeHandle = null;
                }

                _archive = ZipFile.OpenRead(filePath);
                List<ZipArchiveEntry> allSorted = GetSortedImageEntries(_archive);

                if (allowedEntryNames is { Count: > 0 })
                {
                    var allowedSet = new HashSet<string>(allowedEntryNames, StringComparer.OrdinalIgnoreCase);
                    _entries = allSorted.Where(e => allowedSet.Contains(e.FullName)).ToList();
                }
                else
                {
                    _entries = allSorted;
                }

                PageCount = _entries.Count;
            }

            public int PageCount { get; }

            public Task<byte[]?> ExtractRawBytesAsync(int pageIndex, CancellationToken cancellationToken = default)
            {
                return Task.Run<byte[]?>(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed) return null;

                    if (_nativeHandle is { IsInvalid: false, IsClosed: false })
                    {
                        return _engine.GetPageRawBytes(_nativeHandle, pageIndex);
                    }

                    if (_entries is null || pageIndex < 0 || pageIndex >= _entries.Count) return null;
                    lock (_archiveLock)
                    {
                        if (_disposed) return null;
                        return ReadEntryBytes(_entries[pageIndex]);
                    }
                }, cancellationToken);
            }

            public Task<Bitmap?> DecodeFromRawBytesAsync(byte[] rawBytes, CancellationToken cancellationToken = default)
            {
                return Task.Run<Bitmap?>(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_engine.IsAvailable)
                    {
                        Bitmap? nativeBmp = _engine.DecodeImageBytes(rawBytes, AppConstants.MaxReaderDecodeWidth);
                        if (nativeBmp is not null) return nativeBmp;
                    }
                    return ImageHelper.DecodeFromBytes(rawBytes, null);
                }, cancellationToken);
            }

            public Task<Bitmap?> RenderPageAsync(
                int pageIndex,
                int dpi = AppConstants.DefaultRenderDpi,
                CancellationToken cancellationToken = default)
                => RenderCoreAsync(pageIndex, dpi, AppConstants.MaxReaderDecodeWidth, cancellationToken);

            public Task<Bitmap?> RenderPreviewAsync(
                int pageIndex,
                int maxWidth = AppConstants.PreviewDecodeWidth,
                CancellationToken cancellationToken = default)
                => RenderCoreAsync(pageIndex, AppConstants.PreviewRenderDpi, maxWidth, cancellationToken);

            private Task<Bitmap?> RenderCoreAsync(
                int pageIndex,
                int dpi,
                int maxWidth,
                CancellationToken cancellationToken)
            {
                return Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (_nativeHandle is { IsInvalid: false, IsClosed: false })
                    {
                        if (_disposed) return null;
                        return _engine.RenderPageToBitmap(_nativeHandle, pageIndex, (uint)maxWidth);
                    }

                    if (_entries is null || pageIndex < 0 || pageIndex >= _entries.Count) return null;

                    byte[] imageBytes;
                    lock (_archiveLock)
                    {
                        if (_disposed) return null;
                        imageBytes = ReadEntryBytes(_entries[pageIndex]);
                    }

                    return maxWidth > 0
                        ? ImageHelper.DecodeFromBytes(imageBytes, maxWidth)
                        : ImageHelper.DecodeFromBytes(imageBytes, null);
                }, cancellationToken);
            }

            public void Dispose()
            {
                lock (_archiveLock)
                {
                    if (_disposed) return;
                    _disposed = true;

                    _nativeHandle?.Dispose();
                    _nativeHandle = null;

                    _archive?.Dispose();
                }
            }
        }
    }
}
