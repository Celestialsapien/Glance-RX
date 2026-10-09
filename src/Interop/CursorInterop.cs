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
}
