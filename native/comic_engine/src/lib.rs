use std::ffi::CStr;
use std::fs::File;
use std::io::BufReader;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::Path;
use std::ptr;

use image::imageops::FilterType;
use libc::{c_char, c_int, size_t};
use zip::ZipArchive;

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

pub struct DocumentHandle {
    file_path: String,
    entry_indices: Vec<usize>,
}

fn is_supported_image(name: &str) -> bool {
    let ext = Path::new(name)
        .extension()
        .and_then(|e| e.to_str())
        .map(|e| e.to_ascii_lowercase());
    matches!(ext.as_deref(), Some("jpg" | "jpeg" | "png" | "webp" | "bmp"))
}

fn alloc_native_bitmap(rgba: Vec<u8>, width: u32, height: u32) -> NativeBitmap {
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

/// Opens a ZIP/CBZ archive and pre-indexes all supported image entries in natural order.
#[no_mangle]
pub unsafe extern "C" fn open_document(path_utf8: *const c_char) -> *mut DocumentHandle {
    if path_utf8.is_null() {
        return ptr::null_mut();
    }

    catch_unwind(AssertUnwindSafe(|| {
        let Ok(path_str) = CStr::from_ptr(path_utf8).to_str() else {
            return ptr::null_mut();
        };

        let Ok(file) = File::open(path_str) else {
            return ptr::null_mut();
        };
        let Ok(mut archive) = ZipArchive::new(BufReader::new(file)) else {
            return ptr::null_mut();
        };

        let mut entries: Vec<(usize, String)> = Vec::new();
        for i in 0..archive.len() {
            if let Ok(entry) = archive.by_index_raw(i) {
                if !entry.is_dir() && is_supported_image(entry.name()) {
                    entries.push((i, entry.name().to_string()));
                }
            }
        }

        entries.sort_by(|a, b| natord::compare_ignore_case(&a.1, &b.1));
        let entry_indices = entries.into_iter().map(|(idx, _)| idx).collect();

        Box::into_raw(Box::new(DocumentHandle {
            file_path: path_str.to_string(),
            entry_indices,
        }))
    }))
    .unwrap_or(ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn close_document(handle: *mut DocumentHandle) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        drop(Box::from_raw(handle));
    }));
}

#[no_mangle]
pub unsafe extern "C" fn get_page_count(handle: *const DocumentHandle) -> c_int {
    if handle.is_null() {
        return 0;
    }

    catch_unwind(AssertUnwindSafe(|| (*handle).entry_indices.len() as c_int)).unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn get_page_bitmap(
    handle: *const DocumentHandle,
    page_index: c_int,
    max_width: u32,
    out_bitmap: *mut NativeBitmap,
) -> bool {
    if handle.is_null() || out_bitmap.is_null() || page_index < 0 {
        return false;
    }

    catch_unwind(AssertUnwindSafe(|| {
        let doc = &*handle;
        let idx = page_index as usize;
        if idx >= doc.entry_indices.len() {
            return false;
        }

        let zip_index = doc.entry_indices[idx];
        let Ok(file) = File::open(&doc.file_path) else {
            return false;
        };
        let Ok(mut archive) = ZipArchive::new(BufReader::new(file)) else {
            return false;
        };
        let Ok(mut entry) = archive.by_index(zip_index) else {
            return false;
        };

        let mut encoded = Vec::with_capacity(entry.size() as usize);
        if std::io::Read::read_to_end(&mut entry, &mut encoded).is_err() {
            return false;
        }

        let Ok(mut img) = image::load_from_memory(&encoded) else {
            return false;
        };

        // max_width == 0 keeps 100% original resolution for Full Quality tier.
        if max_width > 0 && img.width() > max_width {
            let new_h = ((img.height() as u64 * max_width as u64) / img.width() as u64).max(1) as u32;
            img = img.resize(max_width, new_h, FilterType::Triangle);
        }

        let rgba = img.to_rgba8();
        let (w, h) = rgba.dimensions();
        *out_bitmap = alloc_native_bitmap(rgba.into_raw(), w, h);
        true
    }))
    .unwrap_or(false)
}

#[no_mangle]
pub unsafe extern "C" fn get_page_raw_bytes(
    handle: *const DocumentHandle,
    page_index: c_int,
    out_buf: *mut NativeBuffer,
) -> bool {
    if handle.is_null() || out_buf.is_null() || page_index < 0 {
        return false;
    }

    catch_unwind(AssertUnwindSafe(|| {
        let doc = &*handle;
        let idx = page_index as usize;
        if idx >= doc.entry_indices.len() {
            return false;
        }

        let zip_index = doc.entry_indices[idx];
        let Ok(file) = File::open(&doc.file_path) else {
            return false;
        };
        let Ok(mut archive) = ZipArchive::new(BufReader::new(file)) else {
            return false;
        };
        let Ok(mut entry) = archive.by_index(zip_index) else {
            return false;
        };

        let mut encoded = Vec::with_capacity(entry.size() as usize);
        if std::io::Read::read_to_end(&mut entry, &mut encoded).is_err() {
            return false;
        }

        let len = encoded.len();
        let mut boxed = encoded.into_boxed_slice();
        let data = boxed.as_mut_ptr();
        std::mem::forget(boxed);
        *out_buf = NativeBuffer { data, len };
        true
    }))
    .unwrap_or(false)
}

#[no_mangle]
pub unsafe extern "C" fn free_raw_bytes(buf: *mut NativeBuffer) {
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

#[no_mangle]
pub unsafe extern "C" fn free_bitmap(bitmap: *mut NativeBitmap) {
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
