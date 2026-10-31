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

#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlanceRX.Interop;

/// <summary>文件管理器侧操作：让删除走回收站、在资源管理器中定位。
/// 删除不用 <see cref="System.IO.File.Delete"/> —— 那是永久删除，资源管理器里删东西是可撤销的，
/// 用户对"删"的心理预期是进回收站，不是销毁。</summary>
internal static class ShellFileOps
{
    /// <summary>把文件或整个目录送进回收站。返回 false = 系统拒绝或文件被占用（此时文件仍在原处）。
    /// 用 SHFileOperation 而不是 SHFileOperationEx：前者已是 shell 的稳定入口，
    /// 而且 FOF_ALLOWUNDO 在回收站不可用（U 盘/网络盘）时会由系统自己弹"是否永久删除"，
    /// 所以这里【不】加 FOF_NOCONFIRMATION —— 加了这个标志就等于替用户答应了永久删除。</summary>
    public static bool MoveToRecycleBin(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                // pFrom 必须是双 null 结尾的路径列表；托管封送只补一个结尾，所以要自己再接一个 '\0'。
                pFrom = path + "\0",
                fFlags = FOF_ALLOWUNDO | FOF_SILENT | FOF_NOERRORUI,
            };
            int result = SHFileOperation(ref op);
            return result == 0 && op.fAnyOperationsAborted == 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>在资源管理器里选中一个文件（目录则直接打开它）。
    /// 用 explorer.exe /select 而不是打开所在目录：库里同名画布可能有多份，不选中等于让用户自己找。</summary>
    public static void RevealInExplorer(string path, bool isFolder)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var psi = isFolder
                ? new ProcessStartInfo("explorer.exe", $"\"{path}\"")
                : new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"");
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
        catch (Exception) { }
    }

    private const uint FO_DELETE = 0x0003;

    private const ushort FOF_SILENT = 0x0004;          // 不显示进度条
    private const ushort FOF_ALLOWUNDO = 0x0040;       // 能进回收站就进回收站
    private const ushort FOF_NOERRORUI = 0x0400;       // 错误不弹系统框（调用方自己给提示）

    // 布局必须用默认对齐：Win64 下 wFunc 之后有 4 字节填充，pFrom 才落在偏移 16。
    // 加了 Pack=1 会让 pFrom 落到 12，系统读到的指针就是垃圾。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
