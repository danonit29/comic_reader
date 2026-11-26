using ComicReader.Models;

namespace ComicReader.Services.Interfaces
{
    /// <summary>
    /// Resolves the appropriate IComicReaderService implementation for a comic format.
    /// </summary>
    public interface IComicReaderFactory
    {
        /// <summary>
        /// Creates or returns a reader service supporting the given format.
        /// </summary>
        IComicReaderService GetReader(ComicFormat format);
    }
}
