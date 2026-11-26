using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ComicReader.Helpers;
using ComicReader.Services.Interfaces;

namespace ComicReader.Services
{
    /// <summary>
    /// Reads comic pages in-memory from ZIP/CBZ archives and decodes them into Avalonia Bitmaps.
    /// </summary>
    public sealed class ZipService : IComicReaderService
    {
        private static readonly NaturalStringComparer NaturalComparer = new();

        /// <inheritdoc />
        public Task<IComicReadSession> OpenSessionAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.Run<IComicReadSession>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ZipReadSession(filePath);
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
            private readonly ZipArchive _archive;
            private readonly List<ZipArchiveEntry> _entries;
            private readonly object _archiveLock = new();
            private bool _disposed;

            public ZipReadSession(string filePath)
            {
                _archive = ZipFile.OpenRead(filePath);
                _entries = GetSortedImageEntries(_archive);
                PageCount = _entries.Count;
            }

            public int PageCount { get; }

            public Task<Bitmap?> RenderPageAsync(
                int pageIndex,
                int dpi = AppConstants.DefaultRenderDpi,
                CancellationToken cancellationToken = default)
            {
                return Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pageIndex < 0 || pageIndex >= _entries.Count) return null;

                    byte[] imageBytes;
                    lock (_archiveLock)
                    {
                        if (_disposed) return null;
                        imageBytes = ReadEntryBytes(_entries[pageIndex]);
                    }

                    return ImageHelper.DecodeForReader(imageBytes);
                }, cancellationToken);
            }

            public void Dispose()
            {
                lock (_archiveLock)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _archive.Dispose();
                }
            }
        }
    }
}
