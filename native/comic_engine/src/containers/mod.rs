use std::collections::HashSet;
use std::fs::File;
use std::io::{BufReader, Read};
use std::path::Path;
use std::sync::Mutex;

use zip::ZipArchive;

use crate::chapters::{is_supported_image, split_entries_into_chapters, ChapterSlice};

/// Holds an open `ZipArchive` inside a `Mutex` so random page reads do not re-open
/// and re-parse the ZIP central directory on every single page.
pub struct DocumentHandle {
    archive: Mutex<ZipArchive<BufReader<File>>>,
    entry_indices: Vec<usize>,
}

impl DocumentHandle {
    pub fn open(path: &str, allowed_entries: Option<&HashSet<String>>) -> Option<Self> {
        let file = File::open(path).ok()?;
        let mut archive = ZipArchive::new(BufReader::new(file)).ok()?;

        let mut entries: Vec<(usize, String)> = Vec::new();
        for i in 0..archive.len() {
            if let Ok(entry) = archive.by_index_raw(i) {
                if !entry.is_dir() && is_supported_image(entry.name()) {
                    let full_name = entry.name().to_string();
                    if let Some(filter) = allowed_entries {
                        if !filter.contains(&full_name) {
                            continue;
                        }
                    }
                    entries.push((i, full_name));
                }
            }
        }

        entries.sort_by(|a, b| natord::compare_ignore_case(&a.1, &b.1));
        let entry_indices = entries.into_iter().map(|(idx, _)| idx).collect();

        Some(Self {
            archive: Mutex::new(archive),
            entry_indices,
        })
    }

    pub fn page_count(&self) -> usize {
        self.entry_indices.len()
    }

    pub fn read_page_raw(&self, page_index: usize) -> Option<Vec<u8>> {
        let &zip_index = self.entry_indices.get(page_index)?;
        let mut archive = self.archive.lock().ok()?;
        let mut entry = archive.by_index(zip_index).ok()?;
        let mut encoded = Vec::with_capacity(entry.size() as usize);
        entry.read_to_end(&mut encoded).ok()?;
        Some(encoded)
    }

    pub fn discover_zip_chapters(path: &str, max_pages: usize) -> Option<Vec<ChapterSlice>> {
        let file = File::open(path).ok()?;
        let mut archive = ZipArchive::new(BufReader::new(file)).ok()?;

        let mut names = Vec::new();
        for i in 0..archive.len() {
            if let Ok(entry) = archive.by_index_raw(i) {
                if !entry.is_dir() && is_supported_image(entry.name()) {
                    names.push(entry.name().to_string());
                }
            }
        }

        let archive_name = Path::new(path)
            .file_stem()
            .and_then(|s| s.to_str())
            .unwrap_or("Comic");

        Some(split_entries_into_chapters(archive_name, names, max_pages))
    }
}
