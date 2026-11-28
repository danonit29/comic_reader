using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComicReader.Helpers;
using ComicReader.Models;
using ComicReader.Services.Interfaces;

namespace ComicReader.Services
{
    /// <summary>
    /// Persists page-level bookmarks to a dedicated JSON file (bookmarks.json),
    /// keyed uniquely by (ComicId, ChapterId, PageNumber) so multiple bookmarks coexist cleanly.
    /// </summary>
    public sealed class BookmarkService : IBookmarkService
    {
        private const string BookmarksFileName = "bookmarks.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly SemaphoreSlim _ioLock = new(1, 1);
        private readonly string _bookmarksFilePath;

        /// <summary>
        /// Initializes a new instance of <see cref="BookmarkService"/>.
        /// </summary>
        /// <param name="bookmarksFilePath">Optional custom JSON path (used in tests); defaults to AppData/bookmarks.json.</param>
        public BookmarkService(string? bookmarksFilePath = null)
        {
            _bookmarksFilePath = string.IsNullOrWhiteSpace(bookmarksFilePath)
                ? Path.Combine(AppConstants.AppDataDirectory, BookmarksFileName)
                : bookmarksFilePath;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<Bookmark>> GetBookmarksAsync(string comicId)
        {
            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                List<Bookmark> all = await LoadAllInternalAsync().ConfigureAwait(false);
                return all.Where(b => string.Equals(b.ComicId, comicId, StringComparison.Ordinal))
                          .OrderBy(b => b.CreatedAtUtc)
                          .ThenBy(b => b.PageNumber)
                          .ToList();
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<bool> SaveBookmarkAsync(Bookmark bookmark)
        {
            ArgumentNullException.ThrowIfNull(bookmark);

            string chapterId = NormalizeChapterId(bookmark.ComicId, bookmark.ChapterId);
            bookmark.ChapterId = chapterId;
            if (string.IsNullOrWhiteSpace(bookmark.BookmarkId))
            {
                bookmark.BookmarkId = Guid.NewGuid().ToString("N");
            }

            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                List<Bookmark> all = await LoadAllInternalAsync().ConfigureAwait(false);

                Bookmark? existing = all.FirstOrDefault(b => IsSameTarget(b, bookmark.ComicId, chapterId, bookmark.PageNumber));
                if (existing is not null)
                {
                    all.Remove(existing);
                    await SaveAllInternalAsync(all).ConfigureAwait(false);
                    return false;
                }

                bookmark.CreatedAtUtc = DateTime.UtcNow;
                all.Add(bookmark);
                await SaveAllInternalAsync(all).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task RemoveBookmarkAsync(string comicId, string chapterId, int pageNumber)
        {
            string normalizedChapterId = NormalizeChapterId(comicId, chapterId);

            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                List<Bookmark> all = await LoadAllInternalAsync().ConfigureAwait(false);
                int removed = all.RemoveAll(b => IsSameTarget(b, comicId, normalizedChapterId, pageNumber));
                if (removed > 0)
                {
                    await SaveAllInternalAsync(all).ConfigureAwait(false);
                }
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task RemoveBookmarkByIdAsync(string comicId, string bookmarkId)
        {
            if (string.IsNullOrWhiteSpace(bookmarkId)) return;

            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                List<Bookmark> all = await LoadAllInternalAsync().ConfigureAwait(false);
                int removed = all.RemoveAll(b =>
                    string.Equals(b.ComicId, comicId, StringComparison.Ordinal) &&
                    string.Equals(b.BookmarkId, bookmarkId, StringComparison.Ordinal));

                if (removed > 0)
                {
                    await SaveAllInternalAsync(all).ConfigureAwait(false);
                }
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task RemoveBookmarkAsync(string comicId, int pageNumber)
        {
            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                List<Bookmark> all = await LoadAllInternalAsync().ConfigureAwait(false);
                int removed = all.RemoveAll(b =>
                    string.Equals(b.ComicId, comicId, StringComparison.Ordinal) &&
                    b.PageNumber == pageNumber);

                if (removed > 0)
                {
                    await SaveAllInternalAsync(all).ConfigureAwait(false);
                }
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private static string NormalizeChapterId(string comicId, string? chapterId) =>
            string.IsNullOrWhiteSpace(chapterId) ? comicId : chapterId;

        private static bool IsSameTarget(Bookmark b, string comicId, string chapterId, int pageNumber)
        {
            string existingChapter = NormalizeChapterId(b.ComicId, b.ChapterId);
            return string.Equals(b.ComicId, comicId, StringComparison.Ordinal) &&
                   string.Equals(existingChapter, chapterId, StringComparison.Ordinal) &&
                   b.PageNumber == pageNumber;
        }

        private async Task<List<Bookmark>> LoadAllInternalAsync()
        {
            if (!File.Exists(_bookmarksFilePath))
            {
                return new List<Bookmark>();
            }

            try
            {
                var info = new FileInfo(_bookmarksFilePath);
                if (info.Length == 0)
                {
                    return new List<Bookmark>();
                }

                await using var stream = File.OpenRead(_bookmarksFilePath);
                return await JsonSerializer.DeserializeAsync<List<Bookmark>>(stream, JsonOptions).ConfigureAwait(false)
                       ?? new List<Bookmark>();
            }
            catch (JsonException)
            {
                return new List<Bookmark>();
            }
        }

        private async Task SaveAllInternalAsync(List<Bookmark> bookmarks)
        {
            string? dir = Path.GetDirectoryName(_bookmarksFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tempPath = _bookmarksFilePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, bookmarks, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            File.Move(tempPath, _bookmarksFilePath, overwrite: true);
        }
    }
}
