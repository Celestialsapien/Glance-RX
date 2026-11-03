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
using GlanceRX.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace GlanceRX;

/// 自绘单行文本域（见 SelfDrawnTextBox.xaml 顶部的设计说明）。
public sealed partial class SelfDrawnTextBox : UserControl
{
    public event RoutedEventHandler? Submitted;

    public event RoutedEventHandler? Cancelled;

    /// <summary>宿主失焦（点到域外）时请求提交；调用方通常按"仍在编辑态"守卫后再提交。</summary>
    public event RoutedEventHandler? CommitRequested;

    public event EventHandler? EditorGotFocus;

    public event EventHandler? EditorLostFocus;

    public bool HasEditorFocus => Host.FocusState != FocusState.Unfocused;

    /// <summary>本次焦点请求是否由本控件自身发起。调用方在 GettingFocus 里可据此区分 "用户点进输入框"与"框架擅自把焦点重定位到输入框"（后者需拦截）。</summary>
    public bool IsRequestingFocus { get; private set; }

    public bool OwnsFocusedElement(object? element) => ReferenceEquals(element, Host);

    private double[] _adv = new double[1];
    private double _lineHeight = 16;   // 单行行高（由度量块实测，随字号/文本缩放自适应）
    private double _scrollX;           // 横向"滚动"偏移（保证光标可见，单行不自带滚动条故自行平移）
    private double _anchorX;           // 文本块左端偏移：居中模式下"文本比视口窄"时居中；溢出时为 0
    private bool _dragging;
    private int _dragAnchor;
    private int _caretIndex;           // 插入光标的"活跃端"码元索引：拖选时跟随鼠标（支持反向拖选），其余时刻=选区末端
    private bool _ibeamInstalled;
    private bool _pointerInside;       // 指针据实是否仍在控件范围内（几何判定，不唯事件 —— 以防被"假 PointerExited"骗）
    /// <summary>已经脱离可视树（行容器重建 / 列表换行集合时会发生）。此刻的失焦不是"用户点到别处"，
    /// 不能当成提交 —— 否则列表一刷新，正在改的名字就被自己提交掉、编辑框当场消失。</summary>
    private bool _unloading;
    private DispatcherTimer? _blink;
    private long _lastPressMs;         // 上一次按下时刻（自行判双击，避开 PointerPressed Handled 后 Tapped 不触发）
    private int _lastPressIndex = -1;

    public SelfDrawnTextBox()
    {
        InitializeComponent();
        // 内容根（Root）必须与控件本体等尺寸：内部所有居中、裁切、右对齐的"清空 ×"都以"控件这一格"为基准，
        // 而 UserControl 只把内容按【它自己的期望尺寸】从左上摆放，不会撑满控件。隐藏宿主是原生 TextBox，
        // 模板把最小高度写死绑到主题资源（32）⇒ 期望高度恒为 32，控件再矮它也不缩：
        // 竖向文字在 32 里居中（相对矮盒子整体下沉），横向按文本宽度走（长名字直接画出框外）。
        // 把 Root 钉在实测尺寸上：控件多大，文字就在多大里居中，与宿主/模板无关。
        SizeChanged += (_, e) => PinContentToControl(e.NewSize);
        // Loaded 时字体的继承链已落定 ⇒ 重取行高/前缀宽度（构造期只拿到控件默认字号）
        Loaded += (_, _) =>
        {
            _unloading = false;
            UpdateMetrics();
            RebuildAdvances();
            UpdateCaretAndSelection();
            // 受 AutoFocusOnShow 门控：否则开机时工具栏项（AutoFocusOnShow=False）会在 Loaded 里无条件抢焦点，
            if (AutoFocusOnShow && Visibility == Visibility.Visible) FocusEditor();
        };
        Unloaded += (_, _) => { _unloading = true; RestoreCursor(); StopBlink(); };
        // 可见性翻转 ⇒ 进入编辑落焦点（延迟一拍等布局落定）；折叠时立即收尾（否则指针还停在原地、
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible)
            {
                // 仅"编辑框"语义（如书签）才自动聚焦；工具栏项因收纳会反复显隐，自动聚焦会误抢焦点
                if (AutoFocusOnShow) DispatcherQueue.TryEnqueue(FocusEditor);
            }
            else { RestoreCursor(); StopBlink(); }
        });
        // 字号可能由使用方（模板）在构造之后才设定，同步刷新行高
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => { UpdateMetrics(); UpdateCaretAndSelection(); });
        // 「禁用→启用」剥夺焦点结束后重新启用：禁用期间控件脱离命中测试，框架会抛【假】PointerExited；
        RegisterPropertyChangedCallback(IsEnabledProperty, (_, _) =>
        {
            if (IsEnabled && _pointerInside) InstallIbeam();
        });
        UpdateMetrics();
        RebuildAdvances();
        ApplyTextAreaMargin();   // 显式调一次：不依赖"谁先设值"的顺序
    }

    /// <summary>把内容根的尺寸钉到控件实测尺寸。相等的值不重设（避免无谓的布局失效/回环）；
    /// 尺寸为 0（控件折叠）时跳过 —— 折叠期把 Root 设成 0 会让"不给 Height"的使用方再也长不回来。</summary>
    private void PinContentToControl(Size size)
    {
        if (size.Width > 0 && (double.IsNaN(Root.Width) || Math.Abs(Root.Width - size.Width) > 0.01))
            Root.Width = size.Width;
        if (size.Height > 0 && (double.IsNaN(Root.Height) || Math.Abs(Root.Height - size.Height) > 0.01))
            Root.Height = size.Height;
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SelfDrawnTextBox), new PropertyMetadata("", OnTextPropertyChanged));

    public string Text
    {
        get => (string)(GetValue(TextProperty) ?? "");
        set => SetValue(TextProperty, value);
    }

    private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SelfDrawnTextBox t) t.ApplyExternalText(e.NewValue as string ?? "");
    }

    public static readonly DependencyProperty ContentAlignmentProperty = DependencyProperty.Register(
        nameof(ContentAlignment), typeof(TextAlignment), typeof(SelfDrawnTextBox),
        new PropertyMetadata(TextAlignment.Left, OnLayoutPropertyChanged));

    public TextAlignment ContentAlignment
    {
        get => (TextAlignment)(GetValue(ContentAlignmentProperty) ?? TextAlignment.Left);
        set => SetValue(ContentAlignmentProperty, value);
    }

    public static readonly DependencyProperty ShowClearButtonProperty = DependencyProperty.Register(
        nameof(ShowClearButton), typeof(bool), typeof(SelfDrawnTextBox),
        new PropertyMetadata(true, OnLayoutPropertyChanged));

    /// <summary>聚焦且有文本时是否显示「清空 ×」（窄框如页码框可关掉，避免挤占本就有限的宽度）。</summary>
    public bool ShowClearButton
    {
        get => (bool)(GetValue(ShowClearButtonProperty) ?? true);
        set => SetValue(ShowClearButtonProperty, value);
    }

    public static readonly DependencyProperty AutoFocusOnShowProperty = DependencyProperty.Register(
        nameof(AutoFocusOnShow), typeof(bool), typeof(SelfDrawnTextBox), new PropertyMetadata(true));

    public bool AutoFocusOnShow
    {
        get => (bool)(GetValue(AutoFocusOnShowProperty) ?? true);
        set => SetValue(AutoFocusOnShowProperty, value);
    }

    public static readonly DependencyProperty ShowFrameProperty = DependencyProperty.Register(
        nameof(ShowFrame), typeof(bool), typeof(SelfDrawnTextBox),
        new PropertyMetadata(true, OnShowFrameChanged));

    /// <summary>是否由本控件画那圈描边（默认 true）。置 false 的场合（画布行内改名框）是
    /// "框比控件高"：控件用 <see cref="AutoHeight"/> 只占一行，而框要比一行再宽裕一点，
    /// 于是框改由外层按固定尺寸画。控件自画的框恒等于控件自身尺寸（Root 已钉成控件尺寸），
    /// 两者尺寸不一致时四条边不保证都画得出来，故这类场合必须交给外层。</summary>
    public bool ShowFrame
    {
        get => (bool)(GetValue(ShowFrameProperty) ?? true);
        set => SetValue(ShowFrameProperty, value);
    }

    private static void OnShowFrameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => (d as SelfDrawnTextBox)?.ApplyFrameVisibility();

    /// <summary>聚焦态：框架描边由"是否聚焦"和"是否自绘"两件事共同决定，只在这一个出口计算，
    /// 免得 ShowFrame 与焦点切换各自写 Visibility 互相覆盖。</summary>
    private bool _frameFocused;

    private void ApplyFrameVisibility()
    {
        if (FrameBorder != null)
            FrameBorder.Visibility = ShowFrame ? Visibility.Visible : Visibility.Collapsed;
        if (FocusFrame != null)
            FocusFrame.Visibility = ShowFrame && _frameFocused ? Visibility.Visible : Visibility.Collapsed;
    }

    public static readonly DependencyProperty DigitsOnlyProperty = DependencyProperty.Register(
        nameof(DigitsOnly), typeof(bool), typeof(SelfDrawnTextBox), new PropertyMetadata(false, OnDigitsOnlyChanged));

    public bool DigitsOnly
    {
        get => (bool)(GetValue(DigitsOnlyProperty) ?? false);
        set => SetValue(DigitsOnlyProperty, value);
    }

    public static readonly DependencyProperty AutoHeightProperty = DependencyProperty.Register(
        nameof(AutoHeight), typeof(bool), typeof(SelfDrawnTextBox),
        new PropertyMetadata(false, (d, _) => (d as SelfDrawnTextBox)?.UpdateMetrics()));

    /// <summary>高度恒等于实测行高（随字号/文本缩放自适应）。
    /// 让"本控件的文字盒子"与同一行里高度为一行高的普通 TextBlock 完全同尺寸 ⇒
    /// 两者都用行内 <c>VerticalAlignment=Center</c> 时位置逐像素一致，进出编辑态不产生位移。
    /// 别再套一层固定高度的容器去居中：每多一层居中就多一次像素舍入（高 DPI 下会差 1 物理像素）。</summary>
    public bool AutoHeight
    {
        get => (bool)(GetValue(AutoHeightProperty) ?? false);
        set => SetValue(AutoHeightProperty, value);
    }

    /// <summary>文本区留白只由这一处决定：左右恒 4（文字与描边之间留出空隙，贴边会显得挤、长文本还会
    /// 顶到描边上），聚焦且有文本时右侧让 26 给"清空 ×"；上下恒 0。
    /// ⚠ 这个 4 是"文字与描边之间的视觉约定值"，使用方在同一格里放常态 TextBlock 时
    /// （画布行名字、书签名字）必须给它相同的左内边距，否则进出编辑态文字会横向跳 4px；
    /// 改这里就要同步改那几处的 Margin（Search: "与输入框内文字的左内边距一致"）。
    /// 竖向不在这里做任何推算 —— 文本区高度就是行高（见 <see cref="UpdateMetrics"/>），
    /// 由 <c>VerticalAlignment=Center</c> 在控件里居中，与同一行里"一行高 + 居中"的 TextBlock 位置一致。</summary>
    private void ApplyTextAreaMargin()
    {
        bool showClear = ShowClearButton && Host.FocusState != FocusState.Unfocused && (Host.Text?.Length ?? 0) > 0;
        const double side = 4;
        TextArea.Margin = new Thickness(side, 0, showClear ? 26 : side, 0);
        TextArea.VerticalAlignment = VerticalAlignment.Center;
    }

    private static void OnDigitsOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SelfDrawnTextBox t && t.DigitsOnly)
        {
            t.EnforceDigitsOnly();
            t.RebuildAdvances();
            t.UpdateCaretAndSelection();
        }
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SelfDrawnTextBox t) { t.UpdateMetrics(); t.UpdateCaretAndSelection(); t.UpdateClearButton(); }
    }

    /// <summary>外部写入文本（与宿主现状不同才同步，避免"宿主→DP→宿主"的回环）。</summary>
    private void ApplyExternalText(string v)
    {
        if (Host.Text == v) return;
        Host.Text = v;
        Host.SelectionStart = v.Length;   // 光标恒置末：绕过"程序写 Text 后 SelectionStart 被清零"的原生坑
        Host.SelectionLength = 0;
        _caretIndex = v.Length;
        RebuildAdvances();
        UpdateCaretAndSelection();
    }

    private void UpdateMetrics()
    {
        MeasureTb.Text = "A";
        MeasureTb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double h = MeasureTb.DesiredSize.Height;
        _lineHeight = h > 0 ? h : 16;
        Caret.Height = _lineHeight;
        // AutoHeight：控件高度就是一行行高。盒子内部因此没有任何竖向余量，
        // 文字相对控件零偏移 —— 与同一行里"一行高 + 行内居中"的 TextBlock 位置完全一致。
        if (AutoHeight && _lineHeight > 0 && Math.Abs(Height - _lineHeight) > 0.01) Height = _lineHeight;
        // Canvas 的期望尺寸恒为 0（不按内容定高）⇒ 文本区高度必须在这里显式给出：
        // 它只是"行盒"，竖向居中交给 Canvas 的 VerticalAlignment=Center，
        // 这样控件被给成任何高度（行内 18 / 工具栏 32）时行盒都落在正中。
        TextArea.Height = _lineHeight;
    }

    private void RebuildAdvances()
    {
        string s = Host.Text ?? "";
        if (_adv.Length != s.Length + 1) _adv = new double[s.Length + 1];
        _adv[0] = 0;
        for (int i = 1; i <= s.Length; i++)
        {
            MeasureTb.Text = s.Substring(0, i);
            MeasureTb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _adv[i] = MeasureTb.DesiredSize.Width;
        }
        MeasureTb.Text = "";
        VisibleText.Text = s;
    }

    private double IndexToX(int idx)
    {
        if (idx <= 0) return 0;
        if (idx >= _adv.Length) return _adv[_adv.Length - 1];
        return _adv[idx];
    }

    private int XToIndex(double x)
    {
        double target = x + _scrollX - _anchorX;
        int best = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < _adv.Length; i++)
        {
            double d = Math.Abs(_adv[i] - target);
            if (d < bestD) { bestD = d; best = i; }
            else if (_adv[i] > target) break;
        }
        return best;
    }

    private void UpdateCaretAndSelection()
    {
        string s = Host.Text ?? "";
        int start = Math.Clamp(Host.SelectionStart, 0, s.Length);
        int end = Math.Clamp(start + Host.SelectionLength, 0, s.Length);
        int caretIdx = Math.Clamp(_caretIndex, 0, s.Length);   // 活跃端：拖选时跟随鼠标（支持反向拖选），否则为选区末端
        double cx = IndexToX(caretIdx);
        double total = IndexToX(s.Length);
        // 右边界预留 1px 给光标自身（否则贴右时光标恰落在裁剪边上被裁掉）
        double view = Math.Max(1, TextArea.ActualWidth - 1);

        if (cx - _scrollX > view) _scrollX = cx - view;
        else if (cx - _scrollX < 0) _scrollX = cx;
        _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, total - view));

        // 居中模式：文本比视口窄时把整块居中（此时不滚动）；溢出后 anchor=0，退化为左对齐贴光标滚动
        _anchorX = ContentAlignment == TextAlignment.Center ? Math.Max(0, (view - total) / 2) : 0;

        // Canvas 承载 ⇒ 用 Canvas.SetLeft/SetTop 定位。竖直方向行盒从 Canvas 顶端起排（top=0），
        // "在控件里居中"由 TextArea 的 VerticalAlignment=Center 完成，不在这里算。
        double top = 0;
        Canvas.SetLeft(VisibleText, _anchorX - _scrollX);
        Canvas.SetTop(VisibleText, top);
        Canvas.SetLeft(Caret, _anchorX + cx - _scrollX);
        Canvas.SetTop(Caret, top);
        Caret.Visibility = (Visibility == Visibility.Visible && Host.FocusState != FocusState.Unfocused)
            ? Visibility.Visible : Visibility.Collapsed;

        if (end > start)
        {
            SelRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(SelRect, _anchorX + IndexToX(start) - _scrollX);
            Canvas.SetTop(SelRect, top);
            SelRect.Width = Math.Max(0, IndexToX(end) - IndexToX(start));
            SelRect.Height = _lineHeight;
        }
        else
        {
            SelRect.Visibility = Visibility.Collapsed;
        }

        ResetBlink();
    }

    /// <summary>文本区尺寸变化 ⇒ 重设裁剪矩形（单行横向裁切）并重算位置。</summary>
    private void TextArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        TextArea.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Rect(0, 0, TextArea.ActualWidth, TextArea.ActualHeight)
        };
        UpdateCaretAndSelection();
    }

    /// <summary>「清空 ×」可见时右侧预留其宽度，避免长文本钻到它下面。</summary>
    private void UpdateClearButton()
    {
        bool show = ShowClearButton && Host.FocusState != FocusState.Unfocused && (Host.Text?.Length ?? 0) > 0;
        ClearHit.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ApplyTextAreaMargin();   // 右侧让位与左右内边距是同一份布局，必须走同一个出口
    }

    private void Host_TextChanged(object sender, TextChangedEventArgs e)
    {
        EnforceDigitsOnly();
        if (Text != Host.Text) Text = Host.Text;
        RebuildAdvances();
        UpdateCaretAndSelection();
        UpdateClearButton();
    }

    /// <summary>纯数字模式下剔除非法字符，并把光标按"变更前光标位之前的合法位数"回退，避免落点跳乱。</summary>
    private void EnforceDigitsOnly()
    {
        if (!DigitsOnly) return;
        string raw = Host.Text ?? "";
        string filtered = FilterDigits(raw);
        if (filtered.Length == raw.Length) return;
        int caret = Math.Clamp(Host.SelectionStart, 0, raw.Length);
        int kept = 0;
        for (int i = 0; i < caret; i++) if (IsDigit(raw[i])) kept++;
        Host.Text = filtered;                        // 会再触发一次 TextChanged；届时 filtered==raw 直接放行
        Host.SelectionStart = kept;
        Host.SelectionLength = 0;
        _caretIndex = kept;
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    /// <summary>只保留 0-9（不引 System.Text，用 char[] 拼装）。</summary>
    private static string FilterDigits(string s)
    {
        int n = 0;
        for (int i = 0; i < s.Length; i++) if (IsDigit(s[i])) n++;
        if (n == s.Length) return s;
        char[] buf = new char[n];
        int k = 0;
        for (int i = 0; i < s.Length; i++) if (IsDigit(s[i])) buf[k++] = s[i];
        return new string(buf);
    }

    private void Host_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_dragging) _caretIndex = Host.SelectionStart + Host.SelectionLength;
        UpdateCaretAndSelection();
    }

    private void Host_GotFocus(object sender, RoutedEventArgs e)
    {
        IsRequestingFocus = false;   // 焦点已落定，复位"自发起"标记（其后框架的重定位请求将被调用方拦截）
        _frameFocused = true;
        ApplyFrameVisibility();
        Caret.Visibility = Visibility.Visible;
        StartBlink();
        UpdateCaretAndSelection();
        UpdateClearButton();
        EditorGotFocus?.Invoke(this, EventArgs.Empty);
    }

    private void Host_LostFocus(object sender, RoutedEventArgs e)
    {
        _frameFocused = false;
        ApplyFrameVisibility();
        StopBlink();
        Caret.Visibility = Visibility.Collapsed;
        UpdateClearButton();
        EditorLostFocus?.Invoke(this, EventArgs.Empty);
        // 仅"仍在编辑（控件可见）且还挂在可视树上"时请求提交；Cancel/Confirm 置 IsEditing=false 会先折叠控件
        // ⇒ 天然挡住重复提交。控件已被摘下行（_unloading）时的失焦是重建的副作用，交回调用方保留编辑态。
        if (!_unloading && Visibility == Visibility.Visible) CommitRequested?.Invoke(this, new RoutedEventArgs());
    }

    private void Host_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            if (Text != Host.Text) Text = Host.Text;
            Submitted?.Invoke(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            Cancelled?.Invoke(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Root).Position;

        // 命中「清空 ×」热区 ⇒ 清空文本并保持焦点（该热区不可聚焦，故焦点不会离开宿主）
        if (ClearHit.Visibility == Visibility.Visible && p.X >= Root.ActualWidth - ClearHit.Width)
        {
            ClearText();
            e.Handled = true;
            return;
        }

        int idx = XToIndex(p.X - TextAreaOffsetX());
        long now = Environment.TickCount64;
        bool dbl = now - _lastPressMs <= 400 && idx == _lastPressIndex;
        _lastPressMs = now;
        _lastPressIndex = idx;

        FocusHost();

        _dragging = true;                      // 先置位：随后选区赋值触发的 SelectionChanged 不得覆盖活跃端
        if (dbl) SelectWordAt(idx);            // 自行判双击（PointerPressed 被 Handled 后 Tapped/DoubleTapped 不再来）
        else { Host.SelectionStart = idx; Host.SelectionLength = 0; }

        _dragAnchor = dbl ? Host.SelectionStart : idx;
        _caretIndex = dbl ? Host.SelectionStart + Host.SelectionLength : idx;
        Root.CapturePointer(e.Pointer);
        UpdateCaretAndSelection();
        e.Handled = true;                     // 吞掉按下：否则会冒泡成 ListView 的 ItemClick（编辑中被跳页）
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Root).Position;
        _pointerInside = IsWithin(p);         // 据实刷新"指针在域内"，供假 PointerExited 判定复用
        UpdateCursorScope(p);

        if (!_dragging) return;
        int idx = XToIndex(p.X - TextAreaOffsetX());
        int a = Math.Min(_dragAnchor, idx);
        int b = Math.Max(_dragAnchor, idx);
        _caretIndex = idx;                    // 活跃端随鼠标走：反向拖选时光标跟随到左侧，不再钉在选区右端
        Host.SelectionStart = a;
        Host.SelectionLength = b - a;
        UpdateCaretAndSelection();            // 选区值未变时 SelectionChanged 不触发，需显式刷新
        e.Handled = true;
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);
        UpdateCursorScope(e.GetCurrentPoint(Root).Position);
        e.Handled = true;
    }

    private void Root_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _dragging = false;

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = true;
        InstallIbeam();
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging) return;
        // ① 禁用窗口内的假退出：控件被「临时禁用再启用」（剥夺焦点）时脱离命中测试，框架抛 PointerExited，
        if (!IsEnabled) return;
        // ② 事件若在重新启用后才到：以"报点是否仍在域内"为准，仍在域内即视为假退出，维持工字；
        _pointerInside = IsWithin(e.GetCurrentPoint(Root).Position);
        if (_pointerInside) return;
        RestoreCursor();
    }

    /// <summary>点是否落在本控件范围内（几何判定）。</summary>
    private bool IsWithin(Point p) =>
        p.X >= 0 && p.Y >= 0 && p.X < Root.ActualWidth && p.Y < Root.ActualHeight;

    private void ClearText()
    {
        Host.Text = "";
        Host.SelectionStart = 0;
        Host.SelectionLength = 0;
        _caretIndex = 0;
        FocusHost();
        RebuildAdvances();
        UpdateCaretAndSelection();
        UpdateClearButton();
    }

    private double TextAreaOffsetX() => TextArea.TransformToVisual(Root).TransformPoint(new Point(0, 0)).X;

    private void SelectWordAt(int idx)
    {
        string s = Host.Text ?? "";
        if (s.Length == 0) return;
        int i = Math.Clamp(idx, 0, s.Length);
        int a = i, b = i;
        while (a > 0 && IsWordChar(s[a - 1])) a--;
        while (b < s.Length && IsWordChar(s[b])) b++;
        if (a == b) { a = Math.Min(i, s.Length); b = Math.Min(a + 1, s.Length); }
        Host.SelectionStart = a;
        Host.SelectionLength = b - a;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>把焦点交给宿主（本控件唯一的聚焦入口）。聚焦前立"自发起"标记，供调用方在 GettingFocus 里放行——否则调用方"只允许鼠标进框"的拦截会连本控件的编程聚焦一起挡掉。</summary>
    private void FocusHost()
    {
        IsRequestingFocus = true;
        if (!Host.Focus(FocusState.Programmatic)) IsRequestingFocus = false;
    }

    private void FocusEditor() => FocusEditor(MaxFocusTries);
    /// <summary>落焦点，失败就隔一个调度回合再试，最多 <see cref="MaxFocusTries"/> 次。
    /// 行内改名框是"上一拍才刚 Visible"的元素：容器与布局都还没落定时，那一次 Focus 会静默失败，
    /// 于是框亮着却不吃键盘（打字无反应、Enter/Esc 无效），看上去就是"卡在编辑态退不出去"。
    /// 拿到焦点后才进入正常的收尾（起光标、挪到文本末尾、滚到可见处）。</summary>
    private void FocusEditor(int tries)
    {
        if (Visibility != Visibility.Visible) return;
        FocusHost();
        if (Host.FocusState == FocusState.Unfocused)
        {
            if (tries > 0)
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => FocusEditor(tries - 1));
            }
            return;
        }
        // ⚠ 光标必须在这里【无条件】起表，不能只靠 Host_GotFocus。
        // 焦点若【已经在】宿主上（例如点 √/× 结束编辑时，按钮被调用方的 GettingFocus 拦截挡下、
        // 反之"点别处失焦退出"时焦点真丢了，GotFocus 会来，所以那条路一直正常 ——
        StartBlink();
        int len = Host.Text?.Length ?? 0;
        Host.SelectionStart = len;
        Host.SelectionLength = 0;
        _caretIndex = len;
        _scrollX = double.MaxValue;
        UpdateCaretAndSelection();
    }

    /// <summary>落焦点的重试次数：一行之内必成。给到 6 是留足"容器实例化 + 一次布局"的余量。</summary>
    private const int MaxFocusTries = 6;

    private void StartBlink()
    {
        _blink ??= CreateBlinkTimer();
        Caret.Opacity = 1;
        _blink.Start();
    }

    private void StopBlink()
    {
        _blink?.Stop();
        Caret.Opacity = 1;   // 收尾恒复位为"亮"：避免停表时恰好停在暗相位，下次显示"有概率不见"
    }

    /// <summary>输入/移动光标时把光标复位为"亮"，并重启 530ms 倒计时（与白板 ResetCaretBlink 同语义： 编辑后先亮满一拍再闪，消除"闪相位漂移"导致的时闪时不闪）。</summary>
    private void ResetBlink()
    {
        if (_blink?.IsEnabled != true) return;
        Caret.Opacity = 1;
        _blink.Stop();
        _blink.Start();
    }

    private DispatcherTimer CreateBlinkTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        t.Tick += (_, _) => OnBlinkTick();
        return t;
    }

    /// <summary>闪烁节拍：每拍先自愈校验（控件可见且宿主聚焦），条件不成立即停表并收起光标； 成立才翻转。这样即便某条焦点事件缺失，下一拍也会自动补齐，不再出现"时闪时不闪/不见"。</summary>
    private void OnBlinkTick()
    {
        if (Host.FocusState == FocusState.Unfocused || Visibility != Visibility.Visible)
        {
            Caret.Visibility = Visibility.Collapsed;
            _blink?.Stop();
            return;
        }
        Caret.Visibility = Visibility.Visible;
        Caret.Opacity = Caret.Opacity > 0.5 ? 0 : 1;
    }

    private void InstallIbeam()
    {
        if (_ibeamInstalled) return;
        CursorInterop.OverrideSystemCursorTo(CursorInterop.IdcIbeam);
        _ibeamInstalled = true;
    }

    private void UpdateCursorScope(Point p)
    {
        // 落在「清空 ×」热区上要显示箭头，否则整域工字
        bool overClear = ClearHit.Visibility == Visibility.Visible && p.X >= Root.ActualWidth - ClearHit.Width;
        if (overClear) RestoreCursor(); else InstallIbeam();
    }

    private void RestoreCursor()
    {
        if (!_ibeamInstalled) return;
        _ibeamInstalled = false;
        CursorInterop.RestoreSystemCursors();
    }
}
