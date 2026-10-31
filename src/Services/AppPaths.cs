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

namespace GlanceRX.Services;

/// Glance 便携版的「数据根目录」：程序本体目录下的 GlanceData 子目录。
/// 与崩溃日志（crash_*.log / crash_native_*.log）全部直接落在 GlanceRX.exe 同级目录里，
/// **崩溃日志（Crash 子目录）**，诊断不和用户数据同处一个目录。
/// ⚠ 硬约束不变：便携版禁止写 C 盘 / AppData / 系统 Temp。这里【不是】把数据搬到别处，
internal static class AppPaths
{
    public const string DataFolderName = "GlanceData";

    /// <summary>崩溃日志子目录名，嵌在 之下。 诊断（crash_*.log / crash_all.log / crash_native_*.log）与设置、退出记忆、书签 **分开两个目录**：清诊断不会误删用户数据，查日志也不会在设置文件里翻。</summary>
    public const string CrashFolderName = "Crash";

    public static readonly string DataDirectory = SafeRoot();

    /// <summary>崩溃日志目录完整路径 = &lt;BaseDirectory&gt;/GlanceData/Crash。</summary>
    public static readonly string CrashDirectory = Path.Combine(DataDirectory, CrashFolderName);

    public static readonly string SettingsFilePath = Path.Combine(DataDirectory, "settings.json");

    /// <summary>每份 PDF 的文档级数据（阅读位置/缩放/布局/旋转/侧边栏/窗口几何 + 书签），键 = PDF 完整路径。</summary>
    public static readonly string DocumentsFilePath = Path.Combine(DataDirectory, "documents.json");

    /// <summary>画布库根目录。所有 .gcanvas 集中放这里（允许任意层子目录），与任何 PDF 都不绑定；
    /// 同一份画布可以盖到任意文档上，所以路径里不出现 PDF 名字。</summary>
    public static readonly string CanvasDirectory = Path.Combine(DataDirectory, "Canvases");

    /// <summary>静态构造里立即建目录：数据文件的写点分散在各 Store 里，不能等到第一次写入时才建； 崩溃日志同理（进程最早阶段就可能落盘）。</summary>
    static AppPaths()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(CrashDirectory);
            Directory.CreateDirectory(CanvasDirectory);
        }
        catch { /* 可移植版下目录不可建时静默沿用旧行为 */ }
    }

    private static string SafeRoot()
    {
        try
        {
            string dir = AppContext.BaseDirectory;
            return string.IsNullOrEmpty(dir) ? DataFolderName : Path.Combine(dir, DataFolderName);
        }
        catch
        {
            return DataFolderName;
        }
    }

    /// <summary>确保崩溃日志目录存在（含父级数据目录），返回其路径。创建失败静默。</summary>
    public static string EnsureCrashDirectory()
    {
        try
        {
            if (!Directory.Exists(CrashDirectory))
                Directory.CreateDirectory(CrashDirectory);
        }
        catch { }
        return CrashDirectory;
    }
}
