using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicReader.Models
{
    /// <summary>
    /// Represents a comic book or chapter entry displayed in the library or a folder.
    /// </summary>
    public sealed partial class ComicBook : ObservableObject
    {
        /// <summary>
        /// Unique identifier for the comic.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [ObservableProperty]
        private string _displayName = string.Empty;

        /// <summary>
        /// Backward-compatible property for migrating legacy JSON entries that stored "title".
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Title
        {
            get => null;
            set
            {
                if (string.IsNullOrWhiteSpace(DisplayName) && !string.IsNullOrWhiteSpace(value))
                {
                    DisplayName = value;
                }
            }
        }

        /// <summary>
        /// Absolute path to the physical comic file (.pdf, .zip, .cbz). Never modified by rename.
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasParentFolder))]
        private string? _parentFolderName;

        /// <summary>
        /// Absolute path to the parent folder if imported via Folders.
        /// </summary>
        public string? ParentFolderPath { get; set; }

        [ObservableProperty]
        private string _coverImagePath = string.Empty;

        /// <summary>
        /// Detected file format.
        /// </summary>
        public ComicFormat Format { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PageStatusText))]
        [NotifyPropertyChangedFor(nameof(ProgressPercentage))]
        private int _currentPage = 1;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PageStatusText))]
        [NotifyPropertyChangedFor(nameof(ProgressPercentage))]
        private int _totalPages;

        /// <summary>
        /// Last saved zoom level for this comic (defaults to 1.0 / 100%).
        /// </summary>
        public double ZoomLevel { get; set; } = 1.0;

        /// <summary>
        /// UTC timestamp when the comic was last opened or accessed.
        /// </summary>
        public DateTime LastAccessedTime { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Indicates whether this comic belongs to an imported folder (drives IsVisible in card subtitles).
        /// </summary>
        [JsonIgnore]
        public bool HasParentFolder => !string.IsNullOrWhiteSpace(ParentFolderName);

        /// <summary>
        /// Formatted page status (e.g., "Page 15 of 37").
        /// </summary>
        [JsonIgnore]
        public string PageStatusText =>
            TotalPages > 0 ? $"Page {CurrentPage} of {TotalPages}" : "Unread";

        /// <summary>
        /// Reading completion percentage (0 to 100) for UI progress bars.
        /// </summary>
        [JsonIgnore]
        public double ProgressPercentage =>
            TotalPages > 0 ? Math.Clamp((double)CurrentPage / TotalPages * 100.0, 0, 100) : 0;
    }
}
