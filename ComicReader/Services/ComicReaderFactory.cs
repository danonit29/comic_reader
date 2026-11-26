using System;
using ComicReader.Models;
using ComicReader.Services.Interfaces;

namespace ComicReader.Services
{
    /// <summary>
    /// Selects the concrete reader implementation based on the comic format.
    /// </summary>
    public sealed class ComicReaderFactory : IComicReaderFactory
    {
        private readonly PdfService _pdfService;
        private readonly ZipService _zipService;

        /// <summary>
        /// Initializes a new instance of the <see cref="ComicReaderFactory"/> class.
        /// </summary>
        public ComicReaderFactory(PdfService pdfService, ZipService zipService)
        {
            _pdfService = pdfService;
            _zipService = zipService;
        }

        /// <inheritdoc />
        public IComicReaderService GetReader(ComicFormat format)
        {
            return format switch
            {
                ComicFormat.Pdf => _pdfService,
                ComicFormat.Zip or ComicFormat.Cbz => _zipService,
                _ => throw new NotSupportedException($"Comic format '{format}' is not supported.")
            };
        }
    }
}
