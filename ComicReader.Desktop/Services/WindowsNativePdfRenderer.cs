using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ComicReader.Helpers;
using ComicReader.Services.Interfaces;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ComicReader.Desktop.Services
{
    /// <summary>
    /// Windows 10/11 built-in WinRT PDF renderer (Windows.Data.Pdf) requiring 0 KB of external native binaries.
    /// </summary>
    public sealed class WindowsNativePdfRenderer : INativePdfRenderer
    {
        public async Task<INativePdfSession?> OpenSessionAsync(string filePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageFile storageFile = await StorageFile.GetFileFromPathAsync(filePath).AsTask(cancellationToken);
            PdfDocument pdfDoc = await PdfDocument.LoadFromFileAsync(storageFile).AsTask(cancellationToken);
            return new WindowsNativePdfSession(pdfDoc);
        }

        private sealed class WindowsNativePdfSession : INativePdfSession
        {
            private readonly PdfDocument _pdfDoc;
            private readonly SemaphoreSlim _renderLock = new(1, 1);
            private bool _disposed;

            public WindowsNativePdfSession(PdfDocument pdfDoc)
            {
                _pdfDoc = pdfDoc;
                PageCount = (int)_pdfDoc.PageCount;
            }

            public int PageCount { get; }

            public async Task<Bitmap?> RenderPageAsync(int pageIndex, int dpi, CancellationToken cancellationToken = default)
            {
                if (_disposed || pageIndex < 0 || pageIndex >= PageCount) return null;

                await _renderLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_disposed || cancellationToken.IsCancellationRequested) return null;

                    using PdfPage page = _pdfDoc.GetPage((uint)pageIndex);
                    double scale = Math.Max(0.4, dpi / 72.0);
                    uint destW = (uint)Math.Clamp(Math.Round(page.Size.Width * scale), 64, 4096);
                    uint destH = (uint)Math.Clamp(Math.Round(page.Size.Height * scale), 64, 8192);

                    using var stream = new InMemoryRandomAccessStream();
                    var options = new PdfPageRenderOptions
                    {
                        DestinationWidth = destW,
                        DestinationHeight = destH
                    };

                    await page.RenderToStreamAsync(stream, options).AsTask(cancellationToken).ConfigureAwait(false);
                    stream.Seek(0);

                    using Stream netStream = stream.AsStreamForRead();
                    return ImageHelper.DecodeFromStream(netStream, null);
                }
                finally
                {
                    _renderLock.Release();
                }
            }

            public void Dispose()
            {
                _disposed = true;
                _renderLock.Dispose();
            }
        }
    }
}
