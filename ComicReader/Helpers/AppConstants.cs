using System;
using System.Collections.Generic;
using System.IO;

namespace ComicReader.Helpers
{
    /// <summary>
    /// Centralized application constants for paths, rendering, and UI defaults.
    /// </summary>
    public static class AppConstants
    {
        public const string AppName = "ComicReader";
        public const string ProgressFileName = "library_data.json";
        public const string SettingsFileName = "settings.json";
        public const string CoversFolderName = "Covers";

        public const double DefaultWindowWidth = 1280;
        public const double DefaultWindowHeight = 800;

        public const int DefaultRenderDpi = 200;
        public const int PreviewRenderDpi = 36;
        public const int CoverRenderDpi = 96;
        public const int CoverDecodeWidth = 240;
        public const int PreviewDecodeWidth = 240;
        public const int MaxReaderDecodeWidth = 0; // 0 = keep 100% original resolution for Full Quality tier
        public const int MaxPagesPerChapter = 199; // Strictly < 200 pages per chapter

        public const double MinZoomLevel = 0.3;
        public const double MaxZoomLevel = 4.0;
        public const double ZoomStep = 0.1;
        public const double DefaultZoomLevel = 1.0;

        public const string DefaultStatusReady = "Ready";
        public const string SectionHome = "Home";
        public const string SectionLibrary = "Library";
        public const string SectionFolders = "Folders";
        public const string SectionSearch = "Search";
        public const string SectionSettings = "Settings";

        public static readonly IReadOnlyList<string> SupportedComicExtensions =
            new[] { "*.pdf", "*.zip", "*.cbz" };

        public static readonly IReadOnlyList<string> SupportedImagePatterns =
            new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" };

        public static readonly IReadOnlySet<string> SupportedArchiveExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".zip", ".cbz" };

        public static readonly IReadOnlySet<string> SupportedImageExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };

        public static string AppDataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

        public static string CoversDirectory => Path.Combine(AppDataDirectory, CoversFolderName);

        public static string LibraryDataFilePath => Path.Combine(AppDataDirectory, ProgressFileName);

        public static string SettingsFilePath => Path.Combine(AppDataDirectory, SettingsFileName);
    }
}
