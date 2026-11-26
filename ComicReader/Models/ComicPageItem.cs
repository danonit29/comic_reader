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
        private Bitmap? _image;

        [ObservableProperty]
        private bool _isBookmarked;

        [ObservableProperty]
        private double _placeholderHeight = 900.0;

        /// <summary>
        /// Formatted bookmark badge text (e.g., "Page 12 Bookmarked").
        /// </summary>
        public string BookmarkBadgeText => $"Page {PageNumber} Bookmarked";
    }
}
