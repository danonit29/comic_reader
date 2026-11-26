using System.Collections.Generic;

namespace ComicReader.Models
{
    /// <summary>
    /// Root serialization model persisted to JSON.
    /// </summary>
    public sealed class LibraryStore
    {
        /// <summary>
        /// List of imported comics in the user's library.
        /// </summary>
        public List<ComicBook> Comics { get; set; } = new();

        /// <summary>
        /// List of imported comic folders.
        /// </summary>
        public List<ComicFolder> Folders { get; set; } = new();

        /// <summary>
        /// Reading progress indexed by ComicId.
        /// </summary>
        public Dictionary<string, ReadingProgress> ProgressByComicId { get; set; } = new();
    }
}
