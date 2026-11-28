using System;
using System.Text.Json.Serialization;

namespace ComicReader.Models
{
    /// <summary>
    /// Represents a user-saved bookmark at an exact comic, chapter, and page number.
    /// </summary>
    public sealed class Bookmark
    {
        private string _bookmarkId = Guid.NewGuid().ToString("N");

        /// <summary>
        /// Unique identifier for this bookmark.
        /// </summary>
        public string BookmarkId
        {
            get => _bookmarkId;
            set => _bookmarkId = string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value;
        }

        /// <summary>
        /// Backward-compatible legacy JSON property for reading old "id" fields.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public Guid Id
        {
            get => default;
            set
            {
                if (value != Guid.Empty)
                {
                    _bookmarkId = value.ToString("N");
                }
            }
        }

        /// <summary>
        /// Identifier of the associated comic book.
        /// </summary>
        public string ComicId { get; set; } = string.Empty;

        /// <summary>
        /// Identifier of the chapter (falls back to ComicId for single-chapter standalone files).
        /// </summary>
        public string ChapterId { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable chapter title for display in the bookmarks list.
        /// </summary>
        public string ChapterTitle { get; set; } = string.Empty;

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

        /// <summary>
        /// Formatted display text for the reader bookmark list.
        /// </summary>
        [JsonIgnore]
        public string DisplayLabel => string.IsNullOrWhiteSpace(ChapterTitle)
            ? $"Trang {PageNumber}"
            : $"{ChapterTitle} • Trang {PageNumber}";
    }
}
