using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace ComicReader.Services.Interfaces
{
    /// <summary>
    /// Platform-native OS PDF rendering session (Windows.Data.Pdf on Windows, PdfRenderer on Android)
    /// used when a PDF page contains vector graphics, fonts, or multi-layer content.
    /// </summary>
    public interface INativePdfSession : IDisposable
    {
        int PageCount { get; }

        Task<Bitmap?> RenderPageAsync(int pageIndex, int dpi, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Factory for opening platform-native OS PDF sessions without third-party PDFium binaries.
    /// </summary>
    public interface INativePdfRenderer
    {
        Task<INativePdfSession?> OpenSessionAsync(string filePath, CancellationToken cancellationToken = default);
    }
}
