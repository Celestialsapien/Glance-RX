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

/// <summary>
/// 底层 Win32 辅助。
/// </summary>
internal static class CursorInterop
{
    /// <summary>
    /// 鼠标左键或右键当前是否**物理按下**（直接问系统，不依赖 XAML 的指针事件）。
    ///
    /// 用途：XAML 的 PointerCaptureLost / PointerReleased 在某些时机（ScrollViewer 抢捕获、
    /// 窗口失活等）会乱序或干脆不来，导致"手势中"的标志位提前失效。物理按键状态是权威事实，
    /// 拿它来判断"手势是否还在进行"最可靠。
    /// </summary>
    public static bool AnyMouseButtonDown()
    {
        try
        {
            // 高位为 1 表示当前按下。左右键可能被系统交换，两个都查即可。
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
    //   并非仅靠 WM_SETCURSOR 冒泡 —— 故 WM_SETCURSOR 子类化拦不到主视图的每帧重置，手势停下即被回退为默认箭头。
    // 正解（抄画笔圆环）：把"系统箭头 + 系统 I 形"资源本身替换为手势光标；框架每帧 SetCursor(箭头/I形) 实际画出的就是
    //   我们的手势光标，零滞后、不会被回退。仅手势进行中替换，结束用 SPI_SETCURSORS 一次性恢复全部系统光标。
    private const int IDC_ARROW = 32512;
    private const int IDC_IBEAM = 32513;
    private const uint SPI_SETCURSORS = 0x0057;

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    /// <summary>取系统标准光标句柄（IDC_* 常量）。</summary>
    public static IntPtr LoadSystemCursor(int id) => LoadCursor(IntPtr.Zero, id);

    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    /// <summary>把"系统箭头 + 系统 I 形"资源替换为指定标准光标（hCursor 为标准光标句柄；内部 CopyIcon 副本，原件保留）。
    /// 用于画布手势期间，让框架每帧 SetCursor(箭头/I形) 画出我们的手势光标。SetSystemCursor 会销毁传入副本。</summary>
    public static void OverrideSystemCursorTo(IntPtr hCursor)
    {
        if (hCursor == IntPtr.Zero) return;
        try
        {
            IntPtr a = CopyIcon(hCursor); if (a != IntPtr.Zero) SetSystemCursor(a, IDC_ARROW);
            IntPtr b = CopyIcon(hCursor); if (b != IntPtr.Zero) SetSystemCursor(b, IDC_IBEAM);
        }
        catch { }
    }

    /// <summary>恢复全部系统光标为系统主题（SPI_SETCURSORS），撤销 OverrideSystemCursorTo 的替换。</summary>
    public static void RestoreSystemCursors()
    {
        try { SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0); } catch { }
        try { SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW)); } catch { }
    }
}
