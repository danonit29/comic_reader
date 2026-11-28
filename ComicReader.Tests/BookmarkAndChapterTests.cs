using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ComicReader.Helpers;
using ComicReader.Models;
using ComicReader.Services;
using ComicReader.Services.Interfaces;
using ComicReader.Services.Native;
using ComicReader.ViewModels;
using Xunit;

namespace ComicReader.Tests
{
    public sealed class BookmarkAndChapterTests : IDisposable
    {
        private readonly string _tempRoot;

        public BookmarkAndChapterTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"ComicReaderTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        [Fact]
        public async Task Test1_Bookmark_SavePage2AndPage5_PersistsBothAndDeletingPage2LeavesPage5()
        {
            string jsonPath = Path.Combine(_tempRoot, "bookmarks.json");
            var service = new BookmarkService(jsonPath);

            bool addedPage2 = await service.SaveBookmarkAsync(new Bookmark
            {
                ComicId = "comic-1",
                ChapterId = "chap-1",
                ChapterTitle = "Chương 1",
                PageNumber = 2,
                Note = "Trang 2"
            });

            bool addedPage5 = await service.SaveBookmarkAsync(new Bookmark
            {
                ComicId = "comic-1",
                ChapterId = "chap-1",
                ChapterTitle = "Chương 1",
                PageNumber = 5,
                Note = "Trang 5"
            });

            Assert.True(addedPage2);
            Assert.True(addedPage5);

            // Verify raw JSON on disk has 2 independent records.
            string rawJsonAfterAdd = await File.ReadAllTextAsync(jsonPath);
            List<Bookmark>? diskAfterAdd = JsonSerializer.Deserialize<List<Bookmark>>(
                rawJsonAfterAdd,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(diskAfterAdd);
            Assert.Equal(2, diskAfterAdd!.Count);
            Assert.Contains(diskAfterAdd, b => b.PageNumber == 2);
            Assert.Contains(diskAfterAdd, b => b.PageNumber == 5);
            Assert.NotEqual(diskAfterAdd[0].BookmarkId, diskAfterAdd[1].BookmarkId);

            // Delete bookmark at page 2 (via RemoveBookmarkAsync) and verify 1 record remains (page 5).
            await service.RemoveBookmarkAsync("comic-1", "chap-1", 2);

            string rawJsonAfterDelete = await File.ReadAllTextAsync(jsonPath);
            List<Bookmark>? diskAfterDelete = JsonSerializer.Deserialize<List<Bookmark>>(
                rawJsonAfterDelete,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(diskAfterDelete);
            Assert.Single(diskAfterDelete!);
            Assert.Equal(5, diskAfterDelete[0].PageNumber);
        }

        [Fact]
        public async Task Test1B_Bookmark_SamePageAcrossDifferentChapters_DoesNotOverwrite()
        {
            string jsonPath = Path.Combine(_tempRoot, "bookmarks_chapters.json");
            var service = new BookmarkService(jsonPath);

            await service.SaveBookmarkAsync(new Bookmark { ComicId = "comic-1", ChapterId = "chap-1", PageNumber = 2 });
            await service.SaveBookmarkAsync(new Bookmark { ComicId = "comic-1", ChapterId = "chap-2", PageNumber = 2 });

            IReadOnlyList<Bookmark> list = await service.GetBookmarksAsync("comic-1");
            Assert.Equal(2, list.Count);
            Assert.Contains(list, b => b.ChapterId == "chap-1" && b.PageNumber == 2);
            Assert.Contains(list, b => b.ChapterId == "chap-2" && b.PageNumber == 2);

            // Calling SaveBookmarkAsync again on (chap-1, page 2) toggles/removes only chap-1 page 2.
            bool toggled = await service.SaveBookmarkAsync(new Bookmark { ComicId = "comic-1", ChapterId = "chap-1", PageNumber = 2 });
            Assert.False(toggled);

            list = await service.GetBookmarksAsync("comic-1");
            Assert.Single(list);
            Assert.Equal("chap-2", list[0].ChapterId);
        }

        [Fact]
        public void Test2_FolderParsing_TruyenA_Chuong1_50Pages_And_Chuong2_250Pages_SplitsAndWarnsUnder200()
        {
            string root = Path.Combine(_tempRoot, "LibraryRoot");
            string truyenA = Path.Combine(root, "TruyenA");
            string chuong1 = Path.Combine(truyenA, "Chuong1");
            string chuong2 = Path.Combine(truyenA, "Chuong2");

            CreateDummyImages(chuong1, 50);
            CreateDummyImages(chuong2, 250);

            FolderParseResult parsed = FolderChapterParser.ParseFolder(root);

            Assert.Single(parsed.SeriesGroups);
            FolderSeriesGroup series = parsed.SeriesGroups[0];
            Assert.Equal("TruyenA", series.DisplayName);

            // Chuong1 (50) + Chuong2.1 (199) + Chuong2.2 (51) = 3 chapters, every chapter strictly < 200 pages.
            Assert.Equal(3, series.Chapters.Count);
            Assert.Equal(50, series.Chapters[0].PageCount);
            Assert.Equal(199, series.Chapters[1].PageCount);
            Assert.Equal(51, series.Chapters[2].PageCount);
            Assert.All(series.Chapters, ch => Assert.True(ch.PageCount < 200, $"Chapter {ch.Title} exceeded 199 pages: {ch.PageCount}"));

            // Verify warning emitted and ComicFolder populated.
            Assert.Single(parsed.Warnings);
            Assert.Contains("Chuong2", parsed.Warnings[0]);
            Assert.Equal(3, parsed.Folder.Chapters.Count);
            Assert.Equal(50, parsed.Folder.Chapters[0].ImagePaths.Count);
            Assert.Equal(199, parsed.Folder.Chapters[1].ImagePaths.Count);
            Assert.Equal(51, parsed.Folder.Chapters[2].ImagePaths.Count);
        }

        [Fact]
        public void Test3_FolderParsing_MultiLevelNestedSeries_NeverFlattensDifferentStoriesOrChapters()
        {
            string root = Path.Combine(_tempRoot, "MultiSeries");
            CreateDummyImages(Path.Combine(root, "TruyenA", "Vol1", "Chap1"), 12);
            CreateDummyImages(Path.Combine(root, "TruyenA", "Vol1", "Chap2"), 18);
            CreateDummyImages(Path.Combine(root, "TruyenB", "Chap1"), 25);

            FolderParseResult parsed = FolderChapterParser.ParseFolder(root);

            Assert.Equal(2, parsed.SeriesGroups.Count);
            Assert.Equal("TruyenA", parsed.SeriesGroups[0].DisplayName);
            Assert.Equal(2, parsed.SeriesGroups[0].Chapters.Count);
            Assert.Equal(12, parsed.SeriesGroups[0].Chapters[0].PageCount);
            Assert.Equal(18, parsed.SeriesGroups[0].Chapters[1].PageCount);

            Assert.Equal("TruyenB", parsed.SeriesGroups[1].DisplayName);
            Assert.Single(parsed.SeriesGroups[1].Chapters);
            Assert.Equal(25, parsed.SeriesGroups[1].Chapters[0].PageCount);
        }

        [Fact]
        public async Task Test4_ZipParsing_SplitsInternalFoldersAndOversizedChaptersUnder200Pages()
        {
            string zipPath = Path.Combine(_tempRoot, "multi_chapter.zip");
            using (FileStream fs = File.Create(zipPath))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                for (int i = 1; i <= 30; i++)
                {
                    ZipArchiveEntry entry = archive.CreateEntry($"Manga/Chuong1/{i:D3}.jpg");
                    using Stream s = entry.Open();
                    s.WriteByte(0xFF);
                }

                for (int i = 1; i <= 250; i++)
                {
                    ZipArchiveEntry entry = archive.CreateEntry($"Manga/Chuong2/{i:D3}.jpg");
                    using Stream s = entry.Open();
                    s.WriteByte(0xFF);
                }
            }

            var warnings = new List<string>();
            List<ChapterModel> chapters = ZipService.DiscoverChapters(zipPath, ComicFormat.Zip, warnings);

            Assert.Equal(3, chapters.Count);
            Assert.Equal(30, chapters[0].PageCount);
            Assert.Equal(199, chapters[1].PageCount);
            Assert.Equal(51, chapters[2].PageCount);
            Assert.All(chapters, ch => Assert.True(ch.PageCount < 200));
            Assert.Single(warnings);

            // Opening chapter 1 session only loads 30 pages, not 280.
            var zipService = new ZipService();
            using IComicReadSession session = await zipService.OpenChapterSessionAsync(zipPath, chapters[0]);
            Assert.Equal(30, session.PageCount);
        }

        [Fact]
        public async Task Test5_PdfParsing_Splits250PagePdfIntoChaptersUnder200Pages()
        {
            string pdfPath = Path.Combine(_tempRoot, "long_comic.pdf");
            WriteMinimalPdf(pdfPath, pageCount: 250);

            var warnings = new List<string>();
            List<ChapterModel> chapters = PdfService.DiscoverChapters(pdfPath, warnings);

            Assert.Equal(2, chapters.Count);
            Assert.Equal(0, chapters[0].StartPageIndex);
            Assert.Equal(199, chapters[0].PageCount);
            Assert.Equal(199, chapters[1].StartPageIndex);
            Assert.Equal(51, chapters[1].PageCount);
            Assert.All(chapters, ch => Assert.True(ch.PageCount < 200));
            Assert.Single(warnings);

            var pdfService = new PdfService();
            using IComicReadSession chap2Session = await pdfService.OpenChapterSessionAsync(pdfPath, chapters[1]);
            Assert.Equal(51, chap2Session.PageCount);
        }

        [Fact]
        public async Task Test6_ReaderViewModel_ChaptersBookmarkAndJumpAcrossChapters()
        {
            string root = Path.Combine(_tempRoot, "ReaderComic");
            CreateDummyImages(Path.Combine(root, "Chap1"), 10);
            CreateDummyImages(Path.Combine(root, "Chap2"), 12);

            string bmPath = Path.Combine(_tempRoot, "vm_bookmarks.json");
            var bookmarkService = new BookmarkService(bmPath);
            var storage = new InMemoryStorageService();
            var factory = new ComicReaderFactory(new PdfService(), new ZipService(), new FolderImageReaderService());
            var vm = new ReaderViewModel(factory, storage, bookmarkService);

            var comic = new ComicBook
            {
                Id = "vm-comic-1",
                DisplayName = "ReaderComic",
                FilePath = root,
                Format = ComicFormat.Folder
            };

            await vm.LoadComicAsync(comic);
            Assert.Equal(2, vm.Chapters.Count);
            Assert.True(vm.HasMultipleChapters);
            Assert.True(vm.HasNextChapter);

            // Bookmark page 2 and page 5 in Chap1
            vm.JumpToPage(2);
            await vm.AddBookmarkCommand.ExecuteAsync(null);
            vm.JumpToPage(5);
            await vm.AddBookmarkCommand.ExecuteAsync(null);

            Assert.Equal(2, vm.Bookmarks.Count);
            Assert.Equal(2, vm.CurrentChapterBookmarks.Count);

            // Switch to NextChapter -> resets page back to 1
            await vm.NextChapterCommand.ExecuteAsync(null);
            Assert.Equal(vm.Chapters[1].ChapterId, vm.CurrentChapterId);
            Assert.Equal(1, vm.CurrentPage);
            Assert.Equal(12, vm.TotalPages);

            // Bookmark page 4 in Chap2 -> total 3 bookmarks across 2 chapters
            vm.JumpToPage(4);
            await vm.AddBookmarkCommand.ExecuteAsync(null);
            Assert.Equal(3, vm.Bookmarks.Count);
            Assert.Single(vm.CurrentChapterBookmarks);

            // Click Chap1 page 2 bookmark -> jumps back to Chap1 and Page 2
            Bookmark chap1Page2 = vm.Bookmarks.First(b => b.ChapterId == vm.Chapters[0].ChapterId && b.PageNumber == 2);
            await vm.NavigateToBookmarkCommand.ExecuteAsync(chap1Page2);
            Assert.Equal(vm.Chapters[0].ChapterId, vm.CurrentChapterId);
            Assert.Equal(2, vm.CurrentPage);

            vm.Cleanup();
        }

        [Fact]
        public async Task Test7_ImportComicAndFolder_ImportsNewSeriesEvenWhenCoverFailsAndSurfacesExisting()
        {
            string folderRoot = Path.Combine(_tempRoot, "NewMangaFolder");
            CreateDummyImages(Path.Combine(folderRoot, "BoTruyenMoi", "Chuong1"), 5);

            string zipPath = Path.Combine(_tempRoot, "standalone_new.zip");
            using (FileStream fs = File.Create(zipPath))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry("001.jpg");
                using Stream s = entry.Open();
                s.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            }

            var storage = new InMemoryStorageService();
            var factory = new ComicReaderFactory(new PdfService(), new ZipService(), new FolderImageReaderService());
            var fileService = new StubFileService(new[] { zipPath }, folderRoot);
            var folderVm = new FolderViewModel(fileService, storage, factory);
            var mainVm = new MainViewModel(fileService, storage, factory, new BookmarkService(Path.Combine(_tempRoot, "bm7.json")), folderVm);

            await mainVm.InitializeAsync();

            // 1. Import standalone ZIP (note dummy JPEG bytes fail Avalonia decode -> cover fallback null, must still import!)
            await mainVm.ImportComicCommand.ExecuteAsync(null);
            Assert.Single(mainVm.FilteredComics);
            Assert.Equal("standalone_new", mainVm.FilteredComics[0].DisplayName);

            // 2. Import multi-level Folder -> creates Folder + ComicBook inside it
            await folderVm.ImportFolderCommand.ExecuteAsync(null);
            Assert.Single(folderVm.CombinedItems);
            Assert.Single(folderVm.FolderComics);
            Assert.Equal("BoTruyenMoi", folderVm.FolderComics[0].DisplayName);
        }

        [Fact]
        public void Test8_Layer3_IComicEngine_SafeHandleOpensChapterScopedZipAndFallsBackGracefully()
        {
            string zipPath = Path.Combine(_tempRoot, "engine_chapters.zip");
            using (FileStream fs = File.Create(zipPath))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                for (int i = 1; i <= 5; i++)
                {
                    ZipArchiveEntry e1 = archive.CreateEntry($"Ch1/{i:D2}.jpg");
                    using Stream s1 = e1.Open();
                    s1.Write(new byte[] { 0xFF, 0xD8, (byte)i, 0xD9 });
                }
                for (int i = 1; i <= 3; i++)
                {
                    ZipArchiveEntry e2 = archive.CreateEntry($"Ch2/{i:D2}.jpg");
                    using Stream s2 = e2.Open();
                    s2.Write(new byte[] { 0xFF, 0xD8, (byte)(i + 10), 0xD9 });
                }
            }

            List<ChapterModel> chapters = ZipService.DiscoverChapters(zipPath, ComicFormat.Zip);
            Assert.Equal(2, chapters.Count);

            IComicEngine engine = new RustComicEngine();
            if (engine.IsAvailable)
            {
                using SafeDocumentHandle? handle = engine.OpenZipDocument(zipPath, chapters[1].ZipEntryNames);
                Assert.NotNull(handle);
                Assert.Equal(3, engine.GetPageCount(handle!));
                byte[]? raw = engine.GetPageRawBytes(handle!, 0);
                Assert.NotNull(raw);
                Assert.Equal(11, raw![2]);
            }
        }

        private static void CreateDummyImages(string folderPath, int count)
        {
            Directory.CreateDirectory(folderPath);
            for (int i = 1; i <= count; i++)
            {
                File.WriteAllBytes(Path.Combine(folderPath, $"{i:D3}.jpg"), new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            }
        }

        private static void WriteMinimalPdf(string outputPath, int pageCount)
        {
            var sb = new StringBuilder();
            var offsets = new List<long>();

            sb.Append("%PDF-1.4\n");

            // Object 1: Catalog
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

            // Object 2: Pages
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append("2 0 obj\n<< /Type /Pages /Kids [");
            for (int i = 0; i < pageCount; i++)
            {
                sb.Append($"{3 + i} 0 R ");
            }
            sb.Append($"] /Count {pageCount} >>\nendobj\n");

            // Objects 3..3+pageCount-1: Page objects
            for (int i = 0; i < pageCount; i++)
            {
                offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
                sb.Append($"{3 + i} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
            }

            long xrefStart = Encoding.ASCII.GetByteCount(sb.ToString());
            int totalObjects = 2 + pageCount;
            sb.Append($"xref\n0 {totalObjects + 1}\n");
            sb.Append("0000000000 65535 f \n");
            foreach (long offset in offsets)
            {
                sb.Append($"{offset:D10} 00000 n \n");
            }

            sb.Append($"trailer\n<< /Size {totalObjects + 1} /Root 1 0 R >>\nstartxref\n{xrefStart}\n%%EOF\n");
            File.WriteAllText(outputPath, sb.ToString(), Encoding.ASCII);
        }

        private sealed class InMemoryStorageService : IStorageService
        {
            private LibraryStore _store = new();

            public Task<LibraryStore> LoadAsync() => Task.FromResult(_store);

            public Task SaveAsync(LibraryStore store)
            {
                _store = store;
                return Task.CompletedTask;
            }

            public Task UpdateProgressAsync(ReadingProgress progress)
            {
                _store.ProgressByComicId[progress.ComicId] = progress;
                return Task.CompletedTask;
            }

            public Task AddBookmarkAsync(string comicId, Bookmark bookmark) => Task.CompletedTask;

            public Task<ReadingProgress?> GetProgressAsync(string comicId)
            {
                _store.ProgressByComicId.TryGetValue(comicId, out ReadingProgress? progress);
                return Task.FromResult(progress);
            }
        }

        private sealed class StubFileService : IFileService
        {
            private readonly IReadOnlyList<string> _pickedFiles;
            private readonly string? _pickedFolder;

            public StubFileService(IReadOnlyList<string> pickedFiles, string? pickedFolder)
            {
                _pickedFiles = pickedFiles;
                _pickedFolder = pickedFolder;
            }

            public Task<IReadOnlyList<string>> PickComicFilesAsync() => Task.FromResult(_pickedFiles);
            public Task<string?> PickFolderAsync() => Task.FromResult(_pickedFolder);
            public IReadOnlyList<string> GetComicFilesInFolder(string folderPath) => Array.Empty<string>();
            public Task<string?> PickImageFileAsync() => Task.FromResult<string?>(null);

            public ComicFormat DetectFormat(string filePath)
            {
                string ext = Path.GetExtension(filePath);
                if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return ComicFormat.Pdf;
                if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)) return ComicFormat.Zip;
                if (ext.Equals(".cbz", StringComparison.OrdinalIgnoreCase)) return ComicFormat.Cbz;
                return ComicFormat.Unknown;
            }

            public Task<string> SaveCoverImageAsync(string comicId, Bitmap coverImage) => Task.FromResult(string.Empty);
            public Task<string> SaveCustomThumbnailBitmapAsync(string comicId, Bitmap coverImage) => Task.FromResult(string.Empty);
            public Task<string> SaveCustomThumbnailAsync(string comicId, string sourceImagePath) => Task.FromResult(string.Empty);
        }
    }
}
