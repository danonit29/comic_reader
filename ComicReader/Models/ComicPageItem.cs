using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicReader.Models
{
    /// <summary>
    /// Represents a single rendered page item in the vertical continuous reader.
    /// </summary>
    public sealed partial class ComicPageItem : ObservableObject
    {
        /// <summary>
        /// 1-based page number.
        /// </summary>
        public int PageNumber { get; init; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EffectiveMinHeight))]
        private Bitmap? _image;

        [ObservableProperty]
        private bool _isBookmarked;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EffectiveMinHeight))]
        private double _placeholderHeight = OperatingSystem.IsAndroid() ? 420.0 : 900.0;

        /// <summary>
        /// Effective minimum height for the page container. On Android, collapses to 0 once the image
        /// is loaded so short/wide pages never show black gaps underneath.
        /// </summary>
        public double EffectiveMinHeight => (OperatingSystem.IsAndroid() && Image is not null)
            ? 0.0
            : PlaceholderHeight;

        /// <summary>
        /// Formatted bookmark badge text (e.g., "Page 12 Bookmarked").
        /// </summary>
        public string BookmarkBadgeText => $"Page {PageNumber} Bookmarked";
    }
}
