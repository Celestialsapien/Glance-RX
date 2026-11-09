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

    // 未落盘改动：笔迹与卡片都在画布文件里，所以只有这一个来源。
    private bool _canvasDirty;
    // 保存流程重入闸门：同一次按键可能被多个入口各处理一次（工具栏按钮 + 加速器 + 关窗流程），
    // 而只读/加密文档的保存要弹"另存为"文件框 —— 两次并发就会同时冒出两个框。
    // 后来者拿到的是正在跑的那个 Task：它们等同一个结果，而不是各自再写一遍文件。
    private Task<bool>? _saveTask;

    // 批量加载（从磁盘读回）标注时临时抑制"脏标记 + 自动保存"：对象初始化器会触发 AnnotationViewModel
    // 否则从磁盘读回"已保存"的标注会被误判成未保存更改，导致即使本次没画、关闭也弹保存询问。
    internal static bool SuppressDirtyMarking { get; set; }

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
    private SettingsService _settingsService = new();
    private bool _toolbarAlwaysVisible;
    private uint _totalPages = 0;

    private readonly ObservableCollection<BookmarkViewModel> _bookmarks = new();
    private readonly ObservableCollection<OutlineItemViewModel> _outlineItems = new();
    // 白板模式列表：只映射"当前画布里有页归属、且页码落在本文档内"的卡片（跳过去能看见的那些）。
    private readonly ObservableCollection<BoardCardViewModel> _boardCards = new();
    private enum SidebarMode { Thumbnails, Bookmarks, Outline, Board }
    private SidebarMode _currentSidebarMode = SidebarMode.Thumbnails;
    // （侧边栏开关的阅读位置补偿已整体移到 GpuPageView：CaptureHostResizeAnchor / BeginHostResizeReanchor。

    private double _currentZoomPercent = 100;
    
    private EditMode _currentMode = EditMode.Navigate;
    private bool _isDrawingHighlight = false;
    private AnnotationViewModel? _activeHighlight;

    /// <summary>笔画的最小收点间距（页局部 PDF 点）。0.6pt 在 100% 缩放下不到 1 像素：
    /// 慢写时手在原地的抖动会被滤掉，快写几乎不丢点，于是点数 ≈ 线条长度 ÷ 该值，
    /// 而不是跟着采样频率走。只决定"这个点收不收"，从不改动已收的点。</summary>
    private const double InkMinPointGap = 0.6;

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

    /// <summary>下一条笔的全局落笔序号（见 <see cref="AnnotationViewModel.ZOrder"/>）。换画布/重开文档时
    /// 从 0 重排，不跨文档累加 —— 它只用于同一份画布内的层叠先后，值本身没有其它含义。</summary>
    private int _inkZSeq;

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
        public DispatcherTimer? Timer;   // 常驻胶囊没有计时器（不会自己消失）
        public Storyboard? SlideSb;   // 进行中的位移动画，重定位前先停，避免同属性双动画冲突
        public int Pos;

        /// <summary>常驻（不会自己消失、点了也不消失、不参与栈深淘汰）。画布缺失提示用。</summary>
        public bool Sticky;
    }
    private readonly List<SaveToastItem> _saveToasts = new();

    /// <summary>左下角那枚常驻的"画布有缺失"胶囊（null = 没有）。与普通提示同处 <see cref="_saveToasts"/> 栈里。</summary>
    private SaveToastItem? _canvasMissingToast;

    /// <summary>构造常驻胶囊时的主题（深/浅）。图标色是按主题算死的，换主题就得重建。</summary>
    private bool _canvasMissingToastDark;

    /// <summary>左下角那枚常驻的"画布在库外"胶囊（null = 没有）。当前挂着的画布落在画布库树之外时立起来：
    /// 它只可能来自"库写不进去 → 另存为到别处"。画布照常挂载、可编辑、下次打开还是它，
    /// 但右侧面板只列库树里的文件 ⇒ 面板里没有它那一行，不给提示用户会以为画布丢了。</summary>
    private SaveToastItem? _canvasOutsideToast;

    /// <summary>同上：图标色按主题算死，换主题要重建。</summary>
    private bool _canvasOutsideToastDark;
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
        // 点到行内改名框【以外】的任何地方 = 提交这次改名。见 OnPagePointerPressedCommitRename：
        // 列表行不是可聚焦元素，点它不会移走焦点，只靠 LostFocus 会有"点哪儿都退不出去"的情况。
        this.AddHandler(PointerPressedEvent, new PointerEventHandler(OnPagePointerPressedCommitRename), true);

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
        BoardListView.ItemsSource = _boardCards;

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
        // 右侧画布面板：同一套"不改变阅读位置"的重锚 + 顶部让位（两者共用 UpdateSidebarTopInset）。
        CanvasPanelSplitView.PaneOpening += (s, e) => QueueSidebarReanchor();
        CanvasPanelSplitView.PaneClosing += (s, e) => QueueSidebarReanchor();
        CanvasPanelSplitView.PaneOpened += (s, e) => UpdateSidebarTopInset();
        // 主题切换（设置面板选择/跟随系统变化）时刷新画笔胶囊的选中提示色，
        this.ActualThemeChanged += (s, e) => RefreshPenOptionThemeHighlights();
        this.ActualThemeChanged += (s, e) => UpdateSidebarBackground();
        // 构造期首帧即上色（ActualThemeChanged 不保证首帧触发）；不依赖 XAML 主题键，规避主题切换错乱根因。
        UpdateSidebarBackground();
        // 画布面板的静态文案在这里落一次；树内容随"打开文档"刷新（见 RefreshCanvasTree）。
        if (CanvasPanelHeaderText != null) CanvasPanelHeaderText.Text = LocalizationService.Get("CanvasPanelTitle");
        // 图标按钮不写 Content：文案只做无障碍名（不挂悬浮气泡），颜色/光效全交给主题接管器。
        if (DetachCanvasButton != null)
            AutomationProperties.SetName(DetachCanvasButton, LocalizationService.Get("CanvasDetach"));
        // 列表的数据源恒为 _canvasRows（拍平后的可见行）：增删都靠差分就地改这个集合，容器与滚动位置才保得住。
        if (CanvasRowList != null) CanvasRowList.ItemsSource = _canvasRows;
        // 拖拽的移动/抬起一律挂在【列表】上，不挂在行上：整个拖拽期间行容器可能被刷新重建，
        // 抓在行上会在中途断线。handledEventsToo=true 是因为内部 ScrollViewer 会处理掉一部分指针事件
        // （触摸拖动滚动时 PointerMoved 是 Handled 的），而"移出门槛即作废长按"恰恰要靠这些移动事件。
        if (CanvasRowList != null)
        {
            CanvasRowList.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(CanvasRowList_PointerMoved), true);
            CanvasRowList.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(CanvasRowList_PointerReleased), true);
            CanvasRowList.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(CanvasRowList_PointerCaptureLost), true);
        }
        // 展开状态跨会话恢复：先把上次离开时的展开集合装回来，第一次 BuildTree 就直接是那个样子。
        foreach (string folder in _settingsService.CanvasExpandedFolders)
        {
            if (!string.IsNullOrWhiteSpace(folder)) _expandedCanvasFolders.Add(folder);
        }
        // 菜单关掉后清掉"为了菜单而保留的"悬浮态（菜单开着时故意不清，见 CanvasRow_PointerExited）。
        if (CanvasContextMenu != null)
            CanvasContextMenu.Closed += (s, e) => { _hoverCanvasNode = null; UpdateCanvasRowBackgrounds(); };
        SetupCanvasWatcher();
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
        var color = isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A)
            : Windows.UI.Color.FromArgb(0xFF, 0xEA, 0xEA, 0xEA);
        ThumbnailSidebarPanel.Background = new SolidColorBrush(color);
        // 右侧画布面板用独立画刷实例（不共享）：共享同一实例在主题切换瞬间会让两个面板一起跳色。
        if (CanvasPanelRoot != null) CanvasPanelRoot.Background = new SolidColorBrush(color);
        // 资源管理器一行的底色。同一支画刷实例改 Color ⇒ 所有行同步跟随，不必重建树。
        // 悬浮/按下：悬浮比面板底色亮一档，按下比悬浮暗一档。选中另有两档（单选灰 / 多选紫），见下。
        _canvasRowHoverBrush = SetCanvasRowBrush("CanvasRowHoverBrush", isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x34, 0x34, 0x34)
            : Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2));
        _canvasRowPressedBrush = SetCanvasRowBrush("CanvasRowPressedBrush", isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x2E, 0x2E, 0x2E)
            : Windows.UI.Color.FromArgb(0xFF, 0xE0, 0xE0, 0xE0));
        // 激活态用行首竖条表达（不占底色）：强调紫与"多选淡染"是两套视觉，互不冒充。
        // 竖条那支画刷由行模板用 {StaticResource} 直接引用，这里只改它的 Color，不需要缓存实例。
        SetCanvasRowBrush("CanvasRowActiveBarBrush", isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0xA8, 0x82, 0xFF)
            : Windows.UI.Color.FromArgb(0xFF, 0x98, 0x73, 0xF7));
        // 单选底：普通点击选中的那一行。它只表达"这一行被选中"，不含激活态 ——
        // 激活态走行首竖条，于是"点 A"会同时出现灰底（选中）与竖条（开着），两者互不顶替。
        _canvasRowSelectedBrush = SetCanvasRowBrush("CanvasRowSelectedBrush", isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x3E, 0x42)
            : Windows.UI.Color.FromArgb(0xFF, 0xFB, 0xFB, 0xFB));
        // 多选底 = 强调紫（#A882FF / #9873F7）以约 22% 压到面板底色上，深色下再压暗一点：
        // 直接铺满强调色会把行文字吃掉，这里的角色是"淡染",要和单选底的中性浅色一眼分得开。
        _canvasRowMultiSelectBrush = SetCanvasRowBrush("CanvasRowMultiSelectBrush", isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x49, 0x40, 0x5F)
            : Windows.UI.Color.FromArgb(0xFF, 0xD8, 0xD0, 0xED));
        // 文字色：默认前景色（深色档 = 纯白）在整列铺开时刺眼，夜间尤其。
        // 亮度阶梯按"谁能抢眼"排：激活行名字 > 列表项名称 > 非激活行名字 > 附属信息。
        // 激活与非激活之间可以拉得很开，因为整屏只有一行激活 —— 纯白给一行是强调，给一列才是刺眼。
        SetCanvasRowBrush("SidebarTextBrush", isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0xC9, 0xC9, 0xD1)
            : Windows.UI.Color.FromArgb(0xFF, 0x20, 0x20, 0x26));
        SetCanvasRowBrush("SidebarTextMutedBrush", isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x8E, 0x8E, 0x97)
            : Windows.UI.Color.FromArgb(0xFF, 0x6B, 0x6B, 0x74));
        _canvasRowTextBrush = SetCanvasRowBrush("CanvasRowTextBrush", isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0x9A, 0x9A, 0xA2)
            : Windows.UI.Color.FromArgb(0xFF, 0x60, 0x60, 0x6A));
        _canvasRowTextActiveBrush = SetCanvasRowBrush("CanvasRowTextActiveBrush", isDark
            ? Windows.UI.Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFC)
            : Windows.UI.Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0E));
        // 换色后所有行的底色引用不变（同一支实例），但要重算一次：选中/激活的判定要重新落到新实例上。
        UpdateCanvasRowBackgrounds();
        // 画布缺失胶囊的图标色是按主题算出来的（见 BuildToastCapsule），主题变了要按新主题重建一枚。
        SyncCanvasMissingToast();
        // "画布在库外"同理（它在内部自己处理主题重建，条件不满足时什么也不做）。
        SyncCanvasOutsideRootToast();
        // "画布改动未保存"同理：图标色在构造时算死，换主题要重建（条件满足才会真的建）。
        if (_canvasUnsavedToast != null)
        {
            SaveToastItem stale = _canvasUnsavedToast;
            _canvasUnsavedToast = null;
            DismissToast(stale, animate: false);
            ShowCanvasUnsavedToast();
        }
    }

    /// <summary>改资源里那支画刷的颜色并把它交回调用方缓存 —— 行底色每次都去 Resources 查一遍太浪费，
    /// 而换主题只改 Color、不换实例，缓存因此不会失效。</summary>
    private SolidColorBrush? SetCanvasRowBrush(string key, Windows.UI.Color color)
    {
        if (!Resources.TryGetValue(key, out object? value) || value is not SolidColorBrush brush) return null;
        brush.Color = color;
        return brush;
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
            // 行内改名：Esc = 放弃这次改名（不落盘）。放在最前 —— 改名态下其它 Esc 语义都不该抢。
            if (FindRenamingNode() is { } renaming) { renaming.IsRenaming = false; e.Handled = true; }
            // 白板连线拖拽中按 Esc → 取消本次连线（松手落空同样会取消，Esc 只是更快的退出手感）。
            // ⚠ 不接管"按住波浪键的待接态"：那是白板内部状态，取消由 GpuPageView 自己管（松开它 / 同卡再点 / 左键点空白）。
            else if (GpuViewer.CancelCanvasConnection()) e.Handled = true;
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
        // 新增的模式键必须逐颗挂上：接管器是按元素注册的，不挂就只剩系统默认外观，
        // 既没有 hover / pressed 光效，也没有 checked 的主题紫（其余三颗都在上面）。
        ToggleButtonVisualOverride.Attach(ModeBoardButton);
        // 标题行黄星复用了模式键的 ToggleButton + 同一套样式，故也走同一个接管器：
        // 底色 / 描边 / hover / pressed 逐档同源（IsChecked 恒 false ⇒ 不会亮成模式选中态）。
        ToggleButtonVisualOverride.Attach(AddBookmarkButton);
        // 右栏标题行的「卸下」同样与模式键同源，只是尺寸小（20 高）；不再走 transparentAtRest 的 ghost 档。
        // 它默认 Collapsed，模板根要等可见后才实例化；静止色由样式的 ThemeResource 兜住，
        // 第一次指针 / 点击事件触发 Sync 时接管器会重新解析模板根并接管 hover / pressed。
        ToggleButtonVisualOverride.Attach(DetachCanvasButton);
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
        // 右侧画布面板按钮与左侧边栏按钮完全同源：同样的 hover / pressed 光效 + accent 紫选中态。
        ToggleButtonVisualOverride.Attach(ToggleCanvasPanelButton);
        ButtonVisualOverride.Attach(PrevPageButton);
        ButtonVisualOverride.Attach(NextPageButton);
        ButtonVisualOverride.Attach(RotateButton);
        ButtonVisualOverride.Attach(LayoutToggleButton);
        ButtonVisualOverride.Attach(SaveButton);
        ButtonVisualOverride.Attach(PrintButton);
        ButtonVisualOverride.Attach(SettingsButton);
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

            // 图标按钮只留无障碍名：本项目所有按钮都不挂悬浮气泡。
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
        // 显隐模式改变的是「主视图要不要让出顶带」，与工具栏几何无关，必须在这里显式重算两条链路。
        UpdateViewerTopInset();
        UpdateCanvasViewportInsets();
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
        await SaveChangesAsync();
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
            CanvasPath = _currentCanvasPath ?? "",
            CanvasPanelOpen = CanvasPanelSplitView.IsPaneOpen,
            SavedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        // 窗口几何由 MainWindow 填写（只有它持有 AppWindow）：普通状态取 AppWindow.Position/Size，
        if ((Application.Current as App)?.MainWindow is MainWindow mainWindow)
            mainWindow.CaptureWindowGeometry(data, _currentPdfPath);
        DocumentStore.SaveViewState(_currentPdfPath, data);
    }

    /// <summary>"不保存退出"：未落盘的画布改动不在盘上，放弃它无需回滚任何数据 ——
    /// 清掉内存里的墨与脏标记即可，下次打开仍是文件里那份（上次保存态）。</summary>
    public void DiscardUnsavedOnExit()
    {
        try
        {
            foreach (var p in _pages) p.Annotations.Clear();
        }
        catch { }
        // 清掉脏标记后 CleanupForExit 的 FlushCanvas 不会再写盘；
        // 托管表里那几份"切走时没写进去"的也一并作废 —— 用户选的就是"不保存"。
        _canvasDirty = false;
        _canvasSavePaused = false;   // 这一路是"全部放弃"，暂停态与那枚常驻提示也该跟着散场
        _unsavedCanvasDocs.Clear();
        RefreshUnsavedState();
    }

    public void CleanupForExit()
    {
        // 退出前落白板；"不保存退出"已清掉脏标记，此处不会写。
        // 写不进去时至少留一份库内副本：窗口马上要隐藏、进程马上要终止，这时已经没法再问用户了，
        // 但"把内容留在盘上"仍然做得到（指针改不改都无所谓，进程随后就结束了）。
        if (!FlushCanvas() && _canvasDoc != null) TryAdoptRecoveryCopy(_canvasDoc, _currentCanvasPath);

        // 目录监听必须在进程退出前释放：句柄不关会让可执行目录被占用。
        DisposeCanvasWatcher();

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

    /// <summary>Ctrl+S 与"保存并退出"的唯一入口。Glance 不写 PDF：笔迹与卡片都在画布文件里，
    /// 所以"保存"只剩把所有画布收尾（当前这份 + 切走时托管在内存里的那几份）。</summary>
    public Task<bool> SaveChangesAsync(bool notify = true)
    {
        // 已有保存在跑：返回它，让本次调用等同一个结果。
        // 不能直接 return true —— 关窗流程会把"true"当成"已落盘"随即强杀进程，
        // 而那次写入可能正在半途，文件会被截断。
        var running = _saveTask;
        if (running != null) return running;

        var task = RunSaveAsync(notify);
        _saveTask = task;
        return task;
    }

    private async Task<bool> RunSaveAsync(bool notify)
    {
        // 先让出一次：保证调用方已经把 Task 存进 _saveTask，finally 才可能清到正确的那一个
        // （否则"没有脏标记"这种同步就能跑完的路径会在赋值之前就把字段清掉，之后永远残留）。
        await Task.Yield();
        try
        {
            bool saved = await SaveAllCanvasesAsync();
            if (saved && notify) ShowSaveToast(LocalizationService.Get("SaveToastSuccess"), ToastIconKind.Success);
            // 失败时**不退出**：窗口和内存里的画布都留着，他还能按 Ctrl+S 或再点一次关闭重来 ——
            // 一次误点取消不该等于把这次编辑判死刑。
            return saved;
        }
        finally
        {
            _saveTask = null;
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
            _currentPageIndex = 0;
            PageNumberInput.Text = "1";
            TotalPagesText.Text = "/ 0";
            QueueToolbarOverflowUpdate();

            // 切换文档前，先保存旧 PDF 的阅读位置/缩放（此时 GpuViewer 仍显示旧文档）。
            SaveCurrentViewState();
            // 旧画布先落盘再换指针；写失败只提示（并落恢复副本），不拦住"打开这份文档"这个动作本身。
            // 换指针【之前】托管：失败出口可能是异步的，等它回来 _canvasDoc 已经是新文档的了。
            if (!FlushCanvas())
            {
                StashUnsavedCanvas(_currentCanvasPath, _canvasDoc);
                ReportCanvasSaveFailure();
            }
            RetryUnsavedCanvases();   // 别的文档/画布留下的那几份也顺手再试一次
            _currentPdfPath = path;
            _canvasDirty = false;
            // 新文档自己还没改动，但托管表里可能压着别的画布 —— 不能让一次切文档把它们一起当"已保存"。
            RefreshUnsavedState();
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
            var savedVs = DocumentStore.LoadViewState(_currentPdfPath);
            SidebarSplitView.IsPaneOpen = savedVs?.SidebarOpen == true;
            if (ToggleSidebarButton != null) ToggleSidebarButton.IsChecked = SidebarSplitView.IsPaneOpen;
            // 右侧画布面板的开关与打开的画布都随文档记忆（退出时由 SaveCurrentViewState 写入）。
            CanvasPanelSplitView.IsPaneOpen = savedVs?.CanvasPanelOpen == true;
            if (ToggleCanvasPanelButton != null) ToggleCanvasPanelButton.IsChecked = CanvasPanelSplitView.IsPaneOpen;
            // 画布：恢复"上次盖在这份文档上的那一份"。文件已不存在（被删/改名/换了机器）⇒ 退回没盖画布，
            // 画布库里的数据本身不受影响，去右侧面板重新选一份即可。
            _currentCanvasPath = savedVs?.CanvasPath;
            if (!string.IsNullOrEmpty(_currentCanvasPath) && !File.Exists(_currentCanvasPath))
                _currentCanvasPath = null;
            _canvasDirty = false;
            RefreshCanvasTree();
            // 面板的开关状态是按文档记的，这条路径（打开文档时直接展开）不会经过标题栏开关，
            // 所以这里也要还原一次视口位置，否则"上次明明是开着面板关的"就总是回到左上角。
            ApplySavedCanvasPanelScroll();
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

            // 画布是墨的唯一载体：页上笔迹完全由当前画布重建，PDF 里一概不读、不写。
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
                "Board" => SidebarMode.Board,
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
        ModeBoardButton.IsChecked = _currentSidebarMode == SidebarMode.Board;

        PageListView.Visibility = _currentSidebarMode == SidebarMode.Thumbnails ? Visibility.Visible : Visibility.Collapsed;
        BookmarkListView.Visibility = _currentSidebarMode == SidebarMode.Bookmarks ? Visibility.Visible : Visibility.Collapsed;
        OutlineListView.Visibility = _currentSidebarMode == SidebarMode.Outline ? Visibility.Visible : Visibility.Collapsed;
        SidebarHeaderText.Text = _currentSidebarMode switch
        {
            SidebarMode.Bookmarks => GlanceRX.Services.LocalizationService.Get("SidebarBookmarks"),
            SidebarMode.Outline => GlanceRX.Services.LocalizationService.Get("SidebarOutline"),
            SidebarMode.Board => GlanceRX.Services.LocalizationService.Get("SidebarBoard"),
            _ => GlanceRX.Services.LocalizationService.Get("SidebarThumbnails"),
        };

        // 切到白板模式时重扫一次：卡片可能在别的模式里被改过（自动保存/撤销/换画布都不经过本面板）。
        if (_currentSidebarMode == SidebarMode.Board) RefreshBoardList();
        else
        {
            BoardListView.Visibility = Visibility.Collapsed;
            BoardEmptyText.Visibility = Visibility.Collapsed;
        }
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

    // 白板行跳转：只移动视口，【不选中】卡片（选中会连带把它置顶、改 nodes 顺序并落盘）。
    private void BoardListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BoardCardViewModel card)
            GpuViewer?.GoToCanvasRect(card.PageIndex, card.X, card.Y, card.Width, card.Height);
    }

    // 侧栏标题行右侧的金色五角星：静默把当前页加为书签。
    private void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        // 它是 ToggleButton（只为与模式键共用同一套样式/光效），点击会把 IsChecked 自动翻成 true；
        // 立刻复位，免得黄星亮成"当前模式"的强调紫。同一次输入事件内改回，不会跨帧闪色。
        if (AddBookmarkButton != null) AddBookmarkButton.IsChecked = false;
        if (_totalPages == 0 || _currentPageIndex < 0 || _currentPageIndex >= _totalPages) return;
        if (_bookmarks.Any(b => b.PageIndex == _currentPageIndex)) return;
        InsertBookmarkSorted(_currentPageIndex,
            string.Format(GlanceRX.Services.LocalizationService.Get("BookmarkPagePlaceholder"), _currentPageIndex + 1));
        SyncBookmarkFlags();
        _ = SaveBookmarksAsync();
        ShowSaveToast(GlanceRX.Services.LocalizationService.Get("BookmarkAddedToast"), ToastIconKind.Bookmark);
    }

    /// <summary>Ctrl+D：收藏当前页书签（与标题行黄星按钮等效，同样弹收藏气泡）。 有效性守卫（无文档 / 页索引越界 / 已有书签静默早退）由 AddBookmark_Click 自带。</summary>
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
    // 否则从书签列表删除、或从标题行黄星添加后，行内星的两态会与实际书签不符。
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

    // 书签行按钮活在 DataTemplate 里、没有 x:Name ⇒ MainPage_Loaded 的逐颗注册够不着，
    // 改由模板的 Loaded 逐颗挂接管器。Tag 只用于保证同一个 Button 实例不重复订阅：
    // 列表虚拟化回收再挂载时 Loaded 会重来，而接管器自带 Unloaded/Loaded 重注册。
    private const string BookmarkRowButtonOverrideTag = "BookmarkRowVisualOverride";

    private void BookmarkRowButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not null) return;
        b.Tag = BookmarkRowButtonOverrideTag;
        // 只画按下档：容器回收复用时悬浮状态没有可靠的重算时机，让它不产生视觉输出。
        ButtonVisualOverride.Attach(b, transparentAtRest: true, pressOnly: true);
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
        RestoreViewerFocusAfterBookmarkEdit();
        _ = SaveBookmarksAsync();
    }

    private void BookmarkCancel_Click(object sender, RoutedEventArgs e)
    {
        if (GetBookmarkModel(sender) is not BookmarkViewModel bm) return;
        bm.IsEditing = false;
        RestoreViewerFocusAfterBookmarkEdit();
    }

    private void BookmarkDelete_Click(object sender, RoutedEventArgs e)
    {
        if (GetBookmarkModel(sender) is not BookmarkViewModel bm) return;
        _bookmarks.Remove(bm);
        RestoreViewerFocusAfterBookmarkEdit();
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
        if (GetBookmarkModel(sender) is not BookmarkViewModel bm) return;
        bm.IsEditing = false;
        RestoreViewerFocusAfterBookmarkEdit();
    }

    private void BookmarkEdit_CommitRequested(object sender, RoutedEventArgs e)
    {
        // 点 Cancel/Delete 按钮会先把 IsEditing 置假（控件随之折叠、不再可见），故此处按"仍在编辑态"守卫，
        if (GetBookmarkModel(sender) is BookmarkViewModel bm && bm.IsEditing) CommitBookmark(bm);
    }

    /// 编辑框消失后把键盘焦点交还视口：折叠的自绘编辑框仍持有隐藏宿主的焦点（WinUI 已知行为），
    /// 字母键会进输入法，页面快捷键也被"焦点在 TextBox 上"的判断挡掉。延迟一拍等框架完成折叠与
    /// 焦点迁移，再按 GpuPageView 同源手法显式交给 Viewer；焦点已在别处、或又进入编辑时不抢，
    /// 避免顶掉刚点开的浮层 / 抢走新编辑框的焦点。
    private void RestoreViewerFocusAfterBookmarkEdit()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            object? focused = FocusManager.GetFocusedElement(XamlRoot);
            if (focused != null && focused is not TextBox) return;
            if (_bookmarks.Any(b => b.IsEditing)) return;
            if (GpuViewer.EnsureViewerFocus()) return;
            FocusManager.TryMoveFocus(FocusNavigationDirection.Next);
        });
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
            await DocumentStore.SaveBookmarksAsync(_currentPdfPath, list);
        }
        catch { /* best-effort persistence */ }
    }

    private async Task LoadBookmarksAsync()
    {
        _bookmarks.Clear();
        if (string.IsNullOrEmpty(_currentPdfPath)) return;
        try
        {
            var saved = await DocumentStore.LoadBookmarksAsync(_currentPdfPath);
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

            // 关窗前把右侧面板的会话状态（展开的文件夹 + 视口位置）一次性写回设置。
            // 用 AppWindow.Closing 而不是 Closed：那一刻窗口内容还在，能读到真实的滚动偏移；
            // Closed 时视觉树可能已经拆解，读到的视口是 0，会把用户记住的位置抹成左上角。
            try
            {
                mainWindow.AppWindow.Closing += (s, e) => SaveCanvasPanelStateAtExit(allowScroll: true);
            }
            catch { }
            // 兜底：万一 Closing 没走到，也只补写"展开的文件夹"，偏移一律沿用上次存的值。
            mainWindow.Closed += (s, e) => SaveCanvasPanelStateAtExit(allowScroll: false);

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
        Warning,   // Warning \uE7BA，琥珀色：介于"成功"与"错误"之间的状态（如画布有缺失）
    }

    private void ShowSaveToast(string message, ToastIconKind kind)
    {
        SaveToastItem item = AddToastCapsule(message, kind, sticky: false);
        item.Capsule.Tapped += (s, e) => DismissToast(item, animate: true);

        // 独立计时：2s 后自动淡出（自创建起算 → 最旧先到期）
        item.Timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        var captured = item;
        item.Timer.Tick += (s, e) => DismissToast(captured, animate: true);
        item.Timer.Start();

        // 栈深上限只数"会自己消失的"：常驻胶囊不计入、也永远不会被这条淘汰规则丢掉。
        if (_saveToasts.Count(t => !t.Sticky) > SaveToastMax)
        {
            DismissToast(_saveToasts.First(t => !t.Sticky), animate: true);
        }
    }

    /// <summary>把一枚胶囊推进左下角的胶囊栈：占住最底格（Pos=0），把已有的一律上推一格，
    /// 并播放"从下方一格滑入 + 淡入"。返回值交给调用方决定它什么时候走、怎么走。
    /// 常驻提示与普通提示共用这一条入栈路径 —— 位次、动画、格距、视觉全部同源。</summary>
    private SaveToastItem AddToastCapsule(string message, ToastIconKind kind, bool sticky)
    {
        var capsule = BuildToastCapsule(message, kind);
        // 基值 = 静止格位（0）：动画被停掉时只会停在格位上，不会弹回"下方一格"。
        // 入场位移由动画的 From 提供。
        var tt = new TranslateTransform();
        capsule.RenderTransform = tt;
        capsule.Opacity = 0;
        SaveToastStack.Children.Add(capsule);

        var item = new SaveToastItem { Capsule = capsule, Transform = tt, Pos = 0, Sticky = sticky };

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
            From = SaveToastSlot,   // 起点由动画提供，基值保持 0
            To = 0,
            Duration = TimeSpan.FromMilliseconds(SaveToastSlideMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(tr, tt);
        Storyboard.SetTargetProperty(tr, "Y");
        enter.Children.Add(op);
        enter.Children.Add(tr);
        // 入场结束后把基值对齐到静止态：此后任何 Stop/接替都停在格位上（透明度基值 0 会让胶囊整枚隐形）。
        // 只在仍处于最底格时回写 Y —— 期间若被别的胶囊挤上去，Y 基值已由 SlideToastTo 接管。
        enter.Completed += (s, e) =>
        {
            capsule.Opacity = 1;
            if (item.Pos == 0) tt.Y = 0;
        };
        enter.Begin();

        return item;
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
            ToastIconKind.Warning => "\uE7BA",
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
            ToastIconKind.Warning => dark
                ? Windows.UI.Color.FromArgb(0xFF, 0xFC, 0xE1, 0x00)
                : Windows.UI.Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00),
            _ => dark
                ? Windows.UI.Color.FromArgb(0xFF, 0x6B, 0xB7, 0x00)
                : Windows.UI.Color.FromArgb(0xFF, 0x10, 0x7C, 0x10),
        });

        text.Text = message;

        ElevationShadow.SetIsEnabled(capsule, true);
        ElevationShadow.SetElevation(capsule, 18);
        return capsule;
    }

    /// <summary>将胶囊平滑移动到目标格位（Pos）。
    /// Stop() 会把 TranslateTransform.Y 立刻弹回基值 —— 所以必须【先读当前位、再停表】，
    /// 并把基值钉在这个起点上；否则每次重定位都会从旧的基值重演一遍位移动画
    /// （表现就是被顶上去的常驻胶囊，每次有人消失都先从"下方一格"飞起来再回到位）。
    /// 位移只走一格，读到的起点与终点相同就直接返回，免得起一段零距离动画。</summary>
    private void SlideToastTo(SaveToastItem item, int newPos)
    {
        double to = -newPos * SaveToastSlot;
        double from = item.Transform.Y;   // 必须在停表之前读：停表后读到的是基值而非视觉位置
        item.Pos = newPos;

        item.SlideSb?.Stop();
        item.SlideSb = null;
        item.Transform.Y = from;          // 基值钉在起点：停表/被新动画接替都不跳位
        if (Math.Abs(from - to) < 0.5) return;

        var sb = new Storyboard();
        var da = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(SaveToastSlideMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(da, item.Transform);
        Storyboard.SetTargetProperty(da, "Y");
        sb.Children.Add(da);
        item.SlideSb = sb;
        sb.Completed += (s, e) =>
        {
            if (!ReferenceEquals(item.SlideSb, sb)) return;   // 已被下一次位移动画接管
            item.SlideSb = null;
            item.Transform.Y = to;        // 终点回写基值：后续任何 Stop 都停在终点
        };
        sb.Begin();
    }

    private void DismissToast(SaveToastItem item, bool animate)
    {
        item.Timer?.Stop();
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
        if (IsPointerOverCanvasPanel()) return false;
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

    // 指针当前是否落在右侧画布面板（资源管理器）区域：那里是面板控件、不是涂画区，与左侧边栏同一处理 ——
    // 画笔模式下在面板里一律用系统默认箭头，圆环只出现在真正能画的地方。
    private bool IsPointerOverCanvasPanel()
    {
        if (CanvasPanelSplitView == null || CanvasPanelSplitView.Visibility != Visibility.Visible) return false;
        if (!CanvasPanelSplitView.IsPaneOpen) return false;
        RECT? r = GetCanvasPanelScreenRect();
        if (r == null) return false;
        if (!GetCursorPos(out POINT p)) return false;
        return p.X >= r.Value.Left && p.X < r.Value.Right && p.Y >= r.Value.Top && p.Y < r.Value.Bottom;
    }

    // 右侧画布面板(CanvasPanelRoot)的屏幕矩形（物理像素）。算法与 GetThumbnailSidebarScreenRect 完全一致：
    // TransformToVisual(null) 给的是有效像素，乘 RasterizationScale 才是物理像素，最后加上客户区在屏幕上的原点。
    private RECT? GetCanvasPanelScreenRect()
    {
        try
        {
            if (CanvasPanelSplitView == null || CanvasPanelSplitView.Visibility != Visibility.Visible) return null;
            if (CanvasPanelRoot == null) return null;
            var transform = CanvasPanelRoot.TransformToVisual(null);
            Windows.Foundation.Point tl = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = this.XamlRoot?.RasterizationScale ?? 1.0;
            POINT origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(_hWnd, ref origin)) return null;
            int left = (int)(origin.X + tl.X * scale);
            int top = (int)(origin.Y + tl.Y * scale);
            int right = (int)(origin.X + (tl.X + CanvasPanelRoot.ActualWidth) * scale);
            int bottom = (int)(origin.Y + (tl.Y + CanvasPanelRoot.ActualHeight) * scale);
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
        ResetPenCapsuleAfterFade();
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

        // A capsule closed by HideTopToolbar stays closed: re-showing the bar must not pop it
        // back open. ResetPenCapsuleAfterFade above only clears a fade interrupted mid-flight.
    }

    /// <summary>Stops the previous slide before starting a new one: a completed Storyboard keeps holding its final value, which would otherwise override the next animation's writes.</summary>
    private void StartToolbarAnim(Storyboard sb)
    {
        _toolbarAnim?.Stop();
        _toolbarAnim = sb;
        sb.Begin();
    }

    /// Slides the top toolbar back up and out of view. The bar and its host stop intercepting
    /// input immediately, so a hidden toolbar never blocks the content underneath.
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
        ResetPenCapsuleAfterFade();   // 先收掉上一次淡出留下的半透明状态，再决定是否开始新一轮

        // 胶囊活在独立 Popup 里，工具栏的 Opacity 淡出管不到它。把它的淡出并进同一个 Storyboard，
        // IsOpen=false 延后到动画结束 —— 立刻关会让 Child 脱树，动画根本没机会播。
        bool capsuleFading = PenOptionsPopup != null && PenOptionsPopup.IsOpen && PenOptionsCapsule != null;
        if (!capsuleFading && PenOptionsPopup != null)
            PenOptionsPopup.IsOpen = false;
        if (capsuleFading)
            PenOptionsCapsule!.IsHitTestVisible = false;   // 淡出期间别让半透明胶囊继续吃指针

        var sb = new Storyboard();
        var ya = new DoubleAnimation
        {
            From = 0,
            To = -TopToolbarHiddenOffset(),
            Duration = new Duration(TimeSpan.FromMilliseconds(UiTransitionMs)),
            // 退出用 accelerate（先慢后快），与展开的 decelerate 配对：离开时加速才符合动效惯例。
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(ya, TopToolbarTranslate);
        Storyboard.SetTargetProperty(ya, "Y");
        sb.Children.Add(ya);

        // 同步淡出：60Hz / 150ms 只有 9 帧，accelerate 的末尾单帧位移可达 15px；
        // 让那几帧在接近透明时经过，人眼便不会把它读成跳动。不设 From，从当前不透明度续接，
        // 否则工具栏已隐藏时再次收起会闪一下。
        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(UiTransitionMs))
        };
        Storyboard.SetTarget(fade, TopToolbar);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        if (capsuleFading)
        {
            var capsuleFade = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(UiTransitionMs))
            };
            Storyboard.SetTarget(capsuleFade, PenOptionsCapsule);
            Storyboard.SetTargetProperty(capsuleFade, "Opacity");
            sb.Children.Add(capsuleFade);
        }

        sb.Completed += (_, __) =>
        {
            // Only park it if nothing re-opened the bar in the meantime.
            if (TopToolbar != null && !TopToolbar.IsHitTestVisible)
                TopToolbar.Opacity = 0;
            ResetPenCapsuleAfterFade();
        };
        StartToolbarAnim(sb);
    }

    /// 胶囊淡出若在结束前被 Stop()，Storyboard.Completed 不会触发：半透明胶囊会连着仍打开的
    /// Popup 一起挂在屏幕上。以 IsHitTestVisible=false 作「淡出中」标记收尾，幂等；
    /// 正常打开的胶囊该标志为 true，不会被误关。
    private void ResetPenCapsuleAfterFade()
    {
        if (PenOptionsCapsule == null || PenOptionsCapsule.IsHitTestVisible) return;
        if (PenOptionsPopup != null) PenOptionsPopup.IsOpen = false;
        PenOptionsCapsule.Opacity = 1;
        PenOptionsCapsule.IsHitTestVisible = true;
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
        bool overScrollBar = IsInsideScrollBar(src);   // 命中元素在任一 ScrollBar(或其模板部件)内 → 视为滚动条区域
        // 右侧对比视图排除已改用 IsPointerOverCompareView() 的屏幕矩形判定（容器可见即整片右侧右半区恢复原生），

        // 工具栏归属：命中工具栏 + 实时光标在工具栏矩形内 → 建立；否则按【几何】收敛（指针已不在矩形内，
        _pointerInTopToolbar = overToolbar || overToolbarRect;
        _pointerInPenCapsule = overCapsule;
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
        UpdateViewerTopInset();
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
            ResetPenCapsuleAfterFade();   // Stop 会打断胶囊淡出，Completed 不触发
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
        ToggleCanvasPanelButton, RotateButton, LayoutToggleButton, SplitViewToggleButton, SaveButton, PrintButton,
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
            PrintButton, SaveButton, SplitViewToggleButton, LayoutToggleButton, ToggleCanvasPanelButton, RotateButton,
            NextPageButton, PrevPageButton,
            TotalPagesText, PageNumberInput, ToolbarModeSeparator, ToggleSidebarButton,
            CanvasModeButton, PenModeButton, ReadModeButton,
            SettingsButton,
        };

        _toolbarMenuOrder = new FrameworkElement[]
        {
            ToggleSidebarButton, PrevPageButton, NextPageButton, ReadModeButton, PenModeButton,
            CanvasModeButton, ToggleCanvasPanelButton, RotateButton, LayoutToggleButton, SplitViewToggleButton,
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
        if (el == ToggleCanvasPanelButton) return "ToggleCanvasPanel";
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
        if (el == ToggleCanvasPanelButton) return FluentFontIcon("\uE90D");
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
        else if (el == ToggleCanvasPanelButton) ToggleCanvasPanel_Click(this, new RoutedEventArgs());
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
        UpdateViewerTopInset();         // 顶栏几何变了 ⇒ 两个 PDF 视图的让位量同批重算
        if (ThumbnailSidebarPanel == null) return;

        double top = TopToolbarReservedHeight();

        var p = ThumbnailSidebarPanel.Padding;
        if (Math.Abs(p.Top - top) > 0.5)
            ThumbnailSidebarPanel.Padding = new Thickness(p.Left, top, p.Right, p.Bottom);

        // 右侧画布面板同一个顶部让位量：两者都在 ZIndex=50 的工具栏之下，必须让开同一条带。
        if (CanvasPanelRoot != null)
        {
            var cp = CanvasPanelRoot.Padding;
            if (Math.Abs(cp.Top - top) > 0.5)
                CanvasPanelRoot.Padding = new Thickness(cp.Left, top, cp.Right, cp.Bottom);
        }

        UpdateCanvasViewportInsets();   // 顶栏高度变了 ⇒ 白板悬浮胶囊的安全区也要跟着变
    }

    /// 展开工具栏占掉的顶带高度：实测栏高 + 6px 呼吸空隙，最低 14。
    /// 侧栏、右画布面板与两个 PDF 视图共用同一基准，否则各自的顶线会在同一屏上错开。
    private double TopToolbarReservedHeight()
    {
        double hostTop = TopToolbarHost?.Margin.Top ?? 0;   // 宿主上提 1px 压住标题栏发丝线
        double barH = TopToolbar?.ActualHeight ?? 0;
        if (barH <= 0) barH = 48;                           // 首次布局前的兜底，与 TopToolbarHiddenOffset 同值
        return Math.Max(14, hostTop + barH + 6);
    }

    /// 常驻显示时把左右两个 PDF 视图整体下移一条工具栏带。PDF 画面、滚动视口与白板浮层
    /// 同属 GpuPageView，整体移动才不会与页面坐标错位；只挪画面会让覆盖层与页面脱节。
    /// 自动收起恒为 0：工具栏只在悬停时短暂浮出，收起后本来就不遮挡；若也下移，每次显隐
    /// 都会重排视口并让阅读位置跳动。
    private void UpdateViewerTopInset()
    {
        double top = _toolbarAlwaysVisible ? TopToolbarReservedHeight() : 0;
        SetTopMargin(GpuViewer, top);
        SetTopMargin(GpuViewerRight, top);

        static void SetTopMargin(FrameworkElement? view, double top)
        {
            if (view == null) return;
            var m = view.Margin;
            if (Math.Abs(m.Top - top) < 0.5) return;
            view.Margin = new Thickness(m.Left, top, m.Right, m.Bottom);
        }
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
        // 常驻显示时 GpuViewer 已整体下移到工具栏之下，宿主相对视图的上沿为负：夹到 0，
        // 否则胶囊会被再让位一次（双重偏移），负值还会把钳制下界推到视口上方。
        double top = Math.Max(0, BottomOf(TopToolbarHost));
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

        // 落笔即建画布文件：这份画布从此既承载卡片，也承载墨迹，两者共用同一条保存路径
        // （置脏 → 防抖 500ms → 写回画布文件 → 失败出口），不再有"保存时烘进 PDF"这回事。
        MarkCanvasDirty();

        _isDrawingHighlight = true;
        // 一个 ViewModel 就是整条笔：跨页不会另起对象，点继续往同一串里追加，
        // 于是"整条一起撤"是天然的，不存在撤一半的情形。
        var stroke = NewInkStroke(e.PageIndex, GetActiveColorHexWithOpacity(), _activeThickness);
        stroke.AppendPoint(new Point(e.X, e.Y));
        _pages[e.PageIndex].Annotations.Add(stroke);
        // 这一页可能原本一道墨都没有：告诉渲染侧"有墨页"多了一页，否则它滚出视口后再回来不会重画。
        GpuViewer.NotifyInkPageAdded(e.PageIndex);
        _activeHighlight = stroke;

        _annotationRedo.Clear();
        _annotationHistory.Add(new EditorHistoryEntry(
            undo: () => SetInkStrokeVisible(stroke, false),
            redo: () => SetInkStrokeVisible(stroke, true)));
        GpuViewer.InvalidateOverlayThrottled();
    }

    /// <summary>笔画移动。坐标恒为【发起页】的页局部坐标，越出页框（跨到下一页、穿过页间空隙、
    /// 越过左右页边）都只当作同一条线的延伸 —— 不切段、不裁剪、不补线。</summary>
    private void GpuAnnotation_Moved(object? sender, GpuAnnotationPointerArgs e)
    {
        if (!_isDrawingHighlight || _activeHighlight == null) return;

        // 每次移动都重排自动保存：500ms 的语义是"手停下多久才写盘"，笔还在走就不能写。
        // ⚠ 不标脏的后果不是"晚一点存"，而是【只存下半条笔】—— 按下时排的那次保存会在笔
        //   没画完时落盘，此后再不置脏，长笔的尾段就永久停在文件外面。
        MarkCanvasDirty();

        // 点数正比于线条长短而不是采样次数：慢写时手在原地抖，事件照样每帧都来，
        // 全收只会让文件白胖、且后半段全是几乎重合的点。只决定"这一点收不收"、
        // 从不改动已收的点 —— 存下来的折线与屏幕所见逐点一致（与事后抽稀的根本区别，后者会挪点位）。
        Point last = _activeHighlight.LastPoint;
        if (!double.IsNaN(last.X))
        {
            double dx = e.X - last.X, dy = e.Y - last.Y;
            if (dx * dx + dy * dy < InkMinPointGap * InkMinPointGap) return;
        }
        _activeHighlight.AppendPoint(new Point(e.X, e.Y));
        GpuViewer.InvalidateOverlayThrottled();
    }

    private void GpuAnnotation_Released(object? sender, GpuAnnotationPointerArgs e)
    {
        if (!_isDrawingHighlight) return;
        _isDrawingHighlight = false;

        if (_activeHighlight != null)
        {
            // 收笔点无条件补进这条笔：笔画必须终止在抬笔处，"按下即抬"的单点也靠它保留下来
            // （只有 1 个点的笔由渲染侧画成圆点）。这一步刻意不走最小间距过滤 —— 它就是终点。
            Point last = _activeHighlight.LastPoint;
            double dx = e.X - last.X, dy = e.Y - last.Y;
            if (double.IsNaN(last.X) || dx * dx + dy * dy > 1e-6)
                _activeHighlight.AppendPoint(new Point(e.X, e.Y));
            // 抬笔即置脏：整条笔到此才定稿，必须再写一次盘（否则尾段永远停在半截）。
            MarkCanvasDirty();
        }

        _activeHighlight = null;
        GpuViewer.InvalidateOverlayThrottled();
    }

    /// <summary>新起一条笔：它自己记着发起页，于是"这一笔属于哪页"不需要外部再维护一份映射。
    /// 落笔序号在这里发放：序号越大越晚落笔、画得越靠上，撤销后再重画也是新的一号。</summary>
    private AnnotationViewModel NewInkStroke(int page, string colorHex, double thickness) => new()
    {
        Type = AnnotationType.Pen,
        PageIndex = page,
        ZOrder = _inkZSeq++,
        ColorHex = colorHex,
        Thickness = thickness
    };

    /// <summary>整条笔显示 / 隐藏（撤销、重做）：一笔一个对象，所以本来就是整条一起。</summary>
    private void SetInkStrokeVisible(AnnotationViewModel stroke, bool visible)
    {
        if (stroke.PageIndex >= 0 && stroke.PageIndex < _pages.Count)
        {
            var list = _pages[stroke.PageIndex].Annotations;
            if (visible) { if (!list.Contains(stroke)) list.Add(stroke); }
            else list.Remove(stroke);
        }
        MarkCanvasDirty();
        // 这一笔整条从页上消失了：累积层里还有它的像素，只有整层重烘才会真的擦掉，
        // 光"置脏 + 重画"会把它当已烘入的段落跳过（幽灵笔迹）。
        GpuViewer.InvalidateOverlay();
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
        // 必须【同步】置 Handled：放到 await 之后等于告诉框架这次按键没人认领，
        // 同一次 Ctrl+S 会被再处理一遍 —— 只读/加密文档因此冒出两个"另存为"框。
        args.Handled = true;
        await SaveChangesAsync();
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

    /// <summary>当前盖在这个文档上的画布文件路径（空 = 没盖画布）。画布与 PDF 之间没有任何绑定关系，
    /// 这只是"上次用了哪份"的记忆项：随时可在右侧面板换一份，画布被删/改名后指向失效就退回空。</summary>
    private string? _currentCanvasPath;

    /// <summary>画布库目录监听与它的防抖定时器（见 SetupCanvasWatcher）。
    /// 定时器类型写全名：Windows.System 里也有一个 DispatcherQueueTimer，简写会 CS0104 二义。</summary>
    private FileSystemWatcher? _canvasWatcher;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _canvasRefreshTimer;

    /// <summary>停手多久之后自动落盘。取值只跟"一次连续操作的间隔"有关：拖动、连续打字都会不断刷新这笔定时，
    /// 手停下来半秒才真正写一次盘 —— 用户感知是"随手画完就存好了"，而写盘次数仍然是整个操作一次。
    /// 刻意不设"最长等待上限"：单次写入是整份重写（实测 800 卡 ≈ 9ms），不需要靠强制刷新来摊平长事务。</summary>
    private const int CanvasAutoSaveDelayMs = 500;

    /// <summary>自动保存定时器。懒建（画布面板没开过的会话不必造），一次性触发：每次置脏都重开。</summary>
    private DispatcherTimer? _canvasAutoSaveTimer;

    /// <summary>最近一次写入失败的原因：只用来把提示里的那半句话（根因）说准。</summary>
    private CanvasSaveError _canvasSaveError;

    /// <summary>"另选保存位置"的选择器是否已经开着：自动保存失败可能连着来，叠第二个选择器没有意义。</summary>
    private bool _canvasSavePromptOpen;

    /// <summary>自动保存已暂停（失败出口里用户取消了"另存为"）。**只停自动重试，不放弃内容**：
    /// 脏标记照旧保留，所以 Ctrl+S 与关闭时的询问都还能再存一次；左下角同时立一枚常驻"未保存"，
    /// 直到某次写入真的成功才撤。这样"取消"不是一次性判决，用户不会被一次误点锁死。</summary>
    private bool _canvasSavePaused;

    /// <summary>左下角那枚常驻"画布改动未保存"。它表示的是一个持续状态而非一次性事件，
    /// 所以不走 2 秒自动消失那条路（见 ShowCanvasUnsavedToast）。</summary>
    private SaveToastItem? _canvasUnsavedToast;

    /// <summary>按画布路径暂存的"还没写进磁盘"的内存模型。切画布 / 切文档时，写不进去的那份
    /// 不会被直接丢掉 —— 只切走一下就把整段编辑作废，代价远大于这点内存（一份 800 卡 ≈ 几十 KB）。
    /// 切回来时优先用它恢复（脏标记、常驻提示一并回来）；任何一次成功写入（原文件 / 库内副本 /
    /// 另存为）都把对应项移出本表。键 = 画布文件路径，与 <see cref="_currentCanvasPath"/> 同一套比较规则。</summary>
    private readonly Dictionary<string, CanvasDoc> _unsavedCanvasDocs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>白板（Canvas 模式）：从当前画布文件读取卡片、连线与笔迹，保留模型供交互改写，并把当前数组交给 GpuViewer 渲染。
    /// 内存模型是唯一真相源，交互只置脏、不落盘；写入时机在 FlushCanvas（停手自动保存 / 显式保存 / 切换文档 / 切换画布 / 退出保存）。</summary>
    private void LoadCanvasForCurrentDocument()
    {
        // 这份画布有"切走时没能写进去"的内存版本：用内存版恢复，别拿磁盘上的旧内容把它盖掉。
        CanvasDoc? stashed = null;
        bool pending = !string.IsNullOrEmpty(_currentCanvasPath)
                       && _unsavedCanvasDocs.TryGetValue(_currentCanvasPath!, out stashed);

        CanvasDoc? doc;
        if (pending)
        {
            doc = stashed;
        }
        else
        {
            doc = null;
            try { doc = CanvasStore.LoadFile(_currentCanvasPath); }
            catch { }
        }

        _canvasDoc = doc ?? new CanvasDoc();
        _canvasDirty = pending;
        // 恢复出来的这份仍然写不进去（上次就是这么失败的）：继续暂停自动重试，
        // 别再一回来就弹一次另存为 —— 用户可能只是想看看、想改两笔。
        _canvasSavePaused = pending;
        GpuViewer.SetCanvasData(_canvasDoc.Nodes, _canvasDoc.Edges);
        LoadInkFromCanvasDoc();
        // 换画布 = 换内容：撤销栈里捏着的是上一份画布的 ViewModel，留着它一按 Ctrl+Z
        // 就会把上一份的笔迹塞进当前这份（并且随即被一起存进当前画布文件）。
        _annotationHistory.Clear();
        _annotationRedo.Clear();
        SyncCanvasMissingToast();
        SyncCanvasOutsideRootToast();
        UpdateCanvasDetachButton();
        UpdateCanvasRowBackgrounds();
        // 换画布 / 换文档都会换掉卡片集合：白板列表在这里跟着重建（RefreshCanvas 只覆盖"改卡片"那条链）。
        RefreshBoardList();
        // 托管表里可能还压着别的画布：那就照旧立着"未保存"，别让切一下画布就把提示和关窗询问一起抹掉。
        RefreshUnsavedState();
    }

    /// <summary>按当前画布重建页上墨迹。画布是笔迹的唯一载体：里面有墨就画，没有就把页清空
    /// （换画布就是换墨，上一份的墨不会跟过来）。每条笔带着自己的发起页，跨页的那一截也在同一条点串里。</summary>
    private void LoadInkFromCanvasDoc()
    {
        var ink = _canvasDoc?.Ink;

        // 建 ViewModel 会给 ColorHex / Thickness 赋值，它们的 setter 会反过来置脏 ——
        // 于是"刚打开、什么都没改"也会立刻变成有未保存改动（进而提示保存、白写一次盘）。
        SuppressDirtyMarking = true;
        try
        {
            LoadInkCore(ink);
        }
        finally { SuppressDirtyMarking = false; }

        // 墨被整批换掉了：累积层里还留着上一份画布的像素，必须整层重烘，
        // 否则旧笔迹会以"幽灵"形式留在屏幕上（只置脏不会自己触发重画）。
        GpuViewer.InvalidateOverlay();
    }

    private void LoadInkCore(List<CanvasInk>? ink)
    {
        foreach (var p in _pages) p.Annotations.Clear();
        _inkZSeq = 0;   // 落笔序号按文件里的先后重排：文件已经是按序号写的，见 SyncInkToCanvasDoc
        if (ink == null) return;

        foreach (var stroke in ink)
        {
            // 旧格式（按页切段）残留：每段各还原成一笔，内容不丢，只是不再跨页连成一条。
            if (stroke.LegacySegments is { Count: > 0 })
            {
                foreach (var seg in stroke.LegacySegments)
                    AddInkStroke(stroke, seg.Page, seg.Pts);
                continue;
            }
            AddInkStroke(stroke, stroke.Page, stroke.Pts);
        }
    }

    /// <summary>按一条笔的点串还原成页上的一个墨迹对象。点允许越出页框（跨页、越过左右页边都是正常轨迹），
    /// 这里不做任何范围过滤 —— 过滤掉的就是用户眼里"画了但没留下"的那一截。</summary>
    private void AddInkStroke(CanvasInk stroke, int page, List<double> pts)
    {
        if (page < 0 || page >= _pages.Count || pts.Count < 2) return;
        var vm = new AnnotationViewModel
        {
            Type = AnnotationType.Pen,
            PageIndex = page,
            ZOrder = _inkZSeq++,
            ColorHex = string.IsNullOrEmpty(stroke.Color) ? "#F2C200" : stroke.Color,
            Thickness = stroke.Width > 0 ? stroke.Width : 3.5
        };
        var pc = new Microsoft.UI.Xaml.Media.PointCollection();
        for (int i = 0; i + 1 < pts.Count; i += 2)
            pc.Add(new Point(pts[i], pts[i + 1]));
        vm.PointsCollection = pc;
        _pages[page].Annotations.Add(vm);
    }

    /// <summary>把内存里的墨整体写进画布模型（写文件之前调用）。一条笔一条记录、挂在它的发起页上：
    /// 点串里就是整条线，跨页的那一截也在同一串里，不需要任何分段或布局信息。
    /// 坐标只留两位小数：0.01pt 远低于可见精度，而每个数字省下的字符在几千点的规模上是实打实的。
    /// ⚠ 写出顺序按【落笔序号】而不是按页：数组顺序就是下次读回来的绘制顺序，
    ///   按页写会把文件变成"按页分组"，重开文档后跨页的层级就退回页序了。</summary>
    private void SyncInkToCanvasDoc()
    {
        if (_canvasDoc == null) return;
        _canvasDoc.Ink.Clear();

        var flat = new List<(AnnotationViewModel A, int Page, int Seq)>();
        foreach (var page in _pages)
            foreach (var a in page.Annotations)
                if (a.Type == AnnotationType.Pen) flat.Add((a, page.PageIndex, flat.Count));
        if (flat.Count == 0) return;

        // 序号相同时用扫描次序定序（理论上不会发生）：保证同一份内容每次写出来都逐条一致。
        flat.Sort((x, y) =>
        {
            int c = x.A.ZOrder.CompareTo(y.A.ZOrder);
            return c != 0 ? c : x.Seq.CompareTo(y.Seq);
        });

        // 按排序结果逐条写成记录；Page 优先取笔自带的发起页，缺失时退回它所在的页集合。
        foreach (var (a, pageIndex, _) in flat)
        {
            var ink = new CanvasInk
            {
                Color = a.ColorHex,
                Width = a.Thickness,
                Page = a.PageIndex >= 0 ? a.PageIndex : pageIndex
            };
            var pts = a.GetPointsSnapshot();
            ink.Pts.Capacity = pts.Length * 2;
            for (int i = 0; i < pts.Length; i++)
            {
                ink.Pts.Add(Math.Round(pts[i].X, 2));
                ink.Pts.Add(Math.Round(pts[i].Y, 2));
            }
            if (ink.Pts.Count >= 2) _canvasDoc.Ink.Add(ink);
        }
    }

    /// <summary>把某份画布盖到当前文档上（null = 撤掉画布）。同一份画布可以盖到任意文档，卡片的页归属
    /// 永远按【当前文档】的页码解释。顺序不可反：先把旧画布写回它自己的文件再换指针，否则旧画布的最后一次编辑会丢。
    /// 写失败【不阻断切换】：点了一行却没反应比"少存一次"更让人困惑。但失败的那份内容会被托管起来
    /// （见 StashUnsavedCanvas）—— 切走不是判决，切回来还能接着存。</summary>
    private void ApplyCanvas(string? canvasPath)
    {
        string? next = string.IsNullOrEmpty(canvasPath) ? null : canvasPath;
        if (string.Equals(_currentCanvasPath, next, StringComparison.OrdinalIgnoreCase)) return;
        if (!FlushCanvas())
        {
            // 必须在换指针【之前】托管：失败出口可能是异步的（另存为对话框），等它回来指针早已换人，
            // 那时 _canvasDoc 已经是新画布的了，这份内容就再也找不回来。
            StashUnsavedCanvas(_currentCanvasPath, _canvasDoc);
            ReportCanvasSaveFailure();
        }
        _currentCanvasPath = next;
        LoadCanvasForCurrentDocument();
        SaveCurrentViewState();
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
        // 只在白板模式可见时重建列表：这条链在拖卡片松手 / 撤销重做时都会跑到，
        // 列表重建会让 ScrollViewer 回到顶部 —— 面板没开着就没必要付这个代价
        // （切到白板模式时 ApplySidebarMode 会重扫一次，隐藏期间的改动不会漏）。
        if (_currentSidebarMode == SidebarMode.Board) RefreshBoardList();
    }

    /// <summary>重建白板模式的卡片列表。顺序 = 页码 → 靠上 → 靠左（阅读顺序，找卡片最直觉）。
    /// 只收【带页归属且页码落在本文档内】的卡片：不带 glancePage 的卡片在视口里画不出来，
    /// 页号越界的卡片既画不出也点不到 —— 列进来就是一条点了没反应的行（越界那张另有左下角常驻提示兜底）。
    /// 卡片的 x/y/w/h 是【页局部未旋转 PDF 点】，与存储、渲染同一空间，这里原样交给视口换算。</summary>
    private void RefreshBoardList()
    {
        _boardCards.Clear();

        int pages = (int)_totalPages;
        var nodes = _canvasDoc?.Nodes;
        int total = nodes?.Count ?? 0;
        if (nodes != null && pages > 0)
        {
            var reachable = new List<CanvasNode>(nodes.Count);
            foreach (var n in nodes)
            {
                if (n != null && n.GlancePage >= 0 && n.GlancePage < pages) reachable.Add(n);
            }

            reachable.Sort(static (a, b) =>
            {
                int cmp = a.GlancePage.CompareTo(b.GlancePage);
                if (cmp != 0) return cmp;
                cmp = a.Y.CompareTo(b.Y);
                if (cmp != 0) return cmp;
                return a.X.CompareTo(b.X);
            });

            string untitled = LocalizationService.Get("BoardCardUntitled");
            string pageFormat = LocalizationService.Get("BoardCardPage");
            foreach (var n in reachable)
            {
                _boardCards.Add(new BoardCardViewModel
                {
                    NodeId = n.Id ?? "",
                    PageIndex = n.GlancePage,
                    X = n.X, Y = n.Y, Width = n.Width, Height = n.Height,
                    Title = BoardCardTitle(n.Text, untitled),
                    PageText = string.Format(pageFormat, n.GlancePage + 1),
                });
            }
        }

        // 空态三句话，别混着说：没挂画布 / 画布是空的 / 有卡片但全都指向不存在的页。
        bool board = _currentSidebarMode == SidebarMode.Board;
        bool hasRows = _boardCards.Count > 0;
        BoardListView.Visibility = board && hasRows ? Visibility.Visible : Visibility.Collapsed;
        BoardEmptyText.Visibility = board && !hasRows ? Visibility.Visible : Visibility.Collapsed;
        if (board && !hasRows)
        {
            BoardEmptyText.Text = string.IsNullOrEmpty(_currentCanvasPath)
                ? LocalizationService.Get("BoardEmptyNoCanvas")
                : total > 0 ? LocalizationService.Get("BoardEmptyNoPages")
                            : LocalizationService.Get("BoardEmptyNoCards");
        }
    }

    /// <summary>卡片编辑框提交文字时只改对应那一行的标题。排序键是 页码/Y/X，改文字不影响顺序，
    /// 所以不需要整表重建；面板隐藏时这行实例仍在集合里，顺手一起改掉，切回来就是最新的。</summary>
    private void UpdateBoardCardText(string? nodeId, string? text)
    {
        if (string.IsNullOrEmpty(nodeId)) return;
        foreach (var vm in _boardCards)
        {
            if (!string.Equals(vm.NodeId, nodeId, StringComparison.Ordinal)) continue;
            vm.Title = BoardCardTitle(text, LocalizationService.Get("BoardCardUntitled"));
            return;
        }
    }

    /// <summary>卡片在列表里的标题 = Text 的第一个非空行（卡片正文可多行，180×32 的小卡只露出两行）。
    /// 空卡片必须给占位文案：留空串会让这一行变成"看不见的一行"，用户以为列表坏了。</summary>
    private static string BoardCardTitle(string? text, string untitled)
    {
        if (string.IsNullOrWhiteSpace(text)) return untitled;

        int nl = text.IndexOf('\n');
        int cr = text.IndexOf('\r');
        int cut = nl < 0 ? cr : (cr < 0 ? nl : Math.Min(nl, cr));
        string first = (cut >= 0 ? text.Substring(0, cut) : text).Trim();
        if (first.Length == 0) return untitled;
        // 超长标题只在这里截断：显示侧由 TextTrimming 负责省略号，截断只为不让字符串无界膨胀。
        return first.Length > 200 ? first.Substring(0, 200) : first;
    }

    /// <summary>白板改动只置脏 + 排一次自动保存，不立即落盘：拖拽、点选、改色都是高频操作，
    /// 逐次整份重写 JSON 会在 UI 线程上卡住交互。真正的写入推迟到"手停下 500ms"（AutoSaveCanvas）
    /// 与显式保存 / 切换文档 / 切换画布 / 退出保存（FlushCanvas）—— 那几处一次写入实测 800 卡 ≈ 9ms。
    /// 暂停期（_canvasSavePaused）只掐掉"自动保存这条链路"（建文件 + 排定时），脏标记照置：
    /// 内容确实还没落盘，于是 Ctrl+S 与关闭时的询问都还有东西可存，不会被上一次的"取消"锁死。</summary>
    internal void MarkCanvasDirty()
    {
        _canvasDirty = true;
        HasUnsavedChanges = true;
        if (_canvasSavePaused) return;
        // 第一次改动就把文件建出来：右侧面板就是画布文件的列表，没有文件就没有那一行，
        // 用户画了半天却在面板里找不到自己正在编辑的画布。
        EnsureCanvasFile();
        RestartCanvasAutoSave();
    }

    /// <summary>确保当前白板有一份自己的文件，没有就立刻在库根建一份，返回"文件已就位"。
    /// 建文件刻意不放到保存那一刻：面板行、高亮、"正在编辑哪一份"全都依赖这个路径。
    /// 只增不改内容 —— 真正的卡片数据仍由紧跟着的写入负责。</summary>
    private bool EnsureCanvasFile()
    {
        if (!string.IsNullOrEmpty(_currentCanvasPath)) return true;
        string? created = CanvasLibrary.CreateEmpty(DefaultCanvasName());
        if (created == null) return false;
        _currentCanvasPath = created;
        AppendCanvasOrder(created);   // 自动建的那份同样排到本层最末
        RefreshCanvasTree();
        UpdateCanvasDetachButton();
        UpdateCanvasRowBackgrounds();
        // 面板收着的时候不滚：行集合已经同步好了，展开时自然就在；此刻强行 ScrollIntoView
        // 只会让"露出名字"那条链路对着一堆还没实例化的容器空转十几轮。
        if (CanvasPanelSplitView is { IsPaneOpen: true }) ScrollCanvasRowIntoView(created);
        return true;
    }

    /// <summary>重开自动保存这笔定时：每次置脏都把"手停下多久"重新计时，
    /// 于是连续拖动 / 连续打字期间一次都不写盘，停手满 500ms 才写一次。</summary>
    private void RestartCanvasAutoSave()
    {
        if (DispatcherQueue == null) return;
        if (_canvasAutoSaveTimer == null)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CanvasAutoSaveDelayMs) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();               // 一次性：下一次置脏再重开
                AutoSaveCanvas();
            };
            _canvasAutoSaveTimer = timer;
        }
        _canvasAutoSaveTimer.Stop();
        _canvasAutoSaveTimer.Start();
    }

    /// <summary>自动保存一次。写不进去就走统一失败出口（转挂副本 / 另选位置），不静默 ——
    /// 静默会让用户一直以为已经存好了，直到关软件时才发现。</summary>
    private void AutoSaveCanvas()
    {
        // 脏标记由 FlushCanvas 自己判：没脏它直接返回成功，这里不必再判一次。
        if (!FlushCanvas()) ReportCanvasSaveFailure();
    }

    /// <summary>把脏掉的白板写回【当前画布自己的文件】。写失败保持脏标记（留给失败出口处理），
    /// 并把原因记进 _canvasSaveError 供提示里的根因用。返回"白板已落盘"（无脏标记也算成功）。
    /// **不动暂停态**：暂停的解释权只在显式保存（SaveAllCanvasesAsync）。切画布 / 切文档也走这里，
    /// 若顺手解暂停，用户一旦取消过"另存为"，之后每点一次画布都会被同一个选择器重新拦住一次。</summary>
    private bool FlushCanvas()
    {
        if (!_canvasDirty) return true;
        if (_canvasDoc == null) { _canvasSaveError = CanvasSaveError.Invalid; return false; }
        if (!EnsureCanvasFile()) { _canvasSaveError = CanvasSaveError.WriteFailed; return false; }

        // 墨是画布的一部分：每次落盘前把页上的笔迹收进模型。放这里而不是"画完就写"，
        // 是因为卡片与墨共用同一次写入 —— 一次写盘就该得到一份完整自洽的文件。
        SyncInkToCanvasDoc();

        _canvasAutoSaveTimer?.Stop();   // 这次写入先到，就把可能还在排队的那笔自动保存作废，别再多写一遍
        if (CanvasStore.SaveFile(_currentCanvasPath, _canvasDoc, out CanvasSaveError error))
        {
            _canvasSaveError = CanvasSaveError.None;
            _canvasDirty = false;
            // 写成功就把"暂停自动保存"解开、把这份从托管表里撤掉：内容已经在盘上了，
            // 再留着那一份，下次切回来会被当成"还没存"，白提示一次。
            _canvasSavePaused = false;
            // 自动保存一落盘，"有未保存内容"就该当场消失：否则停手半秒后内容其实已经写进文件，
            // 关窗口时还会再问一次"要不要保存" —— 那时既没有可丢的东西，也没有可保的东西。
            // 批注仍然脏、或托管表里还压着别的画布时不会清：那个标记是所有来源共用的，
            // 由 RefreshUnsavedState 统一算（顺带撤掉左下角那枚"未保存"）。
            DropUnsavedCanvas(_currentCanvasPath);
            UpdateCanvasRowBackgrounds();
            return true;
        }
        _canvasSaveError = error;
        return false;
    }

    /// <summary>保存失败的统一出口（不阻塞的调用点用）：转挂库内副本，转挂不了就问用户存哪儿。
    /// 模型与路径在这一刻就取快照：这条路可能弹出选择器、等上好几秒，期间用户完全可能换了画布，
    /// 那时候再读字段就已经是别人的内容了。</summary>
    private void ReportCanvasSaveFailure() => _ = ResolveCanvasSaveFailureAsync(_canvasDoc, _currentCanvasPath);

    /// <summary>保存失败的统一出口，返回**内容是否已经落盘**（转挂成功 / 另存成功 = true）。
    /// 用户取消"另存为"时返回 false —— 调用方（关闭流程）据此不退出，把窗口和内容都留着，
    /// 让他还能按 Ctrl+S 再试。顺序固定：先试库内副本（不打扰用户、还能继续用面板），
    /// 实在没地方写才问他存哪。
    /// 无论哪条路收尾都同步一次托管表：落盘了就撤掉那一项（下次切回来不该再被当成"还没存"），
    /// 没落盘就当场补一份内存版 —— 失败出口可能是异步的，等它回来指针早已换人，
    /// 那一刻不托管，这份内容就再也没地方待着（切画布很顺手，一下就丢掉整段编辑）。</summary>
    private async Task<bool> ResolveCanvasSaveFailureAsync(CanvasDoc? doc, string? failedPath)
    {
        if (doc == null) return true;
        bool saved = TryAdoptRecoveryCopy(doc, failedPath)
                     || await PromptSaveCanvasElsewhereAsync(doc, failedPath);
        if (saved) DropUnsavedCanvas(failedPath);
        else StashUnsavedCanvas(failedPath, doc);
        return saved;
    }

    /// <summary>所有"还没落盘"的画布一次性收尾，返回"全部落盘"。这是**唯一会解除暂停**的入口
    /// （Ctrl+S / 保存并退出）：上一次在"另存为"里点了取消，只作废那之后的自动重试，
    /// 不许把显式保存也一起废掉。顺序：解除暂停 → 托管表里别的几份先各试一次（磁盘满 / 占用解除后
    /// 通常一次就全好了）→ 当前这份交给 FlushCanvas（它管脏标记）→ 仍写不进去的逐个走统一失败出口。
    /// 任一份被用户取消就返回 false —— 调用方（关窗流程）据此不退出，内容仍然在内存里、仍然记脏。</summary>
    private async Task<bool> SaveAllCanvasesAsync()
    {
        _canvasSavePaused = false;
        RetryUnsavedCanvases();

        if (!FlushCanvas() && !await ResolveCanvasSaveFailureAsync(_canvasDoc, _currentCanvasPath))
            return false;

        // 快照遍历：下面的失败出口可能往表里补项，边遍历边改会抛。
        foreach (var pair in _unsavedCanvasDocs.ToList())
        {
            // 当前这份已经在上面处理过；再写一次只是把同一份内容写两遍。
            if (string.Equals(pair.Key, _currentCanvasPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (CanvasStore.SaveFile(pair.Key, pair.Value, out CanvasSaveError error))
            {
                DropUnsavedCanvas(pair.Key);
                continue;
            }
            // 让提示里的根因说的是这一份的失败，而不是上一份留下的。
            _canvasSaveError = error;
            if (!await ResolveCanvasSaveFailureAsync(pair.Value, pair.Key)) return false;
        }
        return !_canvasDirty && _unsavedCanvasDocs.Count == 0;
    }

    /// <summary>把改动另存成一份【库内】副本，成功就立刻转挂到它上面。
    /// 落点两级：原文件所在目录 → 画布库根，两处都在库树内 ⇒ 副本一定在右侧面板里、能高亮、
    /// 用户能自己点回去。库外一律不落 —— 面板看不见的副本等于"存了但找不到"。
    /// 转挂是必需的：不转挂的话指针还指着写不进去的那份，之后每次编辑都在同一个失败里打转，
    /// 而副本被反复覆盖成最新内容（实测现象：回原件一看修改全没了，其实都在副本里）。</summary>
    private bool TryAdoptRecoveryCopy(CanvasDoc doc, string? failedPath)
    {
        // 先把根因取出来：下面成功后会清掉 _canvasSaveError，而提示里要说的正是这一句。
        string reason = CanvasSaveReason();

        foreach (string dir in RecoveryDirs(failedPath))
        {
            string path = RecoveryPath(dir, failedPath);
            if (!CanvasStore.SaveFile(path, doc)) continue;

            // 已经不是"当前这份"了（用户已经切走、或退出流程已经往下走）：只把副本留在库里，
            // 绝不把指针改回来 —— 那会把用户刚才选的另一份画布顶掉。
            if (string.Equals(_currentCanvasPath, failedPath, StringComparison.OrdinalIgnoreCase))
            {
                _currentCanvasPath = path;
                _canvasDirty = false;
                _canvasSaveError = CanvasSaveError.None;
                AppendCanvasOrder(path);
                RefreshCanvasTree();
                UpdateCanvasDetachButton();
                UpdateCanvasRowBackgrounds();
                if (CanvasPanelSplitView is { IsPaneOpen: true }) ScrollCanvasRowIntoView(path);
                SaveCurrentViewState();   // 下次打开直接是副本，不会又回到那份写不进去的原件
            }
            ShowSaveToast(string.Format(LocalizationService.Get("CanvasSaveAdopted"), reason),
                          ToastIconKind.Success);
            return true;
        }
        return false;
    }

    /// <summary>副本的两个落点，按优先级：原文件所在目录（同层最好找）→ 画布库根（子目录被锁时的退路）。
    /// 两处都在库树内 ⇒ "能落盘"与"能在面板里高亮"永远同时成立，不会出现"存了但看不见"。</summary>
    private static string[] RecoveryDirs(string? failedPath)
    {
        string? own = null;
        if (!string.IsNullOrEmpty(failedPath))
        {
            try { own = Path.GetDirectoryName(failedPath); } catch { }
        }
        return own != null && !string.Equals(own, CanvasLibrary.Root, StringComparison.OrdinalIgnoreCase)
            ? new[] { own, CanvasLibrary.Root }
            : new[] { CanvasLibrary.Root };
    }

    /// <summary>副本文件名：原名 + " (recovered 2026-11-04 0935)"，与库里处理重名的 "名字 (2)" 同一风格。
    /// 带时间戳是因为同一份画布可能修好之后过几天又变成只读，不带的话两次副本会互相覆盖。</summary>
    private static string RecoveryPath(string dir, string? failedPath)
    {
        string baseName = "recovered";
        if (!string.IsNullOrEmpty(failedPath))
        {
            try
            {
                string n = Path.GetFileNameWithoutExtension(failedPath);
                if (!string.IsNullOrWhiteSpace(n)) baseName = n;
            }
            catch { }
        }

        string name = $"{baseName} (recovered {DateTime.Now:yyyy-MM-dd HHmm})";
        string candidate = Path.Combine(dir, name + CanvasStore.Extension);
        for (int i = 2; File.Exists(candidate) && i < 100; i++)
            candidate = Path.Combine(dir, $"{name} ({i}){CanvasStore.Extension}");
        return candidate;
    }

    /// <summary>把 <see cref="_canvasSaveError"/> 翻成半句话（只写根因，不写"该怎么办"）。
    /// 只分两类：只读是用户自己能立刻解决的一种（取消只读就能继续用原件），其余都归"无法写入"——
    /// 被占用 / 权限 / 磁盘满 / 不是我们的文件，对用户是同一件事：这次写不进去，改用副本。</summary>
    private string CanvasSaveReason() => LocalizationService.Get(
        _canvasSaveError == CanvasSaveError.TargetReadOnly ? "CanvasSaveReadOnly" : "CanvasSaveWrite");

    /// <summary>库内两处都写不进去时（磁盘满 / 库目录无写权限）唯一的出口：让用户自己挑一个位置存一份。
    /// 取消**不等于放弃内容** —— 只暂停自动重试（见 PauseCanvasSave），内容仍留在内存里且仍记脏，
    /// 用户可以按 Ctrl+S 再试一次，关软件时也会照常被问一次。
    /// 选择器可能落在库外，那是用户自己点的路径，不算"往别处散播"。</summary>
    private async Task<bool> PromptSaveCanvasElsewhereAsync(CanvasDoc doc, string? failedPath)
    {
        if (_canvasSavePromptOpen) return false;   // 选择器已经开着，别叠第二个
        if (_canvasSavePaused) return false;       // 已暂停：等用户显式保存，别自动重弹

        _canvasSavePromptOpen = true;
        ShowSaveToast(LocalizationService.Get("CanvasSaveNoLibrary"), ToastIconKind.Error);
        SuppressPenRing();
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            var app = Application.Current as App;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(app?.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeChoices.Add("Canvas", new List<string> { CanvasStore.Extension });
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(
                string.IsNullOrEmpty(failedPath) ? DefaultCanvasName() : failedPath);

            StorageFile file = await picker.PickSaveFileAsync();
            if (file == null) { PauseCanvasSave(failedPath); return false; }   // 取消：先存内存，回头再试

            if (!CanvasStore.SaveFile(file.Path, doc))
            {
                ShowSaveToast(LocalizationService.Get("CanvasSaveWrite"), ToastIconKind.Error);
                PauseCanvasSave(failedPath);
                return false;
            }

            // 只有"失败的那份仍然是当前画布"才把指针搬过去：选择器是模态的，但调用点
            // （切画布 / 切文档）可能已经换了指针，无条件搬会把用户刚选的那份顶掉。
            if (string.Equals(_currentCanvasPath, failedPath, StringComparison.OrdinalIgnoreCase))
            {
                _currentCanvasPath = file.Path;
                _canvasDirty = false;
                _canvasSaveError = CanvasSaveError.None;
                _canvasSavePaused = false;
                // 托管表里那一项由调用方（ResolveCanvasSaveFailureAsync）在落盘成功后撤掉，
                // 这里只把当前这份的状态对齐；提示与"有未保存"统一由那一次 Refresh 收尾。
                RefreshUnsavedState();
                SaveCurrentViewState();
                UpdateCanvasRowBackgrounds();
                UpdateCanvasDetachButton();
                // 选择器可能落在库外（用户自己挑的位置）：那份不会出现在右侧面板里，得说一声。
                SyncCanvasOutsideRootToast();
            }
            ShowSaveToast(LocalizationService.Get("SaveAsToastSuccess"), ToastIconKind.Success);   // 另存为成功
            return true;
        }
        catch
        {
            PauseCanvasSave(failedPath);
            return false;
        }
        finally
        {
            _canvasSavePromptOpen = false;
            ResumePenRing();
        }
    }

    /// <summary>暂停自动保存：内容仍留在内存、脏标记照旧（所以 Ctrl+S 与关闭询问都还能再存一次），
    /// 只是不再自动重试、不再反复弹选择器 —— 一次误点取消不该把整个会话的写盘能力锁死。
    /// 左下角立一枚常驻"未保存"，任何一次写入成功都会把它撤掉。
    /// 若调用点已经把画布切走，那份内容此刻在托管表里（不是丢掉），提示照立 —— 它仍然没落盘。</summary>
    private void PauseCanvasSave(string? failedPath)
    {
        // 失败的那份已经不是当前画布（用户已经切走）：不该顺手把新画布的自动保存也暂停掉 ——
        // 那份根本没写失败。它已经在托管表里，提示由 RefreshUnsavedState 一并立起来。
        if (string.Equals(_currentCanvasPath, failedPath, StringComparison.OrdinalIgnoreCase))
        {
            _canvasSavePaused = true;
            _canvasAutoSaveTimer?.Stop();
        }
        RefreshUnsavedState();
    }

    /// <summary>立一枚常驻"画布改动未保存"。它是持续状态而非一次性事件：
    /// 2 秒就消失的普通提示在这件事上没有意义 —— 用户恰恰是在这段时间里需要知道"还没存"。</summary>
    private void ShowCanvasUnsavedToast()
    {
        if (SaveToastStack == null || _canvasUnsavedToast != null) return;
        _canvasUnsavedToast = AddToastCapsule(
            LocalizationService.Get("CanvasUnsaved"), ToastIconKind.Warning, sticky: true);
    }

    /// <summary>写入真正成功后撤掉那枚常驻提示。</summary>
    private void ClearCanvasUnsavedToast()
    {
        if (_canvasUnsavedToast == null) return;
        SaveToastItem gone = _canvasUnsavedToast;
        _canvasUnsavedToast = null;
        DismissToast(gone, animate: true);
    }

    /// <summary>"有未保存内容"的唯一判定点：当前画布脏 / 托管表里还压着别的画布。
    /// 分散在各处手写赋值必然会漏掉后一项，而那几份恰恰最容易"已经切走、内容只在内存里"。
    /// 顺带把那枚常驻提示对齐到同一份状态，省得两处各判一次、判出两种结论。</summary>
    private void RefreshUnsavedState()
    {
        HasUnsavedChanges = _canvasDirty || _unsavedCanvasDocs.Count > 0;
        // 提示只在"确实写不进去"时立：普通的"刚改完、还没到 500ms 自动保存"每拖一下就闪一次，
        // 用户很快就不看它了。
        if (_canvasSavePaused || _unsavedCanvasDocs.Count > 0) ShowCanvasUnsavedToast();
        else ClearCanvasUnsavedToast();
    }

    /// <summary>把一份写不进去的画布内容托管起来（key = 它自己的文件路径）。
    /// 切画布 / 切文档都可能把当前模型换掉，托管之后那份内容才有地方待着、切回来才能恢复。</summary>
    private void StashUnsavedCanvas(string? path, CanvasDoc? doc)
    {
        if (doc == null || string.IsNullOrEmpty(path)) return;
        _unsavedCanvasDocs[path!] = doc;
        RefreshUnsavedState();
    }

    /// <summary>某份画布已经落盘（或内容确定作废），从托管表移除。</summary>
    private void DropUnsavedCanvas(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _unsavedCanvasDocs.Remove(path!);
        RefreshUnsavedState();
    }

    /// <summary>把托管表里每一份都再试一次写回原路径。换文档 / 退出前调用 ——
    /// 磁盘满解除之后，这一下通常就全好了，不必再逐份问用户。当前那份跳过：
    /// 它由 FlushCanvas 负责，那里面还要跟着清脏标记、解暂停、撤提示。</summary>
    private void RetryUnsavedCanvases()
    {
        foreach (var pair in _unsavedCanvasDocs.ToList())
        {
            if (string.Equals(pair.Key, _currentCanvasPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (CanvasStore.SaveFile(pair.Key, pair.Value)) _unsavedCanvasDocs.Remove(pair.Key);
        }
        RefreshUnsavedState();
    }

    /// <summary>卸下当前画布：只断开"这份文档盖着这份画布"这层关系，画布文件本身留在库里不动。
    /// 卸下后与"从没盖过画布"完全同一条路径 —— 再双击空白处就会新建一份（见 EnsureCanvasFile）。</summary>
    private void DetachCanvas()
    {
        if (string.IsNullOrEmpty(_currentCanvasPath)) return;
        ApplyCanvas(null);
        ShowSaveToast(LocalizationService.Get("CanvasDetached"), ToastIconKind.Success);
    }

    private void DetachCanvas_Click(object sender, RoutedEventArgs e)
    {
        // 与模式键共用 ToggleButton 外观：点击自动翻上的 IsChecked 立刻复位，免得亮成"当前模式"紫。
        if (DetachCanvasButton != null) DetachCanvasButton.IsChecked = false;
        DetachCanvas();
    }

    /// <summary>卸下按钮只在"这份文档盖着画布"时出现：没有画布可卸的时候它只会是个失效控件。</summary>
    private void UpdateCanvasDetachButton()
    {
        if (DetachCanvasButton == null) return;
        DetachCanvasButton.Visibility = string.IsNullOrEmpty(_currentCanvasPath)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // ---------------- 右侧画布面板（画布库） ----------------

    /// <summary>右侧画布面板开关。与左侧边栏同一套"不改变阅读位置"的处理：翻转前先冻结视口锚点，
    /// 展开/收起后按新视口重锚 —— 否则面板进出时 PDF 会整体跳一下。</summary>
    private void ToggleCanvasPanel_Click(object sender, RoutedEventArgs e)
    {
        // 欢迎页（未打开文档）时与左侧按钮一致：按钮可见但点击完全无效。
        if (CanvasPanelSplitView == null || SidebarSplitView == null || SidebarSplitView.Visibility != Visibility.Visible)
        {
            if (ToggleCanvasPanelButton != null) ToggleCanvasPanelButton.IsChecked = CanvasPanelSplitView?.IsPaneOpen ?? false;
            return;
        }
        GpuViewer?.CaptureHostResizeAnchor();
        CanvasPanelSplitView.IsPaneOpen = !CanvasPanelSplitView.IsPaneOpen;
        if (ToggleCanvasPanelButton != null) ToggleCanvasPanelButton.IsChecked = CanvasPanelSplitView.IsPaneOpen;
        // 每次展开都重扫目录：用户可能在文件管理器里新建/删掉过画布，面板不该只显示启动时的快照。
        if (CanvasPanelSplitView.IsPaneOpen)
        {
            RefreshCanvasTree();
            // 展开后把上次退出时的视口位置放回去（横向纵向都算）。
            // 只在"这次是打开动作"时做，磁盘监听之类的重扫不会把用户刚滚到的位置弹回去。
            ApplySavedCanvasPanelScroll();
        }
    }

    /// <summary>已展开的文件夹路径。展开是模型上的属性，重建/同步都按它恢复。</summary>
    private readonly HashSet<string> _expandedCanvasFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>根层节点（树）。整棵树只在"打开文档 / 展开面板"时重建，磁盘变动走 RefreshCanvasFolder 打补丁。</summary>
    private ObservableCollection<CanvasTreeNode> _canvasRoots = new();

    /// <summary>拍平后的可见行（展开的文件夹把子项摊进来）。列表只认这一份 —— 层级完全由我们自己算，
    /// 不借框架的树控件，也就不存在"框架的选中态/焦点态"来抢高亮、抢键盘。</summary>
    private readonly ObservableCollection<CanvasTreeNode> _canvasRows = new();

    /// <summary>指针悬浮 / 按下的行。激活行由"当前画布路径"决定，三者互斥后统一上色。</summary>
    private CanvasTreeNode? _hoverCanvasNode;
    private CanvasTreeNode? _pressedCanvasNode;

    /// <summary>多选集合（Ctrl 点选 / Shift 连选）。它是纯 UI 状态，不放模型里：
    /// 行节点实例在增量同步中是复用的，所以按引用记就够，不必给节点加一个"选中"字段。
    /// 语义上只表示"这一批行被勾中、批量操作作用于此"，与"哪份画布盖在 PDF 上"（激活行）互不替代。</summary>
    private readonly HashSet<CanvasTreeNode> _selectedCanvasNodes = new();

    /// <summary>连选的锚点：Shift+点击的起点。每次"普通点击 / Ctrl 点选"重定锚，
    /// Shift 点击只挪范围不挪锚 —— 否则连按几次 Shift 会打滑（每次都从上一格重新起算）。</summary>
    private CanvasTreeNode? _canvasSelectionAnchor;

    /// <summary>当前选择是"普通点击选出的单个"还是"Ctrl/Shift 选出的批"。
    /// 只影响底色：单个走中性灰底（与旧的选中态一致），成批走强调紫底。
    /// 它不参与"哪些行被选中"的判定 —— 那个只有 _selectedCanvasNodes 一份真相。
    /// 普通点击会把这一行真的放进集合，所以"点 A 再 Ctrl 点 B"天然包含 A，不需要额外的补录状态。</summary>
    private bool _canvasSingleSelection;

    private SolidColorBrush? _canvasRowHoverBrush;
    private SolidColorBrush? _canvasRowPressedBrush;
    private SolidColorBrush? _canvasRowSelectedBrush;
    private SolidColorBrush? _canvasRowMultiSelectBrush;
    private SolidColorBrush? _canvasRowTextBrush;
    private SolidColorBrush? _canvasRowTextActiveBrush;

    /// <summary>整棵树重建（换文档、展开面板时用）。</summary>
    private void RefreshCanvasTree()
    {
        if (CanvasRowList == null) return;
        _canvasRoots = CanvasLibrary.BuildTree(_expandedCanvasFolders, _settingsService.CanvasOrder);
        NormalizeCanvasOrderTree(_canvasRoots);   // 建完就把顺序表钉死：此后只有拖拽能改相对次序
        SyncCanvasRows();
    }

    /// <summary>把上次退出时的视口位置放回去（横向纵向都算）。
    /// 面板刚展开时 SplitView 还在做展开动画、ListView 也还没落版 —— 此刻视口宽高是 0，
    /// ChangeView 会被夹成 0，等于白还原，所以按 50ms 跨帧重试，等真的有了视口再滚。</summary>
    private void ApplySavedCanvasPanelScroll()
    {
        if (CanvasRowList == null) return;
        if (CanvasPanelSplitView is not { IsPaneOpen: true }) return;

        double x = _settingsService.CanvasPanelScrollX;
        double y = _settingsService.CanvasPanelScrollY;
        if (x <= 0.5 && y <= 0.5) return;   // 本来就在左上角，没什么可还原

        RetryOnTimer(() =>
        {
            if (CanvasRowList == null || CanvasPanelSplitView is not { IsPaneOpen: true }) return true;   // 面板都关了，放弃
            CanvasRowList.UpdateLayout();
            if (FindDescendantScrollViewer(CanvasRowList) is not { } sv ||
                sv.ViewportWidth <= 0 || sv.ViewportHeight <= 0) return false;

            // 光有视口不够：横向 extent 取决于虚拟化后已实例化的最宽一行，刚展开时还没长够，
            // 这时 ChangeView 会被悄悄夹到当前最大值 —— 等到长够了再下结论，别把夹小的值当成已还原。
            bool ready = sv.ScrollableWidth + 0.5 >= x && sv.ScrollableHeight + 0.5 >= y;
            sv.ChangeView(x, y, null, disableAnimation: true);
            return ready;
        }, 20);
    }

    /// <summary>按 50ms 间隔重试，直到 <paramref name="attempt"/> 返回 true 或用尽次数（用尽时跑 onGiveUp）。
    /// 必须跨帧重试：面板展开动画、虚拟化行的实例化都要等下一轮（甚至几十轮）布局之后才就绪，
    /// 而往调度队列里排 Low 优先级仍然是"同一帧内"的事 —— 排几次都还是没就绪。</summary>
    private void RetryOnTimer(Func<bool> attempt, int tries, Action? onGiveUp = null)
    {
        if (attempt()) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (s, e) =>
        {
            if (attempt()) { timer.Stop(); return; }
            if (--tries > 0) return;
            timer.Stop();
            onGiveUp?.Invoke();
        };
        timer.Start();
    }

    /// <summary>退出时把右侧面板的会话状态（展开的文件夹 + 视口位置）一次性写回设置。
    /// 平时一概不写盘：滚动是高频事件，逐次落盘只为记住"最终停在哪儿"，那是退出时才知道的事。
    /// 取不到有效视口（面板没开 / 已拆解）就沿用上次存的偏移，绝不拿 0 把用户的位置抹掉。</summary>
    private void SaveCanvasPanelStateAtExit(bool allowScroll)
    {
        double x = _settingsService.CanvasPanelScrollX;
        double y = _settingsService.CanvasPanelScrollY;

        if (allowScroll && CanvasPanelSplitView is { IsPaneOpen: true } && CanvasRowList != null &&
            FindDescendantScrollViewer(CanvasRowList) is { } sv &&
            sv.ViewportWidth > 0 && sv.ViewportHeight > 0)
        {
            x = sv.HorizontalOffset;
            y = sv.VerticalOffset;
        }

        _settingsService.SaveCanvasPanelState(_expandedCanvasFolders, x, y);
    }

    /// <summary>只同步库里某一个目录这一层（磁盘变动用）：只增删变动的那几行，其余行、展开态、滚动位置都不动。
    /// 找不到该目录（上级被整体删掉/改名）时退回整树重建。</summary>
    private void RefreshCanvasFolder(string dirPath)
    {
        if (!PatchCanvasFolder(dirPath)) { RefreshCanvasTree(); return; }
        SyncCanvasRows();
    }

    /// <summary>刷新目录的"前半段"：把这一层的磁盘现状同步进树，但不拍平、不动列表。
    /// 拆出来是给批量删除用的 —— 一次删掉多项时会影响多个目录，逐目录调 RefreshCanvasFolder
    /// 会把整棵列表拍平 N 遍（每遍都是 O(行数×IndexOf)），行越多越明显。
    /// 返回 false = 树里找不到这个目录，调用方应整树重建。</summary>
    private bool PatchCanvasFolder(string dirPath)
    {
        if (CanvasRowList == null) return false;

        ObservableCollection<CanvasTreeNode>? target;
        int depth;
        if (string.Equals(dirPath, CanvasLibrary.Root, StringComparison.OrdinalIgnoreCase))
        {
            target = _canvasRoots;
            depth = 0;
        }
        else
        {
            var folder = FindCanvasFolder(_canvasRoots, dirPath);
            target = folder?.Children;
            depth = (folder?.Depth ?? 0) + 1;
        }
        if (target == null) return false;

        CanvasLibrary.SyncChildren(target, dirPath, _expandedCanvasFolders, _settingsService.CanvasOrder, depth);
        NormalizeCanvasOrder(dirPath, target);   // 这一层同样钉死：新出现的一律追加到末尾
        return true;
    }

    /// <summary>把树拍平成可见行并就地同步进列表（增删而非整体替换）：容器与滚动位置都保住。</summary>
    private void SyncCanvasRows()
    {
        var desired = new List<CanvasTreeNode>();
        FlattenCanvasRows(_canvasRoots, desired);

        for (int i = _canvasRows.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(_canvasRows[i])) _canvasRows.RemoveAt(i);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            if (i < _canvasRows.Count && ReferenceEquals(_canvasRows[i], desired[i])) continue;

            int found = _canvasRows.IndexOf(desired[i]);
            if (found >= 0) _canvasRows.Move(found, i);
            else _canvasRows.Insert(i, desired[i]);
        }

        // 悬浮/按下的行若已经不在列表里（收起、删除），别把状态挂在它身上
        if (_hoverCanvasNode != null && !_canvasRows.Contains(_hoverCanvasNode)) _hoverCanvasNode = null;
        if (_pressedCanvasNode != null && !_canvasRows.Contains(_pressedCanvasNode)) _pressedCanvasNode = null;

        // 选中集合同理：删除、改名（换路径 ⇒ 换节点实例）、收起父文件夹都会让旧节点从行集合里消失。
        // 不剪枝的话集合会越攒越大，批量删除时还会去删一个已经不存在的路径。
        if (_selectedCanvasNodes.Count > 0) _selectedCanvasNodes.RemoveWhere(n => !_canvasRows.Contains(n));
        if (_canvasSelectionAnchor != null && !_canvasRows.Contains(_canvasSelectionAnchor))
            _canvasSelectionAnchor = null;
        if (_selectedCanvasNodes.Count == 0) _canvasSingleSelection = false;   // 空集合没有"单选/多选"可言

        UpdateCanvasRowBackgrounds();
    }

    private static void FlattenCanvasRows(IEnumerable<CanvasTreeNode> nodes, List<CanvasTreeNode> into)
    {
        foreach (var n in nodes)
        {
            // 已提交删除的行（含整棵子树）不再出行：删除的耗时全在磁盘那一步，
            // 把它挪到后台之后，界面"点下去就少这几行"，不必等 shell 回来。
            if (n.IsPendingDelete) continue;
            into.Add(n);
            if (n.IsFolder && n.IsExpanded) FlattenCanvasRows(n.Children, into);
        }
    }

    /// <summary>行的状态分开表达，各自只走一条通道：
    /// · 激活（= 盖在 PDF 上的那份）→ 行首强调色竖条 + 名字文字提到最亮一档；
    /// · 选中（= 可以被批量操作的那几行）→ 整行底色：单选一行为中性灰，成批为强调紫。
    /// 底色只表达选中、不表达激活，所以"点 A"能同时给出灰底（选中）与竖条（开着），两件事各自可见。
    /// 底色优先级：选中 &gt; 按下 &gt; 悬浮，空白 = 透明。
    /// 文字亮度是"激活 vs 非激活"唯一的整行线索：层级深时竖条与名字会被缩进拉开几十像素，
    /// 光靠行首那条竖条关联不到名字，靠文字亮度差则不论多深都成立（且不与两种选中底色抢通道）。</summary>
    private void UpdateCanvasRowBackgrounds()
    {
        foreach (var row in _canvasRows)
        {
            row.IsActive = IsActiveCanvas(row);
            row.NameBrush = row.IsActive ? _canvasRowTextActiveBrush : _canvasRowTextBrush;
            row.RowBackground =
                _selectedCanvasNodes.Contains(row)
                    ? (_canvasSingleSelection ? _canvasRowSelectedBrush : _canvasRowMultiSelectBrush)
                : ReferenceEquals(row, _pressedCanvasNode) ? _canvasRowPressedBrush
                : ReferenceEquals(row, _hoverCanvasNode) ? _canvasRowHoverBrush
                : null;
        }
    }

    private bool IsActiveCanvas(CanvasTreeNode row)
        => !row.IsFolder
           && !string.IsNullOrEmpty(_currentCanvasPath)
           && string.Equals(row.FullPath, _currentCanvasPath, StringComparison.OrdinalIgnoreCase);

    private static CanvasTreeNode? FindCanvasFolder(IEnumerable<CanvasTreeNode> nodes, string path)
    {
        foreach (var n in nodes)
        {
            if (!n.IsFolder) continue;
            if (string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase)) return n;
            var hit = FindCanvasFolder(n.Children, path);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>画布里有卡片的页码超出本文档页数时，在左下角胶囊栈里常驻一枚"画布有缺失"。
    /// 那些卡片既不绘制也点不到 —— 静默吞掉会让用户以为卡片丢了；但提示不能进右侧列表（会把整块内容推下去）。
    /// 它走的是与保存提示【完全相同】的入栈路径（AddToastCapsule：同位次、同入场动画、同格距、同构造器），
    /// 只是 Sticky —— 不计时、点了不消失、不参与栈深淘汰；新提示进来把它往上挤一格，提示走光它再落回最底格。</summary>
    private void SyncCanvasMissingToast()
    {
        if (SaveToastStack == null) return;

        int pages = (int)_totalPages;   // _totalPages 是 uint（引擎返回无符号），这里只用于比较
        bool missing = false;
        if (_canvasDoc != null && pages > 0)
        {
            foreach (var n in _canvasDoc.Nodes)
            {
                if (n.GlancePage >= pages) { missing = true; break; }
            }
        }

        bool dark = this.ActualTheme == ElementTheme.Dark;
        // 换主题要重建这一枚：图标色是构造时按主题算死的（底板/描边是 ThemeResource，会自动跟随）。
        if (_canvasMissingToast != null && _canvasMissingToastDark != dark)
        {
            SaveToastItem stale = _canvasMissingToast;
            _canvasMissingToast = null;
            DismissToast(stale, animate: false);
        }

        if (missing && _canvasMissingToast == null)
        {
            _canvasMissingToast = AddToastCapsule(
                LocalizationService.Get("CanvasMissing"), ToastIconKind.Warning, sticky: true);
            _canvasMissingToastDark = dark;
        }
        else if (!missing && _canvasMissingToast != null)
        {
            SaveToastItem gone = _canvasMissingToast;
            _canvasMissingToast = null;
            DismissToast(gone, animate: true);
        }
    }

    /// <summary>当前挂着的画布落在画布库树【之外】时常驻一枚"画布在库外"。
    /// 这种画布只可能来自"库整个写不进去 → 另存为用户自己挑的位置"：它照常挂载、能继续编辑、
    /// 下次打开还是它，但右侧面板列的是库树里的文件 ⇒ 面板里根本没有它那一行。
    /// 不说明白，用户会以为画布丢了或者没保存成功（同样走常驻栈，与"画布缺失"同一套视觉与生命周期）。</summary>
    private void SyncCanvasOutsideRootToast()
    {
        if (SaveToastStack == null) return;

        bool outside = !string.IsNullOrEmpty(_currentCanvasPath)
                       && !CanvasLibrary.IsInsideRoot(_currentCanvasPath!);

        bool dark = this.ActualTheme == ElementTheme.Dark;
        // 换主题要重建这一枚：图标色是构造时按主题算死的（底板/描边是 ThemeResource，会自动跟随）。
        if (_canvasOutsideToast != null && _canvasOutsideToastDark != dark)
        {
            SaveToastItem stale = _canvasOutsideToast;
            _canvasOutsideToast = null;
            DismissToast(stale, animate: false);
        }

        if (outside && _canvasOutsideToast == null)
        {
            _canvasOutsideToast = AddToastCapsule(
                LocalizationService.Get("CanvasOutsideLibrary"), ToastIconKind.Warning, sticky: true);
            _canvasOutsideToastDark = dark;
        }
        else if (!outside && _canvasOutsideToast != null)
        {
            SaveToastItem gone = _canvasOutsideToast;
            _canvasOutsideToast = null;
            DismissToast(gone, animate: true);
        }
    }

    /// <summary>整行按下：这里只落"选中"，主动作（打开画布 / 展开文件夹）留到抬起时结算 —— 见 CommitCanvasPress。
    /// 不在这里执行，是因为长按拖拽要占用同一次按下：按下就展开文件夹会当场重排行集合，
    /// 拖拽还没起飞落点就已经换了一套坐标；按下就切画布则会让一次拖拽顺手把画布换掉（还要整份重新解析）。
    /// 带 Ctrl / Shift 时只改多选、不执行行本身的主动作：否则连选几下会把画布来回换、把文件夹开开关关。</summary>
    private void CanvasRow_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 只认左键：右键也要走 PointerPressed，不挡的话右键菜单一弹就把文件夹收起来/把画布切走。
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (RowNodeOf(sender) is not { } node || node.IsRenaming) return;

        _pressedCanvasNode = node;
        UpdateCanvasRowBackgrounds();

        bool ctrl = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);

        if (ctrl || shift)
        {
            // Shift 优先：Ctrl+Shift 是"把这一整段并进已有选择"，只有 Ctrl 才是点选切换。
            if (shift) ExtendCanvasSelection(node, add: ctrl);
            else ToggleCanvasSelection(node);
            return;
        }

        SelectOnlyCanvasNode(node);
        BeginCanvasPress(node, sender as FrameworkElement, e);
    }

    /// <summary>按下时记账：起点坐标、抓取点在行内的纵向位置、待结算的节点，然后起长按定时器。
    /// 这三样东西就是这次按下期间的全部状态 —— 抬起时据此判断"是点了一下还是拖了一下"。</summary>
    private void BeginCanvasPress(CanvasTreeNode node, FrameworkElement? container, PointerRoutedEventArgs e)
    {
        Point pt = e.GetCurrentPoint(this).Position;
        _canvasPressNode = node;
        _canvasDragPressPoint = pt;
        _canvasDragPointer = e.Pointer;
        _canvasDragMoved = false;

        // 抓取点在行内的纵向位置：幽灵跟手时保持这条"抓取线"，不会一跳就偏到指针正下方。
        double grabY = pt.Y;
        if (container != null)
        {
            try { grabY = pt.Y - container.TransformToVisual(this).TransformPoint(new Point(0, 0)).Y; }
            catch (Exception) { }   // 换算不出来（容器刚被移出树）：退回绝对坐标，幽灵仍跟着指针
        }
        _canvasDragGrabY = Math.Clamp(grabY, 0, 24);

        StartCanvasHoldTimer();
    }

    /// <summary>抬起的结算点：把按下时推迟的动作补上 —— 没拖动过才算"点了一下"。
    /// 由 CanvasRowList_PointerReleased 调用（抬起事件挂在列表上，行容器被重建也不会丢）。
    /// 动作按【节点引用】执行、不按容器：这条路径成立的前提是按下到抬起之间没有人动过行集合。</summary>
    private void CommitCanvasPress()
    {
        CanvasTreeNode? node = _canvasPressNode;
        _canvasPressNode = null;
        if (node == null || _canvasDragMoved) return;
        if (!_canvasRows.Contains(node) || node.IsRenaming) return;   // 期间被删/改名/收起了

        if (!node.IsFolder) { ApplyCanvas(node.FullPath); return; }

        node.IsExpanded = !node.IsExpanded;
        if (node.IsExpanded) _expandedCanvasFolders.Add(node.FullPath);
        else _expandedCanvasFolders.Remove(node.FullPath);
        SyncCanvasRows();
    }

    /// <summary>普通点击：把选择重置成"只有这一行"，并用中性灰底标出来（不是紫底 —— 紫底留给成批选择）。
    /// 因为激活态走行首竖条、不占底色，普通点击可以放心地选中自己。
    /// 这一行真的进了 _selectedCanvasNodes，所以紧接着 Ctrl 点另一行时它天然被包含。</summary>
    private void SelectOnlyCanvasNode(CanvasTreeNode node)
    {
        _canvasSelectionAnchor = node;
        _canvasSingleSelection = true;
        _selectedCanvasNodes.Clear();
        _selectedCanvasNodes.Add(node);
        UpdateCanvasRowBackgrounds();
    }

    private void ClearCanvasSelection()
    {
        _canvasSelectionAnchor = null;
        _canvasSingleSelection = false;
        _selectedCanvasNodes.Clear();
        UpdateCanvasRowBackgrounds();
    }

    /// <summary>Ctrl+点击：点谁勾谁，其余不动；再点一次取消。锚点跟着挪到这一行。
    /// 一按 Ctrl 就退出"单选"档，整批改用紫底 —— 于是"点 A、Ctrl 点 B"两行都是紫的，
    /// 与"点 A 之后点过空白"（集合已空，只会选中 B）区分得开。
    /// 特例：灰底的那一行本来就"选中着"，Ctrl 点它是想把它并进多选（第一时间变紫），
    /// 不是想取消它 —— 所以这一次只切档、不动成员；再点一次才真的取消。</summary>
    private void ToggleCanvasSelection(CanvasTreeNode node)
    {
        if (_canvasSingleSelection && _selectedCanvasNodes.Count == 1 && _selectedCanvasNodes.Contains(node))
        {
            _canvasSingleSelection = false;
            _canvasSelectionAnchor = node;
            UpdateCanvasRowBackgrounds();
            return;
        }

        _canvasSingleSelection = false;
        if (!_selectedCanvasNodes.Remove(node)) _selectedCanvasNodes.Add(node);
        _canvasSelectionAnchor = node;
        UpdateCanvasRowBackgrounds();
    }

    /// <summary>Shift+点击：从锚点连选到这一行（按【可见行】顺序 —— 收起的文件夹里的行不参与，
    /// 因为用户根本看不见它们）。Ctrl+Shift = 并进已有选择，单 Shift = 替换。
    /// 锚点不动，连按几次 Shift 只会伸缩同一段范围。</summary>
    private void ExtendCanvasSelection(CanvasTreeNode node, bool add)
    {
        int to = _canvasRows.IndexOf(node);
        int from = _canvasSelectionAnchor != null ? _canvasRows.IndexOf(_canvasSelectionAnchor) : -1;
        if (to < 0) return;

        _canvasSingleSelection = false;
        if (!add) _selectedCanvasNodes.Clear();

        if (from < 0)   // 没有锚点（或锚点所在的行已不在列表里）：退化成"只勾这一行"
        {
            _selectedCanvasNodes.Add(node);
            _canvasSelectionAnchor = node;
            UpdateCanvasRowBackgrounds();
            return;
        }

        for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) _selectedCanvasNodes.Add(_canvasRows[i]);
        UpdateCanvasRowBackgrounds();
    }

    private void CanvasRow_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pressedCanvasNode == null) return;
        _pressedCanvasNode = null;
        UpdateCanvasRowBackgrounds();
    }

    private void CanvasRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (RowNodeOf(sender) is not { } node) return;
        _hoverCanvasNode = node;
        UpdateCanvasRowBackgrounds();
    }

    private void CanvasRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (RowNodeOf(sender) is not { } node) return;
        // 右键菜单盖在行上时框架会补送一次 PointerExited（指针的命中目标换成了菜单）：
        // 菜单开着期间保持悬浮态不动，否则"右键弹菜单 → 背后那行立刻掉色"（用户报告过）。
        if (CanvasContextMenu is { IsOpen: true }) return;
        if (ReferenceEquals(_hoverCanvasNode, node)) _hoverCanvasNode = null;
        if (ReferenceEquals(_pressedCanvasNode, node)) _pressedCanvasNode = null;
        UpdateCanvasRowBackgrounds();
    }

    // ---------------- 行长按拖拽排序 ----------------
    //
    // 一次拖拽分三段，界限只看指针：按下 → 长按定时器（CanvasDragHoldMs）到点且没移动过 ⇒ 起飞；
    // 期间指针移动超过 CanvasDragSlop ⇒ 认定是滚动手势，长按作废、这次按下只剩"选中"；
    // 起飞后指针捕获在【列表】上，移动抬起来自列表而不是某一行（行容器随时可能被重建）。
    // 落点由"指针 Y 落在哪一行的哪一段"决定：文件夹行中段 = 放进它里面（画描边环），
    // 其余 = 插在两行之间（画一条对齐该层缩进的细线）。落点一律吸附到行的边界上，
    // 不跟指针连续移动 —— 提示必须落在"会变成什么样"的位置，而不是指针在哪就在哪。

    /// <summary>长按多久起飞。太短会和"点一下"抢，太长会让人觉得卡。</summary>
    private const int CanvasDragHoldMs = 380;

    /// <summary>起飞前允许的指针抖动（像素）。超了就当滚动/选择手势，不再起拖。</summary>
    private const double CanvasDragSlop = 5;

    /// <summary>贴边自动滚动的触发带高与每 30ms 滚动的像素数。</summary>
    private const double CanvasDragEdgeZone = 26;
    private const double CanvasDragScrollStep = 12;

    /// <summary>幽灵的高度与不透明度。<see cref="CanvasDragGhostHeight"/> 必须与行高
    /// （<c>CanvasRowTemplate</c> 根 Grid 的 <c>MinHeight</c>）和幽灵自身的 <c>Height</c> 三处同值，
    /// 否则吸附位置与行的竖向居中对不上。
    /// 不透明度 &lt; 1 是刻意的：它浮在落点提示上面，全不透明会把自己下方那条线整段吃掉。</summary>
    private const double CanvasDragGhostHeight = 22;
    private const double CanvasDragGhostOpacity = 0.85;

    private DispatcherTimer? _canvasHoldTimer;
    private CanvasTreeNode? _canvasPressNode;        // 本次按下待结算的行（点击与拖拽共用这一个身份）
    private CanvasTreeNode? _canvasDragNode;         // 已经起飞的那一行
    private Point _canvasDragPressPoint;             // 按下时的页面坐标
    private Microsoft.UI.Xaml.Input.Pointer? _canvasDragPointer;
    private double _canvasDragGrabY;                 // 按下点在行内的纵向位置（幽灵保持这条抓取线）
    private bool _canvasDragMoved;                   // 本次按下期间指针是否已越过门槛
    private bool _canvasDragActive;
    private Point _canvasDragLastPoint;

    private DispatcherTimer? _canvasAutoScrollTimer;
    private double _canvasAutoScrollDelta;

    // 落点：解析出来先存下，落手（抬指）时才真正执行 —— 抬指那一刻指针可能已经不在原位。
    private string? _canvasDropDir;                  // 落点所在目录
    private int _canvasDropAt;                       // 在该目录子项里的插入位置（已剔除被拖的那一项）
    private bool _canvasDropInto;                    // 放进文件夹（画环）而不是插在两行之间（画线）
    private bool _canvasDropAfter;                   // 相对参照行插在它后面
    private int _canvasDropDepth;                    // 参照行的层级（线的左端缩进）
    private bool _canvasDropValid;
    private bool _canvasDropInBand;                  // 落点是列表上/下那条"库根"投放带
    private bool _canvasDropBandTop;                 // 命中的是上带（放最前）还是下带（放最末）
    private Rect _canvasDropRowRect;
    private Rect _canvasDropListRect;

    private void StartCanvasHoldTimer()
    {
        StopCanvasHoldTimer();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CanvasDragHoldMs) };
        timer.Tick += CanvasHoldElapsed;
        _canvasHoldTimer = timer;
        timer.Start();
    }

    private void StopCanvasHoldTimer()
    {
        if (_canvasHoldTimer == null) return;
        _canvasHoldTimer.Stop();
        _canvasHoldTimer.Tick -= CanvasHoldElapsed;
        _canvasHoldTimer = null;
    }

    /// <summary>长按到点：指针还按着、没移动过、这一行还在列表里 ⇒ 起飞。
    /// 三个条件缺一不可 —— 少判"还在列表里"就会去拖一行刚被删/被收起、已经不在树上的节点。</summary>
    private void CanvasHoldElapsed(object? sender, object e)
    {
        StopCanvasHoldTimer();
        if (_canvasDragMoved || _canvasDragActive) return;
        if (_canvasPressNode is not { } node || !_canvasRows.Contains(node)) return;
        BeginCanvasDrag(node);
    }

    private void CanvasRowList_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_canvasDragActive)
        {
            UpdateCanvasDrag(e.GetCurrentPoint(this).Position);
            return;
        }
        if (_canvasPressNode == null) return;

        Point pt = e.GetCurrentPoint(this).Position;
        double dx = pt.X - _canvasDragPressPoint.X;
        double dy = pt.Y - _canvasDragPressPoint.Y;
        if (dx * dx + dy * dy <= CanvasDragSlop * CanvasDragSlop) return;

        // 越过门槛 = 这是一次拖动（滚动 / 拉选），长按作废；这次按下从此只剩"选中"的语义。
        _canvasDragMoved = true;
        StopCanvasHoldTimer();
    }

    private void CanvasRowList_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        StopCanvasHoldTimer();
        if (_canvasDragActive) FinishCanvasDrag(commit: true);
        else CommitCanvasPress();

        // 起飞后指针捕获在列表上，行自己的 PointerReleased 收不到 ⇒ 按下态在这里一并清掉。
        // _canvasPressNode 必须一起清：它是"待结算的点击"，留着的话下一次右键抬起会把它补结算掉、
        // 于是"拖完一行再右键"会莫名其妙打开一份画布。
        _canvasPressNode = null;
        _pressedCanvasNode = null;
        UpdateCanvasRowBackgrounds();
    }

    /// <summary>捕获被夺走（切窗口、系统手势、别的元素抢了捕获）：按取消处理，不落盘。
    /// 宁可这次拖拽白做，也不能在"不知道指针最后在哪"的情况下改用户文件。</summary>
    private void CanvasRowList_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_canvasDragActive) return;
        FinishCanvasDrag(commit: false);
    }

    private void BeginCanvasDrag(CanvasTreeNode node)
    {
        if (CanvasDragLayer == null || CanvasDragGhost == null || CanvasRowList == null) return;

        _canvasDragActive = true;
        _canvasDragNode = node;
        _canvasDragLastPoint = _canvasDragPressPoint;
        _canvasDropValid = false;

        CanvasDragGhostIcon.Glyph = node.IsFolder ? "\uE8B7" : node.FileGlyph;
        CanvasDragGhostText.Text = node.Name;
        ApplyCanvasDragGhostTheme();

        CanvasDropLine.Visibility = Visibility.Collapsed;
        CanvasDropIntoRing.Visibility = Visibility.Collapsed;
        CanvasDragGhost.Opacity = CanvasDragGhostOpacity;
        CanvasDragGhost.Visibility = Visibility.Visible;
        CanvasDragLayer.Visibility = Visibility.Visible;

        UpdateCanvasGhostPosition(_canvasDragPressPoint);
        PlayCanvasGhostIntro();

        // 抓指针：之后整个拖拽期间的移动/抬起都投递到列表本身。
        if (_canvasDragPointer != null)
        {
            try { CanvasRowList.CapturePointer(_canvasDragPointer); } catch (Exception) { }
        }

        // 起飞后清掉悬浮高亮：它和落点提示抢同一块地方。
        _hoverCanvasNode = null;
        UpdateCanvasRowBackgrounds();
        UpdateCanvasDrag(_canvasDragPressPoint);
    }

    /// <summary>拖拽中的每一帧：幽灵跟手、贴边自动滚动、重算落点。
    /// 指针已经离开列表范围（比如挪到 PDF 上）⇒ 没有落点，收掉提示但继续拖。</summary>
    private void UpdateCanvasDrag(Point pagePt)
    {
        if (!_canvasDragActive) return;
        _canvasDragLastPoint = pagePt;

        UpdateCanvasGhostPosition(pagePt);

        if (_canvasDragNode is not { } node || !_canvasRows.Contains(node))
        {
            // 拖着的那一行在拖拽期间消失了（外部删除 / 收起父文件夹）：直接收摊，别再改顺序。
            FinishCanvasDrag(commit: false);
            return;
        }

        Rect listRect = CanvasRowList.TransformToVisual(this).TransformBounds(
            new Rect(0, 0, CanvasRowList.ActualWidth, CanvasRowList.ActualHeight));

        bool resolved = TryResolveCanvasDrop(node, listRect, pagePt);

        // 落在列表下方那条投放带上时不自动滚动：那已经在列表之外，再滚只是徒劳地抖列表。
        if (resolved && _canvasDropInBand) StopCanvasAutoScroll();
        else UpdateCanvasAutoScroll(listRect, pagePt);

        if (!resolved) { ClearCanvasDropCue(); return; }
        ShowCanvasDropCue();
    }

    /// <summary>算落点：指针落在哪一行、那一行的哪一段；或者落在列表下方那条"库根末尾"投放带上。
    /// 行的位置一律用【容器投影到页面】的矩形，不按索引乘行高 —— 虚拟化、横向滚动、
    /// 未来改行高都不会让这套算法失效。</summary>
    private bool TryResolveCanvasDrop(CanvasTreeNode node, Rect listRect, Point pagePt)
    {
        // 横向偏出列表太多（拖到 PDF 区上）就没有落点；纵向给一点溢出容忍，
        // 贴边自动滚动时不会在"有落点/没落点"之间闪。
        if (pagePt.X < listRect.X - 40 || pagePt.X > listRect.Right + 40) return false;

        // ---- 列表下方那条恒定投放带：落上去 = 放到库根最末 ----
        // 先于行判断：它紧贴列表下沿，不属于任何一行，语义只有一个 —— "进根目录，排最后"。
        _canvasDropInBand = false;
        if (TryResolveRootDropBand(node, listRect, pagePt)) return true;

        if (pagePt.Y < listRect.Y - 24 || pagePt.Y > listRect.Bottom + 24) return false;

        int hit = -1, first = -1, last = -1;
        Rect hitRect = default, firstRect = default, lastRect = default;

        for (int i = 0; i < _canvasRows.Count; i++)
        {
            if (CanvasRowList.ContainerFromIndex(i) is not FrameworkElement fe || fe.ActualHeight <= 0) continue;
            Rect r = fe.TransformToVisual(this).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
            if (first < 0) { first = i; firstRect = r; }
            last = i; lastRect = r;
            if (hit < 0 && pagePt.Y >= r.Y && pagePt.Y < r.Bottom) { hit = i; hitRect = r; }
        }
        if (first < 0) return false;

        bool clamped = hit < 0;
        if (clamped)
        {
            // 落在首行之上 / 末行之下（含滚动余量里）：吸附到最近的那一头。
            if (pagePt.Y <= firstRect.Y) { hit = first; hitRect = firstRect; }
            else { hit = last; hitRect = lastRect; }
        }

        CanvasTreeNode row = _canvasRows[hit];
        double rel = Math.Clamp((pagePt.Y - hitRect.Y) / Math.Max(1, hitRect.Height), 0, 1);

        // 磁吸判据：文件夹行的中段 44% = "放进去"，上下两端 = "插在它前后"。
        // 中段给得比一半以内更宽，是指针不必精确停在行中央就能进去；文件行只有一种语义，以中线为界。
        bool into = row.IsFolder && rel > 0.28 && rel < 0.72;
        bool after = clamped ? pagePt.Y > firstRect.Y : rel >= 0.5;

        string dir = into ? row.FullPath : (Path.GetDirectoryName(row.FullPath) ?? CanvasLibrary.Root);

        // 文件夹不能搬进自己或自己的子孙（否则整棵子树会从树上消失）。
        if (node.IsFolder && IsSameOrUnder(dir, node.FullPath)) return false;

        string? srcDir = Path.GetDirectoryName(node.FullPath);
        bool sameDir = string.Equals(srcDir, dir, StringComparison.OrdinalIgnoreCase);

        // 跨层时目标已有同名项：系统会拒绝，提前判掉，别让提示画在会失败的位置上。
        if (!sameDir)
        {
            string fileName = Path.GetFileName(node.FullPath);
            try
            {
                if (File.Exists(Path.Combine(dir, fileName)) || Directory.Exists(Path.Combine(dir, fileName))) return false;
            }
            catch (Exception) { return false; }
        }

        var siblings = new List<CanvasTreeNode>(SiblingNodesOf(dir));
        int at;
        if (into)
        {
            // 放进文件夹 = 排到它的子项末尾；被拖的那一项若本来就在里面，先摘掉再定位。
            siblings.RemoveAll(n => ReferenceEquals(n, node));
            at = siblings.Count;
        }
        else
        {
            int idx = siblings.IndexOf(row);
            if (idx < 0) return false;
            at = idx + (after ? 1 : 0);
            int self = siblings.IndexOf(node);
            if (self >= 0)
            {
                siblings.RemoveAt(self);
                if (self < at) at--;    // 自己先被摘掉，落点整体前移一格
            }
        }

        _canvasDropDir = dir;
        _canvasDropAt = at;
        _canvasDropInto = into;
        _canvasDropAfter = after;
        _canvasDropDepth = row.Depth;
        _canvasDropRowRect = hitRect;
        _canvasDropListRect = listRect;
        _canvasDropValid = true;
        return true;
    }

    private void ShowCanvasDropCue()
    {
        Rect listRect = _canvasDropListRect;

        // 投放带只在被命中时染色：平时它们是列表上下的一段纯留白，不做任何视觉声明。
        ApplyCanvasRootBandTint();

        if (_canvasDropInto)
        {
            CanvasDropLine.Visibility = Visibility.Collapsed;
            CanvasDropIntoRing.Visibility = Visibility.Visible;
            Canvas.SetLeft(CanvasDropIntoRing, listRect.X);
            Canvas.SetTop(CanvasDropIntoRing, _canvasDropRowRect.Y + 1);
            CanvasDropIntoRing.Width = Math.Max(8, listRect.Width - 2);
            CanvasDropIntoRing.Height = Math.Max(8, _canvasDropRowRect.Height - 2);
            return;
        }

        CanvasDropIntoRing.Visibility = Visibility.Collapsed;
        CanvasDropLine.Visibility = Visibility.Visible;

        // 线的左端对齐"新位置所在层"的缩进：行模板是 2 边距 + 12 缩进/级 + 12 箭头列，
        // 所以落点层的名字起点在 14 + 12×层级 —— 线画在那里才看得出会插进第几层。
        double x = listRect.X + 14 + _canvasDropDepth * 12;
        double y = (_canvasDropAfter ? _canvasDropRowRect.Bottom : _canvasDropRowRect.Y) - 1;
        Canvas.SetLeft(CanvasDropLine, x);
        Canvas.SetTop(CanvasDropLine, y);
        CanvasDropLine.Width = Math.Max(8, listRect.Right - 10 - x);
    }

    private void ClearCanvasDropCue()
    {
        _canvasDropValid = false;
        _canvasDropInBand = false;
        _canvasDropBandTop = false;
        if (CanvasDropLine != null) CanvasDropLine.Visibility = Visibility.Collapsed;
        if (CanvasDropIntoRing != null) CanvasDropIntoRing.Visibility = Visibility.Collapsed;
        ApplyCanvasRootBandTint();
    }

    /// <summary>上/下投放带的染色：只有当前命中的那一条亮起来，另一条始终透明。
    /// 亮起来时一并显出箭头（上带向上、下带向下）：光有底色只说"这里能放"，
    /// 说不出"放到哪儿"——两头都是库根，区别全在方向。</summary>
    private void ApplyCanvasRootBandTint()
    {
        bool top = _canvasDropInBand && _canvasDropBandTop;
        bool bottom = _canvasDropInBand && !_canvasDropBandTop;

        if (CanvasRootTopDropZone != null)
            CanvasRootTopDropZone.Background = top ? _canvasRowMultiSelectBrush : null;
        if (CanvasRootDropZone != null)
            CanvasRootDropZone.Background = bottom ? _canvasRowMultiSelectBrush : null;

        if (CanvasRootTopDropGlyph != null)
            CanvasRootTopDropGlyph.Visibility = top ? Visibility.Visible : Visibility.Collapsed;
        if (CanvasRootDropGlyph != null)
            CanvasRootDropGlyph.Visibility = bottom ? Visibility.Visible : Visibility.Collapsed;

        // 顶部那条带子就是"画布"标题行：悬停时箭头顶替标题文字（两者都在左侧同一处，不能叠在一起）。
        if (CanvasPanelHeaderText != null)
            CanvasPanelHeaderText.Visibility = top ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>库根的"最前 / 最末"两条恒定投放区：上 = 面板标题行（24 高，不参与滚动、不占可视区），
    /// 下 = 列表下沿那条带（24 高）。存在的理由：最末一行若是【展开文件夹】里的子项，
    /// 列表整条下边界都会被"进文件夹"吃掉，用户没有任何位置能表达"放到根目录末尾"；
    /// 首行同理（被滚到中间时够不着"最前"）。两条平时全透明，命中时才染色 + 显方向箭头。</summary>
    private bool TryResolveRootDropBand(CanvasTreeNode node, Rect listRect, Point pagePt)
    {
        if (TryResolveRootDropEdge(node, listRect, pagePt, CanvasRootTopDropZone, atTop: true)) return true;
        if (TryResolveRootDropEdge(node, listRect, pagePt, CanvasRootDropZone, atTop: false)) return true;
        return false;
    }

    private bool TryResolveRootDropEdge(CanvasTreeNode node, Rect listRect, Point pagePt,
                                        Border? zone, bool atTop)
    {
        if (zone is not { ActualHeight: > 0 }) return false;

        Rect band = zone.TransformToVisual(this).TransformBounds(
            new Rect(0, 0, zone.ActualWidth, zone.ActualHeight));
        if (pagePt.Y < band.Y || pagePt.Y >= band.Bottom) return false;
        if (pagePt.X < listRect.X - 40 || pagePt.X > listRect.Right + 40) return false;

        // 已经在库根里 = 只是排序，不涉及同名；从别处拖来则目标已有同名项时系统会拒绝，提前判掉。
        bool sameDir = string.Equals(Path.GetDirectoryName(node.FullPath), CanvasLibrary.Root,
                                     StringComparison.OrdinalIgnoreCase);
        if (!sameDir)
        {
            string fileName = Path.GetFileName(node.FullPath);
            try
            {
                if (File.Exists(Path.Combine(CanvasLibrary.Root, fileName)) ||
                    Directory.Exists(Path.Combine(CanvasLibrary.Root, fileName))) return false;
            }
            catch (Exception) { return false; }
        }

        var rootSiblings = new List<CanvasTreeNode>(SiblingNodesOf(CanvasLibrary.Root));
        rootSiblings.RemoveAll(n => ReferenceEquals(n, node));

        _canvasDropDir = CanvasLibrary.Root;
        _canvasDropAt = atTop ? 0 : rootSiblings.Count;
        _canvasDropInto = false;
        _canvasDropDepth = 0;                     // 根层：提示线按 0 级缩进画
        // 提示线：上带画在带子【下沿】（贴住第一行上方），下带画在带子【上沿】（贴住最后一行下方）。
        // ShowCanvasDropCue 用 "after ? Bottom : Y" 再减 1，所以把矩形设成带子本身、after 按方向给。
        _canvasDropAfter = atTop;
        _canvasDropRowRect = band;
        _canvasDropListRect = listRect;
        _canvasDropInBand = true;
        _canvasDropBandTop = atTop;
        _canvasDropValid = true;
        return true;
    }

    /// <summary>幽灵跟着指针走：垂向保持"按下时抓在行里的那一点"，横向偏到指针右侧一小段，
    /// 这样它既不挡住落点线，也不会在指针水平微动时忽左忽右地抖。
    /// 右侧只留 10px：幽灵本身窄（≤160），贴得越近越像"从行里拎起来"，也越不容易整片盖住落点提示。</summary>
    private void UpdateCanvasGhostPosition(Point pagePt)
    {
        if (CanvasDragGhost == null) return;
        double w = CanvasDragGhost.ActualWidth > 0 ? CanvasDragGhost.ActualWidth : 120;
        double x = pagePt.X + 10;
        if (ActualWidth > w) x = Math.Clamp(x, 0, ActualWidth - w - 6);
        Canvas.SetLeft(CanvasDragGhost, x);
        Canvas.SetTop(CanvasDragGhost, pagePt.Y - _canvasDragGrabY - 2);
    }

    /// <summary>抬指时先让幽灵"吸"到落点上（贴住那条线 / 进入那个环），再淡出 —— 落点提示由它交接给静态指示，
    /// 视觉上东西是真的被放下了，而不是原地消失。</summary>
    private void SnapCanvasGhostToDrop()
    {
        if (CanvasDragGhost == null || !_canvasDropValid) return;
        Canvas.SetTop(CanvasDragGhost, _canvasDropInto
            ? _canvasDropRowRect.Y + (_canvasDropRowRect.Height - CanvasDragGhostHeight) / 2
            : (_canvasDropAfter ? _canvasDropRowRect.Bottom : _canvasDropRowRect.Y) - CanvasDragGhostHeight / 2);
    }

    private void PlayCanvasGhostIntro()
    {
        if (CanvasDragGhost == null) return;
        var sb = new Storyboard();

        var fade = new DoubleAnimation
        {
            // 淡到 0.85 而不是 1：幽灵是半透明的浮层，动画的终值必须与 XAML 里那条 Opacity 一致，
            // 否则淡入结束时它会被动画 held 成全不透明，把下方的落点线整段盖掉。
            From = 0, To = CanvasDragGhostOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(110)),
        };
        Storyboard.SetTarget(fade, CanvasDragGhost);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        // 缩放 0.90 → 1.05：从"按在行里的大小"弹到"抬起来的放大态"。
        AddCanvasGhostScaleAnimation(sb, 0.90, 1.05, 150);
        sb.Begin();
    }

    private void PlayCanvasGhostOutro()
    {
        if (CanvasDragGhost == null || CanvasDragLayer == null) return;

        var sb = new Storyboard();
        var fade = new DoubleAnimation
        {
            // From 取常量而不是读当前值：上一次淡出的动画会把 Opacity held 在 0，
            // 读回来是 0 的话这次淡出就等于什么都不做（幽灵当场消失）。
            From = CanvasDragGhostOpacity, To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(90)),
        };
        Storyboard.SetTarget(fade, CanvasDragGhost);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);

        AddCanvasGhostScaleAnimation(sb, 1.05, 0.98, 90);
        sb.Completed += (s, e) =>
        {
            if (_canvasDragActive) return;   // 期间又起了新的一次拖拽：这一层还得用，别收
            CanvasDragGhost.Visibility = Visibility.Collapsed;
            CanvasDragGhost.Opacity = CanvasDragGhostOpacity;
            CanvasDragGhostScale.ScaleX = 1.05;
            CanvasDragGhostScale.ScaleY = 1.05;
            CanvasDragLayer.Visibility = Visibility.Collapsed;
        };
        sb.Begin();
    }

    private void AddCanvasGhostScaleAnimation(Storyboard sb, double from, double to, int ms)
    {
        if (CanvasDragGhostScale == null) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(ms));

        var sx = new DoubleAnimation { From = from, To = to, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(sx, CanvasDragGhostScale);
        Storyboard.SetTargetProperty(sx, "ScaleX");
        sb.Children.Add(sx);

        var sy = new DoubleAnimation { From = from, To = to, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(sy, CanvasDragGhostScale);
        Storyboard.SetTargetProperty(sy, "ScaleY");
        sb.Children.Add(sy);
    }

    /// <summary>幽灵配色：比面板底色再亮一档 + 一道描边，深浅两套跟面板同步
    /// （面板底色是代码里按主题写死的，这里取同一套判断，不引用自定义主题键）。</summary>
    private void ApplyCanvasDragGhostTheme()
    {
        bool dark = this.ActualTheme == ElementTheme.Dark;
        CanvasDragGhost.Background = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(0xFF, 0x3E, 0x3E, 0x46)
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        CanvasDragGhost.BorderBrush = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(0xFF, 0x5A, 0x5A, 0x64)
            : Windows.UI.Color.FromArgb(0xFF, 0xD2, 0xD2, 0xDA));

        // 文字与图标用"激活档"亮度：此时全场只有它在被搬动，它就是主角。
        var fg = new SolidColorBrush(dark
            ? Windows.UI.Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFC)
            : Windows.UI.Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0E));
        CanvasDragGhostText.Foreground = fg;
        CanvasDragGhostIcon.Foreground = fg;
    }

    /// <summary>列表某一层的直接子项（树里的那一层）。库根 = _canvasRoots，其余 = 对应文件夹节点的 Children。
    /// 这一层就是"手动顺序"的落笔单位：拖一次就把整层名字按新顺序写下来。</summary>
    private IReadOnlyList<CanvasTreeNode> SiblingNodesOf(string dir)
    {
        if (string.Equals(dir, CanvasLibrary.Root, StringComparison.OrdinalIgnoreCase)) return _canvasRoots;
        if (FindCanvasFolder(_canvasRoots, dir) is { } folder) return folder.Children;
        return Array.Empty<CanvasTreeNode>();
    }

    /// <summary>贴边自动滚动：指针进上/下 26px 的带子就按固定步长滚，滚完重算落点
    /// （指针没动、行却动了，落点必须跟着变）。</summary>
    private void UpdateCanvasAutoScroll(Rect listRect, Point pagePt)
    {
        double delta = 0;
        if (pagePt.Y < listRect.Y + CanvasDragEdgeZone) delta = -CanvasDragScrollStep;
        else if (pagePt.Y > listRect.Bottom - CanvasDragEdgeZone) delta = CanvasDragScrollStep;

        _canvasAutoScrollDelta = delta;
        if (delta == 0) { StopCanvasAutoScroll(); return; }

        if (_canvasAutoScrollTimer != null) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += CanvasAutoScrollTick;
        _canvasAutoScrollTimer = timer;
        timer.Start();
    }

    private void CanvasAutoScrollTick(object? sender, object e)
    {
        if (!_canvasDragActive) { StopCanvasAutoScroll(); return; }
        if (FindDescendantScrollViewer(CanvasRowList) is not { } sv) return;

        double target = Math.Clamp(sv.VerticalOffset + _canvasAutoScrollDelta, 0, Math.Max(0, sv.ScrollableHeight));
        if (Math.Abs(target - sv.VerticalOffset) < 0.5) return;
        sv.ChangeView(null, target, null, disableAnimation: true);

        // ChangeView 的新布局要下一轮才落定：立刻重算会读到旧矩形，落点慢一格。
        Point pt = _canvasDragLastPoint;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_canvasDragActive) UpdateCanvasDrag(pt);
        });
    }

    private void StopCanvasAutoScroll()
    {
        _canvasAutoScrollDelta = 0;
        if (_canvasAutoScrollTimer == null) return;
        _canvasAutoScrollTimer.Stop();
        _canvasAutoScrollTimer.Tick -= CanvasAutoScrollTick;
        _canvasAutoScrollTimer = null;
    }

    /// <summary>收摊：清状态、放捕获、幽灵淡出，再（可选）落实落点。
    /// 顺序关键 —— 先把状态清干净再动磁盘/刷列表，否则刷新过程中触发的指针事件会走进半截的拖拽逻辑。</summary>
    private void FinishCanvasDrag(bool commit)
    {
        CanvasTreeNode? node = _canvasDragNode;
        bool valid = commit && _canvasDropValid && node != null;
        string? dir = _canvasDropDir;
        int at = _canvasDropAt;
        bool into = _canvasDropInto;

        _canvasDragActive = false;
        _canvasDragNode = null;
        _canvasPressNode = null;
        _canvasDropValid = false;
        _canvasDropDir = null;
        StopCanvasAutoScroll();
        StopCanvasHoldTimer();
        try { CanvasRowList?.ReleasePointerCaptures(); } catch (Exception) { }

        if (valid) SnapCanvasGhostToDrop();
        PlayCanvasGhostOutro();
        ClearCanvasDropCue();   // 线/环/投放带染色一起收

        if (valid) ApplyCanvasDrop(node!, dir!, at, into);
        UpdateCanvasRowBackgrounds();
    }

    /// <summary>落实一次拖拽。同层 = 只改顺序；跨层 = 先动磁盘（MoveNode），再把两层的顺序表都写成完整名字表。
    /// 「写完整表」是这套排序能成立的关键：只记被移动的那一项的话，其余项仍按默认规则排，
    /// 用户摆好的位置会被别的项挤走。表里多出来的名字（文件已删/改名）在排序时自然落空，不用清理。</summary>
    private void ApplyCanvasDrop(CanvasTreeNode node, string dir, int at, bool into)
    {
        string? srcDir = Path.GetDirectoryName(node.FullPath);
        bool sameDir = string.Equals(srcDir, dir, StringComparison.OrdinalIgnoreCase);
        string name = Path.GetFileName(node.FullPath);
        string landed = node.FullPath;   // 落地后的路径（跨层时会被 MoveNode 改写）

        if (sameDir)
        {
            var order = new List<CanvasTreeNode>(SiblingNodesOf(dir));
            int self = order.IndexOf(node);
            if (self < 0) return;
            order.RemoveAt(self);
            if (into) at = order.Count;                      // 放进自己的上层文件夹 = 排到最末
            at = Math.Clamp(at, 0, order.Count);
            order.Insert(at, node);
            _settingsService.SetCanvasOrder(dir, order.Select(n => Path.GetFileName(n.FullPath)).ToList());
        }
        else
        {
            string? moved = CanvasLibrary.MoveNode(node.FullPath, node.IsFolder, dir);
            if (moved == null)
            {
                ShowSaveToast(LocalizationService.Get("CanvasOpFailed"), ToastIconKind.Error);
                return;
            }
            // 当前画布 / 已展开文件夹的路径前缀跟着换（拖走的可能正是盖着的那一份）
            RemapCanvasPath(node.FullPath, moved);
            // 拖走文件夹：它内部各层的顺序表键（按目录全路径存的）整棵一起搬到新前缀，
            // 否则进了新位置的文件夹会把内部画布退回默认排序 —— "内部顺序跟着走"靠这一步。
            if (node.IsFolder) RekeyCanvasSubtreeOrder(node.FullPath, moved);

            // 源层：把它从手动顺序里摘掉。没排过的那层不用写 —— 它本来就该按默认规则排。
            if (srcDir != null && _settingsService.GetCanvasOrder(srcDir) is { } srcOrder)
            {
                _settingsService.SetCanvasOrder(srcDir,
                    srcOrder.Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)));
            }

            // 目标层：现有子项 + 这位新来的（插在落点），写完整名字表
            var names = SiblingNodesOf(dir).Select(n => Path.GetFileName(n.FullPath)).ToList();
            names.Insert(Math.Clamp(into ? names.Count : at, 0, names.Count), Path.GetFileName(moved));
            _settingsService.SetCanvasOrder(dir, names);

            landed = moved;
        }

        // 放进文件夹后顺手展开它：否则拖进去的东西当场看不见，像是拖丢了。
        if (into && FindCanvasFolder(_canvasRoots, dir) is { IsExpanded: false } folder)
        {
            folder.IsExpanded = true;
            _expandedCanvasFolders.Add(dir);
        }

        RefreshCanvasFolder(dir);
        if (!sameDir && srcDir != null) RefreshCanvasFolder(srcDir);

        // 落到新位置后把视口跟过去：拖到列表外（顶部/底部投放带）或拖进折叠区时，
        // 落点行原本不在可见范围，不跟过去用户只看到"东西没了"。
        // 纵向靠 ScrollIntoView，横向（深层缩进把名字顶出右边）靠 RevealCanvasRowName。
        ScrollCanvasRowIntoView(landed);
    }

    /// <summary>右键入口：点到某一行 → 节点菜单；点空白 → 库级菜单（新建画布/文件夹、刷新、在资源管理器中显示）。
    /// 菜单是自绘的（见 SelfDrawnContextMenu），每次按被点的节点现场构造条目。
    /// 坐标一律按【页面】坐标系取：菜单宿主的坐标换算交给控件自己做，不在这里拼。</summary>
    private void CanvasRowList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        CanvasTreeNode? node = FindRowNode(args.OriginalSource as DependencyObject);

        // 坐标优先取"右键按下时的指针位置"：那是 e.GetCurrentPoint(this) 直接给出的页面坐标，
        // 不经任何视觉变换，也就不会被某个元素的布局/尺寸判定带偏（菜单落点只由指针坐标决定）。
        Point pagePt;
        if (_hasCanvasRightPress) pagePt = _canvasRightPressPoint;
        else if (!args.TryGetPosition(this, out pagePt)) return;   // 无指针来源且取不到坐标：不弹，绝不拿 (0,0) 兜底

        CanvasContextMenu.ShowAt(this, pagePt, BuildCanvasMenuRows(node));
        args.Handled = true;
    }

    /// <summary>标题行右键 = 一个永远在的"库根空白位"。列表被画布排满时一个空白像素都不剩，
    /// 而"新建到根目录"必须始终可达 —— 标题行不参与滚动、不会消失，正好当这个落点。
    /// 坐标只取事件自带的这一份：列表的 PointerPressed 没经过这里，
    /// 那份"最近一次右键位置"可能是上一次点在列表里的旧值。</summary>
    private void CanvasHeader_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (CanvasContextMenu is { IsOpen: true }) return;
        if (!args.TryGetPosition(this, out Point pagePt)) return;   // 取不到坐标：不弹，绝不拿 (0,0) 兜底
        CanvasContextMenu.ShowAt(this, pagePt, BuildCanvasMenuRows(null));
        args.Handled = true;
    }

    /// <summary>记下这次按下是不是右键、以及指针在【页面】坐标系里的位置。
    /// 每次按下都刷新（不做"记住上一次右键"）：触摸长按 / 笔杆键这类"按下时才不是右键"的来源
    /// 必须在这一次就判掉，否则会拿上一次的旧坐标去摆菜单。
    /// 顺带处理"点空白处 = 放弃这次选择"：行的 PointerPressed 先跑（冒泡到此仍能往上找到那一行），
    /// 所以只有真点到空白/滚动条之外的地方才会清。清的时候连锚点一起清 —— 否则下一次 Ctrl 点选
    /// 会拿一个"早就作废"的锚点去补录，表现就是"点过空白之后 Ctrl 点 B 却连 A 一起选中"。</summary>
    private void CanvasRowList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint pt = e.GetCurrentPoint(this);
        _canvasRightPressPoint = pt.Position;
        _hasCanvasRightPress = pt.Properties.IsRightButtonPressed;

        if (!pt.Properties.IsLeftButtonPressed) return;
        if (FindRowNode(e.OriginalSource as DependencyObject) != null) return;
        if (IsInCanvasScrollBar(e.OriginalSource as DependencyObject)) return;
        ClearCanvasSelection();
    }

    /// <summary>事件源是否落在滚动条上（拖滚动条不该被当成"点了空白处"）。</summary>
    private static bool IsInCanvasScrollBar(DependencyObject? source)
    {
        for (var d = source; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ScrollBar) return true;
            if (d is ListViewItem) return false;
        }
        return false;
    }

    /// <summary>最近一次按下（在画布列表上）的指针位置与"是否为右键"。页面坐标系，
    /// 供 ContextRequested 摆菜单用 —— 那是唯一不经视觉变换的坐标来源。</summary>
    private Point _canvasRightPressPoint;
    private bool _hasCanvasRightPress;

    /// <summary>从事件源往上找所在的行；不在任何行上（=点的是空白处）返回 null。</summary>
    private static CanvasTreeNode? FindRowNode(DependencyObject? source)
    {
        for (var d = source; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ListViewItem { Content: CanvasTreeNode node }) return node;
        }
        return null;
    }

    /// <summary>菜单按被点的节点现场构造 —— 文件夹能"在里面新建"，文件只能改自己，写死一份会变成一堆禁用项。
    /// 被点的行属于当前多选时切成"批量菜单"：改名/打开路径这些只对单行有意义的条目全部收起来，
    /// 只留对整个选择集合成立的动作（目前是删除）。右键本身不动选择集合：点在某行上不代表要改选。 </summary>
    private List<SelfDrawnMenuRow> BuildCanvasMenuRows(CanvasTreeNode? node)
    {
        var rows = new List<SelfDrawnMenuRow>();

        if (node == null)
        {
            rows.Add(MenuRow("CanvasNewCanvas", "\uE710", () => CreateCanvasIn(null)));
            rows.Add(MenuRow("CanvasNewFolder", "\uE8F4", () => CreateFolderIn(null)));
            rows.Add(SelfDrawnMenuRow.Separator());
            rows.Add(MenuRow("CanvasRefresh", "\uE72C", RefreshCanvasTree));
            rows.Add(MenuRow("CanvasReveal", "\uE8B7", () => ShellFileOps.RevealInExplorer(CanvasLibrary.Root, true)));
            return rows;
        }

        if (IsCanvasMultiSelection(node))
        {
            rows.Add(MenuRowText(
                string.Format(LocalizationService.Get("CanvasDeleteItems"), _selectedCanvasNodes.Count),
                "\uE74D", DeleteSelectedCanvases));
            return rows;
        }

        if (node.IsFolder)
        {
            rows.Add(MenuRow("CanvasNewCanvas", "\uE710", () => CreateCanvasIn(node.FullPath)));
            rows.Add(MenuRow("CanvasNewFolder", "\uE8F4", () => CreateFolderIn(node.FullPath)));
            rows.Add(SelfDrawnMenuRow.Separator());
        }

        rows.Add(MenuRow("CanvasRename", "\uE8AC", () => StartCanvasRename(node)));
        // 卸下只对"此刻盖在文档上的那一份"有意义：别的文件没有可卸的东西，条目会是个空动作。
        if (!node.IsFolder && string.Equals(node.FullPath, _currentCanvasPath, StringComparison.OrdinalIgnoreCase))
            rows.Add(MenuRow("CanvasDetach", "\uE77A", DetachCanvas));
        rows.Add(MenuRow("CanvasDelete", "\uE74D", () => DeleteCanvasNode(node)));
        rows.Add(SelfDrawnMenuRow.Separator());
        rows.Add(MenuRow("CanvasReveal", "\uE8B7", () => ShellFileOps.RevealInExplorer(node.FullPath, node.IsFolder)));

        if (!node.IsFolder)
            rows.Add(MenuRow("CanvasCopyPath", "\uE8C8", () => CopyCanvasPath(node)));

        return rows;
    }

    /// <summary>被点的行是否属于"两个以上"的多选。只勾了一行时仍走单行菜单 ——
    /// 单项操作（改名、复制路径）在只有一行时更顺手。</summary>
    private bool IsCanvasMultiSelection(CanvasTreeNode node)
        => _selectedCanvasNodes.Count >= 2 && _selectedCanvasNodes.Contains(node);

    private static SelfDrawnMenuRow MenuRow(string textKey, string glyph, Action action)
        => new() { Text = LocalizationService.Get(textKey), Glyph = glyph, Invoke = action };

    private static SelfDrawnMenuRow MenuRowText(string text, string glyph, Action action)
        => new() { Text = text, Glyph = glyph, Invoke = action };

    /// <summary>新建画布的默认名：当前 PDF 的文件名（不含扩展名）—— 画布是给"这份文档"画的，
    /// 名字跟着文档走才认得出谁是谁；没开文档时退回本地化的"未命名"。
    /// 重名不在这里处理：同一层再新建一次由 CanvasLibrary 自动加序号。</summary>
    private string DefaultCanvasName()
    {
        string name = "";
        try { name = System.IO.Path.GetFileNameWithoutExtension(_currentPdfPath); } catch { }
        name = (name ?? "").Trim();
        // 长度让位给" (2)"序号与扩展名：文档名可能很长，但画布文件名仍要留余量。
        if (name.Length > 80) name = name[..80].Trim();
        return name.Length > 0 ? name : LocalizationService.Get("CanvasUntitledCanvas");
    }

    /// <summary>在指定目录（null = 库根）里新建一份空画布：**只让它在对应层级出现，不激活**。
    /// 建完就盖上会把正在用的那份挤掉 —— 用户往往是先建好再决定何时切过去，
    /// 新建这个动作本身不该有"切换画布"的副作用。只刷新这一层，不重建整棵树。</summary>
    private void CreateCanvasIn(string? parentDir)
    {
        string? created = CanvasLibrary.CreateEmpty(DefaultCanvasName(), parentDir);
        if (created == null)
        {
            ShowSaveToast(LocalizationService.Get("CanvasOpFailed"), ToastIconKind.Error);
            return;
        }
        ExpandCanvasFolder(parentDir);
        AppendCanvasOrder(created);   // 先记顺序（读的是当前显示顺序），再刷新列表
        RefreshCanvasFolder(parentDir ?? CanvasLibrary.Root);
        ScrollCanvasRowIntoView(created);
    }

    /// <summary>把刚建出来的项追加到该层顺序表的末尾 —— "新建永远在最下方"靠的就是这一步。
    /// 默认排序（哪怕是自然排序）都可能把它插到中间去，只有写进顺序表才算数。
    /// 该层还没有顺序表时，先按【当前显示顺序】物化一份再接上新项：物化结果与用户眼前所见逐项一致，
    /// 于是"新建在最末"这件事在任何历史状态下都成立。
    /// 拿不到同层兄弟（目录还没进树）就不写 —— 只记一个名字会让它成为"唯一排过的项"而排到最前面。</summary>
    private void AppendCanvasOrder(string path)
    {
        if (Path.GetDirectoryName(path) is not { } dir) return;
        var names = SiblingNodesOf(dir).Select(n => Path.GetFileName(n.FullPath)).ToList();
        if (names.Count == 0) return;

        string name = Path.GetFileName(path);
        if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        _settingsService.SetCanvasOrder(dir, names);
    }

    /// <summary>把顺序表与"眼前这一层"对齐：缺的按显示顺序补到末尾、已经不在的剔除。
    /// 内容没变就不写盘（相等即跳过），所以可以在每次刷新后无条件调用。
    /// 这是"顺序只由拖拽决定"的兜底：除拖拽外没有任何路径能调整相对次序 ——
    /// 新建/外部新增只会被追加到末尾，删除/改名只是增删某一条，其余各项的名次原样不动。</summary>
    private void NormalizeCanvasOrder(string dirPath, IReadOnlyList<CanvasTreeNode> children)
    {
        if (children.Count == 0) return;

        var names = new List<string>(children.Count);
        foreach (var c in children) names.Add(Path.GetFileName(c.FullPath));

        if (_settingsService.GetCanvasOrder(dirPath) is { } existing && existing.Count == names.Count)
        {
            bool same = true;
            for (int i = 0; i < names.Count; i++)
            {
                if (!string.Equals(existing[i], names[i], StringComparison.OrdinalIgnoreCase)) { same = false; break; }
            }
            if (same) return;
        }
        _settingsService.SetCanvasOrder(dirPath, names);
    }

    /// <summary>整棵树逐层对齐。折叠的文件夹也在树里（BuildTree 一律递归建），所以深层也会被覆盖。</summary>
    private void NormalizeCanvasOrderTree(IReadOnlyList<CanvasTreeNode> nodes)
    {
        NormalizeCanvasOrder(CanvasLibrary.Root, nodes);
        foreach (var n in nodes)
        {
            if (!n.IsFolder) continue;
            NormalizeCanvasOrder(n.FullPath, n.Children);
            NormalizeCanvasOrderTree(n.Children);
        }
    }

    /// <summary>在指定目录（null = 库根）里新建一个文件夹并展开这一层。
    /// 不进入行内改名：与"新建画布"保持一致，点完就完事，改名一律走右键菜单 ——
    /// 两种新建共用一条心智模型，不因为一个是文件一个是文件夹就分叉。</summary>
    private void CreateFolderIn(string? parentDir)
    {
        string? created = CanvasLibrary.CreateFolder(parentDir, LocalizationService.Get("CanvasUntitledFolder"));
        if (created == null)
        {
            ShowSaveToast(LocalizationService.Get("CanvasOpFailed"), ToastIconKind.Error);
            return;
        }
        ExpandCanvasFolder(parentDir);
        AppendCanvasOrder(created);   // 先记顺序（读的是当前显示顺序），再刷新列表
        RefreshCanvasFolder(parentDir ?? CanvasLibrary.Root);
        ScrollCanvasRowIntoView(created);
    }

    /// <summary>把刚建出来的那一行带进可见范围，**横向也要带**：层级深时缩进把名字推到视口右侧之外，
    /// 只调 ScrollIntoView 只能纵向对准，名字依旧看不见。
    /// 纵向：ScrollIntoView（不需要行容器，立刻生效）。
    /// 横向：必须拿到行容器与名字文本才能算出"右端超出视口多少"，而容器是虚拟化的、
    /// 新建后要等下一轮布局才生成 —— 所以交给 RevealCanvasRowName 按帧重试。
    /// 只滚视口：不动选择、更不动激活。</summary>
    private void ScrollCanvasRowIntoView(string fullPath)
    {
        if (CanvasRowList == null) return;

        if (FindCanvasRowByPath(fullPath) is not { } target) return;   // 父层被收起等情况：这一行根本不在可见行集合里

        CanvasRowList.ScrollIntoView(target);
        RevealCanvasRowName(target, 15);
    }

    /// <summary>等这一行的容器真的实例化出来，再把名字横向滚进视口。
    /// 两道等待必须分开：容器要跨调度帧（ContainerFromItem 对刚插入的行常返回 null），
    /// 布局要跨真实帧（容器有了，但行的宽度与 ScrollViewer 的横向 extent 还是旧值）——
    /// 同一个布局轮次里排多少次队列都还是同一份旧布局，所以第二道用定时器。</summary>
    private void RevealCanvasRowName(CanvasTreeNode row, int left)
    {
        if (CanvasRowList == null) return;
        // 正在行内改名：名字那一格是收起的（换成输入框），没有"要露出的名字"可对；
        // 更关键的是这条链路会连着强制重排（UpdateLayout + 等帧重试），
        // 而改名框正靠焦点活着 —— 持续重排会把刚拿到的焦点打掉。改名优先，让路。
        if (row.IsRenaming) return;
        CanvasRowList.UpdateLayout();

        if (CanvasRowList.ContainerFromItem(row) is not ListViewItem container)
        {
            if (left <= 0) return;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => RevealCanvasRowName(row, left - 1));
            return;
        }
        RetryOnTimer(() => TryRevealName(container, row), 20);
    }

    /// <summary>横向滚到"名字整体可见"，返回 true 表示已经盖棺（滚到位或本来就不必滚）。
    /// 自己算偏移而不靠 StartBringIntoView：后者是"最近边对齐"语义 —— 目标只露一条边就算完成，
    /// 实测只挪几像素。也不能把目标值 clamp 到当前 ScrollableWidth：新行刚插进来时 extent 还是旧值，
    /// clamp 会把"该滚 300px"截成"滚 5px"，这正是"只滚一点点"的成因。
    /// 位置一律在**内容坐标系**里算：TransformToVisual 给出的是视口坐标（已扣掉当前偏移），
    /// 加回 HorizontalOffset 才是与 ScrollableWidth 同系的量。</summary>
    private bool TryRevealName(ListViewItem container, CanvasTreeNode row)
    {
        if (CanvasRowList == null) return true;
        if (row.IsRenaming) return true;   // 改名中：这条链路的理由消失了，收手（别再强制重排）
        if (FindDescendantScrollViewer(CanvasRowList) is not { } sv) return true;

        CanvasRowList.UpdateLayout();
        if (sv.ViewportWidth <= 0) return false;   // 还没落版，量不出宽度

        // 按文本内容认领名字那一格：行模板里还有 FontIcon（内部也可能挂 TextBlock）与行内改名框，
        // 只按类型取第一个会取错对象，算出来的右端位置毫无意义。
        if (FindFirstDescendant<TextBlock>(container,
                t => t.Visibility == Visibility.Visible && t.Text == row.Name) is not { } nameText) return false;
        if (nameText.ActualWidth <= 0) return false;

        const double pad = 8;   // 名字与视口右缘留一点缝，贴边看着像被切
        double left = nameText.TransformToVisual(sv).TransformPoint(new Point(0, 0)).X + sv.HorizontalOffset;
        double width = nameText.ActualWidth;
        double viewportRight = sv.HorizontalOffset + sv.ViewportWidth;

        double target;
        if (width + pad >= sv.ViewportWidth) target = left - pad;                    // 比视口还宽：对齐开头
        else if (left - pad < sv.HorizontalOffset) target = left - pad;              // 左端被切（含滚过头）
        else if (left + width + pad > viewportRight) target = left + width + pad - sv.ViewportWidth;
        else return true;                                                           // 已完整可见
        if (target < 0) target = 0;

        // 目标超出现有 extent 时照样下发（ScrollViewer 自己会夹到最大值），先把能露的露出来；
        // 返回 false 让下一帧带着长出来的 extent 再来一次，直到真的到位。
        bool reached = target <= sv.ScrollableWidth + 0.5;
        if (Math.Abs(target - sv.HorizontalOffset) > 0.5)
            sv.ChangeView(target, null, null, disableAnimation: true);
        return reached;
    }

    /// <summary>可视树里第一个满足条件的指定类型元素（深度优先，含自己）。</summary>
    private static T? FindFirstDescendant<T>(DependencyObject? root, Func<T, bool> match) where T : DependencyObject
    {
        if (root == null) return null;
        if (root is T hit && match(hit)) return hit;
        int n;
        try { n = VisualTreeHelper.GetChildrenCount(root); } catch { return null; }
        for (int i = 0; i < n; i++)
        {
            if (FindFirstDescendant(VisualTreeHelper.GetChild(root, i), match) is { } deep) return deep;
        }
        return null;
    }

    /// <summary>把容器目录展开，让新建出来的行当场可见。展开集合只管"重建时"的初始态，
    /// 已经存在的节点要直接改 IsExpanded 才动得了 —— 增量同步会原样保留旧节点。</summary>
    private void ExpandCanvasFolder(string? dirPath)
    {
        if (string.IsNullOrEmpty(dirPath)) return;
        _expandedCanvasFolders.Add(dirPath!);
        if (FindCanvasFolder(_canvasRoots, dirPath!) is { } node) node.IsExpanded = true;
    }

    /// <summary>右键菜单里的「重命名」。菜单项是在关闭菜单的同一个按下里执行的，
    /// 此刻这一次点击的"抬起"还没走完：当场开编辑的话，紧随其后的抬起/菜单清理会把刚拿到的焦点带走，
    /// 表现就是"改名框一闪就退出、根本改不了"。所以延后一个调度回合再开编辑，让那次点击彻底走完。
    /// 开的时候按【路径】找回当前节点实例：菜单是按右键那一刻构造的，期间行集合可能已经重建过一次。</summary>
    private void StartCanvasRename(CanvasTreeNode node)
    {
        string path = node.FullPath;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => BeginCanvasRename(path));
    }

    /// <summary>真正进入行内改名：同一时刻只允许一行在改，顺手把别的行收掉。
    /// 改名不动高亮 —— 高亮只表达"盖在 PDF 上的那份画布"，和编辑哪个名字无关。</summary>
    private void BeginCanvasRename(string fullPath)
    {
        if (FindCanvasRowByPath(fullPath) is not { } node) return;   // 期间被删/改名/父层收起

        foreach (var other in EnumerateCanvasNodes(_canvasRoots))
        {
            if (!ReferenceEquals(other, node) && other.IsRenaming) other.IsRenaming = false;
        }
        node.RenameText = node.Name;
        node.IsRenaming = true;
    }

    /// <summary>按路径在当前可见行集合里找节点：改名、滚动这些都只对"真的在列表里"的行有意义。</summary>
    private CanvasTreeNode? FindCanvasRowByPath(string fullPath)
    {
        foreach (var row in _canvasRows)
        {
            if (string.Equals(row.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) return row;
        }
        return null;
    }

    private static IEnumerable<CanvasTreeNode> EnumerateCanvasNodes(IEnumerable<CanvasTreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            if (!n.IsFolder) continue;
            foreach (var child in EnumerateCanvasNodes(n.Children)) yield return child;
        }
    }

    private void CanvasRowRename_Submitted(object sender, RoutedEventArgs e) => CommitRowRename(sender);
    private void CanvasRowRename_CommitRequested(object sender, RoutedEventArgs e) => CommitRowRename(sender);

    private void CanvasRowRename_Cancelled(object sender, RoutedEventArgs e)
    {
        if (RowNodeOf(sender) is { } node) node.IsRenaming = false;
    }

    /// <summary>点到行内改名框以外的地方 = 提交。为什么不能只靠失焦：列表行不是可聚焦元素
    /// （行是 Grid、列表 IsTabStop=False），点另一行根本不会把焦点从宿主身上移走 ⇒ 没有 LostFocus
    /// ⇒ 改名框一直亮着，看上去就是"卡在编辑态退不出去"。这里按指针落点兜底，且不看焦点在谁身上。
    /// 菜单里的条目也算"框外"：先提交再执行菜单动作，语义一致（改个名不能被右键菜单挂住）。
    /// <paramref name="handledEventsToo: true"/> 是必需的 —— 行与控件自己都把 PointerPressed 标记了 Handled。</summary>
    private void OnPagePointerPressedCommitRename(object sender, PointerRoutedEventArgs e)
    {
        if (FindRenamingNode() is not { } node) return;
        if (IsPressInsideRenameBox(e.OriginalSource as DependencyObject, node)) return;
        CommitCanvasRename(node);
    }

    /// <summary>这次按下是否落在【正在改名那一行】的输入框里（点框内是定位光标/拖选，不能提交）。</summary>
    private static bool IsPressInsideRenameBox(DependencyObject? source, CanvasTreeNode node)
    {
        for (var d = source; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is SelfDrawnTextBox box) return ReferenceEquals(box.DataContext, node);
        }
        return false;
    }

    /// <summary>当前正在行内改名的行（同一时刻至多一行）。</summary>
    private CanvasTreeNode? FindRenamingNode()
    {
        foreach (var row in _canvasRows)
        {
            if (row.IsRenaming) return row;
        }
        return null;
    }

    private void CommitRowRename(object sender)
    {
        if (RowNodeOf(sender) is { } node) CommitCanvasRename(node);
    }

    /// <summary>提交行内改名。名字没变 / 只是空白 ⇒ 当取消处理；改名成功后把"当前画布"指针一起搬到新路径，
    /// 否则画布明明还在、程序却认为上次那份不见了。</summary>
    private void CommitCanvasRename(CanvasTreeNode node)
    {
        if (!node.IsRenaming) return;
        node.IsRenaming = false;

        string text = node.RenameText.Trim();
        if (text.Length == 0 || string.Equals(text, node.Name, StringComparison.Ordinal)) return;

        string? renamed = CanvasLibrary.Rename(node.FullPath, node.IsFolder, text);
        if (renamed == null)
        {
            ShowSaveToast(LocalizationService.Get("CanvasNameTaken"), ToastIconKind.Error);
            return;
        }
        if (string.Equals(renamed, node.FullPath, StringComparison.OrdinalIgnoreCase)) return;

        string oldPath = node.FullPath;
        RekeyCanvasOrder(oldPath, renamed);   // 必须在 Refresh 之前：此刻树里还是旧名，才落得回原来那一格
        RemapCanvasPath(oldPath, renamed);
        RefreshCanvasFolder(Path.GetDirectoryName(oldPath) ?? CanvasLibrary.Root);
    }

    /// <summary>改名后把顺序表里的"键"一起换掉，让这一项**名次不变**。
    /// 顺序表按两样东西作键：父目录里"含扩展名的文件名"、以及文件夹自身子表的"目录全路径"。
    /// 键一变就对不上 ⇒ 这一项失去名次，被默认排序（当前区域 = 中文拼音 + 数字序号）重新插到别处，
    /// 看上去就是"改个名自己跳位"。所以两级都要换：
    /// ① 父目录表里的这一项：按【当前显示顺序】物化一份，再把旧名原位替换成新名；
    /// ② 文件夹自身那张子表：整体搬到新路径上（空表 = 删掉旧键）。
    /// 幂等：旧名已不在表里（例如外部改名已经处理过一次）就什么都不做。</summary>
    private void RekeyCanvasOrder(string oldPath, string newPath)
    {
        if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return;
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;

        string parentDir = Path.GetDirectoryName(oldPath) ?? CanvasLibrary.Root;
        string oldName = Path.GetFileName(oldPath);
        string newName = Path.GetFileName(newPath);

        var names = SiblingNodesOf(parentDir).Select(n => Path.GetFileName(n.FullPath)).ToList();
        int at = names.FindIndex(n => string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            names[at] = newName;
            _settingsService.SetCanvasOrder(parentDir, names);
        }

        // 文件夹才有"子表"；文件改完名 Directory.Exists 为 false，直接跳过
        if (!Directory.Exists(newPath)) return;
        RekeyCanvasSubtreeOrder(oldPath, newPath);
    }

    /// <summary>把 oldPath 这一层**以及它所有子孙目录**的顺序表键整体搬到 newPath 前缀下。
    /// 子表是按"目录全路径"作键的：文件夹被改名或拖到别处后路径变了，键就全失效 ⇒
    /// 里面的画布顺序会退回默认排序。整棵子树一起搬，"拖走文件夹，内部顺序也跟着走"才成立。
    /// 该层从没排过时，按眼前显示顺序物化一份到新路径上（等于"动完之后和动之前一模一样"）。</summary>
    private void RekeyCanvasSubtreeOrder(string oldPath, string newPath)
    {
        var rekeys = _settingsService.CanvasOrder.Keys
            .Where(k => IsSameOrUnder(k, oldPath))
            .Select(k => (Old: k, New: newPath + k[oldPath.Length..]))
            .ToList();

        if (rekeys.Count == 0)
        {
            var direct = SiblingNodesOf(oldPath).Select(n => Path.GetFileName(n.FullPath)).ToList();
            if (direct.Count > 0) _settingsService.SetCanvasOrder(newPath, direct);
            return;
        }

        foreach ((string oldKey, string newKey) in rekeys)
        {
            var list = _settingsService.GetCanvasOrder(oldKey)?.ToList();
            if (list == null || list.Count == 0) continue;
            _settingsService.SetCanvasOrder(oldKey, Array.Empty<string>());   // 空表 ⇒ 删掉旧键
            _settingsService.SetCanvasOrder(newKey, list);
        }
    }

    private static CanvasTreeNode? RowNodeOf(object sender)
        => sender is FrameworkElement { DataContext: CanvasTreeNode node } ? node : null;

    /// <summary>删除：直接进回收站，不弹确认 —— 回收站本身可撤销，多一个确认框只是多一步。
    /// 删的正是当前盖着的画布（或它所在文件夹）时，先把画布卸掉再删：
    /// 否则删完指针还指着一个不存在的文件，下一次落盘会凭空再造一份出来。</summary>
    private void DeleteCanvasNode(CanvasTreeNode node)
    {
        bool holdsCurrent = !string.IsNullOrEmpty(_currentCanvasPath) && IsSameOrUnder(_currentCanvasPath!, node.FullPath);
        if (holdsCurrent)
        {
            _canvasDirty = false;   // 文件马上要进回收站，把内存里的改动再写一遍没有意义
            ApplyCanvas(null);
        }

        string? parentDir = Path.GetDirectoryName(node.FullPath);
        if (!CanvasLibrary.Delete(node.FullPath))
        {
            ShowSaveToast(LocalizationService.Get("CanvasOpFailed"), ToastIconKind.Error);
            return;
        }
        _expandedCanvasFolders.RemoveWhere(p => IsSameOrUnder(p, node.FullPath));
        RefreshCanvasFolder(parentDir ?? CanvasLibrary.Root);
    }

    /// <summary>批量删除当前多选。与单行删除同一套规则：进回收站、不弹确认。
    /// 只删"最外层"——选中的文件夹已经把它下面的勾选项包含在内，逐个删的话第二次必然找不到路径。
    /// 命中"正盖着的那份画布（或它的上层文件夹）"时先卸画布：否则删完指针还指着不存在的文件，
    /// 下一次落盘会凭空再造一份出来。</summary>
    private void DeleteSelectedCanvases()
    {
        var targets = _selectedCanvasNodes
            .Where(n => !_selectedCanvasNodes.Any(o => !ReferenceEquals(o, n) && IsSameOrUnder(n.FullPath, o.FullPath)))
            .ToList();
        if (targets.Count == 0) return;

        if (!string.IsNullOrEmpty(_currentCanvasPath) &&
            targets.Any(t => IsSameOrUnder(_currentCanvasPath!, t.FullPath)))
        {
            _canvasDirty = false;
            ApplyCanvas(null);
        }

        // 每一层只需刷新一次：同一目录下删掉多项时，逐项重扫既慢又会让列表抖好几下。
        var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in targets)
        {
            _expandedCanvasFolders.RemoveWhere(p => IsSameOrUnder(p, node.FullPath));
            if (Path.GetDirectoryName(node.FullPath) is { } parentDir) parents.Add(parentDir);
        }

        // 先清选择再刷新：别让被删掉的节点还留在选择集合里（SyncCanvasRows 虽然会剪枝，
        // 但那是"不在行集合里"的兜底，这里主动收干净更直白）。
        ClearCanvasSelection();

        // 【乐观隐藏】把这几行标成"待删"再拍平一次：行当场消失，用户看到的响应是即时的。
        // 真正的删除交给后台 STA 线程 —— 一次 shell 往返（回收站索引 + 杀软扫描）在 UI 线程上
        // 无论怎么合并批量，二十项仍会有几百毫秒的可感停顿；挪出去之后这段时间用户已经在做别的事了。
        foreach (var node in targets) node.IsPendingDelete = true;
        SyncCanvasRows();

        var paths = targets.Select(t => t.FullPath).ToList();
        string[] dirs = parents.ToArray();
        var thread = new System.Threading.Thread(() =>
        {
            bool ok = Interop.ShellFileOps.MoveToRecycleBin(paths);
            DispatcherQueue.TryEnqueue(() => FinishCanvasDelete(targets, dirs, ok));
        })
        {
            IsBackground = true,
            Name = "CanvasDelete",
        };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);   // shell 删除要在 STA 上调用
        thread.Start();
    }

    /// <summary>后台删除回来的收尾（UI 线程）：按磁盘真值把受影响的目录重扫一遍。
    /// 成功 ⇒ 文件已不在，SyncChildren 会把节点从树上摘掉；部分/全部失败 ⇒ 清除待删标记，行回到原处。
    /// 标记必须【先无条件清掉】再重扫：批量里可能只成功了一部分，没删掉的那些不能永远隐身。</summary>
    private void FinishCanvasDelete(List<CanvasTreeNode> targets, string[] dirs, bool ok)
    {
        foreach (var node in targets) node.IsPendingDelete = false;

        bool rebuilt = false;
        foreach (string dir in dirs)
        {
            if (PatchCanvasFolder(dir)) continue;
            RefreshCanvasTree();   // 上级被整体删掉了：整树重建（它自己会拍平）
            rebuilt = true;
            break;
        }
        if (!rebuilt) SyncCanvasRows();

        if (!ok) ShowSaveToast(LocalizationService.Get("CanvasOpFailed"), ToastIconKind.Error);
    }

    private void CopyCanvasPath(CanvasTreeNode node)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(node.FullPath);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception) { }
    }

    /// <summary>改名/移动后，把保存在内存里的旧路径（当前画布 + 已展开文件夹）整体换到新路径前缀。</summary>
    private void RemapCanvasPath(string oldPath, string newPath)
    {
        if (!string.IsNullOrEmpty(_currentCanvasPath) && IsSameOrUnder(_currentCanvasPath!, oldPath))
            _currentCanvasPath = newPath + _currentCanvasPath![oldPath.Length..];

        var moved = _expandedCanvasFolders.Where(p => IsSameOrUnder(p, oldPath)).ToList();
        foreach (string p in moved)
        {
            _expandedCanvasFolders.Remove(p);
            _expandedCanvasFolders.Add(newPath + p[oldPath.Length..]);
        }
        UpdateCanvasRowBackgrounds();
    }

    /// <summary>path 是否就是 prefix，或位于 prefix 之下。比较要带上分隔符，
    /// 否则 "…\Canvases2\a.gcanvas" 会被当成 "…\Canvases" 的子项。</summary>
    private static bool IsSameOrUnder(string path, string prefix)
    {
        if (string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith(prefix.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                               StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>画布库目录监听：在文件管理器里新建/改名/删除后，面板不必等用户开关一次才更新。
    /// 事件在后台线程到达，一律转投 UI 线程；一次保存会连发 Changed/Created/Renamed 多条，
    /// 故串一个 400ms 的单次定时器做防抖 —— 逐条刷新会把列表抖成残影。
    /// 目录读不到 / 网络盘不支持时静默放弃：功能退化成"展开面板时才刷新"，不影响使用。</summary>
    private void SetupCanvasWatcher()
    {
        if (_canvasWatcher != null) return;
        try
        {
            _canvasRefreshTimer = DispatcherQueue.CreateTimer();
            _canvasRefreshTimer.Interval = TimeSpan.FromMilliseconds(400);
            _canvasRefreshTimer.IsRepeating = false;
            _canvasRefreshTimer.Tick += (s, e) => FlushCanvasRefreshQueue();

            // Filter 用 "*" 而不是 "*.gcanvas"：Filter 只按名字匹配、不看类型，
            // 用扩展名过滤会把"新建/改名/删除文件夹"整个漏掉。真正关心的路径在 QueueCanvasRefresh 里判。
            var watcher = new FileSystemWatcher(CanvasLibrary.Root, "*")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            FileSystemEventHandler onFile = (s, e) => QueueCanvasRefresh(e.FullPath);
            RenamedEventHandler onRename = (s, e) =>
            {
                QueueCanvasRekey(e.OldFullPath, e.FullPath);   // 外部改名同样要"留在原地"
                QueueCanvasRefresh(e.OldFullPath);   // 旧的一行要消失
                QueueCanvasRefresh(e.FullPath);      // 新的一行要出现
            };
            watcher.Created += onFile;
            watcher.Changed += onFile;
            watcher.Deleted += onFile;
            watcher.Renamed += onRename;
            watcher.EnableRaisingEvents = true;
            _canvasWatcher = watcher;
        }
        catch (Exception)
        {
            _canvasWatcher = null;
        }
    }

    /// <summary>待刷新的目录。同一批事件里若多次落到同一层，合并成一次。</summary>
    private readonly HashSet<string> _pendingCanvasDirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>待换键的改名对（外部改名用）。与刷新同处一个防抖窗口，在真正重扫之前落地 ——
    /// 否则重扫会先按"旧名已失效"把这一项排到末尾，再换键也来不及了。</summary>
    private readonly List<(string OldPath, string NewPath)> _pendingCanvasRekeys = new();

    /// <summary>后台线程：把改名对排到 UI 线程（顺序表与树都只能在 UI 线程动）。</summary>
    private void QueueCanvasRekey(string? oldPath, string? newPath)
    {
        if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return;
        string o = oldPath!, n = newPath!;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (CanvasPanelSplitView?.IsPaneOpen != true) return;   // 面板收起时列表本就要重建，不必排
            _pendingCanvasRekeys.Add((o, n));
        });
    }

    /// <summary>后台线程入口：先判"这条路径关不关心"，再切到 UI 线程排队。
    /// 关心的 = 画布文件（.gcanvas），或没有扩展名的条目（文件夹）。临时文件等一律忽略。</summary>
    private void QueueCanvasRefresh(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;

        bool relevant;
        string? dir;
        try
        {
            relevant = path!.EndsWith(CanvasStore.Extension, StringComparison.OrdinalIgnoreCase)
                       || !Path.HasExtension(path);
            dir = Path.GetDirectoryName(path);
        }
        catch (Exception) { return; }
        if (!relevant || string.IsNullOrEmpty(dir)) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            // 面板收起时不刷新：展开动作本身就会重新枚举（见 ToggleCanvasPanel_Click）。
            if (CanvasPanelSplitView?.IsPaneOpen != true) return;
            _pendingCanvasDirs.Add(dir!);
            _canvasRefreshTimer?.Stop();
            _canvasRefreshTimer?.Start();   // 重复 Start = 重新计时 ⇒ 连续事件只在最后一次之后刷一次
        });
    }

    /// <summary>定时器到点：只同步受影响的那一层目录（在树里就地打补丁），不动其它行、不丢展开态。</summary>
    private void FlushCanvasRefreshQueue()
    {
        // 换键必须先于重扫：重扫时若旧名还挂在表里，这一项已经被排到末尾了
        if (_pendingCanvasRekeys.Count > 0)
        {
            foreach ((string oldPath, string newPath) in _pendingCanvasRekeys) RekeyCanvasOrder(oldPath, newPath);
            _pendingCanvasRekeys.Clear();
        }

        if (_pendingCanvasDirs.Count == 0) { RefreshCanvasTree(); return; }

        var dirs = _pendingCanvasDirs.ToArray();
        _pendingCanvasDirs.Clear();
        foreach (string dir in dirs)
        {
            if (!CanvasLibrary.IsInsideRoot(dir)) { RefreshCanvasTree(); return; }
            RefreshCanvasFolder(dir);
        }
    }

    private void DisposeCanvasWatcher()
    {
        try { _canvasWatcher?.Dispose(); } catch { }
        _canvasWatcher = null;
        try { _canvasRefreshTimer?.Stop(); } catch { }
        _canvasRefreshTimer = null;
        _pendingCanvasDirs.Clear();
        _pendingCanvasRekeys.Clear();
    }

    /// <summary>白板操作入撤销栈。两个方向都改模型 ⇒ 一律置脏，否则撤销后画面回退、磁盘仍停在新状态。</summary>
    private void PushCanvasHistory(Action undo, Action redo)
    {
        _annotationRedo.Clear();
        _annotationHistory.Add(new EditorHistoryEntry(
            undo: () => { undo(); MarkCanvasDirty(); },
            redo: () => { redo(); MarkCanvasDirty(); }));
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
        MarkCanvasDirty();
        RefreshCanvas();
    }

    /// <summary>选中卡片 = 提升到叠放顶层。jsoncanvas 无独立 z 字段，nodes 数组顺序即 z 序， 故"置顶"就是把该节点移到数组末尾。写回模型并置脏（落盘在 FlushCanvas）⇒ 取消选中后层级保持，不会掉回出生顺序。 不入撤销栈：选中是高频操作，入栈会淹没真正可撤销的编辑。</summary>
    private void GpuCanvas_BringToFront(object? sender, CanvasNodeEventArgs e)
    {
        if (_canvasDoc == null) return;
        var nodes = _canvasDoc.Nodes;
        int i = nodes.FindIndex(n => string.Equals(n.Id, e.NodeId, StringComparison.Ordinal));
        if (i < 0 || i == nodes.Count - 1) return;
        var node = nodes[i];
        nodes.RemoveAt(i);
        nodes.Add(node);
        MarkCanvasDirty();
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
        MarkCanvasDirty();
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
        MarkCanvasDirty();
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
        // 侧栏白板列表的行标题是提交时算好的快照，模型改了不会自己跟着变；
        // 定点推一次通知即可（不必 SetCanvasData，渲染侧拿的是同一批节点引用）。
        UpdateBoardCardText(e.NodeId, newText);
        PushCanvasHistory(
            undo: () => { node.Text = oldText; RefreshCanvas(); },
            redo: () => { node.Text = newText; RefreshCanvas(); });
        MarkCanvasDirty();
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
        MarkCanvasDirty();
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
        MarkCanvasDirty();
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
        MarkCanvasDirty();
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
        MarkCanvasDirty();
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
        MarkCanvasDirty();
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
        MarkCanvasDirty();
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
        MarkCanvasDirty();
        RefreshCanvas();
        GpuViewer.SelectCanvasNode(node.Id);
        GpuViewer.BeginCanvasEdit(node.Id);
    }

}

/// <summary>白板模式列表的一行。Title 走属性通知：卡片编辑框每 400ms 提交一次文字，
/// 只改这一行的标题，不整表重建 —— 后者会把列表 ScrollViewer 弹回顶部、打断正在看的上下文。
/// 其余字段在重建时算好，行内不再推导（x:Bind 一次性绑定即可）。</summary>
public sealed class BoardCardViewModel : INotifyPropertyChanged
{
    private string _title = "";

    public string NodeId { get; set; } = "";      // 与 CanvasNode.Id 对应，供定点更新标题
    public int PageIndex { get; set; }            // 0-based，与 CanvasNode.GlancePage 同源
    // 卡片矩形：【页局部未旋转 PDF 点】，与存储 / 渲染同一空间，原样交给视口换算。
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public string Title
    {
        get => _title;
        set
        {
            if (string.Equals(_title, value, StringComparison.Ordinal)) return;
            _title = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }

    public string PageText { get; set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;
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

/// <summary>页面上的一条墨迹。只由 Win2D 直接绘制、不被 XAML 绑定，
/// 所以不实现 INotifyPropertyChanged —— 没有订阅者，广播只是白跑一遍。</summary>
public class AnnotationViewModel
{
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

    /// <summary>最后一个已收点的坐标（没有点时为 NaN）。落笔时据此判断"这一点够不够远"，不必每帧取快照。</summary>
    internal Point LastPoint
    {
        get
        {
            lock (PointsLock)
                return _pointsCollection.Count > 0 ? _pointsCollection[^1] : new Point(double.NaN, double.NaN);
        }
    }

    /// <summary>发起页（0-based）：这条笔的点全部是这一页的局部坐标，画在这一页的变换之下。</summary>
    public int PageIndex { get; set; } = -1;

    /// <summary>全局落笔序号（跨页统一递增）：绘制时按它升序，于是"后画的压在上面"对跨页的笔同样成立。
    /// 一笔一个号、撤销重做都带着它走，故整条笔的层级不随撤销/重做改变。
    /// 存盘点按它排序写进画布文件，故重开文档后层级与落笔当时一致。</summary>
    public int ZOrder { get; set; } = -1;

    public AnnotationType Type { get; set; }

    public string ColorHex
    {
        get => _colorHex;
        set
        {
            if (_colorHex == value) return;
            _colorHex = value;
            MarkInkChanged();
        }
    }

    public double Thickness
    {
        get => _thickness;
        set
        {
            if (_thickness == value) return;
            _thickness = value;
            MarkInkChanged();
        }
    }

    public PointCollection PointsCollection
    {
        get => _pointsCollection;
        set
        {
            lock (PointsLock)
            {
                if (ReferenceEquals(_pointsCollection, value)) return;
                _pointsCollection = value;
                _pointsVersion++;
                _pointsSnapshot = null;
                _snapshotVersion = -1;
            }
        }
    }

    /// <summary>笔的属性被改（颜色 / 粗细）⇒ 画布置脏；加载期间由 SuppressDirtyMarking 抑制。</summary>
    private static void MarkInkChanged()
    {
        if (!MainPage.SuppressDirtyMarking) MainPage.Current?.MarkCanvasDirty();
    }
}
