using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace ComicReader.Services.Interfaces
{
    /// <summary>
    /// Represents an open comic reading session that keeps the underlying file/archive open
    /// for fast random-access page rendering without re-parsing on every page.
    /// </summary>
    public interface IComicReadSession : IDisposable
    {
        /// <summary>
        /// Total number of readable pages in the open comic.
        /// </summary>
        int PageCount { get; }

        /// <summary>
        /// Renders a specific 0-based page index as an Avalonia Bitmap.
        /// </summary>
        Task<Bitmap?> RenderPageAsync(
            int pageIndex,
            int dpi = Helpers.AppConstants.DefaultRenderDpi,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Defines operations for reading pages, opening fast sessions, and extracting cover art.
    /// </summary>
    public interface IComicReaderService
    {
        /// <summary>
        /// Opens a reusable reading session for fast multi-page rendering.
        /// </summary>
        Task<IComicReadSession> OpenSessionAsync(string filePath, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the total number of readable pages in the comic file.
        /// </summary>
        Task<int> GetPageCountAsync(string filePath, CancellationToken cancellationToken = default);

        /// <summary>
        /// Renders a specific 0-based page index as an Avalonia Bitmap.
        /// </summary>
        Task<Bitmap?> RenderPageAsync(
            string filePath,
            int pageIndex,
            int dpi = Helpers.AppConstants.DefaultRenderDpi,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Extracts the first page as a thumbnail cover image.
        /// </summary>
        Task<Bitmap?> ExtractCoverAsync(string filePath, CancellationToken cancellationToken = default);
    }
}
