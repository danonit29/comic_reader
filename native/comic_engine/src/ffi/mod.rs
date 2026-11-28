use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;

use libc::size_t;

#[repr(C)]
pub struct NativeBuffer {
    pub data: *mut u8,
    pub len: size_t,
}

#[repr(C)]
pub struct NativeBitmap {
    pub data: *mut u8,
    pub len: size_t,
    pub width: u32,
    pub height: u32,
    pub stride: u32,
}

pub fn alloc_native_buffer(bytes: Vec<u8>) -> NativeBuffer {
    let len = bytes.len();
    let mut boxed = bytes.into_boxed_slice();
    let data = boxed.as_mut_ptr();
    std::mem::forget(boxed);
    NativeBuffer { data, len }
}

pub fn alloc_native_bitmap(rgba: Vec<u8>, width: u32, height: u32) -> NativeBitmap {
    let len = rgba.len();
    let stride = width.saturating_mul(4);
    let mut boxed = rgba.into_boxed_slice();
    let data = boxed.as_mut_ptr();
    std::mem::forget(boxed);

    NativeBitmap {
        data,
        len,
        width,
        height,
        stride,
    }
}

/// Frees a byte buffer previously allocated by Rust.
///
/// # Safety
/// `buf` must be null or point to a valid `NativeBuffer` returned by `comic_engine`.
pub unsafe fn free_buffer_ptr(buf: *mut NativeBuffer) {
    if buf.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let b = &mut *buf;
        if !b.data.is_null() && b.len > 0 {
            let _ = Box::from_raw(std::ptr::slice_from_raw_parts_mut(b.data, b.len));
        }
        b.data = ptr::null_mut();
        b.len = 0;
    }));
}

/// Frees an RGBA8888 bitmap previously allocated by Rust.
///
/// # Safety
/// `bitmap` must be null or point to a valid `NativeBitmap` returned by `comic_engine`.
pub unsafe fn free_bitmap_ptr(bitmap: *mut NativeBitmap) {
    if bitmap.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        let bmp = &mut *bitmap;
        if !bmp.data.is_null() && bmp.len > 0 {
            let _ = Box::from_raw(std::ptr::slice_from_raw_parts_mut(bmp.data, bmp.len));
        }
        bmp.data = ptr::null_mut();
        bmp.len = 0;
        bmp.width = 0;
        bmp.height = 0;
        bmp.stride = 0;
    }));
}
