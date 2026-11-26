using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.Graphics.Pdf;
using Android.OS;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ComicReader.Services.Interfaces;

namespace ComicReader.Android.Services
{
    /// <summary>
    /// Android OS built-in PDF renderer (android.graphics.pdf.PdfRenderer) requiring 0 KB of external native libraries.
    /// </summary>
    public sealed class AndroidNativePdfRenderer : INativePdfRenderer
    {
        public Task<INativePdfSession?> OpenSessionAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.Run<INativePdfSession?>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var javaFile = new Java.IO.File(filePath);
                var pfd = ParcelFileDescriptor.Open(javaFile, ParcelFileMode.ReadOnly);
                if (pfd is null) return null;

                var renderer = new PdfRenderer(pfd);
                return new AndroidNativePdfSession(pfd, renderer);
            }, cancellationToken);
        }

        private sealed class AndroidNativePdfSession : INativePdfSession
        {
            private readonly ParcelFileDescriptor _pfd;
            private readonly PdfRenderer _renderer;
            private readonly object _renderLock = new();
            private bool _disposed;

            public AndroidNativePdfSession(ParcelFileDescriptor pfd, PdfRenderer renderer)
            {
                _pfd = pfd;
                _renderer = renderer;
                PageCount = renderer.PageCount;
            }

            public int PageCount { get; }

            public Task<Bitmap?> RenderPageAsync(int pageIndex, int dpi, CancellationToken cancellationToken = default)
            {
                return Task.Run<Bitmap?>(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pageIndex < 0 || pageIndex >= PageCount) return null;

                    lock (_renderLock)
                    {
                        if (_disposed || cancellationToken.IsCancellationRequested) return null;

                        using PdfRenderer.Page page = _renderer.OpenPage(pageIndex);
                        double scale = Math.Max(0.4, dpi / 72.0);
                        int width = Math.Clamp((int)Math.Round(page.Width * scale), 64, 4096);
                        int height = Math.Clamp((int)Math.Round(page.Height * scale), 64, 8192);

                        using var androidBmp = global::Android.Graphics.Bitmap.CreateBitmap(
                            width,
                            height,
                            global::Android.Graphics.Bitmap.Config.Argb8888!);

                        androidBmp.EraseColor(global::Android.Graphics.Color.White);
                        page.Render(androidBmp, null, null, PdfRenderMode.ForDisplay);

                        using var ms = new MemoryStream();
                        androidBmp.Compress(global::Android.Graphics.Bitmap.CompressFormat.Png!, 100, ms);
                        ms.Position = 0;
                        return new Bitmap(ms);
                    }
                }, cancellationToken);
            }

            public void Dispose()
            {
                lock (_renderLock)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _renderer.Close();
                    _renderer.Dispose();
                    _pfd.Close();
                    _pfd.Dispose();
                }
            }
        }
    }
}
