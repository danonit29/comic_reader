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
    /// Persists page-level bookmarks to a dedicated JSON file (bookmarks.json).
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

        private static string BookmarksFilePath => Path.Combine(AppConstants.AppDataDirectory, BookmarksFileName);

        /// <inheritdoc />
        public async Task<IReadOnlyList<Bookmark>> GetBookmarksAsync(string comicId)
        {
            await _ioLock.WaitAsync();
            try
            {
                List<Bookmark> all = await LoadAllInternalAsync();
                return all.Where(b => b.ComicId == comicId)
                          .OrderBy(b => b.PageNumber)
                          .ToList();
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task SaveBookmarkAsync(Bookmark bookmark)
        {
            await _ioLock.WaitAsync();
            try
            {
                List<Bookmark> all = await LoadAllInternalAsync();
                all.RemoveAll(b => b.ComicId == bookmark.ComicId && b.PageNumber == bookmark.PageNumber);
                bookmark.CreatedAtUtc = DateTime.UtcNow;
                all.Add(bookmark);
                await SaveAllInternalAsync(all);
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task RemoveBookmarkAsync(string comicId, int pageNumber)
        {
            await _ioLock.WaitAsync();
            try
            {
                List<Bookmark> all = await LoadAllInternalAsync();
                int removed = all.RemoveAll(b => b.ComicId == comicId && b.PageNumber == pageNumber);
                if (removed > 0)
                {
                    await SaveAllInternalAsync(all);
                }
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private static async Task<List<Bookmark>> LoadAllInternalAsync()
        {
            if (!File.Exists(BookmarksFilePath))
            {
                return new List<Bookmark>();
            }

            try
            {
                var info = new FileInfo(BookmarksFilePath);
                if (info.Length == 0)
                {
                    return new List<Bookmark>();
                }

                await using var stream = File.OpenRead(BookmarksFilePath);
                return await JsonSerializer.DeserializeAsync<List<Bookmark>>(stream, JsonOptions) ?? new List<Bookmark>();
            }
            catch (JsonException)
            {
                return new List<Bookmark>();
            }
        }

        private static async Task SaveAllInternalAsync(List<Bookmark> bookmarks)
        {
            Directory.CreateDirectory(AppConstants.AppDataDirectory);
            string tempPath = BookmarksFilePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, bookmarks, JsonOptions);
                await stream.FlushAsync();
            }

            File.Move(tempPath, BookmarksFilePath, overwrite: true);
        }
    }
}
