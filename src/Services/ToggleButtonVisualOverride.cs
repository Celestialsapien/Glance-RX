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
using Microsoft.UI.Xaml.Media;

namespace GlanceRX.Services;

/// 接管 ToggleButton 的视觉状态 —— 专治「鼠标松开后指针一动没动，按钮底色却先跳一档再跳回来」。 与 同源（共享 ），仅多出 checked 状态与单按钮局部覆写。
internal class ToggleButtonVisualOverride : VisualOverrideBase<ToggleButton>
{
    public static void Attach(ToggleButton button)
    {
        if (button == null) return;
        new Link(button).Attach();
    }

    /// <summary>接管指定按钮的视觉输出，并用 对该按钮单独覆写视觉键。 只有传了委托的按钮才走覆写分支，其余按钮 为 null 会短路、行为零变化。</summary>
    public static void Attach(ToggleButton button, Func<string, object?>? colorOverride)
    {
        if (button == null) return;
        new Link(button, colorOverride).Attach();
    }

    private sealed class Link : VisualOverrideBase<ToggleButton>.BaseLink
    {
        private readonly Func<string, object?>? _colorOverride;
        private bool _lastChecked;

        internal Link(ToggleButton button, Func<string, object?>? colorOverride = null) : base(button)
        {
            _colorOverride = colorOverride;
        }

        protected override void AttachExtra()
        {
            _button.RegisterPropertyChangedCallback(
                ToggleButton.IsCheckedProperty, (s, e) => Sync());
        }

        /// <summary>单按钮局部覆写（如画笔模式按钮的实时画笔色）：只有传了委托的按钮才走这一支； 其余按钮 _colorOverride == null ⇒ 短路、行为与之前逐字节一致。</summary>
        protected override object? Resource(string key)
        {
            if (_colorOverride != null && _colorOverride(key) is object overridden)
                return overridden;
            return base.Resource(key);
        }

        protected override void Apply()
        {
            if (_root == null) return;

            if (_button.IsChecked == null) return;

            bool checkedValue = _button.IsChecked == true;
            bool pressed = _button.IsPressed;
            if (!_dirty
                && checkedValue == _lastChecked
                && pressed == _lastPressed
                && _hover == _lastHover)
            {
                return;
            }
            _dirty = false;
            _lastChecked = checkedValue;
            _lastPressed = pressed;
            _lastHover = _hover;

            // 与 generic.xaml 的 CommonStates 状态表逐行对应：按下优先于悬停
            string suffix = checkedValue
                ? (pressed ? "CheckedPressed" : _hover ? "CheckedPointerOver" : "Checked")
                : (pressed ? "Pressed" : _hover ? "PointerOver" : string.Empty);

            _root.Background = Resource("ToggleButtonBackground" + suffix) as Brush;
            _root.Foreground = Resource("ToggleButtonForeground" + suffix) as Brush;
            _root.BorderBrush = Resource("ToggleButtonBorderBrush" + suffix) as Brush;

            // checked 系列才切到 OuterBorderEdge（generic.xaml:2344/5141/7956 起的资源）。
            if (suffix.StartsWith("Checked", StringComparison.Ordinal)
                && Resource("ToggleButtonCheckedStateBackgroundSizing") is BackgroundSizing sizing)
            {
                _root.BackgroundSizing = sizing;
            }
        }
    }
}
