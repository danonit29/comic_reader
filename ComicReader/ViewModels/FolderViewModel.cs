using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ComicReader.Helpers;
using ComicReader.Models;
using ComicReader.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ComicReader.ViewModels
{
    /// <summary>
    /// Manages imported comic folders (A-Z), folder renaming, and chapters inside a selected folder.
    /// </summary>
    public partial class FolderViewModel : ObservableObject
    {
        private readonly IFileService _fileService;
        private readonly IStorageService _storageService;
        private readonly IComicReaderFactory _readerFactory;
        private LibraryStore _libraryStore = new();

        [ObservableProperty]
        private ObservableCollection<object> _combinedItems = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsInsideFolder))]
        [NotifyPropertyChangedFor(nameof(IsOutsideFolder))]
        private ComicFolder? _selectedFolder;

        [ObservableProperty]
        private ObservableCollection<ComicBook> _folderComics = new();

        /// <summary>
        /// Indicates whether the user is currently viewing comics inside a specific folder.
        /// </summary>
        public bool IsInsideFolder => SelectedFolder is not null;

        /// <summary>
        /// Indicates whether the user is viewing the top-level folders grid.
        /// </summary>
        public bool IsOutsideFolder => SelectedFolder is null;

        /// <summary>
        /// Async callback provided by the View to prompt the user for a new folder DisplayName.
        /// </summary>
        public Func<string, Task<string?>>? RequestRenamePrompt { get; set; }

        /// <summary>
        /// Event raised to report status updates to the main window.
        /// </summary>
        public event Action<string>? StatusChanged;

        /// <summary>
        /// Event raised to display a toast notification (message, isError).
        /// </summary>
        public event Action<string, bool>? NotificationRequested;

        /// <summary>
        /// Initializes a new instance of the <see cref="FolderViewModel"/> class.
        /// </summary>
        public FolderViewModel(
            IFileService fileService,
            IStorageService storageService,
            IComicReaderFactory readerFactory)
        {
            _fileService = fileService;
            _storageService = storageService;
            _readerFactory = readerFactory;
        }

        /// <summary>
        /// Synchronizes folders from the shared library store, sorted A-Z.
        /// </summary>
        public void SyncFromStore(LibraryStore store)
        {
            _libraryStore = store;
            RefreshCombinedItems();

            if (SelectedFolder is not null)
            {
                LoadFolderComics(SelectedFolder);
            }
        }

        /// <summary>
        /// Prompts the user to pick a folder via Avalonia StorageProvider and imports all supported comics inside it.
        /// </summary>
        [RelayCommand]
        public async Task ImportFolderAsync()
        {
            string? folderPath = await _fileService.PickFolderAsync();
            if (string.IsNullOrWhiteSpace(folderPath)) return;

            string folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            IReadOnlyList<string> comicFiles = await Task.Run(() => _fileService.GetComicFilesInFolder(folderPath));

            if (comicFiles.Count == 0)
            {
                string emptyMsg = $"No supported comics found in '{folderName}'.";
                Dispatcher.UIThread.Post(() =>
                {
                    StatusChanged?.Invoke(emptyMsg);
                    NotificationRequested?.Invoke(emptyMsg, true);
                });
                return;
            }

            int importedCount = 0;
            foreach (string filePath in comicFiles)
            {
                if (await TryImportFolderComicAsync(filePath, folderName, folderPath))
                {
                    importedCount++;
                }
            }

            EnsureFolderRegistered(folderName, folderPath);
            await _storageService.SaveAsync(_libraryStore);

            Dispatcher.UIThread.Post(() =>
            {
                RefreshCombinedItems();
                StatusChanged?.Invoke($"Imported {importedCount} comic(s) from '{folderName}'.");
                NotificationRequested?.Invoke("Đã import xong folder", false);
            });
        }

        /// <summary>
        /// Renames a folder's DisplayName in JSON without modifying the physical directory on disk.
        /// </summary>
        [RelayCommand]
        private async Task RenameFolderAsync(ComicFolder? folder)
        {
            if (folder is null || RequestRenamePrompt is null) return;

            string? newName = await RequestRenamePrompt(folder.DisplayName);
            if (string.IsNullOrWhiteSpace(newName) || newName == folder.DisplayName) return;

            string trimmedName = newName.Trim();
            folder.DisplayName = trimmedName;

            foreach (ComicBook comic in _libraryStore.Comics.Where(c =>
                string.Equals(c.ParentFolderPath, folder.FolderPath, StringComparison.OrdinalIgnoreCase)))
            {
                comic.ParentFolderName = trimmedName;
            }

            await _storageService.SaveAsync(_libraryStore);
            RefreshCombinedItems();
            StatusChanged?.Invoke($"Renamed folder to '{trimmedName}'");
        }

        /// <summary>
        /// Opens a folder to display all comics/chapters inside it.
        /// </summary>
        [RelayCommand]
        private void OpenFolder(ComicFolder? folder)
        {
            if (folder is null) return;
            SelectedFolder = folder;
            LoadFolderComics(folder);
        }

        /// <summary>
        /// Returns from a specific folder back to the folders list.
        /// </summary>
        [RelayCommand]
        private void BackToFolders()
        {
            SelectedFolder = null;
            FolderComics = new ObservableCollection<ComicBook>();
        }

        private void RefreshCombinedItems()
        {
            IEnumerable<ComicFolder> sortedFolders = _libraryStore.Folders
                .OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase);

            foreach (ComicFolder folder in sortedFolders)
            {
                folder.ComicCount = _libraryStore.Comics.Count(c =>
                    string.Equals(c.ParentFolderPath, folder.FolderPath, StringComparison.OrdinalIgnoreCase));
            }

            CombinedItems = new ObservableCollection<object>(sortedFolders);
        }

        private void LoadFolderComics(ComicFolder folder)
        {
            IEnumerable<ComicBook> items = _libraryStore.Comics
                .Where(c => string.Equals(c.ParentFolderPath, folder.FolderPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.DisplayName, new NaturalStringComparer());

            FolderComics = new ObservableCollection<ComicBook>(items);
        }

        private void EnsureFolderRegistered(string folderName, string folderPath)
        {
            bool exists = _libraryStore.Folders.Any(f =>
                f.FolderPath.Equals(folderPath, StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                _libraryStore.Folders.Add(new ComicFolder
                {
                    DisplayName = folderName,
                    FolderPath = folderPath
                });
            }
        }

        private async Task<bool> TryImportFolderComicAsync(string filePath, string folderName, string folderPath)
        {
            bool exists = _libraryStore.Comics.Any(c =>
                c.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
            if (exists) return false;

            ComicFormat format = _fileService.DetectFormat(filePath);
            if (format == ComicFormat.Unknown) return false;

            Dispatcher.UIThread.Post(() =>
                StatusChanged?.Invoke($"Importing {Path.GetFileName(filePath)}..."));

            IComicReaderService reader = _readerFactory.GetReader(format);

            int totalPages = await reader.GetPageCountAsync(filePath);
            Bitmap? cover = await reader.ExtractCoverAsync(filePath);

            var comic = new ComicBook
            {
                DisplayName = Path.GetFileNameWithoutExtension(filePath),
                FilePath = filePath,
                ParentFolderName = folderName,
                ParentFolderPath = folderPath,
                Format = format,
                TotalPages = totalPages,
                CurrentPage = 1,
                ZoomLevel = AppConstants.DefaultZoomLevel,
                LastAccessedTime = DateTime.MinValue
            };

            if (cover is not null)
            {
                comic.CoverImagePath = await _fileService.SaveCoverImageAsync(comic.Id, cover);
            }

            _libraryStore.Comics.Add(comic);
            return true;
        }
    }
}
