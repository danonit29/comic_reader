using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ComicReader.Helpers;

namespace ComicReader.Models
{
    /// <summary>
    /// Represents a single readable chapter inside a multi-level folder, ZIP/CBZ archive, or PDF.
    /// Guaranteed by chapter-splitting rules to contain fewer than <see cref="AppConstants.MaxPagesPerChapter"/> + 1
    /// (strictly less than 200) pages.
    /// </summary>
    public sealed class ChapterModel
    {
        /// <summary>
        /// Deterministic or unique identifier for this chapter (used to isolate bookmarks per chapter).
        /// </summary>
        public string ChapterId { get; set; } = string.Empty;

        /// <summary>
        /// Display title of the chapter (e.g., "Chuong 1", "Chuong 2.1").
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// 0-based order index of this chapter within the comic.
        /// </summary>
        public int OrderIndex { get; set; }

        /// <summary>
        /// Source format of the chapter.
        /// </summary>
        public ComicFormat Format { get; set; }

        /// <summary>
        /// Physical path to the chapter folder (for Folder format) or container file (for ZIP/CBZ/PDF).
        /// </summary>
        public string SourcePath { get; set; } = string.Empty;

        /// <summary>
        /// Ordered list of image file paths when <see cref="Format"/> is <see cref="ComicFormat.Folder"/>.
        /// </summary>
        public List<string> ImagePaths { get; set; } = new();

        /// <summary>
        /// Ordered list of archive entry full names when <see cref="Format"/> is <see cref="ComicFormat.Zip"/> or <see cref="ComicFormat.Cbz"/>.
        /// </summary>
        public List<string> ZipEntryNames { get; set; } = new();

        /// <summary>
        /// 0-based inclusive start page index inside a PDF file (for <see cref="ComicFormat.Pdf"/>).
        /// </summary>
        public int StartPageIndex { get; set; }

        /// <summary>
        /// Total number of pages in this chapter (always &lt; 200 after chapter splitting).
        /// </summary>
        public int PageCount { get; set; }

        /// <summary>
        /// Optional warning recorded when an oversized source chapter (&gt;= 200 pages) was split.
        /// </summary>
        public string? SplitWarning { get; set; }

        /// <summary>
        /// Formatted label shown in the reader chapter selector.
        /// </summary>
        [JsonIgnore]
        public string DisplayTitle => PageCount > 0 ? $"{Title} ({PageCount} tr.)" : Title;

        public override string ToString() => DisplayTitle;
    }
}
