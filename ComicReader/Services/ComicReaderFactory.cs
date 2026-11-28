using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComicReader.Models;
using ComicReader.Services.Interfaces;

namespace ComicReader.Services
{
    /// <summary>
    /// Selects the concrete reader implementation based on the comic format
    /// and discovers chapters (&lt; 200 pages each) for Folder, ZIP/CBZ, and PDF comics.
    /// </summary>
    public sealed class ComicReaderFactory : IComicReaderFactory
    {
        private readonly PdfService _pdfService;
        private readonly ZipService _zipService;
        private readonly FolderImageReaderService _folderService;

        /// <summary>
        /// Initializes a new instance of the <see cref="ComicReaderFactory"/> class.
        /// </summary>
        public ComicReaderFactory(
            PdfService pdfService,
            ZipService zipService,
            FolderImageReaderService? folderService = null)
        {
            _pdfService = pdfService;
            _zipService = zipService;
            _folderService = folderService ?? new FolderImageReaderService();
        }

        /// <inheritdoc />
        public IComicReaderService GetReader(ComicFormat format)
        {
            return format switch
            {
                ComicFormat.Pdf => _pdfService,
                ComicFormat.Zip or ComicFormat.Cbz => _zipService,
                ComicFormat.Folder => _folderService,
                _ => throw new NotSupportedException($"Comic format '{format}' is not supported.")
            };
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<ChapterModel>> DiscoverChaptersAsync(
            ComicBook comic,
            CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<ChapterModel>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                return comic.Format switch
                {
                    ComicFormat.Folder => FolderChapterParser.DiscoverChaptersForSeries(comic.FilePath),
                    ComicFormat.Zip or ComicFormat.Cbz => ZipService.DiscoverChapters(comic.FilePath, comic.Format),
                    ComicFormat.Pdf => PdfService.DiscoverChapters(comic.FilePath),
                    _ => Array.Empty<ChapterModel>()
                };
            }, cancellationToken);
        }
    }
}
