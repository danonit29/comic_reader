using System.Threading.Tasks;
using ComicReader.Models;

namespace ComicReader.Services.Interfaces
{
    /// <summary>
    /// Persists and retrieves library metadata, reading progress, and bookmarks.
    /// </summary>
    public interface IStorageService
    {
        /// <summary>
        /// Loads the library store from JSON storage.
        /// </summary>
        Task<LibraryStore> LoadAsync();

        /// <summary>
        /// Saves the entire library store to JSON storage.
        /// </summary>
        Task SaveAsync(LibraryStore store);

        /// <summary>
        /// Updates reading progress for a comic and persists the change.
        /// </summary>
        Task UpdateProgressAsync(ReadingProgress progress);

        /// <summary>
        /// Adds a bookmark to a comic's reading progress and persists the change.
        /// </summary>
        Task AddBookmarkAsync(string comicId, Bookmark bookmark);

        /// <summary>
        /// Retrieves saved reading progress for a specific comic, if available.
        /// </summary>
        Task<ReadingProgress?> GetProgressAsync(string comicId);
    }
}
