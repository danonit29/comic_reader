using System.Collections.Generic;
using System.Threading.Tasks;
using ComicReader.Models;

namespace ComicReader.Services.Interfaces
{
    /// <summary>
    /// Manages saving, toggling, and querying page-level bookmarks in JSON storage.
    /// </summary>
    public interface IBookmarkService
    {
        /// <summary>
        /// Returns all bookmarks saved for the specified comic across all chapters.
        /// </summary>
        Task<IReadOnlyList<Bookmark>> GetBookmarksAsync(string comicId);

        /// <summary>
        /// Checks whether a bookmark exists for (ComicId, ChapterId, PageNumber).
        /// If not present, adds it; if already present, toggles (removes) it.
        /// Returns <c>true</c> if added, <c>false</c> if removed.
        /// </summary>
        Task<bool> SaveBookmarkAsync(Bookmark bookmark);

        /// <summary>
        /// Removes a bookmark matching the given comic, chapter, and page number.
        /// </summary>
        Task RemoveBookmarkAsync(string comicId, string chapterId, int pageNumber);

        /// <summary>
        /// Removes a bookmark by its unique <see cref="Bookmark.BookmarkId"/>.
        /// </summary>
        Task RemoveBookmarkByIdAsync(string comicId, string bookmarkId);

        /// <summary>
        /// Removes bookmarks matching the given comic and page number across chapters.
        /// </summary>
        Task RemoveBookmarkAsync(string comicId, int pageNumber);
    }
}
