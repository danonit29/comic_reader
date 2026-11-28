use std::collections::BTreeMap;
use std::path::Path;

pub const MAX_PAGES_PER_CHAPTER: usize = 199;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChapterSlice {
    pub title: String,
    pub key: String,
    pub entries: Vec<String>,
}

pub fn is_supported_image(name: &str) -> bool {
    let ext = Path::new(name)
        .extension()
        .and_then(|e| e.to_str())
        .map(|e| e.to_ascii_lowercase());
    matches!(ext.as_deref(), Some("jpg" | "jpeg" | "png" | "webp" | "bmp"))
}

fn normalized_dir(full_name: &str) -> String {
    let normalized = full_name.replace('\\', "/");
    match normalized.rfind('/') {
        Some(pos) if pos > 0 => normalized[..pos].to_string(),
        _ => String::new(),
    }
}

fn common_top_prefix(keys: &[String]) -> String {
    if keys.is_empty() || keys.iter().any(|k| k.is_empty()) {
        return String::new();
    }
    let first = &keys[0];
    let Some(pos) = first.find('/') else {
        return String::new();
    };
    let candidate = &first[..=pos];
    if keys.iter().all(|k| k.starts_with(candidate)) {
        candidate.to_string()
    } else {
        String::new()
    }
}

/// Groups naturally sorted archive entry paths by internal directory and splits
/// any group exceeding `max_pages` (capped at 199 so every chapter is strictly < 200 pages).
pub fn split_entries_into_chapters(
    archive_name: &str,
    mut entry_names: Vec<String>,
    max_pages: usize,
) -> Vec<ChapterSlice> {
    if entry_names.is_empty() {
        return Vec::new();
    }

    let limit = max_pages.clamp(1, MAX_PAGES_PER_CHAPTER);
    entry_names.sort_by(|a, b| natord::compare_ignore_case(a, b));

    let mut groups: BTreeMap<String, Vec<String>> = BTreeMap::new();
    for name in entry_names {
        let dir = normalized_dir(&name);
        groups.entry(dir).or_default().push(name);
    }

    let mut ordered_groups: Vec<(String, Vec<String>)> = groups.into_iter().collect();
    ordered_groups.sort_by(|a, b| natord::compare_ignore_case(&a.0, &b.0));

    let keys: Vec<String> = ordered_groups.iter().map(|(k, _)| k.clone()).collect();
    let prefix = if ordered_groups.len() > 1 {
        common_top_prefix(&keys)
    } else {
        String::new()
    };

    let mut result = Vec::new();
    for (dir_key, entries) in ordered_groups {
        let base_title = if dir_key.is_empty() {
            if keys.len() > 1 {
                format!("{archive_name} (Root)")
            } else {
                archive_name.to_string()
            }
        } else {
            let trimmed = dir_key.strip_prefix(&prefix).unwrap_or(&dir_key);
            if trimmed.is_empty() {
                archive_name.to_string()
            } else {
                trimmed.replace('/', " / ")
            }
        };

        let key_value = if dir_key.is_empty() {
            archive_name.to_string()
        } else {
            dir_key
        };

        if entries.len() <= limit {
            result.push(ChapterSlice {
                title: base_title,
                key: key_value,
                entries,
            });
        } else {
            for (idx, chunk) in entries.chunks(limit).enumerate() {
                result.push(ChapterSlice {
                    title: format!("{base_title}.{}", idx + 1),
                    key: key_value.clone(),
                    entries: chunk.to_vec(),
                });
            }
        }
    }

    result
}

fn escape_json(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 8);
    for ch in s.chars() {
        match ch {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if c.is_control() => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out
}

pub fn chapters_to_json(chapters: &[ChapterSlice]) -> String {
    let mut json = String::from("[");
    for (i, ch) in chapters.iter().enumerate() {
        if i > 0 {
            json.push(',');
        }
        json.push_str("{\"title\":\"");
        json.push_str(&escape_json(&ch.title));
        json.push_str("\",\"key\":\"");
        json.push_str(&escape_json(&ch.key));
        json.push_str("\",\"entries\":[");
        for (j, entry) in ch.entries.iter().enumerate() {
            if j > 0 {
                json.push(',');
            }
            json.push('"');
            json.push_str(&escape_json(entry));
            json.push('"');
        }
        json.push_str("]}");
    }
    json.push(']');
    json
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn splits_multichapter_and_oversized_under_200() {
        let mut names = Vec::new();
        for i in 1..=30 {
            names.push(format!("Truyen/Chuong1/{i:03}.jpg"));
        }
        for i in 1..=250 {
            names.push(format!("Truyen/Chuong2/{i:03}.jpg"));
        }

        let chapters = split_entries_into_chapters("Truyen", names, 199);
        assert_eq!(chapters.len(), 3);
        assert_eq!(chapters[0].title, "Chuong1");
        assert_eq!(chapters[0].entries.len(), 30);
        assert_eq!(chapters[1].title, "Chuong2.1");
        assert_eq!(chapters[1].entries.len(), 199);
        assert_eq!(chapters[2].title, "Chuong2.2");
        assert_eq!(chapters[2].entries.len(), 51);
        assert!(chapters.iter().all(|c| c.entries.len() < 200));
    }
}
