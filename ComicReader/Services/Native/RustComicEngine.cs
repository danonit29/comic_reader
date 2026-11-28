using System.Collections.Generic;
using Avalonia.Media.Imaging;

namespace ComicReader.Services.Native
{
    /// <summary>
    /// Layer 3 concrete <see cref="IComicEngine"/> adapter backed by the native Rust <c>comic_engine</c> library.
    /// </summary>
    public sealed class RustComicEngine : IComicEngine
    {
        /// <inheritdoc />
        public bool IsAvailable => RustInterop.IsAvailable;

        /// <inheritdoc />
        public SafeDocumentHandle? OpenZipDocument(string filePath, IReadOnlyList<string>? allowedEntryNames = null) =>
            RustInterop.OpenZip(filePath, allowedEntryNames);

        /// <inheritdoc />
        public int GetPageCount(SafeDocumentHandle handle) =>
            handle.IsInvalid || handle.IsClosed ? 0 : RustInterop.get_page_count(handle);

        /// <inheritdoc />
        public byte[]? GetPageRawBytes(SafeDocumentHandle handle, int pageIndex) =>
            RustInterop.GetPageRawBytes(handle, pageIndex);

        /// <inheritdoc />
        public Bitmap? RenderPageToBitmap(SafeDocumentHandle handle, int pageIndex, uint maxWidth = 0) =>
            RustInterop.RenderPageToBitmap(handle, pageIndex, maxWidth);

        /// <inheritdoc />
        public Bitmap? DecodeImageBytes(byte[] rawBytes, uint maxWidth = 0) =>
            RustInterop.DecodeBytesToBitmap(rawBytes, maxWidth);
    }
}
