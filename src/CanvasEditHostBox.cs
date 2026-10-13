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
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace GlanceRX;

/// <summary>
/// 白板编辑框专用宿主（<see cref="RichEditBox"/> 派生）。
///
/// 唯一目的：把 <see cref="Microsoft.UI.Xaml.UIElement.ProtectedCursor"/> 暴露出来 ——
/// 编辑态下 RichEditBox 覆盖整张卡片（含 Win2D 自绘的滚动条），指针落在滚动条上时控件默认显示工字光标；
/// 需要能按"是否在滚动条上"把光标切回箭头（null = 恢复控件默认的文本 I 形光标）。
/// 必须走框架自身的 <c>ProtectedCursor</c> 机制 —— 用 user32 SetCursor 手动改会被框架"每帧按元素光标重设"覆盖掉。
///
/// ⚠ 派生 WinUI 控件【必须】显式 <c>DefaultStyleKey = typeof(RichEditBox)</c>：默认风格键会解析到派生类型，
///    不设就拿不到 RichEditBox 的默认模板 ⇒ 内部 ScrollViewer / TOM 文档全废、连输入都不工作。
/// </summary>
public sealed class CanvasEditHostBox : RichEditBox
{
    public CanvasEditHostBox()
    {
        // 继承 RichEditBox 的默认模板与样式（缺这句会丢模板）。
        DefaultStyleKey = typeof(RichEditBox);
    }

    /// <summary>null = 恢复控件默认光标（文本 I 形）。</summary>
    public void OverrideCursor(InputCursor? cursor) => ProtectedCursor = cursor;
}
