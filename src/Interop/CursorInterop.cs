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
using System.Runtime.InteropServices;

namespace GlanceRX.Interop;

/// <summary>底层 Win32 辅助。</summary>
internal static class CursorInterop
{
    /// 鼠标左键或右键当前是否**物理按下**（直接问系统，不依赖 XAML 的指针事件）。
    public static bool AnyMouseButtonDown()
    {
        try
        {
            return (GetKeyState(VK_LBUTTON) & 0x8000) != 0
                || (GetKeyState(VK_RBUTTON) & 0x8000) != 0;
        }
        catch { return false; }
    }

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    // ---- 画布手势光标稳定（SetSystemCursor 资源级替换，对齐画笔圆环方案）----
    // 根因：WinUI3 框架在渲染/合成循环里【每帧直接 SetCursor】重设光标（MainPage 画笔圆环注释已坐实），
    private const int IDC_ARROW = 32512;
    private const int IDC_IBEAM = 32513;
    private const int IDC_HAND = 32649;   // 链接选择手型（食指）

    public const int IdcIbeam = IDC_IBEAM;
    public const int IdcHand = IDC_HAND;

    private const uint SPI_SETCURSORS = 0x0057;

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    public static IntPtr LoadSystemCursor(int id) => LoadCursor(IntPtr.Zero, id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    /// 把【系统共享光标】复制成"调用方拥有"的 HCURSOR 副本（GetIconInfo + CreateIconIndirect）。
    /// 【为何不能用 CopyIcon —— 这就是此前资源替换静默失效的根因】：
    public static IntPtr CloneSystemCursor(int idcId)
    {
        IntPtr src = LoadCursor(IntPtr.Zero, idcId);
        if (src == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            if (!GetIconInfo(src, out ICONINFO ii)) return IntPtr.Zero;
            try
            {
                return CreateIconIndirect(ref ii);
            }
            finally
            {
                // GetIconInfo 取出的位图是本进程副本，必须释放（光标本体由 CreateIconIndirect 持有）。
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>把"系统箭头（+可选系统 I 形）"资源替换为指定 IDC 形状的系统光标。 副本由 生成（不再用对共享句柄失效的 CopyIcon）。 keepIBeam=true 时【只替换箭头槽】：编辑态下文本区仍走系统 I 形槽（本就是工字），</summary>
    public static void OverrideSystemCursorTo(int idcId, bool keepIBeam = false)
    {
        try
        {
            IntPtr a = CloneSystemCursor(idcId);
            if (a != IntPtr.Zero) SetSystemCursor(a, IDC_ARROW);
            if (!keepIBeam)
            {
                IntPtr b = CloneSystemCursor(idcId);
                if (b != IntPtr.Zero) SetSystemCursor(b, IDC_IBEAM);
            }
        }
        catch { }
    }

    public static void RestoreSystemCursors()
    {
        try { SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0); } catch { }
        try { SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW)); } catch { }
    }
}
