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
using System.Text.Json;

namespace GlanceRX.Services;

/// <summary>Service for persisting app settings (theme) to a JSON file in the app's own directory (portable/green mode).</summary>
public class SettingsService
{
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

    public bool ToolbarAlwaysVisible
    {
        get => _data.ToolbarAlwaysVisible;
        set
        {
            if (_data.ToolbarAlwaysVisible != value)
            {
                _data.ToolbarAlwaysVisible = value;
                Save();
            }
        }
    }

    /// <summary>资源管理器里被展开的文件夹（绝对路径）。跨会话记住 —— 下次打开应用，库还摊成离开时的样子。</summary>
    public IReadOnlyList<string> CanvasExpandedFolders => _data.CanvasExpandedFolders;

    /// <summary>右侧资源管理器的视口位置（横向 / 纵向偏移）。存在这里而不是 documents.json：
    /// 画布库是全局的一份，不跟任何 PDF 绑定 —— 同一棵目录树换个文档打开，能看到的行一模一样，
    /// 按文档存会把同一份"滚到哪儿了"复制 N 份，切换文档时还会互相覆盖。</summary>
    public double CanvasPanelScrollX => _data.CanvasPanelScrollX;

    public double CanvasPanelScrollY => _data.CanvasPanelScrollY;

    /// <summary>一次性写回右侧面板的会话状态：展开的文件夹 + 视口位置。
    /// 只在退出时调用一次 —— 面板滚动是高频事件，平时一概不落盘，
    /// 要记住的本来也只是"最终停在哪儿"。内容没变就不写文件。
    /// 亚像素抖动不值得落盘，位移小于半像素直接当没变。</summary>
    public void SaveCanvasPanelState(IEnumerable<string> folders, double scrollX, double scrollY)
    {
        var list = new List<string>();
        foreach (string p in folders)
        {
            if (!string.IsNullOrWhiteSpace(p)) list.Add(p);
        }

        bool changed = !SamePaths(list, _data.CanvasExpandedFolders)
                       || Math.Abs(_data.CanvasPanelScrollX - scrollX) >= 0.5
                       || Math.Abs(_data.CanvasPanelScrollY - scrollY) >= 0.5;
        if (!changed) return;

        _data.CanvasExpandedFolders = list;
        _data.CanvasPanelScrollX = scrollX;
        _data.CanvasPanelScrollY = scrollY;
        Save();
    }

    private static bool SamePaths(List<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        var set = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        foreach (string p in a)
        {
            if (!set.Contains(p)) return false;
        }
        return true;
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
                _data.CanvasExpandedFolders ??= new();   // 设置文件里写成 null 时的兜底
                // 反序列化出来的字典用的是默认比较器，而路径比较必须忽略大小写（Windows）。
                if (_data.CanvasOrder == null) _data.CanvasOrder = new(StringComparer.OrdinalIgnoreCase);
                else if (!ReferenceEquals(_data.CanvasOrder.Comparer, StringComparer.OrdinalIgnoreCase))
                {
                    _data.CanvasOrder = new Dictionary<string, List<string>>(
                        _data.CanvasOrder, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(_data, JsonOptions);
            // 先写临时文件再原子替换：展开状态是"点一下就存"，写入频率高，
            // 直接覆盖原文件时若进程正好退出，会留下半截 JSON —— 下次启动连主题都会一起丢。
            string tmp = SettingsFilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsFilePath, overwrite: true);
        }
        catch { }
    }

    /// <summary>画布库每一层的手动顺序：目录绝对路径 → 子项的文件/目录名（含扩展名，按显示顺序）。
    /// 不在表里的目录 = 没手动排过，按"文件夹在前、再按名称"。表里多出来的名字（文件被删/改名）
    /// 在下次排序时自然落空，不影响其余项的顺序。</summary>
    public IReadOnlyDictionary<string, List<string>> CanvasOrder => _data.CanvasOrder;

    /// <summary>某一层的手动顺序；没排过返回 null（= 用默认排序）。</summary>
    public IReadOnlyList<string>? GetCanvasOrder(string dirPath)
        => _data.CanvasOrder.TryGetValue(dirPath, out List<string>? names) ? names : null;

    /// <summary>写某一层的手动顺序（整体覆盖）。名字列表按显示顺序给，空列表等价于"没排过"。</summary>
    public void SetCanvasOrder(string dirPath, IEnumerable<string> names)
    {
        if (string.IsNullOrEmpty(dirPath)) return;
        var list = new List<string>(names);
        if (list.Count == 0) _data.CanvasOrder.Remove(dirPath);
        else _data.CanvasOrder[dirPath] = list;
        Save();
    }

    private class SettingsData
    {
        public string AppTheme { get; set; } = "Default";

        public bool ToolbarAlwaysVisible { get; set; }

        /// <summary>资源管理器里已展开的文件夹路径。旧设置文件没有这一项 ⇒ 反序列化后即为空列表。</summary>
        public List<string> CanvasExpandedFolders { get; set; } = new();

        /// <summary>右侧资源管理器的视口位置（像素）。旧设置文件没有这两项 ⇒ 默认 0 = 停在左上角。</summary>
        public double CanvasPanelScrollX { get; set; }

        public double CanvasPanelScrollY { get; set; }

        /// <summary>每一层的手动顺序（拖拽的结果）。旧设置文件没有这一项 ⇒ 反序列化后为 null，Load 里补齐。</summary>
        public Dictionary<string, List<string>> CanvasOrder { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}