using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ComicReader.Helpers;
using ComicReader.Services.Interfaces;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ComicReader.Services
{
    /// <summary>
    /// Cross-platform PDF reader service without PDFium:
    /// - Fast path for scanned comic PDFs: extracts embedded JPEG/PNG image bytes directly via PdfPig
    ///   (100% original quality, lock-free RAM compressed byte cache just like ZIP/CBZ).
    /// - Fallback path for text/vector PDFs: delegates to the OS-native renderer
    ///   (Windows.Data.Pdf on Windows, android.graphics.pdf.PdfRenderer on Android).
    /// </summary>
    public sealed class PdfService : IComicReaderService
    {
        private readonly INativePdfRenderer? _nativeRenderer;

        public PdfService(INativePdfRenderer? nativeRenderer = null)
        {
            _nativeRenderer = nativeRenderer;
        }

        /// <inheritdoc />
        public Task<IComicReadSession> OpenSessionAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.Run<IComicReadSession>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new PdfReadSession(filePath, _nativeRenderer);
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<int> GetPageCountAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using PdfDocument doc = PdfDocument.Open(filePath);
                return doc.NumberOfPages;
            }, cancellationToken);
        }

        /// <inheritdoc />
        public async Task<Bitmap?> RenderPageAsync(
            string filePath,
            int pageIndex,
            int dpi = AppConstants.DefaultRenderDpi,
            CancellationToken cancellationToken = default)
        {
            using IComicReadSession session = await OpenSessionAsync(filePath, cancellationToken).ConfigureAwait(false);
            return await session.RenderPageAsync(pageIndex, dpi, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Bitmap?> ExtractCoverAsync(string filePath, CancellationToken cancellationToken = default)
        {
            using IComicReadSession session = await OpenSessionAsync(filePath, cancellationToken).ConfigureAwait(false);
            return await session.RenderPreviewAsync(0, AppConstants.CoverDecodeWidth, cancellationToken).ConfigureAwait(false);
        }

        private static byte[]? TryExtractSingleScanImageBytes(PdfDocument doc, int pageIndex)
        {
            if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) return null;

            Page page = doc.GetPage(pageIndex + 1);
            if (page.Letters.Count > 0) return null;

            IReadOnlyList<IPdfImage> images = page.GetImages().ToList();
            if (images.Count != 1) return null;

            IPdfImage img = images[0];
            ReadOnlySpan<byte> raw = img.RawBytes;
            if (raw.Length >= 3 && raw[0] == 0xFF && raw[1] == 0xD8)
            {
                return raw.ToArray();
            }

            if (img.TryGetPng(out byte[]? pngBytes) && pngBytes is { Length: > 0 })
            {
                return pngBytes;
            }

            return null;
        }

        private sealed class PdfReadSession : IComicReadSession
        {
            private readonly string _filePath;
            private readonly INativePdfRenderer? _nativeRenderer;
            private readonly PdfDocument _pdfDoc;
            private readonly object _pdfPigLock = new();
            private readonly SemaphoreSlim _nativeOpenLock = new(1, 1);
            private INativePdfSession? _nativeSession;
            private bool _disposed;

            public PdfReadSession(string filePath, INativePdfRenderer? nativeRenderer)
            {
                _filePath = filePath;
                _nativeRenderer = nativeRenderer;
                _pdfDoc = PdfDocument.Open(filePath);
                PageCount = _pdfDoc.NumberOfPages;
            }

            public int PageCount { get; }

            public bool SupportsDynamicZoomDpi => true;

            public Task<byte[]?> ExtractRawBytesAsync(int pageIndex, CancellationToken cancellationToken = default)
            {
                return Task.Run<byte[]?>(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (_pdfPigLock)
                    {
                        if (_disposed) return null;
                        return TryExtractSingleScanImageBytes(_pdfDoc, pageIndex);
                    }
                }, cancellationToken);
            }

            public Task<Bitmap?> DecodeFromRawBytesAsync(byte[] rawBytes, CancellationToken cancellationToken = default)
            {
                return Task.Run<Bitmap?>(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ImageHelper.DecodeFromBytes(rawBytes, null);
                }, cancellationToken);
            }

            public async Task<Bitmap?> RenderPreviewAsync(
                int pageIndex,
                int maxWidth = AppConstants.PreviewDecodeWidth,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? scanBytes = await ExtractRawBytesAsync(pageIndex, cancellationToken).ConfigureAwait(false);
                if (scanBytes is not null)
                {
                    return await Task.Run(() => ImageHelper.DecodeFromBytes(scanBytes, maxWidth), cancellationToken).ConfigureAwait(false);
                }

                INativePdfSession? native = await EnsureNativeSessionAsync(cancellationToken).ConfigureAwait(false);
                return native is not null
                    ? await native.RenderPageAsync(pageIndex, AppConstants.PreviewRenderDpi, cancellationToken).ConfigureAwait(false)
                    : null;
            }

            public async Task<Bitmap?> RenderPageAsync(
                int pageIndex,
                int dpi = AppConstants.DefaultRenderDpi,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? scanBytes = await ExtractRawBytesAsync(pageIndex, cancellationToken).ConfigureAwait(false);
                if (scanBytes is not null)
                {
                    // Return 100% original untouched scan resolution for Full Quality tier.
                    return await Task.Run(() => ImageHelper.DecodeFromBytes(scanBytes, null), cancellationToken).ConfigureAwait(false);
                }

                INativePdfSession? native = await EnsureNativeSessionAsync(cancellationToken).ConfigureAwait(false);
                return native is not null
                    ? await native.RenderPageAsync(pageIndex, dpi, cancellationToken).ConfigureAwait(false)
                    : null;
            }

            private async Task<INativePdfSession?> EnsureNativeSessionAsync(CancellationToken cancellationToken)
            {
                if (_nativeSession is not null || _nativeRenderer is null || _disposed)
                    return _nativeSession;

                await _nativeOpenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_nativeSession is null && !_disposed)
                    {
                        _nativeSession = await _nativeRenderer.OpenSessionAsync(_filePath, cancellationToken).ConfigureAwait(false);
                    }
                    return _nativeSession;
                }
                finally
                {
                    _nativeOpenLock.Release();
                }
            }

            public void Dispose()
            {
                lock (_pdfPigLock)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _pdfDoc.Dispose();
                }

                _nativeSession?.Dispose();
                _nativeSession = null;
                _nativeOpenLock.Dispose();
            }
        }
    }
}
