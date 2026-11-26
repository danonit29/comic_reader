using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ComicReader.Helpers;
using ComicReader.Services.Interfaces;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace ComicReader.Services
{
    /// <summary>
    /// Cross-platform PDF rendering service built on Docnet.Core (PDFium) that converts PDF pages
    /// into Skia-backed Avalonia Bitmaps with session-level caching.
    /// </summary>
    public sealed class PdfService : IComicReaderService
    {
        // Docnet.Core wraps native PDFium whose state must be synchronized across threads.
        private static readonly object DocnetLock = new();
        private static readonly NaiveTransparencyRemover WhiteBackgroundConverter = new(255, 255, 255);

        /// <inheritdoc />
        public Task<IComicReadSession> OpenSessionAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.Run<IComicReadSession>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new PdfReadSession(filePath);
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<int> GetPageCountAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (DocnetLock)
                {
                    using IDocReader docReader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(1.0));
                    return docReader.GetPageCount();
                }
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<Bitmap?> RenderPageAsync(
            string filePath,
            int pageIndex,
            int dpi = AppConstants.DefaultRenderDpi,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                double scalingFactor = Math.Max(0.5, dpi / 72.0);

                lock (DocnetLock)
                {
                    using IDocReader docReader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(scalingFactor));
                    return RenderFromReader(docReader, pageIndex, dpi);
                }
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<Bitmap?> ExtractCoverAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (DocnetLock)
                {
                    using IDocReader docReader = DocLib.Instance.GetDocReader(
                        filePath,
                        new PageDimensions(AppConstants.CoverDecodeWidth, AppConstants.CoverDecodeWidth * 2));

                    return RenderFromReader(docReader, 0, AppConstants.CoverRenderDpi);
                }
            }, cancellationToken);
        }

        internal static Bitmap? RenderFromReader(IDocReader docReader, int pageIndex, int dpi)
        {
            int pageCount = docReader.GetPageCount();
            if (pageIndex < 0 || pageIndex >= pageCount)
            {
                return null;
            }

            using IPageReader pageReader = docReader.GetPageReader(pageIndex);
            int width = pageReader.GetPageWidth();
            int height = pageReader.GetPageHeight();
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            byte[] rawBgra = pageReader.GetImage(WhiteBackgroundConverter, RenderFlags.RenderAnnotations);
            return ImageHelper.CreateFromBgra32(rawBgra, width, height, dpi);
        }

        private sealed class PdfReadSession : IComicReadSession
        {
            private readonly string _filePath;
            private IDocReader? _docReader;
            private double _loadedScalingFactor;
            private bool _disposed;

            public PdfReadSession(string filePath)
            {
                _filePath = filePath;
                _loadedScalingFactor = AppConstants.DefaultRenderDpi / 72.0;

                lock (DocnetLock)
                {
                    _docReader = DocLib.Instance.GetDocReader(_filePath, new PageDimensions(_loadedScalingFactor));
                    PageCount = _docReader.GetPageCount();
                }
            }

            public int PageCount { get; }

            public Task<Bitmap?> RenderPageAsync(
                int pageIndex,
                int dpi = AppConstants.DefaultRenderDpi,
                CancellationToken cancellationToken = default)
            {
                return Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    double requestedScale = Math.Max(0.5, dpi / 72.0);
                    bool lockTaken = false;
                    try
                    {
                        while (!lockTaken)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            Monitor.TryEnter(DocnetLock, 25, ref lockTaken);
                        }

                        if (_disposed || cancellationToken.IsCancellationRequested) return null;

                        if (_docReader is null || Math.Abs(requestedScale - _loadedScalingFactor) > 0.01)
                        {
                            _docReader?.Dispose();
                            _loadedScalingFactor = requestedScale;
                            _docReader = DocLib.Instance.GetDocReader(_filePath, new PageDimensions(_loadedScalingFactor));
                        }

                        return RenderFromReader(_docReader, pageIndex, dpi);
                    }
                    finally
                    {
                        if (lockTaken)
                        {
                            Monitor.Exit(DocnetLock);
                        }
                    }
                }, cancellationToken);
            }

            public void Dispose()
            {
                lock (DocnetLock)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _docReader?.Dispose();
                    _docReader = null;
                }
            }
        }
    }
}
