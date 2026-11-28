using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ComicReader.Helpers;
using ComicReader.Models;
using ComicReader.Services.Interfaces;
using ComicReader.Services.Native;

namespace ComicReader.Services
{
    /// <summary>
    /// Reads comic pages directly from a folder chapter's image files on disk,
    /// using <see cref="IComicEngine"/> for native decode when available.
    /// </summary>
    public sealed class FolderImageReaderService : IComicReaderService
    {
        private readonly IComicEngine _engine;

        public FolderImageReaderService(IComicEngine? engine = null)
        {
            _engine = engine ?? new RustComicEngine();
        }

        /// <inheritdoc />
        public Task<IComicReadSession> OpenSessionAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.Run<IComicReadSession>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<ChapterModel> chapters = FolderChapterParser.DiscoverChaptersForSeries(filePath);
                IReadOnlyList<string> images = chapters.Count > 0
                    ? chapters[0].ImagePaths
                    : Array.Empty<string>();
                return new FolderChapterReadSession(images, _engine);
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
                IReadOnlyList<string> images = chapter.ImagePaths.Count > 0
                    ? chapter.ImagePaths
                    : Array.Empty<string>();
                return new FolderChapterReadSession(images, _engine);
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<int> GetPageCountAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<ChapterModel> chapters = FolderChapterParser.DiscoverChaptersForSeries(filePath);
                int total = 0;
                foreach (ChapterModel ch in chapters)
                {
                    total += ch.PageCount;
                }
                return total;
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
                List<ChapterModel> chapters = FolderChapterParser.DiscoverChaptersForSeries(filePath);
                if (chapters.Count == 0 || pageIndex < 0 || pageIndex >= chapters[0].ImagePaths.Count)
                {
                    return null;
                }

                byte[] bytes = File.ReadAllBytes(chapters[0].ImagePaths[pageIndex]);
                return ImageHelper.DecodeForReader(bytes);
            }, cancellationToken);
        }

        /// <inheritdoc />
        public Task<Bitmap?> ExtractCoverAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<ChapterModel> chapters = FolderChapterParser.DiscoverChaptersForSeries(filePath);
                foreach (ChapterModel ch in chapters)
                {
                    if (ch.ImagePaths.Count > 0 && File.Exists(ch.ImagePaths[0]))
                    {
                        byte[] bytes = File.ReadAllBytes(ch.ImagePaths[0]);
                        return ImageHelper.DecodeFromBytes(bytes, AppConstants.CoverDecodeWidth);
                    }
                }
                return null;
            }, cancellationToken);
        }

        private sealed class FolderChapterReadSession : IComicReadSession
        {
            private readonly IReadOnlyList<string> _imagePaths;
            private readonly IComicEngine _engine;
            private volatile bool _disposed;

            public FolderChapterReadSession(IReadOnlyList<string> imagePaths, IComicEngine engine)
            {
                _imagePaths = imagePaths;
                _engine = engine;
                PageCount = imagePaths.Count;
            }

            public int PageCount { get; }

            public Task<byte[]?> ExtractRawBytesAsync(int pageIndex, CancellationToken cancellationToken = default)
            {
                return Task.Run<byte[]?>(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed || pageIndex < 0 || pageIndex >= _imagePaths.Count) return null;
                    string path = _imagePaths[pageIndex];
                    return File.Exists(path) ? File.ReadAllBytes(path) : null;
                }, cancellationToken);
            }

            public Task<Bitmap?> DecodeFromRawBytesAsync(byte[] rawBytes, CancellationToken cancellationToken = default)
            {
                return Task.Run<Bitmap?>(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_engine.IsAvailable)
                    {
                        Bitmap? nativeBmp = _engine.DecodeImageBytes(rawBytes, AppConstants.MaxReaderDecodeWidth);
                        if (nativeBmp is not null) return nativeBmp;
                    }
                    return ImageHelper.DecodeFromBytes(rawBytes, null);
                }, cancellationToken);
            }

            public Task<Bitmap?> RenderPreviewAsync(
                int pageIndex,
                int maxWidth = AppConstants.PreviewDecodeWidth,
                CancellationToken cancellationToken = default)
                => RenderCoreAsync(pageIndex, maxWidth, cancellationToken);

            public Task<Bitmap?> RenderPageAsync(
                int pageIndex,
                int dpi = AppConstants.DefaultRenderDpi,
                CancellationToken cancellationToken = default)
                => RenderCoreAsync(pageIndex, AppConstants.MaxReaderDecodeWidth, cancellationToken);

            private Task<Bitmap?> RenderCoreAsync(int pageIndex, int maxWidth, CancellationToken cancellationToken)
            {
                return Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed || pageIndex < 0 || pageIndex >= _imagePaths.Count) return null;

                    string path = _imagePaths[pageIndex];
                    if (!File.Exists(path)) return null;

                    byte[] bytes = File.ReadAllBytes(path);
                    if (_engine.IsAvailable)
                    {
                        Bitmap? nativeBmp = _engine.DecodeImageBytes(bytes, maxWidth > 0 ? (uint)maxWidth : 0u);
                        if (nativeBmp is not null) return nativeBmp;
                    }

                    return maxWidth > 0
                        ? ImageHelper.DecodeFromBytes(bytes, maxWidth)
                        : ImageHelper.DecodeFromBytes(bytes, null);
                }, cancellationToken);
            }

            public void Dispose()
            {
                _disposed = true;
            }
        }
    }
}
