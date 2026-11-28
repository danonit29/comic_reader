using System;

namespace ComicReader.Services.Native
{
    /// <summary>
    /// RAII wrapper around Rust-allocated <see cref="RustInterop.NativeBitmap"/> memory.
    /// Ensures <c>free_bitmap</c> is always executed when leaving scope.
    /// </summary>
    internal sealed class SafeBitmapHandle : IDisposable
    {
        private RustInterop.NativeBitmap _bitmap;
        private bool _disposed;

        public SafeBitmapHandle(RustInterop.NativeBitmap bitmap)
        {
            _bitmap = bitmap;
        }

        public ref readonly RustInterop.NativeBitmap Value => ref _bitmap;

        public bool IsValid =>
            !_disposed &&
            _bitmap.Data != IntPtr.Zero &&
            _bitmap.Width > 0 &&
            _bitmap.Height > 0;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            RustInterop.free_bitmap(ref _bitmap);
        }
    }
}
