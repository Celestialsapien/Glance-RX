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
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GlanceRX.Services;

/// <summary>
/// 清理程序输出目录中由 WinUI 3 / .NET 依赖库带来的多余卫星资源语言文件夹。
/// Glance 自身只支持 en/zh/es 三种语言，但依赖库会复制 60+ 个 BCP-47 语言目录到根目录。
/// 此服务在 App 启动时异步运行，只删除匹配语言目录形态的文件夹，并严格保护非语言目录。
/// </summary>
internal static class LanguageFolderTrimmer
{
    // 真正卫星语言目录的 BCP-47 形态：2-3 个小写字母 + 一个或多个 [-_]大写子段。
    // 例如：af-ZA, de-DE, zh-Hans, ca-ES-VALENCIA, sr-Cyrl-RS。
    // 此形态天然排除 lib/ref/win-x64/native/runtimes/Assets/Cache 等非语言目录。
    private static readonly Regex s_localeFolderRegex = new(
        @"^[a-z]{2,3}(?:[-_][A-Z][a-zA-Z0-9]*)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 极少数情况下依赖库可能以纯两字母目录存在（如 fr, de, ja）。删除它们，但用黑名单保护确定非语言的目录。
    private static readonly Regex s_bareTwoLetterRegex = new(
        @"^[a-z]{2}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Glance 支持的语言（含常见变体）。
    private static readonly HashSet<string> s_keepLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "en-US", "en-GB",
        "zh", "zh-CN", "zh-Hans", "zh-Hant", "zh-TW",
        "es", "es-ES", "es-MX"
    };

    // 绝对不可视为语言目录的纯两字母名字（DOS 设备名 + 常见构建目录缩写）。
    private static readonly HashSet<string> s_bareDenyList = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "res", "con", "aux", "nul", "prn",
        "com", "lpt", "cs", "db", "dl", "fs", "go", "js", "ts", "vb", "vc", "wp"
    };

    /// <summary>
    /// 在后台线程清理 <paramref name="baseDir"/> 下的多余语言文件夹。
    /// 如果 baseDir 为空，则使用 AppContext.BaseDirectory（glance.exe 所在目录）。
    /// </summary>
    public static Task RunAsync(string? baseDir = null)
    {
        return Task.Run(() => RunCore(baseDir));
    }

    private static void RunCore(string? baseDir)
    {
        string root = baseDir ?? AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        try
        {
            // 只扫描直接子目录，避免误删深层结构；语言文件夹都是平铺在根目录的。
            foreach (string dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!ShouldRemove(name))
                    continue;

                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // 单个目录删除失败不影响其余清理；静默跳过。
                }
            }
        }
        catch
        {
            // 扫描失败绝不影响启动。
        }
    }

    private static bool ShouldRemove(string name)
    {
        if (s_keepLanguages.Contains(name))
            return false;

        if (s_localeFolderRegex.IsMatch(name))
            return true;

        if (s_bareTwoLetterRegex.IsMatch(name) && !s_bareDenyList.Contains(name))
            return true;

        return false;
    }
}
