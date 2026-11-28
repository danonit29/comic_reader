using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ComicReader.Models
{
    /// <summary>
    /// Represents a selectable page preview inside the Change Thumbnail picker.
    /// </summary>
    public sealed partial class ThumbnailPageOption : ObservableObject
    {
        public int PageNumber { get; init; }

        [ObservableProperty]
        private Bitmap? _preview;

        public string PageLabel => $"Trang {PageNumber}";
    }
}
