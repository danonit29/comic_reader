using System;
using System.Collections.Generic;

namespace ComicReader.Models
{
    /// <summary>
    /// Tracks reading progress and bookmarks for a single comic book.
    /// </summary>
    public sealed class ReadingProgress
    {
        /// <summary>
        /// Identifier of the associated comic book.
        /// </summary>
        public string ComicId { get; set; } = string.Empty;

        /// <summary>
        /// Identifier of the last read chapter (empty for single-chapter books).
        /// </summary>
        public string CurrentChapterId { get; set; } = string.Empty;

        /// <summary>
        /// 1-based index of the last read page.
        /// </summary>
        public int CurrentPage { get; set; } = 1;

        /// <summary>
        /// Total number of pages in the comic.
        /// </summary>
        public int TotalPages { get; set; }

        /// <summary>
        /// Normalized vertical scroll position (0.0 to 1.0) for exact resume on reopen.
        /// </summary>
        public double ScrollOffsetRatio { get; set; }

        /// <summary>
        /// Normalized vertical offset (0.0 to 1.0) within the CurrentPage container for zero-drift resume.
        /// </summary>
        public double PageLocalOffsetRatio { get; set; }

        /// <summary>
        /// Last zoom level used when reading this comic.
        /// </summary>
        public double ZoomLevel { get; set; } = 1.0;

        /// <summary>
        /// UTC timestamp of the last reading session.
        /// </summary>
        public DateTime LastReadAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Collection of bookmarks saved for this comic.
        /// </summary>
        public List<Bookmark> Bookmarks { get; set; } = new();
    }
}
