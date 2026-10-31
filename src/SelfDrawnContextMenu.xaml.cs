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

using System;
using System.Collections.Generic;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace GlanceRX;

/// <summary>自绘菜单的一行。分隔线也是"一行"，只是模板不同（<see cref="IsSeparator"/>）。
/// 文本由调用方本地化好再传进来 —— 菜单不关心文案从哪来。
/// public：它是 <see cref="SelfDrawnContextMenu.Show"/> 的参数类型，也是 XAML 里 x:DataType 的目标类型。</summary>
public sealed class SelfDrawnMenuRow : INotifyPropertyChanged
{
    public string Text { get; init; } = "";
    public string Glyph { get; init; } = "";
    public bool IsSeparator { get; init; }

    /// <summary>点击后执行。菜单先关再跑它 —— 动作里可能重建列表、弹提示，不该还压着一层菜单。</summary>
    public Action? Invoke { get; init; }

    private bool _hover;

    /// <summary>悬停底色层的透明度。用 double 而不是 bool：XAML 侧没有 bool→Opacity 的转换器。</summary>
    public double HoverOpacity => _hover ? 1 : 0;

    public void SetHover(bool on)
    {
        if (_hover == on) return;
        _hover = on;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HoverOpacity)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static SelfDrawnMenuRow Separator() => new() { IsSeparator = true };
}

/// <summary>自绘右键菜单。见 XAML 头部说明：不用 MenuFlyout 的原因（切光标 / 背后行掉悬浮态）。
/// 用法：常驻页面根 Grid，调 <see cref="Show"/> 传屏幕内坐标（相对本控件）+ 行集合。</summary>
public sealed partial class SelfDrawnContextMenu : UserControl
{
    public SelfDrawnContextMenu()
    {
        InitializeComponent();
    }

    /// <summary>菜单是否正开着（<see cref="Close"/> 之后立刻为 false）。</summary>
    public bool IsOpen => Panel.Visibility == Visibility.Visible;

    /// <summary>关闭后触发：宿主据此清掉"为了菜单而保留的"悬浮态。</summary>
    public event EventHandler? Closed;

    /// <summary>在 <paramref name="inSpaceOf"/> 的坐标系里、(x, y) 处弹出。调用方只需给"一个确定铺满页面的元素"
    /// 和"该坐标系里的点"，换算与摆位全在这里做 —— 不去猜框架认为的参考元素是谁。</summary>
    public void ShowAt(UIElement inSpaceOf, Windows.Foundation.Point point, IReadOnlyList<SelfDrawnMenuRow> rows)
    {
        Windows.Foundation.Point local = point;
        try { local = inSpaceOf.TransformToVisual(this).TransformPoint(point); }
        catch (Exception) { }   // 换算不成时按"本控件与页面同原点"处理（页面根 Grid 的直接子项本就如此）
        Show(local.X, local.Y, rows);
    }

    /// <summary>在 (x, y) 处弹出（坐标相对本控件）。贴边时自动内收，保证整块菜单落在可视区内。</summary>
    private void Show(double x, double y, IReadOnlyList<SelfDrawnMenuRow> rows)
    {
        if (rows.Count == 0) { Close(); return; }

        var rowTemplate = (DataTemplate)Resources["MenuRowTemplate"];
        var separatorTemplate = (DataTemplate)Resources["MenuSeparatorTemplate"];

        ItemsHost.Children.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            SelfDrawnMenuRow row = rows[i];
            // ContentControl 只是"把行数据套上行模板"的壳：Stretch 让整行铺满面板宽度，
            // IsTabStop=false 保证它不会被 Tab / 点击拿走焦点（行本身也是 Grid，不是 Button）。
            ItemsHost.Children.Add(new ContentControl
            {
                Content = row,
                ContentTemplate = row.IsSeparator ? separatorTemplate : rowTemplate,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsTabStop = false,
            });
        }

        Catcher.Visibility = Visibility.Visible;
        Panel.Visibility = Visibility.Visible;
        Place(x, y);
    }

    public void Close()
    {
        if (!IsOpen) return;
        Panel.Visibility = Visibility.Collapsed;
        Catcher.Visibility = Visibility.Collapsed;
        ItemsHost.Children.Clear();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>摆位：先按内容量出实际大小，再往里收。<paramref name="Layer"/> 是铺满整页的 Canvas，
    /// 所以 Left/Top 就是页面坐标，不需要任何对齐/边距参与。</summary>
    private void Place(double x, double y)
    {
        Panel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Windows.Foundation.Size size = Panel.DesiredSize;

        double maxW = Layer.ActualWidth;
        double maxH = Layer.ActualHeight;
        double left = x;
        double top = y;
        // 只有容器确实铺开（装得下整块菜单 + 8 余量）才做内收：容器尺寸判定不对时内收会算出贴边值，
        // 那正是"菜单飞到窗口左边"的成因 —— 宁可贴着指针，也不能被一个错误的边界推到别处。
        if (maxW >= size.Width + 8 && left + size.Width > maxW - 4) left = maxW - size.Width - 4;
        if (maxH >= size.Height + 8 && top + size.Height > maxH - 4) top = maxH - size.Height - 4;
        if (double.IsNaN(left) || left < 0) left = 0;
        if (double.IsNaN(top) || top < 0) top = 0;

        Canvas.SetLeft(Panel, left);
        Canvas.SetTop(Panel, top);
    }

    // 菜单外按下：只做"关掉"，不把这一下透给背后（与系统菜单一致）。
    private void Catcher_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    // 滚轮也关：接住层挡在列表上方，滚轮不会真的滚到背后的列表（等于卡住），干脆按系统菜单惯例直接收起。
    private void Catcher_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
        => RowOf(sender)?.SetHover(true);

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
        => RowOf(sender)?.SetHover(false);

    private void Row_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;   // 右键落在条目上不该触发
        SelfDrawnMenuRow? row = RowOf(sender);
        Close();
        row?.Invoke?.Invoke();
    }

    private static SelfDrawnMenuRow? RowOf(object sender)
        => (sender as FrameworkElement)?.DataContext as SelfDrawnMenuRow;
}
