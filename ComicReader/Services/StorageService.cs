using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComicReader.Helpers;
using ComicReader.Models;
using ComicReader.Services.Interfaces;

namespace ComicReader.Services
{
    /// <summary>
    /// Persists library metadata, reading progress, and bookmarks as JSON.
    /// </summary>
    public sealed class StorageService : IStorageService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly SemaphoreSlim _ioLock = new(1, 1);

        /// <inheritdoc />
        public async Task<LibraryStore> LoadAsync()
        {
            await _ioLock.WaitAsync();
            try
            {
                return await LoadInternalAsync();
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task SaveAsync(LibraryStore store)
        {
            await _ioLock.WaitAsync();
            try
            {
                await SaveInternalAsync(store);
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task UpdateProgressAsync(ReadingProgress progress)
        {
            await _ioLock.WaitAsync();
            try
            {
                LibraryStore store = await LoadInternalAsync();
                progress.LastReadAtUtc = DateTime.UtcNow;

                if (store.ProgressByComicId.TryGetValue(progress.ComicId, out ReadingProgress? existing))
                {
                    progress.Bookmarks = existing.Bookmarks;
                }

                store.ProgressByComicId[progress.ComicId] = progress;
                await SaveInternalAsync(store);
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task AddBookmarkAsync(string comicId, Bookmark bookmark)
        {
            await _ioLock.WaitAsync();
            try
            {
                LibraryStore store = await LoadInternalAsync();
                if (!store.ProgressByComicId.TryGetValue(comicId, out ReadingProgress? progress))
                {
                    progress = new ReadingProgress { ComicId = comicId };
                    store.ProgressByComicId[comicId] = progress;
                }

                progress.Bookmarks.Add(bookmark);
                await SaveInternalAsync(store);
            }
            finally
            {
                _ioLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<ReadingProgress?> GetProgressAsync(string comicId)
        {
            await _ioLock.WaitAsync();
            try
            {
                LibraryStore store = await LoadInternalAsync();
                return store.ProgressByComicId.TryGetValue(comicId, out ReadingProgress? progress)
                    ? progress
                    : null;
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private static Task<LibraryStore> LoadInternalAsync()
        {
            return Task.Run(async () =>
            {
                if (!File.Exists(AppConstants.LibraryDataFilePath))
                {
                    return new LibraryStore();
                }

                try
                {
                    var info = new FileInfo(AppConstants.LibraryDataFilePath);
                    if (info.Length == 0)
                    {
                        return new LibraryStore();
                    }

                    await using var stream = File.OpenRead(AppConstants.LibraryDataFilePath);
                    return await JsonSerializer.DeserializeAsync<LibraryStore>(stream, JsonOptions).ConfigureAwait(false)
                           ?? new LibraryStore();
                }
                catch (JsonException)
                {
                    return new LibraryStore();
                }
            });
        }

        private static Task SaveInternalAsync(LibraryStore store)
        {
            return Task.Run(async () =>
            {
                Directory.CreateDirectory(AppConstants.AppDataDirectory);
                string tempPath = AppConstants.LibraryDataFilePath + ".tmp";
                await using (var stream = File.Create(tempPath))
                {
                    await JsonSerializer.SerializeAsync(stream, store, JsonOptions).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }

                File.Move(tempPath, AppConstants.LibraryDataFilePath, overwrite: true);
            });
        }
    }
}
