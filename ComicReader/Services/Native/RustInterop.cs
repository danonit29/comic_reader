using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ComicReader.Helpers;

namespace ComicReader.Services.Native
{
    /// <summary>
    /// Low-level C# [LibraryImport] P/Invoke bindings to the Rust native core (comic_engine).
    /// </summary>
    internal static partial class RustInterop
    {
        private const string LibraryName = "comic_engine";

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeBuffer
        {
            public IntPtr Data;
            public nuint Len;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeBitmap
        {
            public IntPtr Data;
            public nuint Len;
            public uint Width;
            public uint Height;
            public uint Stride;
        }

        public static bool IsAvailable { get; } = ProbeLibrary();

        private static bool ProbeLibrary()
        {
            try
            {
                if (NativeLibrary.TryLoad(LibraryName, typeof(RustInterop).Assembly, null, out IntPtr handle))
                {
                    NativeLibrary.Free(handle);
                    return true;
                }

                string localPath = Path.Combine(AppContext.BaseDirectory, $"{LibraryName}.dll");
                if (File.Exists(localPath) && NativeLibrary.TryLoad(localPath, out IntPtr localHandle))
                {
                    NativeLibrary.Free(localHandle);
                    return true;
                }
            }
            catch
            {
                // Native library optional; fall back to managed PdfService / ZipService / FolderImageReaderService.
            }

            return false;
        }

        [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr open_document(string pathUtf8);

        [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr open_document_filtered(string pathUtf8, string entriesUtf8);

        [LibraryImport(LibraryName)]
        internal static partial void close_document(IntPtr handle);

        [LibraryImport(LibraryName)]
        internal static partial int get_page_count(SafeDocumentHandle handle);

        [LibraryImport(LibraryName)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static partial bool get_page_bitmap(
            SafeDocumentHandle handle,
            int pageIndex,
            uint maxWidth,
            out NativeBitmap outBitmap);

        [LibraryImport(LibraryName)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static partial bool get_page_raw_bytes(
            SafeDocumentHandle handle,
            int pageIndex,
            out NativeBuffer outBuf);

        [LibraryImport(LibraryName)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static unsafe partial bool decode_image_bytes(
            byte* rawPtr,
            nuint rawLen,
            uint maxWidth,
            out NativeBitmap outBitmap);

        [LibraryImport(LibraryName)]
        internal static partial void free_raw_bytes(ref NativeBuffer buf);

        [LibraryImport(LibraryName)]
        internal static partial void free_bitmap(ref NativeBitmap bitmap);

        public static SafeDocumentHandle? OpenZip(string path, IReadOnlyList<string>? allowedEntries = null)
        {
            if (!IsAvailable) return null;

            IntPtr ptr = allowedEntries is { Count: > 0 }
                ? open_document_filtered(path, string.Join('\n', allowedEntries))
                : open_document(path);

            return ptr != IntPtr.Zero ? new SafeDocumentHandle(ptr) : null;
        }

        public static byte[]? GetPageRawBytes(SafeDocumentHandle handle, int pageIndex)
        {
            if (handle.IsInvalid || handle.IsClosed || pageIndex < 0) return null;

            NativeBuffer buf = default;
            if (!get_page_raw_bytes(handle, pageIndex, out buf))
            {
                return null;
            }

            try
            {
                if (buf.Data == IntPtr.Zero || buf.Len == 0) return null;
                int len = checked((int)buf.Len);
                byte[] managed = new byte[len];
                Marshal.Copy(buf.Data, managed, 0, len);
                return managed;
            }
            finally
            {
                free_raw_bytes(ref buf);
            }
        }

        public static Bitmap? RenderPageToBitmap(
            SafeDocumentHandle handle,
            int pageIndex,
            uint maxWidth = AppConstants.MaxReaderDecodeWidth)
        {
            if (handle.IsInvalid || handle.IsClosed || pageIndex < 0) return null;

            if (!get_page_bitmap(handle, pageIndex, maxWidth, out NativeBitmap nativeBmp))
            {
                return null;
            }

            using var safeBmp = new SafeBitmapHandle(nativeBmp);
            return CopyNativeBitmapToAvalonia(safeBmp);
        }

        public static unsafe Bitmap? DecodeBytesToBitmap(
            byte[] rawBytes,
            uint maxWidth = AppConstants.MaxReaderDecodeWidth)
        {
            if (!IsAvailable || rawBytes is null || rawBytes.Length == 0) return null;

            fixed (byte* ptr = rawBytes)
            {
                if (!decode_image_bytes(ptr, (nuint)rawBytes.Length, maxWidth, out NativeBitmap nativeBmp))
                {
                    return null;
                }

                using var safeBmp = new SafeBitmapHandle(nativeBmp);
                return CopyNativeBitmapToAvalonia(safeBmp);
            }
        }

        private static unsafe Bitmap? CopyNativeBitmapToAvalonia(SafeBitmapHandle safeBmp)
        {
            if (!safeBmp.IsValid) return null;

            ref readonly NativeBitmap nativeBmp = ref safeBmp.Value;
            int width = (int)nativeBmp.Width;
            int height = (int)nativeBmp.Height;
            int srcStride = nativeBmp.Stride > 0 ? (int)nativeBmp.Stride : width * 4;

            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Rgba8888,
                AlphaFormat.Unpremul);

            using ILockedFramebuffer fb = bitmap.Lock();
            byte* srcBase = (byte*)nativeBmp.Data;
            byte* dstBase = (byte*)fb.Address;

            if (fb.RowBytes == srcStride)
            {
                long totalBytes = Math.Min((long)nativeBmp.Len, (long)fb.RowBytes * height);
                Buffer.MemoryCopy(srcBase, dstBase, (long)fb.RowBytes * height, totalBytes);
            }
            else
            {
                int rowCopyBytes = Math.Min(srcStride, fb.RowBytes);
                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(
                        srcBase + (long)y * srcStride,
                        dstBase + (long)y * fb.RowBytes,
                        fb.RowBytes,
                        rowCopyBytes);
                }
            }

            return bitmap;
        }
    }
}
