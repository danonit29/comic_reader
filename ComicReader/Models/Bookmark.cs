using System;

namespace ComicReader.Models
{
    /// <summary>
    /// Represents a user-saved bookmark at an exact comic, chapter, and page number.
    /// </summary>
    public sealed class Bookmark
    {
        /// <summary>
        /// Unique identifier for the bookmark.
        /// </summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Identifier of the associated comic book.
        /// </summary>
        public string ComicId { get; set; } = string.Empty;

        /// <summary>
        /// Identifier of the chapter or parent folder (falls back to ComicId for standalone files).
        /// </summary>
        public string ChapterId { get; set; } = string.Empty;

        /// <summary>
        /// 1-based page number where the bookmark was placed.
        /// </summary>
        public int PageNumber { get; set; }

        /// <summary>
        /// Normalized vertical offset (0.0 to 1.0) within the bookmarked page.
        /// </summary>
        public double PageLocalOffsetRatio { get; set; }

        /// <summary>
        /// Optional user note for the bookmark.
        /// </summary>
        public string Note { get; set; } = string.Empty;

        /// <summary>
        /// UTC timestamp when the bookmark was created.
        /// </summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
