pub mod chapters;
pub mod containers;
pub mod ffi;
pub mod imaging;

use std::collections::HashSet;
use std::ffi::CStr;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;

use libc::{c_char, c_int, size_t};

use crate::chapters::chapters_to_json;
use crate::containers::DocumentHandle;
use crate::ffi::{alloc_native_buffer, free_bitmap_ptr, free_buffer_ptr, NativeBitmap, NativeBuffer};
use crate::imaging::decode_bytes_to_bitmap;

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

        DocumentHandle::open(path_str, None)
            .map(|doc| Box::into_raw(Box::new(doc)))
            .unwrap_or(ptr::null_mut())
    }))
    .unwrap_or(ptr::null_mut())
}

/// Opens a ZIP/CBZ archive scoped strictly to newline-separated `entries_utf8` (for chapter reading).
#[no_mangle]
pub unsafe extern "C" fn open_document_filtered(
    path_utf8: *const c_char,
    entries_utf8: *const c_char,
) -> *mut DocumentHandle {
    if path_utf8.is_null() {
        return ptr::null_mut();
    }

    catch_unwind(AssertUnwindSafe(|| {
        let Ok(path_str) = CStr::from_ptr(path_utf8).to_str() else {
            return ptr::null_mut();
        };

        let filter: Option<HashSet<String>> = if entries_utf8.is_null() {
            None
        } else if let Ok(raw_list) = CStr::from_ptr(entries_utf8).to_str() {
            let set: HashSet<String> = raw_list
                .lines()
                .map(|l| l.trim())
                .filter(|l| !l.is_empty())
                .map(|l| l.to_string())
                .collect();
            if set.is_empty() { None } else { Some(set) }
        } else {
            None
        };

        DocumentHandle::open(path_str, filter.as_ref())
            .map(|doc| Box::into_raw(Box::new(doc)))
            .unwrap_or(ptr::null_mut())
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

    catch_unwind(AssertUnwindSafe(|| (*handle).page_count() as c_int)).unwrap_or(0)
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
        let Some(encoded) = doc.read_page_raw(page_index as usize) else {
            return false;
        };
        let Some(bmp) = decode_bytes_to_bitmap(&encoded, max_width) else {
            return false;
        };
        *out_bitmap = bmp;
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
        let Some(encoded) = doc.read_page_raw(page_index as usize) else {
            return false;
        };
        *out_buf = alloc_native_buffer(encoded);
        true
    }))
    .unwrap_or(false)
}

/// Decodes raw compressed image bytes (from folder or memory cache) into an RGBA8888 `NativeBitmap`.
#[no_mangle]
pub unsafe extern "C" fn decode_image_bytes(
    raw_ptr: *const u8,
    raw_len: size_t,
    max_width: u32,
    out_bitmap: *mut NativeBitmap,
) -> bool {
    if raw_ptr.is_null() || raw_len == 0 || out_bitmap.is_null() {
        return false;
    }

    catch_unwind(AssertUnwindSafe(|| {
        let slice = std::slice::from_raw_parts(raw_ptr, raw_len);
        let Some(bmp) = decode_bytes_to_bitmap(slice, max_width) else {
            return false;
        };
        *out_bitmap = bmp;
        true
    }))
    .unwrap_or(false)
}

/// Discovers chapters inside a ZIP/CBZ archive (< 200 pages per chapter) and returns UTF-8 JSON.
#[no_mangle]
pub unsafe extern "C" fn discover_zip_chapters_json(
    path_utf8: *const c_char,
    max_pages: c_int,
    out_json: *mut NativeBuffer,
) -> bool {
    if path_utf8.is_null() || out_json.is_null() {
        return false;
    }

    catch_unwind(AssertUnwindSafe(|| {
        let Ok(path_str) = CStr::from_ptr(path_utf8).to_str() else {
            return false;
        };
        let limit = if max_pages <= 0 { 199 } else { max_pages as usize };
        let Some(chapters) = DocumentHandle::discover_zip_chapters(path_str, limit) else {
            return false;
        };
        let json = chapters_to_json(&chapters);
        *out_json = alloc_native_buffer(json.into_bytes());
        true
    }))
    .unwrap_or(false)
}

#[no_mangle]
pub unsafe extern "C" fn free_raw_bytes(buf: *mut NativeBuffer) {
    free_buffer_ptr(buf);
}

#[no_mangle]
pub unsafe extern "C" fn free_bitmap(bitmap: *mut NativeBitmap) {
    free_bitmap_ptr(bitmap);
}
