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
        /// Returns all bookmarks saved for the specified comic.
        /// </summary>
        Task<IReadOnlyList<Bookmark>> GetBookmarksAsync(string comicId);

        /// <summary>
        /// Saves or updates a bookmark for the exact ComicId, ChapterId, and PageNumber.
        /// </summary>
        Task SaveBookmarkAsync(Bookmark bookmark);

        /// <summary>
        /// Removes a bookmark at the given comic and page number if it exists.
        /// </summary>
        Task RemoveBookmarkAsync(string comicId, int pageNumber);
    }
}
