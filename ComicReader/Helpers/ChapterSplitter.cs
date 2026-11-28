using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ComicReader.Models;

namespace ComicReader.Helpers
{
    /// <summary>
    /// Shared chapter splitting utilities guaranteeing every resulting chapter has
    /// strictly fewer than 200 pages (<see cref="AppConstants.MaxPagesPerChapter"/> = 199 max).
    /// </summary>
    public static class ChapterSplitter
    {
        /// <summary>
        /// Generates a deterministic, stable chapter identifier from the source path, chapter key, and part index.
        /// </summary>
        public static string CreateDeterministicChapterId(string sourcePath, string chapterKey, int partIndex = 1)
        {
            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                normalizedPath = sourcePath;
            }

            string raw = $"{normalizedPath.ToLowerInvariant()}|{chapterKey.ToLowerInvariant()}|{partIndex}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
        }

        /// <summary>
        /// Splits a flat list of page items (file paths or ZIP entry names) into one or more
        /// <see cref="ChapterModel"/> instances, each containing at most <paramref name="maxPagesPerChapter"/> pages.
        /// </summary>
        public static List<ChapterModel> SplitItemsIntoChapters<T>(
            string sourcePath,
            string baseTitle,
            string chapterKey,
            IReadOnlyList<T> items,
            ComicFormat format,
            Action<ChapterModel, List<T>> assignItems,
            ref int nextOrderIndex,
            List<string>? warnings = null,
            int maxPagesPerChapter = AppConstants.MaxPagesPerChapter)
        {
            var result = new List<ChapterModel>();
            if (items.Count == 0) return result;

            int effectiveMax = Math.Clamp(maxPagesPerChapter, 1, AppConstants.MaxPagesPerChapter);
            if (items.Count <= effectiveMax)
            {
                var single = new ChapterModel
                {
                    ChapterId = CreateDeterministicChapterId(sourcePath, chapterKey, 1),
                    Title = baseTitle,
                    OrderIndex = nextOrderIndex++,
                    Format = format,
                    SourcePath = sourcePath,
                    PageCount = items.Count
                };
                var list = new List<T>(items);
                assignItems(single, list);
                result.Add(single);
                return result;
            }

            int totalParts = (items.Count + effectiveMax - 1) / effectiveMax;
            string warning = $"Chương '{baseTitle}' có {items.Count} trang (>= 200), đã tự động chia thành {totalParts} phần (< 200 trang/chương).";
            warnings?.Add(warning);

            for (int part = 0; part < totalParts; part++)
            {
                int start = part * effectiveMax;
                int count = Math.Min(effectiveMax, items.Count - start);
                var slice = new List<T>(count);
                for (int i = 0; i < count; i++)
                {
                    slice.Add(items[start + i]);
                }

                var chapter = new ChapterModel
                {
                    ChapterId = CreateDeterministicChapterId(sourcePath, chapterKey, part + 1),
                    Title = $"{baseTitle}.{part + 1}",
                    OrderIndex = nextOrderIndex++,
                    Format = format,
                    SourcePath = sourcePath,
                    PageCount = count,
                    SplitWarning = warning
                };
                assignItems(chapter, slice);
                result.Add(chapter);
            }

            return result;
        }

        /// <summary>
        /// Splits a contiguous PDF page range into chapters of at most <paramref name="maxPagesPerChapter"/> pages.
        /// </summary>
        public static List<ChapterModel> SplitPdfRangeIntoChapters(
            string pdfPath,
            string baseTitle,
            string chapterKey,
            int startPageIndex,
            int totalSectionPages,
            ref int nextOrderIndex,
            List<string>? warnings = null,
            int maxPagesPerChapter = AppConstants.MaxPagesPerChapter)
        {
            var result = new List<ChapterModel>();
            if (totalSectionPages <= 0) return result;

            int effectiveMax = Math.Clamp(maxPagesPerChapter, 1, AppConstants.MaxPagesPerChapter);
            if (totalSectionPages <= effectiveMax)
            {
                result.Add(new ChapterModel
                {
                    ChapterId = CreateDeterministicChapterId(pdfPath, chapterKey, 1),
                    Title = baseTitle,
                    OrderIndex = nextOrderIndex++,
                    Format = ComicFormat.Pdf,
                    SourcePath = pdfPath,
                    StartPageIndex = startPageIndex,
                    PageCount = totalSectionPages
                });
                return result;
            }

            int totalParts = (totalSectionPages + effectiveMax - 1) / effectiveMax;
            string warning = $"Chương '{baseTitle}' có {totalSectionPages} trang (>= 200), đã tự động chia thành {totalParts} phần (< 200 trang/chương).";
            warnings?.Add(warning);

            for (int part = 0; part < totalParts; part++)
            {
                int offset = part * effectiveMax;
                int count = Math.Min(effectiveMax, totalSectionPages - offset);
                result.Add(new ChapterModel
                {
                    ChapterId = CreateDeterministicChapterId(pdfPath, chapterKey, part + 1),
                    Title = $"{baseTitle}.{part + 1}",
                    OrderIndex = nextOrderIndex++,
                    Format = ComicFormat.Pdf,
                    SourcePath = pdfPath,
                    StartPageIndex = startPageIndex + offset,
                    PageCount = count,
                    SplitWarning = warning
                });
            }

            return result;
        }
    }
}
