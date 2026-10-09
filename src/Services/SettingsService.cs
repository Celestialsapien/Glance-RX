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
using System.IO;
using System.Text.Json;

namespace GlanceRX.Services;

/// <summary>
/// Service for persisting app settings (theme) to a JSON file
/// in the app's own directory (portable/green mode).
/// </summary>
public class SettingsService
{
    // 数据落在程序本体目录的 GlanceData 子目录（便携版硬约束：不写 C 盘 AppData / 系统 Temp）。
    private static readonly string SettingsFilePath = AppPaths.SettingsFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private SettingsData _data;

    public SettingsService()
    {
        _data = new SettingsData();
        Load();
    }

    public string AppTheme
    {
        get => _data.AppTheme;
        set
        {
            if (_data.AppTheme != value)
            {
                _data.AppTheme = value;
                Save();
            }
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return;
            string json = File.ReadAllText(SettingsFilePath);
            var data = JsonSerializer.Deserialize<SettingsData>(json);
            if (data != null)
            {
                _data = data;
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(_data, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch { }
    }

    private class SettingsData
    {
        public string AppTheme { get; set; } = "Default";
    }
}