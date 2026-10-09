// Glance-RX - a high-performance PDF reader
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace GlanceRX.Services;

/// <summary>
/// Persistent store for user-defined bookmarks (per PDF document).
/// Data is stored as JSON in the app's GlanceData subfolder (portable, no AppData/System/C drive).
/// </summary>
public sealed class BookmarkStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, List<PdfBookmark>> _cache = new();
    private bool _loaded;

    public BookmarkStore()
    {
        // 数据落在程序本体目录的 GlanceData 子目录（便携版硬约束：不写 C 盘 AppData / 系统 Temp）。
        _filePath = AppPaths.BookmarksFilePath;
    }

    /// <summary>Load bookmarks for the given PDF path. Always returns a mutable list owned by the caller.</summary>
    public async Task<List<PdfBookmark>> LoadAsync(string pdfPath)
    {
        if (string.IsNullOrEmpty(pdfPath)) return new List<PdfBookmark>();

        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            string key = MakeKey(pdfPath);
            if (_cache.TryGetValue(key, out var list))
            {
                // Return deep copy so callers can mutate without affecting cache until SaveAsync.
                return list.Select(b => new PdfBookmark { PageIndex = b.PageIndex, DisplayName = b.DisplayName }).ToList();
            }
            return new List<PdfBookmark>();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Save bookmarks for the given PDF path.</summary>
    public async Task SaveAsync(string pdfPath, List<PdfBookmark> bookmarks)
    {
        if (string.IsNullOrEmpty(pdfPath)) return;

        await _lock.WaitAsync();
        try
        {
            await EnsureLoadedAsync();
            string key = MakeKey(pdfPath);
            var clean = bookmarks
                .Where(b => b.PageIndex >= 0)
                .Select(b => new PdfBookmark { PageIndex = b.PageIndex, DisplayName = (b.DisplayName ?? "").Trim() })
                .ToList();
            _cache[key] = clean;
            await FlushAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        if (File.Exists(_filePath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(_filePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, List<PdfBookmark>>>(json);
                if (data != null) _cache = data;
            }
            catch { /* best-effort: start empty on corruption */ }
        }
        _loaded = true;
    }

    private async Task FlushAsync()
    {
        try
        {
            string json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_filePath, json);
        }
        catch { /* best-effort persistence */ }
    }

    private static string MakeKey(string pdfPath)
    {
        // Use full path as key. Path changes across PCs are expected; the file is local metadata.
        return pdfPath;
    }
}

public sealed class PdfBookmark
{
    public int PageIndex { get; set; }
    public string DisplayName { get; set; } = "";
}
