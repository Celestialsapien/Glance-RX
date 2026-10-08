// Glance - a high-performance PDF reader
// Copyright (C) 2026 Adrian
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
// SPDX-License-Identifier: AGPL-3.0

// File handler - Read/write JSON files
use std::fs;
use std::path::Path;

/// Read JSON file from disk
pub fn read_json_file(path: &str) -> Result<String, String> {
    if path.is_empty() {
        return Err("File path cannot be empty".to_string());
    }

    fs::read_to_string(path)
        .map_err(|e| format!("Failed to read file '{}': {}", path, e))
}

/// Write JSON file to disk
pub fn write_json_file(data: &str, path: &str) -> Result<(), String> {
    if path.is_empty() {
        return Err("File path cannot be empty".to_string());
    }

    if data.is_empty() {
        return Err("Cannot write empty data".to_string());
    }

    // Create parent directory if needed
    if let Some(parent) = Path::new(path).parent() {
        if !parent.as_os_str().is_empty() {
            fs::create_dir_all(parent)
                .map_err(|e| format!("Failed to create directory: {}", e))?;
        }
    }

    fs::write(path, data)
        .map_err(|e| format!("Failed to write file '{}': {}", path, e))
}

#[cfg(test)]
mod tests {
    use super::*;
    use tempfile::TempDir;

    #[test]
    fn test_write_and_read() {
        let dir = TempDir::new().unwrap();
        let file_path = dir.path().join("test.json").to_string_lossy().to_string();

        let test_data = r#"{"test":"data"}"#;
        write_json_file(test_data, &file_path).unwrap();

        let read_data = read_json_file(&file_path).unwrap();
        assert_eq!(test_data, read_data);
    }

    #[test]
    fn test_write_empty_data() {
        let dir = TempDir::new().unwrap();
        let file_path = dir.path().join("test.json").to_string_lossy().to_string();

        let result = write_json_file("", &file_path);
        assert!(result.is_err());
    }

    #[test]
    fn test_read_nonexistent_file() {
        let result = read_json_file("/nonexistent/path/file.json");
        assert!(result.is_err());
    }
}
