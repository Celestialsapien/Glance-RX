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

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace GlanceRX.Services;

/// 接管普通功能按钮（，非 ）的视觉输出 —— 与 ToggleButtonVisualOverride 同源（共享 ）， 专治「鼠标松开后指针一动没动，按钮底色却先跳一档再跳回来」。
/// 只有文字能被点中），只给 hover / pressed 上色（菜单项语义：静止不该有独立底色）。
internal class ButtonVisualOverride : VisualOverrideBase<Button>
{
    /// 接管指定按钮的视觉输出。应在控件模板已应用之后调用（例如 Page.Loaded）。
    /// ⚠ 底色绝不能写 null：null 背景的 ContentPresenter 整片不参与命中测试，只有文字能被命中
    /// ⇒ hover / click 只在文字附近生效、扫过文字后才整片可点。
    /// ⚠ 前景也绝不能写 null —— null 是「本地值 = 无画刷」，文字会整段消失（浮层静止时全不可见）。
    public static void Attach(Button button, bool transparentAtRest = false, bool inPopupTree = false, bool accent = false)
    {
        if (button == null) return;
        new Link(button, transparentAtRest, inPopupTree, accent).Attach();
    }

    // 透明刷：静止档拿它（而非 null）填 ContentPresenter.Background。
    // ⚠ WinUI 命中测试：null 背景的 ContentPresenter 整片不参与命中，只有文字（TextBlock）能被命中
    private static readonly Brush TransparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    /// 浮层关闭/展开时强清预设项的 hover：WinUI 在 Flyout/light-dismiss 关闭瞬间吞掉 PointerExited ⇒ _hover 卡 true ⇒ 悬浮色残留、下次展开仍…
        public static void ClearFlyoutHoverStates()
        {
            foreach (var link in _instances)
                if (link.ExcludeFromPageHoverDrive) link.ClearTransparentHoverState();
        }

    /// 某颗按钮因钳制 / 布局改变发生位移后调用：把它立即清回静止，并开启 hover 冷处理窗口 （默认 900ms，仅由它自己的真实 PointerMoved 提前解除）。
    public static void SuppressHoverFor(ButtonBase? clicked) => BeginHoverHoldOffFor(clicked);

    /// <summary>权威指派某颗按钮的 hover 状态（几何由调用方按实时指针位置算好）。 用途：元素因布局变化滑到静止指针下方时，WinUI 不会主动派发 PointerEntered/Moved ⇒ 几何驱动没机会重算，光效要等它某次迟到的重新命中测试才亮。</summary>
    public static void ForceHover(Button? button, bool hover)
    {
        if (button == null) return;
        foreach (var link in _instances)
        {
            if (!ReferenceEquals(link.Button, button)) continue;
            link.ForceHover(hover);
            return;
        }
    }

    /// <summary>浮层预设项「独占 hover」：只让 那一颗亮，其余 transparentAtRest（= 浮层 8 颗）一律灭。由菜单面板自身的 PointerMoved 实时驱动 （面板是这片区域里唯一稳定收到指针事件的元素），</summary>
    public static void SetFlyoutHover(Button? hovered)
    {
        foreach (var link in _instances) link.SetTransparentHoverState(hovered);
    }

    private sealed class Link : VisualOverrideBase<Button>.BaseLink
    {
        private readonly bool _transparentAtRest;
        private readonly bool _inPopupTree;
        private readonly bool _accent;

        internal Link(Button button, bool transparentAtRest, bool inPopupTree, bool accent) : base(button)
        {
            _transparentAtRest = transparentAtRest;
            _inPopupTree = inPopupTree;
            _accent = accent;
        }

        protected override void AttachExtra() { }

        /// <summary>整页驱动退出：只有 inPopupTree（缩放预设浮层 8 颗）退出 —— 它们活在 popup 的 独立 Xaml 树，跨树 GetCurrentPoint 坐标不可靠，且已由 ZoomPresetPanel 面板驱动 ⇒ 整页驱动必须跳过，</summary>
        internal override bool ExcludeFromPageHoverDrive => _inPopupTree;

        /// <summary>popup 树按钮（缩放预设浮层 8 颗、操作指南按钮）跳过视觉树遍历、直接用软件主题源： 该树的 FlyoutPresenter 挂着 WinUI 自己镜像的 RequestedTheme，</summary>
        protected override bool UseAppThemeSourceOnly => _inPopupTree;

        /// <summary>浮层强清：仅 transparentAtRest 项才清 hover，避免误清正常功能按钮。</summary>
        protected override void ClearTransparentHoverIfAny()
        {
            if (!_transparentAtRest) return;
            if (!_hover) return;
            _hover = false;
            _dirty = true;
            Sync();
        }

        protected override void SetTransparentHover(Button? hovered)
        {
            if (!_transparentAtRest) return;
            bool hover = ReferenceEquals(_button, hovered);
            if (_hover == hover) return;
            _hover = hover;
            _dirty = true;
            Sync();
        }

        protected override void Apply()
        {
            if (_root == null) return;

            bool pressed = _button.IsPressed;
            if (!_dirty
                && pressed == _lastPressed
                && _hover == _lastHover)
            {
                return;
            }
            _dirty = false;
            _lastPressed = pressed;
            _lastHover = _hover;

            // 欢迎页「打开 PDF」按钮（accent 模式）：几何驱动上色，不套 GlanceAccentButtonStyle 的原生
            // VSM + BrushTransition —— 点击弹文件选择器时输入上下文切换、且无法判定鼠标是否仍在按钮上，
            // 会让 Pressed→Normal→PointerOver 序列在快速点击下闪烁。
            // 配色用 Obsidian 紫三档（GlanceAccentBrush 0.9 / 0.8 梯度），
            if (_accent)
            {
                if (pressed)
                {
                    _root.Background = Resource("GlanceAccentBrushPressed") as Brush;
                    _root.Foreground = Resource("GlanceAccentForegroundPressed") as Brush;
                    _root.BorderBrush = Resource("ControlFillColorTransparentBrush") as Brush;
                }
                else if (_hover)
                {
                    _root.Background = Resource("GlanceAccentBrushPointerOver") as Brush;
                    _root.Foreground = Resource("GlanceAccentForeground") as Brush;
                    _root.BorderBrush = Resource("AccentControlElevationBorderBrush") as Brush;
                }
                else
                {
                    _root.Background = Resource("GlanceAccentBrush") as Brush;
                    _root.Foreground = Resource("GlanceAccentForeground") as Brush;
                    _root.BorderBrush = Resource("AccentControlElevationBorderBrush") as Brush;
                }
                return;
            }

            // 与 generic.xaml 的 CommonStates 状态表逐行对应：按下优先于悬停（无 IsChecked 档）。
            // ⚠ 键用 ToggleButton* 的「未选中三档」（App.xaml 主题字典，与亚克力同源、深浅已验收），
            string suffix = pressed ? "Pressed" : _hover ? "PointerOver" : string.Empty;

            if (_transparentAtRest && suffix.Length == 0)
            {
                // 透明项静止档：背景写「透明刷」而非 null —— 目的有二。
                //   ① 不涂成 App.xaml 的 ToggleButtonBackground（不透明中性底，会破坏菜单项透明设计）；
                //      只在文字附近生效、扫过文字后才整片可点。透明刷不可见但仍可命中。
                // ⚠ Foreground 也绝不能写 null：null 是「本地值 = 无画刷」而非「清值回退 TemplateBinding」，
                //   文字会整段消失（缩放预设浮层静止完全不可见、hover 才显形）。必须写回静止档前景刷。
                _root.Background = TransparentBrush;
                _root.BorderBrush = null;
                _root.Foreground = Resource("ToggleButtonForeground") as Brush;
                return;
            }

            if (_transparentAtRest)
            {
                // 缩放胶囊 4 颗 + 浮层预设 8 颗（均 transparentAtRest）的悬浮/点击态。
                // 同源反色/近胶囊【淡叠加】档。静止完全透明（胶囊=并入亚克力、浮层=菜单项语义）；
                // ⚠ 边框留 null：淡叠加已自带形状，加描边反而显脏。
                string key = pressed ? "ZoomPillButtonPressed" : "ZoomPillButtonHover";
                _root.Background = Resource(key + "Background") as Brush;
                _root.Foreground = Resource("ToggleButtonForeground") as Brush;
                _root.BorderBrush = null;
                return;
            }

            _root.Background = Resource("ToggleButtonBackground" + suffix) as Brush;
            _root.Foreground = Resource("ToggleButtonForeground" + suffix) as Brush;
            _root.BorderBrush = Resource("ToggleButtonBorderBrush" + suffix) as Brush;
        }
    }
}
