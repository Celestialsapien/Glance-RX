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
    /// 立体投影附加属性：给任意 UIElement 挂一枚 ThemeShadow，并配合 Translation.Z 抬升， 让面板/胶囊从内容层"浮起来"，产生立体的下沉阴影。
    public static class ElevationShadow
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled", typeof(bool), typeof(ElevationShadow),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static readonly DependencyProperty ElevationProperty =
            DependencyProperty.RegisterAttached(
                "Elevation", typeof(double), typeof(ElevationShadow),
                new PropertyMetadata(16.0, OnParamChanged));

        public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject o, bool v) => o.SetValue(IsEnabledProperty, v);
        public static double GetElevation(DependencyObject o) => (double)o.GetValue(ElevationProperty);
        public static void SetElevation(DependencyObject o, double v) => o.SetValue(ElevationProperty, v);

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

            float z = (float)GetElevation(el);
            el.Translation = new Vector3(el.Translation.X, el.Translation.Y, z);
        }

        private static ThemeShadow? GetShadow(UIElement el) => (ThemeShadow?)el.GetValue(_shadowKey);
        private static void SetShadow(UIElement el, ThemeShadow? v) => el.SetValue(_shadowKey, v);
    }
}
