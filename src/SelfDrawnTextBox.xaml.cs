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
    private DispatcherTimer? _blink;
    private long _lastPressMs;         // 上一次按下时刻（自行判双击，避开 PointerPressed Handled 后 Tapped 不触发）
    private int _lastPressIndex = -1;

    public SelfDrawnTextBox()
    {
        InitializeComponent();
        // Loaded 时字体的继承链已落定 ⇒ 重取行高/前缀宽度（构造期只拿到控件默认字号）
        Loaded += (_, _) =>
        {
            UpdateMetrics();
            RebuildAdvances();
            UpdateCaretAndSelection();
            // 受 AutoFocusOnShow 门控：否则开机时工具栏项（AutoFocusOnShow=False）会在 Loaded 里无条件抢焦点，
            if (AutoFocusOnShow && Visibility == Visibility.Visible) FocusEditor();
        };
        Unloaded += (_, _) => { RestoreCursor(); StopBlink(); };
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
        ApplyCompactMode();   // 默认非紧凑 = XAML 里的 4,6,4,6；显式调一次避免依赖"谁先设值"的顺序
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

    public static readonly DependencyProperty DigitsOnlyProperty = DependencyProperty.Register(
        nameof(DigitsOnly), typeof(bool), typeof(SelfDrawnTextBox), new PropertyMetadata(false, OnDigitsOnlyChanged));

    public bool DigitsOnly
    {
        get => (bool)(GetValue(DigitsOnlyProperty) ?? false);
        set => SetValue(DigitsOnlyProperty, value);
    }

    public static readonly DependencyProperty CompactModeProperty = DependencyProperty.Register(
        nameof(CompactMode), typeof(bool), typeof(SelfDrawnTextBox), new PropertyMetadata(false, OnCompactModeChanged));

    /// <summary>紧凑模式：去掉文本区上下各 6px 的留白，整控件高度 ≈ 行高 + 描边。
    /// 供"行高固定 22px 的列表内改名"用 —— 默认留白会把行撑高一截，进编辑时列表会跳一下。</summary>
    public bool CompactMode
    {
        get => (bool)(GetValue(CompactModeProperty) ?? false);
        set => SetValue(CompactModeProperty, value);
    }

    private static void OnCompactModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => (d as SelfDrawnTextBox)?.ApplyCompactMode();

    private void ApplyCompactMode()
        => TextArea.Margin = CompactMode ? new Thickness(4, 0, 4, 0) : new Thickness(4, 6, 4, 6);

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
        // Canvas 的期望尺寸恒为 0 ⇒ 不会像 Grid 那样"按内容定高"，盒子高度会被隐藏宿主 TextBox 单方面撑高/压扁，
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

        // Canvas 承载 ⇒ 用 Canvas.SetLeft/SetTop 定位；垂直方向按行高空出上下居中（Canvas 忽略对齐属性）
        double top = Math.Max(0, (TextArea.ActualHeight - _lineHeight) / 2);
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
        TextArea.Margin = new Thickness(4, 6, show ? 26 : 4, 6);
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
        FocusFrame.Visibility = Visibility.Visible;
        Caret.Visibility = Visibility.Visible;
        StartBlink();
        UpdateCaretAndSelection();
        UpdateClearButton();
        EditorGotFocus?.Invoke(this, EventArgs.Empty);
    }

    private void Host_LostFocus(object sender, RoutedEventArgs e)
    {
        FocusFrame.Visibility = Visibility.Collapsed;
        StopBlink();
        Caret.Visibility = Visibility.Collapsed;
        UpdateClearButton();
        EditorLostFocus?.Invoke(this, EventArgs.Empty);
        // 仅"仍在编辑（控件可见）"时请求提交；Cancel/Confirm 置 IsEditing=false 会先折叠控件 ⇒ 天然挡住重复提交
        if (Visibility == Visibility.Visible) CommitRequested?.Invoke(this, new RoutedEventArgs());
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

    private void FocusEditor()
    {
        if (Visibility != Visibility.Visible) return;
        FocusHost();
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
