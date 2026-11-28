using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;

namespace ComicReader.Services.Native
{
    /// <summary>
    /// Layer 3 native engine abstraction isolating C# Application Services from raw P/Invoke details.
    /// Provides automatic fallback when native binaries are unavailable.
    /// </summary>
    public interface IComicEngine
    {
        /// <summary>
        /// True when the native Rust engine library (comic_engine.dll / libcomic_engine.so) is loaded.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Opens a ZIP/CBZ archive natively, optionally scoped to specific chapter entries.
        /// Returns null if the native engine is unavailable or fails to open.
        /// </summary>
        SafeDocumentHandle? OpenZipDocument(string filePath, IReadOnlyList<string>? allowedEntryNames = null);

        /// <summary>
        /// Returns the page count for an open native document handle.
        /// </summary>
        int GetPageCount(SafeDocumentHandle handle);

        /// <summary>
        /// Extracts raw compressed image bytes from an open native document handle.
        /// </summary>
        byte[]? GetPageRawBytes(SafeDocumentHandle handle, int pageIndex);

        /// <summary>
        /// Decodes and optionally resizes a page from an open native document handle directly into a Bitmap.
        /// </summary>
        Bitmap? RenderPageToBitmap(SafeDocumentHandle handle, int pageIndex, uint maxWidth = 0);

        /// <summary>
        /// Decodes compressed image bytes directly via Rust into an Avalonia Bitmap.
        /// </summary>
        Bitmap? DecodeImageBytes(byte[] rawBytes, uint maxWidth = 0);
    }
}
