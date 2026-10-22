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
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Markup;
using Windows.Storage;
using Windows.Data.Pdf;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;
using System.Runtime.InteropServices;
using GlanceRX.Interop;
using GlanceRX.Services;
using static GlanceRX.Tuning;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Drawing;

namespace GlanceRX;

public enum EditMode
{
    Navigate,
    Pen,
    Canvas
}

public enum AnnotationType
{
    Pen
}

/// <summary>操作指南浮层里的一行：左侧手势/按键标签 + 右侧本地化说明。 声明在【命名空间级】而非 MainPage 内部，是为了让 XAML 编译绑定 x:DataType="local:GuideRow" 能解析到类型（xmlns:local = "using:GlanceRX"）。</summary>
public sealed class GuideRow
{
    public string Gesture { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}

public sealed partial class MainPage : Page
{
    public static MainPage? Current { get; private set; }

    public bool HasUnsavedChanges { get; set; } = false;

    // 批量加载（从磁盘读回）标注时临时抑制"脏标记 + 自动保存"：对象初始化器会触发 AnnotationViewModel
    // 否则从磁盘读回"已保存"的标注会被误判成未保存更改，导致即使本次没画、关闭也弹保存询问。
    internal static bool SuppressAnnotationDirty { get; set; }

    private Windows.Storage.StorageFile? _pendingFileToLoad;

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is Windows.Storage.StorageFile file)
        {
            _pendingFileToLoad = file;
        }
    }

    private PdfRenderService? _pdfRenderService;
    private readonly SemaphoreSlim _ffiSemaphore = new(1, 1);
    // 缩略图渲染并发闸门（限 1）：侧边栏大距离跳转后 realized 一屏缩略图各自并发渲染会与主视图
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);
    // 缩略图【待渲染任务集合】（去重 + 在途上限）：PageListView 默认 CacheLength=4.0 会 realized
    private readonly HashSet<int> _thumbnailQueued = new();
    private const int MaxThumbnailPending = 8;
    private ScrollViewer? _sidebarScrollViewer;  // PageListView 内部 ScrollViewer（Loaded 后缓存，滚动补渲染用）
    private double _sidebarItemHeight;           // 缩略图项高实测缓存（~56px，GetSidebarItemHeight 惰性取）

    // GPU 阅读器为唯一渲染路径（GpuViewer / GpuViewerRight）。
    // This avoids doubling memory (the old path loaded the whole file into an
    private PdfRenderService? _pdfRenderServiceRight;
    private CancellationTokenSource? _pdfRenderCts;
    private CancellationTokenSource? _pdfRenderRightCts;
    private readonly HashSet<int> _realizedSidebar = new();
    private ObservableCollection<PdfPageViewModel> _pages = new();
    private int _currentPageIndex = 0;
    private bool _isUnloading = false;
    private string _currentPdfPath = "";
    private string? _currentPdfPassword;
    // 能否将批注写回原文件（覆盖保存）。加密/只读 PDF 为 false —— 这类文档永不改写原文件，
    private bool _canOverwriteOriginal = true;
    private SettingsService _settingsService = new();
    private bool _toolbarAlwaysVisible;
    private uint _totalPages = 0;

    private readonly BookmarkStore _bookmarkStore = new();
    private readonly ObservableCollection<BookmarkViewModel> _bookmarks = new();
    private readonly ObservableCollection<OutlineItemViewModel> _outlineItems = new();
    private enum SidebarMode { Thumbnails, Bookmarks, Outline }
    private SidebarMode _currentSidebarMode = SidebarMode.Thumbnails;
    // （侧边栏开关的阅读位置补偿已整体移到 GpuPageView：CaptureHostResizeAnchor / BeginHostResizeReanchor。

    private double _currentZoomPercent = 100;
    
    private EditMode _currentMode = EditMode.Navigate;
    private bool _isDrawingHighlight = false;
    private AnnotationViewModel? _activeHighlight;
    // 画笔色板第 1 位 = 主题强调紫（Obsidian 紫）：浅色 #9873F7 / 深色 #A882FF，
    private const string AccentColorTag = "Accent";

    private string _activeColorHex = AccentColorTag;

    // Live highlight brushes for the Pen mode ToggleButton: the checked-state background
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _penHighlightBrush =
        new(Microsoft.UI.Colors.Yellow);
    // 悬停/按下态底色 = 画笔色向白色提亮，制造点击反馈（否则三态同色、点击无反馈）。
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _penHighlightBrushHover =
        new(Microsoft.UI.Colors.Yellow);
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _penHighlightBrushPressed =
        new(Microsoft.UI.Colors.Yellow);
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _penForegroundBrush =
        new(Microsoft.UI.Colors.Black);

    // 颜色按钮不再有任何包围环，仅用「圆点大小」区分状态。
    private const double ColorSwatchBaseSize = 16.0;
    private const double ColorSwatchEnlargedSize = 16.0 * 1.6;
    private const double ColorSwatchBaseRadius = ColorSwatchBaseSize / 2.0;
    private const double ColorSwatchEnlargedRadius = ColorSwatchEnlargedSize / 2.0;
    private const double ColorSwatchEnlargedInset = (ColorSwatchEnlargedSize - ColorSwatchBaseSize) / 2.0;
    private Button? _selectedColorButton;

    private double _activeThickness = 3.5;
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _thicknessPresetTransparentBrush =
        new(Microsoft.UI.Colors.Transparent);
    // 粗细预设按钮的选中/悬浮背景。颜色按主题自适应（浅色=中灰、深色=亮灰）。
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _thicknessPresetSelectedBrush =
        new(Windows.UI.Color.FromArgb(255, 0xC0, 0xC0, 0xC0));

    /// <summary>按当前主题（浅色/深色）设置画笔胶囊选中提示色： 粗细选中背景：浅色=中灰、深色=亮灰（保证与深浅胶囊底色都有足够对比）。 （颜色按钮无环、纯靠大小区分，无需主题适配。）</summary>
    private void ApplyPenOptionThemeBrushes()
    {
        bool dark = this.ActualTheme == ElementTheme.Dark;
        _thicknessPresetSelectedBrush.Color = dark
            ? Windows.UI.Color.FromArgb(255, 0x52, 0x52, 0x52)
            : Windows.UI.Color.FromArgb(255, 0xC0, 0xC0, 0xC0);
    }

    // Undo/Redo history stack
    private List<EditorHistoryEntry> _annotationHistory = new();
    // 重做栈：撤销时把条目从历史栈移入此处（Ctrl+Y 重做）；新操作开始即清空（重做分支失效，
    private List<EditorHistoryEntry> _annotationRedo = new();

    /// 统一撤销/重做条目：把「撤销做什么、重做做什么」封成一对委托。
    private sealed class EditorHistoryEntry
    {
        private readonly Action _undo;
        private readonly Action _redo;
        public EditorHistoryEntry(Action undo, Action redo) { _undo = undo; _redo = redo; }
        public void Undo() => _undo();
        public void Redo() => _redo();
    }

    private ObservableCollection<PdfPageViewModel> _pagesRight = new();

    private bool _isSyncScrolling = false;
    private double _lastSyncValue = 0;

    private sealed class SaveToastItem
    {
        public Border Capsule = null!;
        public TranslateTransform Transform = null!;
        public DispatcherTimer Timer = null!;
        public Storyboard? SlideSb;   // 进行中的位移动画，重定位前先停，避免同属性双动画冲突
        public int Pos;
    }
    private readonly List<SaveToastItem> _saveToasts = new();
    private const int SaveToastMax = 5;          // 栈深上限：超出即强制淡出最旧一枚
    private const double SaveToastSlot = 50;
    private const double SaveToastSlideMs = 160;
    private IntPtr _hWnd;
    private const int IDC_ARROW = 32512;        // 系统箭头（= OCR_NORMAL），用于退出时兜底恢复
    private const uint SPI_SETCURSORS = 0x0057;  // SystemParametersInfo 命令：重载系统光标方案（恢复原生箭头）

    // 画笔模式圆环光标（窗口作用域，真实 HCURSOR + 作用域限定）。
    // WinUI 3 主视图（客户区）由 XAML Island 承载，光标由框架经【指针输入】设置，
    // 退出/崩溃都通过 SPI_SETCURSORS 兜底恢复，不会残留「卡死圆环」。
    private bool _penRingActive;
    private IntPtr _hRingCursor;        // 当前画笔圆环光标句柄（GDI 资源，需 DestroyIcon 释放）
    private int _ringDiameter;
    private string? _ringColorHex;
    private bool _ringInstalled;
    private int _ringSuppressCount;     // 临时抑制圆环的引用计数（如关闭窗口"保存更改"对话框、Ctrl+S 另存为对话框期间，整个窗口恢复原生鼠标）。支持嵌套。
    private Microsoft.UI.Xaml.DispatcherTimer? _ringScopeTimer;
    private DispatcherTimer? _zoomPillHideTimer; // 缩放胶囊隐藏兜底：Flyout(快速缩放菜单)关闭瞬间的输入上下文切换会吞掉胶囊的 PointerExited，导致胶囊"悬停卡住"不隐藏；低频轮询核对实时光标位置兜底淡出
    private bool _glyphStrokeScaleHooked;        // OpenSecondPdfGlyph 的描边是否已挂钩 RasterizationScale 变化（Loaded 后只挂一次）
    private bool _zoomPresetOverrideAttached;    // 缩放预设浮层 8 颗是否已接管（Flyout 内容首次打开才进可视树，Loaded / Opening 两处兜底各试一次）
    private bool _pageHoverDriveAttached;        // 整页 hover 几何驱动是否已挂到页面根（避免 Loaded 重入导致重复注册）
    private ListViewItem? _bookmarkPressedRow;   // 书签列表里当前被按下的那一行（判定见 BookmarkRow_PointerPressed：显式排除行内按钮/编辑框）
    private Point _bookmarkRowPointer;           // 书签行几何判定用的实时光标位置（MainPage 坐标系，由 PageRoot_PointerMoved 用 e.GetCurrentPoint(this) 维护）

    public MainPage()
    {
        Current = this;
        this.InitializeComponent();

        // 主视觉树内按钮：一律不可 Tab 聚焦（彻底关掉 Tab/方向键落到按钮的路径，兜底）。
        SetButtonsNonTabStop();
        AttachDefocusToPopupButtons();
        AttachColorSwatchHover();
        InstallThicknessPresetHoverBrushes();
        // 书签行的悬浮 / 点击态：改由指针几何驱动（不再采信框架悬停标记），详见方法注释。
        AttachBookmarkRowStateDrive();

        // 全局键盘聚焦拦截：禁止任何焦点落到 ButtonBase（用户要求按钮只由鼠标单击触发，
        this.AddHandler(GettingFocusEvent, new TypedEventHandler<UIElement, GettingFocusEventArgs>(OnGettingFocusBlockButtons), true);
        // 全局点击剥夺焦点（兜底）：鼠标点任意按钮后，在 PointerReleased 强制剥夺其焦点，
        this.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPointerReleasedDefocus), true);
        // 全局拦截 Tab：用户要求界面只由鼠标操作，Tab 不应把键盘焦点移到任何控件
        // （页数输入框、侧边栏缩略图、工具栏按钮等都在主视觉树内，Tab 的 KeyDown 会冒泡到此处）。
        this.AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDownSuppressTab), true);

        // 主键盘 = / - ：WinUI XAML 枚举解析不支持 OemPlus/OemMinus 字符串（WMC0055），
        var zoomInAccel = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = (Windows.System.VirtualKey)0xBB, // 主键盘 =（OemPlus 枚举名在本 SDK 投影中缺失，用键码 0xBB）
            Modifiers = Windows.System.VirtualKeyModifiers.Control
        };
        zoomInAccel.Invoked += ZoomIn_Invoked;
        this.KeyboardAccelerators.Add(zoomInAccel);

        var zoomOutAccel = new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = (Windows.System.VirtualKey)0xBD, // 主键盘 -（OemMinus 枚举名在本 SDK 投影中缺失，用键码 0xBD）
            Modifiers = Windows.System.VirtualKeyModifiers.Control
        };
        zoomOutAccel.Invoked += ZoomOut_Invoked;
        this.KeyboardAccelerators.Add(zoomOutAccel);

        // 模式切换快捷键：V=阅读 / B=画笔 / N=白板。与导航/缩放同机制，走输入管理器全局分发、
        var readModeAccel = new KeyboardAccelerator { Key = VirtualKey.V };
        readModeAccel.Invoked += ModeSwitch_Invoked;
        this.KeyboardAccelerators.Add(readModeAccel);
        var penModeAccel = new KeyboardAccelerator { Key = VirtualKey.B };
        penModeAccel.Invoked += ModeSwitch_Invoked;
        this.KeyboardAccelerators.Add(penModeAccel);
        var canvasModeAccel = new KeyboardAccelerator { Key = VirtualKey.N };
        canvasModeAccel.Invoked += ModeSwitch_Invoked;
        this.KeyboardAccelerators.Add(canvasModeAccel);

        PageListView.ItemsSource = _pages;
        BookmarkListView.ItemsSource = _bookmarks;
        OutlineListView.ItemsSource = _outlineItems;

        GpuViewer.CurrentPageChanged += (s, idx) =>
        {
            _currentPageIndex = idx;
            // 仅在输入框未获得焦点时回显页码：用户正在输入时不能覆盖其文本，
            // 否则每次滚动/缩放都会把光标顶到最左、并显示（可能错误的）页码。
            if (!PageNumberInput.HasEditorFocus)
            {
                PageNumberInput.Text = (idx + 1).ToString();
            }
            if (idx >= 0 && idx < _pages.Count && PageListView.SelectedIndex != idx)
            {
                PageListView.SelectedIndex = idx;
            }
            // 侧边栏跟随：直接操作内部 ScrollViewer 按索引滚动（不依赖 ScrollIntoView/选中项自动滚动——
            ScrollSidebarToIndex(idx);
            // 主视图翻页 → 兜底补渲染新页附近缺图缩略图（Low 优先级：等侧边栏滚动
            this.DispatcherQueue?.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RefillThumbnailQueue);
        };
        GpuViewer.ZoomPercentChanged += (s, zp) => SetZoomPercent(zp);
        // GPU 视图滚动/缩放 → 更新中间同步滚动条，并剥夺页码框焦点（见 DefocusPageNumberInput 注释）。
        GpuViewer.ViewportChanged += (s, e) => { DefocusPageNumberInput(); UpdateSyncScrollBar(); };
        GpuViewerRight.ViewportChanged += (s, e) => { DefocusPageNumberInput(); UpdateSyncScrollBar(); };
        // 分屏同步滚动条 SyncScrollBar 的滑块拖拽 → 两侧视图进入"拖拽期抑制渲染请求"，
        GpuViewer.LayoutModeChanged += (s, mode) => UpdateLayoutToggleVisual();
        GpuViewerRight.LayoutModeChanged += (s, mode) => UpdateLayoutToggleVisual();
        GpuViewer.ArrangementChanged += (s, ar) => { if (LayoutViewComboBox != null) LayoutViewComboBox.SelectedIndex = (int)ar; };
        GpuViewerRight.ArrangementChanged += (s, ar) => { if (LayoutViewComboBox != null) LayoutViewComboBox.SelectedIndex = (int)ar; };

        // GPU 标注叠加层：数据仍在 _pages[i].Annotations（保存/撤销/导出管线全复用），
        GpuViewer.AnnotationProvider = idx =>
            (idx >= 0 && idx < _pages.Count) ? _pages[idx].Annotations : null;
        GpuViewer.AnnotationPointerPressed += GpuAnnotation_Pressed;
        GpuViewer.AnnotationPointerMoved += GpuAnnotation_Moved;
        GpuViewer.AnnotationPointerReleased += GpuAnnotation_Released;

        GpuViewer.CanvasNodeDragged += GpuCanvas_Dragged;
        GpuViewer.CanvasNodeDragCompleted += GpuCanvas_DragCompleted;
        GpuViewer.CanvasNodeCreateRequested += GpuCanvas_CreateRequested;
        GpuViewer.CanvasNodeDeleteRequested += GpuCanvas_DeleteRequested;
        GpuViewer.CanvasNodeTextCommitted += GpuCanvas_TextCommitted;
        GpuViewer.CanvasNodeConnectionRequested += GpuCanvas_ConnectionRequested;
        GpuViewer.CanvasNodeResized += GpuCanvas_Resized;
        GpuViewer.CanvasNodeResizeCompleted += GpuCanvas_ResizeCompleted;
        GpuViewer.CanvasEdgeDeleteRequested += GpuCanvas_EdgeDeleteRequested;
        GpuViewer.CanvasEdgeStyleChanged += GpuCanvas_EdgeStyleChanged;
        GpuViewer.CanvasEdgeRerouteRequested += GpuCanvas_EdgeRerouteRequested;
        GpuViewer.CanvasNodeColorChanged += GpuCanvas_NodeColorChanged;
        GpuViewer.CanvasNodeCreateConnectedRequested += GpuCanvas_CreateConnectedRequested;
        GpuViewer.CanvasNodeBringToFrontRequested += GpuCanvas_BringToFront;
        GpuViewer.CanvasUndoRequested += (_, __) => RequestUndo();
        GpuViewer.CanvasRedoRequested += (_, __) => RequestRedo();
        // Ctrl+Z / Ctrl+Y 不走 Page 加速器（在文本框聚焦时会被吞掉），故由整页 KeyDown 兜底。
        this.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(UndoRedo_KeyDown), true);

        WelcomePanel.Visibility = Visibility.Visible;
        SidebarSplitView.Visibility = Visibility.Collapsed;

        // 欢迎页(未打开文档)时隐藏"只对文档有意义"的工具栏按钮：画笔模式 / 旋转 / 纵横排列。
        WelcomePanel.RegisterPropertyChangedCallback(UIElement.VisibilityProperty,
            (s, dp) => ApplyWelcomeChrome(WelcomePanel.Visibility == Visibility.Visible));
        ApplyWelcomeChrome(true);   // 初始就是欢迎页（赋同值不触发回调，故显式应用一次）

        this.Loaded += MainPage_Loaded;
        // 【开关侧边栏 = 保持"阅读位置不变量"，而不是死板 fit 到视口】
        // 目标：纵向布局下，开关前/后视口左右两条边线切到的内容完全一致（过渡时长用框架默认）。
        SidebarSplitView.PaneOpening += (s, e) => QueueSidebarReanchor();
        SidebarSplitView.PaneClosing += (s, e) => QueueSidebarReanchor();
        SidebarSplitView.PaneOpened += (s, e) => UpdateSidebarTopInset();   // 展开后按实测工具栏高度让出顶部空间
        // 主题切换（设置面板选择/跟随系统变化）时刷新画笔胶囊的选中提示色，
        this.ActualThemeChanged += (s, e) => RefreshPenOptionThemeHighlights();
        this.ActualThemeChanged += (s, e) => UpdateSidebarBackground();
        // 构造期首帧即上色（ActualThemeChanged 不保证首帧触发）；不依赖 XAML 主题键，规避主题切换错乱根因。
        UpdateSidebarBackground();
    }

    // 侧边栏开关的重锚触发点：过渡起点事件之后排一个 Low 回合再重锚。
    private void QueueSidebarReanchor()
    {
        if (GpuViewer == null || GpuViewer.Visibility != Visibility.Visible) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (GpuViewer == null || GpuViewer.Visibility != Visibility.Visible) return;
            GpuViewer.BeginHostResizeReanchor();
        });
    }

    /// <summary>主题变化后：更新胶囊提示色 brush，并重刷当前选中态（颜色大小提示 + 粗细背景）。 共享 brush 对象改色后，重新调用高亮函数让每个按钮重新取 brush 引用并重绘。</summary>
    private void RefreshPenOptionThemeHighlights()
    {
        ApplyPenOptionThemeBrushes();
        ApplyPenHighlight();
        SyncColorSwatchHighlight();
        SelectThicknessPreset(_activeThickness);
    }

    /// 让侧边栏底色与主视图基色区分（现代分层配色）： 浅色模式用比主视图基色(#F3F3F3)更深的 #EAEAEA，深色模式用比主视图基色(#202020)更浅的 #2A2A2A。
    /// ⚠ 必须在【代码后置】着色，不能引用 App.xaml 主题字典的【自定义】键（那会破坏主题切换）；
    /// 也不能覆盖系统键 CardStrokeColorDefaultBrush（连坐 9 处引用 + 25 个别名）。
    private void UpdateSidebarBackground()
    {
        if (ThumbnailSidebarPanel == null) return;
        var isDark = ((Application.Current as App)?.MainWindow as MainWindow)?.IsDarkTheme
                     ?? (ActualTheme == ElementTheme.Dark);
        ThumbnailSidebarPanel.Background = isDark
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xEA, 0xEA, 0xEA));
    }

    /// 工具栏内任意按钮点击后的焦点剥夺处理。
    /// Focus 会静默失败、焦点仍留在按钮上→Enter/Space 仍重触发。
    private void OnPointerReleasedDefocus(object sender, PointerRoutedEventArgs e)
    {
        bool inFlyout = false;
        ButtonBase? clicked = null;
        for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is Button b && b.Flyout != null) { inFlyout = true; break; }
            if (d is FlyoutPresenter) { inFlyout = true; break; }
            if (d is ButtonBase bb) clicked = bb;
        }
        if (inFlyout || clicked == null) return;
        DefocusButton(clicked);
    }

    /// 禁止任何焦点落到 ButtonBase 上：用户要求按钮只由鼠标单击触发，不保留 Tab+Enter 触发路径， 点后焦点也不残留→Enter/Space 不重触发。
    private void OnGettingFocusBlockButtons(UIElement sender, GettingFocusEventArgs e)
    {
        // ⚠ 一律用 TryCancel()（是否取消由这里判），不要 e.Cancel = true 硬拦：WinUI 在
        if (e.NewFocusedElement is ButtonBase)
            e.TryCancel();
        // 页数输入框：焦点其实落在自绘控件的隐藏宿主上，故按"是否本控件的宿主"识别。仅允许用户
        else if (PageNumberInput.OwnsFocusedElement(e.NewFocusedElement)
                 && e.FocusState != FocusState.Pointer && !PageNumberInput.IsRequestingFocus)
            e.TryCancel();
    }

    /// 全局拦截 Tab：主视觉树内任意控件按下 Tab，标记 Handled=true，框架即不执行默认的 Tab 焦点跳转， 使 Tab 在界面上彻底失效（用户要求只由鼠标操作）。
    private void OnKeyDownSuppressTab(object sender, KeyRoutedEventArgs e)
    {
        // Tab：剥夺焦点遍历（保留自定义控件焦点行为），其余按键放行。
        if (e.Key == VirtualKey.Tab)
        {
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            // 白板连线拖拽中按 Esc → 取消本次连线（松手落空同样会取消，Esc 只是更快的退出手感）。
            // ⚠ 不接管"按住波浪键的待接态"：那是白板内部状态，取消由 GpuPageView 自己管（松开它 / 同卡再点 / 左键点空白）。
            if (GpuViewer.CancelCanvasConnection()) e.Handled = true;
        }
        else if (e.Key == VirtualKey.Delete &&
                 !e.Handled && _currentMode == EditMode.Canvas && !GpuViewer.IsCanvasEditing)
        {
            // 白板模式：优先删选中的连线，否则删选中的卡片。编辑文本时交给文本框原生处理。
            // ⚠ 只认 Delete，**不认 Backspace**：退格是打字/翻页时最容易被误按的键，一按就整张卡片或
            if (GpuViewer.HasSelectedCanvasEdge) { GpuViewer.DeleteSelectedCanvasEdge(); e.Handled = true; }
            else if (GpuViewer.HasSelectedCanvasNode) { GpuViewer.DeleteSelectedCanvasNode(); e.Handled = true; }
        }
    }

    /// <summary>设置面板（Flyout，独立视觉树）内的 Tab 拦截：KeyDown 从 Flyout 内控件冒泡到此处， 标记 Handled=true 阻止焦点跳到 ThemeComboBox / 网站链接 / GitHub 链接等。</summary>
    private void SettingsFlyout_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Tab)
            e.Handled = true;
    }

    /// 强制剥夺某个按钮的键盘焦点：临时禁用再启用，使其无法持焦（焦点被交出），随即重新启用，用户无感。
    /// 不能依赖 Control.Focus(FocusState.Unfocused)（抛 E_INVALIDARG），也不能依赖 GpuViewer.Focus(Programmatic)
    private void DefocusButton(ButtonBase bb)
    {
        if (bb == null) return;
        bb.IsEnabled = false;
        bb.IsEnabled = true;
    }

    /// 主视觉树内的按钮：一律设为不可 Tab 聚焦（IsTabStop=false），彻底关闭 Tab/方向键落到按钮上的路径。
    private void SetButtonsNonTabStop()
    {
        if (this.Content is DependencyObject root)
            WalkSetNonTabStop(root);
    }

    private void WalkSetNonTabStop(DependencyObject d)
    {
        if (d is Popup) return;
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is ButtonBase bb)
                bb.IsTabStop = false;
            WalkSetNonTabStop(child);
        }
    }

    /// <summary>Popup（PenOptionsPopup）内的按钮在独立视觉树，主页面冒泡的 PointerReleased/GettingFocus 抓不到。 故在此直接给每个按钮订阅：① Click 后剥夺焦点（防 Enter/Space 重触发）；</summary>
    private void AttachDefocusToPopupButtons()
    {
        if (PenOptionsPopup?.Child is DependencyObject root)
            WalkAttachPopupButtons(root);
    }

    private void WalkAttachPopupButtons(DependencyObject d)
    {
        if (d is ButtonBase bb)
        {
            bb.IsTabStop = false;
            bb.Click += PopupButton_Click;
            bb.AddHandler(GettingFocusEvent, new TypedEventHandler<UIElement, GettingFocusEventArgs>(PopupButton_GettingFocus), true);
        }
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
            WalkAttachPopupButtons(VisualTreeHelper.GetChild(d, i));
    }

    private void PopupButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ButtonBase bb)
            DefocusButton(bb);
    }

    private void PopupButton_GettingFocus(UIElement sender, GettingFocusEventArgs e)
    {
        // 改用 TryCancel()：焦点从外部重新进入（FromOutside）等不可取消情形下返回 false 而非抛
        if (e.NewFocusedElement == sender) e.TryCancel();
    }

    /// 对比视图「打开第二份 PDF」圆形按钮的手绘符号，固定为「恒定 2 物理像素」描边： 描边 DIP = 2 / RasterizationScale，保证任意缩放下设备描边宽恒为 2（偶）像素。
    private void ApplyGlyphStrokeThickness()
    {
        double scale = this.XamlRoot?.RasterizationScale ?? 1.0;
        if (!(scale > 0)) scale = 1.0;
        double t = 2.0 / scale;
        if (OpenSecondPdfGlyph != null) OpenSecondPdfGlyph.StrokeThickness = t;
        if (CloseSecondPdfGlyph != null) CloseSecondPdfGlyph.StrokeThickness = t;
        if (ZoomOutGlyph != null) ZoomOutGlyph.StrokeThickness = t;
        if (ZoomInGlyph != null) ZoomInGlyph.StrokeThickness = t;
        if (FitWidthGlyph != null) FitWidthGlyph.StrokeThickness = t;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeToWindowStateChanges();
        InitializeSettings();
        InitializeLocalization();
        UpdateTopToolbarMargin();

        // 圆形按钮手绘符号的「恒定 2 物理像素」描边：Loaded 后先应用一次，再挂 RasterizationScale 变化
        ApplyGlyphStrokeThickness();
        if (!_glyphStrokeScaleHooked && this.XamlRoot != null)
        {
            _glyphStrokeScaleHooked = true;
            this.XamlRoot.Changed += (s, args) => ApplyGlyphStrokeThickness();
        }

        // 接管原生视觉状态机。
        // · 顶部工具栏「阅读模式」按钮
        ToggleButtonVisualOverride.Attach(ReadModeButton);
        // 白板模式按钮：与阅读/画笔同级，采用同一套强调色（accent 紫）checked / hover / pressed
        ToggleButtonVisualOverride.Attach(CanvasModeButton);
        ToggleButtonVisualOverride.Attach(ModeThumbnailsButton);
        ToggleButtonVisualOverride.Attach(ModeBookmarksButton);
        ToggleButtonVisualOverride.Attach(ModeOutlineButton);
        ToggleButtonVisualOverride.Attach(SplitViewToggleButton);
        ToggleButtonVisualOverride.Attach(PenModeButton, key => key switch
        {
            "ToggleButtonBackgroundChecked" => _penHighlightBrush,
            "ToggleButtonBackgroundCheckedPointerOver" => _penHighlightBrushHover,
            "ToggleButtonBackgroundCheckedPressed" => _penHighlightBrushPressed,
            // 符号前景必须随画笔色反相（BT.601 亮度），否则深色画笔（如黑色）下符号与底同色不可见。
            "ToggleButtonForegroundChecked" => _penForegroundBrush,
            "ToggleButtonForegroundCheckedPointerOver" => _penForegroundBrush,
            "ToggleButtonForegroundCheckedPressed" => _penForegroundBrush,
            _ => null,
        });

        // 接管普通功能按钮（非选中态）的视觉输出 —— 仅推广点击光效、不改颜色，与 ToggleButton 同源。
        ToggleButtonVisualOverride.Attach(ToggleSidebarButton);
        ButtonVisualOverride.Attach(PrevPageButton);
        ButtonVisualOverride.Attach(NextPageButton);
        ButtonVisualOverride.Attach(RotateButton);
        ButtonVisualOverride.Attach(LayoutToggleButton);
        ButtonVisualOverride.Attach(SaveButton);
        ButtonVisualOverride.Attach(PrintButton);
        ButtonVisualOverride.Attach(SettingsButton);
        ButtonVisualOverride.Attach(AddBookmarkButton);

        // 欢迎页「打开 PDF」按钮：几何驱动 + accent 紫配色，避开原生 VSM flicker。
        ButtonVisualOverride.Attach(OpenPdfButton, accent: true);

        // 对比视图开/关 PDF 的两颗 pill：改用 ButtonVisualOverride（整颗变色，与功能按钮同逻辑）。
        ButtonVisualOverride.Attach(OpenSecondPdfButton);
        ButtonVisualOverride.Attach(CloseSecondPdfButton);

        // 设置浮层内的「操作指南」按钮：与标准功能按钮同源（静止带底 + hover/pressed 整颗变色），
        // ⚠ inPopupTree=true：按钮活在主可视树之外的 SettingsFlyout 独立树，跨树 GetCurrentPoint 坐标不可靠，
        ButtonVisualOverride.Attach(GuideButton, inPopupTree: true);

        // 缩放胶囊 4 颗（ZoomOut / ZoomIn / ZoomValue / FitWidth）：与对比视图两颗【完全同一路线】——
        // 同样套 ButtonNoStateMachineStyle、同样静止就带底、hover / pressed 整颗变色。
        // ⚠ 静止档为【完全透明】（transparentAtRest）：胶囊背景是【亚克力】FlyoutPresenterBackground
        //   ⚠ 这里【不传 inPopupTree】：胶囊在主可视树，必须参与整页 hover 几何驱动，否则悬浮色残留回归。
        ButtonVisualOverride.Attach(ZoomOutButton, transparentAtRest: true);
        ButtonVisualOverride.Attach(ZoomInButton, transparentAtRest: true);
        ButtonVisualOverride.Attach(ZoomValueButton, transparentAtRest: true);
        ButtonVisualOverride.Attach(FitWidthButton, transparentAtRest: true);

        // 顶部工具栏「…」溢出收纳按钮：与前排功能按钮同一套 hover/pressed 反馈（静止带底、整颗变色）。
        ButtonVisualOverride.Attach(ToolbarMoreButton);
        InitializeToolbarOverflow();

        AttachZoomPresetOverrides();

        // —— 悬浮色残留的通用根治 ——
        // hover 若只由【每颗按钮自己】的 PointerEntered/Moved/Exited 维护（基类 Track），
        if (this.Content is UIElement pageRoot && !_pageHoverDriveAttached)
        {
            pageRoot.AddHandler(PointerMovedEvent, new PointerEventHandler(PageRoot_PointerMoved), true);
            _pageHoverDriveAttached = true;
        }

        AttachSidebarScrollRefill();

        // 缩放胶囊隐藏兜底：快速缩放菜单(Flyout)打开期间 BottomZoomPill_PointerExited 被忽略，
        if (_zoomPillHideTimer == null)
        {
            _zoomPillHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _zoomPillHideTimer.Tick += ZoomPillHideTimer_Tick;
        }
        try { _zoomPillHideTimer.Start(); } catch { }

        // Hide the toolbar when the pointer leaves the whole page (i.e. exits the window).
        this.PointerExited += XamlRoot_PointerExited;

        // Safety net for the auto-hide: a moved-anywhere handler (handledEventsToo, so it
        this.AddHandler(PointerMovedEvent, new PointerEventHandler(Root_PointerMoved), true);

        // 页面基础的指针事件同样锚定"最近一次真实移动"的时刻。工具栏子级若把事件标记 Handled，
        this.AddHandler(PointerEnteredEvent, new PointerEventHandler(Root_PointerActivity), true);
        this.AddHandler(PointerPressedEvent, new PointerEventHandler(Root_PointerActivity), true);
        this.AddHandler(PointerExitedEvent, new PointerEventHandler(Root_PointerActivity), true);

        // Keep the floating pen-options capsule anchored below the Pen button on resize,
        this.SizeChanged += (s, args) =>
        {
            UpdateTopToolbarMargin();
            ApplyGlyphStrokeThickness();
            if (PenOptionsPopup != null && PenOptionsPopup.IsOpen)
                PositionPenOptionsPopup();
        };
        // 视图区自身尺寸变化（含首次打开文档由 Collapsed→Visible，0 → 实际尺寸）也要重算白板胶囊的安全区：
        // 否则"打开文档后第一次钳制"用的还是 0 内缩，胶囊照样会被顶栏宿主压住。
        GpuViewer.SizeChanged += (s, args) => UpdateCanvasViewportInsets();

        // Pen mode 按钮的勾选态：底色 + 符号前景都跟随实时画笔色，交给 ToggleButtonVisualOverride 的
        // 单按钮覆写委托处理。背景三态分键，且**与阅读模式按钮同一机制**。
        if (PenModeButton != null)
        {
            ApplyPenHighlight();
        }

        // Thickness preset buttons: no white-ring affordance on hover/press (the gray

        // 首次按当前主题定色（ActualTheme 此时已确定），再设置默认选中高亮。
        ApplyPenOptionThemeBrushes();

        // Mark the default thickness preset (3.5 px) as selected.
        SelectThicknessPreset(3.5);
        SyncColorSwatchHighlight();

        // Dismiss the pen-options capsule on any outside pointer press, without eating
        this.AddHandler(UIElement.PointerPressedEvent,
                        new PointerEventHandler(Root_PointerPressed), true);

        if (_pendingFileToLoad != null)
        {
            // When opening via file association (double-click from explorer),
            WelcomePanel.Visibility = Visibility.Collapsed;
            SidebarSplitView.Visibility = Visibility.Visible;
            
            UpdateWindowTitle(_pendingFileToLoad.Name);
            try
            {
                await LoadPdfAsync(_pendingFileToLoad.Path);
            }
            catch (Exception)
            {
                // If loading fails, show welcome screen again. Reset the toolbar
                UpdateWindowTitle(null);
                WelcomePanel.Visibility = Visibility.Visible;
                SidebarSplitView.Visibility = Visibility.Collapsed;
            }
            _pendingFileToLoad = null;
        }
        else if (App.PendingFilePath != null)
        {
            WelcomePanel.Visibility = Visibility.Collapsed;
            SidebarSplitView.Visibility = Visibility.Visible;
            
            try
            {
                string filePath = App.PendingFilePath;
                App.PendingFilePath = null; // Clear immediately to prevent re-use
                
                // 直接按路径打开：不再经 StorageFile.GetFileFromPathAsync。本地文件实测该调用
                UpdateWindowTitle(Path.GetFileName(filePath));
                await LoadPdfAsync(filePath);
            }
            catch (Exception)
            {
                // If loading fails, show welcome screen again. Reset the toolbar
                UpdateWindowTitle(null);
                WelcomePanel.Visibility = Visibility.Visible;
                SidebarSplitView.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void InitializeLocalization()
    {
        try
        {

            if (SettingsTitleText != null) SettingsTitleText.Text = GlanceRX.Services.LocalizationService.Get("Settings");
            if (AppThemeLabel != null) AppThemeLabel.Text = GlanceRX.Services.LocalizationService.Get("AppTheme");

            if (ThemeLightItem != null) ThemeLightItem.Content = GlanceRX.Services.LocalizationService.Get("ThemeLight");
            if (ThemeDarkItem != null) ThemeDarkItem.Content = GlanceRX.Services.LocalizationService.Get("ThemeDark");
            if (ThemeDefaultItem != null) ThemeDefaultItem.Content = GlanceRX.Services.LocalizationService.Get("ThemeSystem");

            if (LayoutViewLabel != null) LayoutViewLabel.Text = GlanceRX.Services.LocalizationService.Get("LayoutView");
            if (SinglePageItem != null) SinglePageItem.Content = GlanceRX.Services.LocalizationService.Get("SinglePage");
            if (TwoPageItem != null) TwoPageItem.Content = GlanceRX.Services.LocalizationService.Get("TwoPage");
            if (BookItem != null) BookItem.Content = GlanceRX.Services.LocalizationService.Get("BookView");

            if (ToolbarDisplayLabel != null) ToolbarDisplayLabel.Text = GlanceRX.Services.LocalizationService.Get("ToolbarDisplay");
            if (ToolbarAlwaysItem != null) ToolbarAlwaysItem.Content = GlanceRX.Services.LocalizationService.Get("ToolbarAlwaysVisible");
            if (ToolbarAutoHideItem != null) ToolbarAutoHideItem.Content = GlanceRX.Services.LocalizationService.Get("ToolbarAutoHide");

            if (GuideButtonText != null) GuideButtonText.Text = GlanceRX.Services.LocalizationService.Get("GuideTitle");
            if (GuideTitleText != null) GuideTitleText.Text = GlanceRX.Services.LocalizationService.Get("GuideTitle");
            if (GuideMouseSectionText != null) GuideMouseSectionText.Text = GlanceRX.Services.LocalizationService.Get("GuideMouseSection");
            if (GuideKeyboardSectionText != null) GuideKeyboardSectionText.Text = GlanceRX.Services.LocalizationService.Get("GuideKeyboardSection");
            if (GuideCloseButton != null) GuideCloseButton.Content = GlanceRX.Services.LocalizationService.Get("GuideClose");
            BuildGuideRows();

            if (WelcomeSubtitleText != null) WelcomeSubtitleText.Text = GlanceRX.Services.LocalizationService.Get("WelcomeSubtitle");
            if (WelcomeOpenButtonText != null) WelcomeOpenButtonText.Text = GlanceRX.Services.LocalizationService.Get("WelcomeOpenButton");

            if (AddBookmarkButton != null)
                AutomationProperties.SetName(AddBookmarkButton, GlanceRX.Services.LocalizationService.Get("AddBookmark"));
            ApplySidebarMode(); // refresh header text after localization strings are ready
        }
        catch { }
    }

    private void InitializeSettings()
    {
        try
        {
            string themeStr = _settingsService.AppTheme;

            // Set ComboBox index
            int selectedIndex = 2;
            if (themeStr == "Light") selectedIndex = 0;
            else if (themeStr == "Dark") selectedIndex = 1;

            if (ThemeComboBox != null)
            {
                ThemeComboBox.SelectedIndex = selectedIndex;
            }

            // 布局视图下拉初始选中项：默认单页（0）。与 ThemeComboBox 同源——初始选中项用代码设，
            // 不在 XAML 写 SelectedIndex（否则 InitializeComponent 期间会同步触发 SelectionChanged，
            if (LayoutViewComboBox != null)
            {
                LayoutViewComboBox.SelectedIndex = 0;
            }

            // 工具栏显示方式下拉初始选中项：0=常驻显示，1=鼠标移开便收起（默认，与原有行为一致）。
            _toolbarAlwaysVisible = _settingsService.ToolbarAlwaysVisible;
            if (ToolbarDisplayComboBox != null)
            {
                ToolbarDisplayComboBox.SelectedIndex = _toolbarAlwaysVisible ? 0 : 1;
            }

            if (Enum.TryParse(themeStr, out ElementTheme theme))
            {
                var mainWindow = (Application.Current as App)?.MainWindow;
                if (mainWindow?.Content is FrameworkElement rootElement)
                {
                    rootElement.RequestedTheme = theme;
                    GlanceRX.Services.ToggleButtonVisualOverride.RefreshAll();
                    GlanceRX.Services.ButtonVisualOverride.RefreshAll();
                }

                if (mainWindow is MainWindow glanceWindow)
                {
                    bool isDark = (theme == ElementTheme.Dark) ||
                                  (theme == ElementTheme.Default &&
                                   Application.Current.RequestedTheme == ApplicationTheme.Dark);
                    glanceWindow.UpdateTitleBarTheme(isDark);
                    glanceWindow.UpdateWindowBackground(theme);
                }
            }

        }
        catch { }
    }

        private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox == null) return;
        
        if (ThemeComboBox.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is string themeStr)
        {
            if (Enum.TryParse(themeStr, out ElementTheme theme))
            {
                var mainWindow = (Application.Current as App)?.MainWindow;
                if (mainWindow?.Content is FrameworkElement rootElement)
                {
                    rootElement.RequestedTheme = theme;
                    GlanceRX.Services.ToggleButtonVisualOverride.RefreshAll();
                    GlanceRX.Services.ButtonVisualOverride.RefreshAll();
                }

                if (mainWindow is MainWindow glanceWindow)
                {
                    bool isDark = (theme == ElementTheme.Dark) ||
                                  (theme == ElementTheme.Default &&
                                   Application.Current.RequestedTheme == ApplicationTheme.Dark);
                    glanceWindow.UpdateTitleBarTheme(isDark);
                    glanceWindow.UpdateWindowBackground(theme);
                }

                _settingsService.AppTheme = themeStr;
            }
        }
    }

        private void LayoutViewComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LayoutViewComboBox == null) return;
        if (LayoutViewComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tagStr && int.TryParse(tagStr, out int idx))
        {
            // 下拉只管纵向列数/排版；横向胶片条模式忽略 Arrangement（RecomputeLayout 横向分支不读它）。
            GpuViewer.Arrangement = (PageArrangement)idx;
            if (GpuViewerRight != null && GpuViewerRight.Visibility == Visibility.Visible)
                GpuViewerRight.Arrangement = (PageArrangement)idx;
        }
    }

        private void ToolbarDisplayComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolbarDisplayComboBox == null) return;
        bool always = ToolbarDisplayComboBox.SelectedIndex == 0;
        _settingsService.ToolbarAlwaysVisible = always;
        ApplyToolbarAlwaysVisible(always);
    }

    /// <summary>应用「工具栏显示方式」设置。 常驻显示：取消待收起计时并强制展开工具栏，后续所有收起请求被守卫拦截。 鼠标移开便收起：恢复原有自动收起行为（立即请求一次收起，指针仍在本身上时不收）。</summary>
    private void ApplyToolbarAlwaysVisible(bool always)
    {
        _toolbarAlwaysVisible = always;
        if (TopToolbar == null) return;

        if (always)
        {
            CancelToolbarHide();
            ShowTopToolbar();
        }
        else if (TopToolbar.IsHitTestVisible)
        {
            RequestHideTopToolbar();
        }
    }

    /// <summary>本地化缩写，仅用于构建指南行，避免每行重复全限定名。</summary>
    private static string Loc(string key) => GlanceRX.Services.LocalizationService.Get(key);

    /// 构建指南内容。两条itum都以【源码】为准而非凭记忆抄先手： · 鼠标行 ↔ GpuPageView.xaml.
    private void BuildGuideRows()
    {
        if (GuideMouseItems != null)
        {
            GuideMouseItems.ItemsSource = new List<GuideRow>
            {
                new() { Gesture = Loc("GuideMousePanGesture"),         Description = Loc("GuideMousePanDesc") },
                new() { Gesture = Loc("GuideMousePenDrawGesture"),     Description = Loc("GuideMousePenDrawDesc") },
                new() { Gesture = Loc("GuideMouseWheelGesture"),       Description = Loc("GuideMouseWheelDesc") },
                new() { Gesture = Loc("GuideMouseCtrlWheelGesture"),   Description = Loc("GuideMouseCtrlWheelDesc") },
                new() { Gesture = Loc("GuideMouseShiftWheelGesture"),  Description = Loc("GuideMouseShiftWheelDesc") },
                new() { Gesture = Loc("GuideMouseDoubleClickGesture"), Description = Loc("GuideMouseDoubleClickDesc") },
                new() { Gesture = Loc("GuideMouseRightZoomGesture"),   Description = Loc("GuideMouseRightZoomDesc") },
            };
        }

        if (GuideKeyboardItems != null)
        {
            // 按键标签是【符号】而非可译散文（键帽上写的就是 Ctrl、Home、End），故不进本地化字典；
            GuideKeyboardItems.ItemsSource = new List<GuideRow>
            {
                new() { Gesture = "Ctrl + O",         Description = Loc("GuideKeyOpenDesc") },
                new() { Gesture = "Ctrl + +",         Description = Loc("GuideKeyZoomInDesc") },
                new() { Gesture = "Ctrl + -",         Description = Loc("GuideKeyZoomOutDesc") },
                new() { Gesture = "Ctrl + 0",         Description = Loc("GuideKeyZoomResetDesc") },
                new() { Gesture = "F",                Description = Loc("GuideKeyFitDesc") },
                new() { Gesture = "G",                Description = Loc("GuideKeyToggleAnnotsDesc") },
                new() { Gesture = "↑ ↓ ← →", Description = Loc("GuideKeyArrowsDesc") },
                new() { Gesture = "Space",            Description = Loc("GuideKeySpaceViewportDesc") },
                new() { Gesture = "PageDown",         Description = Loc("GuideKeyNextPageDesc") },
                new() { Gesture = "PageUp",           Description = Loc("GuideKeyPrevPageDesc") },
                new() { Gesture = "Home",             Description = Loc("GuideKeyFirstPageDesc") },
                new() { Gesture = "End",              Description = Loc("GuideKeyLastPageDesc") },
                new() { Gesture = "Ctrl + D",         Description = Loc("GuideKeyBookmarkDesc") },
                new() { Gesture = "Ctrl + Z",         Description = Loc("GuideKeyUndoDesc") },
                new() { Gesture = "Ctrl + Y",         Description = Loc("GuideKeyRedoDesc") },
                new() { Gesture = "Ctrl + B",         Description = Loc("GuideKeyBoldDesc") },
                new() { Gesture = "Ctrl + I",         Description = Loc("GuideKeyItalicDesc") },
                new() { Gesture = "Ctrl + U",         Description = Loc("GuideKeyStrikeDesc") },
                new() { Gesture = "Ctrl + H",         Description = Loc("GuideKeyHighlightDesc") },
                new() { Gesture = "Ctrl + J",         Description = Loc("GuideKeyInlineCodeDesc") },
                new() { Gesture = "Ctrl + S",         Description = Loc("GuideKeySaveDesc") },
                new() { Gesture = "Ctrl + P",         Description = Loc("GuideKeyPrintDesc") },
            };
        }
    }

    /// <summary>设置菜单「操作指南」按钮：先收起设置 Flyout（弹层绘制在 popup 层，若不收起会盖在 主视觉树里的浮层之上），再显示指南浮层。</summary>
    private void GuideButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsFlyout?.Hide();
        GuideOverlay?.Visibility = Visibility.Visible;
    }

    private void GuideScrim_Tapped(object sender, TappedRoutedEventArgs e)
    {
        GuideOverlay?.Visibility = Visibility.Collapsed;
    }

    private void GuideCloseButton_Click(object sender, RoutedEventArgs e)
    {
        GuideOverlay?.Visibility = Visibility.Collapsed;
    }

    private async void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        await OpenPdfAsync();
    }

    private async Task OpenPdfAsync()
    {
        var picker = new FileOpenPicker();

        // WinUI 3 desktop apps need to associate the picker with the window handle (HWND)
        var app = Application.Current as App;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(app?.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".pdf");

        // 弹模态文件选择器期间抑制所有按钮的 hover 更新（强制静止）：弹窗打开瞬间会有游离的
        ButtonVisualOverride.SetHoverSuppressed(true);
        try
        {
            StorageFile file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                UpdateWindowTitle(file.Name);
                try
                {
                    await LoadPdfAsync(file.Path);
                }
                catch (Exception ex)
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = GlanceRX.Services.LocalizationService.Get("Error"),
                    Content = string.Format(GlanceRX.Services.LocalizationService.Get("PdfLoadErrorContent"), ex.Message),
                    CloseButtonText = GlanceRX.Services.LocalizationService.Get("Ok"),
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = DialogTheme()
                };
                await errorDialog.ShowAsync();
                // If loading fails, show welcome screen again. Reset the toolbar
                UpdateWindowTitle(null);
                WelcomePanel.Visibility = Visibility.Visible;
                SidebarSplitView.Visibility = Visibility.Collapsed;
            }
        }
        }
        finally
        {
            ButtonVisualOverride.SetHoverSuppressed(false);
        }
    }

    private void Open_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = OpenPdfAsync();
    }

    private async void SavePdf_Click(object sender, RoutedEventArgs e)
    {
        await SaveOriginalPdfAsync();
    }

    private async void Print_Click(object? sender, RoutedEventArgs? e)
    {
        if (string.IsNullOrEmpty(_currentPdfPath) || _pages.Count == 0) return;

        var app = Application.Current as App;
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(app?.MainWindow);

        // Use a dedicated render service for printing so it never contends with
        PdfRenderService? printRender = null;
        try
        {
            printRender = new PdfRenderService();
            await printRender.InitializeAsync(_currentPdfPath, _currentPdfPassword);

            string docTitle = System.IO.Path.GetFileNameWithoutExtension(_currentPdfPath);
            int currentPage = PageListView.SelectedIndex;
            if (currentPage < 0) currentPage = 0;

            var printService = new GlanceRX.Services.PdfPrintService(hwnd, printRender, _pages, docTitle, currentPage);

            bool shown = await printService.ShowPrintDialogAsync();
            if (!shown)
            {
                await ShowPrintErrorDialog("NoPrinterTitle", "NoPrinterContent");
            }
        }
        catch (Exception ex)
        {
            await ShowPrintErrorDialog("PrintErrorTitle", "PrintErrorContent", ex.Message);
        }
        finally
        {
            if (printRender != null)
            {
                try { printRender.Dispose(); } catch { }
            }
        }
    }

    /// WinUI 3 的 ContentDialog 打开时按 Application.RequestedTheme（跟随系统）解析主题资源，
    /// microsoft-ui-xaml#6281）。因此每个对话框都必须显式携带窗口根元素解析后的 ActualTheme，
    /// 否则应用内选深色/浅色而系统主题相反时，对话框外观与主界面不一致。
    private static ElementTheme DialogTheme()
    {
        var root = (Application.Current as App)?.MainWindow?.Content as FrameworkElement;
        return root?.ActualTheme ?? ElementTheme.Default;
    }

    private async Task ShowPrintErrorDialog(string titleKey, string contentKey, string? arg = null)
    {
        var dialog = new ContentDialog
        {
            Title = GlanceRX.Services.LocalizationService.Get(titleKey),
            Content = arg == null
                ? GlanceRX.Services.LocalizationService.Get(contentKey)
                : string.Format(GlanceRX.Services.LocalizationService.Get(contentKey), arg),
            CloseButtonText = GlanceRX.Services.LocalizationService.Get("Ok"),
            XamlRoot = this.XamlRoot,
            RequestedTheme = DialogTheme()
        };
        await dialog.ShowAsync();
    }

    private void SaveCurrentViewState()
    {
        if (string.IsNullOrEmpty(_currentPdfPath)) return;
        if (GpuViewer.PageCount <= 0) return;
        GpuViewer.GetCurrentViewState(out double zoom, out double h, out double v, out int page);
        ViewStateData data = new ViewStateData
        {
            ZoomPercent = zoom,
            HorizontalOffset = h,
            VerticalOffset = v,
            PageIndex = page,
            RotationDegrees = GpuViewer.RotationDegrees,
            LayoutMode = (int)GpuViewer.LayoutMode,
            Arrangement = (int)GpuViewer.Arrangement,
            SidebarOpen = SidebarSplitView.IsPaneOpen,
            SavedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        // 窗口几何由 MainWindow 填写（只有它持有 AppWindow）：普通状态取 AppWindow.Position/Size，
        if ((Application.Current as App)?.MainWindow is MainWindow mainWindow)
            mainWindow.CaptureWindowGeometry(data, _currentPdfPath);
        ViewStateStore.Save(_currentPdfPath, data);
    }

    public void CleanupForExit()
    {
        // 退出前先保存阅读位置/缩放（此时 GpuViewer 仍存活，可安全读取视图状态）。
        SaveCurrentViewState();

        _isUnloading = true;

        if (_pdfRenderCts != null)
        {
            try { _pdfRenderCts.Cancel(); } catch { }
        }

        if (_pdfRenderRightCts != null)
        {
            try { _pdfRenderRightCts.Cancel(); } catch { }
        }

        // Release native resources without blocking the UI thread.
        if (_pdfRenderServiceRight != null)
        {
            try { _pdfRenderServiceRight.Dispose(); } catch { }
            _pdfRenderServiceRight = null;
        }
        if (_pdfRenderService != null)
        {
            try { _pdfRenderService.Dispose(); } catch { }
            _pdfRenderService = null;
        }
    }

    public async Task<bool> SaveOriginalPdfSilentAsync(bool onExit = false)
    {
        if (string.IsNullOrEmpty(_currentPdfPath) || _pages.Count == 0) return false;

        // Cancel page rendering FIRST to release the semaphore and prevent deadlocks!
        if (_pdfRenderCts != null)
        {
            try { _pdfRenderCts.Cancel(); } catch { }
        }

        await _ffiSemaphore.WaitAsync();
        try
        {
            var savedList = new List<SavedAnnotation>();
            foreach (var page in _pages)
            {
                foreach (var anno in page.Annotations)
                {
                    var savedAnno = new SavedAnnotation
                    {
                        PageIndex = page.PageIndex,
                        Type = anno.Type,
                        X = anno.X,
                        Y = anno.Y,
                        Width = anno.Width,
                        Height = anno.Height,
                        Content = anno.Content ?? "",
                        ColorHex = anno.ColorHex,
                        Thickness = anno.Thickness,
                        RotationAngle = page.RotationAngle
                    };

                    foreach (var pt in anno.PointsCollection)
                    {
                        savedAnno.Points.Add(new SavedPoint { X = pt.X, Y = pt.Y });
                    }

                    savedList.Add(savedAnno);
                }
            }

            var pageRotations = new Dictionary<int, double>();
            foreach (var page in _pages)
            {
                pageRotations[page.PageIndex] = page.RotationAngle;
            }

            string tempFile = CacheManager.GetTempFilePath("pdf");
            
            try
            {
                if (SaveButton != null) SaveButton.IsEnabled = false;

                // Cancel background rendering task to avoid accessing native engine during/after cleanup
                await CancelBackgroundRenderAsync();

                // 保存不改变阅读位置：仅替换底层引擎句柄，

                // 1. Clear resources to unlock original file BEFORE exporting (so PdfSharp can access it)
                if (_pdfRenderService != null)
                {
                    _pdfRenderService.Dispose();
                    _pdfRenderService = null;
                }

                var exportService = new GlanceRX.Services.PdfExportService();
                await exportService.ExportPdfWithAnnotationsAsync(_currentPdfPath, tempFile, savedList, pageRotations, _currentPdfPassword);

                // 3. Overwrite original file
                // 否则 File.Copy 覆盖会抛 UnauthorizedAccessException(Access denied)。
                try
                {
                    if ((System.IO.File.GetAttributes(_currentPdfPath) & System.IO.FileAttributes.ReadOnly) != 0)
                        System.IO.File.SetAttributes(_currentPdfPath, System.IO.FileAttributes.Normal);
                }
                catch { }
                File.Copy(tempFile, _currentPdfPath, true);
                try { File.Delete(tempFile); } catch { }

                // Reset the rotation angle since rotation is now permanently applied in the PDF file itself.
                foreach (var page in _pages)
                {
                    page.RotationAngle = 0.0;
                }
                HasUnsavedChanges = false;

                if (onExit)
                {
                    // Exit early without re-rendering or reloading PDF in memory to avoid shutdown deadlocks
                    return true;
                }

                _pdfRenderService = new PdfRenderService();
                await _pdfRenderService.InitializeAsync(_currentPdfPath, _currentPdfPassword);

                // GPU 阅读器仅"换引擎句柄 + 清旧位图缓存"重新挂接更新后的文件，
                GpuViewer.ReattachRenderService(_pdfRenderService);

                GpuViewer.InvalidateOverlayThrottled();

                if (_pdfRenderCts != null)
                {
                    try { _pdfRenderCts.Cancel(); } catch { }
                    _pdfRenderCts.Dispose();
                }
                _pdfRenderCts = new CancellationTokenSource();

                for (int i = 0; i < _pages.Count; i++)
                {
                    _pages[i].ThumbnailSource = null;
                    if (_realizedSidebar.Contains(i)) _ = EnsureThumbnailRenderedAsync(i);
                }

                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    string logPath = CacheManager.GetCacheFilePath("save_error.txt");
                    File.WriteAllText(logPath, $"Time: {DateTime.Now}\nError: {ex.Message}\nStack: {ex.StackTrace}\nInner: {ex.InnerException?.Message}");
                }
                catch { }

                // 保存失败时恢复渲染引擎，使查看器仍可正常使用。
                // ⚠ 关键：必须用【非破坏式】ReattachRenderService，绝不能 LoadAsync ——
                // 否则会重建查看器并跑 TryInitialFit，把视口重置回首页 + 适应宽度
                if (_pdfRenderService == null && !string.IsNullOrEmpty(_currentPdfPath))
                {
                    try
                    {
                        _pdfRenderService = new PdfRenderService();
                        await _pdfRenderService.InitializeAsync(_currentPdfPath, _currentPdfPassword);
                        GpuViewer.ReattachRenderService(_pdfRenderService);
                        GpuViewer.InvalidateOverlayThrottled();

                        if (_pdfRenderCts != null)
                        {
                            try { _pdfRenderCts.Cancel(); } catch { }
                            _pdfRenderCts.Dispose();
                        }
                        _pdfRenderCts = new CancellationTokenSource();
                    }
                    catch (Exception)
                    {
                    }
                }

                return false;
            }
        }
        finally
        {
            _ffiSemaphore.Release();
            if (SaveButton != null)
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    if (SaveButton != null) SaveButton.IsEnabled = true;
                });
            }
        }
    }

    public async Task<bool> SaveOriginalPdfAsync(bool notify = true)
    {
        // 路由依据"当前文档的原始文件是否可被覆盖"（_canOverwriteOriginal），而非是否带密码。
        if (!_canOverwriteOriginal)
        {
            return await ExportPdfAsync(notify);
        }

        bool success = await SaveOriginalPdfSilentAsync();
        if (!success)
        {
            // 直接覆盖失败（目标被 OS 限制写入 / 只读 / 锁定等，典型如 Windows 受控文件夹访问
            SuppressPenRing();
            try
            {
                var dialog = new ContentDialog
                {
                    Title = GlanceRX.Services.LocalizationService.Get("OverwriteFailedTitle"),
                    Content = GlanceRX.Services.LocalizationService.Get("OverwriteFailedContent"),
                    PrimaryButtonText = GlanceRX.Services.LocalizationService.Get("SaveCopy"),
                    CloseButtonText = GlanceRX.Services.LocalizationService.Get("Cancel"),
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = DialogTheme()
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    return await ExportPdfAsync(notify);
                }
                if (notify) ShowSaveToast(GlanceRX.Services.LocalizationService.Get("SaveToastFailed"), ToastIconKind.Error);
                return false;
            }
            finally
            {
                ResumePenRing();
            }
        }

        if (notify) ShowSaveToast(GlanceRX.Services.LocalizationService.Get("SaveToastSuccess"), ToastIconKind.Success);
        return true;
    }

    private async Task<bool> ExportPdfAsync(bool notify = true)
    {
        if (string.IsNullOrEmpty(_currentPdfPath) || _pages.Count == 0) return false;
        // 另存为流程全程（含系统 FileSavePicker 文件选择框、导出成功/失败提示）整个窗口恢复原生鼠标，
        SuppressPenRing();
        try
        {
            // 保存触发前先快照视口：FileSavePicker 会让主窗口失焦/挂起，可能使 ScrollViewer
        GpuViewer.GetCurrentViewState(out double svZoom, out double svH, out double svV, out int svPage);
        var savedView = new ViewStateData
        {
            ZoomPercent = svZoom,
            HorizontalOffset = svH,
            VerticalOffset = svV,
            PageIndex = svPage,
            RotationDegrees = 0,
            LayoutMode = (int)GpuViewer.LayoutMode,
            Arrangement = (int)GpuViewer.Arrangement,
            SavedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

            var picker = new FileSavePicker();

        var app = Application.Current as App;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(app?.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("PDF Document", new List<string> { ".pdf" });
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_currentPdfPath) + "_annotated";

        StorageFile file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return false;
        }

        var savedList = new List<SavedAnnotation>();
            foreach (var page in _pages)
            {
                foreach (var anno in page.Annotations)
                {
                    var savedAnno = new SavedAnnotation
                    {
                        PageIndex = page.PageIndex,
                        Type = anno.Type,
                        X = anno.X,
                        Y = anno.Y,
                        Width = anno.Width,
                        Height = anno.Height,
                        Content = anno.Content ?? "",
                        ColorHex = anno.ColorHex,
                        Thickness = anno.Thickness,
                        RotationAngle = page.RotationAngle
                    };

                    foreach (var pt in anno.PointsCollection)
                    {
                        savedAnno.Points.Add(new SavedPoint { X = pt.X, Y = pt.Y });
                    }

                    savedList.Add(savedAnno);
                }
            }

            var pageRotations = new Dictionary<int, double>();
            foreach (var page in _pages)
            {
                pageRotations[page.PageIndex] = page.RotationAngle;
            }

            // 2. 导出真实 /Ink 注解对象到新文件。导出前先释放 Rust 引擎对源文件(_currentPdfPath)的
            _pdfRenderService?.Dispose();
            _pdfRenderService = null;

            var exportService = new GlanceRX.Services.PdfExportService();
            await exportService.ExportPdfWithAnnotationsAsync(_currentPdfPath, file.Path, savedList, pageRotations, _currentPdfPassword);

            HasUnsavedChanges = false;

            // 让"另存为"接管为活动文档：引擎切到导出副本（页面内容一致、旋转已写进文件，
            try
            {
                foreach (var page in _pages) page.RotationAngle = 0.0;

                _pdfRenderService = new PdfRenderService();
                await _pdfRenderService.InitializeAsync(file.Path, null);
                GpuViewer.ReattachRenderService(_pdfRenderService);
                GpuViewer.InvalidateOverlayThrottled();

                _currentPdfPath = file.Path;
                _canOverwriteOriginal = IsFileOverwritable(_currentPdfPath);
                _currentPdfPassword = null;
                UpdateWindowTitle(file.Name);
            }
            catch
            {
                // 导出本身已成功；若接管新文件失败（引擎重挂/初始化异常），恢复引擎到原文件
                try
                {
                    if (_pdfRenderService == null)
                    {
                        _pdfRenderService = new PdfRenderService();
                        await _pdfRenderService.InitializeAsync(_currentPdfPath, _currentPdfPassword);
                        GpuViewer.ReattachRenderService(_pdfRenderService);
                        GpuViewer.InvalidateOverlayThrottled();
                    }
                }
                catch { }
            }

            // 把视口拉回触发另存为前的位置（防御 FileSavePicker 使窗口失焦导致的首页复位）。
            try { GpuViewer.RestoreView(savedView); } catch { }

            // 3. 成功提示：改用右下角小弹窗（自动消失），不再弹模态对话框（免去点击确定）。
            if (notify) ShowSaveToast(GlanceRX.Services.LocalizationService.Get("SaveAsToastSuccess"), ToastIconKind.Success);
            // FileSavePicker 会改变焦点态导致视口漂移，故返回后仍需补保视口（已不用模态对话框），
            try { GpuViewer.RestoreView(savedView); } catch { }
            try { GpuViewer.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => GpuViewer.RestoreView(savedView)); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            // 失败提示：改用右下角小弹窗（自动消失）。详细错误写入日志便于排查（小弹窗不展示长文本）。
            try
            {
                string logPath = CacheManager.GetCacheFilePath("save_error.txt");
                File.WriteAllText(logPath, $"Time: {DateTime.Now}\nExportPdf error: {ex.Message}\nStack: {ex.StackTrace}\nInner: {ex.InnerException?.Message}");
            }
            catch { }
            // 导出失败：本方法已释放引擎，须恢复到原文件(_currentPdfPath 尚未切换)，使查看器仍可正常使用。
            try
            {
                if (_pdfRenderService == null)
                {
                    _pdfRenderService = new PdfRenderService();
                    await _pdfRenderService.InitializeAsync(_currentPdfPath, _currentPdfPassword);
                    GpuViewer.ReattachRenderService(_pdfRenderService);
                    GpuViewer.InvalidateOverlayThrottled();
                }
            }
            catch { }
            if (notify) ShowSaveToast(GlanceRX.Services.LocalizationService.Get("SaveAsToastFailed"), ToastIconKind.Error);
            return false;
        }
        finally
        {
            ResumePenRing();
        }
    }

    private async Task CancelBackgroundRenderAsync()
    {
        var renderCts = _pdfRenderCts;
        if (renderCts != null)
        {
            try
            {
                renderCts.Cancel();
            }
            catch { }
        }
        if (renderCts == _pdfRenderCts) _pdfRenderCts = null;
        try
        {
            renderCts?.Dispose();
        }
        catch { }

        await Task.CompletedTask;
    }

    private void PageListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        var pageVm = args.Item as PdfPageViewModel;
        if (pageVm != null)
        {
            int index = pageVm.PageIndex;
            if (args.InRecycleQueue)
            {
                _realizedSidebar.Remove(index);
                // NOTE: Do NOT clear ThumbnailSource here to avoid unnecessary
            }
            else
            {
                _realizedSidebar.Add(index);
                if (pageVm.ThumbnailSource == null)
                {
                    _ = EnsureThumbnailRenderedAsync(index);
                }
            }
        }
    }

    /// 把 Rust 直出的 BGRA 像素直接写入 WriteableBitmap（零转换贴图）。
    /// 必须在 UI 线程调用（WriteableBitmap 要求）。
    private static WriteableBitmap CreateBitmapFromBgra(byte[] pixels, int width, int height)
    {
        var wb = new WriteableBitmap(width, height);
        using (var stream = wb.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }
        wb.Invalidate();
        return wb;
    }

    private async Task EnsureThumbnailRenderedAsync(int pageIndex)
    {
        if (_pages == null || pageIndex < 0 || pageIndex >= _pages.Count) return;

        var pageVm = _pages[pageIndex];
        if (pageVm.ThumbnailSource != null) return;

        if (_pdfRenderService == null)
        {
            return;
        }

        var globalToken = _pdfRenderCts?.Token ?? default;
        try
        {
            // Debounce for 30ms to prevent flooding during ultra-fast sidebar scrolling
            await Task.Delay(30, globalToken);
            if (globalToken.IsCancellationRequested || _isUnloading) return;

            if (!_realizedSidebar.Contains(pageIndex))
            {
                return;
            }

            // 在途任务已达上限（CacheLength 默认 4.0 下大跳转 realized ~120 项）→ 不排队，
            if (!QueueThumbnailIfRoom(pageIndex)) return;

            await RenderThumbnailAsync((uint)pageIndex, globalToken);
        }
        catch (TaskCanceledException) { }
        catch (OperationCanceledException) { }
    }

    private async Task RenderThumbnailAsync(uint pageIndex, CancellationToken cancellationToken = default)
    {
        if (_isUnloading || pageIndex >= _pages.Count) return;

        var renderService = _pdfRenderService;
        if (renderService == null || _isUnloading)
        {
            return;
        }

        try
        {
            if (pageIndex >= _pages.Count || _isUnloading) return;
            var pageVm = _pages[(int)pageIndex];
            
            double pageRatio = pageVm.PageWidth / pageVm.PageHeight;
            if (pageRatio <= 0) pageRatio = 0.75;
            // 缩略图显示仅 36×48 DIP（×dpiScale 后物理 ~45×60），96px 高是 1.6-2× 超采样浪费——
            uint height = 64;
            uint width = (uint)(64.0 * pageRatio);
            if (width == 0) width = 48;
            
            if (cancellationToken.IsCancellationRequested || _isUnloading) return;

            // 【缩略图并发闸门】侧边栏大距离跳转后 realized 一屏 ~20 个缩略图，若各自并发渲染，
            await _thumbnailGate.WaitAsync(cancellationToken);
            try
            {
                // 排队等闸门期间可能已滚出侧边栏视口（快速滚动/再次跳转）→ 直接跳过，不浪费渲染
                if (!_realizedSidebar.Contains((int)pageIndex)) return;
                var (pixels, aw, ah) = await renderService.RenderPageBgraAsync(pageIndex, width, height, cancellationToken);

                if (cancellationToken.IsCancellationRequested || _isUnloading || pageIndex >= _pages.Count) return;
                var rustBitmap = CreateBitmapFromBgra(pixels!, aw, ah);

                if (_isUnloading || pageIndex >= _pages.Count) return;
                if (!_realizedSidebar.Contains((int)pageIndex))
                {
                    return; // Discard if no longer visible in sidebar
                }
                _pages[(int)pageIndex].ThumbnailSource = rustBitmap;
            }
            finally
            {
                try { _thumbnailGate.Release(); } catch { }
            }
        }
        catch (TaskCanceledException) { }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            // 无论成功/跳过/取消：从待渲染集合移除，并补位一个"离侧边栏视口中心近"的 realized 缺图项
            _thumbnailQueued.Remove((int)pageIndex);
            RefillThumbnailQueue();
        }
    }

    /// <summary>把某页登记进缩略图待渲染集合。去重 + 在途上限；false = 已在队列或已满（交给补位机制）。UI 线程调用。</summary>
    private bool QueueThumbnailIfRoom(int pageIndex)
    {
        if (_thumbnailQueued.Contains(pageIndex)) return false;
        if (_thumbnailQueued.Count >= MaxThumbnailPending) return false;
        _thumbnailQueued.Add(pageIndex);
        return true;
    }

    /// <summary>缩略图补位：在途任务少于上限时，从 realized 缺图项里按"离侧边栏视口中心近"补足。 触发点：① 任务完成/跳过（RenderThumbnailAsync finally）；② 侧边栏滚动停止（ScrollViewer ViewChanged）；</summary>
    private void RefillThumbnailQueue()
    {
        if (_isUnloading || _pages == null || _pages.Count == 0) return;
        if (_thumbnailQueued.Count >= MaxThumbnailPending) return;
        int center = GetSidebarCenterIndex();
        var token = _pdfRenderCts?.Token ?? default;
        while (_thumbnailQueued.Count < MaxThumbnailPending)
        {
            int best = -1;
            double bestDist = double.MaxValue;
            foreach (int i in _realizedSidebar)
            {
                if (_thumbnailQueued.Contains(i)) continue;
                if (i < 0 || i >= _pages.Count) continue;
                if (_pages[i].ThumbnailSource != null) continue;
                double d = Math.Abs(i - center);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            if (best < 0) break;
            _thumbnailQueued.Add(best);
            _ = RenderThumbnailAsync((uint)best, token);
        }
    }

    /// <summary>侧边栏视口中心对应的页索引（供补位优先渲染）。拿不到内部 ScrollViewer 时退回主视图当前页。</summary>
    private int GetSidebarCenterIndex()
    {
        try
        {
            if (_sidebarScrollViewer != null)
            {
                double itemH = GetSidebarItemHeight();
                if (itemH > 0)
                {
                    double centerOff = _sidebarScrollViewer.VerticalOffset + _sidebarScrollViewer.ViewportHeight / 2.0;
                    return (int)(centerOff / itemH);
                }
            }
        }
        catch { }
        return _currentPageIndex;
    }

    /// <summary>实测缩略图项高（首个 realized 容器），惰性缓存；失败回退 0（调用方再退回当前页）。</summary>
    private double GetSidebarItemHeight()
    {
        if (_sidebarItemHeight > 0) return _sidebarItemHeight;
        var lv = PageListView;
        if (lv == null) return 0;
        try
        {
            int max = Math.Min(lv.Items?.Count ?? 0, 64);
            for (int i = 0; i < max; i++)
            {
                if (lv.ContainerFromIndex(i) is FrameworkElement fe && fe.ActualHeight > 0)
                {
                    _sidebarItemHeight = fe.ActualHeight;
                    break;
                }
            }
        }
        catch { }
        return _sidebarItemHeight;
    }

    /// <summary>订阅侧边栏滚动停止事件：缓存窗口内的项滚入视口【不会】重新触发 ContainerContentChanging， 其缩略图要靠这里补渲染（RefillThumbnailQueue 按新视口中心优先补位）。Loaded 后调用一次。</summary>
    private void AttachSidebarScrollRefill()
    {
        if (PageListView == null || _sidebarScrollViewer != null) return;
        try
        {
            _sidebarScrollViewer = FindDescendantScrollViewer(PageListView);
            if (_sidebarScrollViewer != null)
            {
                _sidebarScrollViewer.ViewChanged += (s, e) =>
                {
                    if (!e.IsIntermediate) RefillThumbnailQueue();
                };
            }
        }
        catch { }
    }

    /// 侧边栏跟随主视图：把 PageListView 内部 ScrollViewer 直接滚到"第 index 项"的偏移。
    /// 项高固定（模板 Border 48 + Padding 4,4 ≈ 56px，实测缓存），按 索引×项高 精确定位——
    private void ScrollSidebarToIndex(int index)
    {
        if (index < 0) return;
        if (_sidebarScrollViewer == null) AttachSidebarScrollRefill();
        var sv = _sidebarScrollViewer;
        if (sv == null) return;
        double itemH = GetSidebarItemHeight();
        if (itemH <= 0) return;
        try
        {
            double target = index * itemH - (sv.ViewportHeight - itemH) / 2.0;
            target = Math.Max(0, Math.Min(target, Math.Max(0, sv.ScrollableHeight)));
            sv.ChangeView(0, target, null, disableAnimation: true);
        }
        catch { }
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        try
        {
            int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer sv) return sv;
                var found = FindDescendantScrollViewer(child);
                if (found != null) return found;
            }
        }
        catch { }
        return null;
    }

    private async Task LoadPdfAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        // Cancel page rendering FIRST to release the semaphore and prevent deadlocks!
        if (_pdfRenderCts != null)
        {
            try { _pdfRenderCts.Cancel(); } catch { }
        }

        await _ffiSemaphore.WaitAsync();
        try
        {
            await CancelBackgroundRenderAsync();
            _isUnloading = false;

            _pages.Clear();
            _annotationHistory.Clear();
            _annotationRedo.Clear();
            _currentPageIndex = 0;
            PageNumberInput.Text = "1";
            TotalPagesText.Text = "/ 0";
            QueueToolbarOverflowUpdate();

            // 切换文档前，先保存旧 PDF 的阅读位置/缩放（此时 GpuViewer 仍显示旧文档）。
            SaveCurrentViewState();
            _currentPdfPath = path;
            _canOverwriteOriginal = true; // 默认可覆盖；引擎初始化成功后按加密/只读重写
            HasUnsavedChanges = false;
            _realizedSidebar.Clear();
            _thumbnailQueued.Clear(); // 换文档：丢弃旧文档的缩略图排队（旧任务经 _pdfRenderCts 取消后 Remove/Refill 幂等）
            _sidebarItemHeight = 0;   // 项高实测缓存随新文档重新取（模板不变其实可留，清零更稳）
            if (_pdfRenderCts != null)
            {
                try { _pdfRenderCts.Cancel(); } catch { }
                _pdfRenderCts.Dispose();
            }
            _pdfRenderCts = new CancellationTokenSource();

            WelcomePanel.Visibility = Visibility.Collapsed;
            SidebarSplitView.Visibility = Visibility.Visible;
            // 侧边栏开关状态随文件记忆（与阅读位置/缩放同一份记录，按 PDF 路径存）。
            // 必须在引擎初始化【之前】摆好：其后 InitializeAsync/GetPageCountAsync 有多个 await，
            var savedVs = ViewStateStore.Load(_currentPdfPath);
            SidebarSplitView.IsPaneOpen = savedVs?.SidebarOpen == true;
            if (ToggleSidebarButton != null) ToggleSidebarButton.IsChecked = SidebarSplitView.IsPaneOpen;
            SetToolbarItemAppVisible(SaveButton, true);
            if (PrintButton != null) SetToolbarItemAppVisible(PrintButton, true);
            if (SplitViewToggleButton != null) SetToolbarItemAppVisible(SplitViewToggleButton, true);

            // Initialize Rust PDF engine (only rendering backend), retrying when
            bool engineReady = false;
            string? pdfPassword = null;
            bool isRetry = false;
            while (!engineReady)
            {
                try
                {
                    _pdfRenderService?.Dispose();
                    _pdfRenderService = new PdfRenderService();
                    await _pdfRenderService.InitializeAsync(_currentPdfPath, pdfPassword);
                    engineReady = true;
                    _currentPdfPassword = pdfPassword;
                    // 不能覆盖原文件（加密/只读）→ 走另存为生成未加密副本；副本可写 → 可覆盖自身。
                    _canOverwriteOriginal = (_currentPdfPassword == null) && IsFileOverwritable(_currentPdfPath);
                }
                catch (PdfPasswordRequiredException)
                {
                    var entered = await PromptForPasswordAsync(Path.GetFileName(path), isRetry);
                    if (entered == null)
                    {
                        // User cancelled the password prompt — revert to welcome screen.
                        UpdateWindowTitle(null);
                        WelcomePanel.Visibility = Visibility.Visible;
                        SidebarSplitView.Visibility = Visibility.Collapsed;
                        return;
                    }
                    pdfPassword = entered;
                    isRetry = true;
                }
            }

            // ── GPU 阅读器（唯一渲染路径）────────────────────────────────────
            _totalPages = await _pdfRenderService!.GetPageCountAsync();
            TotalPagesText.Text = $"/ {_totalPages}";
            QueueToolbarOverflowUpdate();
            GpuViewer.Visibility = Visibility.Visible;
            // 还原上次关闭时的阅读位置与缩放：必须在 LoadAsync 时就传入 restoreView——
            // 否则 GpuPageView 内部 TryInitialFit 会先做"适应宽度"（ChangeView(0,0,fit)）把
            await GpuViewer.LoadAsync(_pdfRenderService, savedVs);

            // 复用 GpuViewer 已取的页尺寸填充 _pages 占位 → 缩略图侧栏
            foreach (var p in GpuViewer.PageInfos)
            {
                _pages.Add(new PdfPageViewModel
                {
                    PageIndex = p.Index,
                    PageWidth = p.WidthPoints,
                    PageHeight = p.HeightPoints,
                });
            }
            // 补一次侧边栏选中同步：LoadAsync 内还原落定的 NotifyViewState→CurrentPageChanged 发生时
            // _pages 尚未填充（ItemsSource 为空，侧边栏设置被跳过）；若还原已同步完成、后续无
            int restoredIdx = GpuViewer.CurrentPageIndex;
            if (restoredIdx >= 0 && restoredIdx < _pages.Count && PageListView.SelectedIndex != restoredIdx)
            {
                PageListView.SelectedIndex = restoredIdx;
                PageListView.ScrollIntoView(_pages[restoredIdx]);
            }

            await LoadAnnotationsAsync(_currentPdfPath);
            GpuViewer.InvalidateOverlay();

            LoadCanvasForCurrentDocument();

            await LoadBookmarksAsync();
            await LoadOutlineAsync();

            // Proactively return the transient load-time allocation spike to
            SchedulePostLoadMemoryTrim();
        }
        catch (Exception ex)
        {
                var dialog = new ContentDialog
                {
                    Title = GlanceRX.Services.LocalizationService.Get("PdfLoadErrorTitle"),
                    Content = $"{GlanceRX.Services.LocalizationService.Get("Error")}: {ex.Message}\n\n{GlanceRX.Services.LocalizationService.Get("Stack")}:\n{ex.StackTrace}",
                    CloseButtonText = GlanceRX.Services.LocalizationService.Get("Ok"),
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = DialogTheme()
                };
            _ = dialog.ShowAsync();
        }
        finally
        {
            _ffiSemaphore.Release();
        }
    }

    /// <summary>Prompt the user for a PDF password. Returns the entered password (which may be empty if they typed nothing), or null if they cancelled.</summary>
        private async Task<string?> PromptForPasswordAsync(string fileName, bool isRetry)
        {
            var stack = new StackPanel { Spacing = 12 };
            stack.Children.Add(new TextBlock
            {
                Text = isRetry
                    ? GlanceRX.Services.LocalizationService.Get("PasswordMessageRetry")
                    : string.Format(GlanceRX.Services.LocalizationService.Get("PasswordMessage"), fileName),
                TextWrapping = TextWrapping.Wrap,
            });
            var passwordBox = new PasswordBox
            {
                Width = 320,
                PlaceholderText = GlanceRX.Services.LocalizationService.Get("PasswordPlaceholder"),
            };
            passwordBox.Loaded += (s, e) => passwordBox.Focus(FocusState.Programmatic);
            stack.Children.Add(passwordBox);

            var dialog = new ContentDialog
            {
                Title = GlanceRX.Services.LocalizationService.Get(isRetry ? "PasswordTitleRetry" : "PasswordTitle"),
                Content = stack,
                PrimaryButtonText = GlanceRX.Services.LocalizationService.Get("Open"),
                CloseButtonText = GlanceRX.Services.LocalizationService.Get("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
                RequestedTheme = DialogTheme(),
            };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? passwordBox.Password : null;
    }

    private DispatcherTimer? _postLoadTrimTimer;

    /// Opening a large scanned PDF causes a transient allocation spike (page enumeration + first renders + …
    private void SchedulePostLoadMemoryTrim()
    {
        _postLoadTrimTimer?.Stop();
        _postLoadTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _postLoadTrimTimer.Tick += (s, e) =>
        {
            _postLoadTrimTimer?.Stop();
            _postLoadTrimTimer = null;
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            catch { /* best-effort; never crash on a cosmetic trim */ }
        };
        _postLoadTrimTimer.Start();
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        // 欢迎页里这个按钮仍可见，但点击必须完全无效：此时 SidebarSplitView 整体 Collapsed，
        if (SidebarSplitView == null || SidebarSplitView.Visibility != Visibility.Visible)
        {
            // 欢迎页：按钮可见但点击完全无效。该按钮已是 ToggleButton，点击会自动翻
            if (ToggleSidebarButton != null) ToggleSidebarButton.IsChecked = SidebarSplitView?.IsPaneOpen ?? false;
            return;
        }

        // 【翻转前】同步冻结"阅读位置不变量"：SplitView 的列宽在过渡起点(KeyTime=0)即变，翻转后读不到旧几何。
        GpuViewer?.CaptureHostResizeAnchor();
        SidebarSplitView.IsPaneOpen = !SidebarSplitView.IsPaneOpen;
        if (ToggleSidebarButton != null) ToggleSidebarButton.IsChecked = SidebarSplitView.IsPaneOpen;
    }

    private void SidebarMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton btn && btn.Tag is string tag)
        {
            _currentSidebarMode = tag switch
            {
                "Bookmarks" => SidebarMode.Bookmarks,
                "Outline" => SidebarMode.Outline,
                _ => SidebarMode.Thumbnails,
            };
            ApplySidebarMode();
        }
    }

    private void ApplySidebarMode()
    {
        ModeThumbnailsButton.IsChecked = _currentSidebarMode == SidebarMode.Thumbnails;
        ModeBookmarksButton.IsChecked = _currentSidebarMode == SidebarMode.Bookmarks;
        ModeOutlineButton.IsChecked = _currentSidebarMode == SidebarMode.Outline;

        PageListView.Visibility = _currentSidebarMode == SidebarMode.Thumbnails ? Visibility.Visible : Visibility.Collapsed;
        BookmarkListView.Visibility = _currentSidebarMode == SidebarMode.Bookmarks ? Visibility.Visible : Visibility.Collapsed;
        OutlineListView.Visibility = _currentSidebarMode == SidebarMode.Outline ? Visibility.Visible : Visibility.Collapsed;
        SidebarHeaderText.Text = _currentSidebarMode switch
        {
            SidebarMode.Bookmarks => GlanceRX.Services.LocalizationService.Get("SidebarBookmarks"),
            SidebarMode.Outline => GlanceRX.Services.LocalizationService.Get("SidebarOutline"),
            _ => GlanceRX.Services.LocalizationService.Get("SidebarThumbnails"),
        };
    }

    private void PageListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        var clickedItem = e.ClickedItem as PdfPageViewModel;
        if (clickedItem != null && clickedItem.PageIndex != _currentPageIndex)
        {
            NavigateToPage(clickedItem.PageIndex);
        }
    }

    /// 挂上书签行几何驱动的两个来源：指针移动（见 PageRoot_PointerMoved）与 布局变化（滚动 / 增删书签 / 编辑态切换 / 侧边栏滑动都会让行移动，
    /// ⇒ 必须有第二个来源，否则悬浮行会停在旧位置）。
    private void AttachBookmarkRowStateDrive()
    {
        if (BookmarkListView == null) return;

        this.AddHandler(PointerPressedEvent, new PointerEventHandler(BookmarkRow_PointerPressed), true);
        this.AddHandler(PointerReleasedEvent, new PointerEventHandler(BookmarkRow_PointerReleased), true);

        BookmarkListView.LayoutUpdated += (s, e) => RefreshBookmarkRowStates();
    }

    /// <summary>判定这一次按下算不算"落在书签行上"，是则该行进入点击态。 ⚠ 因为挂在页面根（不受 Handled 影响），点在行内按钮 / 自绘编辑框时也会到达这里 ⇒ 必须显式排除 —— 那正是用户定的语义：「点击落在按钮 / 编辑框 = 落在书签栏更上一层，不穿透到行」。</summary>
    private void BookmarkRow_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (BookmarkListView == null || BookmarkListView.Visibility != Visibility.Visible) return;
        var src = e.OriginalSource as DependencyObject;
        if (src == null) return;

        bool onButton = HasAncestor<Button>(src);
        bool onEditor = HasAncestor<SelfDrawnTextBox>(src);
        var row = FindAncestorListViewItem(src);
        bool inBookmarkList = row != null && IsDescendantOf(row, BookmarkListView);

        if (onButton || onEditor || row == null || !inBookmarkList) return;

        _bookmarkPressedRow = row;
        RefreshBookmarkRowStates();
    }

    private static bool HasAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T) return true;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>任何一次松开都解除按下态（挂页面根、不受 Handled 影响，故在按钮 / 编辑框上松开也算）。</summary>
    private void BookmarkRow_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_bookmarkPressedRow == null) return;
        _bookmarkPressedRow = null;
        RefreshBookmarkRowStates();
    }

    /// 书签行三档底色的唯一权威：按指针几何判定，而不是框架的 IsPointerOver。
    private void RefreshBookmarkRowStates()
    {
        if (BookmarkListView == null || BookmarkListView.Visibility != Visibility.Visible) return;
        var panel = BookmarkListView.ItemsPanelRoot;
        if (panel == null) return;

        var pt = _bookmarkRowPointer;

        // 按下后指针被拖出行外（或该行已被虚拟化回收）⇒ 解除按下态，避免点击色残留。
        if (_bookmarkPressedRow != null && !IsPointerWithinRow(_bookmarkPressedRow, pt))
            _bookmarkPressedRow = null;

        // 指针不在整块列表内 ⇒ 一律回常态，省掉逐行的 TransformToVisual（LayoutUpdated 触发很频繁）。
        bool overList = IsPointerWithinRow(BookmarkListView, pt);

        foreach (var child in panel.Children)
        {
            if (child is not ListViewItem row) continue;
            bool pressed = ReferenceEquals(row, _bookmarkPressedRow);
            bool hover = overList && !pressed && IsPointerWithinRow(row, pt);
            // 不做"状态未变就跳过"的幂等：容器被虚拟化回收复用时，Tag 会留着上一行的旧值、
            ApplyBookmarkRowVisual(row, pressed ? "Pressed" : hover ? "PointerOver" : "Normal");
        }
    }

    /// 直接切换行模板里三层底色的显隐 —— 刻意不走 VisualStateManager： ListViewItem 控件自身的状态机会以 IsPointerOver 为依据调用 GoToState("N…
    /// 我们一旦定义同名状态就被它抢（假 PointerExited 到时它把行切回 Normal，实测"改了没变化"）。
    /// ⚠ 每次都重新解析模板根，绝不缓存：模板被框架重新应用后旧节点即"幻影"（写它等于写空气）。
    private static void ApplyBookmarkRowVisual(ListViewItem row, string state)
    {
        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(row) <= 0) return;
        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(row, 0) is not Panel grid) return;

        foreach (var child in grid.Children)
        {
            if (child is not FrameworkElement fe) continue;
            fe.Visibility = fe.Name switch
            {
                "RestLayer"    => state == "Normal"      ? Visibility.Visible : Visibility.Collapsed,
                "HoverLayer"   => state == "PointerOver" ? Visibility.Visible : Visibility.Collapsed,
                "PressedLayer" => state == "Pressed"     ? Visibility.Visible : Visibility.Collapsed,
                _ => fe.Visibility,
            };
        }
    }

    /// <summary>实时指针是否落在元素的视觉矩形内（与 GetLivePointerPoint() 同为 MainPage 坐标系）。 用 TransformToVisual 取元素原点 —— 它含祖先的一切 RenderTransform，得到的是屏幕上的真实位置。</summary>
    private bool IsPointerWithinRow(FrameworkElement element, Point pt)
    {
        double w = element.ActualWidth, h = element.ActualHeight;
        if (w <= 0 || h <= 0) return false;
        try
        {
            var origin = element.TransformToVisual(this).TransformPoint(new Point(0, 0));
            return pt.X >= origin.X && pt.X < origin.X + w
                && pt.Y >= origin.Y && pt.Y < origin.Y + h;
        }
        catch { return false; }
    }

    private static ListViewItem? FindAncestorListViewItem(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is ListViewItem lvi) return lvi;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    // 书签行跳转走【容器级点击】（与 PageListView_ItemClick 同源）。
    private void BookmarkListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookmarkViewModel bm)
        {
            if (bm.PageIndex != _currentPageIndex)
                NavigateToPage(bm.PageIndex);
        }
    }

    // 侧边栏头部金色五角星按钮：静默把当前页加为书签。
    private void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_totalPages == 0 || _currentPageIndex < 0 || _currentPageIndex >= _totalPages) return;
        if (_bookmarks.Any(b => b.PageIndex == _currentPageIndex)) return;
        InsertBookmarkSorted(_currentPageIndex,
            string.Format(GlanceRX.Services.LocalizationService.Get("BookmarkPagePlaceholder"), _currentPageIndex + 1));
        SyncBookmarkFlags();
        _ = SaveBookmarksAsync();
        ShowSaveToast(GlanceRX.Services.LocalizationService.Get("BookmarkAddedToast"), ToastIconKind.Bookmark);
    }

    /// <summary>Ctrl+D：收藏当前页书签（与侧边栏顶部收藏按钮等效，同样弹收藏气泡）。 有效性守卫（无文档 / 页索引越界 / 已有书签静默早退）由 AddBookmark_Click 自带。</summary>
    private void AddBookmark_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        AddBookmark_Click(this, new RoutedEventArgs());
        args.Handled = true;
    }

    // 侧栏缩略图行内那颗星：按下标 Handled，阻止 ListViewItem 进入按下态并捕获指针。
    private void RowStar_PointerPressed(object sender, PointerRoutedEventArgs e) => e.Handled = true;

    // 松手成 Tap 时切换书签；同样标 Handled，避免 ListViewItem 把它当成 ItemClick 而跳页。
    private void RowStar_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement star && star.DataContext is PdfPageViewModel pageVm)
            TogglePageBookmark(pageVm);
    }

    // 行内星点击语义：在"加书签 / 移出书签"之间切换。
    private void TogglePageBookmark(PdfPageViewModel pageVm)
    {
        int idx = pageVm.PageIndex;
        if (idx < 0 || idx >= _pages.Count) return;

        var existing = _bookmarks.FirstOrDefault(b => b.PageIndex == idx);
        var kind = ToastIconKind.Bookmark;
        string msgKey = "BookmarkAddedToast";
        if (existing != null)
        {
            _bookmarks.Remove(existing);
            kind = ToastIconKind.Remove;
            msgKey = "BookmarkRemovedToast";
        }
        else
        {
            InsertBookmarkSorted(idx,
                string.Format(GlanceRX.Services.LocalizationService.Get("BookmarkPagePlaceholder"), idx + 1));
        }
        SyncBookmarkFlags();
        _ = SaveBookmarksAsync();
        ShowSaveToast(GlanceRX.Services.LocalizationService.Get(msgKey), kind);
    }

    // 书签成员关系的唯一真源是 _bookmarks；缩略图行内的星必须与它保持一致，
    // 否则从书签列表删除、或从顶部黄星添加后，行内星的两态会与实际书签不符。
    private void SyncBookmarkFlags()
    {
        var marked = new HashSet<int>(_bookmarks.Select(b => b.PageIndex));
        foreach (var p in _pages)
        {
            bool on = marked.Contains(p.PageIndex);
            if (p.IsBookmarked != on) p.IsBookmarked = on;
        }
    }

    // 书签列表恒按 PageIndex 升序维护：新加入的插到它自己的页码位置上，而不是追加到尾部。
    // 依据：一页最多一个书签（重复守卫在 AddBookmark_Click / TogglePageBookmark）
    //       加载路径 LoadBookmarksAsync 同样是 OrderBy(PageIndex)，两处语义必须一致，
    //       否则出现"刚打开是页码序、加了书签后尾部乱掉"。
    private void InsertBookmarkSorted(int pageIndex, string displayName)
    {
        int at = _bookmarks.Count;
        for (int i = 0; i < _bookmarks.Count; i++)
        {
            if (_bookmarks[i].PageIndex > pageIndex)
            {
                at = i;
                break;
            }
        }
        _bookmarks.Insert(at, new BookmarkViewModel { PageIndex = pageIndex, DisplayName = displayName });
    }

    private void BookmarkEdit_Click(object sender, RoutedEventArgs e)
    {
        if (GetBookmarkModel(sender) is not BookmarkViewModel bm) return;
        bm.EditText = bm.DisplayName;
        bm.IsEditing = true;
    }

    private void BookmarkConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (GetBookmarkModel(sender) is not BookmarkViewModel bm) return;
        CommitBookmark(bm);
    }

    private void CommitBookmark(BookmarkViewModel bm)
    {
        string text = bm.EditText.Trim();
        if (!string.IsNullOrEmpty(text)) bm.DisplayName = text;
        bm.IsEditing = false;
        _ = SaveBookmarksAsync();
    }

    private void BookmarkCancel_Click(object sender, RoutedEventArgs e)
    {
        if (GetBookmarkModel(sender) is not BookmarkViewModel bm) return;
        bm.IsEditing = false;
    }

    private void BookmarkDelete_Click(object sender, RoutedEventArgs e)
    {
        if (GetBookmarkModel(sender) is not BookmarkViewModel bm) return;
        _bookmarks.Remove(bm);
        SyncBookmarkFlags();
        _ = SaveBookmarksAsync();
        ShowSaveToast(GlanceRX.Services.LocalizationService.Get("BookmarkRemovedToast"), ToastIconKind.Remove);
    }

    // 自绘文本域（SelfDrawnTextBox）的事件：回车提交 / Esc 取消 / 失焦请求提交。
    private void BookmarkEdit_Submitted(object sender, RoutedEventArgs e)
    {
        if (GetBookmarkModel(sender) is BookmarkViewModel bm && bm.IsEditing) CommitBookmark(bm);
    }

    private void BookmarkEdit_Cancelled(object sender, RoutedEventArgs e)
    {
        if (GetBookmarkModel(sender) is BookmarkViewModel bm) bm.IsEditing = false;
    }

    private void BookmarkEdit_CommitRequested(object sender, RoutedEventArgs e)
    {
        // 点 Cancel/Delete 按钮会先把 IsEditing 置假（控件随之折叠、不再可见），故此处按"仍在编辑态"守卫，
        if (GetBookmarkModel(sender) is BookmarkViewModel bm && bm.IsEditing) CommitBookmark(bm);
    }

    private BookmarkViewModel? GetBookmarkModel(object sender)
    {
        if (sender is FrameworkElement fe && fe.DataContext is BookmarkViewModel bm) return bm;
        return null;
    }

    private async Task SaveBookmarksAsync()
    {
        if (string.IsNullOrEmpty(_currentPdfPath)) return;
        try
        {
            var list = _bookmarks.Select(b => new PdfBookmark { PageIndex = b.PageIndex, DisplayName = b.DisplayName }).ToList();
            await _bookmarkStore.SaveAsync(_currentPdfPath, list);
        }
        catch { /* best-effort persistence */ }
    }

    private async Task LoadBookmarksAsync()
    {
        _bookmarks.Clear();
        if (string.IsNullOrEmpty(_currentPdfPath)) return;
        try
        {
            var saved = await _bookmarkStore.LoadAsync(_currentPdfPath);
            foreach (var b in saved.OrderBy(x => x.PageIndex))
            {
                string name = string.IsNullOrWhiteSpace(b.DisplayName)
                    ? string.Format(GlanceRX.Services.LocalizationService.Get("BookmarkPagePlaceholder"), b.PageIndex + 1)
                    : b.DisplayName;
                _bookmarks.Add(new BookmarkViewModel { PageIndex = b.PageIndex, DisplayName = name });
            }
        }
        catch { /* best-effort load */ }
        SyncBookmarkFlags();
    }

    private async Task LoadOutlineAsync()
    {
        _outlineItems.Clear();
        if (string.IsNullOrEmpty(_currentPdfPath)) return;
        try
        {
            var nodes = await PdfOutlineLoader.LoadAsync(_currentPdfPath, _currentPdfPassword);
            if (nodes.Count == 0)
            {
                _outlineItems.Add(new OutlineItemViewModel
                {
                    Title = GlanceRX.Services.LocalizationService.Get("NoOutline"),
                    PageIndex = -1,
                    Indent = 0,
                });
                return;
            }
            FlattenOutline(nodes, 0);
        }
        catch { /* best-effort outline */ }
    }

    private void FlattenOutline(List<OutlineNode> nodes, int depth)
    {
        foreach (var n in nodes)
        {
            _outlineItems.Add(new OutlineItemViewModel
            {
                Title = string.IsNullOrWhiteSpace(n.Title) ? "(untitled)" : n.Title,
                PageIndex = n.PageIndex,
                Indent = depth * 16.0,
            });
            if (n.Children != null && n.Children.Count > 0)
            {
                FlattenOutline(n.Children, depth + 1);
            }
        }
    }

    private void OutlineListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OutlineItemViewModel item && item.PageIndex >= 0 && item.PageIndex < _totalPages)
        {
            NavigateToPage(item.PageIndex);
        }
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        DefocusPageNumberInput();
        if (_totalPages > 0 && _currentPageIndex > 0)
        {
            NavigateToPage(_currentPageIndex - 1, smooth: true);
        }
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        DefocusPageNumberInput();
        if (_totalPages > 0 && _currentPageIndex < _totalPages - 1)
        {
            NavigateToPage(_currentPageIndex + 1, smooth: true);
        }
    }
 
    private void NavigateToPage(int index, bool smooth = false)
    {
        if (index >= 0 && _totalPages > 0 && index < _totalPages)
        {
            _currentPageIndex = index;

            PageNumberInput.Text = (index + 1).ToString();
            PageListView.SelectedIndex = index;

            // 上一页/下一页按钮：逐页弹簧动画（与 PageUp/PageDown 同源），不加遮罩。
            if (smooth)
                GpuViewer.GoToPageSmooth(index);
            else
                GpuViewer.GoToPage(index);
        }
    }

    private void PageNumberInput_Submitted(object sender, RoutedEventArgs e)
    {
        JumpToInputPage();
        DefocusPageNumberInput();   // 不可用 this.Focus(Programmatic)：对 Page 静默失败，焦点不走
    }

    private void PageNumberInput_Cancelled(object sender, RoutedEventArgs e)
    {
        PageNumberInput.Text = (_currentPageIndex + 1).ToString();
        DefocusPageNumberInput();
    }

    private void PageNumberInput_EditorGotFocus(object sender, EventArgs e)
    {
        // 焦点进入：记录此刻显示的页码（用于「点过但未输入」判定），并标记输入框持有焦点，
        _pageInputHasFocus = true;
        _pageInputValueAtFocus = PageNumberInput.Text;
        CancelToolbarHide();
    }

    private void PageNumberInput_EditorLostFocus(object sender, EventArgs e)
    {
        _pageInputHasFocus = false;

        // 仅当用户实际改动了页码才跳转；点过但未输入 / 输入未变 / 输入非法都视为「未提交」，
        string entered = PageNumberInput.Text.Trim();
        if (entered == _pageInputValueAtFocus.Trim() || !int.TryParse(entered, out _))
        {
            PageNumberInput.Text = (_currentPageIndex + 1).ToString();
            RequestHideTopToolbar();
            return;
        }
        JumpToInputPage();
    }

    private void JumpToInputPage()
    {
        if (int.TryParse(PageNumberInput.Text, out int pageNum))
        {
            int target = pageNum - 1;
            NavigateToPage(target);
            // 提交后把焦点基准同步到新页，避免失焦回环里重复跳转同一页。
            _pageInputValueAtFocus = PageNumberInput.Text;
            RequestHideTopToolbar();
        }
        else
        {
            PageNumberInput.Text = (_currentPageIndex + 1).ToString();
        }
    }

    /// 剥夺页码输入框的焦点。
    /// 否则焦点残留会导致：① 切窗口时视口被带回页面顶部；② 滚动/缩放时光标被顶到最左并覆盖输入；
    private bool _defocusingPageInput; // 重入守卫：剥夺焦点会同步触发 EditorLostFocus→JumpToInputPage→GoToPage→ViewportChanged→本方法，须阻断该回环

    // 页码输入框焦点状态：用于区分「点过但未输入」与「实际改了页码」，并驱动工具栏收起逻辑。
    private string _pageInputValueAtFocus = "1";
    private bool _pageInputHasFocus;
    private void DefocusPageNumberInput()
    {
        if (_defocusingPageInput) return; // 已在「剥夺焦点」链中（EditorLostFocus 回环），直接返回，避免重入
        if (!PageNumberInput.HasEditorFocus) return;
        _defocusingPageInput = true;
        try
        {
            PageNumberInput.IsEnabled = false;
            PageNumberInput.IsEnabled = true;
        }
        finally
        {
            _defocusingPageInput = false;
        }
        // 焦点离开后，让输入框回显真实当前页，避免停留在用户键入但未提交的残留文本
        PageNumberInput.Text = (_currentPageIndex + 1).ToString();
    }

    /// <summary>点击 PDF 画面（左/右视图）→ 立即剥夺页码框焦点，使用户后续滚动/缩放不再受其残留焦点影响。</summary>
    private void GpuViewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        DefocusPageNumberInput();
    }

    private void SubscribeToWindowStateChanges()
    {
        try
        {
            var mainWindow = (Application.Current as App)?.MainWindow;
            if (mainWindow == null) return;

            try { _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow); } catch { }

            // 画笔模式圆环作用域由 SetSystemCursor + 窗口内/前台判定实现（见 UpdateRingScope），

            // 兜底恢复原生光标：窗口关闭/进程终止/未处理异常/卸载任一路径触发即可（重复恢复无害）。
            mainWindow.Closed += (s, e) => RestoreNativeCursor();
            try
            {
                if (Application.Current != null)
                    Application.Current.UnhandledException += (s, e) => RestoreNativeCursor();
            }
            catch { }
            try
            {
                System.AppDomain.CurrentDomain.ProcessExit += (s, e) => RestoreNativeCursor();
            }
            catch { }

            this.Unloaded += (s, e) => RestoreNativeCursor();
        }
        catch { }
    }

    // ── 保存/另存为结果小弹窗（左下角，自动消失）──────────────────────────────
    // 保存/另存为成功或失败时弹出一枚角落小弹窗，2s 自动消失、点击即消失。
    private enum ToastIconKind
    {
        Success,
        Error,
        Bookmark,  // StarFilled \uE735，#F2C200（与收藏按钮同符号同色）
        Remove,    // Delete \uE74D，中性灰（与书签模式垃圾桶按钮同符号；不用红，红色已被 Error 占用）
    }

    private void ShowSaveToast(string message, ToastIconKind kind)
    {
        var capsule = BuildToastCapsule(message, kind);
        var tt = new TranslateTransform { Y = SaveToastSlot }; // 入场前先落在标准位置下方一格
        capsule.RenderTransform = tt;
        capsule.Opacity = 0;
        SaveToastStack.Children.Add(capsule);

        var item = new SaveToastItem { Capsule = capsule, Transform = tt, Pos = 0 };
        capsule.Tapped += (s, e) => DismissToast(item, animate: true);

        foreach (var existing in _saveToasts)
        {
            SlideToastTo(existing, existing.Pos + 1);
        }
        _saveToasts.Add(item);

        // 新胶囊入场：淡入(50ms) + 从下方一格滑入标准位置(160ms)
        var enter = new Storyboard();
        var op = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(50) };
        Storyboard.SetTarget(op, capsule);
        Storyboard.SetTargetProperty(op, "Opacity");
        var tr = new DoubleAnimation
        {
            From = SaveToastSlot,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(SaveToastSlideMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(tr, tt);
        Storyboard.SetTargetProperty(tr, "Y");
        enter.Children.Add(op);
        enter.Children.Add(tr);
        enter.Begin();

        // 独立计时：2s 后自动淡出（自创建起算 → 最旧先到期）
        item.Timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        var captured = item;
        item.Timer.Tick += (s, e) => DismissToast(captured, animate: true);
        item.Timer.Start();

        if (_saveToasts.Count > SaveToastMax)
        {
            DismissToast(_saveToasts[0], animate: true);
        }
    }

    /// <summary>构造一枚与旧单枚胶囊视觉完全一致的提示胶囊（含图标档位 + 主题色）。 背景/边框必须用 XamlReader 加载，使 {ThemeResource} 按元素实际主题解析： 若用 Application.Current.Resources[key] 只会解析到 App.</summary>
    private Border BuildToastCapsule(string message, ToastIconKind kind)
    {
        bool dark = this.ActualTheme == ElementTheme.Dark;

        const string xaml = @"
<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        x:Name='CapsuleBorder'
        Background='{ThemeResource FlyoutPresenterBackground}'
        BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}'
        BorderThickness='1'
        CornerRadius='20' Padding='0,4,0,4' Height='42'
        VerticalAlignment='Bottom' HorizontalAlignment='Left'>
    <Grid x:Name='CapsuleInner' ColumnDefinitions='40,*' Height='32'>
        <FontIcon x:Name='CapsuleIcon' FontFamily='Segoe Fluent Icons,Segoe MDL2 Assets' FontSize='18'
                  HorizontalAlignment='Center' VerticalAlignment='Center' Margin='0,-1,0,-1'/>
        <TextBlock x:Name='CapsuleText' FontSize='16' Grid.Column='1'
                   HorizontalAlignment='Left' VerticalAlignment='Center' Margin='2,-1,28,-1'/>
    </Grid>
</Border>";

        var capsule = (Border)XamlReader.Load(xaml);
        // 不依赖 FindName（XamlReader 的 namescope 在 WinUI3 下不可靠），直接按树遍历取子元素。
        var grid = (Grid)capsule.Child!;
        var icon = (FontIcon)grid.Children[0]!;
        var text = (TextBlock)grid.Children[1]!;

        icon.Glyph = kind switch
        {
            ToastIconKind.Error => "\uE783",
            ToastIconKind.Bookmark => "\uE735",
            ToastIconKind.Remove => "\uE74D",
            _ => "\uE73E",
        };
        icon.Foreground = new SolidColorBrush(kind switch
        {
            ToastIconKind.Error => dark
                ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B)
                : Windows.UI.Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C),
            ToastIconKind.Bookmark => Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xC2, 0x00),
            ToastIconKind.Remove => dark
                ? Windows.UI.Color.FromArgb(0xFF, 0xB8, 0xB8, 0xB8)
                : Windows.UI.Color.FromArgb(0xFF, 0x76, 0x76, 0x76),
            _ => dark
                ? Windows.UI.Color.FromArgb(0xFF, 0x6B, 0xB7, 0x00)
                : Windows.UI.Color.FromArgb(0xFF, 0x10, 0x7C, 0x10),
        });

        text.Text = message;

        ElevationShadow.SetIsEnabled(capsule, true);
        ElevationShadow.SetElevation(capsule, 18);
        return capsule;
    }

    /// <summary>将胶囊平滑移动到目标格位（Pos），重定位前先停掉进行中的同属性位移动画。</summary>
    private void SlideToastTo(SaveToastItem item, int newPos)
    {
        item.Pos = newPos;
        var to = -newPos * SaveToastSlot;
        item.SlideSb?.Stop();
        var sb = new Storyboard();
        var da = new DoubleAnimation
        {
            From = item.Transform.Y,
            To = to,
            Duration = TimeSpan.FromMilliseconds(SaveToastSlideMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(da, item.Transform);
        Storyboard.SetTargetProperty(da, "Y");
        sb.Children.Add(da);
        item.SlideSb = sb;
        sb.Begin();
    }

    private void DismissToast(SaveToastItem item, bool animate)
    {
        item.Timer.Stop();
        if (!_saveToasts.Remove(item)) return;

        foreach (var other in _saveToasts)
        {
            if (other.Pos > item.Pos) SlideToastTo(other, other.Pos - 1);
        }

        if (animate)
        {
            var fade = new Storyboard();
            var op = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(150) };
            Storyboard.SetTarget(op, item.Capsule);
            Storyboard.SetTargetProperty(op, "Opacity");
            fade.Children.Add(op);
            fade.Completed += (s, e) => { SaveToastStack.Children.Remove(item.Capsule); };
            fade.Begin();
        }
        else
        {
            SaveToastStack.Children.Remove(item.Capsule);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    // 画笔模式圆环光标：窗口作用域，无需任何全局光标替换。
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // 画笔模式圆环光标：用真实 HCURSOR 构造一个透明背景、只画一圈圆环的光标。
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, [In] byte[] lpBits);

    [System.Runtime.InteropServices.DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    // 作用域限定用的核心 API：把"系统箭头(OCR_NORMAL)"替换为真实 HCURSOR 圆环；
    // 注意 SetSystemCursor 内部会 DestroyCursor(hCursor)，故必须传入 CopyIcon 复制出的副本。
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CopyIcon(IntPtr hIcon);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>Set zoom percentage and update the display (zoom buttons / value display call this).</summary>
    private void SetZoomPercent(double zoomPercent)
    {
        zoomPercent = Math.Clamp(zoomPercent, MinZoomPercent, MaxZoomPercent);
        _currentZoomPercent = zoomPercent;
        if (ZoomValueText != null)
        {
            ZoomValueText.Text = $"{zoomPercent:F0}%";
        }
    }

    /// <summary>应用缩放百分比。缩放交给 GpuViewer（ZoomPercentChanged 回调会同步百分比显示）。</summary>
    private void ApplyZoomPercent(double zoomPercent, bool animate = true)
    {
        GpuViewer.SetZoomPercent(zoomPercent, animate);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        // 快速缩放菜单仍展开时，本点击只用于收起菜单，不触发缩放；
        if (ZoomPresetFlyout != null && ZoomPresetFlyout.IsOpen)
        {
            ZoomPresetFlyout.Hide();
            return;
        }
        // 用实时缩放(Viewer.ZoomFactor)作基准，避免动画期间 _currentZoomPercent 滞后导致连点只缩放一次。
        ZoomStepOut();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        if (ZoomPresetFlyout != null && ZoomPresetFlyout.IsOpen)
        {
            ZoomPresetFlyout.Hide();
            return;
        }
        ZoomStepIn();
    }

    /// <summary>缩小一步（与缩放胶囊 - 按钮等效）：以实时缩放(Viewer.ZoomPercent)为基准步进， 经 SetZoomPercent 内部 Math.Clamp 自动夹到 [MinZoomPercent, MaxZoomPercent]。</summary>
    private void ZoomStepOut()
    {
        if (GpuViewer == null || _totalPages <= 0) return;
        GpuViewer.ZoomStepPercent(-ZoomButtonStep);
    }

    private void ZoomStepIn()
    {
        if (GpuViewer == null || _totalPages <= 0) return;
        GpuViewer.ZoomStepPercent(ZoomButtonStep);
    }

    private void ZoomIn_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ZoomStepIn();
        args.Handled = true;
    }

    private void ZoomOut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ZoomStepOut();
        args.Handled = true;
    }

    private void ZoomReset_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (GpuViewer != null && _totalPages > 0)
            ApplyZoomPercent(100);
        args.Handled = true;
    }

    private void ZoomPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int zoomPercent))
        {
            ZoomPresetFlyout.Hide();
            ApplyZoomPercent(zoomPercent);
        }
    }

    private void FlyoutPresenterRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border root && root.Resources["OpenAnimation"] is Storyboard sb)
        {
            sb.Begin();
        }
    }

    private void FitWidth_Click(object sender, RoutedEventArgs e)
    {
        if (ZoomPresetFlyout != null && ZoomPresetFlyout.IsOpen)
        {
            ZoomPresetFlyout.Hide();
            return;
        }
        GpuViewer.FitWidth();
    }

    // F：适应宽度（纵向布局） / 适应高度（横向布局）。与 FitWidthButton 行为完全一致。
    private void Fit_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or PasswordBox or ButtonBase or ComboBox or ListViewBase or HyperlinkButton)
            return;

        if (GpuViewer == null || _totalPages <= 0) return;
        GpuViewer.FitWidth();
        args.Handled = true;
    }

    // G 键：切换画笔批注显示/隐藏。默认恒显示，隐藏仅本次会话临时生效（不持久化，
    private void ToggleAnnotations_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or PasswordBox or ButtonBase or ComboBox or ListViewBase or HyperlinkButton)
            return;

        if (GpuViewer == null || _totalPages <= 0) return;
        GpuViewer.AnnotationsVisible = !GpuViewer.AnnotationsVisible;
        args.Handled = true;
    }

    // ========================================================================

    /// <summary>隐藏 ScrollBar 模板中三角按钮的视觉内容（箭头图标），但保持布局空间。 这样 Thumb 起始位置与两侧滚动条一致，但三角块不可见。</summary>
    private void HideScrollBarArrowButtons(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            
            if (child is FrameworkElement fe)
            {
                if (fe.Name == "VerticalSmallDecrease" || fe.Name == "VerticalSmallIncrease"
                    || fe.Name == "HorizontalSmallDecrease" || fe.Name == "HorizontalSmallIncrease")
                {
                    HideButtonVisualContent(fe);
                }
            }
            HideScrollBarArrowButtons(child);
        }
    }

    /// <summary>隐藏按钮模板内部的视觉元素（Grid/Path/TextBlock 等），保持按钮本身的布局空间。</summary>
    private void HideButtonVisualContent(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe)
            {
                fe.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>同步滚动用的统一访问接口：把"左/右活动视图"抽象成 偏移/可滚动高度/视口高度 + 设置器。 GPU 模式下活动视图是 GpuPageView，旧模式下是 ScrollViewer；两者都通过这里统一驱动。</summary>
    private sealed class SyncScrollTarget
    {
        public double Offset;
        public double Scrollable;
        public double Viewport;
        public Action<double, bool> SetOffset = (_, _) => { };
        public bool HasTarget;
    }

    private SyncScrollTarget GetLeftSyncTarget()
    {
        if (GpuViewer != null && GpuViewer.Visibility == Visibility.Visible)
        {
            // 按当前布局主轴取偏移/范围：横向布局→水平轴，纵向→垂直轴。
            if (GpuViewer.IsHorizontalLayout)
                return new SyncScrollTarget { HasTarget = true, Offset = GpuViewer.HorizontalOffset, Scrollable = GpuViewer.ScrollableWidth, Viewport = GpuViewer.ViewportWidth, SetOffset = (o, a) => GpuViewer.SetHorizontalOffset(o, a) };
            return new SyncScrollTarget { HasTarget = true, Offset = GpuViewer.VerticalOffset, Scrollable = GpuViewer.ScrollableHeight, Viewport = GpuViewer.ViewportHeight, SetOffset = (o, a) => GpuViewer.SetVerticalOffset(o, a) };
        }
        return new SyncScrollTarget();
    }

    private SyncScrollTarget GetRightSyncTarget()
    {
        if (GpuViewerRight != null && GpuViewerRight.Visibility == Visibility.Visible)
        {
            if (GpuViewerRight.IsHorizontalLayout)
                return new SyncScrollTarget { HasTarget = true, Offset = GpuViewerRight.HorizontalOffset, Scrollable = GpuViewerRight.ScrollableWidth, Viewport = GpuViewerRight.ViewportWidth, SetOffset = (o, a) => GpuViewerRight.SetHorizontalOffset(o, a) };
            return new SyncScrollTarget { HasTarget = true, Offset = GpuViewerRight.VerticalOffset, Scrollable = GpuViewerRight.ScrollableHeight, Viewport = GpuViewerRight.ViewportHeight, SetOffset = (o, a) => GpuViewerRight.SetVerticalOffset(o, a) };
        }
        return new SyncScrollTarget();
    }

    private void SyncScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        // 滑块拖拽期抑制渲染（对齐 Sumatra 快拖静止行为，机制见 GpuPageView.BeginScrollBarDrag）。
        if (e.ScrollEventType == Microsoft.UI.Xaml.Controls.Primitives.ScrollEventType.ThumbTrack)
        {
            GpuViewer.BeginScrollBarDrag();
            GpuViewerRight.BeginScrollBarDrag();
        }
        else if (e.ScrollEventType == Microsoft.UI.Xaml.Controls.Primitives.ScrollEventType.EndScroll)
        {
            GpuViewer.EndScrollBarDrag();
            GpuViewerRight.EndScrollBarDrag();
        }

        if (_isSyncScrolling) return;
        double delta = e.NewValue - _lastSyncValue;
        _lastSyncValue = e.NewValue;
        if (Math.Abs(delta) < 0.5) return;

        var left = GetLeftSyncTarget();
        var right = GetRightSyncTarget();
        if (!left.HasTarget || !right.HasTarget) return;

        _isSyncScrolling = true;
        try
        {
            double leftTarget = Math.Clamp(left.Offset + delta, 0, left.Scrollable);
            double rightTarget = Math.Clamp(right.Offset + delta, 0, right.Scrollable);
            left.SetOffset(leftTarget, true);
            right.SetOffset(rightTarget, true);
        }
        finally
        {
            this.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => _isSyncScrolling = false);
        }
    }

    /// 更新同步滚动条的范围和当前位置。
    private void UpdateSyncScrollBar()
    {
        if (SyncScrollBar == null) return;

        if (SecondaryViewerContainer == null || SecondaryViewerContainer.Visibility != Visibility.Visible
            || _pdfRenderServiceRight == null || _pagesRight == null || _pagesRight.Count == 0)
        {
            if (SyncScrollBar.Visibility == Visibility.Visible)
            {
                SyncScrollBar.Visibility = Visibility.Collapsed;
                SyncScrollColumn.Width = new GridLength(0);
            }
            return;
        }

        if (SyncScrollBar.Visibility != Visibility.Visible)
        {
            SyncScrollBar.Visibility = Visibility.Visible;
            SyncScrollColumn.Width = new GridLength(16);
            SyncScrollBar.UpdateLayout();
            HideScrollBarArrowButtons(SyncScrollBar);
        }

        if (_isSyncScrolling) return;

        var left = GetLeftSyncTarget();
        var right = GetRightSyncTarget();
        if (!left.HasTarget || !right.HasTarget) return;

        double minScrollable = Math.Min(left.Scrollable, right.Scrollable);
        double minViewport = Math.Min(left.Viewport, right.Viewport);

        SyncScrollBar.Maximum = Math.Max(0, minScrollable);
        SyncScrollBar.ViewportSize = Math.Max(1, minViewport);

        double syncValue = Math.Min(left.Offset, right.Offset);
        syncValue = Math.Clamp(syncValue, 0, SyncScrollBar.Maximum);

        if (Math.Abs(SyncScrollBar.Value - syncValue) > 0.5)
        {
            SyncScrollBar.Value = syncValue;
        }
        _lastSyncValue = syncValue;
    }

    private void ReadMode_Click(object sender, RoutedEventArgs e)
    {
        SetEditMode(EditMode.Navigate);
    }

    private void PenMode_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMode == EditMode.Pen)
        {
            // Already in pen mode: if the capsule is open, collapse it (stay in pen mode);
            if (PenOptionsPopup != null && PenOptionsPopup.IsOpen)
            {
                PenOptionsPopup.IsOpen = false;
            }
            else
            {
                PositionPenOptionsPopup();
                if (PenOptionsPopup != null)
                {
                    PenOptionsPopup.IsOpen = true;
                    PenOptionsCapsule?.UpdateLayout();
                }
            }
            // The ToggleButton auto-toggles IsChecked on click; force it back to reflect
            PenModeButton.IsChecked = true;
            ReadModeButton.IsChecked = false;
        }
        else
        {
            SetEditMode(EditMode.Pen);
        }
    }

    /// <summary>白板模式开关（与画笔模式平级）。已在白板模式时连点保持不退出（与画笔按钮一致）， 退出白板模式由"阅读模式"按钮负责；从其它模式点击则切入白板模式。</summary>
    private void CanvasMode_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMode == EditMode.Canvas)
        {
            CanvasModeButton.IsChecked = true;
            ReadModeButton.IsChecked = false;
            return;
        }
        SetEditMode(EditMode.Canvas);
    }

    /// <summary>Dismisses the pen-options capsule when the user presses anywhere outside it (e.g.</summary>
    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 在任意打开的浮层(颜色胶囊/缩放菜单/气泡)内部按下时，不应关闭胶囊或收起工具栏——
        // 否则点击浮层内的按钮会被 Root_PointerPressed(handledEventsToo) 误判为"外部按下"而把菜单整个关掉。
        if (FindTopmostPopupHit(GetLivePointerPoint()) != null)
            return;

        if (PenOptionsPopup == null || !PenOptionsPopup.IsOpen)
            return;

        // Ignore presses that originate inside the capsule, on the pen button, or anywhere
        if (e.OriginalSource is DependencyObject src)
        {
            if (src == PenModeButton || IsDescendantOf(src, PenModeButton) ||
                src == PenOptionsCapsule || IsDescendantOf(src, PenOptionsCapsule) ||
                src == TopToolbar || IsDescendantOf(src, TopToolbar))
                return;
        }

        // Quietly close the capsule; let the pointer event continue to its target
        PenOptionsPopup.IsOpen = false;
        HideTopToolbar();
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        if (node == null || ancestor == null)
            return false;
        var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        while (parent != null)
        {
            if (parent == ancestor)
                return true;
            parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
        }
        return false;
    }

    /// <summary>取实时光标位置(MainPage 坐标系 DIP)。指针在浮层(独立视觉树)上时 Root_PointerMoved 收不到、 _lastPointerPoint 会过期，故直接用 GetCursorPos 取屏幕坐标换算，覆盖浮层场景。</summary>
    private Point GetLivePointerPoint()
    {
        try
        {
            if (!GetCursorPos(out POINT p)) return _lastPointerPoint;
            double scale = XamlRoot?.RasterizationScale ?? 1.0;
            double rootDipX = p.X / scale;
            double rootDipY = p.Y / scale;
            var t = this.TransformToVisual(null);
            Point origin = t.TransformPoint(new Point(0, 0));
            return new Point(rootDipX - origin.X, rootDipY - origin.Y);
        }
        catch { return _lastPointerPoint; }
    }

    /// 对当前所有打开的浮层(Tooltip / Flyout / 胶囊 Popup)做命中测试，返回光标下的【最顶】命中元素。
    /// 浮层位于独立视觉树，主树的 FindElementsInHostCoordinates(pt, this) 搜不到，必须对每个 Popup.Child
    private DependencyObject? FindTopmostPopupHit(Point pt)
    {
        try
        {
            if (XamlRoot == null) return null;
            foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
            {
                if (popup.Child is not Microsoft.UI.Xaml.UIElement child) continue;

                // 原点用 Popup 的 Horizontal/VerticalOffset：相对 Popup 的【直接父级=页面根 Grid】，
                Point local = new Point(pt.X - popup.HorizontalOffset, pt.Y - popup.VerticalOffset);

                foreach (var h in Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(local, child))
                    return h as DependencyObject;

                // 2) 包围盒兜底：浮层内有无背景刷子的容器（胶囊间隙/未生成模板区域）不参与命中测试，
                if (child is FrameworkElement fe)
                {
                    if (fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
                    {
                        try { fe.UpdateLayout(); } catch { }
                    }
                    double w = fe.ActualWidth > 0 ? fe.ActualWidth : fe.DesiredSize.Width;
                    double h2 = fe.ActualHeight > 0 ? fe.ActualHeight : fe.DesiredSize.Height;
                    if (w > 0 && h2 > 0 && local.X >= 0 && local.Y >= 0 && local.X <= w && local.Y <= h2)
                    {
                        // 排除"近全窗"浮层（light-dismiss 遮罩 / 对话框背板），否则整块画布会被误判成
                        double vw = XamlRoot.Size.Width, vh = XamlRoot.Size.Height;
                        if (vw <= 0 || vh <= 0 || (w * h2) <= vw * vh * 0.6)
                            return child;
                    }
                }
            }
        }
        catch { /* 光标纯装饰 */ }
        return null;
    }

    /// <summary>欢迎页 ↔ 文档视图 的工具栏按钮可见性。欢迎页没有文档，「画笔模式 / 旋转 / 纵横排列」 三个按钮对它毫无意义（点了只会进入没有内容可操作的状态），故一并隐藏，打开文档后恢复。 由 WelcomePanel 的 Visibility 变更回调统一驱动，所有切换路径行为一致。</summary>
    private void ApplyWelcomeChrome(bool welcomeVisible)
    {
        // 走「应用可见性」通道（而非直接写 Visibility），由收纳系统统一决定实际显隐，避免两者打架。
        bool show = !welcomeVisible;
        if (PenModeButton != null) SetToolbarItemAppVisible(PenModeButton, show);
        if (CanvasModeButton != null) SetToolbarItemAppVisible(CanvasModeButton, show);
        if (RotateButton != null) SetToolbarItemAppVisible(RotateButton, show);
        if (LayoutToggleButton != null) SetToolbarItemAppVisible(LayoutToggleButton, show);

        // 回到欢迎页时若仍停留在画笔/白板模式，按钮已隐藏、用户无法退出 → 主动切回阅读模式
        if (welcomeVisible && _currentMode != EditMode.Navigate)
            SetEditMode(EditMode.Navigate);
    }

    /// <summary>Shows the open document's name in the window title bar (e.g. "Glance-RX — file.pdf").</summary>
    private void UpdateWindowTitle(string? fileName)
    {
        string title = string.IsNullOrEmpty(fileName) ? "Glance-RX" : $"Glance-RX — {fileName}";
        if (Application.Current is App app && app.MainWindow is MainWindow glanceWindow)
            glanceWindow.SetWindowTitle(title);
    }

    // ---------------- 画笔模式圆环光标（窗口作用域，SetSystemCursor + 作用域判定） ----------------
    // 用真实 HCURSOR 圆环替换"系统箭头"本身（SetSystemCursor），框架每帧 SetCursor(箭头) 实际画出圆环，
    // 故主视图/边缘生效、零滞后（WM_SETCURSOR 子类化拦不到 Island 主视图）。
    // 窗口外、标题栏、已展开工具栏、已展开画笔颜色胶囊均为原生光标，退出/崩溃兜底也恢复，不会把 SetSystemCursor 泄漏到全局（「卡死圆环」）。

    private void RefreshRingCursor()
    {
        bool wasInstalled = _ringInstalled;
        if (wasInstalled) RestoreSystemArrow();   // 先卸下旧圆环，避免系统持有即将销毁的位图
        DestroyRingCursor();
        EnsureRingCursor();
        if (wasInstalled) InstallRingAsSystemCursor();
    }

    private void EnterPenRingMode()
    {
        EnsureRingCursor();
        if (_ringScopeTimer == null)
        {
            _ringScopeTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            _ringScopeTimer.Tick += RingScopeTimer_Tick;
        }
        try { _ringScopeTimer.Stop(); _ringScopeTimer.Start(); } catch { }
        UpdateRingScope();
    }

    private void ExitPenRingMode()
    {
        try { _ringScopeTimer?.Stop(); } catch { }
        RestoreSystemArrow();
        _ringInstalled = false;
        DestroyRingCursor();
    }

    // 任意兜底恢复：强制原生箭头并释放资源（窗口关闭/进程终止/未处理异常/卸载均可触发，重复无害）。
    private void RestoreNativeCursor()
    {
        try { _ringScopeTimer?.Stop(); } catch { }
        RestoreSystemArrow();
        _ringInstalled = false;
        DestroyRingCursor();
    }

    // 把"系统箭头(OCR_NORMAL=IDC_ARROW)"替换为我们的圆环 HCURSOR。
    // SetSystemCursor 会 DestroyCursor 传入的句柄，故必须传 CopyIcon 复制出的副本，原件保留复用。
    private void InstallRingAsSystemCursor()
    {
        if (_hRingCursor == IntPtr.Zero) return;
        try
        {
            IntPtr copy = CopyIcon(_hRingCursor);
            if (copy != IntPtr.Zero && SetSystemCursor(copy, (uint)IDC_ARROW))
                _ringInstalled = true;
            else if (copy != IntPtr.Zero)
                DestroyIcon(copy);
        }
        catch { }
    }

    private void RestoreSystemArrow()
    {
        try { SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0); } catch { }
        try { SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW)); } catch { }
    }

    // 作用域判定：指针在本窗口矩形内、本窗口为前台、且不在标题栏区域 → 装圆环；
    // 否则（离开窗口、Alt-Tab 切走、或位于标题栏）→ 恢复原生箭头。
    private void UpdateRingScope()
    {
        if (!_penRingActive) return;
        // 临时抑制（如关闭窗口的"保存更改"对话框、Ctrl+S 另存为对话框期间）：整个窗口恢复原生鼠标，且禁止重装圆环。
        if (_ringSuppressCount > 0)
        {
            if (_ringInstalled)
            {
                RestoreSystemArrow();
                _ringInstalled = false;
            }
            return;
        }
        bool shouldRing = IsPointerInRingZone();
        if (shouldRing && !_ringInstalled)
            InstallRingAsSystemCursor();
        else if (!shouldRing && _ringInstalled)
        {
            RestoreSystemArrow();
            _ringInstalled = false;
        }
    }

    // 临时全面抑制圆环光标（整个窗口恢复原生鼠标）。用于前景模态对话框（如关闭窗口的"保存更改"询问框、Ctrl+S 另存为对话框）期间，
    public void SuppressPenRing()
    {
        bool wasZero = _ringSuppressCount == 0;
        _ringSuppressCount++;
        if (wasZero && _ringInstalled)
        {
            RestoreSystemArrow();
            _ringInstalled = false;
        }
    }

    public void ResumePenRing()
    {
        if (_ringSuppressCount > 0) _ringSuppressCount--;
    }

    private bool IsPointerInsideOurWindow()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg != _hWnd) return false;
            if (!GetCursorPos(out POINT p)) return false;
            if (!GetWindowRect(_hWnd, out RECT r)) return false;
            return p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;
        }
        catch { return false; }
    }

    // 作用域判定（含标题栏/已展开工具栏/已展开画笔颜色胶囊排除）：在本窗口且前台、且不在上述任一区域 → 显示圆环；
    // 否则（离开窗口/Alt-Tab 切走/位于标题栏/位于已展开工具栏/位于已展开画笔颜色胶囊）→ 还原原生光标。
    private bool IsPointerInRingZone()
    {
        if (!IsPointerInsideOurWindow()) return false;
        if (IsPointerOverTitleBar()) return false;
        if (IsPointerOverExpandedToolbar()) return false;
        if (IsPointerOverPenOptions()) return false;
        if (IsPointerOverZoomPill()) return false;
        if (IsPointerOverZoomFlyout()) return false;
        if (IsPointerOverScrollBar()) return false;
        if (IsPointerOverCompareView()) return false;
        if (IsPointerOverThumbnailSidebar()) return false;
        if (IsPointerOverSettingsFlyout()) return false;
        return true;
    }

    private bool IsPointerOverTitleBar()
    {
        RECT? tb = GetTitleBarScreenRect();
        if (tb == null) return false;
        if (!GetCursorPos(out POINT p)) return false;
        RECT r = tb.Value;
        return p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;
    }

    // 计算自定义标题栏的屏幕矩形（物理像素），用于把圆环光标排除在标题栏之外。
    private RECT? GetTitleBarScreenRect()
    {
        try
        {
            var mw = (Application.Current as App)?.MainWindow as MainWindow;
            var tb = mw?.PenTitleBar;
            if (tb == null || tb.ActualWidth <= 0 || tb.ActualHeight <= 0) return null;
            var transform = tb.TransformToVisual(null);
            Windows.Foundation.Point tl = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = this.XamlRoot?.RasterizationScale ?? 1.0;
            POINT origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(_hWnd, ref origin)) return null;
            int left = (int)(origin.X + tl.X * scale);
            int top = (int)(origin.Y + tl.Y * scale);
            int right = (int)(origin.X + (tl.X + tb.ActualWidth) * scale);
            int bottom = (int)(origin.Y + (tl.Y + tb.ActualHeight) * scale);
            return new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        }
        catch { return null; }
    }

    private bool IsPointerOverExpandedToolbar()
    {
        if (!IsTopToolbarShown) return false;          // 仅工具栏已展开才排除
        RECT? tr = GetTopToolbarScreenRect();
        if (tr == null) return false;
        if (!GetCursorPos(out POINT p)) return false;
        RECT r = tr.Value;
        return p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;
    }

    // 指针当前是否落在【已展开】的顶层工具栏矩形内（物理像素屏幕矩形 + 实时光标坐标）。
    // 与 IsPointerOverExpandedToolbar 同一判据、同一工具，只是用途不同：那个决定"用哪种光标"，
    private bool IsPointerOverTopToolbarRect()
    {
        if (!IsTopToolbarShown) return false;
        RECT? tr = GetTopToolbarScreenRect();
        if (tr == null) return false;
        if (!GetCursorPos(out POINT p)) return false;
        RECT r = tr.Value;
        return p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;
    }

    // 计算已展开工具栏的屏幕矩形（物理像素），用于把圆环光标排除在工具栏之外。
    private RECT? GetTopToolbarScreenRect()
    {
        try
        {
            if (TopToolbar == null || TopToolbar.ActualWidth <= 0 || TopToolbar.ActualHeight <= 0) return null;
            var transform = TopToolbar.TransformToVisual(null);
            Windows.Foundation.Point tl = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = this.XamlRoot?.RasterizationScale ?? 1.0;
            POINT origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(_hWnd, ref origin)) return null;
            int left = (int)(origin.X + tl.X * scale);
            int top = (int)(origin.Y + tl.Y * scale);
            int right = (int)(origin.X + (tl.X + TopToolbar.ActualWidth) * scale);
            int bottom = (int)(origin.Y + (tl.Y + TopToolbar.ActualHeight) * scale);
            return new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        }
        catch { return null; }
    }

    private bool IsPointerOverPenOptions()
    {
        if (PenOptionsPopup == null || !PenOptionsPopup.IsOpen) return false;   // 仅胶囊展开才排除
        RECT? pr = GetPenOptionsScreenRect();
        if (pr == null) return false;
        if (!GetCursorPos(out POINT p)) return false;
        RECT r = pr.Value;
        return p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;
    }

    // 计算已展开画笔颜色胶囊(Popup)的屏幕矩形（物理像素），用于把圆环光标排除在胶囊之外。
    private RECT? GetPenOptionsScreenRect()
    {
        try
        {
            if (PenOptionsCapsule == null || PenOptionsCapsule.ActualWidth <= 0 || PenOptionsCapsule.ActualHeight <= 0) return null;
            var transform = PenOptionsCapsule.TransformToVisual(null);
            Windows.Foundation.Point tl = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = this.XamlRoot?.RasterizationScale ?? 1.0;
            POINT origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(_hWnd, ref origin)) return null;
            int left = (int)(origin.X + tl.X * scale);
            int top = (int)(origin.Y + tl.Y * scale);
            int right = (int)(origin.X + (tl.X + PenOptionsCapsule.ActualWidth) * scale);
            int bottom = (int)(origin.Y + (tl.Y + PenOptionsCapsule.ActualHeight) * scale);
            return new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        }
        catch { return null; }
    }

    // 指针当前是否落在【可见的】右下角缩放胶囊矩形内（胶囊可见时使用原生光标，方便点按缩放按钮）。
    private bool IsPointerOverZoomPill()
    {
        if (!IsZoomPillVisible()) return false;               // 仅胶囊可见才排除
        return IsPointOverZoomPill();                         // 复用已有的几何命中判定（窗口客户区 DIP 坐标系）
    }

    private bool IsZoomPillVisible()
    {
        try { return BottomZoomPillBorder != null && BottomZoomPillBorder.Opacity > 0.05; }
        catch { return false; }
    }

    private bool IsPointerOverZoomFlyout()
    {
        if (ZoomPresetFlyout == null || !ZoomPresetFlyout.IsOpen) return false;   // 仅菜单展开才排除
        RECT? fr = GetZoomFlyoutScreenRect();
        if (fr == null) return false;
        if (!GetCursorPos(out POINT p)) return false;
        RECT r = fr.Value;
        return p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;
    }

    // 计算已展开快速缩放选择菜单(ZoomPresetPanel)的屏幕矩形（物理像素），用于把圆环光标排除在菜单之外。
    private RECT? GetZoomFlyoutScreenRect()
    {
        try
        {
            if (ZoomPresetPanel == null || ZoomPresetPanel.ActualWidth <= 0 || ZoomPresetPanel.ActualHeight <= 0) return null;
            var transform = ZoomPresetPanel.TransformToVisual(null);
            Windows.Foundation.Point tl = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = this.XamlRoot?.RasterizationScale ?? 1.0;
            POINT origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(_hWnd, ref origin)) return null;
            int left = (int)(origin.X + tl.X * scale);
            int top = (int)(origin.Y + tl.Y * scale);
            int right = (int)(origin.X + (tl.X + ZoomPresetPanel.ActualWidth) * scale);
            int bottom = (int)(origin.Y + (tl.Y + ZoomPresetPanel.ActualHeight) * scale);
            return new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        }
        catch { return null; }
    }

    // 指针当前是否落在【纵横滚动条】区域（滚动条上恢复原生光标，方便拖拽滑块/点按箭头）。
    private bool IsPointerOverScrollBar() => _pointerOverScrollBar;

    // 指针当前是否落在【右侧分屏/对比视图】区域（该区不可涂画，恢复原生光标）。
    // 门槛是「右侧分屏容器(SecondaryViewerContainer)可见」——无论对比 PDF 是否已加载。
    // 容器是空 Grid(无背景)，空白区不会命中测试，不能靠 IsDescendantOf；改用屏幕矩形 + 光标坐标判定最稳。
    private bool IsPointerOverCompareView()
    {
        if (SecondaryViewerContainer == null || SecondaryViewerContainer.Visibility != Visibility.Visible) return false;
        RECT? r = GetSecondaryViewerScreenRect();
        if (r == null) return false;
        if (!GetCursorPos(out POINT p)) return false;
        return p.X >= r.Value.Left && p.X < r.Value.Right && p.Y >= r.Value.Top && p.Y < r.Value.Bottom;
    }

    // 计算右侧分屏容器(SecondaryViewerContainer)的屏幕矩形：控件 TransformToVisual(null)(有效像素)
    private RECT? GetSecondaryViewerScreenRect()
    {
        try
        {
            if (SecondaryViewerContainer == null || SecondaryViewerContainer.Visibility != Visibility.Visible) return null;
            var transform = SecondaryViewerContainer.TransformToVisual(null);
            Windows.Foundation.Point tl = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = this.XamlRoot?.RasterizationScale ?? 1.0;
            POINT origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(_hWnd, ref origin)) return null;
            int left = (int)(origin.X + tl.X * scale);
            int top = (int)(origin.Y + tl.Y * scale);
            int right = (int)(origin.X + (tl.X + SecondaryViewerContainer.ActualWidth) * scale);
            int bottom = (int)(origin.Y + (tl.Y + SecondaryViewerContainer.ActualHeight) * scale);
            return new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        }
        catch { return null; }
    }

    // 指针当前是否落在【左侧缩略图侧边栏】区域（侧边栏用于翻页/跳页，非涂画区，恢复原生光标）。
    private bool IsPointerOverThumbnailSidebar()
    {
        if (SidebarSplitView == null || SidebarSplitView.Visibility != Visibility.Visible) return false;
        if (!SidebarSplitView.IsPaneOpen) return false;
        RECT? r = GetThumbnailSidebarScreenRect();
        if (r == null) return false;
        if (!GetCursorPos(out POINT p)) return false;
        return p.X >= r.Value.Left && p.X < r.Value.Right && p.Y >= r.Value.Top && p.Y < r.Value.Bottom;
    }

    // 计算左侧缩略图侧边栏(ThumbnailSidebarPanel)的屏幕矩形：控件 TransformToVisual(null)(有效像素)
    private RECT? GetThumbnailSidebarScreenRect()
    {
        try
        {
            if (SidebarSplitView == null || SidebarSplitView.Visibility != Visibility.Visible) return null;
            if (ThumbnailSidebarPanel == null) return null;
            var transform = ThumbnailSidebarPanel.TransformToVisual(null);
            Windows.Foundation.Point tl = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = this.XamlRoot?.RasterizationScale ?? 1.0;
            POINT origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(_hWnd, ref origin)) return null;
            int left = (int)(origin.X + tl.X * scale);
            int top = (int)(origin.Y + tl.Y * scale);
            int right = (int)(origin.X + (tl.X + ThumbnailSidebarPanel.ActualWidth) * scale);
            int bottom = (int)(origin.Y + (tl.Y + ThumbnailSidebarPanel.ActualHeight) * scale);
            return new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        }
        catch { return null; }
    }

    // 指针当前是否落在【右上角设置菜单】区域（设置菜单含主题下拉框，非涂画区，恢复原生光标）。
    private bool IsPointerOverSettingsFlyout()
    {
        if (SettingsFlyout == null || !SettingsFlyout.IsOpen) return false;
        if (!GetCursorPos(out POINT p)) return false;

        RECT? r = GetSettingsFlyoutScreenRect();
        if (r != null && PointInScreenRect(p, r.Value)) return true;

        if (XamlRoot != null)
        {
            foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
            {
                if (popup == null || !popup.IsOpen) continue;
                if (popup == PenOptionsPopup) continue;
                if (popup.Child is not Microsoft.UI.Xaml.UIElement child) continue;
                RECT? pr = GetPopupScreenRect(popup, child);
                if (pr != null && PointInScreenRect(p, pr.Value)) return true;
            }
        }
        return false;
    }

    private RECT? GetSettingsFlyoutScreenRect()
    {
        try
        {
            if (SettingsFlyoutPanel == null) return null;
            var transform = SettingsFlyoutPanel.TransformToVisual(null);
            Windows.Foundation.Point tl = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = this.XamlRoot?.RasterizationScale ?? 1.0;
            POINT origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(_hWnd, ref origin)) return null;
            int left = (int)(origin.X + tl.X * scale);
            int top = (int)(origin.Y + tl.Y * scale);
            int right = (int)(origin.X + (tl.X + SettingsFlyoutPanel.ActualWidth) * scale);
            int bottom = (int)(origin.Y + (tl.Y + SettingsFlyoutPanel.ActualHeight) * scale);
            return new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        }
        catch { return null; }
    }

    // 计算一个 open popup 的屏幕矩形：Popup.Horizontal/VerticalOffset 相对页面根（与 MainPage 同系 DIP，
    private RECT? GetPopupScreenRect(Microsoft.UI.Xaml.Controls.Primitives.Popup popup, Microsoft.UI.Xaml.UIElement child)
    {
        try
        {
            if (child is not FrameworkElement fe) return null;
            if (fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
            {
                try { fe.UpdateLayout(); } catch { }
            }
            double w = fe.ActualWidth > 0 ? fe.ActualWidth : fe.DesiredSize.Width;
            double h = fe.ActualHeight > 0 ? fe.ActualHeight : fe.DesiredSize.Height;
            if (w <= 0 || h <= 0) return null;
            double scale = this.XamlRoot?.RasterizationScale ?? 1.0;
            POINT origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(_hWnd, ref origin)) return null;
            int left = (int)(origin.X + popup.HorizontalOffset * scale);
            int top = (int)(origin.Y + popup.VerticalOffset * scale);
            int right = (int)(origin.X + (popup.HorizontalOffset + w) * scale);
            int bottom = (int)(origin.Y + (popup.VerticalOffset + h) * scale);
            return new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
        }
        catch { return null; }
    }

    private static bool PointInScreenRect(POINT p, RECT r)
        => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    // 命中元素是否处于某个 ScrollBar 内（含其模板部件：Thumb / RepeatButton / 轨道 Rectangle）。
    private static bool IsInsideScrollBar(DependencyObject? node)
    {
        var cur = node;
        while (cur != null)
        {
            if (cur is Microsoft.UI.Xaml.Controls.Primitives.ScrollBar) return true;
            cur = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    // 作用域检测定时器：周期性刷新圆环开/关（指针进出窗口、Alt-Tab 切换前台等都由它兜底）。
    private void RingScopeTimer_Tick(object? sender, object e) => UpdateRingScope();

    private void EnsureRingCursor()
    {
        try
        {
            // 圆环直接以【目标显示尺寸】渲染——光标按位图自身像素尺寸显示，系统不缩放，
            // ⚠ 不能用 1bpp AND 掩码做二值透明：系统会走"掩码模式"忽略 alpha（黑边 + 上下左右发平）。
            int diameter = (int)Math.Clamp(_activeThickness * 3.0, 14, 20);
            string colorHex = ResolveActiveColorHex();
            if (_hRingCursor != IntPtr.Zero && _ringDiameter == diameter && _ringColorHex == colorHex)
                return;
            DestroyRingCursor();
            var (r, g, b) = ParseRgb(colorHex);
            _hRingCursor = CreateRingCursorHandle(diameter, r, g, b);
            _ringDiameter = diameter;
            _ringColorHex = colorHex;
        }
        catch { }
    }

    private void DestroyRingCursor()
    {
        if (_hRingCursor != IntPtr.Zero)
        {
            try { DestroyIcon(_hRingCursor); } catch { }
            _hRingCursor = IntPtr.Zero;
        }
    }

    // 构造透明背景、仅一圈【纯色】圆环的 HCURSOR（无内外描边，本体即画笔色）。
    // ⚠ 不能用 1bpp AND 掩码做二值透明 → 系统会走"掩码模式"忽略 alpha（黑边 + 上下左右发平），
    private IntPtr CreateRingCursorHandle(int diameter, byte r, byte g, byte b)
    {
        int d = Math.Max(12, diameter);
        int cx = d / 2, cy = d / 2;
        double halfW = Math.Clamp(d * 0.07, 0.9, 1.6);
        double margin = 0.6;
        double ringR = d / 2.0 - halfW - margin;
        if (ringR < 1.0) ringR = 1.0;
        const int bpp = 4;
        int colorStride = d * bpp;
        byte[] color = new byte[colorStride * d];
        int maskRowBytes = ((d + 15) / 16) * 2;
        byte[] mask = new byte[maskRowBytes * d];

        for (int y = 0; y < d; y++)
        {
            for (int x = 0; x < d; x++)
            {
                double dx = x - (cx - 0.5);
                double dy = y - (cy - 0.5);
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double e = dist - ringR;
                double cov = halfW + 0.5 - Math.Abs(e);   // 解析 AA 覆盖度（过渡 0.5px）
                if (cov <= 0) continue;
                if (cov > 1) cov = 1;

                byte a = (byte)Math.Round(cov * 255.0);
                int cidx = y * colorStride + x * bpp;
                color[cidx]     = (byte)Math.Round((double)b * cov);
                color[cidx + 1] = (byte)Math.Round((double)g * cov);
                color[cidx + 2] = (byte)Math.Round((double)r * cov);
                color[cidx + 3] = a;
            }
        }

        IntPtr hColor = CreateBitmap(d, d, 1, 32, color);
        IntPtr hMask = CreateBitmap(d, d, 1, 1, mask);
        if (hColor == IntPtr.Zero || hMask == IntPtr.Zero)
        {
            if (hColor != IntPtr.Zero) DeleteObject(hColor);
            if (hMask != IntPtr.Zero) DeleteObject(hMask);
            return IntPtr.Zero;
        }

        ICONINFO info = new ICONINFO
        {
            fIcon = 0,
            xHotspot = cx,
            yHotspot = cy,
            hbmMask = hMask,
            hbmColor = hColor
        };
        IntPtr hCursor = CreateIconIndirect(ref info);
        DeleteObject(hColor);
        DeleteObject(hMask);
        return hCursor;
    }

    private static (byte r, byte g, byte b) ParseRgb(string hex)
    {
        try
        {
            string s = (hex ?? "#F2C200").Replace("#", "");
            if (s.Length == 8) s = s.Substring(2);
            if (s.Length < 6) s = s.PadRight(6, '0');
            byte r = Convert.ToByte(s.Substring(0, 2), 16);
            byte g = Convert.ToByte(s.Substring(2, 2), 16);
            byte b = Convert.ToByte(s.Substring(4, 2), 16);
            return (r, g, b);
        }
        catch { return (255, 255, 0); }
    }

    private void SetEditMode(EditMode mode, bool openPenOptions = true)
    {
        _currentMode = mode;
        ReadModeButton.IsChecked = (mode == EditMode.Navigate);
        PenModeButton.IsChecked = (mode == EditMode.Pen);
        CanvasModeButton.IsChecked = (mode == EditMode.Canvas);

        // Toggle annotations background to allow clicking notes and scrolling in Navigate mode
        Microsoft.UI.Xaml.Media.Brush? bg = (mode == EditMode.Navigate)
            ? null
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        
        foreach (var page in _pages)
        {
            page.AnnotationsBackground = bg;
        }

        // 画笔模式：左键交给标注管线（控件内部禁用拖动平移/双击缩放，右键缩放保留）。
        if (GpuViewer != null)
        {
            GpuViewer.AnnotationInputActive = (mode == EditMode.Pen);
            GpuViewer.CanvasInputActive = (mode == EditMode.Canvas);
        }

        // Toggle the floating pen-options capsule (color + thickness) for drawing mode.
        // It lives in a Popup positioned below the Pen button, so it never shifts the toolbar layout.
        bool showColors = (mode == EditMode.Pen) && openPenOptions;
        if (PenOptionsPopup != null)
        {
            if (showColors)
            {
                ShowTopToolbar();
                ApplyPenHighlight();
                PositionPenOptionsPopup();
                PenOptionsPopup.IsOpen = true;
            }
            else
            {
                PenOptionsPopup.IsOpen = false;
            }
        }

        _penRingActive = (mode == EditMode.Pen);
        if (_penRingActive)
            EnterPenRingMode();
        else
            ExitPenRingMode();

    }

    /// Positions the pen-options Popup so its capsule drops down just below the Pen button.
    private void PositionPenOptionsPopup()
    {
        if (PenOptionsPopup == null || PenModeButton == null || TopToolbar == null)
            return;

        // Transform into the MainPage coordinate system, which matches the Popup parent.
        var btnOrigin = PenModeButton.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
        PenOptionsPopup.HorizontalOffset = btnOrigin.X;

        // The toolbar's clip host is pinned to the page top (pulled up 1px so it overlaps the
        double hostTop = TopToolbarHost?.Margin.Top ?? 0;
        PenOptionsPopup.VerticalOffset = hostTop + TopToolbar.ActualHeight - 1;
    }

    /// 全局 UI 过渡时长（毫秒）。
    /// 那边的 Duration="0:0:0.15" 必须一起改。

    // --- Top toolbar auto-hide state -------------------------------------------------
    // PointerExited is unreliable, which used to leave the bar stuck open whenever the
    private bool _pointerInTopToolbar;
    private bool _pointerInPenCapsule;
    private bool _pointerInTitleBar;
    // 画笔模式下所有"非画布"的 chrome 区域标志：标题栏/工具栏/笔胶囊由各自 PointerEntered 维护，
        private bool _pointerInBottomZoomPill;
        private bool _pointerInSecondaryPill;
        private bool _pointerOverScrollBar;

        // 光标的"几何命中"判定用的指针位置（MainPage 坐标系，由 Root_PointerMoved 持续刷新）。
        private Point _lastPointerPoint;

    private DispatcherTimer? _toolbarHideTimer;
    private Storyboard? _toolbarAnim;

    // 工具栏铺满内容区全宽时，指针"在工具栏内"不会产生任何移动事件（点击工具栏上没有独立
    private const double ToolbarWidthFitEpsilon = 0.5;

    // 最近一次指针移动的时间戳（Stopwatch 口径，与 UI 更新解耦）。
    private static readonly System.Diagnostics.Stopwatch PointerMoveClock = System.Diagnostics.Stopwatch.StartNew();
    private long _lastPointerMoveMs = PointerMoveClock.ElapsedMilliseconds;

    // 由【根元素】冒泡上来的悬停校正标志：按钮里任何把 Handled 置真的指针处理器都能让 Root_PointerMoved
    private bool _toolbarHoverSuspect;

    // 工具栏「指针归属」：仅含工具栏本体与笔胶囊。
    // 不再纳入此处拦截——否则其 leave 事件被吞时 _pointerInTitleBar 卡成 true，工具栏永远收不起来
    private bool PointerOwnsToolbar => _pointerInTopToolbar || _pointerInPenCapsule;

    /// <summary>True while the toolbar is on screen (or animating in).</summary>
    private bool IsTopToolbarShown => TopToolbar != null && TopToolbar.IsHitTestVisible;

    /// Slides the top toolbar down into view (it is a floating overlay, hidden by default).
    internal void ShowTopToolbar()
    {
        if (TopToolbar == null) return;
        CancelToolbarHide();
        if (TopToolbarHost != null) TopToolbarHost.IsHitTestVisible = true;
        if (TopToolbar.IsHitTestVisible) return;

        TopToolbar.IsHitTestVisible = true;
        TopToolbar.Opacity = 1;   // no cross-fade: a clean, complete drop-down

        // Slide the whole bar down from fully tucked away to its resting position.
        var sb = new Storyboard();
        var ya = new DoubleAnimation
        {
            From = -TopToolbarHiddenOffset(),
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(UiTransitionMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(ya, TopToolbarTranslate);
        Storyboard.SetTargetProperty(ya, "Y");
        sb.Children.Add(ya);
        StartToolbarAnim(sb);

        // NOTE: the pen-options capsule is deliberately NOT restored here. Once the toolbar
        // (pen mode itself is unaffected). Re-showing the bar must not pop the capsule open.
    }

    /// <summary>Runs a toolbar slide animation, stopping any previous one first. A completed Storyboard keeps holding its final value, which would otherwise…</summary>
    private void StartToolbarAnim(Storyboard sb)
    {
        _toolbarAnim?.Stop();
        _toolbarAnim = sb;
        sb.Begin();
    }

    /// Slides the top toolbar back up and out of view.
    /// entirely. The toolbar stops intercepting input immediately so it never blocks the
    private void HideTopToolbar()
    {
        if (TopToolbar == null) return;
        if (_toolbarAlwaysVisible) return;
        CancelToolbarHide();
        TopToolbar.IsHitTestVisible = false;
        // 收起后顶带立即让出指针：透明宿主若继续吃指针，落在顶带的卡片（矮卡尤其）会点不动、拖不出来。
        if (TopToolbarHost != null) TopToolbarHost.IsHitTestVisible = false;
        _pointerInTopToolbar = false;
        _pointerInPenCapsule = false;
        if (PenOptionsPopup != null)
            PenOptionsPopup.IsOpen = false;

        var sb = new Storyboard();
        var ya = new DoubleAnimation
        {
            From = 0,
            To = -TopToolbarHiddenOffset(),
            Duration = new Duration(TimeSpan.FromMilliseconds(UiTransitionMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(ya, TopToolbarTranslate);
        Storyboard.SetTargetProperty(ya, "Y");
        sb.Children.Add(ya);
        sb.Completed += (_, __) =>
        {
            // Only park it if nothing re-opened the bar in the meantime.
            if (TopToolbar != null && !TopToolbar.IsHitTestVisible)
                TopToolbar.Opacity = 0;
        };
        StartToolbarAnim(sb);
    }

    /// <summary>Distance the bar has to travel to be completely tucked above the clip window.</summary>
    private double TopToolbarHiddenOffset()
    {
        double h = TopToolbar?.ActualHeight ?? 0;
        if (h <= 0) h = 48;
        return h + 2;   // +2 covers the 1px overlap margin and the bottom hairline
    }

    /// <summary>Requests a hide after a short grace period. The delay lets the pointer cross the seam between the title bar, the toolbar and the pen capsule…</summary>
    private void RequestHideTopToolbar()
    {
        if (TopToolbar == null || !TopToolbar.IsHitTestVisible) return;
        if (_toolbarAlwaysVisible) return;

        if (_toolbarHideTimer == null)
        {
            _toolbarHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ToolbarHideDelayMs) };
            _toolbarHideTimer.Tick += (_, __) =>
            {
                _toolbarHideTimer!.Stop();
                if (_pageInputHasFocus) return;
                if (!PointerOwnsToolbar)
                {
                    // 悬停标志未必可信：工具栏内按钮若把移动事件标记 Handled，Root_PointerMoved
                    // 会完全断流（标志既建不起来、也无从失效）。故此处用实时光标与工具栏矩形做一次
                    //   指针确实还在工具栏上 → 补上归属并重新等待。否则"指针在工具栏上却不收起"这条
                    if (IsPointerOverTopToolbarRect()) _pointerInTopToolbar = true;
                    else if (!_toolbarHoverSuspect) PointerVerifyToolbarHover();
                }
                if (PointerOwnsToolbar || _pageInputHasFocus) return;
                if (IsAuxiliaryPopupOpen())
                {
                    // A menu / tooltip from the bar is still up: keep polling instead of
                    _toolbarHideTimer.Start();
                    return;
                }
                HideTopToolbar();
            };
        }
        // Deliberately do NOT restart a pending countdown: Root_PointerMoved fires on every
        if (_toolbarHideTimer.IsEnabled) return;
        _toolbarHideTimer.Start();
    }

    private void CancelToolbarHide() => _toolbarHideTimer?.Stop();

    /// True when a flyout / menu opened from a toolbar button is on screen.
    /// the menu — without this guard the bar would slide away under the open menu.
    private bool IsAuxiliaryPopupOpen()
    {
        if (XamlRoot == null) return false;
        foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
        {
            if (popup == PenOptionsPopup) continue;
            if (popup.IsOpen) return true;
        }
        return false;
    }

    /// 工具栏【本体内】的指针移动：重建"指针在工具栏上"的归属。
    private void TopToolbar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, TopToolbar)) return;
        _pointerInTopToolbar = true;
        _pointerInTitleBar = false;
        CancelToolbarHide();
    }

    /// 指针在工具栏的命中区外移动（工具栏的父级，即页面根）：此时指针【确实已被工具栏的 命中区排除】——否则它不会去命中工具栏的父级。
    /// 命中区排除】——否则它不会去命中工具栏的父级。但这未必等于"指针已在工具栏矩形之外"：
    private void TopToolbarHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!IsTopToolbarShown || !PointerOwnsToolbar) return;
        if (IsPointerOverTopToolbarRect()) _toolbarHoverSuspect = true;
    }

    /// 悬停标志的兜底校正：工具栏矩形已不含实时光标、却长时间没有 PointerMoved 事件到达时， 认定标志失效并请求收起（是否真收起仍由 PointerOwnsToolbar / 输入框焦点 / 浮…
    private void PointerVerifyToolbarHover()
    {
        if (TopToolbar == null || TopToolbarHost == null) return;
        if (TopToolbar.ActualWidth < TopToolbarHost.ActualWidth - ToolbarWidthFitEpsilon) return;

        long now = PointerMoveClock.ElapsedMilliseconds;
        if (now - _lastPointerMoveMs < ToolbarHideDelayMs) return;

        _lastPointerMoveMs = now;
        if (IsPointerOverTopToolbarRect()) return;
        _pointerInTopToolbar = false;
        RequestHideTopToolbar();
    }

    // 工具栏的"指针在内部"归属只在这四个位置建立：①本 Border 的 PointerEntered/②PointerMoved
    private void TopToolbar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerInTopToolbar = true;
        _pointerInTitleBar = false;   // the pointer is demonstrably below the title bar now
        CancelToolbarHide();
    }

    private void TopToolbar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 只降级、不收起：本事件在浮层打开/关闭时会被误派发（指针其实一直在工具栏上），
        _pointerInTopToolbar = false;
    }

    private void PenOptionsCapsule_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerInPenCapsule = true;
        CancelToolbarHide();
    }

    private void PenOptionsCapsule_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInPenCapsule = false;
        RequestHideTopToolbar();
    }

    /// <summary>Called by MainWindow when the pointer enters / leaves the window title bar, which is the trigger surface for the toolbar.</summary>
    internal void SetTitleBarHover(bool inside)
    {
        _pointerInTitleBar = inside;
        if (inside)
        {
            CancelToolbarHide();
            ShowTopToolbar();
        }
        else
        {
            if (!_pageInputHasFocus)
                RequestHideTopToolbar();
        }
    }

    /// Catch-all safety net: whenever the pointer moves anywhere over the page content and it is not curren…
    private void Root_PointerActivity(object sender, PointerRoutedEventArgs e)
    {
        _lastPointerMoveMs = PointerMoveClock.ElapsedMilliseconds;
        _toolbarHoverSuspect = false;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // The pointer is inside the page content, so it is definitely NOT on the title bar.
        _pointerInTitleBar = false;

        try { _lastPointerPoint = e.GetCurrentPoint(this).Position; } catch { /* 装饰 */ }

        // 用「指针实际命中的元素 + 实时光标位置」权威纠正所有 chrome 区域归属（标题栏/工具栏/笔胶囊/
        var src = e.OriginalSource as DependencyObject;
        bool overToolbarRect = IsPointerOverTopToolbarRect();
        // 命中元素落在工具栏内【且】实时坐标确实在工具栏矩形内 → 才认定为"在工具栏上"。
        bool overToolbar = overToolbarRect &&
            (src != null) && (ReferenceEquals(src, TopToolbar) || IsDescendantOf(src, TopToolbar));
        bool overCapsule = (src != null) && (ReferenceEquals(src, PenOptionsCapsule) || IsDescendantOf(src, PenOptionsCapsule));
        bool overZoom = (src != null) && (ReferenceEquals(src, BottomZoomPill) || IsDescendantOf(src, BottomZoomPill));
        bool overSecondary = (src != null) && ((ReferenceEquals(src, SecondaryOpenPill) || IsDescendantOf(src, SecondaryOpenPill))
                                            || (ReferenceEquals(src, SecondaryClosePill) || IsDescendantOf(src, SecondaryClosePill)));
        bool overScrollBar = IsInsideScrollBar(src);   // 命中元素在任一 ScrollBar(或其模板部件)内 → 视为滚动条区域
        // 右侧对比视图排除已改用 IsPointerOverCompareView() 的屏幕矩形判定（容器可见即整片右侧右半区恢复原生），

        // 工具栏归属：命中工具栏 + 实时光标在工具栏矩形内 → 建立；否则按【几何】收敛（指针已不在矩形内，
        _pointerInTopToolbar = overToolbar || overToolbarRect;
        _pointerInPenCapsule = overCapsule;
        _pointerInBottomZoomPill = overZoom;
        _pointerInSecondaryPill = overSecondary;
        _pointerOverScrollBar = overScrollBar;

        _lastPointerMoveMs = PointerMoveClock.ElapsedMilliseconds;
        _toolbarHoverSuspect = false;

        if (IsTopToolbarShown)
        {
            // 收起判据不止"指针不在工具栏上"：任何打开的浮层（设置菜单/工具提示/快速缩放菜单）都必须
            // 让工具栏先留在原地。菜单打开期间若收起工具栏，菜单关闭时指针可能已不在工具栏矩形内，
            // 才切模式"是用户已经习惯的两步式，工具栏必须陪到最后。
            if (overToolbar || overCapsule || _pageInputHasFocus || IsAuxiliaryPopupOpen())
                CancelToolbarHide();
            else
                RequestHideTopToolbar();
        }
    }

    /// When the pointer leaves the whole window content area (e.g.
    private void XamlRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInTopToolbar = false;
        _pointerInPenCapsule = false;
        // Deliberately NOT clearing _pointerInTitleBar: moving up from the page into the
        if (_pageInputHasFocus) return;
        RequestHideTopToolbar();
    }

    /// Called by MainWindow when the pointer leaves the window's whole client area (including the title bar…
    /// the title bar, which MainPage cannot observe). Clears the title-bar hover and asks for
    internal void OnWindowPointerExited()
    {
        _pointerInTitleBar = false;
        _pointerInTopToolbar = false;
        // 焦点仍在页码输入框时不收起（见需求）；窗口失焦通常会先触发输入框失焦，由统一逻辑收起。
        if (_pageInputHasFocus) return;
        RequestHideTopToolbar();
    }

    /// <summary>Retracts the toolbar unconditionally, clearing every hover flag. Used when the window is deactivated or hidden, where no pointer events are …</summary>
    internal void ForceHideTopToolbar()
    {
        _pointerInTopToolbar = false;
        _pointerInPenCapsule = false;
        _pointerInTitleBar = false;
        if (_toolbarAlwaysVisible) return;   // 常驻显示：失焦/最小化也不收起，避免恢复时闪动
        HideTopToolbar();
    }

    internal void OnAppDeactivated()
    {
        // 失焦（Alt-Tab 到其它程序）时立即卸下圆环、恢复系统箭头，避免其它前台窗口短暂显示圆环；
        if (_ringInstalled)
        {
            RestoreSystemArrow();
            _ringInstalled = false;
        }
        ForceHideTopToolbar();
    }

    internal void OnAppActivated()
    {
        if (_toolbarAlwaysVisible) ShowTopToolbar();
    }

    /// <summary>Pins the toolbar's clip window to the very top of the MainPage content area.</summary>
    private void UpdateTopToolbarMargin()
    {
        if (TopToolbarHost == null) return;
        TopToolbarHost.Margin = new Thickness(0, -1, 0, 0);
        UpdateTopToolbarClip();
        UpdateCanvasViewportInsets();   // 顶栏几何变了 ⇒ 同步白板胶囊的安全区
    }

    /// Keeps the clip rectangle in sync with the host size.
    private void UpdateTopToolbarClip()
    {
        if (TopToolbarHost == null) return;
        double w = TopToolbarHost.ActualWidth;
        double h = TopToolbarHost.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // 裁剪区向下预留阴影空间：工具栏向下投影（BlurRadius 20 + Offset 6 ≈ 26px），
        // 否则会被 TopToolbarHost 的矩形裁剪切掉，立体感出不来。顶部仍为 0，收起时工具栏
        const double ToolbarShadowAllowance = 36;
        TopToolbarHost.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, w, h + ToolbarShadowAllowance)
        };

        // Park the bar out of sight while it is hidden, using the freshly measured height.
        // Stop the held animation first, otherwise the direct write to Y is ignored.
        if (TopToolbar != null && !TopToolbar.IsHitTestVisible && TopToolbarTranslate != null)
        {
            _toolbarAnim?.Stop();
            _toolbarAnim = null;
            TopToolbarTranslate.Y = -TopToolbarHiddenOffset();
            TopToolbar.Opacity = 0;
        }
    }

    private void TopToolbarHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTopToolbarClip();
        UpdateSecondaryPillTopOffset();
        QueueToolbarOverflowUpdate();
    }

    // ======================= 顶部工具栏「空间不足收纳」 =======================
    // 目标：窗口变窄时，左右两组绝不互相重叠，也不被窗口裁切（含极端窄窗口）。

    private bool _toolbarOverflowInit;
    private bool _toolbarOverflowQueued;

    /// <summary>收纳候选：越靠前越先被收进「…」菜单。</summary>
    private FrameworkElement[] _toolbarOverflowCandidates = Array.Empty<FrameworkElement>();

    /// <summary>「…」菜单内条目的排列顺序 = 工具栏「从左到右」的顺序（越靠左越常用 ⇒ 菜单越靠上）。 与 _toolbarOverflowCandidates（收纳优先序）分开维护：后者决定「先收谁」，前者只决定「菜单里怎么排」， 两者集合（是否参与）与顺序都不同。</summary>
    private FrameworkElement[] _toolbarMenuOrder = Array.Empty<FrameworkElement>();

    /// <summary>「应用层希望的可见性」——XAML 初值 + 应用逻辑的显隐。与「是否被收纳」正交： 实际 Visibility = 应用可见 && 未被收纳。否则会出现「应用收起 → 收纳放回 → 又冒出来」的打架。</summary>
    private readonly Dictionary<FrameworkElement, bool> _toolbarAppVisible = new();

    private readonly HashSet<FrameworkElement> _toolbarOverflowed = new();

    /// <summary>「必须同进同出」的成对元素（页码输入框 ↔ 总页数）：收其中一个时一并收起。</summary>
    private readonly Dictionary<FrameworkElement, FrameworkElement> _toolbarCompanions = new();

    private readonly Dictionary<FrameworkElement, double> _toolbarItemWidth = new();

    private double _toolbarSpacing = 8;

    /// <summary>宽度试算时左右两组之间至少留出的呼吸空隙。取偏大值让收纳触发更「敏感」—— 左右两组一接近就先把右侧收起来，而不是等到几乎贴住/重叠才收。</summary>
    private const double ToolbarMinCenterGap = 28;

    private FrameworkElement[] LeftToolbarItems() => new FrameworkElement[]
    {
        ToggleSidebarButton, PrevPageButton, PageNumberInput, TotalPagesText, NextPageButton,
        ToolbarModeSeparator, ReadModeButton, PenModeButton, CanvasModeButton
    };

    private FrameworkElement[] RightToolbarItems() => new FrameworkElement[]
    {
        RotateButton, LayoutToggleButton, SplitViewToggleButton, SaveButton, PrintButton,
        SettingsButton, ToolbarMoreButton
    };

    private void InitializeToolbarOverflow()
    {
        _toolbarSpacing = TopLeftPanel?.Spacing ?? 8;

        // 先保留「初始化前」已由应用逻辑写入的可见性意图（如欢迎页 ApplyWelcomeChrome 早于本方法）；
        foreach (var el in LeftToolbarItems()) if (!_toolbarAppVisible.ContainsKey(el)) _toolbarAppVisible[el] = el.Visibility != Visibility.Collapsed;
        foreach (var el in RightToolbarItems()) if (!_toolbarAppVisible.ContainsKey(el)) _toolbarAppVisible[el] = el.Visibility != Visibility.Collapsed;
        _toolbarAppVisible[ToolbarMoreButton] = true;

        // 「…」平时 Collapsed：先量一次自然宽度缓存起来，之后按宽度试算时才能把它自己算进去。
        if (ToolbarMoreButton != null)
        {
            ToolbarMoreButton.Visibility = Visibility.Visible;
            ToolbarMoreButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (ToolbarMoreButton.DesiredSize.Width > 0) _toolbarItemWidth[ToolbarMoreButton] = ToolbarMoreButton.DesiredSize.Width;
            ToolbarMoreButton.Visibility = Visibility.Collapsed;
        }

        _toolbarOverflowCandidates = new FrameworkElement[]
        {
            PrintButton, SaveButton, SplitViewToggleButton, LayoutToggleButton, RotateButton,
            NextPageButton, PrevPageButton,
            TotalPagesText, PageNumberInput, ToolbarModeSeparator, ToggleSidebarButton,
            CanvasModeButton, PenModeButton, ReadModeButton,
            SettingsButton,
        };

        _toolbarMenuOrder = new FrameworkElement[]
        {
            ToggleSidebarButton, PrevPageButton, NextPageButton, ReadModeButton, PenModeButton,
            CanvasModeButton, RotateButton, LayoutToggleButton, SplitViewToggleButton,
            SaveButton, PrintButton, SettingsButton,
        };

        // 页码输入框与总页数是「一个整体」：窗口变窄时要么都留、要么一起收，避免只剩孤零零的「/ 12」或「1」。
        _toolbarCompanions[TotalPagesText] = PageNumberInput;
        _toolbarCompanions[PageNumberInput] = TotalPagesText;

        if (ToolbarMoreFlyout != null)
            ToolbarMoreFlyout.Opening += (s, e) => UpdateToolbarOverflow();

        _toolbarOverflowInit = true;
        QueueToolbarOverflowUpdate();   // 首帧按实际宽度落一次收纳结果
    }

    private void SetToolbarItemAppVisible(FrameworkElement el, bool visible)
    {
        if (el == null) return;
        _toolbarAppVisible[el] = visible;
        if (!_toolbarOverflowInit)
        {
            // 收纳系统尚未初始化（如欢迎页在 Loaded 之前就设置显隐）：先直接落地到 Visibility，
            el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        QueueToolbarOverflowUpdate();
    }

    private void QueueToolbarOverflowUpdate()
    {
        if (!_toolbarOverflowInit || _toolbarOverflowQueued) return;
        _toolbarOverflowQueued = true;
        // 延迟到本轮布局之后：SizeChanged 里直接量 ActualWidth 可能取到旧值，且避免测量引起的布局重入。
        DispatcherQueue.TryEnqueue(() =>
        {
            _toolbarOverflowQueued = false;
            UpdateToolbarOverflow();
            UpdateMinWindowClientWidth();   // 与收纳同拍重算最小窗口宽度（同一套实测宽度口径）
        });
    }

    private void UpdateToolbarOverflow()
    {
        if (!_toolbarOverflowInit || TopToolbar == null) return;

        double available = TopToolbar.ActualWidth - TopToolbar.Padding.Left - TopToolbar.Padding.Right;
        if (!(available > 0)) return;   // 尚未布局，等下一次 SizeChanged / 排队回调

        _toolbarOverflowed.Clear();

        int guard = _toolbarOverflowCandidates.Length + 1;
        while (!ToolbarFits(available) && guard-- > 0)
        {
            var next = NextToolbarOverflowCandidate();
            if (next == null) break;      // 可收的都收完了（窗口已到极限），剩下的只能挤一挤
            _toolbarOverflowed.Add(next);
            if (_toolbarCompanions.TryGetValue(next, out var companion)) _toolbarOverflowed.Add(companion);
        }

        ApplyToolbarVisibility();
        RebuildToolbarMoreMenu();
    }

    private bool ToolbarFits(double available)
        => ToolbarPanelWidth(LeftToolbarItems()) + ToolbarPanelWidth(RightToolbarItems()) + ToolbarMinCenterGap <= available;

    private double ToolbarPanelWidth(FrameworkElement[] items)
    {
        double w = 0; int n = 0;
        foreach (var el in items)
        {
            if (!IsToolbarItemShown(el)) continue;
            w += ToolbarItemWidth(el);
            n++;
        }
        return n > 1 ? w + _toolbarSpacing * (n - 1) : w;
    }

    // ======================= 最小窗口尺寸（DIP；MainWindow 换算成物理像素后经 WM_GETMINMAXINFO 施加）=======================
    // 设计目标：
    //   · 宽度 —— 展开工具栏时「侧边栏 / 阅读 / 画笔 / 白板 / 设置 / …」这一组必须能并排放下且互不重叠。
    // 宽度取自工具栏自身的实测宽度（与收纳判定同源口径）⇒ 随 DPI / 语言 / 主题自适应，不写死像素。
    private const double A4ShortEdgePt = 595.276;
    private const double A4LongEdgePt = 841.89;

    private double _minWindowClientWidthDip;

    /// <summary>最小窗口「客户区宽度」(DIP)。MainWindow 在 WM_GETMINMAXINFO / 钳制里读取该值。 未测出时返回 0（= 暂无下限），调用方一律跳过钳制。</summary>
    internal double MinWindowClientWidthDip => _minWindowClientWidthDip;

    /// <summary>最小宽度下视口（= 客户区高 − 标题栏高）所需高度：适应宽度时整页 A4 恰好可见。 「适应宽度」把页宽缩到 (视口宽 − 2×DocMargin)，页高按 A4 长/短边比随之缩放，上下再补 DocMargin ⇒ 从文档顶部起可完整看到一页 A4。</summary>
    internal double MinViewportHeightDip
    {
        get
        {
            if (_minWindowClientWidthDip <= 0) return 0;
            double pageW = Math.Max(1.0, _minWindowClientWidthDip - 2 * Tuning.DocMargin);
            return pageW * (A4LongEdgePt / A4ShortEdgePt) + 2 * Tuning.DocMargin;
        }
    }

    /// <summary>最小尺寸下工具栏必须常驻的那组项（其余都可进「…」菜单）：与收纳优先级链条的末端一致。</summary>
    private FrameworkElement[] MinSizeLeftToolbarItems()
        => new FrameworkElement[] { ToggleSidebarButton, ToolbarModeSeparator, ReadModeButton, PenModeButton, CanvasModeButton };

    private FrameworkElement[] MinSizeRightToolbarItems()
        => new FrameworkElement[] { SettingsButton, ToolbarMoreButton };

    private void UpdateMinWindowClientWidth()
    {
        if (TopToolbar == null || !_toolbarOverflowInit) return;

        double left = RequiredToolbarPanelWidth(MinSizeLeftToolbarItems());
        double right = RequiredToolbarPanelWidth(MinSizeRightToolbarItems());
        if (!(left > 0) || !(right > 0)) return;

        double w = left + right + ToolbarMinCenterGap
                 + TopToolbar.Padding.Left + TopToolbar.Padding.Right;
        if (!(w > _minWindowClientWidthDip)) return;   // 只增不减：避免某一次偏小的测量把下限压低

        _minWindowClientWidthDip = w;
        // 已存在的窗口可能仍停在保存的旧几何上，立刻顶到新下限。
        ((Application.Current as App)?.MainWindow as MainWindow)?.ClampWindowToMinSize();
    }

    /// <summary>一组工具项的自然总宽（含各 item 的 Margin 与 StackPanel Spacing），【不看】当前收纳状态。 每项向上取整：XAML 布局按整像素落位，实际占宽可能比测量值大最多 1 DIP/项；</summary>
    private double RequiredToolbarPanelWidth(FrameworkElement[] items)
    {
        double w = 0; int n = 0;
        foreach (var el in items)
        {
            if (el == null) continue;
            w += Math.Ceiling(ToolbarItemWidth(el));
            n++;
        }
        return n > 1 ? w + _toolbarSpacing * (n - 1) : w;
    }

    private bool ToolbarItemIsAppVisible(FrameworkElement el)
        => _toolbarAppVisible.TryGetValue(el, out bool v) ? v : el.Visibility != Visibility.Collapsed;

    private bool HasVisibleOverflowedItems()
    {
        foreach (var el in _toolbarOverflowed) if (ToolbarItemIsAppVisible(el)) return true;
        return false;
    }

    private bool IsToolbarItemShown(FrameworkElement el)
    {
        // 「…」只在确有可见项被收起时才出现。
        if (el == ToolbarMoreButton) return HasVisibleOverflowedItems();
        return ToolbarItemIsAppVisible(el) && !_toolbarOverflowed.Contains(el);
    }

    /// <summary>工具项在工具栏中占用的宽度（含左右外边距）。ActualWidth 不含 Margin，必须补上 —— 否则带 Margin 的项（总页数 12px、分隔线 8px）会被低估，导致收纳触发过晚、左右两组先贴住甚至重叠。</summary>
    private double ToolbarItemWidth(FrameworkElement el)
    {
        double raw;
        if (el.Visibility == Visibility.Visible && el.ActualWidth > 0)
        {
            raw = el.ActualWidth;
            _toolbarItemWidth[el] = raw;
        }
        else if (_toolbarItemWidth.TryGetValue(el, out double cached) && cached > 0)
        {
            raw = cached;
        }
        else
        {
            // 尚未布局过（如加载文档后 / 从欢迎页返回后才显形的保存、打印、对比键）：临时置为可见量一次
            var old = el.Visibility;
            el.Visibility = Visibility.Visible;
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            raw = el.DesiredSize.Width;
            el.Visibility = old;
            if (raw > 0) _toolbarItemWidth[el] = raw;
        }
        return raw + el.Margin.Left + el.Margin.Right;
    }

    /// <summary>当前处于「激活」态的模式键（阅读 / 画笔 / 白板）恒定保留：它既是当前模式的唯一指示、 又是退出该模式的入口，画笔模式下颜色胶囊还按它的位置定位。故只有「未激活」的模式键可被收纳。</summary>
    private bool IsToolbarItemActiveMode(FrameworkElement el)
    {
        if (el == ReadModeButton) return ReadModeButton.IsChecked == true;
        if (el == PenModeButton) return PenModeButton.IsChecked == true;
        if (el == CanvasModeButton) return CanvasModeButton.IsChecked == true;
        return false;
    }

    /// <summary>「…」按钮承载溢出菜单本身，绝不能被收纳。</summary>
    private bool IsToolbarItemPinned(FrameworkElement el)
        => el == ToolbarMoreButton;

    private FrameworkElement? NextToolbarOverflowCandidate()
    {
        foreach (var el in _toolbarOverflowCandidates)
        {
            if (el == null || !ToolbarItemIsAppVisible(el)) continue;
            if (_toolbarOverflowed.Contains(el) || IsToolbarItemActiveMode(el) || IsToolbarItemPinned(el)) continue;
            return el;
        }
        return null;
    }

    private void ApplyToolbarVisibility()
    {
        foreach (var el in LeftToolbarItems()) el.Visibility = IsToolbarItemShown(el) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var el in RightToolbarItems()) el.Visibility = IsToolbarItemShown(el) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildToolbarMoreMenu()
    {
        if (ToolbarMoreFlyout == null) return;
        ToolbarMoreFlyout.Items.Clear();
        foreach (var el in _toolbarMenuOrder)
        {
            if (el == null || !_toolbarOverflowed.Contains(el) || !ToolbarItemIsAppVisible(el)) continue;
            if (!IsToolbarItemActionable(el)) continue;   // 页码输入/总页数/分隔线无动作，只收纳不上菜单
            ToolbarMoreFlyout.Items.Add(CreateToolbarMoreItem(el));
        }
    }

    private bool IsToolbarItemActionable(FrameworkElement el)
        => el != TotalPagesText && el != PageNumberInput && el != ToolbarModeSeparator;

    private MenuFlyoutItemBase CreateToolbarMoreItem(FrameworkElement el)
    {
        string label = Loc(ToolbarItemLabelKey(el));
        var icon = CreateToolbarItemIcon(el);

        // 一律用 MenuFlyoutItem：菜单里只要混入一个 ToggleMenuFlyoutItem，WinUI 就会对【所有】条目
        var item = new MenuFlyoutItem { Text = label, Icon = icon };
        item.Click += (s, e) => InvokeToolbarItemFromOverflow(el);
        return item;
    }

    private string ToolbarItemLabelKey(FrameworkElement el)
    {
        if (el == PrintButton) return "Print";
        if (el == SaveButton) return "Save";
        if (el == SplitViewToggleButton) return "SplitView";
        if (el == LayoutToggleButton) return "LayoutToggle";
        if (el == RotateButton) return "Rotate";
        if (el == NextPageButton) return "NextPage";
        if (el == PrevPageButton) return "PrevPage";
        if (el == ToggleSidebarButton) return "ToggleSidebar";
        if (el == ReadModeButton) return "ReadMode";
        if (el == PenModeButton) return "PenMode";
        if (el == CanvasModeButton) return "CanvasMode";
        if (el == SettingsButton) return "Settings";
        return "MoreTools";
    }

    private IconElement CreateToolbarItemIcon(FrameworkElement el)
    {
        if (el == PrintButton) return FluentFontIcon("\uE749");
        if (el == SaveButton) return new SymbolIcon(Microsoft.UI.Xaml.Controls.Symbol.Save);
        if (el == SplitViewToggleButton) return new SymbolIcon(Microsoft.UI.Xaml.Controls.Symbol.TwoPage);
        if (el == LayoutToggleButton) return FluentFontIcon("\uE8AB");
        if (el == RotateButton) return new SymbolIcon(Microsoft.UI.Xaml.Controls.Symbol.Rotate);
        if (el == NextPageButton) return FluentFontIcon("\uE70D");
        if (el == PrevPageButton) return FluentFontIcon("\uE70E");
        if (el == ToggleSidebarButton) return FluentFontIcon("\uE90C");
        if (el == ReadModeButton) return new SymbolIcon(Microsoft.UI.Xaml.Controls.Symbol.Read);
        if (el == PenModeButton) return new SymbolIcon(Microsoft.UI.Xaml.Controls.Symbol.Edit);
        if (el == CanvasModeButton) return FluentFontIcon("\uECA5");
        if (el == SettingsButton) return new SymbolIcon(Microsoft.UI.Xaml.Controls.Symbol.Setting);
        return FluentFontIcon("\uE712");
    }

    /// <summary>与工具栏图标同源的字体回退链（Win11 用 Segoe Fluent Icons，Win10 回退 Segoe MDL2 Assets）。</summary>
    private static FontIcon FluentFontIcon(string glyph) => new FontIcon
    {
        Glyph = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
    };

    /// <summary>菜单项被点击：先把该项放回工具栏（模式键需先可见，画笔胶囊才定位得对）， 再执行与点击按钮完全相同的处理逻辑，最后按新宽度重新试算收纳。</summary>
    private void InvokeToolbarItemFromOverflow(FrameworkElement el)
    {
        _toolbarOverflowed.Remove(el);
        ApplyToolbarVisibility();

        if (el == PrintButton) Print_Click(this, new RoutedEventArgs());
        else if (el == SaveButton) SavePdf_Click(this, new RoutedEventArgs());
        else if (el == SplitViewToggleButton)
        {
            if (SplitViewToggleButton != null)
                SplitViewToggleButton.IsChecked = !(SplitViewToggleButton.IsChecked == true);
            SplitViewToggle_Click(this, new RoutedEventArgs());
        }
        else if (el == LayoutToggleButton) LayoutToggle_Click(this, new RoutedEventArgs());
        else if (el == RotateButton) Rotate_Click(this, new RoutedEventArgs());
        else if (el == NextPageButton) NextPage_Click(this, new RoutedEventArgs());
        else if (el == PrevPageButton) PrevPage_Click(this, new RoutedEventArgs());
        else if (el == ToggleSidebarButton) ToggleSidebar_Click(this, new RoutedEventArgs());
        else if (el == ReadModeButton) ReadMode_Click(this, new RoutedEventArgs());
        else if (el == PenModeButton) PenMode_Click(this, new RoutedEventArgs());
        else if (el == CanvasModeButton) CanvasMode_Click(this, new RoutedEventArgs());
        else if (el == SettingsButton)
        {
            // 设置键被收进菜单时，其浮出层改挂到「…」按钮上弹出。延迟一拍，等「…」菜单先关闭，
            // 否则新浮出层可能被菜单的 light-dismiss 顺手关掉。
            DispatcherQueue.TryEnqueue(() =>
            {
                if (SettingsFlyout != null) SettingsFlyout.ShowAt(ToolbarMoreButton);
            });
        }

        UpdateToolbarOverflow();
    }

    // 对比视图圆按钮的外层 Grid 内边距（与 MainPage.xaml 中 SecondaryOpen/ClosePill 的 Padding 一致）。
    private const double SecondaryPillPadding = 8;
    private const double SecondaryPillGap = 8;

    /// <summary>把对比视图左上"+"和右上"×"下移到展开后的上工具栏之下——否则工具栏滑下来就把它们盖住了。 偏移量取自实测的工具栏高度（不硬编码），字号/DPI/按钮内容变化时自动跟随。</summary>
    private void UpdateSecondaryPillTopOffset()
    {
        if (SecondaryOpenPill == null || SecondaryClosePill == null) return;

        double hostTop = TopToolbarHost?.Margin.Top ?? 0;   // 宿主上提 1px 压住标题栏发丝线
        double barH = TopToolbar?.ActualHeight ?? 0;
        if (barH <= 0) barH = 48;                           // 首次布局前的兜底，与 TopToolbarHiddenOffset 同值

        // 圆的上沿 = 工具栏底边 + 空隙；Margin 要再减去外层热区的 Padding。
        double top = Math.Max(4, hostTop + barH + SecondaryPillGap - SecondaryPillPadding);

        var mo = SecondaryOpenPill.Margin;
        SecondaryOpenPill.Margin = new Thickness(mo.Left, top, mo.Right, mo.Bottom);

        var mc = SecondaryClosePill.Margin;
        SecondaryClosePill.Margin = new Thickness(mc.Left, top, mc.Right, mc.Bottom);

        UpdateSidebarTopInset();
    }

    /// 侧边栏内容顶部让出展开工具栏的高度——否则同一根 Grid 里 ZIndex=50 的工具栏会盖住 侧边栏标题行与第一张缩略图的上沿（SidebarSplitView 是 ZIndex=0，恒在其下）。
    /// 与 UpdateSecondaryPillTopOffset 同一思路：偏移量取自实测的工具栏高度（不硬编码），
    private void UpdateSidebarTopInset()
    {
        if (ThumbnailSidebarPanel == null) return;

        double hostTop = TopToolbarHost?.Margin.Top ?? 0;   // 宿主上提 1px 压住标题栏发丝线
        double barH = TopToolbar?.ActualHeight ?? 0;
        if (barH <= 0) barH = 48;                           // 首次布局前的兜底，与 TopToolbarHiddenOffset 同值

        // 在工具栏底边之下再留 6px 呼吸空隙，避免标题行紧贴工具栏发丝线。
        double top = Math.Max(14, hostTop + barH + 6);

        var p = ThumbnailSidebarPanel.Padding;
        if (Math.Abs(p.Top - top) > 0.5)
            ThumbnailSidebarPanel.Padding = new Thickness(p.Left, top, p.Right, p.Bottom);

        UpdateCanvasViewportInsets();   // 顶栏高度变了 ⇒ 白板悬浮胶囊的安全区也要跟着变
    }

    /// 把【盖在 GpuPageView 之上、会吞掉指针】的那层浮层占掉的视口四边量告诉 GpuPageView，
    /// 让白板悬浮胶囊（卡片 / 连线）与文本菜单的视口钳制避开这些带 —— 否则胶囊被钳到它们底下时
    private void UpdateCanvasViewportInsets()
    {
        if (GpuViewer == null) return;
        double vw = GpuViewer.ActualWidth, vh = GpuViewer.ActualHeight;
        if (vw <= 0 || vh <= 0) return;

        // 只避让【顶 / 底】两条带：左右两侧只有角上的圆钮占位，为它们整条内缩会把中间的可用宽度也吃掉，
        // 不划算。
        // ⚠ 顶部安全区【仅取工具栏宿主高度本身】，不再把左上「+」/右上「×」圆钮的整条高度并入。
        double top = BottomOf(TopToolbarHost);
        // 缩放胶囊（BottomZoomPillBorder）靠 Opacity 淡入淡出隐藏，但 Opacity=0 的元素【仍参与命中测试、
        double zoomPillH = BottomZoomPillBorder?.ActualHeight ?? 0;
        if (zoomPillH <= 0 && BottomZoomPillBorder != null && !double.IsNaN(BottomZoomPillBorder.Height))
            zoomPillH = BottomZoomPillBorder.Height;                // 未布局/已脱树兜底：用设计高度
        if (zoomPillH <= 0) zoomPillH = 42;                        // 最坏兜底：设计稿高度
        double zoomPillBottomMargin = BottomZoomPill?.Margin.Bottom ?? 10;
        double bottom = zoomPillH + zoomPillBottomMargin;
        GpuViewer.SetCanvasViewportInsets(0, top, 0, bottom);

        // 元素在 GpuViewer 坐标系里的上/下沿；不可见 / 已淡出 / 已脱树 ⇒ 0（= 不占边）。
        double BottomOf(FrameworkElement? el)
        {
            var t = TopOf(el);
            if (t == 0) return 0;
            return t + (el?.ActualHeight ?? 0);
        }

        double TopOf(FrameworkElement? el)
        {
            if (el == null || el.Visibility != Visibility.Visible || el.Opacity <= 0.05) return 0;
            if (el.ActualWidth <= 0 || el.ActualHeight <= 0) return 0;
            try
            {
                return el.TransformToVisual(GpuViewer).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            }
            catch { return 0; }   // 元素已脱树（窗口关闭中）⇒ 当作不占边
        }
    }

    /// <summary>把当前画笔色解析为可绘制的 6 位 hex（含 alpha 前缀由调用方处理）。 哨兵 "Accent" = 主题强调紫，按当前 解析（浅 #9873F7 / 深 #A882FF）， 使其随深浅主题切换；其余色号为固定值、仅顺序调动。</summary>
    private string ResolveActiveColorHex()
    {
        if (_activeColorHex == AccentColorTag)
            return this.ActualTheme == ElementTheme.Dark ? "#A882FF" : "#9873F7";
        return _activeColorHex ?? "#F2C200";
    }

    private string GetActiveColorHexWithOpacity()
    {
        string color = ResolveActiveColorHex();
        
        string clean = color.Replace("#", "");
        if (clean.Length == 8)
        {
            clean = clean.Substring(2);
        }
        
        return $"#FF{clean}";
    }

    /// <summary>Updates the Pen mode button's live highlight to match the currently selected pen color.</summary>
    private void ApplyPenHighlight()
    {
        string clean = ResolveActiveColorHex().Replace("#", "");
        if (clean.Length == 8) clean = clean.Substring(2);
        if (clean.Length != 6) clean = "F2C200";

        byte r = Convert.ToByte(clean.Substring(0, 2), 16);
        byte g = Convert.ToByte(clean.Substring(2, 2), 16);
        byte b = Convert.ToByte(clean.Substring(4, 2), 16);

        // 三态背景 = 同一画笔色，但按主题对 ReadModeButton 的档差施加不透明度梯度。
        //   Checked            → Opacity 1.0
        Windows.UI.Color pen = Windows.UI.Color.FromArgb(255, r, g, b);
        _penHighlightBrush.Color = pen;
        _penHighlightBrush.Opacity = 1.0;
        _penHighlightBrushHover.Color = pen;
        _penHighlightBrushHover.Opacity = 0.9;
        _penHighlightBrushPressed.Color = pen;
        _penHighlightBrushPressed.Opacity = 0.8;

        double luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
        _penForegroundBrush.Color = luminance > 0.6
            ? Microsoft.UI.Colors.Black
            : Microsoft.UI.Colors.White;
    }

    private void ThicknessPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag &&
            double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out double w))
        {
            _activeThickness = w;
            SyncPenCursorThickness();
            if (_penRingActive) RefreshRingCursor();
            HighlightThicknessPreset(btn);
        }
    }

    /// <summary><summary> </summary></summary>
    private void SyncPenCursorThickness()
    {
        try { if (GpuViewer != null) GpuViewer.PenThicknessPoints = _activeThickness; }
        catch { /* 光标只是提示，不影响绘制 */ }
    }

    /// 选中/悬浮/按下的背景完全由按钮控件的 Background 属性（DP）承载， 与左侧颜色按钮直接改 BorderBrush 同一个模式：改 DP 属性会立即重绘， 不存在"重开才生效"的问题。
    private void HighlightThicknessPreset(Button? selected)
    {
        if (ThicknessPresetPanel == null) return;
        foreach (var child in ThicknessPresetPanel.Children)
        {
            if (child is Button b)
            {
                b.Background = (b == selected) ? _thicknessPresetSelectedBrush : _thicknessPresetTransparentBrush;
            }
        }
    }

    /// 构造期【一次性】把粗细预设按钮的悬浮/按下底色指向共享灰刷 （选中/悬浮/按下同色，杜绝状态切换变色）。
    /// （崩溃日志 stack：ResourceDictionary.set_Item → RefreshColorSwatchState）。
    private void InstallThicknessPresetHoverBrushes()
    {
        if (ThicknessPresetPanel == null) return;
        foreach (var child in ThicknessPresetPanel.Children)
        {
            if (child is Button b)
            {
                b.Resources["ButtonBackgroundPointerOver"] = _thicknessPresetSelectedBrush;
                b.Resources["ButtonBackgroundPressed"] = _thicknessPresetSelectedBrush;
            }
        }
    }

    /// <summary>把单一颜色按钮刷新为「选中/未选中」对应的视觉： - 选中：圆点放大到 16 × 1.72；未选中：原大小 - 无任何包围环：边框恒透明（悬浮/按下的边框与底色档在 XAML 解析期逐按钮声明）。</summary>
    private void RefreshColorSwatchState(Button b)
    {
        bool isSel = (b == _selectedColorButton);
        SetColorSwatchSize(b, isSel);
    }

    /// 放大/还原圆点：改布局宽高 + CornerRadius + 四边负 Margin，而不是 RenderTransform。
    /// 边缘被重采样后发糊（用户反馈的"选中放大后更模糊"根因）。改布局尺寸则让框架按
    /// 圆点相对自身中心向四周均匀溢出（StackPanel 不裁剪子元素，与原 RenderTransform 表现一致）。
    private void SetColorSwatchSize(Button b, bool enlarged)
    {
        b.Width = b.Height = enlarged ? ColorSwatchEnlargedSize : ColorSwatchBaseSize;
        b.CornerRadius = new CornerRadius(enlarged ? ColorSwatchEnlargedRadius : ColorSwatchBaseRadius);
        double inset = enlarged ? ColorSwatchEnlargedInset : 0.0;
        b.Margin = new Thickness(-inset);
    }

    /// <summary>Highlights the selected color swatch and clears the others.</summary>
    private void HighlightColorSwatch(Button? selected)
    {
        _selectedColorButton = selected;
        if (ColorSelectorPanel == null) return;
        foreach (var child in ColorSelectorPanel.Children)
        {
            if (child is Button b) RefreshColorSwatchState(b);
        }
    }

    /// <summary>悬浮预览：圆点放大到 16 × 1.72；离开后由 PointerExited 恢复为 选中（同放大）或未选中（原大小）。</summary>
    private void ColorSwatch_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button b) SetColorSwatchSize(b, true);
    }

    private void ColorSwatch_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button b) RefreshColorSwatchState(b);
    }

    /// <summary>给颜色按钮逐个挂悬浮/离开事件（悬浮预览用）。 悬浮/按下的底色与边框透明档已在 XAML 解析期写进每个按钮自己的 Resources， 这里不做任何 element.Resources 写入（运行时写入会闪退）。</summary>
    private void AttachColorSwatchHover()
    {
        if (ColorSelectorPanel == null) return;
        foreach (var child in ColorSelectorPanel.Children)
        {
            if (child is Button b)
            {
                b.PointerEntered += ColorSwatch_PointerEntered;
                b.PointerExited += ColorSwatch_PointerExited;
            }
        }
    }

    /// <summary>Programmatically syncs the color-swatch highlight to match the currently stored _activeColorHex (used on load / after deserializing settings…</summary>
    private void SyncColorSwatchHighlight()
    {
        if (ColorSelectorPanel == null) return;
        string target = _activeColorHex ?? "#F2C200";
        Button? match = null;
        foreach (var child in ColorSelectorPanel.Children)
        {
            if (child is Button b && b.Tag?.ToString() is string tag && tag.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                match = b;
                break;
            }
        }
        HighlightColorSwatch(match);
    }

    /// <summary>Programmatically selects a thickness preset (used to mark the default on load).</summary>
    private void SelectThicknessPreset(double w)
    {
        _activeThickness = w;
        SyncPenCursorThickness();
        if (ThicknessPresetPanel == null) return;
        foreach (var child in ThicknessPresetPanel.Children)
        {
            if (child is Button b && b.Tag is string tag &&
                double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out double bw) && bw == w)
            {
                HighlightThicknessPreset(b);
                return;
            }
        }
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        if (button != null && button.Tag != null)
        {
            _activeColorHex = button.Tag.ToString() ?? "#F2C200";
            ApplyPenHighlight();
            HighlightColorSwatch(button);
            if (_penRingActive) RefreshRingCursor();
        }
    }

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfRenderService == null) return;

        GpuViewer.RotateClockwise();
    }

    private void LayoutToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfRenderService == null) return;
        var next = GpuViewer.IsHorizontalLayout
            ? PageLayoutMode.VerticalContinuous
            : PageLayoutMode.HorizontalContinuous;
        GpuViewer.LayoutMode = next;
        if (GpuViewerRight != null && GpuViewerRight.Visibility == Visibility.Visible)
            GpuViewerRight.LayoutMode = next;
    }

    private void UpdateLayoutToggleVisual()
    {
        if (LayoutToggleIcon == null || LayoutToggleButton == null) return;
        bool horiz = GpuViewer != null && GpuViewer.IsHorizontalLayout;
        // 图标旋转 90° 作为"横向"的视觉提示；纵向时正立。绕中心旋转避免位移。
        LayoutToggleIcon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        LayoutToggleIcon.RenderTransform = horiz
            ? new Microsoft.UI.Xaml.Media.RotateTransform { Angle = 90 }
            : null;
    }

    private void SplitViewToggle_Click(object sender, RoutedEventArgs e)
    {
        if (SplitViewToggleButton == null) return;

        if (SplitViewToggleButton.IsChecked == true)
        {
            // Open split view (50/50 columns). The comparison open/close pills live inside
            SecondaryViewerColumn.Width = new GridLength(1, GridUnitType.Star);
            SecondaryViewerContainer.Visibility = Visibility.Visible;
            ViewerDivider.Visibility = Visibility.Visible;
            // 刚开分屏时右侧还是空的："+"常驻可见，否则用户在空白区找不到入口。
            SetSecondaryOpenPillPinned(GpuViewerRight.Visibility != Visibility.Visible);
            // 兜底：容器为 Collapsed 时可能错过工具栏的 SizeChanged，这里补算一次纵向偏移。
            UpdateSecondaryPillTopOffset();
            UpdateSyncScrollBar();
        }
        else
        {
            SecondaryViewerColumn.Width = new GridLength(0);
            SecondaryViewerContainer.Visibility = Visibility.Collapsed;
            ViewerDivider.Visibility = Visibility.Collapsed;
            SyncScrollBar.Visibility = Visibility.Collapsed;
            SyncScrollColumn.Width = new GridLength(0);
            _pagesRight.Clear();
            if (_pdfRenderServiceRight != null)
            {
                try { _pdfRenderServiceRight.Dispose(); } catch { }
                _pdfRenderServiceRight = null;
            }
            // GPU 右视图：隐藏并释放其 GPU 位图缓存（否则显存随对照 PDF 滞留）。
            GpuViewerRight.Visibility = Visibility.Collapsed;
            GpuViewerRight.ResetView();
            SetSecondaryOpenPillPinned(true);
            if (_pdfRenderRightCts != null)
            {
                try { _pdfRenderRightCts.Cancel(); } catch { }
                _pdfRenderRightCts.Dispose();
                _pdfRenderRightCts = null;
            }
            SchedulePostLoadMemoryTrim();
        }
    }

    // --- Comparison (right pane) open/close pills ----------------------------------------
    // The old drop-down comparison bar was deleted (too hard to unify with the main toolbar).

    private bool _secondaryOpenPillPinned = true;

    /// <summary>切换 "+" 胶囊的常驻/悬停模式。pinned=true → 立即常显；false → 立即淡出为悬停触发。</summary>
    private void SetSecondaryOpenPillPinned(bool pinned)
    {
        _secondaryOpenPillPinned = pinned;
        if (SecondaryOpenPillBorder == null) return;

        // WinUI 坑：Storyboard 默认 HoldEnd 会锁住被动画的 DP，不先 Stop 就直接赋值不生效。
        SecondaryOpenPillShowAnim?.Stop();
        SecondaryOpenPillHideAnim?.Stop();
        SecondaryOpenPillBorder.Opacity = pinned ? 1.0 : 0.0;
    }

    private void SecondaryOpenPill_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_secondaryOpenPillPinned) return;   // 已经常显，不必再播淡入
        SecondaryOpenPillShowAnim?.Begin();
    }

    private void SecondaryOpenPill_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_secondaryOpenPillPinned) return;
        SecondaryOpenPillHideAnim?.Begin();
    }

    private void SecondaryClosePill_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SecondaryClosePillShowAnim?.Begin();
    }

    private void SecondaryClosePill_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SecondaryClosePillHideAnim?.Begin();
    }

    private void BottomZoomPill_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        BottomPillShowAnim?.Begin();
    }

    private void BottomZoomPill_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        bool isOpen = ZoomPresetFlyout != null && ZoomPresetFlyout.IsOpen;
        if (isOpen) return;
        // LightDismiss 覆盖层移除瞬间会误派发 PointerExited（即使指针仍悬停在胶囊上），
        if (IsPointOverZoomPill()) return;
        BottomPillHideAnim?.Begin();
    }

    private void ZoomPresetFlyout_Closed(object sender, object e)
    {
        // 菜单关闭后：若指针仍悬停在胶囊上则保持可见，避免"菜单关了胶囊也闪一下消失、过一会又浮现"的闪烁。
        this.DispatcherQueue?.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                if (ZoomPresetFlyout != null && ZoomPresetFlyout.IsOpen) return;
                if (IsPointOverZoomPill()) return;
                BottomPillHideAnim?.Begin();
            });
    }

    /// 快速缩放菜单展开瞬间：强制缩放胶囊同步可见（二者同生共死）。
    /// 竞态：胶囊正在淡出（BottomZoomPill_PointerExited 或 _zoomPillHideTimer 已触发
    // 缩放预设浮层里的 8 颗倍率按钮：没有 x:Name，按容器遍历接管。
    // ⚠ Flyout 内容活在主可视树之外：InitializeComponent 时 ZoomPresetPanel.Children 一般已可用，
    private void AttachZoomPresetOverrides()
    {
        if (_zoomPresetOverrideAttached || ZoomPresetPanel == null) return;

        int count = ZoomPresetPanel.Children.Count;
        int attached = 0;
        // 每项四角统一倒圆角，与展开菜单外缘圆角完全同心。
        var itemRadius = new CornerRadius(3);
        for (int i = 0; i < count; i++)
        {
            if (ZoomPresetPanel.Children[i] is Button presetItem)
            {
                // 静止保持透明（菜单项语义：静止不该有独立底色），hover / pressed 才上色。
                ButtonVisualOverride.Attach(presetItem, transparentAtRest: true, inPopupTree: true);
                presetItem.CornerRadius = itemRadius;
                attached++;
            }
        }

        if (attached > 0)
        {
            // 菜单面板「独占 hover」：由面板自身的 PointerMoved 实时算指针落在哪颗内，
            // PointerExited —— 后者在 popup/flyout 输入上下文切换时常被 WinUI 吞掉（否则旧项会残留亮着）。
            ZoomPresetPanel.AddHandler(PointerMovedEvent, new PointerEventHandler(ZoomPresetPanel_PointerMoved), true);
            // 指针离开整片菜单 ⇒ 全灭（用相对坐标核对确已离开，排除「从面板背景移到子项」的假退出）。
            ZoomPresetPanel.AddHandler(PointerExitedEvent, new PointerEventHandler(ZoomPresetPanel_PointerExited), true);
        }
        _zoomPresetOverrideAttached = attached > 0;
    }

    // 菜单面板驱动预设项独占 hover：指针落在哪颗内、就只让那颗亮；落在凹缝 / 菜单外则全灭。
    private void ZoomPresetPanel_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Button? hovered = null;
        int n = ZoomPresetPanel.Children.Count;
        for (int i = 0; i < n; i++)
        {
            if (ZoomPresetPanel.Children[i] is Button b)
            {
                var p = e.GetCurrentPoint(b).Position;
                if (p.X >= 0 && p.Y >= 0 && p.X < b.ActualWidth && p.Y < b.ActualHeight)
                {
                    hovered = b;
                    break;
                }
            }
        }
        ButtonVisualOverride.SetFlyoutHover(hovered);
    }

    // ⚠ UWP/WinUI 里从面板背景移到子项也会触发面板的 PointerExited，故先用相对坐标核对指针确实已在面板外，
    private void ZoomPresetPanel_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(ZoomPresetPanel).Position;
        if (p.X >= 0 && p.Y >= 0 && p.X < ZoomPresetPanel.ActualWidth && p.Y < ZoomPresetPanel.ActualHeight)
            return;
        ButtonVisualOverride.ClearFlyoutHoverStates();
    }

    /// 整页指针移动 ⇒ 按实时几何重算所有已接管按钮的 hover（天然独占：只有指针真正落在里面的那颗亮）。
    private void PageRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ButtonVisualOverride.UpdateHoverFromPointer(e);
        ToggleButtonVisualOverride.UpdateHoverFromPointer(e);

        // 书签行：用框架给出的同树坐标，而不是 GetLivePointerPoint()（那个是 GetCursorPos 的【屏幕】坐标
        try { _bookmarkRowPointer = e.GetCurrentPoint(this).Position; } catch { }
        RefreshBookmarkRowStates();
    }

    private void ZoomPresetFlyout_Opening(object sender, object e)
    {
        AttachZoomPresetOverrides();      // 兜底：Loaded 时若 Flyout 内容尚未就绪，这里补一次
        // 每次展开都从「无任何项悬停」开始：清掉上次残留（不依赖 Loaded 时机的兜底，成本为零）。
        ButtonVisualOverride.ClearFlyoutHoverStates();

        BottomPillHideAnim?.Stop();   // 取消在途淡出，避免它与下面的淡入抢同一 Opacity 属性
        BottomPillShowAnim?.Begin();
    }

    // 缩放胶囊隐藏兜底：Flyout/light-dismiss 覆盖层关闭瞬间的输入上下文切换、快速移动等会让
    // 胶囊的 PointerExited 丢失（微软文档明确：popup/flyout 关闭时 Entered/Exited 配对不保证），
    private void ZoomPillHideTimer_Tick(object? sender, object e)
    {
        try
        {
            if (BottomZoomPillBorder == null || BottomZoomPillBorder.Opacity <= 0.05) return;
            if (ZoomPresetFlyout != null && ZoomPresetFlyout.IsOpen) return;
            if (IsPointOverZoomPill()) return;
            BottomPillHideAnim?.Begin();                                                       // 指针已离开，兜底淡出
        }
        catch { /* 窗口关闭/对象分离期间忽略 */ }
    }

    /// 实时光标是否落在右下角缩放胶囊范围内。
    /// 光标与胶囊矩形必须落在同一坐标系（窗口客户区 DIP）：
    private bool IsPointOverZoomPill()
    {
        if (!GetCursorPos(out POINT p)) return false;
        double scale = XamlRoot?.RasterizationScale ?? 1.0;

        var app = Application.Current as App;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(app?.MainWindow);
        if (hwnd == IntPtr.Zero) return false;

        // 取【客户区】原点屏幕坐标（ClientToScreen 已扣除非客户边框/标题栏）。
        // 不能用 GetWindowRect：它返回窗口外框、含约 34px 非客户上边框，会让光标 Y 系统性偏低。
        POINT clientOrigin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref clientOrigin)) return false;

        double clientX = (p.X - clientOrigin.X) / scale;
        double clientY = (p.Y - clientOrigin.Y) / scale;

        var tl = BottomZoomPill.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var rect = new Rect(tl.X, tl.Y, BottomZoomPill.ActualWidth, BottomZoomPill.ActualHeight);
        return rect.Contains(new Point(clientX, clientY));
    }

    private void CloseSecondPdf_Click(object sender, RoutedEventArgs e)
    {
        if (SplitViewToggleButton != null)
        {
            SplitViewToggleButton.IsChecked = false;
            SplitViewToggle_Click(this, new RoutedEventArgs());
        }
    }

    private async void OpenSecondPdf_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        
        var app = Application.Current as App;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(app?.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".pdf");

        StorageFile file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            await LoadSecondPdfAsync(file);
        }
    }

    private async Task LoadSecondPdfAsync(StorageFile file)
    {
        try
        {
            _pagesRight.Clear();
            if (_pdfRenderRightCts != null)
            {
                try { _pdfRenderRightCts.Cancel(); } catch { }
                _pdfRenderRightCts.Dispose();
            }
            _pdfRenderRightCts = new CancellationTokenSource();

            // Dispose any previously-loaded right engine before loading a new one.
            if (_pdfRenderServiceRight != null)
            {
                try { _pdfRenderServiceRight.Dispose(); } catch { }
                _pdfRenderServiceRight = null;
            }

            // Load the comparison PDF through the SAME MuPDF engine as the left side.
            bool rightReady = false;
            string? rightPassword = null;
            bool rightRetry = false;
            while (!rightReady)
            {
                try
                {
                    _pdfRenderServiceRight?.Dispose();
                    _pdfRenderServiceRight = new PdfRenderService();
                    await _pdfRenderServiceRight.InitializeAsync(file.Path, rightPassword);
                    rightReady = true;
                }
                catch (PdfPasswordRequiredException)
                {
                    var entered = await PromptForPasswordAsync(file.Name, rightRetry);
                    if (entered == null)
                    {
                        GpuViewerRight.Visibility = Visibility.Collapsed;
                        // 右侧回到空白（旧引擎在上面已释放）→ "+"必须常驻，否则没入口重开。
                        SetSecondaryOpenPillPinned(true);
                        return;
                    }
                    rightPassword = entered;
                    rightRetry = true;
                }
            }

            uint rightCount = await _pdfRenderServiceRight!.GetPageCountAsync();

            // Populate placeholders via the batch page-size API（一次 FFI 调用替代 N 次逐页往返，
            var rightDims = await _pdfRenderServiceRight.GetAllPageDimensionsAsync(rightCount);
            for (int i = 0; i < (int)rightCount; i++)
            {
                _pagesRight.Add(new PdfPageViewModel
                {
                    PageIndex = i,
                    PageWidth = rightDims[i * 2],
                    PageHeight = rightDims[i * 2 + 1],
                });
            }

            GpuViewerRight.Visibility = Visibility.Visible;
            await GpuViewerRight.LoadAsync(_pdfRenderServiceRight);

            // 右侧已有内容 → "+"不再常驻，恢复"悬停才淡入"，别挡着页面。
            SetSecondaryOpenPillPinned(false);

            UpdateSyncScrollBar();

            SchedulePostLoadMemoryTrim();
        }
        catch (Exception)
        {
            // 加载失败右侧多半是空的 → 让 "+"保持可见，用户能直接重试。
            SetSecondaryOpenPillPinned(GpuViewerRight.Visibility != Visibility.Visible);
        }
    }

    // ── GPU 路径标注处理器（坐标已由 GpuPageView 换算成页局部 PDF 点；

    private void GpuAnnotation_Pressed(object? sender, GpuAnnotationPointerArgs e)
    {
        if (_currentMode == EditMode.Navigate) return;
        if (e.PageIndex < 0 || e.PageIndex >= _pages.Count) return;
        var pageVm = _pages[e.PageIndex];
        var point = new Point(e.X, e.Y);

        HasUnsavedChanges = true;

        _isDrawingHighlight = true;
        _activeHighlight = new AnnotationViewModel
        {
            Type = AnnotationType.Pen,
            X = 0, Y = 0,
            ColorHex = GetActiveColorHexWithOpacity(),
            Thickness = _activeThickness
        };
        _activeHighlight.AppendPoint(point);
        pageVm.Annotations.Add(_activeHighlight);
        _annotationRedo.Clear();
        var created = _activeHighlight;
        _annotationHistory.Add(new EditorHistoryEntry(
            undo: () =>
            {
                pageVm.Annotations.Remove(created);
                HasUnsavedChanges = true;
                _ = SaveAnnotationsAsync();
            },
            redo: () =>
            {
                if (!pageVm.Annotations.Contains(created)) pageVm.Annotations.Add(created);
                HasUnsavedChanges = true;
                _ = SaveAnnotationsAsync();
            }));
        GpuViewer.InvalidateOverlayThrottled();
    }

    private void GpuAnnotation_Moved(object? sender, GpuAnnotationPointerArgs e)
    {
        if (!_isDrawingHighlight || _activeHighlight == null) return;
        // 用线程安全 AppendPoint：加锁并递增版本号 → 渲染侧缓存自动失效
        _activeHighlight.AppendPoint(new Point(e.X, e.Y));
        // 节流刷新：合并 1000Hz 鼠标高频事件，一帧最多重画一次（GPU 不再被打爆）
        GpuViewer.InvalidateOverlayThrottled();
    }

    private void GpuAnnotation_Released(object? sender, GpuAnnotationPointerArgs e)
    {
        if (_isDrawingHighlight)
        {
            _isDrawingHighlight = false;

            // Compute proper bounding box for pen strokes so that X/Y/Width/Height
            if (_activeHighlight != null && _activeHighlight.Type == AnnotationType.Pen)
            {
                // 用快照读取，保证线程安全（此时 UI 线程已不加点，但快照机制统一路径更可靠）
                var pts = _activeHighlight.GetPointsSnapshot();
                if (pts.Length > 0)
                {
                    double minX = pts[0].X, minY = pts[0].Y;
                    double maxX = pts[0].X, maxY = pts[0].Y;
                    for (int i = 1; i < pts.Length; i++)
                    {
                        if (pts[i].X < minX) minX = pts[i].X;
                        if (pts[i].Y < minY) minY = pts[i].Y;
                        if (pts[i].X > maxX) maxX = pts[i].X;
                        if (pts[i].Y > maxY) maxY = pts[i].Y;
                    }
                    // 包围盒只作元数据/侧栏校验用途（Rust 的 validate_annotation 硬性要求
                    double bx = minX < 0 ? 0 : minX;
                    double by = minY < 0 ? 0 : minY;
                    _activeHighlight.X = bx;
                    _activeHighlight.Y = by;
                    _activeHighlight.Width = Math.Max(0, maxX - bx);
                    _activeHighlight.Height = Math.Max(0, maxY - by);
                }
            }

            _activeHighlight = null;
            _ = SaveAnnotationsAsync();
            GpuViewer.InvalidateOverlayThrottled();
        }
    }

    // Ctrl+Z / Ctrl+Y：整页 KeyDown 兜底（见构造函数 AddHandler）。不再用 Page 级加速器 ——
    private void UndoRedo_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                     & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        if (e.Handled) return;
        if (!ctrl) return;
        if (e.Key == VirtualKey.Z) { RequestUndo(); e.Handled = true; }
        else if (e.Key == VirtualKey.Y) { RequestRedo(); e.Handled = true; }
    }

    // Ctrl+Z / Ctrl+Y 加速器：与兜底 KeyDown 共用幂等入口，避免同一次按键双触发。
    private void Undo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        RequestUndo();
        args.Handled = true;
    }

    private void Redo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        RequestRedo();
        args.Handled = true;
    }

    private bool _undoRedoGuard;

    private void RequestUndo()
    {
        if (_undoRedoGuard) return;
        _undoRedoGuard = true;
        UndoLastAnnotation();
        DispatcherQueue.TryEnqueue(() => _undoRedoGuard = false);
    }

    private void RequestRedo()
    {
        if (_undoRedoGuard) return;
        _undoRedoGuard = true;
        RedoLastAnnotation();
        DispatcherQueue.TryEnqueue(() => _undoRedoGuard = false);
    }

    private async void Save_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        await SaveOriginalPdfAsync();
        args.Handled = true;
    }

    private void Print_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_totalPages > 0)
            Print_Click(null, null);
        args.Handled = true;
    }

    /// 阅读区键盘导航（与焦点无关，走 KeyboardAccelerator 全局分发）。
    private void Nav_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // 焦点在文本输入 / 按钮 / 列表等原生可交互控件上时，交还原生行为
        // ⚠ 必须含 RichEditBox：白板卡片的编辑宿主就是 RichEditBox，光标被它吃掉时加速器不会触发，
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or PasswordBox or RichEditBox or ButtonBase or ComboBox
            or ListViewBase or HyperlinkButton)
            return;

        if (GpuViewer == null || _totalPages <= 0) return;

        switch (sender.Key)
        {
            case VirtualKey.Up:
            case VirtualKey.Down:
            case VirtualKey.Left:
            case VirtualKey.Right:
                GpuViewer.ScrollByArrow(sender.Key);
                break;

            case VirtualKey.Space:
                GpuViewer.ScrollViewportDown();
                break;

            case VirtualKey.PageDown:
                GpuViewer.GoToPageSmooth(_currentPageIndex + 1);
                break;

            case VirtualKey.PageUp:
                GpuViewer.GoToPageSmooth(_currentPageIndex - 1);
                break;

            case VirtualKey.Home:
                GpuViewer.GoToPage(0);
                break;
            case VirtualKey.End:
                GpuViewer.GoToPage(GpuViewer.PageCount - 1);
                break;

            default:
                return;
        }

        args.Handled = true;
    }

    /// 模式切换快捷键 V/B/N（见构造函数注册的 KeyboardAccelerator，输入管理器全局分发、与焦点无关）。
    private void ModeSwitch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or PasswordBox or RichEditBox
            or ButtonBase or ComboBox or ListViewBase or HyperlinkButton)
            return;

        // 欢迎页（尚未打开任何文档）：V/B/N 一律不响应。此时没有可读写的内容，切换模式只会让人困惑
        if (WelcomePanel.Visibility == Visibility.Visible) return;

        EditMode target;
        switch (sender.Key)
        {
            case VirtualKey.V: target = EditMode.Navigate; break;
            case VirtualKey.B: target = EditMode.Pen;      break;
            case VirtualKey.N: target = EditMode.Canvas;   break;
            default: return;
        }
        // 键盘进画笔(B)不展开工具栏/颜色胶囊（openPenOptions=false），与 V/N 纯切模式一致；
        SetEditMode(target, openPenOptions: target != EditMode.Pen);
        args.Handled = true;
    }

    private void UndoLastAnnotation()
    {
        if (_annotationHistory.Count > 0)
        {
            var last = _annotationHistory[_annotationHistory.Count - 1];
            _annotationHistory.RemoveAt(_annotationHistory.Count - 1);
            last.Undo();
            _annotationRedo.Add(last);
            GpuViewer?.InvalidateOverlay();
        }
    }

    /// <summary>重做一步（Ctrl+Y）：从重做栈弹回最近一次被撤销的操作。 可多步重做直到重做栈空；一旦产生新操作，重做栈被清空（重做分支失效）。</summary>
    private void RedoLastAnnotation()
    {
        if (_annotationRedo.Count > 0)
        {
            var last = _annotationRedo[_annotationRedo.Count - 1];
            _annotationRedo.RemoveAt(_annotationRedo.Count - 1);
            last.Redo();
            _annotationHistory.Add(last);
            GpuViewer?.InvalidateOverlay();
        }
    }

    /// <summary>批注持久化走"保存时写入 PDF 真实 /Ink 注解对象"（见 SaveOriginalPdfAsync / ExportPdfAsync）， 文件即唯一真相源。</summary>
    public Task SaveAnnotationsAsync() => Task.CompletedTask;

    /// <summary>从 PDF 文件读回 Glance 写入的真实 /Ink 注解对象，重建叠加层模型。文件即唯一真相源： 无 JSON 侧栏、无副本。"不保存退出"不再需要回滚——未保存的笔迹从未落盘，文件始终是 上次保存态，重开即读到已保存批注。</summary>
    private async Task LoadAnnotationsAsync(string pdfPath)
    {
        SuppressAnnotationDirty = true;
        try
        {
            foreach (var p in _pages) p.Annotations.Clear();
            if (string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath)) return;

            // 后台线程读 PDF 真实注解对象（MuPDF 按需打开文件读取、
            var savedList = await Task.Run(() => PdfExportService.ReadGlanceInkAnnotations(pdfPath, _currentPdfPassword));
            if (savedList == null) return;

            foreach (var saved in savedList)
            {
                if (saved.PageIndex < 0 || saved.PageIndex >= _pages.Count) continue;
                var annoVm = new AnnotationViewModel
                {
                    Type = saved.Type,
                    X = saved.X,
                    Y = saved.Y,
                    Width = saved.Width,
                    Height = saved.Height,
                    Content = saved.Content,
                    ColorHex = saved.ColorHex ?? "#F2C200",
                    Thickness = saved.Thickness > 0 ? saved.Thickness : 3.5
                };
                if (saved.Points != null && saved.Points.Count > 0)
                {
                    var pc = new Microsoft.UI.Xaml.Media.PointCollection();
                    foreach (var pt in saved.Points) pc.Add(new Point(pt.X, pt.Y));
                    annoVm.PointsCollection = pc;
                }
                _pages[saved.PageIndex].Annotations.Add(annoVm);
                // 已保存的批注不进入撤销历史：加载已完成、用户不应通过 Ctrl+Z
                // 撤销已落盘的内容（否则会误删用户上次保存的工作）。
            }
        }
        catch { }
        finally { SuppressAnnotationDirty = false; }
    }

/// <summary>白板（Canvas 模式）：从 &lt;pdf&gt;.canvas 读取卡片与连线，保留模型供交互改写， 并把当前数组交给 GpuViewer 渲染。模型即唯一真相源，每次操作后由 SaveCanvas 立即落盘。</summary>
    private void LoadCanvasForCurrentDocument()
    {
        CanvasDoc? doc = null;
        try { doc = CanvasStore.Load(_currentPdfPath); }
        catch { }
        _canvasDoc = doc ?? new CanvasDoc();
        GpuViewer.SetCanvasData(_canvasDoc.Nodes, _canvasDoc.Edges);
    }

    // ---- 白板模型与交互（P2）：数据/历史留在 MainPage，GpuPageView 只发事件 ----
    private CanvasDoc? _canvasDoc;

    private CanvasNode? FindCanvasNode(string? id)
    {
        if (_canvasDoc == null || string.IsNullOrEmpty(id)) return null;
        foreach (var n in _canvasDoc.Nodes)
            if (string.Equals(n.Id, id, StringComparison.Ordinal)) return n;
        return null;
    }

    /// <summary>把当前白板数组重新交给渲染侧（增删节点/连线后必须调用，索引需重建）。</summary>
    private void RefreshCanvas()
    {
        GpuViewer?.SetCanvasData(_canvasDoc?.Nodes, _canvasDoc?.Edges);
    }

    /// <summary>白板立即落盘（&lt;pdf&gt;.canvas 旁挂文件）。白板不写进 PDF，故不置 HasUnsavedChanges —— 退出时不会为此弹"保存 PDF"询问，避免误导。</summary>
    private void SaveCanvas()
    {
        if (_canvasDoc == null || string.IsNullOrEmpty(_currentPdfPath)) return;
        try { CanvasStore.Save(_currentPdfPath, _canvasDoc); } catch { }
    }

    private void PushCanvasHistory(Action undo, Action redo)
    {
        _annotationRedo.Clear();
        _annotationHistory.Add(new EditorHistoryEntry(undo, redo));
    }

    private static int _canvasIdSeq;
    private static string NewCanvasId() => "c" + (++_canvasIdSeq).ToString() + "_" + Guid.NewGuid().ToString("N")[..6];

    private void GpuCanvas_Dragged(object? sender, CanvasNodeEventArgs e)
    {
        var node = FindCanvasNode(e.NodeId);
        if (node == null) return;
        node.X = e.X;
        node.Y = e.Y;
        GpuViewer.InvalidateOverlayThrottled();
    }

    private void GpuCanvas_DragCompleted(object? sender, CanvasNodeEventArgs e)
    {
        var node = FindCanvasNode(e.NodeId);
        if (node == null) return;
        double nx = node.X, ny = node.Y;
        double ox = e.OrigX, oy = e.OrigY;
        if (Math.Abs(nx - ox) < 0.5 && Math.Abs(ny - oy) < 0.5) return;
        PushCanvasHistory(
            undo: () => { node.X = ox; node.Y = oy; RefreshCanvas(); },
            redo: () => { node.X = nx; node.Y = ny; RefreshCanvas(); });
        SaveCanvas();
        RefreshCanvas();
    }

    /// <summary>选中卡片 = 提升到叠放顶层。jsoncanvas 无独立 z 字段，nodes 数组顺序即 z 序， 故"置顶"就是把该节点移到数组末尾。写回模型并落盘 ⇒ 取消选中后层级保持，不会掉回出生顺序。 不入撤销栈：选中是高频操作，入栈会淹没真正可撤销的编辑。</summary>
    private void GpuCanvas_BringToFront(object? sender, CanvasNodeEventArgs e)
    {
        if (_canvasDoc == null) return;
        var nodes = _canvasDoc.Nodes;
        int i = nodes.FindIndex(n => string.Equals(n.Id, e.NodeId, StringComparison.Ordinal));
        if (i < 0 || i == nodes.Count - 1) return;
        var node = nodes[i];
        nodes.RemoveAt(i);
        nodes.Add(node);
        SaveCanvas();
        RefreshCanvas();
    }

    private void GpuCanvas_CreateRequested(object? sender, CanvasNodeEventArgs e)
    {
        if (_canvasDoc == null) return;
        if (e.PageIndex < 0 || e.PageIndex >= _pages.Count) return;
        var doc = _canvasDoc;
        var node = new CanvasNode
        {
            Id = NewCanvasId(),
            GlancePage = e.PageIndex,
            X = e.X,
            Y = e.Y,
            Width = CanvasNode.DefaultWidth,
            Height = CanvasNode.DefaultHeight,
            Text = ""
        };
        doc.Nodes.Add(node);
        PushCanvasHistory(
            undo: () => { doc.Nodes.Remove(node); RefreshCanvas(); },
            redo: () => { if (!doc.Nodes.Contains(node)) doc.Nodes.Add(node); RefreshCanvas(); });
        SaveCanvas();
        RefreshCanvas();
        GpuViewer.SelectCanvasNode(node.Id);
        GpuViewer.BeginCanvasEdit(node.Id);
    }

    private void GpuCanvas_DeleteRequested(object? sender, CanvasNodeEventArgs e)
    {
        var node = FindCanvasNode(e.NodeId);
        if (node == null || _canvasDoc == null) return;
        var doc = _canvasDoc;
        int idx = doc.Nodes.IndexOf(node);
        // 连带删除挂在它身上的连线（撤销时一并还原，避免残留悬空连线）。
        var removedEdges = new List<CanvasEdge>();
        for (int i = doc.Edges.Count - 1; i >= 0; i--)
        {
            var ed = doc.Edges[i];
            if (string.Equals(ed.FromNode, node.Id, StringComparison.Ordinal) ||
                string.Equals(ed.ToNode, node.Id, StringComparison.Ordinal))
            {
                removedEdges.Add(ed);
                doc.Edges.RemoveAt(i);
            }
        }
        doc.Nodes.Remove(node);
        PushCanvasHistory(
            undo: () =>
            {
                if (!doc.Nodes.Contains(node))
                    doc.Nodes.Insert(Math.Min(idx, doc.Nodes.Count), node);
                foreach (var ed in removedEdges)
                    if (!doc.Edges.Contains(ed)) doc.Edges.Add(ed);
                RefreshCanvas();
            },
            redo: () =>
            {
                doc.Nodes.Remove(node);
                foreach (var ed in removedEdges) doc.Edges.Remove(ed);
                RefreshCanvas();
            });
        SaveCanvas();
        RefreshCanvas();
    }

    private void GpuCanvas_TextCommitted(object? sender, CanvasNodeEventArgs e)
    {
        var node = FindCanvasNode(e.NodeId);
        if (node == null) return;
        string oldText = node.Text ?? "";
        string newText = e.Text ?? "";
        if (string.Equals(oldText, newText, StringComparison.Ordinal)) return;
        node.Text = newText;
        PushCanvasHistory(
            undo: () => { node.Text = oldText; RefreshCanvas(); },
            redo: () => { node.Text = newText; RefreshCanvas(); });
        SaveCanvas();
        GpuViewer.InvalidateOverlay();
    }

    /// <summary>连线建立（P3）：由 GpuViewer 从手柄拖到另一张卡片后发出。 源边来自手柄，目标边为吸附结果；两端节点可跨页（渲染侧各自经所属页变换映射）。</summary>
    private void GpuCanvas_ConnectionRequested(object? sender, CanvasNodeEventArgs e)
    {
        if (_canvasDoc == null) return;
        var from = FindCanvasNode(e.NodeId);
        var to = FindCanvasNode(e.TargetNodeId);
        if (from == null || to == null || ReferenceEquals(from, to)) return;
        var doc = _canvasDoc;
        // 去重：仅当【四元组】(fromNode, fromSide, toNode, toSide) 完全相同才视为重复，与 Obsidian 一致。
        // 故不能只比 fromSide，必须把 toSide 一并纳入判据。
        foreach (var ex in doc.Edges)
        {
            if (string.Equals(ex.FromNode, from.Id, StringComparison.Ordinal) &&
                string.Equals(ex.ToNode, to.Id, StringComparison.Ordinal) &&
                string.Equals(ex.FromSide ?? "", e.FromSide ?? "", StringComparison.Ordinal) &&
                string.Equals(ex.ToSide ?? "", e.ToSide ?? "", StringComparison.Ordinal))
                return;
        }
        var edge = new CanvasEdge
        {
            Id = NewCanvasId(),
            FromNode = from.Id,
            FromSide = string.IsNullOrEmpty(e.FromSide) ? null : e.FromSide,
            ToNode = to.Id,
            ToSide = string.IsNullOrEmpty(e.ToSide) ? null : e.ToSide,
            ToEnd = "arrow",
        };
        doc.Edges.Add(edge);
        PushCanvasHistory(
            undo: () => { doc.Edges.Remove(edge); RefreshCanvas(); },
            redo: () => { if (!doc.Edges.Contains(edge)) doc.Edges.Add(edge); RefreshCanvas(); });
        SaveCanvas();
        RefreshCanvas();
    }

    private void GpuCanvas_Resized(object? sender, CanvasNodeEventArgs e)
    {
        var node = FindCanvasNode(e.NodeId);
        if (node == null) return;
        node.X = e.X;
        node.Y = e.Y;
        node.Width = e.Width;
        node.Height = e.Height;
        GpuViewer.InvalidateOverlayThrottled();
    }

    private void GpuCanvas_ResizeCompleted(object? sender, CanvasNodeEventArgs e)
    {
        var node = FindCanvasNode(e.NodeId);
        if (node == null) return;
        double nx = node.X, ny = node.Y, nw = node.Width, nh = node.Height;
        double ox = e.OrigX, oy = e.OrigY, ow = e.OrigWidth, oh = e.OrigHeight;
        if (Math.Abs(nx - ox) < 0.5 && Math.Abs(ny - oy) < 0.5 &&
            Math.Abs(nw - ow) < 0.5 && Math.Abs(nh - oh) < 0.5) return;
        PushCanvasHistory(
            undo: () => { node.X = ox; node.Y = oy; node.Width = ow; node.Height = oh; RefreshCanvas(); },
            redo: () => { node.X = nx; node.Y = ny; node.Width = nw; node.Height = nh; RefreshCanvas(); });
        SaveCanvas();
        RefreshCanvas();
    }

    private void GpuCanvas_EdgeDeleteRequested(object? sender, CanvasNodeEventArgs e)
    {
        if (_canvasDoc == null) return;
        var doc = _canvasDoc;
        CanvasEdge? edge = null;
        foreach (var ed in doc.Edges)
            if (string.Equals(ed.Id, e.EdgeId, StringComparison.Ordinal)) { edge = ed; break; }
        if (edge == null) return;
        int idx = doc.Edges.IndexOf(edge);
        doc.Edges.Remove(edge);
        PushCanvasHistory(
            undo: () => { if (!doc.Edges.Contains(edge)) doc.Edges.Insert(Math.Min(idx, doc.Edges.Count), edge); RefreshCanvas(); },
            redo: () => { doc.Edges.Remove(edge); RefreshCanvas(); });
        SaveCanvas();
        RefreshCanvas();
    }

    private void GpuCanvas_EdgeStyleChanged(object? sender, CanvasNodeEventArgs e)
    {
        if (_canvasDoc == null) return;
        CanvasEdge? edge = null;
        foreach (var ed in _canvasDoc.Edges)
            if (string.Equals(ed.Id, e.EdgeId, StringComparison.Ordinal)) { edge = ed; break; }
        if (edge == null) return;

        string? oldFromEnd = edge.FromEnd, oldToEnd = edge.ToEnd, oldColor = edge.Color;
        if (e.ArrowStyle != null)
        {
            switch (e.ArrowStyle)
            {
                case "none": edge.FromEnd = null; edge.ToEnd = "none"; break;
                case "both": edge.FromEnd = "arrow"; edge.ToEnd = "arrow"; break;
                default: edge.FromEnd = null; edge.ToEnd = "arrow"; break;
            }
        }
        if (e.EdgeColor != null)
            edge.Color = string.IsNullOrEmpty(e.EdgeColor) ? null : e.EdgeColor;

        if (string.Equals(oldFromEnd, edge.FromEnd, StringComparison.Ordinal) &&
            string.Equals(oldToEnd, edge.ToEnd, StringComparison.Ordinal) &&
            string.Equals(oldColor, edge.Color, StringComparison.Ordinal))
            return;

        string? nFromEnd = edge.FromEnd, nToEnd = edge.ToEnd, nColor = edge.Color;
        PushCanvasHistory(
            undo: () => { edge.FromEnd = oldFromEnd; edge.ToEnd = oldToEnd; edge.Color = oldColor; RefreshCanvas(); },
            redo: () => { edge.FromEnd = nFromEnd; edge.ToEnd = nToEnd; edge.Color = nColor; RefreshCanvas(); });
        SaveCanvas();
        RefreshCanvas();
    }

    /// <summary>改线：只改连线的节点/边（FromNode/FromSide 或 ToNode/ToSide），FromEnd/ToEnd/Color 等箭头样式原样保留。 空白删除走 CanvasEdgeDeleteRequested，不在本事件内发生。</summary>
    private void GpuCanvas_EdgeRerouteRequested(object? sender, CanvasNodeEventArgs e)
    {
        if (_canvasDoc == null) return;
        CanvasEdge? edge = null;
        foreach (var ed in _canvasDoc.Edges)
            if (string.Equals(ed.Id, e.EdgeId, StringComparison.Ordinal)) { edge = ed; break; }
        if (edge == null) return;
        string oldNode = e.RerouteFromEnd ? edge.FromNode : edge.ToNode;
        string? oldSide = e.RerouteFromEnd ? edge.FromSide : edge.ToSide;
        string newNode = e.TargetNodeId;
        string? newSide = string.IsNullOrEmpty(e.ToSide) ? null : e.ToSide;
        // 无变化（含改接回同一节点同一边）→ 不入历史。
        if (string.Equals(oldNode, newNode, StringComparison.Ordinal) &&
            string.Equals(oldSide ?? "", newSide ?? "", StringComparison.Ordinal))
            return;
        var ed2 = edge;
        if (e.RerouteFromEnd) { ed2.FromNode = newNode; ed2.FromSide = newSide; }
        else { ed2.ToNode = newNode; ed2.ToSide = newSide; }
        PushCanvasHistory(
            undo: () =>
            {
                if (e.RerouteFromEnd) { ed2.FromNode = oldNode; ed2.FromSide = oldSide; }
                else { ed2.ToNode = oldNode; ed2.ToSide = oldSide; }
                RefreshCanvas();
            },
            redo: () =>
            {
                if (e.RerouteFromEnd) { ed2.FromNode = newNode; ed2.FromSide = newSide; }
                else { ed2.ToNode = newNode; ed2.ToSide = newSide; }
                RefreshCanvas();
            });
        SaveCanvas();
        RefreshCanvas();
    }

    private void GpuCanvas_NodeColorChanged(object? sender, CanvasNodeEventArgs e)
    {
        if (_canvasDoc == null) return;
        var node = FindCanvasNode(e.NodeId);
        if (node == null) return;
        if (e.NodeColor == null) return;
        string oldColor = node.Color ?? "";
        if (string.Equals(oldColor, e.NodeColor, StringComparison.Ordinal)) return;
        string newColor = e.NodeColor;
        PushCanvasHistory(
            undo: () => { node.Color = oldColor; RefreshCanvas(); },
            redo: () => { node.Color = newColor; RefreshCanvas(); });
        node.Color = newColor;
        SaveCanvas();
        RefreshCanvas();
    }

    private void GpuCanvas_CreateConnectedRequested(object? sender, CanvasNodeEventArgs e)
    {
        if (_canvasDoc == null) return;
        if (e.PageIndex < 0 || e.PageIndex >= _pages.Count) return;
        var doc = _canvasDoc;
        var src = FindCanvasNode(e.NodeId);
        var node = new CanvasNode
        {
            Id = NewCanvasId(),
            GlancePage = e.PageIndex,
            X = e.X,
            Y = e.Y,
            Width = CanvasNode.DefaultWidth,
            Height = CanvasNode.DefaultHeight,
            Text = ""
        };
        doc.Nodes.Add(node);
        CanvasEdge? edge = null;
        if (src != null)
        {
            edge = new CanvasEdge
            {
                Id = NewCanvasId(),
                FromNode = src.Id,
                FromSide = string.IsNullOrEmpty(e.FromSide) ? null : e.FromSide,
                ToNode = node.Id,
                ToEnd = "arrow",
            };
            doc.Edges.Add(edge);
        }
        PushCanvasHistory(
            undo: () =>
            {
                doc.Nodes.Remove(node);
                if (edge != null) doc.Edges.Remove(edge);
                RefreshCanvas();
            },
            redo: () =>
            {
                if (!doc.Nodes.Contains(node)) doc.Nodes.Add(node);
                if (edge != null && !doc.Edges.Contains(edge)) doc.Edges.Add(edge);
                RefreshCanvas();
            });
        SaveCanvas();
        RefreshCanvas();
        GpuViewer.SelectCanvasNode(node.Id);
        GpuViewer.BeginCanvasEdit(node.Id);
    }

    /// <summary>判定指定文件是否可被覆盖写入。加密文档由调用方另行处理；此处覆盖两类"不能覆盖保存"的普通情形： ① NTFS 只读属性；② 无写权限（属性未标只读但写探测失败，如受限目录）。两者任一成立即不可覆盖。</summary>
    private static bool IsFileOverwritable(string path)
    {
        try
        {
            if (!File.Exists(path)) return true; // 不存在 → 保存时由所在目录决定，此处无法判定，乐观视为可覆盖
            if ((File.GetAttributes(path) & System.IO.FileAttributes.ReadOnly) != 0) return false;
            using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>"不保存退出"：批注只写进 PDF 文件（文件即真相源），未保存的笔迹从未落盘，文件始终是 "上次保存态"。因此放弃未保存更改无需回滚任何数据——清空内存叠加层、复位脏标记即可， 下次打开自然读到已保存批注（加密/只读走另存为、普通可写走原地保存，行为一致）。</summary>
    public void DiscardUnsavedOnExit()
    {
        try
        {
            foreach (var p in _pages) p.Annotations.Clear();
        }
        catch { }
        HasUnsavedChanges = false;
    }

}

public sealed class OutlineItemViewModel
{
    public string Title { get; set; } = "";
    public int PageIndex { get; set; } = -1;   // -1 if the destination could not be resolved
    public double Indent { get; set; }          // left padding in px reflecting heading depth
}

public sealed class BookmarkViewModel : INotifyPropertyChanged
{
    private string _displayName = "";
    private string _editText = "";
    private bool _isEditing;

    public int PageIndex { get; set; }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (SetProperty(ref _isEditing, value))
            {
                OnPropertyChanged(nameof(IsReadOnly));
            }
        }
    }

    public bool IsReadOnly => !IsEditing;

    public string EditText
    {
        get => _editText;
        set => SetProperty(ref _editText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class PdfPageViewModel : INotifyPropertyChanged
{
    private double _pageWidth;
    private double _pageHeight;
    private double _rotationAngle = 0.0;
    private int _pageIndex;
    private ImageSource? _thumbnailSource = null;
    private Microsoft.UI.Xaml.Media.Brush? _annotationsBackground = null;
    private bool _isBookmarked;

    // 侧栏缩略图行内书签星的两态：灰边空心(E734) / 黄实心(E735)。
    public bool IsBookmarked
    {
        get => _isBookmarked;
        set
        {
            if (SetProperty(ref _isBookmarked, value)) OnPropertyChanged(nameof(IsNotBookmarked));
        }
    }

    public bool IsNotBookmarked => !_isBookmarked;

    public Microsoft.UI.Xaml.Media.Brush? AnnotationsBackground
    {
        get => _annotationsBackground;
        set => SetProperty(ref _annotationsBackground, value);
    }

    public ImageSource? ThumbnailSource
    {
        get => _thumbnailSource;
        set => SetProperty(ref _thumbnailSource, value);
    }

    public int PageIndex
    {
        get => _pageIndex;
        set
        {
            if (SetProperty(ref _pageIndex, value))
            {
                OnPropertyChanged(nameof(PageNumberText));
            }
        }
    }
    
    public double PageWidth
    {
        get => _pageWidth;
        set => SetProperty(ref _pageWidth, value);
    }

    public double PageHeight
    {
        get => _pageHeight;
        set => SetProperty(ref _pageHeight, value);
    }

    public double RotationAngle
    {
        get => _rotationAngle;
        set => SetProperty(ref _rotationAngle, value);
    }

    public string PageNumberText => $"{PageIndex + 1}";
    public ObservableCollection<AnnotationViewModel> Annotations { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class SavedPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}

public class SavedAnnotation
{
    public int PageIndex { get; set; }
    public AnnotationType Type { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string Content { get; set; } = "";
    public string ColorHex { get; set; } = "#F2C200";
    public List<SavedPoint> Points { get; set; } = new();
    public double Thickness { get; set; } = 3.5;
    public double RotationAngle { get; set; } = 0.0;
}

public class AnnotationViewModel : INotifyPropertyChanged
{
    private double _x;
    private double _y;
    private double _width;
    private double _height;
    private string _content = "";
    private string _colorHex = "#F2C200";
    private double _thickness = 3.5;
    private PointCollection _pointsCollection = new();

    // ============== 线程安全与缓存字段（修复 GPU 画笔闪退 & 高占用） ==============
    // 必须加锁 + 快照，否则枚举被修改 / 数组半扩容读野指针 → AccessViolation 闪退。
    internal readonly object PointsLock = new();
    private int _pointsVersion;      // 每次修改 PointsCollection 时递增（渲染线程据此判断缓存是否过期）
    private Point[]? _pointsSnapshot;// 上次快照副本（渲染线程直接用，不怕并发修改）
    private int _snapshotVersion;

    internal int PointsVersion
    {
        get { lock (PointsLock) return _pointsVersion; }
    }

    /// <summary>线程安全地获取当前 Points 的快照副本（供渲染线程使用）。 若版本号未变则复用上次快照，避免每帧 new 数组。</summary>
    internal Point[] GetPointsSnapshot()
    {
        lock (PointsLock)
        {
            if (_pointsSnapshot != null && _snapshotVersion == _pointsVersion)
                return _pointsSnapshot;
            int n = _pointsCollection.Count;
            var arr = new Point[n];
            for (int i = 0; i < n; i++) arr[i] = _pointsCollection[i];
            _pointsSnapshot = arr;
            _snapshotVersion = _pointsVersion;
            return arr;
        }
    }

    /// <summary>向画笔追加一个点（仅 UI 线程调用）。自动递增版本号，使渲染侧缓存失效。</summary>
    internal void AppendPoint(Point pt)
    {
        lock (PointsLock)
        {
            _pointsCollection.Add(pt);
            _pointsVersion++;
            // 快照不立即重建：渲染线程首次 GetPointsSnapshot 时懒重建即可，省一次数组拷贝
        }
    }

    public AnnotationType Type { get; set; }

    public double X
    {
        get => _x;
        set => SetProperty(ref _x, value);
    }

    public double Y
    {
        get => _y;
        set => SetProperty(ref _y, value);
    }

    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    public double Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }

    public string Content
    {
        get => _content;
        set
        {
                if (SetProperty(ref _content, value))
                {
                    if (!MainPage.SuppressAnnotationDirty && MainPage.Current != null)
                    {
                        MainPage.Current.HasUnsavedChanges = true;
                    }
                    if (!MainPage.SuppressAnnotationDirty)
                        _ = MainPage.Current?.SaveAnnotationsAsync();
                }
        }
    }

    public string ColorHex
    {
        get => _colorHex;
        set
        {
                if (SetProperty(ref _colorHex, value))
                {
                    if (!MainPage.SuppressAnnotationDirty && MainPage.Current != null)
                    {
                        MainPage.Current.HasUnsavedChanges = true;
                    }
                    if (!MainPage.SuppressAnnotationDirty)
                        _ = MainPage.Current?.SaveAnnotationsAsync();
                }
        }
    }

    public PointCollection PointsCollection
    {
        get => _pointsCollection;
        set
        {
            lock (PointsLock)
            {
                if (SetProperty(ref _pointsCollection, value))
                {
                    _pointsVersion++;
                    _pointsSnapshot = null;
                    _snapshotVersion = -1;
                }
            }
        }
    }

    public double Thickness
    {
        get => _thickness;
        set
        {
                if (SetProperty(ref _thickness, value))
                {
                    if (!MainPage.SuppressAnnotationDirty && MainPage.Current != null)
                    {
                        MainPage.Current.HasUnsavedChanges = true;
                    }
                    if (!MainPage.SuppressAnnotationDirty)
                        _ = MainPage.Current?.SaveAnnotationsAsync();
                }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(storage, value)) return false;
        
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}