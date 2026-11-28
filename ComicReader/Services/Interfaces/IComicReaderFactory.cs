using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComicReader.Models;

namespace ComicReader.Services.Interfaces
{
    /// <summary>
    /// Resolves the appropriate IComicReaderService implementation for a comic format
    /// and discovers chapters across Folder, ZIP/CBZ, and PDF sources.
    /// </summary>
    public interface IComicReaderFactory
    {
        /// <summary>
        /// Creates or returns a reader service supporting the given format.
        /// </summary>
        IComicReaderService GetReader(ComicFormat format);

        /// <summary>
        /// Discovers chapters (&lt; 200 pages each) for the given comic regardless of format.
        /// </summary>
        Task<IReadOnlyList<ChapterModel>> DiscoverChaptersAsync(
            ComicBook comic,
            CancellationToken cancellationToken = default);
    }
}
