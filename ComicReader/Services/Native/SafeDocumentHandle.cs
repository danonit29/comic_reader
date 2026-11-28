using System;
using Microsoft.Win32.SafeHandles;

namespace ComicReader.Services.Native
{
    /// <summary>
    /// Managed SafeHandle wrapping a native Rust <c>DocumentHandle*</c> pointer.
    /// Guarantees <c>close_document</c> is invoked exactly once upon Dispose or GC finalization.
    /// </summary>
    public sealed class SafeDocumentHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeDocumentHandle()
            : base(ownsHandle: true)
        {
        }

        internal SafeDocumentHandle(IntPtr preexistingHandle)
            : base(ownsHandle: true)
        {
            SetHandle(preexistingHandle);
        }

        protected override bool ReleaseHandle()
        {
            if (handle != IntPtr.Zero)
            {
                RustInterop.close_document(handle);
                SetHandle(IntPtr.Zero);
            }
            return true;
        }
    }
}
