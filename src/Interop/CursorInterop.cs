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

    /// <summary>系统标准 I 形（文本）光标的 IDC 值，供外部 LoadSystemCursor 使用（编辑态强制固定工字）。</summary>
    public const int IdcIbeam = IDC_IBEAM;

    private const uint SPI_SETCURSORS = 0x0057;

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    /// <summary>取系统标准光标句柄（IDC_* 常量）。</summary>
    public static IntPtr LoadSystemCursor(int id) => LoadCursor(IntPtr.Zero, id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;      // true = icon；false = cursor
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

    /// <summary>把【系统共享光标】复制成"调用方拥有"的 HCURSOR 副本（GetIconInfo + CreateIconIndirect）。
    ///
    /// 【为何不能用 CopyIcon —— 这就是此前资源替换静默失效的根因】：
    /// <c>LoadCursor(NULL, IDC_*)</c> 返回的是【系统共享句柄】，<c>CopyIcon</c> 对它并不适用（会失败返回 0），
    /// 于是 <c>SetSystemCursor</c> 拿到的是零句柄而被静默跳过 —— 替换从未真正发生，编辑框内按下仍显示默认箭头。
    /// 画笔圆环之所以成功，是因为它的 HCURSOR 是【自己创建】的（归调用方所有），CopyIcon 有效。
    /// 此处改用 GetIconInfo 取出位图【副本】+ CreateIconIndirect 造出归属调用方的新光标（SetSystemCursor 可安全销毁）。
    /// 任一步失败返回 Zero —— 绝不触碰系统共享句柄本身。</summary>
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

    /// <summary>把"系统箭头（+可选系统 I 形）"资源替换为指定 IDC 形状的系统光标。
    /// 副本由 <see cref="CloneSystemCursor"/> 生成（不再用对共享句柄失效的 CopyIcon）。
    /// keepIBeam=true 时【只替换箭头槽】：编辑态下文本区仍走系统 I 形槽（本就是工字），
    /// 而框架若按箭头槽求值也会画出工字 —— 两头都是工字，且不牵连其他文本区域。</summary>
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

    /// <summary>恢复全部系统光标为系统主题（SPI_SETCURSORS），撤销 OverrideSystemCursorTo 的替换。</summary>
    public static void RestoreSystemCursors()
    {
        try { SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0); } catch { }
        try { SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW)); } catch { }
    }
}
