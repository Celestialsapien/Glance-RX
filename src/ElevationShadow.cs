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

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Numerics;

namespace GlanceRX
{
    /// <summary>
    /// 立体投影附加属性：给任意 UIElement 挂一枚 ThemeShadow，并配合 Translation.Z 抬升，
    /// 让面板/胶囊从内容层"浮起来"，产生立体的下沉阴影。
    ///
    /// 为什么是 ThemeShadow 而不是 Composition DropShadow：
    /// 本 SDK（WindowsAppSDK 2.2.1 的 WinUI）托管面里【不存在】Composition 的
    /// DropShadow / CreateDropShadow / CreateSpriteVisual，Microsoft.UI.Composition.Visual
    /// 上也没有 Shadow 属性；唯一被暴露的投影机制就是 XAML 侧的 IShadow / IThemeShadow。
    /// 因此这里只能用 ThemeShadow（模糊/偏移/颜色由系统 elevation 规则决定，不可逐元素自定义）。
    ///
    /// 投影是元素渲染的一部分，元素 Opacity 变 0 时投影一并消失；被父级矩形裁剪时投影也会被裁掉
    /// （工具栏需向下预留裁切空间、侧边栏需在 Content 层单独放一枚投影载体）。
    /// </summary>
    public static class ElevationShadow
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled", typeof(bool), typeof(ElevationShadow),
                new PropertyMetadata(false, OnIsEnabledChanged));

        /// <summary>Z 轴抬升量（像素）。越大投影越大越柔，0 表示贴面不投影。常用 8~32。</summary>
        public static readonly DependencyProperty ElevationProperty =
            DependencyProperty.RegisterAttached(
                "Elevation", typeof(double), typeof(ElevationShadow),
                new PropertyMetadata(16.0, OnParamChanged));

        public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject o, bool v) => o.SetValue(IsEnabledProperty, v);
        public static double GetElevation(DependencyObject o) => (double)o.GetValue(ElevationProperty);
        public static void SetElevation(DependencyObject o, double v) => o.SetValue(ElevationProperty, v);

        // 缓存本次创建的 ThemeShadow，参数变更时就地更新而非重建。
        private static readonly DependencyProperty _shadowKey =
            DependencyProperty.RegisterAttached(
                "_Shadow", typeof(ThemeShadow), typeof(ElevationShadow), null);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not UIElement el) return;
            if ((bool)e.NewValue)
            {
                if (GetShadow(el) == null) SetShadow(el, new ThemeShadow());
                Apply(el);
            }
            else
            {
                el.Shadow = null;
                SetShadow(el, null);
                el.Translation = new Vector3(el.Translation.X, el.Translation.Y, 0f);
            }
        }

        private static void OnParamChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is UIElement el && GetIsEnabled(el)) Apply(el);
        }

        private static void Apply(UIElement el)
        {
            var shadow = GetShadow(el);
            if (shadow == null)
            {
                shadow = new ThemeShadow();
                SetShadow(el, shadow);
            }

            el.Shadow = shadow;

            // ThemeShadow 的强度/扩散完全由 Translation.Z 决定：Z=0 时几乎看不到投影。
            float z = (float)GetElevation(el);
            el.Translation = new Vector3(el.Translation.X, el.Translation.Y, z);
        }

        private static ThemeShadow? GetShadow(UIElement el) => (ThemeShadow?)el.GetValue(_shadowKey);
        private static void SetShadow(UIElement el, ThemeShadow? v) => el.SetValue(_shadowKey, v);
    }
}
