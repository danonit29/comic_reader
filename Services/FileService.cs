using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ComicReader.Helpers;
using ComicReader.Models;
using ComicReader.Services.Interfaces;

namespace ComicReader.Services
{
    /// <summary>
    /// Provides cross-platform StorageProvider file/folder pickers, format detection, and cover caching.
    /// </summary>
    public sealed class FileService : IFileService
    {
        private static readonly FilePickerFileType ComicFileType = new("Comic Files")
        {
            Patterns = AppConstants.SupportedComicExtensions
        };

        private static readonly FilePickerFileType ImageFileType = new("Image Files")
        {
            Patterns = AppConstants.SupportedImagePatterns
        };

        private readonly Func<TopLevel?>? _topLevelProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="FileService"/> class.
        /// </summary>
        public FileService(Func<TopLevel?>? topLevelProvider = null)
        {
            _topLevelProvider = topLevelProvider;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<string>> PickComicFilesAsync()
        {
            IStorageProvider? storageProvider = await GetStorageProviderAsync();
            if (storageProvider is null || !storageProvider.CanOpen)
            {
                return Array.Empty<string>();
            }

            IReadOnlyList<IStorageFile> files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Comics",
                AllowMultiple = true,
                FileTypeFilter = new[] { ComicFileType }
            });

            return files
                .Select(static f => f.TryGetLocalPath())
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToList();
        }

        /// <inheritdoc />
        public async Task<string?> PickFolderAsync()
        {
            IStorageProvider? storageProvider = await GetStorageProviderAsync();
            if (storageProvider is null || !storageProvider.CanPickFolder)
            {
                return null;
            }

            IReadOnlyList<IStorageFolder> folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Import Comic Folder",
                AllowMultiple = false
            });

            return folders.FirstOrDefault()?.TryGetLocalPath();
        }

        /// <inheritdoc />
        public IReadOnlyList<string> GetComicFilesInFolder(string folderPath)
        {
            if (!Directory.Exists(folderPath))
            {
                return Array.Empty<string>();
            }

            var comparer = new NaturalStringComparer();
            return Directory.EnumerateFiles(folderPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(file => DetectFormat(file) != ComicFormat.Unknown)
                .OrderBy(file => file, comparer)
                .ToList();
        }

        /// <inheritdoc />
        public async Task<string?> PickImageFileAsync()
        {
            IStorageProvider? storageProvider = await GetStorageProviderAsync();
            if (storageProvider is null || !storageProvider.CanOpen)
            {
                return null;
            }

            IReadOnlyList<IStorageFile> files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Thumbnail Image",
                AllowMultiple = false,
                FileTypeFilter = new[] { ImageFileType }
            });

            return files.FirstOrDefault()?.TryGetLocalPath();
        }

        /// <inheritdoc />
        public ComicFormat DetectFormat(string filePath)
        {
            string extension = Path.GetExtension(filePath);

            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return ComicFormat.Pdf;
            }

            if (extension.Equals(".cbz", StringComparison.OrdinalIgnoreCase))
            {
                return ComicFormat.Cbz;
            }

            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return ComicFormat.Zip;
            }

            return ComicFormat.Unknown;
        }

        /// <inheritdoc />
        public async Task<string> SaveCoverImageAsync(string comicId, Bitmap coverImage)
        {
            Directory.CreateDirectory(AppConstants.CoversDirectory);
            string outputPath = Path.Combine(AppConstants.CoversDirectory, $"{comicId}.png");

            await Task.Run(() =>
            {
                using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                coverImage.Save(fileStream);
            });

            return outputPath;
        }

        /// <inheritdoc />
        public async Task<string> SaveCustomThumbnailAsync(string comicId, string sourceImagePath)
        {
            Directory.CreateDirectory(AppConstants.CoversDirectory);
            string extension = Path.GetExtension(sourceImagePath);
            string uniqueName = $"{comicId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{extension}";
            string outputPath = Path.Combine(AppConstants.CoversDirectory, uniqueName);

            await Task.Run(() => File.Copy(sourceImagePath, outputPath, overwrite: true));
            return outputPath;
        }

        private async Task<IStorageProvider?> GetStorageProviderAsync()
        {
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                TopLevel? topLevel = _topLevelProvider?.Invoke();
                if (topLevel is null &&
                    Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow is { } mainWindow)
                {
                    topLevel = TopLevel.GetTopLevel(mainWindow);
                }

                return topLevel?.StorageProvider;
            });
        }
    }
}
