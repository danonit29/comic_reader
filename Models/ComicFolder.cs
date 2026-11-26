using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicReader.Models
{
    /// <summary>
    /// Represents an imported folder containing one or more comic files.
    /// </summary>
    public sealed partial class ComicFolder : ObservableObject
    {
        /// <summary>
        /// Unique identifier for the folder entry.
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [ObservableProperty]
        private string _displayName = string.Empty;

        /// <summary>
        /// Backward-compatible property for migrating legacy JSON entries that stored "name".
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name
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
        /// Absolute path to the folder on disk. Never modified by rename.
        /// </summary>
        public string FolderPath { get; set; } = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ComicCountText))]
        private int _comicCount;

        /// <summary>
        /// Formatted item count displayed on folder cards.
        /// </summary>
        [JsonIgnore]
        public string ComicCountText => $"{ComicCount} item(s)";
    }
}
