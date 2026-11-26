using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ComicReader.Models;

namespace ComicReader.Services.Interfaces
{
    /// <summary>
    /// Handles cross-platform StorageProvider pickers, format detection, and local cover caching.
    /// </summary>
    public interface IFileService
    {
        /// <summary>
        /// Opens a native file picker allowing the user to select one or more comics.
        /// </summary>
        Task<IReadOnlyList<string>> PickComicFilesAsync();

        /// <summary>
        /// Opens a native folder picker allowing the user to select a folder of comics.
        /// </summary>
        Task<string?> PickFolderAsync();

        /// <summary>
        /// Returns all supported comic file paths inside the specified folder sorted naturally.
        /// </summary>
        IReadOnlyList<string> GetComicFilesInFolder(string folderPath);

        /// <summary>
        /// Opens a native file picker allowing the user to select a custom thumbnail image.
        /// </summary>
        Task<string?> PickImageFileAsync();

        /// <summary>
        /// Determines the ComicFormat from a file path extension.
        /// </summary>
        ComicFormat DetectFormat(string filePath);

        /// <summary>
        /// Saves an extracted cover image to the local cache directory and returns its path.
        /// </summary>
        Task<string> SaveCoverImageAsync(string comicId, Bitmap coverImage);

        /// <summary>
        /// Copies a user-selected thumbnail image into the local cache directory and returns its path.
        /// </summary>
        Task<string> SaveCustomThumbnailAsync(string comicId, string sourceImagePath);
    }
}
