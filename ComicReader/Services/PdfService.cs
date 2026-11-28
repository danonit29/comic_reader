using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ComicReader.Helpers;
using ComicReader.Models;
using ComicReader.Services.Interfaces;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Outline;

namespace ComicReader.Services
{
    /// <summary>
    /// Cross-platform PDF reader service without PDFium:
    /// - Fast path for scanned comic PDFs: extracts embedded JPEG/PNG image bytes directly via PdfPig.
    /// - Fallback path for text/vector PDFs: delegates to the OS-native renderer.
    /// - Also discovers PDF outline chapters and splits oversized sections (&gt;= 200 pages) into &lt; 200 page chapters.
    /// </summary>
    public sealed class PdfService : IComicReaderService
    {
        private readonly INativePdfRenderer? _nativeRenderer;

        public PdfService(INativePdfRenderer? nativeRenderer = null)
        {
            _nativeRenderer = nativeRenderer;
        }

        /// <summary>
        /// Discovers chapters in a PDF using embedded outline bookmarks (if present)
        /// and splits any chapter &gt;= 200 pages so every resulting chapter has &lt; 200 pages.
        /// </summary>
        public static List<ChapterModel> DiscoverChapters(
            string filePath,
            List<string>? warnings = null,
            int maxPagesPerChapter = AppConstants.MaxPagesPerChapter)
        {
            var chapters = new List<ChapterModel>();
            if (!File.Exists(filePath)) return chapters;

            using PdfDocument doc = PdfDocument.Open(filePath);
            int totalPages = doc.NumberOfPages;
            if (totalPages <= 0) return chapters;

            string baseName = Path.GetFileNameWithoutExtension(filePath);
            int orderIndex = 0;

            List<(string Title, int StartPageOneBased)> outlineAnchors = ExtractOutlineAnchors(doc, totalPages);

            if (outlineAnchors.Count >= 2)
            {
                if (outlineAnchors[0].StartPageOneBased > 1)
                {
                    int introPages = outlineAnchors[0].StartPageOneBased - 1;
                    chapters.AddRange(ChapterSplitter.SplitPdfRangeIntoChapters(
                        filePath,
                        "Mở đầu",
                        "__intro__",
                        0,
                        introPages,
                        ref orderIndex,
                        warnings,
                        maxPagesPerChapter));
                }

                for (int i = 0; i < outlineAnchors.Count; i++)
                {
                    int startOneBased = outlineAnchors[i].StartPageOneBased;
                    int nextStartOneBased = i + 1 < outlineAnchors.Count
                        ? outlineAnchors[i + 1].StartPageOneBased
                        : totalPages + 1;
                    int sectionPages = nextStartOneBased - startOneBased;
                    if (sectionPages <= 0) continue;

                    string title = string.IsNullOrWhiteSpace(outlineAnchors[i].Title)
                        ? $"Chương {i + 1}"
                        : outlineAnchors[i].Title.Trim();

                    chapters.AddRange(ChapterSplitter.SplitPdfRangeIntoChapters(
                        filePath,
                        title,
                        $"outline:{startOneBased}:{title}",
                        startOneBased - 1,
                        sectionPages,
                        ref orderIndex,
                        warnings,
                        maxPagesPerChapter));
                }

                if (chapters.Count > 0)
                {
                    return chapters;
                }
            }

            chapters.AddRange(ChapterSplitter.SplitPdfRangeIntoChapters(
                filePath,
                totalPages > Math.Clamp(maxPagesPerChapter, 1, AppConstants.MaxPagesPerChapter)
                    ? "Chương"
                    : baseName,
                baseName,
                0,
                totalPages,
                ref orderIndex,
                warnings,
                maxPagesPerChapter));

            return chapters;
        }

        private static List<(string Title, int StartPageOneBased)> ExtractOutlineAnchors(PdfDocument doc, int totalPages)
        {
            var anchors = new List<(string Title, int StartPageOneBased)>();
            try
            {
                if (!doc.TryGetBookmarks(out Bookmarks? bookmarks) || bookmarks is null || bookmarks.Roots.Count == 0)
                {
                    return anchors;
                }

                IEnumerable<BookmarkNode> candidateNodes = bookmarks.Roots.Count >= 2
                    ? bookmarks.Roots
                    : FlattenBookmarkNodes(bookmarks.Roots);

                var seenPages = new HashSet<int>();
                foreach (DocumentBookmarkNode node in candidateNodes.OfType<DocumentBookmarkNode>())
                {
                    int page = node.PageNumber;
                    if (page < 1 || page > totalPages || !seenPages.Add(page)) continue;
                    anchors.Add((node.Title ?? string.Empty, page));
                }

                anchors.Sort(static (a, b) => a.StartPageOneBased.CompareTo(b.StartPageOneBased));
            }
            catch
            {
                // Ignore malformed PDF outline metadata and fall back to page-count chunking.
            }

            return anchors;
        }

        private static IEnumerable<BookmarkNode> FlattenBookmarkNodes(IEnumerable<BookmarkNode> nodes)
        {
            foreach (BookmarkNode node in nodes)
            {
                yield return node;
                if (node.Children is { Count: > 0 })
                {
                    foreach (BookmarkNode child in FlattenBookmarkNodes(node.Children))
                    {
                        yield return child;
                    }
                }
            }
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
        public Task<IComicReadSession> OpenChapterSessionAsync(
            string sourcePath,
            ChapterModel chapter,
            CancellationToken cancellationToken = default)
        {
            return Task.Run<IComicReadSession>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullSession = new PdfReadSession(sourcePath, _nativeRenderer);
                if (chapter.StartPageIndex == 0 && chapter.PageCount == fullSession.PageCount)
                {
                    return fullSession;
                }

                return new PdfChapterReadSession(fullSession, chapter.StartPageIndex, chapter.PageCount);
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

        private sealed class PdfChapterReadSession : IComicReadSession
        {
            private readonly PdfReadSession _inner;
            private readonly int _startPageIndex;

            public PdfChapterReadSession(PdfReadSession inner, int startPageIndex, int pageCount)
            {
                _inner = inner;
                _startPageIndex = Math.Max(0, startPageIndex);
                PageCount = Math.Clamp(pageCount, 0, Math.Max(0, inner.PageCount - _startPageIndex));
            }

            public int PageCount { get; }

            public bool SupportsDynamicZoomDpi => _inner.SupportsDynamicZoomDpi;

            public Task<byte[]?> ExtractRawBytesAsync(int pageIndex, CancellationToken cancellationToken = default) =>
                pageIndex >= 0 && pageIndex < PageCount
                    ? _inner.ExtractRawBytesAsync(_startPageIndex + pageIndex, cancellationToken)
                    : Task.FromResult<byte[]?>(null);

            public Task<Bitmap?> DecodeFromRawBytesAsync(byte[] rawBytes, CancellationToken cancellationToken = default) =>
                _inner.DecodeFromRawBytesAsync(rawBytes, cancellationToken);

            public Task<Bitmap?> RenderPreviewAsync(
                int pageIndex,
                int maxWidth = AppConstants.PreviewDecodeWidth,
                CancellationToken cancellationToken = default) =>
                pageIndex >= 0 && pageIndex < PageCount
                    ? _inner.RenderPreviewAsync(_startPageIndex + pageIndex, maxWidth, cancellationToken)
                    : Task.FromResult<Bitmap?>(null);

            public Task<Bitmap?> RenderPageAsync(
                int pageIndex,
                int dpi = AppConstants.DefaultRenderDpi,
                CancellationToken cancellationToken = default) =>
                pageIndex >= 0 && pageIndex < PageCount
                    ? _inner.RenderPageAsync(_startPageIndex + pageIndex, dpi, cancellationToken)
                    : Task.FromResult<Bitmap?>(null);

            public void Dispose() => _inner.Dispose();
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
