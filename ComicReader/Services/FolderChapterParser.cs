using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using ComicReader.Helpers;
using ComicReader.Models;

namespace ComicReader.Services
{
    /// <summary>
    /// Represents a discovered image-based comic series inside an imported folder tree.
    /// </summary>
    public sealed class FolderSeriesGroup
    {
        public string DisplayName { get; init; } = string.Empty;
        public string SeriesPath { get; init; } = string.Empty;
        public List<ChapterModel> Chapters { get; init; } = new();
    }

    /// <summary>
    /// Result of recursively parsing a folder hierarchy into chapters (&lt; 200 pages each)
    /// and comic archive files (.pdf, .zip, .cbz).
    /// </summary>
    public sealed class FolderParseResult
    {
        public ComicFolder Folder { get; init; } = new();
        public List<FolderSeriesGroup> SeriesGroups { get; init; } = new();
        public List<string> ArchiveFiles { get; init; } = new();
        public List<string> Warnings { get; init; } = new();
    }

    /// <summary>
    /// Recursively parses multi-level comic folders (e.g., Root / [Truyen] / [Chuong] / [Images])
    /// without flattening separate chapters together, and guarantees every chapter has &lt; 200 pages.
    /// </summary>
    public static class FolderChapterParser
    {
        private static readonly NaturalStringComparer NaturalComparer = new();

        /// <summary>
        /// Recursively inspects <paramref name="rootFolderPath"/> and returns a <see cref="ComicFolder"/>
        /// populated with <see cref="ChapterModel"/> items, grouped series, archive files, and any split warnings.
        /// </summary>
        public static FolderParseResult ParseFolder(
            string rootFolderPath,
            int maxPagesPerChapter = AppConstants.MaxPagesPerChapter)
        {
            string normalizedRoot = Path.GetFullPath(rootFolderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string rootName = Path.GetFileName(normalizedRoot);
            if (string.IsNullOrWhiteSpace(rootName))
            {
                rootName = normalizedRoot;
            }

            var folder = new ComicFolder
            {
                DisplayName = rootName,
                FolderPath = normalizedRoot
            };

            var result = new FolderParseResult { Folder = folder };
            if (!Directory.Exists(normalizedRoot))
            {
                return result;
            }

            var allChapters = new List<ChapterModel>();
            int orderIndex = 0;

            List<string> directImages = GetSortedImages(normalizedRoot);
            List<string> immediateSubDirs = GetSortedSubDirectories(normalizedRoot);

            // Collect any archive files (.pdf, .zip, .cbz) anywhere in the folder tree.
            CollectArchiveFilesRecursive(normalizedRoot, result.ArchiveFiles);

            // Determine whether immediate subdirectories represent separate series (e.g. Root/TruyenA/Chuong1)
            // vs direct chapters of a single comic (e.g. TruyenA/Chuong1).
            bool anyChildHasChapterSubdirs = immediateSubDirs.Any(HasDescendantImageDirectory);

            if (anyChildHasChapterSubdirs)
            {
                if (directImages.Count > 0)
                {
                    var rootDirectChapters = new List<ChapterModel>();
                    int localOrder = 0;
                    var split = ChapterSplitter.SplitItemsIntoChapters(
                        normalizedRoot,
                        rootName,
                        normalizedRoot,
                        directImages,
                        ComicFormat.Folder,
                        static (ch, imgs) => ch.ImagePaths = imgs,
                        ref localOrder,
                        result.Warnings,
                        maxPagesPerChapter);

                    foreach (ChapterModel ch in split)
                    {
                        ch.OrderIndex = orderIndex++;
                        rootDirectChapters.Add(ch);
                        allChapters.Add(ch);
                    }

                    result.SeriesGroups.Add(new FolderSeriesGroup
                    {
                        DisplayName = rootName,
                        SeriesPath = normalizedRoot,
                        Chapters = rootDirectChapters
                    });
                }

                foreach (string childDir in immediateSubDirs)
                {
                    var seriesChapters = new List<ChapterModel>();
                    int seriesOrder = 0;
                    CollectChaptersRecursive(childDir, childDir, seriesChapters, result.Warnings, ref seriesOrder, maxPagesPerChapter);

                    if (seriesChapters.Count == 0) continue;

                    string seriesName = Path.GetFileName(childDir);
                    foreach (ChapterModel ch in seriesChapters)
                    {
                        ch.OrderIndex = orderIndex++;
                        allChapters.Add(ch);
                    }

                    result.SeriesGroups.Add(new FolderSeriesGroup
                    {
                        DisplayName = seriesName,
                        SeriesPath = childDir,
                        Chapters = seriesChapters
                    });
                }
            }
            else
            {
                // Single series rooted at rootFolderPath (either direct images or Root/Chuong1, Root/Chuong2, ...)
                int seriesOrder = 0;
                CollectChaptersRecursive(normalizedRoot, normalizedRoot, allChapters, result.Warnings, ref seriesOrder, maxPagesPerChapter);

                if (allChapters.Count > 0)
                {
                    result.SeriesGroups.Add(new FolderSeriesGroup
                    {
                        DisplayName = rootName,
                        SeriesPath = normalizedRoot,
                        Chapters = new List<ChapterModel>(allChapters)
                    });
                }
            }

            folder.Chapters = new ObservableCollection<ChapterModel>(allChapters);
            folder.ParseWarnings = new List<string>(result.Warnings);
            folder.ComicCount = result.SeriesGroups.Count + result.ArchiveFiles.Count;

            return result;
        }

        /// <summary>
        /// Discovers all chapters under a single comic folder path (guaranteed &lt; 200 pages per chapter).
        /// </summary>
        public static List<ChapterModel> DiscoverChaptersForSeries(
            string seriesFolderPath,
            List<string>? warnings = null,
            int maxPagesPerChapter = AppConstants.MaxPagesPerChapter)
        {
            var chapters = new List<ChapterModel>();
            if (!Directory.Exists(seriesFolderPath)) return chapters;

            string normalized = Path.GetFullPath(seriesFolderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            int orderIndex = 0;
            CollectChaptersRecursive(normalized, normalized, chapters, warnings, ref orderIndex, maxPagesPerChapter);
            return chapters;
        }

        private static void CollectChaptersRecursive(
            string currentDir,
            string seriesRoot,
            List<ChapterModel> chapters,
            List<string>? warnings,
            ref int orderIndex,
            int maxPagesPerChapter)
        {
            List<string> directImages = GetSortedImages(currentDir);
            List<string> subDirs = GetSortedSubDirectories(currentDir);

            if (directImages.Count > 0)
            {
                string title = string.Equals(currentDir, seriesRoot, StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(seriesRoot)
                    : Path.GetRelativePath(seriesRoot, currentDir).Replace('\\', '/');

                if (string.IsNullOrWhiteSpace(title))
                {
                    title = Path.GetFileName(currentDir);
                }

                List<ChapterModel> split = ChapterSplitter.SplitItemsIntoChapters(
                    currentDir,
                    title,
                    currentDir,
                    directImages,
                    ComicFormat.Folder,
                    static (ch, imgs) => ch.ImagePaths = imgs,
                    ref orderIndex,
                    warnings,
                    maxPagesPerChapter);

                chapters.AddRange(split);
            }

            foreach (string subDir in subDirs)
            {
                CollectChaptersRecursive(subDir, seriesRoot, chapters, warnings, ref orderIndex, maxPagesPerChapter);
            }
        }

        private static void CollectArchiveFilesRecursive(string currentDir, List<string> archiveFiles)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(currentDir, "*.*", SearchOption.TopDirectoryOnly)
                    .OrderBy(static f => f, NaturalComparer))
                {
                    string ext = Path.GetExtension(file);
                    if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".cbz", StringComparison.OrdinalIgnoreCase))
                    {
                        archiveFiles.Add(file);
                    }
                }

                foreach (string subDir in GetSortedSubDirectories(currentDir))
                {
                    CollectArchiveFilesRecursive(subDir, archiveFiles);
                }
            }
            catch
            {
                // Ignore inaccessible system directories.
            }
        }

        private static bool HasDescendantImageDirectory(string dirPath)
        {
            foreach (string subDir in GetSortedSubDirectories(dirPath))
            {
                if (GetSortedImages(subDir).Count > 0 || HasDescendantImageDirectory(subDir))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> GetSortedImages(string dirPath)
        {
            try
            {
                return Directory.EnumerateFiles(dirPath, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(static f => AppConstants.SupportedImageExtensions.Contains(Path.GetExtension(f)))
                    .OrderBy(static f => f, NaturalComparer)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static List<string> GetSortedSubDirectories(string dirPath)
        {
            try
            {
                return Directory.EnumerateDirectories(dirPath, "*", SearchOption.TopDirectoryOnly)
                    .Where(static d =>
                    {
                        string name = Path.GetFileName(d);
                        return !string.IsNullOrEmpty(name) && !name.StartsWith('.');
                    })
                    .OrderBy(static d => d, NaturalComparer)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }
    }
}
