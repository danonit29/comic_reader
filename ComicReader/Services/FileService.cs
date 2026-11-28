using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
            Patterns = AppConstants.SupportedComicExtensions,
            MimeTypes = new[]
            {
                "application/pdf",
                "application/zip",
                "application/x-zip-compressed",
                "application/x-cbz",
                "application/vnd.comicbook+zip",
                "application/octet-stream"
            }
        };

        private static readonly FilePickerFileType ImageFileType = new("Image Files")
        {
            Patterns = AppConstants.SupportedImagePatterns,
            MimeTypes = new[] { "image/*" }
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
                FileTypeFilter = OperatingSystem.IsAndroid()
                    ? new[] { FilePickerFileTypes.All }
                    : new[] { ComicFileType }
            });

            var resolvedPaths = new List<string>(files.Count);
            foreach (IStorageFile file in files)
            {
                string? localPath = await ResolveLocalFilePathAsync(file, "ImportedComics");
                if (!string.IsNullOrWhiteSpace(localPath))
                {
                    resolvedPaths.Add(localPath);
                }
            }

            return resolvedPaths;
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

            IStorageFolder? picked = folders.FirstOrDefault();
            if (picked is null)
            {
                return null;
            }

            return await ResolveLocalFolderPathAsync(picked);
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
                FileTypeFilter = OperatingSystem.IsAndroid()
                    ? new[] { FilePickerFileTypes.ImageAll }
                    : new[] { ImageFileType }
            });

            IStorageFile? picked = files.FirstOrDefault();
            return picked is null
                ? null
                : await ResolveLocalFilePathAsync(picked, "ImportedThumbnails");
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
        public async Task<string> SaveCustomThumbnailBitmapAsync(string comicId, Bitmap coverImage)
        {
            Directory.CreateDirectory(AppConstants.CoversDirectory);
            string outputPath = Path.Combine(
                AppConstants.CoversDirectory,
                $"{comicId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png");

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
                if (topLevel is null)
                {
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                        desktop.MainWindow is { } mainWindow)
                    {
                        topLevel = TopLevel.GetTopLevel(mainWindow);
                    }
                    else if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime singleView &&
                        singleView.MainView is { } mainView)
                    {
                        topLevel = TopLevel.GetTopLevel(mainView);
                    }
                }

                return topLevel?.StorageProvider;
            });
        }

        private async Task<string?> ResolveLocalFilePathAsync(IStorageFile file, string cacheSubfolder)
        {
            string? localPath = file.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
                return localPath;
            }

            string fileName = SanitizeFileName(file.Name);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = $"comic_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            }

            string uriKey = file.Path?.ToString() ?? fileName;
            string destFolder = Path.Combine(AppConstants.AppDataDirectory, cacheSubfolder, ComputeShortHash(uriKey));
            Directory.CreateDirectory(destFolder);
            string destPath = Path.Combine(destFolder, fileName);

            await CopyStorageFileToPathAsync(file, destPath);
            return File.Exists(destPath) ? destPath : null;
        }

        private async Task<string?> ResolveLocalFolderPathAsync(IStorageFolder folder)
        {
            string? localPath = folder.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(localPath) && Directory.Exists(localPath))
            {
                return localPath;
            }

            string folderName = SanitizeFileName(folder.Name);
            if (string.IsNullOrWhiteSpace(folderName))
            {
                folderName = "ImportedFolder";
            }

            string uriKey = folder.Path?.ToString() ?? folderName;
            string destRoot = Path.Combine(
                AppConstants.AppDataDirectory,
                "ImportedFolders",
                ComputeShortHash(uriKey),
                folderName);
            Directory.CreateDirectory(destRoot);

            await CopyStorageFolderRecursiveAsync(folder, destRoot);
            return destRoot;
        }

        private async Task CopyStorageFolderRecursiveAsync(IStorageFolder sourceFolder, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);

            await foreach (IStorageItem item in sourceFolder.GetItemsAsync())
            {
                if (item is IStorageFolder subFolder)
                {
                    string subName = SanitizeFileName(subFolder.Name);
                    if (!string.IsNullOrWhiteSpace(subName))
                    {
                        await CopyStorageFolderRecursiveAsync(subFolder, Path.Combine(destinationDir, subName));
                    }
                }
                else if (item is IStorageFile subFile)
                {
                    string fileName = SanitizeFileName(subFile.Name);
                    if (string.IsNullOrWhiteSpace(fileName) || !IsSupportedFolderItemExtension(fileName))
                    {
                        continue;
                    }

                    string destFile = Path.Combine(destinationDir, fileName);
                    if (!File.Exists(destFile))
                    {
                        await CopyStorageFileToPathAsync(subFile, destFile);
                    }
                }
            }
        }

        private static async Task CopyStorageFileToPathAsync(IStorageFile sourceFile, string destinationPath)
        {
            try
            {
                await using Stream src = await sourceFile.OpenReadAsync();
                string tempPath = $"{destinationPath}.tmp";
                await using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await src.CopyToAsync(dst);
                }

                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }
                File.Move(tempPath, destinationPath);
            }
            catch
            {
                // Ignore transient or unreadable provider entries.
            }
        }

        private static bool IsSupportedFolderItemExtension(string fileName)
        {
            string ext = Path.GetExtension(fileName);
            return AppConstants.SupportedImageExtensions.Contains(ext)
                || AppConstants.SupportedArchiveExtensions.Contains(ext)
                || ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        }

        private static string SanitizeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name.Trim())
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            return sb.ToString();
        }

        private static string ComputeShortHash(string value)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
        }
    }
}
