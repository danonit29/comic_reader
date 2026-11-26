use std::ffi::CStr;
use std::fs::File;
use std::io::Cursor;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::Path;
use std::ptr;
use std::sync::{Arc, Mutex};

use image::imageops::FilterType;
use libc::{c_char, c_int, size_t};
use memmap2::Mmap;
use pdfium_render::prelude::*;
use zip::ZipArchive;

#[repr(C)]
pub struct NativeBitmap {
    pub data: *mut u8,
    pub len: size_t,
    pub width: u32,
    pub height: u32,
    pub stride: u32,
}

enum DocumentBackend {
    Zip {
        mmap: Arc<Mmap>,
        entry_indices: Vec<usize>,
    },
    Pdf {
        // Pdfium is synchronized per document handle to guarantee thread safety.
        inner: Mutex<PdfState>,
    },
}

struct PdfState {
    _pdfium: Box<Pdfium>,
    doc: PdfDocument<'static>,
    page_count: i32,
}

// Safety: PdfState is only accessed while holding its Mutex.
unsafe impl Send for PdfState {}

pub struct DocumentHandle {
    backend: DocumentBackend,
}

fn is_supported_image(name: &str) -> bool {
    let ext = Path::new(name)
        .extension()
        .and_then(|e| e.to_str())
        .map(|e| e.to_ascii_lowercase());
    matches!(ext.as_deref(), Some("jpg" | "jpeg" | "png" | "webp" | "bmp"))
}

fn open_zip_backend(path_str: &str) -> Option<DocumentBackend> {
    let file = File::open(path_str).ok()?;
    let mmap = Arc::new(unsafe { Mmap::map(&file).ok()? });
    let archive = ZipArchive::new(Cursor::new(&mmap[..])).ok()?;

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

    Some(DocumentBackend::Zip { mmap, entry_indices })
}

fn open_pdf_backend(path_str: &str) -> Option<DocumentBackend> {
    let bindings = Pdfium::bind_to_library(Pdfium::pdfium_platform_library_name_at_path("./"))
        .or_else(|_| Pdfium::bind_to_system_library())
        .ok()?;
    let pdfium = Box::new(Pdfium::new(bindings));
    // Leak Box into 'static lifetime; dropped cleanly when PdfState drops.
    let pdfium_ref: &'static Pdfium = unsafe { &*(&*pdfium as *const Pdfium) };
    let doc = pdfium_ref.load_pdf_from_file(path_str, None).ok()?;
    let page_count = doc.pages().len() as i32;

    Some(DocumentBackend::Pdf {
        inner: Mutex::new(PdfState {
            _pdfium: pdfium,
            doc,
            page_count,
        }),
    })
}

fn alloc_native_bitmap(mut rgba: Vec<u8>, width: u32, height: u32) -> NativeBitmap {
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

/// Opens a PDF or ZIP/CBZ document once and returns an opaque handle.
#[no_mangle]
pub unsafe extern "C" fn open_document(path_utf8: *const c_char) -> *mut DocumentHandle {
    if path_utf8.is_null() {
        return ptr::null_mut();
    }

    catch_unwind(AssertUnwindSafe(|| {
        let Ok(path_str) = CStr::from_ptr(path_utf8).to_str() else {
            return ptr::null_mut();
        };

        let is_pdf = Path::new(path_str)
            .extension()
            .and_then(|e| e.to_str())
            .is_some_and(|e| e.eq_ignore_ascii_case("pdf"));

        let backend = if is_pdf {
            open_pdf_backend(path_str)
        } else {
            open_zip_backend(path_str)
        };

        match backend {
            Some(b) => Box::into_raw(Box::new(DocumentHandle { backend: b })),
            None => ptr::null_mut(),
        }
    }))
    .unwrap_or(ptr::null_mut())
}

/// Closes and frees an open DocumentHandle.
#[no_mangle]
pub unsafe extern "C" fn close_document(handle: *mut DocumentHandle) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        drop(Box::from_raw(handle));
    }));
}

/// Returns total number of pages in the open document.
#[no_mangle]
pub unsafe extern "C" fn get_page_count(handle: *const DocumentHandle) -> c_int {
    if handle.is_null() {
        return 0;
    }

    catch_unwind(AssertUnwindSafe(|| {
        let doc = &*handle;
        match &doc.backend {
            DocumentBackend::Zip { entry_indices, .. } => entry_indices.len() as c_int,
            DocumentBackend::Pdf { inner } => {
                inner.lock().map(|s| s.page_count).unwrap_or(0)
            }
        }
    }))
    .unwrap_or(0)
}

/// Extracts and decodes/renders a single page directly into an RGBA8888 NativeBitmap.
/// For ZIP, limits width to `max_width` (e.g. 2000px) preserving aspect ratio.
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

        match &doc.backend {
            DocumentBackend::Zip { mmap, entry_indices } => {
                if idx >= entry_indices.len() {
                    return false;
                }

                let zip_index = entry_indices[idx];
                let Ok(mut archive) = ZipArchive::new(Cursor::new(&mmap[..])) else {
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

                let limit = if max_width > 0 { max_width } else { 2000 };
                if img.width() > limit {
                    let new_h = ((img.height() as u64 * limit as u64) / img.width() as u64).max(1) as u32;
                    img = img.resize(limit, new_h, FilterType::Triangle);
                }

                let rgba = img.to_rgba8();
                let (w, h) = rgba.dimensions();
                *out_bitmap = alloc_native_bitmap(rgba.into_raw(), w, h);
                true
            }
            DocumentBackend::Pdf { inner } => {
                let Ok(state) = inner.lock() else {
                    return false;
                };
                if page_index >= state.page_count {
                    return false;
                }
                let Ok(page) = state.doc.pages().get(page_index as u16) else {
                    return false;
                };

                let limit = if max_width > 0 { max_width as i32 } else { 2000 };
                let config = PdfRenderConfig::new()
                    .set_target_width(limit)
                    .set_maximum_height(limit * 3)
                    .render_form_data(true)
                    .render_annotations(true);

                let Ok(rendered) = page.render_with_config(&config) else {
                    return false;
                };

                let dyn_img = rendered.as_image();
                let rgba = dyn_img.to_rgba8();
                let (w, h) = rgba.dimensions();
                *out_bitmap = alloc_native_bitmap(rgba.into_raw(), w, h);
                true
            }
        }
    }))
    .unwrap_or(false)
}

/// Safely frees the RGBA buffer allocated by Rust and zeroes the struct to prevent double-free.
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
