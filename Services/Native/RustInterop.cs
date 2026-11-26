using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ComicReader.Helpers;

namespace ComicReader.Services.Native
{
    /// <summary>
    /// High-performance C# Interop bridge to the Rust native core (comic_engine.dll).
    /// Uses .NET 8 [LibraryImport] and direct memory copy into Avalonia WriteableBitmap (Rgba8888).
    /// Gracefully reports IsAvailable = false when the native library is not present.
    /// </summary>
    internal static partial class RustInterop
    {
        private const string LibraryName = "comic_engine";

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
                // Native library optional; fall back to managed PdfService / ZipService.
            }

            return false;
        }

        [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr open_document(string pathUtf8);

        [LibraryImport(LibraryName)]
        internal static partial void close_document(IntPtr handle);

        [LibraryImport(LibraryName)]
        internal static partial int get_page_count(IntPtr handle);

        [LibraryImport(LibraryName)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static partial bool get_page_bitmap(
            IntPtr handle,
            int pageIndex,
            uint maxWidth,
            out NativeBitmap outBitmap);

        [LibraryImport(LibraryName)]
        internal static partial void free_bitmap(ref NativeBitmap bitmap);

        /// <summary>
        /// Decodes/renders a page via Rust into RGBA8888 and copies directly into a WriteableBitmap
        /// without intermediate PNG/JPEG/MemoryStream conversions, freeing Rust memory in finally.
        /// </summary>
        public static unsafe Bitmap? RenderPageToBitmap(
            IntPtr handle,
            int pageIndex,
            int dpi = AppConstants.DefaultRenderDpi)
        {
            if (handle == IntPtr.Zero || pageIndex < 0) return null;

            NativeBitmap nativeBmp = default;
            if (!get_page_bitmap(handle, pageIndex, (uint)AppConstants.MaxReaderDecodeWidth, out nativeBmp))
            {
                return null;
            }

            try
            {
                if (nativeBmp.Data == IntPtr.Zero || nativeBmp.Width == 0 || nativeBmp.Height == 0)
                    return null;

                int width = (int)nativeBmp.Width;
                int height = (int)nativeBmp.Height;
                int srcStride = nativeBmp.Stride > 0 ? (int)nativeBmp.Stride : width * 4;

                var bitmap = new WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(dpi, dpi),
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
            finally
            {
                free_bitmap(ref nativeBmp);
            }
        }
    }
}
