use image::imageops::FilterType;

use crate::ffi::{alloc_native_bitmap, NativeBitmap};

/// Decodes encoded image bytes (JPEG/PNG/WebP/BMP) into an RGBA8888 `NativeBitmap`.
/// When `max_width > 0` and image width exceeds `max_width`, downscales preserving aspect ratio.
pub fn decode_bytes_to_bitmap(encoded: &[u8], max_width: u32) -> Option<NativeBitmap> {
    let mut img = image::load_from_memory(encoded).ok()?;

    if max_width > 0 && img.width() > max_width {
        let new_h = ((img.height() as u64 * max_width as u64) / img.width() as u64).max(1) as u32;
        img = img.resize(max_width, new_h, FilterType::Triangle);
    }

    let rgba = img.to_rgba8();
    let (w, h) = rgba.dimensions();
    Some(alloc_native_bitmap(rgba.into_raw(), w, h))
}
