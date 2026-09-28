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
using Glance.Interop;
using Glance.Services;
using static Glance.Tuning;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Drawing;

namespace Glance;

public enum EditMode
{
    Navigate,
    Pen
}

public enum AnnotationType
{
    Pen
}

public sealed partial class MainPage : Page
{
    public static MainPage? Current { get; private set; }

    public bool HasUnsavedChanges { get; set; } = false;

    // 批量加载（从磁盘读回）标注时临时抑制"脏标记 + 自动保存"：对象初始化器会触发 AnnotationViewModel
    // 的 ColorHex/Thickness/Content setter，这些 setter 原本一律置 HasUnsavedChanges=true。
    // 否则从磁盘读回"已保存"的标注会被误判成未保存更改，导致即使本次没画、关闭也弹保存询问。
    // 加载期间置 true，finally 复位；真实的用户编辑（画/撤销）不受限。
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
    // 渲染线程无差别争抢 FFI 锁，把主视图每页渲染插队放大。串行化缩略图后主视图最多被插队一次。
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);
    // 缩略图【待渲染任务集合】（去重 + 在途上限）：PageListView 默认 CacheLength=4.0 会 realized
    // 视口外前后各 2 屏（大跳转后 ~120 项），若每一项 realized 都排队渲染，MuPDF 串行解码上百个
    // 整页图像流 = 缩略图"很久才加载完"。只允许 MaxThumbnailPending 个在途，完成一个按"离侧边栏
    // 视口中心近"补位（RefillThumbnailQueue），滚动/翻页时主动补 → 可见缩略图快速出图、风暴封顶。
    private readonly HashSet<int> _thumbnailQueued = new();
    private const int MaxThumbnailPending = 8;
    private ScrollViewer? _sidebarScrollViewer;  // PageListView 内部 ScrollViewer（Loaded 后缓存，滚动补渲染用）
    private double _sidebarItemHeight;           // 缩略图项高实测缓存（~56px，GetSidebarItemHeight 惰性取）

    // GPU 阅读器为唯一渲染路径（GpuViewer / GpuViewerRight）。旧 ScrollViewer+Image 回退路径已移除。
    // Right (comparison) PDF now uses the same MuPDF engine as the left side,
    // instead of the UWP Windows.Data.Pdf engine + full in-memory file copy.
    // This avoids doubling memory (the old path loaded the whole file into an
    // InMemoryRandomAccessStream AND ran a second PDF engine → ~1GB on large scans).
    private PdfRenderService? _pdfRenderServiceRight;
    private CancellationTokenSource? _pdfRenderCts;
    private CancellationTokenSource? _pdfRenderRightCts;
    private readonly HashSet<int> _realizedSidebar = new(); // 侧栏已生成缩略图的页索引(静默重渲染时增量刷新用)。旧路径的 _realizedLeft/_realizedRight/_renderingPages*/_previewRenderingPages* 已于 GPU 化后移除。
    private ObservableCollection<PdfPageViewModel> _pages = new();
    private int _currentPageIndex = 0;
    private bool _isUnloading = false;
    private string _currentPdfPath = "";
    private string? _currentPdfPassword;
    // 能否将批注写回原文件（覆盖保存）。加密/只读 PDF 为 false —— 这类文档永不改写原文件，
    // 走另存为生成未加密副本；副本可写 → true，可覆盖自身。批注以真实 PDF /Ink 注解对象写入文件，
    // 文件即唯一真相源，"不保存退出"只需从文件重载即可回到上次保存态，无需任何草稿/副本。
    private bool _canOverwriteOriginal = true;
    private SettingsService _settingsService = new();
    private uint _totalPages = 0;

    // 侧边栏：缩略图 / 书签 / 大纲
    private readonly BookmarkStore _bookmarkStore = new();
    private readonly ObservableCollection<BookmarkViewModel> _bookmarks = new();
    private readonly ObservableCollection<OutlineItemViewModel> _outlineItems = new();
    private enum SidebarMode { Thumbnails, Bookmarks, Outline }
    private SidebarMode _currentSidebarMode = SidebarMode.Thumbnails;
    // （侧边栏开关的阅读位置补偿已整体移到 GpuPageView：CaptureHostResizeAnchor / BeginHostResizeReanchor。
    //   快照在 ToggleSidebar_Click 里【翻转前】同步取，重锚由 PaneOpening/PaneClosing 排一个 Low 回合触发，
    //   这里不再保留任何延时器或视口尺寸字段。）

    // （旧 CPU 渲染路径的性能参数/预览层/滚动门控已随 GPU 化整体移除；
    //   页位图缓存与预取策略统一由 GpuPageView 内部管理。）

    private double _currentZoomPercent = 100;
    
    // Annotation states
    private EditMode _currentMode = EditMode.Navigate;
    private bool _isDrawingHighlight = false;
    private AnnotationViewModel? _activeHighlight;
    private string _activeColorHex = "#F2C200"; // Default Yellow

    // Live highlight brushes for the Pen mode ToggleButton: the checked-state background
    // and foreground are overridden to follow the currently selected pen color.
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _penHighlightBrush =
        new(Microsoft.UI.Colors.Yellow);
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _penForegroundBrush =
        new(Microsoft.UI.Colors.Black);

    // 颜色按钮不再有任何包围环，仅用「圆点大小」区分状态：
    // 未选中 = 原大小；悬浮/选中 = 1.72×。
    // ScaleTransform 共享实例，多个按钮引用同一对象，不参与布局（无位移跳动）。
    private readonly Microsoft.UI.Xaml.Media.ScaleTransform _colorSwatchScale = new()
    { ScaleX = 1.72, ScaleY = 1.72 };
    private Button? _selectedColorButton; // 当前选中的颜色按钮（悬浮恢复时判断用）

    // Currently selected pen thickness (replaces the old ThicknessSlider).
    private double _activeThickness = 3.5;
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _thicknessPresetTransparentBrush =
        new(Microsoft.UI.Colors.Transparent);
    // 粗细预设按钮的选中/悬浮背景。颜色按主题自适应（浅色=中灰、深色=亮灰）：
    // SystemControlBackgroundListMediumBrush 在深色主题下解析偏暗，与深色胶囊底色对比不足，
    // 故不再复用该主题资源，改为两套显式颜色，主题切换时由 ApplyPenOptionThemeBrushes 更新。
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _thicknessPresetSelectedBrush =
        new(Windows.UI.Color.FromArgb(255, 0xC0, 0xC0, 0xC0));

    /// <summary>
    /// 按当前主题（浅色/深色）设置画笔胶囊选中提示色：
    /// 粗细选中背景：浅色=中灰、深色=亮灰（保证与深浅胶囊底色都有足够对比）。
    /// （颜色按钮无环、纯靠大小区分，无需主题适配。）
    /// </summary>
    private void ApplyPenOptionThemeBrushes()
    {
        bool dark = this.ActualTheme == ElementTheme.Dark;
        _thicknessPresetSelectedBrush.Color = dark
            ? Windows.UI.Color.FromArgb(255, 0x52, 0x52, 0x52)
            : Windows.UI.Color.FromArgb(255, 0xC0, 0xC0, 0xC0);
    }

    // Undo/Redo history stack
    // 撤销栈：本会话所有笔画，后进先出（Ctrl+Z 连续按 = 多步撤销）。从文件读回的批注不入栈。
    private List<(PdfPageViewModel Page, AnnotationViewModel Annotation)> _annotationHistory = new();
    // 重做栈：撤销时把条目从历史栈移入此处（Ctrl+Y 重做）；新笔画开始即清空（重做分支失效，
    // 与 PS 历史记录同逻辑：新操作使 redo 分支作废）。换文档时随历史一并清空。
    private List<(PdfPageViewModel Page, AnnotationViewModel Annotation)> _annotationRedo = new();

    // Secondary PDF pages (for Split View comparison)
    private ObservableCollection<PdfPageViewModel> _pagesRight = new();

    // Sync scrollbar state (split view)
    private bool _isSyncScrolling = false;
    private double _lastSyncValue = 0;

    // 保存/书签提示胶囊：微信式堆叠（见 ShowSaveToast / SaveToastStack）。
    private sealed class SaveToastItem
    {
        public Border Capsule = null!;
        public TranslateTransform Transform = null!;
        public DispatcherTimer Timer = null!;
        public Storyboard? SlideSb;   // 进行中的位移动画，重定位前先停，避免同属性双动画冲突
        public int Pos;               // 距底边格数：0=最底(最新)，越大越靠上(越旧)
    }
    private readonly List<SaveToastItem> _saveToasts = new();
    private const int SaveToastMax = 5;          // 栈深上限：超出即强制淡出最旧一枚
    private const double SaveToastSlot = 50;     // 单枚占位 = 高42 + 间隙8
    private const double SaveToastSlideMs = 160; // 上移/补位滑动时长
    private IntPtr _hWnd;                          // 主窗口句柄（画笔圆环作用域使用）
    private const int IDC_ARROW = 32512;        // 系统箭头（= OCR_NORMAL），用于退出时兜底恢复
    private const uint SPI_SETCURSORS = 0x0057;  // SystemParametersInfo 命令：重载系统光标方案（恢复原生箭头）

    // 画笔模式圆环光标（窗口作用域，真实 HCURSOR + 作用域限定）：
    // WinUI 3 主视图（客户区）由 XAML Island 承载，光标由框架经【指针输入】设置，
    // 既不走 WM_SETCURSOR、也不易被 WM_MOUSEMOVE 拦截——任何子类化 Island 的方式都拦不到它。
    // 故改用最可靠方案：用 SetSystemCursor 把"系统箭头"本身替换为真实 HCURSOR 圆环。
    // 这样框架每帧 SetCursor(箭头) 实际画出的就是圆环，主视图/边缘生效、零滞后。
    // 窗口作用域靠"指针是否在本窗口内且本窗口为前台"判定：进入窗口装圆环、离开/失焦即恢复系统箭头；
    // 另通过 IsPointerOverTitleBar 把【标题栏区域】排除在圆环之外（标题栏恢复原生鼠标）。
    // 窗口外：其他程序由 OS 各自显示原生光标；仅在指针位于本窗口主体内的极短区间(≤定时周期)系统箭头为圆环，
    // 退出/崩溃都通过 SPI_SETCURSORS 兜底恢复，彻底告别此前"卡死圆环"。
    private bool _penRingActive;
    private IntPtr _hRingCursor;        // 当前画笔圆环光标句柄（GDI 资源，需 DestroyIcon 释放）
    private int _ringDiameter;          // 上次构建时的位图边长（= 显示尺寸），用于判断是否需要重建
    private string? _ringColorHex;      // 上次构建时的颜色，用于判断是否需要重建
    private bool _ringInstalled;        // 系统箭头是否已被替换为圆环（作用域判定后设置）
    private int _ringSuppressCount;     // 临时抑制圆环的引用计数（如关闭窗口"保存更改"对话框、Ctrl+S 另存为对话框期间，整个窗口恢复原生鼠标）。支持嵌套。
    private Microsoft.UI.Xaml.DispatcherTimer? _ringScopeTimer; // 周期性检测指针是否在窗口内，作用域开/关圆环
    private DispatcherTimer? _zoomPillHideTimer; // 缩放胶囊隐藏兜底：Flyout(快速缩放菜单)关闭瞬间的输入上下文切换会吞掉胶囊的 PointerExited，导致胶囊"悬停卡住"不隐藏；低频轮询核对实时光标位置兜底淡出

    public MainPage()
    {
        Current = this;
        this.InitializeComponent();

        // 主视觉树内按钮：一律不可 Tab 聚焦（彻底关掉 Tab/方向键落到按钮的路径，兜底）。
        SetButtonsNonTabStop();
        // Popup（PenOptionsPopup）内按钮在独立视觉树，主页面冒泡事件抓不到，单独订阅处理。
        AttachDefocusToPopupButtons();
        // 颜色按钮悬浮预览：环=按钮自身色+放大一圈（Popup 内元素需单独挂事件）。
        AttachColorSwatchHover();

        // 全局键盘聚焦拦截：禁止任何焦点落到 ButtonBase（用户要求按钮只由鼠标单击触发，
        // 无 Tab+Enter 触发路径，点后焦点也不残留→Enter/Space 不重触发）。不限 FocusState：
        // WinUI 3 的 Tab 导航 FocusState 未必是 Keyboard（可能是 Programmatic），故全拦。
        this.AddHandler(GettingFocusEvent, new TypedEventHandler<UIElement, GettingFocusEventArgs>(OnGettingFocusBlockButtons), true);
        // 全局点击剥夺焦点（兜底）：鼠标点任意按钮后，在 PointerReleased 强制剥夺其焦点，
        // 防 Enter/Space 重触发。用「禁用/启用」可靠手法（见 DefocusButton），而非依赖某控件可聚焦。
        this.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPointerReleasedDefocus), true);
        // 全局拦截 Tab：用户要求界面只由鼠标操作，Tab 不应把键盘焦点移到任何控件
        // （页数输入框、侧边栏缩略图、工具栏按钮等都在主视觉树内，Tab 的 KeyDown 会冒泡到此处）。
        // 标记 Handled=true 后框架不再执行默认的 Tab 焦点跳转。仅拦 Tab，方向键/Enter 不受影响
        // （ComboBox、页码框的 Enter 跳转照常工作）。注意：Flyout/Popup 是独立视觉树，主树 KeyDown
        // 抓不到，设置面板里的 Tab 由 SettingsFlyout_KeyDown（XAML 挂在 Flyout 内容根）单独拦截，
        // 其可聚焦控件另设 IsTabStop=false（见 MainPage.xaml）。
        this.AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDownSuppressTab), true);

        // 主键盘 = / - ：WinUI XAML 枚举解析不支持 OemPlus/OemMinus 字符串（WMC0055），
        // 故在此用 Windows.System.VirtualKey 枚举添加，与 XAML 中 Add/Subtract（小键盘 +/-）互补。
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

        PageListView.ItemsSource = _pages;
        BookmarkListView.ItemsSource = _bookmarks;
        OutlineListView.ItemsSource = _outlineItems;

        // GPU 视图 → 工具栏回显：滚动更新页码框/侧栏选中；缩放更新百分比显示。
        GpuViewer.CurrentPageChanged += (s, idx) =>
        {
            _currentPageIndex = idx;
            // 仅在输入框未获得焦点时回显页码：用户正在输入时不能覆盖其文本，
            // 否则每次滚动/缩放都会把光标顶到最左、并显示（可能错误的）页码。
            if (PageNumberInput.FocusState == FocusState.Unfocused)
            {
                PageNumberInput.Text = (idx + 1).ToString();
            }
            if (idx >= 0 && idx < _pages.Count && PageListView.SelectedIndex != idx)
            {
                PageListView.SelectedIndex = idx;
            }
            // 侧边栏跟随：直接操作内部 ScrollViewer 按索引滚动（不依赖 ScrollIntoView/选中项自动滚动——
            // WinUI 中"设置 SelectedIndex 后立即 ScrollIntoView"对 unrealized 目标经常失效，且选中项
            // 容器未就绪时滚动请求被吞；按固定项高 × 索引直接 ChangeView 是确定有效的路径）。
            ScrollSidebarToIndex(idx);
            // 主视图翻页 → 兜底补渲染新页附近缺图缩略图（Low 优先级：等侧边栏滚动
            // 落地、realized 更新后再按新视口中心补位；侧边栏滚动事件本身也会触发一次，幂等）。
            this.DispatcherQueue?.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RefillThumbnailQueue);
        };
        GpuViewer.ZoomPercentChanged += (s, zp) => SetZoomPercent(zp);
        // GPU 视图滚动/缩放 → 更新中间同步滚动条，并剥夺页码框焦点（见 DefocusPageNumberInput 注释）。
        GpuViewer.ViewportChanged += (s, e) => { DefocusPageNumberInput(); UpdateSyncScrollBar(); };
        GpuViewerRight.ViewportChanged += (s, e) => { DefocusPageNumberInput(); UpdateSyncScrollBar(); };
        // 分屏同步滚动条 SyncScrollBar 的滑块拖拽 → 两侧视图进入"拖拽期抑制渲染请求"，
        // 在 SyncScrollBar_Scroll 内按 ScrollEventType(ThumbTrack/EndScroll) 判定转发
        // （GpuPageView 内置滚动条由其自身在模板树中订阅 Thumb 事件捕获，见 HookScrollBarThumbs）。
        // 布局方向变化 → 同步更新工具栏按钮图标/提示（也覆盖通过 API 而非按钮切换的情况）。
        GpuViewer.LayoutModeChanged += (s, mode) => UpdateLayoutToggleVisual();
        GpuViewerRight.LayoutModeChanged += (s, mode) => UpdateLayoutToggleVisual();

        // GPU 标注叠加层：数据仍在 _pages[i].Annotations（保存/撤销/导出管线全复用），
        // GpuPageView 负责绘制与坐标换算（页局部 PDF 点，与旧路径同一坐标空间）。
        GpuViewer.AnnotationProvider = idx =>
            (idx >= 0 && idx < _pages.Count) ? _pages[idx].Annotations : null;
        GpuViewer.AnnotationPointerPressed += GpuAnnotation_Pressed;
        GpuViewer.AnnotationPointerMoved += GpuAnnotation_Moved;
        GpuViewer.AnnotationPointerReleased += GpuAnnotation_Released;

        WelcomePanel.Visibility = Visibility.Visible;
        SidebarSplitView.Visibility = Visibility.Collapsed;

        // 欢迎页(未打开文档)时隐藏"只对文档有意义"的工具栏按钮：画笔模式 / 旋转 / 纵横排列。
        // 用 Visibility 属性变更回调集中驱动，而不是在每处 WelcomePanel.Visibility 赋值点各写一遍
        // ——加载成功/失败、密码取消、命令行打开等所有切换路径都会自动同步，不会漏。
        WelcomePanel.RegisterPropertyChangedCallback(UIElement.VisibilityProperty,
            (s, dp) => ApplyWelcomeChrome(WelcomePanel.Visibility == Visibility.Visible));
        ApplyWelcomeChrome(true);   // 初始就是欢迎页（赋同值不触发回调，故显式应用一次）

        this.Loaded += MainPage_Loaded;
        // 侧边栏展开/收起时驱动 Content 层的投影载体：SplitView Pane 自身阴影无法落到主视图，
        // 故在 ViewerContainerGrid 内放了一枚透明投影元素（SidebarDropShadow），由事件联动显隐。
        //
        // 【开关侧边栏 = 保持"阅读位置不变量"，而不是死板 fit 到视口】
        // 目标（用户拍板）：纵向布局下，开关前/后视口左右两条边线切到的内容完全一致 ——
        //   侧边栏 Inline 挤开等同于把主轴视口按比例缩放，zoom 随之等比缩放，于是"视口里可见的
        //   文档宽度"不变，阅读内容不产生任何左右位移。原本 fit 宽度 ⇒ 开关后仍恰好 fit 新宽度；
        //   原本 ¼ fit ⇒ 仍是 ¼。（更早被否的两版：① 无脑 FitWidth 重适应 → 把用户手动调好的倍率
        //   拽回 fit，判为"太硬"；② 以视口中心为不动点 → 左右两端各平移 (vpW'/2)(1/z − 1/z')，
        //   判为"左右端切到的内容变了"。）
        //
        // 时机：翻转 IsPaneOpen【之前】同步取不变量快照（列宽在过渡起点即变，事后读不到旧几何）；
        // 重锚则挂在**过渡起点**事件（PaneOpening / PaneClosing）上、再让出一个 Low 布局回合 ——
        // 这样 PDF 的缩放弹簧与侧边栏滑动【同时开始】，整个开关是一段连贯动画，而不是滑动完再"啪"地缩放。
        // 依据（WinUI 2.2.1 generic.xaml 的 SplitView 模板）：开/关【两个方向】的 ContentRoot.(Grid.Column)
        // 都在 KeyTime=0 离散切换（开 generic.xaml:35397-35398 → 1；关 :35358-35359 → 0），
        // 即【布局宽度在过渡起点就已到位】；其后的动画只是 ContentTransform/PaneTransform 的
        // TranslateX **渲染**滑动（不改宽度）—— 所以重锚时机对开、关两个方向都成立。
        // 所以过渡起点之后一个 Low 回合，Viewer.Viewport* 必然已是最终值，正是起弹簧的时机；
        // 而旧版写死 120ms + 挂在过渡终点，等于"等滑完再瞬间缩放"，手感断裂。
        // 兜底：即便事件时序与预期有出入，BeginHostResizeReanchor 起手还会调一次 Viewer.UpdateLayout()，
        // 把列宽变更强制落到布局上再读 Viewport* ⇒ "起弹簧时几何已到位"不依赖该事件的精确时序。
        // 连开连关也天然合并：快照每次翻转前覆盖一次，GpuPageView 内部以降清标志去重。
        // 【侧边栏自身的滑动时长不在此处干预】曾经试过用 VSM 公开 API 改模板关键帧的 KeyTime
        // （一路调到开 500ms / 关 400ms，是框架默认 0.2s/0.1s 的 2.5~4 倍），用户实测**无感**、明确放弃
        // ⇒ 已整体移除，恢复框架默认。⚠ 无感的原因**未确证**：从 2.5 倍差异不可能感知不到这一点看，
        // 更像是那段代码**压根没生效**（两条可疑路径：① PaneOpening/PaneClosing 可能在 VisualStateManager
        // 状态切换【之后】才触发，于是每次都赶不上本次 storyboard；② 模板根的 VisualStateGroups /
        // VisualTransition.Storyboard 取不到时被 try/catch 静默吞掉）。**别再加回来** ——
        // 要遍历模板 Storyboard，失败还只能静默回退，收益与复杂度不成正比。
        SidebarSplitView.PaneOpening += (s, e) => QueueSidebarReanchor();
        SidebarSplitView.PaneClosing += (s, e) =>
        {
            if (SidebarDropShadow != null) SidebarDropShadow.Visibility = Visibility.Collapsed;
            QueueSidebarReanchor();
        };
        SidebarSplitView.PaneOpened += (s, e) =>
        {
            if (SidebarDropShadow != null) SidebarDropShadow.Visibility = Visibility.Visible;
            UpdateSidebarTopInset();   // 展开后按实测工具栏高度让出顶部空间
        };
        // 整个侧边栏隐藏（如回到欢迎页）时，投影一并收起。
        SidebarSplitView.RegisterPropertyChangedCallback(UIElement.VisibilityProperty,
            (s, dp) =>
            {
                if (SidebarSplitView.Visibility != Visibility.Visible && SidebarDropShadow != null)
                    SidebarDropShadow.Visibility = Visibility.Collapsed;
            });

        // 主题切换（设置面板选择/跟随系统变化）时刷新画笔胶囊的选中提示色，
        // 并重刷当前选中的颜色环与粗细背景，保证深浅色模式下对比清晰。
        this.ActualThemeChanged += (s, e) => RefreshPenOptionThemeHighlights();
    }

    // 侧边栏开关的重锚触发点：过渡起点事件之后排一个 Low 回合再重锚。
    // Low 在布局/渲染之后执行 ⇒ 此时 SplitView 已把列宽离散改到最终值、Viewport* 也已刷新，可以起弹簧。
    // 快照在 ToggleSidebar_Click 翻转前取；GpuViewer 不可见时 CaptureHostResizeAnchor 会直接 return
    // （_hostResizeCaptureValid 恒 false），BeginHostResizeReanchor 自行早退。
    private void QueueSidebarReanchor()
    {
        if (GpuViewer == null || GpuViewer.Visibility != Visibility.Visible) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (GpuViewer == null || GpuViewer.Visibility != Visibility.Visible) return;
            GpuViewer.BeginHostResizeReanchor();
        });
    }

    /// <summary>
    /// 主题变化后：更新胶囊提示色 brush，并重刷当前选中态（颜色大小提示 + 粗细背景）。
    /// 共享 brush 对象改色后，重新调用高亮函数让每个按钮重新取 brush 引用并重绘。
    /// </summary>
    private void RefreshPenOptionThemeHighlights()
    {
        ApplyPenOptionThemeBrushes();
        SyncColorSwatchHighlight();
        SelectThicknessPreset(_activeThickness);
    }

    /// <summary>
    /// 工具栏内任意按钮点击后的焦点剥夺处理。
    /// WinUI 的 Button/ToggleButton 在持有键盘焦点时会响应 Enter/Space；若点击后焦点残留，
    /// 用户随后按回车/空格会再次触发该按钮。用户要求工具栏按钮只由鼠标单击触发，故点击后
    /// 立即把焦点从按钮移走（落到 Page 上，Page 无默认按键行为），后续键盘按键不再误触。
    /// 带 Flyout 的按钮（如「设置」）不处理：框架会把焦点移入 Flyout 由其自行管理，强行剥夺
    /// 焦点会立刻关闭 Flyout。
    /// </summary>
    /// <summary>
    /// 任意按钮（工具栏、对比视图胶囊、画笔胶囊内预设按钮等）点击后剥夺焦点，防止随后 Enter/Space 重复触发。
    /// 用户要求按钮只由鼠标单击触发。带 Flyout 的按钮（「设置」）及其内部控件跳过——框架把焦点移入
    /// Flyout 自行管理，剥夺焦点会立刻关闭 Flyout。
    /// </summary>
    /// <summary>
    /// 鼠标在任意按钮上松开（PointerReleased，全局冒泡）后强制剥夺该按钮的焦点，防随后 Enter/Space 重复触发。
    /// 覆盖工具栏、侧边栏、对比视图胶囊、缩放胶囊等主树内按钮（Popup 内按钮由 AttachDefocusToPopupButtons 单独处理）。
    /// 带 Flyout 的按钮（「设置」）及其内部 FlyoutPresenter 内的控件跳过，避免把焦点移出 Flyout。
    /// 剥夺焦点用 DefocusButton（禁用/启用可靠手法），而非 GpuViewer.Focus——自定义控件未必可聚焦，
    /// Focus 会静默失败、焦点仍留在按钮上→Enter/Space 仍重触发（这是上一版失效的根因）。
    /// </summary>
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

    /// <summary>
    /// 禁止任何焦点落到 ButtonBase 上：用户要求按钮只由鼠标单击触发，不保留 Tab+Enter 触发路径，
    /// 点后焦点也不残留→Enter/Space 不重触发。Tab/方向键与鼠标点击都拦（WinUI 3 的 Tab 导航
    /// FocusState 未必是 Keyboard，故不限 FocusState）；Popup 内按钮在独立视觉树、本 handler 抓不到，
    /// 由 PopupButton_GettingFocus 处理。编程聚焦（Flyout 自动聚焦，独立树）不受影响。
    /// </summary>
    private void OnGettingFocusBlockButtons(UIElement sender, GettingFocusEventArgs e)
    {
        // 根因修复：原实现用 e.Cancel=true 拦截焦点落到 ButtonBase / 程序化聚焦输入框。但 WinUI 在
        // 焦点移动不可取消的情形（如 FromOutside：Alt-Tab 切回窗口、点击任务栏重新激活、窗口失焦再
        // 获焦后 OS 还原焦点）下，设置 Cancel 会抛 E_INVALIDARG（"Value does not fall within the
        // expected range"），表现为「正常使用中放置一会后闪退」。改用 TryCancel()：不可取消时返回
        // false 而非抛异常，语义等价且安全。
        if (e.NewFocusedElement is ButtonBase)
            e.TryCancel();
        // 页数输入框：仅允许鼠标点击聚焦（FocusState.Pointer）。Tab 键与「按钮 Defocus 后框架自动
        // 把焦点重定位到本框」这类程序化焦点一律拦截。这样既把 Tab 彻底挡在框外，又保留鼠标点进
        // 去编辑页码的能力（直接设 IsTabStop=false 在本项目会把鼠标点击聚焦也禁掉，导致无法改页数）。
        else if (e.NewFocusedElement == PageNumberInput && e.FocusState != FocusState.Pointer)
            e.TryCancel();
    }

    /// <summary>
    /// 全局拦截 Tab：主视觉树内任意控件按下 Tab，标记 Handled=true，框架即不执行默认的 Tab 焦点跳转，
    /// 使 Tab 在界面上彻底失效（用户要求只由鼠标操作）。此 handler 仅在焦点位于 MainPage 子树内控件时触发
    /// （KeyDown 冒泡事件）。Ctrl 系（P/Z/S）与方向键/空格/PageUp·Down/Home·End 全部走 KeyboardAccelerator
    /// （MainPage.Nav_Invoked，输入管理器全局分发、与焦点无关），不在此 handler 内。
    /// Enter 仍由各自控件处理。Flyout/Popup 是独立视觉树、此 handler 抓不到，设置面板里的 Tab 由 SettingsFlyout_KeyDown 处理。
    /// </summary>
    private void OnKeyDownSuppressTab(object sender, KeyRoutedEventArgs e)
    {
        // Tab：剥夺焦点遍历（保留自定义控件焦点行为），其余按键放行。
        // 导航键已统一走 KeyboardAccelerator（Nav_Invoked，与焦点无关），此处不再处理。
        if (e.Key == VirtualKey.Tab)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// 设置面板（Flyout，独立视觉树）内的 Tab 拦截：KeyDown 从 Flyout 内控件冒泡到此处，
    /// 标记 Handled=true 阻止焦点跳到 ThemeComboBox / 网站链接 / GitHub 链接等。
    /// 作为额外保险，这些控件在 XAML 里也设了 IsTabStop=false。
    /// </summary>
    private void SettingsFlyout_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Tab)
            e.Handled = true;
    }

    /// <summary>
    /// 强制剥夺某个按钮的键盘焦点：临时禁用再启用，使其无法持焦（焦点被交出），随即重新启用，用户无感。
    /// 不能依赖 Control.Focus(FocusState.Unfocused)（抛 E_INVALIDARG），也不能依赖 GpuViewer.Focus(Programmatic)
    /// （自定义控件未必可聚焦，Focus 会静默失败、焦点仍留在按钮上→Enter/Space 重触发）。
    /// 沿用 DefocusPageNumberInput 在本项目验证可靠的「禁用/启用」手法。
    /// </summary>
    private void DefocusButton(ButtonBase bb)
    {
        if (bb == null) return;
        bb.IsEnabled = false;
        bb.IsEnabled = true;
    }

    /// <summary>
    /// 主视觉树内的按钮：一律设为不可 Tab 聚焦（IsTabStop=false），彻底关闭 Tab/方向键落到按钮上的路径。
    /// IsTabStop 只影响键盘导航、不影响鼠标点击聚焦，故点击仍能触发按钮、只是点后焦点会被
    /// OnGettingFocusBlockButtons / OnPointerReleasedDefocus 剥夺，不会残留→Enter/Space 不重触发。
    /// （Popup 内的按钮在主树之外，由 AttachDefocusToPopupButtons 单独处理。）
    /// </summary>
    private void SetButtonsNonTabStop()
    {
        if (this.Content is DependencyObject root)
            WalkSetNonTabStop(root);
    }

    private void WalkSetNonTabStop(DependencyObject d)
    {
        // 跳过 Popup 子树：Popup 内容在独立视觉树，主树遍历够不到；其按钮由 AttachDefocusToPopupButtons 处理。
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

    /// <summary>
    /// Popup（PenOptionsPopup）内的按钮在独立视觉树，主页面冒泡的 PointerReleased/GettingFocus 抓不到。
    /// 故在此直接给每个按钮订阅：① Click 后剥夺焦点（防 Enter/Space 重触发）；② GettingFocus 取消
    /// （防 Tab/点击聚焦）；并设 IsTabStop=false（与 SetButtonsNonTabStop 一致）。
    /// </summary>
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
        // E_INVALIDARG，避免「放置一会后闪退」。
        if (e.NewFocusedElement == sender) e.TryCancel();
    }


    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeToWindowStateChanges();
        InitializeSettings();
        InitializeLocalization();
        UpdateTopToolbarMargin();

        // 接管原生视觉状态机（详见 Services/ToggleButtonVisualOverride.cs 注释）：
        // · 顶部工具栏「阅读模式」按钮
        // · 侧边栏「缩略图 / 书签 / 大纲」三模式切换键 —— 互斥，共用 SidebarMode_Click + Tag，
        //   切换由 ApplySidebarMode() 程序化改 IsChecked，接管器会把每次变化瞬间写成颜色
        // · 顶部工具栏「拆分视图（打开/关闭右侧对比视图）」开关 SplitViewToggleButton
        //   （默认 Visibility=Collapsed，加载 PDF 后设为可见；IsChecked 由 SplitViewToggle_Click /
        //   CloseSecondPdf_Click 程序化变更，接管器订阅 IsCheckedProperty 回调，勾选态即 Obsidian 紫）
        ToggleButtonVisualOverride.Attach(ReadModeButton);
        ToggleButtonVisualOverride.Attach(ModeThumbnailsButton);
        ToggleButtonVisualOverride.Attach(ModeBookmarksButton);
        ToggleButtonVisualOverride.Attach(ModeOutlineButton);
        ToggleButtonVisualOverride.Attach(SplitViewToggleButton);

        // 侧边栏滚动 → 补渲染滚入视口的缩略图（缓存窗口项滚入视口不触发 ContainerContentChanging）。
        AttachSidebarScrollRefill();

        // 缩放胶囊隐藏兜底：快速缩放菜单(Flyout)打开期间 BottomZoomPill_PointerExited 被忽略，
        // Flyout 关闭后若指针仍在胶囊上，ZoomPresetFlyout_Closed 的一次性兜底也会放弃，
        // 此时唯一隐藏路径依赖下一次 PointerExited——而 WinUI 在 popup/覆盖层关闭瞬间的输入
        // 上下文切换经常吞掉它（"点开菜单→点其他按钮→移出胶囊不隐藏"高发）。常驻低频轮询兜底。
        if (_zoomPillHideTimer == null)
        {
            _zoomPillHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _zoomPillHideTimer.Tick += ZoomPillHideTimer_Tick;
        }
        try { _zoomPillHideTimer.Start(); } catch { }

        // Hide the toolbar when the pointer leaves the whole page (i.e. exits the window).
        // WinUI 3's XamlRoot has no PointerExited, but the top-level Page element does fire
        // it when the pointer leaves the page bounds.
        this.PointerExited += XamlRoot_PointerExited;

        // Safety net for the auto-hide: a moved-anywhere handler (handledEventsToo, so it
        // still fires while the viewer is handling the move) retracts the toolbar as soon as
        // the pointer is over the document instead of the bar. This is what makes the bar
        // retract inside the window, not only when leaving it.
        this.AddHandler(PointerMovedEvent, new PointerEventHandler(Root_PointerMoved), true);

        // 页面基础的指针事件同样锚定"最近一次真实移动"的时刻。工具栏子级若把事件标记 Handled，
        // 页面基础（handledEventsToo）仍会运行、Root_PointerMoved 却不会——此处只记录时刻，
        // 不做任何命中/归属判定（判定一律交给 Root_PointerMoved，避免两处结论互相打架）。
        this.AddHandler(PointerEnteredEvent, new PointerEventHandler(Root_PointerActivity), true);
        this.AddHandler(PointerPressedEvent, new PointerEventHandler(Root_PointerActivity), true);
        this.AddHandler(PointerExitedEvent, new PointerEventHandler(Root_PointerActivity), true);


        // Keep the floating pen-options capsule anchored below the Pen button on resize,
        // and keep the top toolbar positioned flush under the window title bar.
        this.SizeChanged += (s, args) =>
        {
            UpdateTopToolbarMargin();
            if (PenOptionsPopup != null && PenOptionsPopup.IsOpen)
                PositionPenOptionsPopup();
        };

        // Make the Pen mode button's highlight follow the currently selected pen color.
        // Override the ToggleButton's checked-state theme brushes at the element level so the
        // default accent (blue) is replaced by _penHighlightBrush (updated live in ApplyPenHighlight).
        if (PenModeButton != null)
        {
            PenModeButton.Resources["ToggleButtonBackgroundChecked"] = _penHighlightBrush;
            PenModeButton.Resources["ToggleButtonBackgroundCheckedPointerOver"] = _penHighlightBrush;
            PenModeButton.Resources["ToggleButtonBackgroundCheckedPressed"] = _penHighlightBrush;
            PenModeButton.Resources["ToggleButtonBorderBrushChecked"] = _penHighlightBrush;
            PenModeButton.Resources["ToggleButtonBorderBrushCheckedPointerOver"] = _penHighlightBrush;
            PenModeButton.Resources["ToggleButtonBorderBrushCheckedPressed"] = _penHighlightBrush;
            PenModeButton.Resources["ToggleButtonForegroundChecked"] = _penForegroundBrush;
            PenModeButton.Resources["ToggleButtonForegroundCheckedPointerOver"] = _penForegroundBrush;
            PenModeButton.Resources["ToggleButtonForegroundCheckedPressed"] = _penForegroundBrush;
            ApplyPenHighlight();
        }

        // Thickness preset buttons: no white-ring affordance on hover/press (the gray
        // highlight on the inner ThicknessXHighlight Border is the only selected-state
        // indicator). We only need to keep the system from overlaying a near-black
        // PointerOver/Pressed background on top of the transparent button / inner highlight,
        // which is already handled by HighlightThicknessPreset.

        // 首次按当前主题定色（ActualTheme 此时已确定），再设置默认选中高亮。
        ApplyPenOptionThemeBrushes();

        // Mark the default thickness preset (3.5 px) as selected.
        SelectThicknessPreset(3.5);
        SyncColorSwatchHighlight();

        // Dismiss the pen-options capsule on any outside pointer press, without eating
        // the click (so drawing on the canvas still works). handledEventsToo lets it fire
        // even when the canvas marks the pointer as handled.
        this.AddHandler(UIElement.PointerPressedEvent,
                        new PointerEventHandler(Root_PointerPressed), true);

        // Case 1: File passed via AppInstance file activation (packaged mode)
        if (_pendingFileToLoad != null)
        {
            // When opening via file association (double-click from explorer),
            // hide welcome screen immediately before loading PDF
            // so user never sees the welcome page
            WelcomePanel.Visibility = Visibility.Collapsed;
            SidebarSplitView.Visibility = Visibility.Visible;
            
            UpdateWindowTitle(_pendingFileToLoad.Name);
            try
            {
                // 激活参数里的 StorageFile 由 OS 直接给出，取其 Path 不额外付费。
                await LoadPdfAsync(_pendingFileToLoad.Path);
            }
            catch (Exception)
            {
                // If loading fails, show welcome screen again. Reset the toolbar
                // title too, since it was set to the file name before loading.
                UpdateWindowTitle(null);
                WelcomePanel.Visibility = Visibility.Visible;
                SidebarSplitView.Visibility = Visibility.Collapsed;
            }
            _pendingFileToLoad = null;
        }
        // Case 2: File passed via command-line arguments (unpackaged mode fallback)
        else if (App.PendingFilePath != null)
        {
            WelcomePanel.Visibility = Visibility.Collapsed;
            SidebarSplitView.Visibility = Visibility.Visible;
            
            try
            {
                string filePath = App.PendingFilePath;
                App.PendingFilePath = null; // Clear immediately to prevent re-use
                
                // 直接按路径打开：不再经 StorageFile.GetFileFromPathAsync。本地文件实测该调用
                // 耗 49ms（进程相对 729.0→778.0ms），而引擎最终只消费路径字符串（见下方
                // _currentPdfPath），属纯浪费。Sumatra 全链路同样只传路径：
                // CreateEngineFromFile(path, pwdUI, chmInFixedUI)（SumatraPDF.cpp:2047 / 4232）。
                UpdateWindowTitle(Path.GetFileName(filePath));
                await LoadPdfAsync(filePath);
            }
            catch (Exception)
            {
                // If loading fails, show welcome screen again. Reset the toolbar
                // title too, since it was set to the file name before loading.
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
            // Set all UI element strings based on system language (detected in LocalizationService)

            // Settings Panel
            if (SettingsTitleText != null) SettingsTitleText.Text = Glance.Services.LocalizationService.Get("Settings");
            if (AppThemeLabel != null) AppThemeLabel.Text = Glance.Services.LocalizationService.Get("AppTheme");

            if (ThemeLightItem != null) ThemeLightItem.Content = Glance.Services.LocalizationService.Get("ThemeLight");
            if (ThemeDarkItem != null) ThemeDarkItem.Content = Glance.Services.LocalizationService.Get("ThemeDark");
            if (ThemeDefaultItem != null) ThemeDefaultItem.Content = Glance.Services.LocalizationService.Get("ThemeSystem");

            if (AboutTitleText != null) AboutTitleText.Text = Glance.Services.LocalizationService.Get("AboutTitle");
            if (DeveloperText != null) DeveloperText.Text = Glance.Services.LocalizationService.Get("Developer");

            if (WebsiteLink != null) WebsiteLink.Content = Glance.Services.LocalizationService.Get("WebsiteLink");
            if (GitHubLink != null) GitHubLink.Content = Glance.Services.LocalizationService.Get("GitHubLink");

            // Welcome Screen
            if (WelcomeSubtitleText != null) WelcomeSubtitleText.Text = Glance.Services.LocalizationService.Get("WelcomeSubtitle");
            if (WelcomeOpenButtonText != null) WelcomeOpenButtonText.Text = Glance.Services.LocalizationService.Get("WelcomeOpenButton");

            // Sidebar（添加书签按钮无文字，用本地化串补可访问名称）
            if (AddBookmarkButton != null)
                AutomationProperties.SetName(AddBookmarkButton, Glance.Services.LocalizationService.Get("AddBookmark"));
            ApplySidebarMode(); // refresh header text after localization strings are ready
        }
        catch { }
    }

    private void InitializeSettings()
    {
        try
        {
            // Load theme from file-based settings (portable: stored alongside the app)
            string themeStr = _settingsService.AppTheme;

            // Set ComboBox index
            int selectedIndex = 2; // Default (System)
            if (themeStr == "Light") selectedIndex = 0;
            else if (themeStr == "Dark") selectedIndex = 1;

            if (ThemeComboBox != null)
            {
                ThemeComboBox.SelectedIndex = selectedIndex;
            }

            // Apply theme
            if (Enum.TryParse(themeStr, out ElementTheme theme))
            {
                var mainWindow = (Application.Current as App)?.MainWindow;
                if (mainWindow?.Content is FrameworkElement rootElement)
                {
                    rootElement.RequestedTheme = theme;
                }

                // Update title bar and window background colors to match the theme
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
                }

                // Update title bar and window background colors to match the theme
                if (mainWindow is MainWindow glanceWindow)
                {
                    bool isDark = (theme == ElementTheme.Dark) ||
                                  (theme == ElementTheme.Default &&
                                   Application.Current.RequestedTheme == ApplicationTheme.Dark);
                    glanceWindow.UpdateTitleBarTheme(isDark);
                    glanceWindow.UpdateWindowBackground(theme);
                }

                // Save theme to file-based settings (portable: stored alongside the app)
                _settingsService.AppTheme = themeStr;
            }
        }
    }

    private async void OpenPdf_Click(object sender, RoutedEventArgs e)
    {
        await OpenPdfAsync();
    }

    /// <summary>打开 PDF 文件选择器并加载。Ctrl+O 与欢迎页「打开」按钮共用此逻辑。</summary>
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
                // Show error dialog
                var errorDialog = new ContentDialog
                {
                    Title = Glance.Services.LocalizationService.Get("Error"),
                    Content = string.Format(Glance.Services.LocalizationService.Get("PdfLoadErrorContent"), ex.Message),
                    CloseButtonText = Glance.Services.LocalizationService.Get("Ok"),
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = DialogTheme()
                };
                await errorDialog.ShowAsync();
                // If loading fails, show welcome screen again. Reset the toolbar
                // title too, since it was set to the file name before loading.
                UpdateWindowTitle(null);
                WelcomePanel.Visibility = Visibility.Visible;
                SidebarSplitView.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Ctrl+O：打开文件（与欢迎页「打开」按钮等效，不限定欢迎页）。</summary>
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
        // (or gets disposed by) the live viewer engine.
        PdfRenderService? printRender = null;
        try
        {
            printRender = new PdfRenderService();
            await printRender.InitializeAsync(_currentPdfPath, _currentPdfPassword);

            string docTitle = System.IO.Path.GetFileNameWithoutExtension(_currentPdfPath);
            int currentPage = PageListView.SelectedIndex;
            if (currentPage < 0) currentPage = 0;

            var printService = new Glance.Services.PdfPrintService(hwnd, printRender, _pages, docTitle, currentPage);

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

    /// <summary>
    /// WinUI 3 的 ContentDialog 打开时按 Application.RequestedTheme（跟随系统）解析主题资源，
    /// 不会自动跟随窗口根元素上设置的 RequestedTheme（已知问题，见 WinUI-Gallery#126 /
    /// microsoft-ui-xaml#6281）。因此每个对话框都必须显式携带窗口根元素解析后的 ActualTheme，
    /// 否则应用内选深色/浅色而系统主题相反时，对话框外观与主界面不一致。
    /// </summary>
    private static ElementTheme DialogTheme()
    {
        var root = (Application.Current as App)?.MainWindow?.Content as FrameworkElement;
        return root?.ActualTheme ?? ElementTheme.Default;
    }

    private async Task ShowPrintErrorDialog(string titleKey, string contentKey, string? arg = null)
    {
        var dialog = new ContentDialog
        {
            Title = Glance.Services.LocalizationService.Get(titleKey),
            Content = arg == null
                ? Glance.Services.LocalizationService.Get(contentKey)
                : string.Format(Glance.Services.LocalizationService.Get(contentKey), arg),
            CloseButtonText = Glance.Services.LocalizationService.Get("Ok"),
            XamlRoot = this.XamlRoot,
            RequestedTheme = DialogTheme()
        };
        await dialog.ShowAsync();
    }

    /// <summary>保存当前 PDF 的阅读位置、缩放与窗口几何到本地（按路径区分），供重开时精确还原。</summary>
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
            // 侧边栏开关状态随文件记忆：与偏移/缩放同一份记录、同一时机（退出前/切换文档前）落盘。
            SidebarOpen = SidebarSplitView.IsPaneOpen,
            SavedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        // 窗口几何由 MainWindow 填写（只有它持有 AppWindow）：普通状态取 AppWindow.Position/Size，
        // 最大化取 GetWindowPlacement 的 rcNormalPosition（SumatraPDF.cpp:775-841）。
        if ((Application.Current as App)?.MainWindow is MainWindow mainWindow)
            mainWindow.CaptureWindowGeometry(data, _currentPdfPath);
        ViewStateStore.Save(_currentPdfPath, data);
    }

    public void CleanupForExit()
    {
        // 退出前先保存阅读位置/缩放（此时 GpuViewer 仍存活，可安全读取视图状态）。
        SaveCurrentViewState();

        _isUnloading = true;

        // Cancel all background rendering tasks
        if (_pdfRenderCts != null)
        {
            try { _pdfRenderCts.Cancel(); } catch { }
        }

        if (_pdfRenderRightCts != null)
        {
            try { _pdfRenderRightCts.Cancel(); } catch { }
        }

        // Release native resources without blocking the UI thread.
        // Do NOT use _ffiSemaphore.Wait() here — it can deadlock if a
        // fire-and-forget SaveAnnotationsAsync holds the semaphore and
        // its continuation needs the UI thread (SynchronizationContext).
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

        // 保存直接以当前文档(_currentPdfPath)为源，把真实 /Ink 注解对象写回文件（无副本、无烘焙）。

        // Cancel page rendering FIRST to release the semaphore and prevent deadlocks!
        if (_pdfRenderCts != null)
        {
            try { _pdfRenderCts.Cancel(); } catch { }
        }

        await _ffiSemaphore.WaitAsync();
        try
        {
            // 1. Gather all current annotations
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

            // 临时文件放到便携版缓存目录（glance.exe 同级 Cache），不写系统 Temp（C 盘）
            string tempFile = CacheManager.GetTempFilePath("pdf");
            
            try
            {
                if (SaveButton != null) SaveButton.IsEnabled = false;

                // Cancel background rendering task to avoid accessing native engine during/after cleanup
                await CancelBackgroundRenderAsync();

                // 保存不改变阅读位置：仅替换底层引擎句柄（见下方 ReattachRenderService），
                // 不再重建查看器、不再快照/还原视口，滚动偏移与缩放原封不动。

                // 1. Clear resources to unlock original file BEFORE exporting (so PdfSharp can access it)
                if (_pdfRenderService != null)
                {
                    _pdfRenderService.Dispose();
                    _pdfRenderService = null;
                }

                // 2. Export annotations to the temporary file (直接以当前文档为源写真实注解对象)
                var exportService = new Glance.Services.PdfExportService();
                await exportService.ExportPdfWithAnnotationsAsync(_currentPdfPath, tempFile, savedList, pageRotations, _currentPdfPassword);

                // 3. Overwrite original file
                // 目标可能带只读属性（受保护原文件的派生副本偶尔会继承只读位）→ 先清除，
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
                // Keep the annotations in memory so they remain editable and clickable.
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

                // Re-initialize Rust PDF engine with the updated file (no second UWP document load)
                _pdfRenderService = new PdfRenderService();
                await _pdfRenderService.InitializeAsync(_currentPdfPath, _currentPdfPassword);

                // GPU 阅读器仅"换引擎句柄 + 清旧位图缓存"重新挂接更新后的文件，
                // 刻意【不】重建布局、不触碰视口：滚动偏移/缩放/_didInitialFit/实时镜像全部保留，
                // 满足"保存不改变阅读位置与缩放"的硬要求。页面尺寸与旋转在保存前后一致
                // （旋转已烘焙进文件、此处同步归零），故 Spacer 布局不变、ScrollViewer 偏移原封不动。
                GpuViewer.ReattachRenderService(_pdfRenderService);

                GpuViewer.InvalidateOverlayThrottled();

                if (_pdfRenderCts != null)
                {
                    try { _pdfRenderCts.Cancel(); } catch { }
                    _pdfRenderCts.Dispose();
                }
                _pdfRenderCts = new CancellationTokenSource();

                // 缩略图清空后由侧栏虚拟化回调按需补齐
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
                    // 诊断日志放到便携版缓存目录（不写 AppData / C 盘），安全可删、启动自清
                    string logPath = CacheManager.GetCacheFilePath("save_error.txt");
                    File.WriteAllText(logPath, $"Time: {DateTime.Now}\nError: {ex.Message}\nStack: {ex.StackTrace}\nInner: {ex.InnerException?.Message}");
                }
                catch { }

                // 保存失败时恢复渲染引擎，使查看器仍可正常使用。
                // ⚠ 关键：必须用【非破坏式】ReattachRenderService，绝不能 LoadAsync ——
                // 否则会重建查看器并跑 TryInitialFit，把视口重置回首页 + 适应宽度
                // （只读文件覆盖失败即走此分支，表现为"保存触发另存为时视口跳回首页"）。
                // 失败意味着文件内容未变，故只换引擎句柄 + 清旧位图缓存即可，
                // 滚动偏移/缩放/_didInitialFit/实时镜像/旋转态(_rotation 与 _pages 一致)全部保留。
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
        // 路由依据"当前文档的原始文件是否可被覆盖"（_canOverwriteOriginal），而非是否带密码：
        // - 原始加密/只读 PDF → _canOverwriteOriginal=false → 走另存为（Save Copy），绝不改写受保护原文件；
        // - 加密 PDF 另存为得到的副本 B（可写）→ _canOverwriteOriginal=true → 走原地保存，
        //   从原始加密文件（带密码）烘焙并覆盖 B，全程不落解密明文副本。
        if (!_canOverwriteOriginal)
        {
            return await ExportPdfAsync(notify);
        }

        bool success = await SaveOriginalPdfSilentAsync();
        if (!success)
        {
            // 直接覆盖失败（目标被 OS 限制写入 / 只读 / 锁定等，典型如 Windows 受控文件夹访问
            // Controlled Folder Access 拦截应用对桌面/文档等受保护目录的程序化 File.Copy，
            // 但放行"系统文件选择器"发起的写）。弹出"无法覆盖"提示，由用户选择是否另存为副本；
            // 不再自动改走另存为，避免在副本 B 上点保存却直接生成副本 C。
            SuppressPenRing();
            try
            {
                var dialog = new ContentDialog
                {
                    Title = Glance.Services.LocalizationService.Get("OverwriteFailedTitle"),
                    Content = Glance.Services.LocalizationService.Get("OverwriteFailedContent"),
                    PrimaryButtonText = Glance.Services.LocalizationService.Get("SaveCopy"),
                    CloseButtonText = Glance.Services.LocalizationService.Get("Cancel"),
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = DialogTheme()
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    return await ExportPdfAsync(notify);
                }
                if (notify) ShowSaveToast(Glance.Services.LocalizationService.Get("SaveToastFailed"), ToastIconKind.Error);
                return false;
            }
            finally
            {
                ResumePenRing();
            }
        }

        // 保存成功。关闭窗口路径(notify=false)不提示。
        if (notify) ShowSaveToast(Glance.Services.LocalizationService.Get("SaveToastSuccess"), ToastIconKind.Success);
        return true;
    }

    private async Task<bool> ExportPdfAsync(bool notify = true)
    {
        if (string.IsNullOrEmpty(_currentPdfPath) || _pages.Count == 0) return false;
        // 另存为流程全程（含系统 FileSavePicker 文件选择框、导出成功/失败提示）整个窗口恢复原生鼠标，
        // 与关闭窗口"保存更改"对话框、Ctrl+S 只读/加密触发另存为的行为一致。
        SuppressPenRing();
        try
        {
            // 保存触发前先快照视口：FileSavePicker 会让主窗口失焦/挂起，可能使 ScrollViewer
        // 复位到首页。整个导出流程不重建查看器，结束后用 RestoreView 把视口拉回原处。
        GpuViewer.GetCurrentViewState(out double svZoom, out double svH, out double svV, out int svPage);
        var savedView = new ViewStateData
        {
            ZoomPercent = svZoom,
            HorizontalOffset = svH,
            VerticalOffset = svV,
            PageIndex = svPage,
            RotationDegrees = 0,
            LayoutMode = (int)GpuViewer.LayoutMode,
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

        // 1. Gather all current annotations
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
            //    占用，使 PdfSharp 能读取该文件（另存为的源要么是加密/只读原文件、要么是普通文件，
            //    均不可被引擎同时占用）。
            _pdfRenderService?.Dispose();
            _pdfRenderService = null;

            var exportService = new Glance.Services.PdfExportService();
            await exportService.ExportPdfWithAnnotationsAsync(_currentPdfPath, file.Path, savedList, pageRotations, _currentPdfPassword);

            HasUnsavedChanges = false;

            // 让"另存为"接管为活动文档：引擎切到导出副本（页面内容一致、旋转已写进文件，
            // ReattachRenderService 保留视口、零打断）。待引擎成功挂接后再切换逻辑路径。
            try
            {
                foreach (var page in _pages) page.RotationAngle = 0.0;

                _pdfRenderService = new PdfRenderService();
                await _pdfRenderService.InitializeAsync(file.Path, null);
                GpuViewer.ReattachRenderService(_pdfRenderService);
                GpuViewer.InvalidateOverlayThrottled();

                _currentPdfPath = file.Path;          // 逻辑文档切到导出副本
                _canOverwriteOriginal = IsFileOverwritable(_currentPdfPath);
                _currentPdfPassword = null;           // 导出副本不加密、可覆盖自身
                UpdateWindowTitle(file.Name);
            }
            catch
            {
                // 导出本身已成功；若接管新文件失败（引擎重挂/初始化异常），恢复引擎到原文件
                // （_currentPdfPath 尚未切换），保留导出前状态（副本已生成、批注仍可见）。
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
            //    关闭窗口路径(notify=false)不提示（窗口即将关闭）。
            if (notify) ShowSaveToast(Glance.Services.LocalizationService.Get("SaveAsToastSuccess"), ToastIconKind.Success);
            // 原模态对话框会引发焦点态变化导致视口漂移，曾需在对话框后补保视口；现已无对话框，
            // 但保留这两次 RestoreView 作为防御（无害），确保另存为后视口纹丝不动。
            try { GpuViewer.RestoreView(savedView); } catch { }
            try { GpuViewer.DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => GpuViewer.RestoreView(savedView)); } catch { }
            return true;
        }
        catch (Exception ex)
        {
            // 失败提示：改用右下角小弹窗（自动消失）。详细错误写入日志便于排查（小弹窗不展示长文本）。
            // 关闭窗口路径(notify=false)不提示。
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
            if (notify) ShowSaveToast(Glance.Services.LocalizationService.Get("SaveAsToastFailed"), ToastIconKind.Error);
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
                // re-rendering when the item comes back into view.
                // The thumbnail is cached in the ViewModel and will be reused.
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

    /// <summary>
    /// 把 Rust 直出的 BGRA 像素直接写入 WriteableBitmap（零转换贴图）。
    /// MuPDF 原生输出即 BGRA、WriteableBitmap 像素格式即 BGRA8，
    /// 整条管线不再有 PNG 编码/解码，也没有任何通道交换——这是渲染提速的最大杠杆。
    /// 必须在 UI 线程调用（WriteableBitmap 要求）。
    /// </summary>
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
        if (pageVm.ThumbnailSource != null) return; // Already rendered!

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
            // 交给 RefillThumbnailQueue（任务完成补位 / 侧边栏滚动 / 主视图翻页）按"离视口中心近"补。
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
            // 渲染像素直接减少 56%。MuPDF 渲染小图的主要开销在解码整页图像流（无法跳过），
            // 但光栅化/缩采样部分随目标尺寸线性下降，能省一点是一点。
            uint height = 64;
            uint width = (uint)(64.0 * pageRatio);
            if (width == 0) width = 48;
            
            if (cancellationToken.IsCancellationRequested || _isUnloading) return;

            // 【缩略图并发闸门】侧边栏大距离跳转后 realized 一屏 ~20 个缩略图，若各自并发渲染，
            // 会与主视图渲染 worker 无差别争抢 PdfRenderService 的 FFI 锁——主视图每页渲染被反复
            // 插队放大数倍，表现为"大跳转后主视图与缩略图双双超长时间才出现"。这里把缩略图并发
            // 限制为 1：同一时刻最多 1 个 96px 小图在抢锁（~10ms），主视图每页最多被插队一次，
            // 缩略图自身串行渲染一屏也只需 ~200ms，视觉上几乎无差别。
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
            // （滑动窗口保持 MaxThumbnailPending 个在途；Refill 内部再启动任务，递归但受上限约束）。
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

    /// <summary>
    /// 缩略图补位：在途任务少于上限时，从 realized 缺图项里按"离侧边栏视口中心近"补足。
    /// 触发点：① 任务完成/跳过（RenderThumbnailAsync finally）；② 侧边栏滚动停止（ScrollViewer ViewChanged）；
    /// ③ 主视图翻页（CurrentPageChanged Low 优先级兜底）。UI 线程调用。
    /// </summary>
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

    /// <summary>
    /// 订阅侧边栏滚动停止事件：缓存窗口内的项滚入视口【不会】重新触发 ContainerContentChanging，
    /// 其缩略图要靠这里补渲染（RefillThumbnailQueue 按新视口中心优先补位）。Loaded 后调用一次。
    /// </summary>
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

    /// <summary>
    /// 侧边栏跟随主视图：把 PageListView 内部 ScrollViewer 直接滚到"第 index 项"的偏移。
    /// 项高固定（模板 Border 48 + Padding 4,4 ≈ 56px，实测缓存），按 索引×项高 精确定位——
    /// 这是不依赖 ScrollIntoView / 选中项自动滚动的确定性路径（WinUI 对 unrealized 目标
    /// 的 ScrollIntoView 经常被吞，且大跳转时选中容器尚未就绪）。
    /// </summary>
    private void ScrollSidebarToIndex(int index)
    {
        if (index < 0) return;
        if (_sidebarScrollViewer == null) AttachSidebarScrollRefill(); // 懒获取（Loaded 后正常已就绪）
        var sv = _sidebarScrollViewer;
        if (sv == null) return;
        double itemH = GetSidebarItemHeight();
        if (itemH <= 0) return;
        try
        {
            // 目标偏移：目标项尽量靠近视口中心（跟随主视图阅读位置，比贴顶直观）
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
            // Cancel background rendering task if any is running
            await CancelBackgroundRenderAsync();
            _isUnloading = false;

            _pages.Clear();
            _annotationHistory.Clear();
            _annotationRedo.Clear();
            _currentPageIndex = 0;
            PageNumberInput.Text = "1";
            TotalPagesText.Text = "/ 0";

            // 切换文档前，先保存旧 PDF 的阅读位置/缩放（此时 GpuViewer 仍显示旧文档）。
            SaveCurrentViewState();
            _currentPdfPath = path;
            _canOverwriteOriginal = true; // 默认可覆盖；引擎初始化成功后按加密/只读重写
            // 渲染源直接指向当前文档本身，加载时绝不复制任何副本（便携性 + 无信息泄露）。
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

            // Hide welcome screen, show document panel and home button
            WelcomePanel.Visibility = Visibility.Collapsed;
            SidebarSplitView.Visibility = Visibility.Visible;
            // 侧边栏开关状态随文件记忆（与阅读位置/缩放同一份记录，按 PDF 路径存）。
            // 必须在引擎初始化【之前】摆好：其后 InitializeAsync/GetPageCountAsync 有多个 await，
            // 到 LoadAsync 还原视图时 Pane 列宽早已落定，于是还原的偏移/缩放与退出时处于同一视口宽度；
            // 若挪到 LoadAsync 之后再翻，会先按"收起"的宽度还原、再展开侧边栏触发一次多余的重锚。
            var savedVs = ViewStateStore.Load(_currentPdfPath);
            SidebarSplitView.IsPaneOpen = savedVs?.SidebarOpen == true;
            SaveButton.Visibility = Visibility.Visible;
            if (PrintButton != null) PrintButton.Visibility = Visibility.Visible;
            if (SplitViewToggleButton != null) SplitViewToggleButton.Visibility = Visibility.Visible;

            // Initialize Rust PDF engine (only rendering backend), retrying when
            // the PDF is password-protected until the user supplies the correct
            // password or cancels the prompt.
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
                        // Reset the toolbar title too, since it was already set to the
                        // file name (e.g. in the Open-file picker) before LoadPdfAsync ran.
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
            // 左视图由 GpuPageView 承载：自建页列表 + 按需 GPU 光栅，滚动/缩放由 GPU 变换。
            _totalPages = await _pdfRenderService!.GetPageCountAsync();
            TotalPagesText.Text = $"/ {_totalPages}";
            GpuViewer.Visibility = Visibility.Visible;
            // 还原上次关闭时的阅读位置与缩放：必须在 LoadAsync 时就传入 restoreView——
            // 否则 GpuPageView 内部 TryInitialFit 会先做"适应宽度"（ChangeView(0,0,fit)）把
            // _live*/渲染队列播种成首页，之后 MainPage 再调 RestoreView 才切回记忆位置，中间隔着
            // LoadAnnotationsAsync 的 await → 遮罩淡出后露出"闪一下首页"。传进去后 TryInitialFit
            // 见到 _pendingRestore!=null 直接跳过适应宽度、按还原位置 SyncLiveView+SetVisibleRangeFor
            // +QueueVisiblePages，画布首帧即从记忆位置绘制（遮罩淡出露出的就是目标页，零闪）。
            await GpuViewer.LoadAsync(_pdfRenderService, savedVs);

            // 复用 GpuViewer 已取的页尺寸填充 _pages 占位 → 缩略图侧栏
            // （PageListView 虚拟化 → EnsureThumbnailRenderedAsync，96px 极轻）。
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
            // Viewer_ViewChanged 补发，侧边栏会一直无选中项。这里按 GpuViewer 权威当前页补上
            // （还原异步落地时 _live* 已是记忆位置 → CurrentPageIndex 正确；落地后的 ViewChanged
            // 补发会再同步一次，幂等）。
            // 页码框【不】在此覆盖：CurrentPageChanged 的页码分支不依赖 _pages，LoadAsync 内已更新为
            // 还原/适应的真实页码；还原异步落地后的 ViewChanged→NotifyViewState 会再补发一次。
            int restoredIdx = GpuViewer.CurrentPageIndex;
            if (restoredIdx >= 0 && restoredIdx < _pages.Count && PageListView.SelectedIndex != restoredIdx)
            {
                PageListView.SelectedIndex = restoredIdx;
                PageListView.ScrollIntoView(_pages[restoredIdx]);
            }

            // 加载已有标注（_pages 占位已填充完毕，按 PageIndex 挂接安全）。
            await LoadAnnotationsAsync(_currentPdfPath);
            GpuViewer.InvalidateOverlay(); // 结构变化 → 整层重建

            // 加载用户书签与文档大纲。
            await LoadBookmarksAsync();
            await LoadOutlineAsync();

            // Proactively return the transient load-time allocation spike to
            // the OS a few seconds after opening, instead of waiting minutes
            // for Windows to trim the working set on its own.
            SchedulePostLoadMemoryTrim();
        }
        catch (Exception ex)
        {
                var dialog = new ContentDialog
                {
                    Title = Glance.Services.LocalizationService.Get("PdfLoadErrorTitle"),
                    Content = $"{Glance.Services.LocalizationService.Get("Error")}: {ex.Message}\n\n{Glance.Services.LocalizationService.Get("Stack")}:\n{ex.StackTrace}",
                    CloseButtonText = Glance.Services.LocalizationService.Get("Ok"),
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

    /// <summary>
    /// Prompt the user for a PDF password. Returns the entered password (which
    /// may be empty if they typed nothing), or null if they cancelled.
    /// </summary>
        private async Task<string?> PromptForPasswordAsync(string fileName, bool isRetry)
        {
            var stack = new StackPanel { Spacing = 12 };
            stack.Children.Add(new TextBlock
            {
                Text = isRetry
                    ? Glance.Services.LocalizationService.Get("PasswordMessageRetry")
                    : string.Format(Glance.Services.LocalizationService.Get("PasswordMessage"), fileName),
                TextWrapping = TextWrapping.Wrap,
            });
            var passwordBox = new PasswordBox
            {
                Width = 320,
                PlaceholderText = Glance.Services.LocalizationService.Get("PasswordPlaceholder"),
            };
            passwordBox.Loaded += (s, e) => passwordBox.Focus(FocusState.Programmatic);
            stack.Children.Add(passwordBox);

            var dialog = new ContentDialog
            {
                Title = Glance.Services.LocalizationService.Get(isRetry ? "PasswordTitleRetry" : "PasswordTitle"),
                Content = stack,
                PrimaryButtonText = Glance.Services.LocalizationService.Get("Open"),
                CloseButtonText = Glance.Services.LocalizationService.Get("Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
                RequestedTheme = DialogTheme(),
            };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? passwordBox.Password : null;
    }

    private DispatcherTimer? _postLoadTrimTimer;

    /// <summary>
    /// Opening a large scanned PDF causes a transient allocation spike
    /// (page enumeration + first renders + decode buffers). The live set
    /// drops quickly, but the .NET heap keeps the peak until a full GC
    /// happens much later. This runs a one-shot deep GC compaction a few
    /// seconds after load so the managed heap reflects the real footprint.
    /// 注意：不再调用 SetProcessWorkingSetSize(-1,-1) 强制收缩工作集——
    /// 那会把代码页与已缓存位图换出，导致用户首次滚动/拖拽时页错误风暴(卡顿)。
    /// 低内存表现由窗口最小化时的 EmptyWorkingSet 负责(隐藏时不交互)。
    /// </summary>
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
        // 翻转 IsPaneOpen 既看不见效果，又会把该值留在字段里 —— 而它在"密码取消后回到欢迎页"
        // 这类路径上会被 SaveCurrentViewState 连同视图一起写进某个文件的记录（那里 _currentPdfPath
        // 已切到新路径、GpuViewer 还留着旧文档），导致下次打开该文件时侧边栏开/关错乱。
        // 正式阅读时 SidebarSplitView 恒为 Visible，故正常开关不受此守卫影响。
        if (SidebarSplitView == null || SidebarSplitView.Visibility != Visibility.Visible) return;

        // 【翻转前】同步冻结"阅读位置不变量"：SplitView 的列宽在过渡起点(KeyTime=0)即变，翻转后读不到旧几何。
        // 这里只取快照、不改视图；随后 PaneOpening/PaneClosing（过渡起点）会排队一次重锚，
        // 由 GpuPageView 用弹簧把 zoom 推到目标值，且全程保持视口左右边线切到的内容不变。
        GpuViewer?.CaptureHostResizeAnchor();
        SidebarSplitView.IsPaneOpen = !SidebarSplitView.IsPaneOpen;
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
            SidebarMode.Bookmarks => Glance.Services.LocalizationService.Get("SidebarBookmarks"),
            SidebarMode.Outline => Glance.Services.LocalizationService.Get("SidebarOutline"),
            _ => Glance.Services.LocalizationService.Get("SidebarThumbnails"),
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

    // 书签行跳转走【容器级点击】（与 PageListView_ItemClick 同源）：
    // 命中目标是 ListViewItem 容器，整行任何位置（含空白区）都算，不依赖子元素的背景命中。
    // 早先挂在行内 Grid 的 Tapped 上，只有点到文字/按钮才冒泡到处理函数，点空白区直接穿透、
    // 而本列表又没有 IsItemClickEnabled 兜底 ⇒ "点了书签卡在原地"。
    private void BookmarkListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BookmarkViewModel bm)
        {
            if (bm.PageIndex != _currentPageIndex)
                NavigateToPage(bm.PageIndex);
        }
    }

    // ---------- Bookmarks ----------

    // 侧边栏头部金色五角星按钮：静默把当前页加为书签。
    // 静默 = 用默认「第 N 页」命名，不切侧边栏模式、不改选中项、不进命名编辑态，不打断阅读；
    // 需要改名走书签列表里的铅笔按钮。当前页已有书签则不重复添加，直接返回。
    private void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_totalPages == 0 || _currentPageIndex < 0 || _currentPageIndex >= _totalPages) return;
        if (_bookmarks.Any(b => b.PageIndex == _currentPageIndex)) return;
        InsertBookmarkSorted(_currentPageIndex,
            string.Format(Glance.Services.LocalizationService.Get("BookmarkPagePlaceholder"), _currentPageIndex + 1));
        SyncBookmarkFlags();
        _ = SaveBookmarksAsync();
        ShowSaveToast(Glance.Services.LocalizationService.Get("BookmarkAddedToast"), ToastIconKind.Bookmark);
    }

    /// <summary>Ctrl+D：收藏当前页书签（与侧边栏顶部收藏按钮等效，同样弹收藏气泡）。
    /// 有效性守卫（无文档 / 页索引越界 / 已有书签静默早退）由 AddBookmark_Click 自带。</summary>
    private void AddBookmark_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        AddBookmark_Click(this, new RoutedEventArgs());
        args.Handled = true;
    }

    // 侧栏缩略图行内那颗星：按下标 Handled，阻止 ListViewItem 进入按下态并捕获指针。
    // 这是"点击时整行悬浮提示色不消失"的关键：指针一旦被捕获，本行立刻收到 PointerExited，
    // 悬浮态被清掉，松手后才恢复 —— 表现为提示色闪烁。
    private void RowStar_PointerPressed(object sender, PointerRoutedEventArgs e) => e.Handled = true;

    // 松手成 Tap 时切换书签；同样标 Handled，避免 ListViewItem 把它当成 ItemClick 而跳页。
    private void RowStar_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement star && star.DataContext is PdfPageViewModel pageVm)
            TogglePageBookmark(pageVm);
    }

    // 行内星点击语义：在"加书签 / 移出书签"之间切换。
    // 静默语义与顶部黄星一致（默认「第 N 页」命名、不切模式、不改选中项、不进编辑态）。
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
                string.Format(Glance.Services.LocalizationService.Get("BookmarkPagePlaceholder"), idx + 1));
        }
        SyncBookmarkFlags();
        _ = SaveBookmarksAsync();
        ShowSaveToast(Glance.Services.LocalizationService.Get(msgKey), kind);
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
    //       ⇒ PageIndex 是全集上的全序，不存在并列，插入位唯一；
    //       加载路径 LoadBookmarksAsync 同样是 OrderBy(PageIndex)，两处语义必须一致，
    //       否则出现"刚打开是页码序、加了书签后尾部乱掉"。
    //       UI 显示顺序 == _bookmarks 顺序（全项目无 CollectionViewSource/SortDescriptor）。
    // 线性找插入位：书签量级几十个，且天然稳定（新元素夹到首个更大页码之前）。
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
        ShowSaveToast(Glance.Services.LocalizationService.Get("BookmarkRemovedToast"), ToastIconKind.Remove);
    }

    private void BookmarkEditBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            BookmarkConfirm_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            BookmarkCancel_Click(sender, e);
            e.Handled = true;
        }
    }

    private void BookmarkEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Commit on outside click; if the user clicked Cancel/Delete the button handler already changed state.
        if (sender is TextBox tb && tb.DataContext is BookmarkViewModel bm && bm.IsEditing)
        {
            BookmarkConfirm_Click(sender, e);
        }
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
                    ? string.Format(Glance.Services.LocalizationService.Get("BookmarkPagePlaceholder"), b.PageIndex + 1)
                    : b.DisplayName;
                _bookmarks.Add(new BookmarkViewModel { PageIndex = b.PageIndex, DisplayName = name });
            }
        }
        catch { /* best-effort load */ }
        SyncBookmarkFlags();   // 书签载入后回填缩略图行内星的两态
    }

    // ---------- Outline ----------

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
                    Title = Glance.Services.LocalizationService.Get("NoOutline"),
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
            // 输入框跳页：smooth=false，走瞬时落定（由淡出遮罩衔接跳变）。
            if (smooth)
                GpuViewer.GoToPageSmooth(index);
            else
                GpuViewer.GoToPage(index);
        }
    }

    private void PageNumberInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            // 回车提交：仅做跳页与失焦，工具栏的收起完全交给正常的伸缩逻辑
            // （鼠标仍在工具栏→保持；鼠标移出工具栏→收起），不在此强行干预。
            // 提交期间置 _pageInputCommitting，抑制 DefocusPageNumberInput「禁用/启用」引发的
            // 伪焦点还原把 _pageInputHasFocus 重新置真（同步与异步时序都覆盖，见字段注释）。
            _pageInputCommitting = true;
            try
            {
                JumpToInputPage();
            }
            finally
            {
                // 排队复位：晚于 DefocusPageNumberInput 内排队的异步伪还原，故伪还原触发时
                // _pageInputCommitting 仍为真，会被 GotFocus/GettingFocus 拦截。
                this.DispatcherQueue?.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => _pageInputCommitting = false);
            }
            // Lose focus from input box
            this.Focus(FocusState.Programmatic);
        }
    }

    private void PageNumberInput_GotFocus(object sender, RoutedEventArgs e)
    {
        // 剥夺焦点流程（DefocusPageNumberInput 的「禁用再启用」）或回车提交期，WinUI 会在
        // 重新启用时把焦点还原回输入框，触发一次伪 GotFocus；据此把工具栏保持展开是错的，直接忽略。
        if (_defocusingPageInput || _pageInputCommitting) return;
        // 焦点进入：记录此刻显示的页码（用于「点过但未输入」判定），并标记输入框持有焦点，
        // 使工具栏在输入期间不被鼠标移到画面上而收起（见 Root_PointerMoved / TopToolbar_PointerExited 等守卫）。
        _pageInputHasFocus = true;
        _pageInputValueAtFocus = PageNumberInput.Text;
        CancelToolbarHide();
    }

    private void PageNumberInput_GettingFocus(UIElement sender, GettingFocusEventArgs e)
    {
        // 在焦点真正落下前拦截「剥夺焦点流程 / 回车提交期」中重新启用导致的伪焦点还原：
        // 取消这次还原，使输入框真正失焦（_pageInputHasFocus 不被重新置真、工具栏得以正常收起）。
        // 这是 GettingFocus（落下前、可取消）预事件，配合 _pageInputCommitting 覆盖异步还原时序。
        // 改用 TryCancel()：焦点从外部重新进入等不可取消情形下返回 false 而非抛 E_INVALIDARG。
        if (_defocusingPageInput || _pageInputCommitting)
            e.TryCancel();
    }

    private void PageNumberInput_LosingFocus(object sender, RoutedEventArgs e)
    {
        _pageInputHasFocus = false;

        // 仅当用户实际改动了页码才跳转；点过但未输入 / 输入未变 / 输入非法都视为「未提交」，
        // 原地不动（不再把视图拉到该页顶端），仅把显示同步回真实当前页。
        string entered = PageNumberInput.Text.Trim();
        if (entered == _pageInputValueAtFocus.Trim())
        {
            PageNumberInput.Text = (_currentPageIndex + 1).ToString();
            RequestHideTopToolbar();
            return;
        }
        if (!int.TryParse(entered, out int pageNum))
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
            // 提交跳页后请求收起工具栏（鼠标不在工具栏触发区时即生效；键盘提交已在
            // PageNumberInput_KeyDown 里撤销 _pointerInTopToolbar，确保此处能正常收起）。
            RequestHideTopToolbar();
        }
        else
        {
            // Reset to current page
            PageNumberInput.Text = (_currentPageIndex + 1).ToString();
        }
    }

    /// <summary>
    /// 剥夺页码输入框的焦点。页码框只在「用户主动调整页码」（点击/Tab 进入）时才应持有焦点；
    /// 其余一切操作（点击 PDF 画面、滚动、缩放、翻页）都应立即把焦点从它身上移走。
    /// 否则焦点残留会导致：① 切窗口时视口被带回页面顶部；② 滚动/缩放时光标被顶到最左并覆盖输入；
    /// ③ 缩放过程中回显（可能错误的）页码。
    /// 剥夺焦点用「临时禁用再启用」而非 Control.Focus(FocusState.Unfocused)：后者会向 combase
    /// 传入非法 FocusState 参数并抛出 0x80070057(E_INVALIDARG) 致崩溃（WER 报告故障模块 combase.dll、
    /// 异常代码 80070057）。禁用控件无法持有焦点，焦点被强制交出并触发 LosingFocus（提交输入页码），
    /// 随即重新启用，用户无感。
    /// </summary>
    private bool _defocusingPageInput; // 重入守卫：剥夺焦点会同步触发 LosingFocus→JumpToInputPage→GoToPage→ViewportChanged→本方法，须阻断该回环

    // 页码输入框焦点状态：用于区分「点过但未输入」与「实际改了页码」，并驱动工具栏收起逻辑。
    // _pageInputValueAtFocus = 焦点进入时框内显示的页码文本；失焦时若文本仍与之相同，说明用户未改动，不跳转。
    private string _pageInputValueAtFocus = "1";
    private bool _pageInputHasFocus;
    // 回车提交期标记：回车提交会经 DefocusPageNumberInput 的「禁用/启用」触发一次伪焦点还原
    // （WinUI 重新启用控件时把焦点回灌，异步发生）。该伪还原的 GotFocus/GettingFocus 会试图把
    // _pageInputHasFocus 重新置真、卡死工具栏收起。_pageInputCommitting 在提交期间为真，
    // 并经 DispatcherQueue 排队复位（晚于异步伪还原），从而同步/异步两种时序下都拦得住。
    private bool _pageInputCommitting;
    private void DefocusPageNumberInput()
    {
        if (_defocusingPageInput) return; // 已在「剥夺焦点」链中（LosingFocus 回环），直接返回，避免重入
        if (PageNumberInput.FocusState != FocusState.Unfocused)
        {
            _defocusingPageInput = true;
            try
            {
                PageNumberInput.IsEnabled = false; // 强制失焦：禁用控件无法持焦，焦点交出并触发 LosingFocus
            }
            finally
            {
                PageNumberInput.IsEnabled = true;  // 立即重新启用，用户无感（同一 tick 内完成）
                _defocusingPageInput = false;
            }
            // 焦点离开后，让输入框回显真实当前页，避免停留在用户键入但未提交的残留文本
            // （例如刚点过输入框、又去缩放，缩放结束后框里还显示旧输入值而非真实页码）。
            PageNumberInput.Text = (_currentPageIndex + 1).ToString();
        }
    }

    /// <summary>
    /// 点击 PDF 画面（左/右视图）→ 立即剥夺页码框焦点，使用户后续滚动/缩放不再受其残留焦点影响。
    /// </summary>
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
            // 无需子类化 XAML Island——主视图光标由框架经指针输入设置，子类化拦截不到。

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
    // 保存/书签提示胶囊：微信式堆叠（见 SaveToastStack + ShowSaveToast）。
    // 每枚胶囊独立计时 2s（自创建起算，故最旧先到期），到时淡出；点击即消；
    // 栈深上限 SaveToastMax(=5) 触发时强制淡出最旧一枚，防止无限堆积。
    // 显隐与位移均走显式 Storyboard（不被 SPI_GETCLIENTAREAANIMATION 抑制），
    // 与缩放胶囊同一条 DWM 合成路径。09-24 的「A/B 双槽位交替」方案已整体回退，本实现不走双槽位。
    // 左下角胶囊的图标档位。原先只有 bool success（✓/✗），加书签反馈需要第三种形态
    // （与侧边栏顶部黄星同符号 E735、同色 #F2C200），bool 无法表达，故升为枚举。
    private enum ToastIconKind
    {
        Success,   // CheckMark \uE73E，绿
        Error,     // Error \uE783，红
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

        // 旧胶囊整体上移一格（Pos+1 → 更靠上）
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

        // 栈深超限：强制淡出最旧（最顶）一枚
        if (_saveToasts.Count > SaveToastMax)
        {
            DismissToast(_saveToasts[0], animate: true);
        }
    }

    /// <summary>构造一枚与旧单枚胶囊视觉完全一致的提示胶囊（含图标档位 + 主题色）。
    /// 背景/边框必须用 XamlReader 加载，使 {ThemeResource} 按元素实际主题解析：
    /// 若用 Application.Current.Resources[key] 只会解析到 App.RequestedTheme 档，
    /// 当 App 用 Default 跟随系统时取到的永远是 Light 档 → 深色模式下浅底+浅字不可见。</summary>
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

    /// <summary>移除一枚胶囊：停计时器 → 其余胶囊下移补位 → 淡出后从视觉树摘除。</summary>
    private void DismissToast(SaveToastItem item, bool animate)
    {
        item.Timer.Stop();
        if (!_saveToasts.Remove(item)) return;

        // 位于被移除者【上方】的更旧项（Pos 更大）需下移一格补位
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
    // 仅用 GetWindowRect 判断系统指针是否在本窗口矩形内（决定画布上的圆环是否绘制）。
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // 画笔模式圆环光标：用真实 HCURSOR 构造一个透明背景、只画一圈圆环的光标。
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;        // 0 = 光标（非图标）
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;   // 单色掩码（1=透明, 0=显示颜色）
        public IntPtr hbmColor;  // 32bpp 颜色位图（BGRA）
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

    // 重载系统光标方案（恢复被替换的原生箭头）；不销毁我们保留的 _hRingCursor 副本。
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Set zoom percentage and update the display (zoom buttons / value display call this).
    /// </summary>
    private void SetZoomPercent(double zoomPercent)
    {
        zoomPercent = Math.Clamp(zoomPercent, MinZoomPercent, MaxZoomPercent);
        _currentZoomPercent = zoomPercent;
        if (ZoomValueText != null)
        {
            ZoomValueText.Text = $"{zoomPercent:F0}%";
        }
    }


    /// <summary>
    /// 应用缩放百分比。缩放交给 GpuViewer（ZoomPercentChanged 回调会同步百分比显示）。
    /// </summary>
    private void ApplyZoomPercent(double zoomPercent, bool animate = true)
    {
        GpuViewer.SetZoomPercent(zoomPercent, animate);
    }


    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        // 快速缩放菜单仍展开时，本点击只用于收起菜单，不触发缩放；
        // 再次点击才会真正执行 -。
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

    /// <summary>缩小一步（与缩放胶囊 - 按钮等效）：以实时缩放(Viewer.ZoomPercent)为基准步进，
    /// 经 SetZoomPercent 内部 Math.Clamp 自动夹到 [MinZoomPercent, MaxZoomPercent]。</summary>
    private void ZoomStepOut()
    {
        if (GpuViewer == null || _totalPages <= 0) return;
        GpuViewer.ZoomStepPercent(-ZoomButtonStep);
    }

    /// <summary>放大一步（与缩放胶囊 + 按钮等效）。</summary>
    private void ZoomStepIn()
    {
        if (GpuViewer == null || _totalPages <= 0) return;
        GpuViewer.ZoomStepPercent(ZoomButtonStep);
    }

    /// <summary>Ctrl+= / Ctrl+小键盘+：放大一步（等效 + 按钮）。</summary>
    private void ZoomIn_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ZoomStepIn();
        args.Handled = true;
    }

    /// <summary>Ctrl+- / Ctrl+小键盘-：缩小一步（等效 - 按钮）。</summary>
    private void ZoomOut_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ZoomStepOut();
        args.Handled = true;
    }

    /// <summary>Ctrl+0（主键盘 / 小键盘）：文件原始大小 = 100%。超限时由 clamp 自动取边界。</summary>
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
    // 走输入管理器全局分发、与焦点无关；焦点在文本输入等原生控件上时交还原生行为。
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
    // 重开回到默认显示）。只影响叠加层绘制，批注数据/输入/保存/撤销均不受影响。
    private void ToggleAnnotations_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or PasswordBox or ButtonBase or ComboBox or ListViewBase or HyperlinkButton)
            return;

        if (GpuViewer == null || _totalPages <= 0) return;
        GpuViewer.AnnotationsVisible = !GpuViewer.AnnotationsVisible; // setter 内部已 Invalidate 立即重画
        args.Handled = true;
    }


    // ========================================================================
    // Sync Scrollbar (split view) — 同步滚动条
    // 增量同步：记录上次同步值，拖动时计算 delta，两边各自从当前位置加 delta。
    // 这样内容长的那边独立滚动后，再次拖动同步滚动条不会跳回。
    // ========================================================================

    /// <summary>
    /// 隐藏 ScrollBar 模板中三角按钮的视觉内容（箭头图标），但保持布局空间。
    /// 这样 Thumb 起始位置与两侧滚动条一致，但三角块不可见。
    /// </summary>
    private void HideScrollBarArrowButtons(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            
            if (child is FrameworkElement fe)
            {
                // 找到三角按钮，递归隐藏其内部视觉元素
                if (fe.Name == "VerticalSmallDecrease" || fe.Name == "VerticalSmallIncrease"
                    || fe.Name == "HorizontalSmallDecrease" || fe.Name == "HorizontalSmallIncrease")
                {
                    HideButtonVisualContent(fe);
                }
            }
            HideScrollBarArrowButtons(child);
        }
    }

    /// <summary>
    /// 隐藏按钮模板内部的视觉元素（Grid/Path/TextBlock 等），保持按钮本身的布局空间。
    /// </summary>
    private void HideButtonVisualContent(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe)
            {
                // 隐藏第一层内容元素（通常是 Grid 或 Path）
                fe.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>同步滚动用的统一访问接口：把"左/右活动视图"抽象成 偏移/可滚动高度/视口高度 + 设置器。
    /// GPU 模式下活动视图是 GpuPageView，旧模式下是 ScrollViewer；两者都通过这里统一驱动。</summary>
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
        // 滑块拖拽期抑制渲染（对齐 Sumatra 快拖静止行为，机制见 GpuPageView.BeginScrollBarDrag）：
        // ThumbTrack=拖拽中 → 两侧视图同步抑制；EndScroll=松手 → 立即按最终视口补渲染。
        // 注意：程序化赋值（UpdateSyncScrollBar 设 Value）不触发 Scroll 事件，不会误入抑制态。
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
            // 两边各自从当前位置加 delta，独立滚动的超出量自然保留（lockstep 像素同步）。
            double leftTarget = Math.Clamp(left.Offset + delta, 0, left.Scrollable);
            double rightTarget = Math.Clamp(right.Offset + delta, 0, right.Scrollable);
            left.SetOffset(leftTarget, true);
            right.SetOffset(rightTarget, true);
        }
        finally
        {
            // 延迟重置，让 ChangeView 引发的异步 ViewChanged 被 _isSyncScrolling 拦截
            this.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => _isSyncScrolling = false);
        }
    }

    /// <summary>
    /// 更新同步滚动条的范围和当前位置。
    /// Maximum = min(左边可滚动高度, 右边可滚动高度)，以内容短的那边为基准。
    /// Value = min(左边当前偏移, 右边当前偏移)。
    /// 仅在分屏视图且两边都加载了PDF时显示。
    /// 左/右活动视图在 GPU 模式下取 GpuPageView，旧模式下取 ScrollViewer。
    /// </summary>
    private void UpdateSyncScrollBar()
    {
        if (SyncScrollBar == null) return;

        // 仅在分屏打开且右边有PDF时显示
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


    // Annotation Mode Handlers
    private void ReadMode_Click(object sender, RoutedEventArgs e)
    {
        SetEditMode(EditMode.Navigate);
    }

    private void PenMode_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMode == EditMode.Pen)
        {
            // Already in pen mode: if the capsule is open, collapse it (stay in pen mode);
            // if it was dismissed by an outside click, clicking the pen button re-expands it.
            // The Read button is what actually exits pen mode.
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
            // that we are staying in pen mode, otherwise the "blue" highlight would vanish.
            PenModeButton.IsChecked = true;
            ReadModeButton.IsChecked = false;
        }
        else
        {
            SetEditMode(EditMode.Pen);
        }
    }

    /// <summary>
    /// Dismisses the pen-options capsule when the user presses anywhere outside it
    /// (e.g. on the PDF canvas) WITHOUT consuming the click, so drawing still works.
    /// Registered with handledEventsToo so it also fires when the canvas handles the pointer.
    /// </summary>
    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 在任意打开的浮层(颜色胶囊/缩放菜单/气泡)内部按下时，不应关闭胶囊或收起工具栏——
        // 否则点击浮层内的按钮会被 Root_PointerPressed(handledEventsToo) 误判为"外部按下"而把菜单整个关掉。
        if (FindTopmostPopupHit(GetLivePointerPoint()) != null)
            return;

        if (PenOptionsPopup == null || !PenOptionsPopup.IsOpen)
            return;

        // Ignore presses that originate inside the capsule, on the pen button, or anywhere
        // on the top toolbar (e.g. clicking the Read-mode button while the capsule is open
        // should not hide the toolbar).
        if (e.OriginalSource is DependencyObject src)
        {
            if (src == PenModeButton || IsDescendantOf(src, PenModeButton) ||
                src == PenOptionsCapsule || IsDescendantOf(src, PenOptionsCapsule) ||
                src == TopToolbar || IsDescendantOf(src, TopToolbar))
                return;
        }

        // Quietly close the capsule; let the pointer event continue to its target
        // (e.g. the canvas starts drawing). This is why we don't use IsLightDismissEnabled.
        PenOptionsPopup.IsOpen = false;
        // The capsule is gone, so also slide the floating toolbar back out of view.
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

    /// <summary>
    /// 取实时光标位置(MainPage 坐标系 DIP)。指针在浮层(独立视觉树)上时 Root_PointerMoved 收不到、
    /// _lastPointerPoint 会过期，故直接用 GetCursorPos 取屏幕坐标换算，覆盖浮层场景。
    /// 失败回退到 _lastPointerPoint。
    /// </summary>
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

    /// <summary>
    /// 对当前所有打开的浮层(Tooltip / Flyout / 胶囊 Popup)做命中测试，返回光标下的【最顶】命中元素。
    /// 浮层位于独立视觉树，主树的 FindElementsInHostCoordinates(pt, this) 搜不到，必须对每个 Popup.Child
    /// 单独测（并把 MainPage 相对坐标换算到浮层 Child 的局部坐标，因为浮层靠 Offset 定位、坐标系不同）。
    /// 浮层永远盖在主树之上，命中即权威 → 直接判 chrome(箭头)。返回 null 表示光标不在任何浮层之上。
    /// </summary>
    private DependencyObject? FindTopmostPopupHit(Point pt)
    {
        try
        {
            if (XamlRoot == null) return null;
            foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
            {
                if (popup.Child is not Microsoft.UI.Xaml.UIElement child) continue;

                // 原点用 Popup 的 Horizontal/VerticalOffset：相对 Popup 的【直接父级=页面根 Grid】，
                // 与 pt（MainPage 坐标系）同系，且 Popup 一打开即有效（首帧就准）。
                // 不再用 TransformToVisual：浮层【首次】打开、child 尚未 arrange 时它的变换仍是恒等，
                Point local = new Point(pt.X - popup.HorizontalOffset, pt.Y - popup.VerticalOffset);

                // 1) 精确命中：胶囊内部元素（按钮/分隔线/间隙）已生成时直接命中。
                foreach (var h in Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(local, child))
                    return h as DependencyObject;   // 第一个即最顶命中

                // 2) 包围盒兜底：浮层内有无背景刷子的容器（胶囊间隙/未生成模板区域）不参与命中测试，
                //    几何包围盒能可靠覆盖；并强制一次布局以拿到首帧尺寸——浮层刚打开尚未 measure 时
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



    /// <summary>
    /// 欢迎页 ↔ 文档视图 的工具栏按钮可见性。欢迎页没有文档，「画笔模式 / 旋转 / 纵横排列」
    /// 三个按钮对它毫无意义（点了只会进入没有内容可操作的状态），故一并隐藏，打开文档后恢复。
    /// 由 WelcomePanel 的 Visibility 变更回调统一驱动，所有切换路径行为一致。
    /// </summary>
    private void ApplyWelcomeChrome(bool welcomeVisible)
    {
        var vis = welcomeVisible ? Visibility.Collapsed : Visibility.Visible;
        if (PenModeButton != null) PenModeButton.Visibility = vis;
        if (RotateButton != null) RotateButton.Visibility = vis;
        if (LayoutToggleButton != null) LayoutToggleButton.Visibility = vis;

        // 回到欢迎页时若仍停留在画笔模式，按钮已隐藏、用户无法退出 → 主动切回阅读模式
        if (welcomeVisible && _currentMode != EditMode.Navigate)
            SetEditMode(EditMode.Navigate);
    }

    /// <summary>
    /// Shows the open document's name in the window title bar (e.g. "Glance — file.pdf").
    /// Replaces the old centered toolbar title, which could overlap toolbar buttons.
    /// </summary>
    private void UpdateWindowTitle(string? fileName)
    {
        string title = string.IsNullOrEmpty(fileName) ? "Glance" : $"Glance — {fileName}";
        if (Application.Current is App app && app.MainWindow is MainWindow glanceWindow)
            glanceWindow.SetWindowTitle(title);
    }

    // ---------------- 画笔模式圆环光标（窗口作用域，SetSystemCursor + 作用域判定） ----------------
    // 用真实 HCURSOR 圆环替换"系统箭头"本身（SetSystemCursor），框架每帧 SetCursor(箭头) 实际画出圆环，
    // 故主视图/边缘生效、零滞后（这正是此前 WM_SETCURSOR 子类化拦不到 Island 主视图的根因）。
    // 窗口作用域靠 UpdateRingScope：仅当指针在本窗口矩形内、本窗口为前台、且不在标题栏/已展开工具栏/已展开画笔颜色胶囊区域时
    // 才装圆环；离开窗口、Alt-Tab 切走、或移到上述任一区域立即经 SPI_SETCURSORS 恢复系统箭头——
    // 窗口外、标题栏、已展开工具栏、已展开画笔颜色胶囊均为原生光标，退出/崩溃兜底也恢复，彻底告别此前 SetSystemCursor 泄漏到全局的"卡死圆环"。
    // 颜色/粗细变化只重建一次句柄。

    // 画笔模式开启、或颜色/粗细变化时调用：重建一次圆环光标（同步，非 async）。
    private void RefreshRingCursor()
    {
        bool wasInstalled = _ringInstalled;
        if (wasInstalled) RestoreSystemArrow();   // 先卸下旧圆环，避免系统持有即将销毁的位图
        DestroyRingCursor();
        EnsureRingCursor();
        if (wasInstalled) InstallRingAsSystemCursor(); // 仍处作用域内则换上新圆环
    }

    // 进入画笔模式：构建圆环光标并启动作用域检测（进入窗口即装圆环）。
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

    // 退出画笔模式：停止检测、恢复系统箭头、释放 GDI 资源。
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

    // 恢复系统原生箭头（重载光标方案；不销毁我们保留的 _hRingCursor 副本）。
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
    // 避免圆环覆盖在对话框上。对话框关闭后调 ResumePenRing() 解除（嵌套安全：计数归零才真正解除）。
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

    // 解除一次临时抑制（配对 SuppressPenRing）。计数归零后恢复常规作用域判定，定时器下一拍会重新评估是否装环。
    public void ResumePenRing()
    {
        if (_ringSuppressCount > 0) _ringSuppressCount--;
    }

    // 指针是否位于本 Glance 窗口内（且本窗口为前台窗口）。
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
        if (!IsPointerInsideOurWindow()) return false;        // 不在本窗口/非前台 → 不装圆环
        if (IsPointerOverTitleBar()) return false;            // 标题栏区域 → 还原原生光标
        if (IsPointerOverExpandedToolbar()) return false;     // 已展开工具栏区域 → 还原原生光标
        if (IsPointerOverPenOptions()) return false;          // 已展开画笔颜色胶囊 → 还原原生光标
        if (IsPointerOverZoomPill()) return false;            // 可见的右下角缩放胶囊 → 还原原生光标
        if (IsPointerOverZoomFlyout()) return false;         // 展开的快速缩放选择菜单 → 还原原生光标
        if (IsPointerOverScrollBar()) return false;          // 纵横滚动条 → 还原原生光标
        if (IsPointerOverCompareView()) return false;        // 右侧对比视图（不可涂画）→ 还原原生光标
        if (IsPointerOverThumbnailSidebar()) return false;   // 已展开左侧缩略图侧边栏 → 还原原生光标
        if (IsPointerOverSettingsFlyout()) return false;     // 已展开右上角设置菜单（含主题下拉框）→ 还原原生光标
        return true;
    }

    // 指针当前是否落在自定义标题栏矩形内（标题栏区域使用原生光标）。
    private bool IsPointerOverTitleBar()
    {
        RECT? tb = GetTitleBarScreenRect();
        if (tb == null) return false;
        if (!GetCursorPos(out POINT p)) return false;
        RECT r = tb.Value;
        return p.X >= r.Left && p.X <= r.Right && p.Y >= r.Top && p.Y <= r.Bottom;
    }

    // 计算自定义标题栏的屏幕矩形（物理像素），用于把圆环光标排除在标题栏之外。
    // 标题栏是 TitleBar 控件，其 TransformToVisual(null) 坐标为【有效像素】，需乘以 XamlRoot.RasterizationScale
    // 再叠加客户区原点(ClientToScreen)的【物理像素】屏幕坐标，得到正确的屏幕矩形。
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

    // 指针当前是否落在【已展开】的顶层工具栏矩形内（工具栏展开时使用原生光标，方便点击按钮）。
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
    // 这个决定"悬停标志该是什么值"。二者同源，故不存在"工具栏被判定为不在指针下、光标却仍排除在外"
    // 这类自相矛盾的中间态。
    //
    // 这是唯一不会被 WinUI 吞掉的命中判据：一旦有 Popup（设置菜单/画笔胶囊/快速缩放菜单）打开，
    // 输入上下文切换会吞掉工具栏自身的 PointerEntered/Exited，而 PointerMoved 虽仍到达页面根，
    // 其 OriginalSource 却会停在浮层之前的最后命中元素上（上游 FindTopmostPopupHit 亦为此专门绕开
    // 主树命中测试）——即"工具栏/胶囊"这两个来源在浮层打开期间都不可靠，只有几何命中可靠。
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
    // 工具栏是 Border(TopToolbar)，其 TransformToVisual(null) 坐标为【有效像素】，需乘以 XamlRoot.RasterizationScale
    // 再叠加客户区原点(ClientToScreen)的【物理像素】屏幕坐标。该变换含 RenderTransform(下滑动画)，
    // 故动画过程中矩形会随工具栏实时位置变化（滑入时仅排除已露出部分，滑出时已 IsTopToolbarShown=false 不再排除）。
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

    // 指针当前是否落在【已展开】的画笔颜色胶囊(Popup)矩形内（展开时使用原生光标，方便点选颜色）。
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
    // 胶囊内容 Border(PenOptionsCapsule) 的 TransformToVisual(null) 坐标为【有效像素】，乘以 XamlRoot.RasterizationScale
    // 再叠加客户区原点(ClientToScreen)的【物理像素】屏幕坐标。
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
    // 仅在胶囊可见(Opacity>0)时才排除——胶囊隐藏时该处实为 PDF 画布，应保持圆环光标。
    private bool IsPointerOverZoomPill()
    {
        if (!IsZoomPillVisible()) return false;               // 仅胶囊可见才排除
        return IsPointOverZoomPill();                         // 复用已有的几何命中判定（窗口客户区 DIP 坐标系）
    }

    // 缩放胶囊当前是否处于可见状态（淡入/淡出动画过程中也能正确反映目标可见性）。
    private bool IsZoomPillVisible()
    {
        try { return BottomZoomPillBorder != null && BottomZoomPillBorder.Opacity > 0.05; }
        catch { return false; }
    }

    // 指针当前是否落在【已展开】的快速缩放选择菜单(ZoomPresetFlyout)矩形内（展开时使用原生光标，方便选档）。
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
    // 菜单内容 StackPanel(ZoomPresetPanel) 在 Flyout 的 Popup 视觉树内，其 TransformToVisual(null) 坐标为【有效像素】，
    // 需乘以 XamlRoot.RasterizationScale 再叠加客户区原点(ClientToScreen)的【物理像素】屏幕坐标
    // （与画笔颜色胶囊 Popup 的取矩形方式一致）。
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
    // 标志由 Root_PointerMoved 按 OriginalSource 命中元素权威维护（命中元素在任一 ScrollBar 或其模板部件内即视为滚动条）。
    private bool IsPointerOverScrollBar() => _pointerOverScrollBar;

    // 指针当前是否落在【右侧分屏/对比视图】区域（该区不可涂画，恢复原生光标）。
    // 门槛是「右侧分屏容器(SecondaryViewerContainer)可见」——无论对比 PDF 是否已加载：
    //   开启分屏后该容器即 Visible 并占右半列宽，即使 GpuViewerRight 尚未 LoadAsync(仍 Collapsed)，
    //   右半空白区也已不可涂画，故同样应恢复原生鼠标。
    // 容器是空 Grid(无背景)，空白区不会命中测试，不能靠 IsDescendantOf；改用屏幕矩形 + 光标坐标判定最稳。
    // 关闭分屏(容器 Collapsed)后右半列宽归零、回归主画布，圆环自动恢复。
    private bool IsPointerOverCompareView()
    {
        if (SecondaryViewerContainer == null || SecondaryViewerContainer.Visibility != Visibility.Visible) return false;
        RECT? r = GetSecondaryViewerScreenRect();
        if (r == null) return false;
        if (!GetCursorPos(out POINT p)) return false;
        return p.X >= r.Value.Left && p.X < r.Value.Right && p.Y >= r.Value.Top && p.Y < r.Value.Bottom;
    }

    // 计算右侧分屏容器(SecondaryViewerContainer)的屏幕矩形：控件 TransformToVisual(null)(有效像素)
    // × RasterizationScale 再叠加客户区原点(ClientToScreen)的【物理像素】屏幕坐标。
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
    // 门槛是「SidebarSplitView.IsPaneOpen」——Pane 展开即占左列宽；用屏幕矩形 + 光标坐标判定最稳
    // （Pane 内 Grid 有半透明背景会命中测试，但矩形法覆盖整条 Pane 区域、含空白边距，与右侧分屏做法一致）。
    // 收起(IsPaneOpen=false)后 Pane 列宽归零、回归主画布，圆环自动恢复。
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
    // × RasterizationScale 再叠加客户区原点(ClientToScreen)的【物理像素】屏幕坐标。
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
    // 门槛是「SettingsFlyout.IsOpen」；菜单面板用屏幕矩形 + 光标坐标判定（与快速缩放菜单同模式）。
    // 主题 ComboBox 展开时其下拉框是独立 Popup、可能超出菜单面板下缘 → 设置菜单打开期间，
    // 光标落在任一 open popup 矩形内也一并还原原生（此时除 PenOptionsPopup 外不可能有别家浮层）。
    private bool IsPointerOverSettingsFlyout()
    {
        if (SettingsFlyout == null || !SettingsFlyout.IsOpen) return false;
        if (!GetCursorPos(out POINT p)) return false;

        // 1) 设置菜单面板矩形。
        RECT? r = GetSettingsFlyoutScreenRect();
        if (r != null && PointInScreenRect(p, r.Value)) return true;

        // 2) 主题下拉框等独立 Popup 矩形（含设置菜单 Flyout 自身的 popup，重复覆盖无妨）。
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

    // 计算设置菜单面板(SettingsFlyoutPanel)的屏幕矩形（与 GetZoomFlyoutScreenRect 同模式）。
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
    // 与 FindTopmostPopupHit 同源，浮层首帧偏移即有效）+ child 实际尺寸（首帧未 measure 时强制布局取 DesiredSize）。
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

    // 屏幕坐标点是否落在矩形内（右/下开区间）。
    private static bool PointInScreenRect(POINT p, RECT r)
        => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    // 命中元素是否处于某个 ScrollBar 内（含其模板部件：Thumb / RepeatButton / 轨道 Rectangle）。
    // 滚动条是 ScrollViewer 模板的一部分（纵向/横向），也含 MainPage 的同步滚动条 SyncScrollBar。
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

    // 按需构建圆环光标（颜色/粗细不变则复用）。
    private void EnsureRingCursor()
    {
        try
        {
            // 圆环直接以【目标显示尺寸】渲染——光标按位图自身像素尺寸显示，系统不缩放，
            // 故位图多大、屏幕上圆环就多大（不要再造超大位图假超采样，那只会让环变大变粗）。
            // 根因修复（黑边 + 上下左右发平）：此前用 1bpp AND 掩码做二值透明，系统走"掩码模式"忽略 alpha：
            //   ① 正交方向掩码边呈像素级平台 = 发平；② 边缘半透明像素的预乘暗色被按不透明显示 = 黑边。
            // 改为【掩码全 0 + 32bpp 预乘 alpha】→ 系统走 alpha 混合：边缘解析 AA 平滑、透明区 alpha=0 真透明。
            int diameter = (int)Math.Clamp(_activeThickness * 3.0, 14, 20);
            string colorHex = _activeColorHex ?? "#F2C200";
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

    // 释放圆环光标 GDI 资源。
    private void DestroyRingCursor()
    {
        if (_hRingCursor != IntPtr.Zero)
        {
            try { DestroyIcon(_hRingCursor); } catch { }
            _hRingCursor = IntPtr.Zero;
        }
    }

    // 构造透明背景、仅一圈【纯色】圆环的 HCURSOR（无内外描边，本体即画笔色）。
    // 根因修复（黑边 + 上下左右发平）：此前用 1bpp AND 掩码做二值透明 → 系统走"掩码模式"忽略 alpha，
    //   正交方向掩码边呈像素平台（发平）+ 边缘预乘暗色被按不透明显示（黑边）。
    // 修复：AND 掩码【全 0】→ 系统按 32bpp alpha 通道平滑混合；预乘 alpha 让边缘为半透明画笔色（无暗边）。
    // 位图边长 d 即显示尺寸（系统不缩放），故 d 直接取目标屏幕直径。
    private IntPtr CreateRingCursorHandle(int diameter, byte r, byte g, byte b)
    {
        int d = Math.Max(12, diameter);             // 位图边长 = 显示尺寸
        int cx = d / 2, cy = d / 2;
        double halfW = Math.Clamp(d * 0.07, 0.9, 1.6);   // 环半厚（像素），细而清晰
        double margin = 0.6;                              // AA 余量，防边缘裁切
        double ringR = d / 2.0 - halfW - margin;          // 环中心线半径
        if (ringR < 1.0) ringR = 1.0;
        const int bpp = 4;
        int colorStride = d * bpp;
        byte[] color = new byte[colorStride * d];
        int maskRowBytes = ((d + 15) / 16) * 2;       // 1bpp，每行 16 位对齐
        byte[] mask = new byte[maskRowBytes * d];     // 全 0：全部交给 alpha 通道（不复用掩码做透明）

        for (int y = 0; y < d; y++)
        {
            for (int x = 0; x < d; x++)
            {
                double dx = x - (cx - 0.5);
                double dy = y - (cy - 0.5);
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double e = dist - ringR;                  // 到环中心线的有符号距离
                double cov = halfW + 0.5 - Math.Abs(e);   // 解析 AA 覆盖度（过渡 0.5px）
                if (cov <= 0) continue;                   // alpha=0 + 预乘 0 → 透明（掩码已全 0）
                if (cov > 1) cov = 1;

                // 预乘 alpha：系统按 alpha 混合，边缘为半透明画笔色（无暗边/黑边）。
                byte a = (byte)Math.Round(cov * 255.0);
                int cidx = y * colorStride + x * bpp;
                color[cidx]     = (byte)Math.Round((double)b * cov);  // B
                color[cidx + 1] = (byte)Math.Round((double)g * cov);  // G
                color[cidx + 2] = (byte)Math.Round((double)r * cov);  // R
                color[cidx + 3] = a;                                 // A
                // mask 全 0，无需置位
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
            fIcon = 0,            // 0 = 光标
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
            if (s.Length == 8) s = s.Substring(2); // 去掉 alpha
            if (s.Length < 6) s = s.PadRight(6, '0');
            byte r = Convert.ToByte(s.Substring(0, 2), 16);
            byte g = Convert.ToByte(s.Substring(2, 2), 16);
            byte b = Convert.ToByte(s.Substring(4, 2), 16);
            return (r, g, b);
        }
        catch { return (255, 255, 0); }
    }

    private void SetEditMode(EditMode mode)
    {
        _currentMode = mode;
        ReadModeButton.IsChecked = (mode == EditMode.Navigate);
        PenModeButton.IsChecked = (mode == EditMode.Pen);

        // Toggle annotations background to allow clicking notes and scrolling in Navigate mode
        Microsoft.UI.Xaml.Media.Brush? bg = (mode == EditMode.Navigate)
            ? null
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        
        foreach (var page in _pages)
        {
            page.AnnotationsBackground = bg;
        }

        // 标注模式下左键交给标注管线（控件内部禁用拖动平移/双击缩放，右键缩放保留）。
        if (GpuViewer != null)
            GpuViewer.AnnotationInputActive = (mode != EditMode.Navigate);

        // Toggle the floating pen-options capsule (color + thickness) for drawing mode.
        // It lives in a Popup positioned below the Pen button, so it never shifts the toolbar layout.
        bool showColors = (mode == EditMode.Pen);
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

        // 画笔模式圆环光标（窗口作用域）：进入=隐藏本窗口原生光标并自绘圆环；退出=恢复原生光标。
        _penRingActive = (mode == EditMode.Pen);
        if (_penRingActive)
            EnterPenRingMode();
        else
            ExitPenRingMode();

    }

    /// <summary>
    /// Positions the pen-options Popup so its capsule drops down just below the Pen button.
    /// WinUI 3's Popup has no Placement/PlacementTarget; HorizontalOffset/VerticalOffset
    /// are relative to the Popup's DIRECT PARENT (the page root Grid), not XamlRoot.
    /// So we transform all coordinates into the MainPage coordinate system.
    /// </summary>
    private void PositionPenOptionsPopup()
    {
        if (PenOptionsPopup == null || PenModeButton == null || TopToolbar == null)
            return;

        // Transform into the MainPage coordinate system, which matches the Popup parent.
        // (Only the X axis is used; the toolbar's slide-in animation translates Y, which
        //  must NOT affect the capsule's horizontal alignment.)
        var btnOrigin = PenModeButton.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
        PenOptionsPopup.HorizontalOffset = btnOrigin.X;

        // The toolbar's clip host is pinned to the page top (pulled up 1px so it overlaps the
        // title bar hairline), so the bar's bottom edge sits at host.Margin.Top + Height.
        // Use that directly (no TransformToVisual on Y) so the slide-in animation doesn't
        // misplace the capsule. -1 more so the capsule's 1px top border overlaps the
        // toolbar's 1px bottom border, leaving no seam.
        double hostTop = TopToolbarHost?.Margin.Top ?? 0;
        PenOptionsPopup.VerticalOffset = hostTop + TopToolbar.ActualHeight - 1;
    }

    /// <summary>
    /// 全局 UI 过渡时长（毫秒）。上工具栏滑入/滑出、底部缩放胶囊淡入淡出、对比视图
    /// "+"/"×"淡入淡出统一用这一个值——以前分别是 200/170 与 150/250，快慢不一，
    /// 观感上像是几套互不相干的控件。取其中最快的 150ms 作为统一基准。
    /// 改这里只影响工具栏（C# 构建的 Storyboard）；胶囊的动画写在 MainPage.xaml 里，
    /// 那边的 Duration="0:0:0.15" 必须一起改。
    /// </summary>

    // --- Top toolbar auto-hide state -------------------------------------------------
    // Hover tracking is flag based (not coordinate based): coordinate hit-testing inside
    // PointerExited is unreliable, which used to leave the bar stuck open whenever the
    // pointer moved onto the PDF. A short debounce timer bridges the one-frame gap while
    // the pointer travels between the bar, its pen capsule and the window title bar.
    private bool _pointerInTopToolbar;
    private bool _pointerInPenCapsule;
    private bool _pointerInTitleBar;
    // 画笔模式下所有"非画布"的 chrome 区域标志：标题栏/工具栏/笔胶囊由各自 PointerEntered 维护，
    // 缩放胶囊/次要胶囊/滚动条由 Root_PointerMoved(Viewer_PointerMoved) 按命中元素权威维护。
        private bool _pointerInBottomZoomPill;
        private bool _pointerInSecondaryPill;
        private bool _pointerOverScrollBar;

        // 光标的"几何命中"判定用的指针位置（MainPage 坐标系，由 Root_PointerMoved 持续刷新）。
        // 取代原先依赖 PointerEntered/Exited + OriginalSource 的命中标志——那些标志会被 WinUI 吞掉而
        private Point _lastPointerPoint;

    private DispatcherTimer? _toolbarHideTimer;
    private Storyboard? _toolbarAnim;

    // 工具栏铺满内容区全宽时，指针"在工具栏内"不会产生任何移动事件（点击工具栏上没有独立
    // 处理指针的元素时尤其如此）：此时工具栏的"指针在内部"过渡在上一拍已建立，故"工具栏外"
    // 的 PointerExited 不可能派发，任何悬停标志都无从失效——收起被 PointerOwnsToolbar 卡住。
    // 取严格的零容差：工具栏满宽时 || 必精确成立，任何非满宽布局（边距/居中式工具栏）都保留容差。
    private const double ToolbarWidthFitEpsilon = 0.5;

    // 最近一次指针移动的时间戳（Stopwatch 口径，与 UI 更新解耦）。
    // 悬停标志的兜底依据：鼠标已离开工具栏矩形时移动事件必然发生；若工具栏矩形已不含指针、
    // 却长时间没有 PointerMoved 事件到达，说明按钮的指针处理器把它标记了 Handled 而未冒泡到
    private static readonly System.Diagnostics.Stopwatch PointerMoveClock = System.Diagnostics.Stopwatch.StartNew();
    private long _lastPointerMoveMs = PointerMoveClock.ElapsedMilliseconds;
    private long _lastPointerOutsideMs = PointerMoveClock.ElapsedMilliseconds;

    // 由【根元素】冒泡上来的悬停校正标志：按钮里任何把 Handled 置真的指针处理器都能让 Root_PointerMoved
    // 完全断流（本页就有两例：GpuViewer 的画布移动处理、鼠标悬停 Thumb 时的范围自动滚动）。
    // 置真后，以上一次真实移动时刻为基准过 ToolbarHideDelayMs 再校正一次；任何真实移动都会复位它。
    private bool _toolbarHoverSuspect;

    // 工具栏「指针归属」：仅含工具栏本体与笔胶囊。
    // 标题栏的「保持展开」由 SetTitleBarHover(true)→CancelToolbarHide 接管（它也会取消待定收起），
    // 不再纳入此处拦截——否则其 leave 事件被吞时 _pointerInTitleBar 卡成 true，工具栏永远收不起来
    // （典型表现：回车跳页后工具栏不收回，需再点一次输入框剥夺焦点才收）。
    private bool PointerOwnsToolbar => _pointerInTopToolbar || _pointerInPenCapsule;

    /// <summary>
    /// True while the toolbar is on screen (or animating in).
    /// </summary>
    private bool IsTopToolbarShown => TopToolbar != null && TopToolbar.IsHitTestVisible;


    /// <summary>
    /// Slides the top toolbar down into view (it is a floating overlay, hidden by default).
    /// The bar moves as a rigid whole (TranslateTransform, no scaling) from -Height to 0;
    /// the host grid clips everything above its top edge, so the bar appears to slide out
    /// from behind the window title bar. Called from the window title bar pointer-enter,
    /// or when entering pen mode.
    /// </summary>
    internal void ShowTopToolbar()
    {
        if (TopToolbar == null) return;
        CancelToolbarHide();
        if (TopToolbar.IsHitTestVisible) return;

        TopToolbar.IsHitTestVisible = true;
        TopToolbar.Opacity = 1;   // no cross-fade: a clean, complete drop-down

        // Slide the whole bar down from fully tucked away to its resting position.
        // From/To are both explicit: a finished Storyboard holds its end value (HoldEnd) and
        // would otherwise mask any direct assignment to Y.
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
        // retracts, the capsule stays collapsed until the user clicks the Pen button again
        // (pen mode itself is unaffected). Re-showing the bar must not pop the capsule open.
    }

    /// <summary>
    /// Runs a toolbar slide animation, stopping any previous one first. A completed
    /// Storyboard keeps holding its final value, which would otherwise both mask direct
    /// writes to TranslateTransform.Y and fight the next animation.
    /// </summary>
    private void StartToolbarAnim(Storyboard sb)
    {
        _toolbarAnim?.Stop();
        _toolbarAnim = sb;
        sb.Begin();
    }

    /// <summary>
    /// Slides the top toolbar back up and out of view. Called when the pointer leaves the
    /// combined toolbar + pen-options capsule + title bar area, or leaves the window
    /// entirely. The toolbar stops intercepting input immediately so it never blocks the
    /// viewer behind it.
    /// </summary>
    private void HideTopToolbar()
    {
        if (TopToolbar == null) return;
        CancelToolbarHide();
        TopToolbar.IsHitTestVisible = false;
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

    /// <summary>
    /// Distance the bar has to travel to be completely tucked above the clip window.
    /// Uses a sane fallback before the first layout pass has measured the bar.
    /// </summary>
    private double TopToolbarHiddenOffset()
    {
        double h = TopToolbar?.ActualHeight ?? 0;
        if (h <= 0) h = 48;
        return h + 2;   // +2 covers the 1px overlap margin and the bottom hairline
    }

    /// <summary>
    /// Requests a hide after a short grace period. The delay lets the pointer cross the
    /// seam between the title bar, the toolbar and the pen capsule without the bar
    /// flickering closed. Any PointerEntered on those surfaces cancels it.
    /// </summary>
    private void RequestHideTopToolbar()
    {
        if (TopToolbar == null || !TopToolbar.IsHitTestVisible) return;

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
                    // 权威核对：
                    //   指针确实还在工具栏上 → 补上归属并重新等待。否则"指针在工具栏上却不收起"这条
                    //   不变量就完全押在悬停标志上，标志一丢（按钮吞掉移动事件）工具栏就在指针下收起，
                    //   用户看到的正是"鼠标还在工具栏上，工具栏却消失了"。
                    //   已离开 → 走 PointerVerifyToolbarHover 复核（满宽场景下标志可能失效）。
                    if (IsPointerOverTopToolbarRect()) _pointerInTopToolbar = true;
                    else if (!_toolbarHoverSuspect) PointerVerifyToolbarHover();
                }
                if (PointerOwnsToolbar || _pageInputHasFocus) return;
                if (IsAuxiliaryPopupOpen())
                {
                    // A menu / tooltip from the bar is still up: keep polling instead of
                    // giving up, so the bar retracts the moment that menu closes.
                    _toolbarHideTimer.Start();
                    return;
                }
                HideTopToolbar();
            };
        }
        // Deliberately do NOT restart a pending countdown: Root_PointerMoved fires on every
        // pointer move over the document, and restarting each time would postpone the hide
        // forever while the user keeps moving the mouse. Re-entering the bar cancels it.
        if (_toolbarHideTimer.IsEnabled) return;
        _toolbarHideTimer.Start();
    }

    private void CancelToolbarHide() => _toolbarHideTimer?.Stop();

    /// <summary>
    /// True when a flyout / menu opened from a toolbar button is on screen. Those live in
    /// their own popup root, so the pointer "leaves" the toolbar as soon as it moves onto
    /// the menu — without this guard the bar would slide away under the open menu.
    /// The pen-options capsule is excluded: it is owned by us and tracked with its own flag.
    /// </summary>
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

    /// <summary>
    /// 工具栏【本体内】的指针移动：重建"指针在工具栏上"的归属。
    /// 本处理器挂在 Border 本身（不是页面根，且没有 handledEventsToo）：工具栏内任何按钮一旦
    /// 把移动事件标记 Handled，冒泡就到此为止、Root_PointerMoved 永不运行 —— 这正是"悬停标志
    /// 已过期、收起却被 PointerOwnsToolbar 卡住"的成因。此处是工具栏内部唯一还能看到真实指针的
    /// 位置。排除本 Border 的直接子元素（按钮命中时子元素负责，避免把归属从"工具栏"误改成"按钮"）。
    /// </summary>
    private void TopToolbar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, TopToolbar)) return;
        _pointerInTopToolbar = true;
        _pointerInTitleBar = false;
        CancelToolbarHide();
    }

    /// <summary>
    /// 指针在工具栏的命中区外移动（工具栏的父级，即页面根）：此时指针【确实已被工具栏的
    /// 命中区排除】——否则它不会去命中工具栏的父级。但这未必等于"指针已在工具栏矩形之外"：
    /// 工具栏下方留边距处的元素把移动事件标记 Handled 并继续冒泡。故此处只做标记、不做判定：
    /// 由当次 PointerMoved 的收敛分支（Root_PointerMoved）或下一拍的权威核对来裁定；全程不放宽
    /// 任何隐藏路径，只是把"标志与实时光标不一致"这件事记下来。
    /// </summary>
    private void TopToolbarHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!IsTopToolbarShown || !PointerOwnsToolbar) return;
        if (IsPointerOverTopToolbarRect()) _toolbarHoverSuspect = true;
    }

    /// <summary>
    /// 悬停标志的兜底校正：工具栏矩形已不含实时光标、却长时间没有 PointerMoved 事件到达时，
    /// 认定标志失效并请求收起（是否真收起仍由 PointerOwnsToolbar / 输入框焦点 / 浮层判据共同裁定）。
    /// 只在工具栏满宽（铺满内容区）时由调用方放行：满宽时"指针在工具栏内"不可能产生任何移动事件，
    /// 故"无事件 + 矩形不含指针"必然是标志失效；非满宽布局（工具栏未铺满）时这个推论不成立，故整体跳过。
    /// </summary>
    private void PointerVerifyToolbarHover()
    {
        if (TopToolbar == null || TopToolbarHost == null) return;
        if (TopToolbar.ActualWidth < TopToolbarHost.ActualWidth - ToolbarWidthFitEpsilon) return;

        long now = PointerMoveClock.ElapsedMilliseconds;
        if (now - _lastPointerMoveMs < ToolbarHideDelayMs) return;   // 移动事件仍在流，标志是新鲜的

        _lastPointerMoveMs = now;
        if (IsPointerOverTopToolbarRect()) return;                   // 指针确实还在工具栏上 → 保持
        _pointerInTopToolbar = false;
        RequestHideTopToolbar();
    }

    // 工具栏的"指针在内部"归属只在这四个位置建立：①本 Border 的 PointerEntered/②PointerMoved
    // （工具栏内按钮吞掉移动事件时的兜底）、③页面基础的几何核对（悬浮列表、无事件可派发时）、
    // ④笔胶囊的 PointerEntered（胶囊与工具栏同属一个悬停区域）。
    // 全都不依赖"工具栏收到 PointerExited"——那是被 WinUI 吞得最厉害的一环。
    private void TopToolbar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerInTopToolbar = true;
        _pointerInTitleBar = false;   // the pointer is demonstrably below the title bar now
        CancelToolbarHide();
    }

    private void TopToolbar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 只降级、不收起：本事件在浮层打开/关闭时会被误派发（指针其实一直在工具栏上），
        // 而正确的 PointerEntered 可能被同一轮输入上下文切换吞掉，据此收起就会把工具栏从指针下抽走。
        // 真正的收起一律由几何核对（悬浮列表 / 无事件时的兜底核对）裁定，那两处只看实时光标位置。
        // 例如：展开的设置菜单（遮住工具栏）→ 移到画笔按钮 → 菜单收起 → 指针重新落到工具栏，
        // 钩子里重算的正是实时光标位置，故"第一次点击只收起菜单"这一预期行为不受影响。
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

    /// <summary>
    /// Called by MainWindow when the pointer enters / leaves the window title bar, which is
    /// the trigger surface for the toolbar. The title bar lives outside MainPage's visual
    /// tree, so it cannot be tracked with the local PointerEntered handlers.
    /// </summary>
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
            // 焦点仍在页码输入框时不收起工具栏（点过输入框未输入不应收起）。
            if (!_pageInputHasFocus)
                RequestHideTopToolbar();
        }
    }

    /// <summary>
    /// Catch-all safety net: whenever the pointer moves anywhere over the page content and
    /// it is not currently inside the toolbar / capsule / title bar, retract the bar. This
    /// covers the cases where WinUI silently swallows a PointerExited (fast moves, pointer
    /// capture during scrolling, focus changes), which used to leave the bar stuck open.
    /// </summary>
    /// <summary>
    /// 页面基础的指针活动"打点"（handledEventsToo）：只把最近一次真实指针输入的时刻记下来，
    /// 供"无移动事件"路径判定标志是否还有效。命中测试与归属判定一概不在这里做
    /// （Root_PointerMoved 才是唯一权威，避免两处结论互相打架）。
    /// </summary>
    private void Root_PointerActivity(object sender, PointerRoutedEventArgs e)
    {
        _lastPointerMoveMs = PointerMoveClock.ElapsedMilliseconds;
        _toolbarHoverSuspect = false;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // The pointer is inside the page content, so it is definitely NOT on the title bar.
        // The title bar is a non-client drag region and its PointerExited is not guaranteed
        // to fire, so clearing the flag here keeps it from getting stuck "true".
        _pointerInTitleBar = false;

        try { _lastPointerPoint = e.GetCurrentPoint(this).Position; } catch { /* 装饰 */ }

        // 用「指针实际命中的元素 + 实时光标位置」权威纠正所有 chrome 区域归属（标题栏/工具栏/笔胶囊/
        // 缩放胶囊/次要胶囊/滚动条），避免各自的 PointerEntered/Exited 被 WinUI 吞掉导致标志卡死/错位。
        // 滚动条归属由本函数按 OriginalSource 是否在任一 ScrollBar 内维护（见 IsInsideScrollBar / _pointerOverScrollBar）。
        var src = e.OriginalSource as DependencyObject;
        bool overToolbarRect = IsPointerOverTopToolbarRect();
        // 命中元素落在工具栏内【且】实时坐标确实在工具栏矩形内 → 才认定为"在工具栏上"。
        // 只取命中元素是不够的：设置菜单/画笔胶囊打开期间，浮层被命中且遮住工具栏时，OriginalSource
        // 仍会停在工具栏按钮上（浮层不参与主树冒泡），据此会误判成"指针还在工具栏"。
        bool overToolbar = overToolbarRect &&
            (src != null) && (ReferenceEquals(src, TopToolbar) || IsDescendantOf(src, TopToolbar));
        bool overCapsule = (src != null) && (ReferenceEquals(src, PenOptionsCapsule) || IsDescendantOf(src, PenOptionsCapsule));
        bool overZoom = (src != null) && (ReferenceEquals(src, BottomZoomPill) || IsDescendantOf(src, BottomZoomPill));
        bool overSecondary = (src != null) && ((ReferenceEquals(src, SecondaryOpenPill) || IsDescendantOf(src, SecondaryOpenPill))
                                            || (ReferenceEquals(src, SecondaryClosePill) || IsDescendantOf(src, SecondaryClosePill)));
        bool overScrollBar = IsInsideScrollBar(src);   // 命中元素在任一 ScrollBar(或其模板部件)内 → 视为滚动条区域
        // 右侧对比视图排除已改用 IsPointerOverCompareView() 的屏幕矩形判定（容器可见即整片右侧右半区恢复原生），
        // 不再依赖 Root_PointerMoved 维护标志（容器是空 Grid、空白区不命中测试，标志法会漏判未加载对比 PDF 的状态）。

        // 工具栏归属：命中工具栏 + 实时光标在工具栏矩形内 → 建立；否则按【几何】收敛（指针已不在矩形内，
        // 归属必然是过期的）。收敛不看命中元素是为了防"标志只进不出"：命中元素在浮层遮住工具栏时
        // 仍会停在工具栏按钮上，只看它就会把过期归属一直供着。
        // 归真也不指望本函数一定运行：按钮吞掉移动事件时本函数根本不跑，那时由页面基础的几何核对补上。
        _pointerInTopToolbar = overToolbar || overToolbarRect;
        _pointerInPenCapsule = overCapsule;
        _pointerInBottomZoomPill = overZoom;
        _pointerInSecondaryPill = overSecondary;
        _pointerOverScrollBar = overScrollBar;

        _lastPointerMoveMs = PointerMoveClock.ElapsedMilliseconds;
        _toolbarHoverSuspect = false;   // 有真实移动事件到达 → 下面的悬停标志是权威的，撤销待校定

        if (IsTopToolbarShown)
        {
            // 收起判据不止"指针不在工具栏上"：任何打开的浮层（设置菜单/工具提示/快速缩放菜单）都必须
            // 让工具栏先留在原地。菜单打开期间若收起工具栏，菜单关闭时指针可能已不在工具栏矩形内，
            // 工具栏就再也不展开了 —— 而"展开的设置菜单 → 点画笔按钮（第一次点击只收起菜单）→ 再点
            // 才切模式"是用户已经习惯的两步式，工具栏必须陪到最后。
            // 判据放在收起判定里、而不是仅放在定时器里：定时器 180ms 已到期时（指针在工具栏上悬停很久
            // 后打开菜单）这里若仍按"不在工具栏上"请求收起，一进一出就在同一个 tick 里把工具栏关掉。
            if (overToolbar || overCapsule || _pageInputHasFocus || IsAuxiliaryPopupOpen())
                CancelToolbarHide();
            else
                RequestHideTopToolbar();
        }
    }

    /// <summary>
    /// When the pointer leaves the whole window content area (e.g. moves to another window
    /// or outside the Glance window), hide the toolbar. WinUI does not always fire
    /// PointerExited on individual controls when the pointer exits the window, so we listen
    /// at the top-level Page level.
    /// </summary>
    private void XamlRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInTopToolbar = false;
        _pointerInPenCapsule = false;
        // Deliberately NOT clearing _pointerInTitleBar: moving up from the page into the
        // title bar also raises this event, and the title bar's PointerEntered can arrive a
        // frame later. The debounced hide lets that cancel cleanly instead of flickering.
        // 焦点仍在页码输入框时收起逻辑交给失焦统一处理，不在指针离开时收起。
        if (_pageInputHasFocus) return;
        RequestHideTopToolbar();
    }

    /// <summary>
    /// Called by MainWindow when the pointer leaves the window's whole client area (including
    /// the title bar, which MainPage cannot observe). Clears the title-bar hover and asks for
    /// a debounced hide, so that moving into the pen-options capsule — which lives in the
    /// popup layer and therefore also raises this — can still cancel it.
    /// </summary>
    internal void OnWindowPointerExited()
    {
        _pointerInTitleBar = false;
        _pointerInTopToolbar = false;
        // 焦点仍在页码输入框时不收起（见需求）；窗口失焦通常会先触发输入框失焦，由统一逻辑收起。
        if (_pageInputHasFocus) return;
        RequestHideTopToolbar();
    }

    /// <summary>
    /// Retracts the toolbar unconditionally, clearing every hover flag. Used when the window
    /// is deactivated or hidden, where no pointer events are guaranteed to arrive at all.
    /// </summary>
    internal void ForceHideTopToolbar()
    {
        _pointerInTopToolbar = false;
        _pointerInPenCapsule = false;
        _pointerInTitleBar = false;
        HideTopToolbar();
    }

    internal void OnAppDeactivated()
    {
        // 失焦（Alt-Tab 到其它程序）时立即卸下圆环、恢复系统箭头，避免其它前台窗口短暂显示圆环；
        // 画笔模式若仍激活，作用域定时器会在本窗口重新成为前台且指针在内时自动重新装上圆环。
        if (_ringInstalled)
        {
            RestoreSystemArrow();
            _ringInstalled = false;
        }
        ForceHideTopToolbar();
    }

    internal void OnAppActivated()
    {
    }

    /// <summary>
    /// Pins the toolbar's clip window to the very top of the MainPage content area. Y=0 of
    /// MainPage is already the title bar's bottom edge, and we pull it up 1px more so the
    /// bar overlaps the title bar's bottom hairline instead of leaving a 1px seam.
    /// </summary>
    private void UpdateTopToolbarMargin()
    {
        if (TopToolbarHost == null) return;
        TopToolbarHost.Margin = new Thickness(0, -1, 0, 0);
        UpdateTopToolbarClip();
    }

    /// <summary>
    /// Keeps the clip rectangle in sync with the host size. The clip is what makes the bar
    /// look like it slides out from behind the title bar: while translated up it simply
    /// falls outside the clip and is not drawn at all (instead of painting over the title
    /// bar, which the previous version did).
    /// </summary>
    private void UpdateTopToolbarClip()
    {
        if (TopToolbarHost == null) return;
        double w = TopToolbarHost.ActualWidth;
        double h = TopToolbarHost.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // 裁剪区向下预留阴影空间：工具栏向下投影（BlurRadius 20 + Offset 6 ≈ 26px），
        // 否则会被 TopToolbarHost 的矩形裁剪切掉，立体感出不来。顶部仍为 0，收起时工具栏
        // 上移仍被裁掉，不影响"从标题栏后滑出"的效果。
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
    }

    // 对比视图圆按钮的外层 Grid 内边距（与 MainPage.xaml 中 SecondaryOpen/ClosePill 的 Padding 一致）。
    // 热区比 36px 圆大一圈，算顶部位置时要把这段减掉，才是"圆的上沿"对齐。
    private const double SecondaryPillPadding = 8;
    // 工具栏底边与圆按钮之间留的呼吸空隙。
    private const double SecondaryPillGap = 8;

    /// <summary>
    /// 把对比视图左上"+"和右上"×"下移到展开后的上工具栏之下——否则工具栏滑下来就把它们盖住了。
    /// 偏移量取自实测的工具栏高度（不硬编码），字号/DPI/按钮内容变化时自动跟随。
    /// </summary>
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

    /// <summary>
    /// 侧边栏内容顶部让出展开工具栏的高度——否则同一根 Grid 里 ZIndex=50 的工具栏会盖住
    /// 侧边栏标题行与第一张缩略图的上沿（SidebarSplitView 是 ZIndex=0，恒在其下）。
    /// 与 UpdateSecondaryPillTopOffset 同一思路：偏移量取自实测的工具栏高度（不硬编码），
    /// 字号/DPI/按钮内容变化时自动跟随。
    /// </summary>
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
    }


    private string GetActiveColorHexWithOpacity()
    {
        string color = _activeColorHex;
        if (string.IsNullOrEmpty(color)) color = "#F2C200";
        
        string clean = color.Replace("#", "");
        if (clean.Length == 8)
        {
            clean = clean.Substring(2); // Strip existing alpha
        }
        
        return $"#FF{clean}";
    }

    /// <summary>
    /// Updates the Pen mode button's live highlight to match the currently selected pen color.
    /// Called whenever the color changes or when entering pen mode. The icon foreground is
    /// chosen (black/white) for legibility against the chosen color.
    /// </summary>
    private void ApplyPenHighlight()
    {
        string clean = (_activeColorHex ?? "#F2C200").Replace("#", "");
        if (clean.Length == 8) clean = clean.Substring(2); // strip alpha
        if (clean.Length != 6) clean = "F2C200";

        byte r = Convert.ToByte(clean.Substring(0, 2), 16);
        byte g = Convert.ToByte(clean.Substring(2, 2), 16);
        byte b = Convert.ToByte(clean.Substring(4, 2), 16);

        _penHighlightBrush.Color = Windows.UI.Color.FromArgb(255, r, g, b);

        // Perceived luminance (ITU-R BT.601) to pick a readable icon color.
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

    /// <summary>
    /// </summary>
    private void SyncPenCursorThickness()
    {
        try { if (GpuViewer != null) GpuViewer.PenThicknessPoints = _activeThickness; }
        catch { /* 光标只是提示，不影响绘制 */ }
    }

    /// <summary>
    /// 选中/悬浮/按下的背景完全由按钮控件的 Background 属性（DP）承载，
    /// 与左侧颜色按钮直接改 BorderBrush 同一个模式：改 DP 属性会立即重绘，
    /// 不存在"重开才生效"的问题。内部 highlight Border 永远透明，不再参与着色。
    /// </summary>
    private void HighlightThicknessPreset(Button? selected)
    {
        if (ThicknessPresetPanel == null) return;
        var uniformBg = _thicknessPresetSelectedBrush;
        foreach (var child in ThicknessPresetPanel.Children)
        {
            if (child is Button b)
            {
                // 常态：选中按钮 = 深灰，未选中 = 透明（直接赋 DP，点击立即生效）
                b.Background = (b == selected) ? uniformBg : _thicknessPresetTransparentBrush;
                // 悬浮/按下也统一用同一个深灰，杜绝状态切换配色变化；
                // 选中按钮在这些状态下颜色保持不变（无任何变换）。
                b.Resources["ButtonBackgroundPointerOver"] = uniformBg;
                b.Resources["ButtonBackgroundPressed"] = uniformBg;
            }
        }
    }

    /// <summary>
    /// 把单一颜色按钮刷新为「选中/未选中」对应的视觉：
    ///  - 选中：1.36×；未选中：原大小
    ///  - 无任何包围环：BorderBrush 恒透明，并同步覆盖 PointerOver/Pressed
    ///    模板资源为透明，防止悬浮/按下时模板用主题白环覆盖出框。
    /// </summary>
    private void RefreshColorSwatchState(Button b)
    {
        bool isSel = (b == _selectedColorButton);
        var transparent = _thicknessPresetTransparentBrush;
        b.BorderBrush = transparent;
        b.Resources["ButtonBorderBrushPointerOver"] = transparent;
        b.Resources["ButtonBorderBrushPressed"] = transparent;
        if (isSel)
        {
            b.RenderTransform = _colorSwatchScale;
            b.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        }
        else
        {
            b.RenderTransform = null;
        }
    }

    /// <summary>
    /// Highlights the selected color swatch and clears the others.
    /// </summary>
    private void HighlightColorSwatch(Button? selected)
    {
        _selectedColorButton = selected;
        if (ColorSelectorPanel == null) return;
        foreach (var child in ColorSelectorPanel.Children)
        {
            if (child is Button b) RefreshColorSwatchState(b);
        }
    }

    /// <summary>
    /// 悬浮预览：放大到 1.72×（无环）；离开后由 PointerExited 恢复为
    /// 选中（1.72×）或未选中（原大小）。
    /// </summary>
    private void ColorSwatch_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button b)
        {
            var transparent = _thicknessPresetTransparentBrush;
            b.BorderBrush = transparent;
            b.Resources["ButtonBorderBrushPointerOver"] = transparent;
            b.Resources["ButtonBorderBrushPressed"] = transparent;
            b.RenderTransform = _colorSwatchScale;
            b.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        }
    }

    private void ColorSwatch_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button b) RefreshColorSwatchState(b);
    }

    /// <summary>
    /// 给颜色按钮逐个挂悬浮/离开事件（悬浮预览用），并一次性覆盖
    /// PointerOver/Pressed 模板资源：
    ///  - Background（悬浮/按下背景）= 按钮自身色：模板不会把圆点压暗
    ///  - BorderBrush（悬浮/按下边框）= 透明：无任何包围环
    /// </summary>
    private void AttachColorSwatchHover()
    {
        if (ColorSelectorPanel == null) return;
        foreach (var child in ColorSelectorPanel.Children)
        {
            if (child is Button b)
            {
                if (b.Background is SolidColorBrush swatch)
                {
                    var swatchBrush = new SolidColorBrush(swatch.Color);
                    b.Resources["ButtonBackgroundPointerOver"] = swatchBrush;
                    b.Resources["ButtonBackgroundPressed"] = swatchBrush;
                }
                b.PointerEntered += ColorSwatch_PointerEntered;
                b.PointerExited += ColorSwatch_PointerExited;
            }
        }
    }

    /// <summary>
    /// Programmatically syncs the color-swatch highlight to match the
    /// currently stored _activeColorHex (used on load / after deserializing settings).
    /// </summary>
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

    /// <summary>
    /// Programmatically selects a thickness preset (used to mark the default on load).
    /// </summary>
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

        // 纯呈现层旋转（位图缓存复用，GPU 变换，瞬时生效）。
        GpuViewer.RotateClockwise();
    }

    private void LayoutToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfRenderService == null) return;
        // 在纵向/横向连续布局间切换；缩放、拖拽、批注等其余操作完全一致。
        var next = GpuViewer.IsHorizontalLayout
            ? PageLayoutMode.VerticalContinuous
            : PageLayoutMode.HorizontalContinuous;
        GpuViewer.LayoutMode = next;
        if (GpuViewerRight != null && GpuViewerRight.Visibility == Visibility.Visible)
            GpuViewerRight.LayoutMode = next;
    }

    /// <summary>按当前布局方向刷新切换按钮的图标朝向与提示文案。</summary>
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
            // SecondaryViewerContainer and fade in on hover (same hover-fade language as the
            // bottom-right zoom pill), so there is no comparison bar to slide in here.
            SecondaryViewerColumn.Width = new GridLength(1, GridUnitType.Star);
            SecondaryViewerContainer.Visibility = Visibility.Visible;
            ViewerDivider.Visibility = Visibility.Visible;
            // 刚开分屏时右侧还是空的："+"常驻可见，否则用户在空白区找不到入口。
            SetSecondaryOpenPillPinned(GpuViewerRight.Visibility != Visibility.Visible);
            // 兜底：容器此前一直 Collapsed，可能错过了工具栏的 SizeChanged，这里补算一次纵向偏移。
            UpdateSecondaryPillTopOffset();
            UpdateSyncScrollBar();
        }
        else
        {
            // Close split view
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
            // 对比 PDF 已卸载 → "+"回到常驻，下次开分屏就是可见的。
            SetSecondaryOpenPillPinned(true);
            if (_pdfRenderRightCts != null)
            {
                try { _pdfRenderRightCts.Cancel(); } catch { }
                _pdfRenderRightCts.Dispose();
                _pdfRenderRightCts = null;
            }
            // Right engine released; reclaim its working set promptly.
            SchedulePostLoadMemoryTrim();
        }
    }

    // --- Comparison (right pane) open/close pills ----------------------------------------
    // The old drop-down comparison bar was deleted (too hard to unify with the main toolbar).
    // Instead, two round pills live at the top corners of the comparison view and fade in on
    // hover using the exact same Storyboard language as the bottom-right zoom pill (Opacity
    // 0.15s in / 0.25s out, Opacity=0 still hit-tests).
    //
    // 例外：对比视图还没载入 PDF 时，右侧是一片空白，"+"若也隐藏起来用户就无从下手。
    // 所以此时把 "+" 钉为常驻可见（_secondaryOpenPillPinned=true）；一旦载入了对比 PDF，
    // 右侧有内容了，再恢复成"悬停才淡入"的默认行为。"×"始终是悬停显示，不受影响。

    private bool _secondaryOpenPillPinned = true;

    /// <summary>
    /// 切换 "+" 胶囊的常驻/悬停模式。pinned=true → 立即常显；false → 立即淡出为悬停触发。
    /// </summary>
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
        if (_secondaryOpenPillPinned) return;   // 常驻模式下移出也不淡出
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
        // 用实时光标位置判定：指针真的离开胶囊才淡出，避免“菜单收起胶囊也跟着消失”。
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
                if (IsPointOverZoomPill()) return;          // 指针仍在胶囊上，保持可见
                BottomPillHideAnim?.Begin();                // 指针已离开胶囊，淡出
            });
    }

    /// <summary>
    /// 快速缩放菜单展开瞬间：强制缩放胶囊同步可见（二者同生共死）。
    /// 竞态根因：胶囊正在淡出（此前 BottomZoomPill_PointerExited 或 _zoomPillHideTimer 已触发
    /// BottomPillHideAnim）时点到 ZoomValueButton，菜单展开但胶囊仍按在途动画继续隐到 0，
    /// 留下"悬空菜单"。本处理先停掉在途淡出再重新淡入，使菜单始终有胶囊陪衬。
    /// 注：胶囊显隐只动画 Opacity（MainPage.xaml:523-528），Opacity=0 时仍 IsHitTestVisible，
    /// 故不可见的 ZoomValueButton 仍可点击——这正是竞态能被触发的前提，此处用 Opening 补上不变量。
    /// </summary>
    private void ZoomPresetFlyout_Opening(object sender, object e)
    {
        BottomPillHideAnim?.Stop();   // 取消在途淡出，避免它与下面的淡入抢同一 Opacity 属性
        BottomPillShowAnim?.Begin();  // 重新淡入：菜单展开期间胶囊必可见
    }

    // 缩放胶囊隐藏兜底：Flyout/light-dismiss 覆盖层关闭瞬间的输入上下文切换、快速移动等会让
    // 胶囊的 PointerExited 丢失（微软文档明确：popup/flyout 关闭时 Entered/Exited 配对不保证），
    // 且 Flyout 打开期间的 PointerExited 被主动忽略、Closed 一次性兜底在"指针仍在胶囊上"时放弃，
    // 之后若 PointerExited 再丢失则胶囊会一直挂着。本定时器周期性核对实时光标位置：
    // 胶囊可见 && 菜单未展开 && 指针已离开胶囊 → 补一次淡出。
    // 只做"该隐藏时隐藏"，从不主动淡入（淡入仍由 PointerEntered 驱动），故不影响正常悬停行为。
    private void ZoomPillHideTimer_Tick(object? sender, object e)
    {
        try
        {
            if (BottomZoomPillBorder == null || BottomZoomPillBorder.Opacity <= 0.05) return; // 已隐藏
            if (ZoomPresetFlyout != null && ZoomPresetFlyout.IsOpen) return;                   // 菜单展开期间保持可见
            if (IsPointOverZoomPill()) return;                                                 // 指针仍在胶囊上
            BottomPillHideAnim?.Begin();                                                       // 指针已离开，兜底淡出
        }
        catch { /* 窗口关闭/对象分离期间忽略 */ }
    }

    /// <summary>实时光标是否落在右下角缩放胶囊范围内。</summary>
    /// <remarks>
    /// 光标与胶囊矩形必须落在同一坐标系（窗口客户区 DIP）：
    /// 光标 = GetCursorPos(屏幕物理像素) − ClientToScreen(hwnd)(客户区原点屏幕物理像素)，再 /RasterizationScale；
    /// 胶囊矩形 = BottomZoomPill.TransformToVisual(null)(窗口客户区 DIP)。
    /// 二者均基于窗口客户区原点，与 ExtendsContentIntoTitleBar 下 XAML 客户区坐标同帧；
    /// 不可用 TransformToVisual(this)（MainPage 帧，其原点因标题栏/顶栏比客户区低约 33 DIP，会系统性错位）。
    /// </remarks>
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
            // No InMemoryRandomAccessStream whole-file copy, no second UWP PDF engine —
            // MuPDF opens the file on demand so a large scan no longer
            // costs ~1GB in the comparison view. Retry on password-protected PDFs, just
            // like the left pane.
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
                        // User cancelled — leave the comparison pane unloaded.
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
            // 大 PDF 加载更快；MuPDF 读尺寸不解析整页内容，内存保持平稳）。
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

            // GPU 阅读器：右侧也走 Win2D 呈现层（与左侧同款），内存/性能同档。
            GpuViewerRight.Visibility = Visibility.Visible;
            await GpuViewerRight.LoadAsync(_pdfRenderServiceRight);

            // 右侧已有内容 → "+"不再常驻，恢复"悬停才淡入"，别挡着页面。
            SetSecondaryOpenPillPinned(false);

            // 第二个PDF加载完成后显示同步滚动条
            UpdateSyncScrollBar();

            // Reclaim the transient load-time working set spike promptly (same as left side).
            SchedulePostLoadMemoryTrim();
        }
        catch (Exception)
        {
            // 加载失败右侧多半是空的 → 让 "+"保持可见，用户能直接重试。
            SetSecondaryOpenPillPinned(GpuViewerRight.Visibility != Visibility.Visible);
        }
    }


    // ── GPU 路径标注处理器（坐标已由 GpuPageView 换算成页局部 PDF 点；
    //    数据/保存/撤销逻辑与旧 CPU 绘制路径一致，仅入口不同） ──────────────────

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
        // 新笔画 = 新的历史操作 → 清空重做分支（与 PS/标准编辑器一致：撤销后画新笔，重做路径作废）。
        _annotationRedo.Clear();
        _annotationHistory.Add((pageVm, _activeHighlight));
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
            // reflect the actual stroke extent (not left at 0,0,0,0).  This ensures
            // the saved JSON has meaningful geometry data and any downstream code
            // that inspects the bounding box sees correct values.
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
                    // x/y >= 0，见 native/glance-native/src/annotations/processor.rs）。画笔实际
                    // 几何完全由 Points 决定（DrawAnnotations 只画 Points，与包围盒无关），因此把
                    // 包围盒夹到非负域：既通过校验、又不改变所画即所得的笔迹本身。
                    // 拖出上/左边缘时 minX/minY 为负，若原样写入会被 Rust 整条驳回、连累整次保存。
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

    // Keyboard Accelerator for Undo (Ctrl+Z)
    private void Undo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        UndoLastAnnotation();
        args.Handled = true;
    }

    // Keyboard Accelerator for Redo (Ctrl+Y)
    private void Redo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        RedoLastAnnotation();
        args.Handled = true;
    }

    private async void Save_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        await SaveOriginalPdfAsync();
        args.Handled = true;
    }

    // Keyboard Accelerator for Print (Ctrl+P)
    private void Print_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_totalPages > 0)
            Print_Click(null, null);
        args.Handled = true;
    }

    /// <summary>
    /// 阅读区键盘导航（与焦点无关，走 KeyboardAccelerator 全局分发）。
    /// 方向键=视口滚动一格（弹簧）；空格/PageDown=下一页、PageUp=上一页（弹簧翻页）；
    /// Home/End=首页/末页（直接跳、无动画）。文本输入控件/按钮/列表等保留原生按键行为。
    /// </summary>
    private void Nav_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // 焦点在文本输入 / 按钮 / 列表等原生可交互控件上时，交还原生行为
        // （页码框光标移动、工具栏按钮空格激活、缩略图列表选择等），不抢按键。
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or PasswordBox or ButtonBase or ComboBox or ListViewBase or HyperlinkButton)
            return;

        if (GpuViewer == null || _totalPages <= 0) return;

        switch (sender.Key)
        {
            // 方向键：视口导航，按一下 = 对应方向滚轮滚一格（弹簧引擎）。
            case VirtualKey.Up:
            case VirtualKey.Down:
            case VirtualKey.Left:
            case VirtualKey.Right:
                GpuViewer.ScrollByArrow(sender.Key);
                break;

            // 空格 / PageDown：跳到下一页（弹簧动画，移动一页靠弹簧足够，不加遮罩）。
            case VirtualKey.Space:
            case VirtualKey.PageDown:
                GpuViewer.GoToPageSmooth(_currentPageIndex + 1);
                break;

            // PageUp：跳到上一页（弹簧动画，移动一页靠弹簧足够，不加遮罩）。
            case VirtualKey.PageUp:
                GpuViewer.GoToPageSmooth(_currentPageIndex - 1);
                break;

            // Home / End：跳到首页/末页，移动距离大 → 直接跳转页码。
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

    private void UndoLastAnnotation()
    {
        if (_annotationHistory.Count > 0)
        {
            // Remove the last annotation from the history stack and page collection
            var last = _annotationHistory[_annotationHistory.Count - 1];
            _annotationHistory.RemoveAt(_annotationHistory.Count - 1);

            last.Page.Annotations.Remove(last.Annotation);
            // 压入重做栈，供 Ctrl+Y 恢复（多步重做：连续撤销 N 笔后可连续重做 N 笔）。
            _annotationRedo.Add(last);
            HasUnsavedChanges = true;
            _ = SaveAnnotationsAsync();
            GpuViewer?.InvalidateOverlay(); // 结构变化 → 整层重建消除被撤销的标注
        }
    }

    /// <summary>重做一步（Ctrl+Y）：从重做栈弹回最近一次被撤销的笔画，重新加入页面叠加层。
    /// 可多步重做直到重做栈空；一旦画了新笔画，重做栈被清空（重做分支失效）。</summary>
    private void RedoLastAnnotation()
    {
        if (_annotationRedo.Count > 0)
        {
            var last = _annotationRedo[_annotationRedo.Count - 1];
            _annotationRedo.RemoveAt(_annotationRedo.Count - 1);

            last.Page.Annotations.Add(last.Annotation);
            _annotationHistory.Add(last); // 重做后重新成为可撤销的最近操作
            HasUnsavedChanges = true;
            _ = SaveAnnotationsAsync();
            GpuViewer?.InvalidateOverlay(); // 结构变化 → 整层重建恢复被重做的标注
        }
    }

    // Save and Load Annotations (Auto-save via Rust backend)
    /// <summary>
    /// 批注持久化已改为"保存时写入 PDF 真实 /Ink 注解对象"（见 SaveOriginalPdfAsync / ExportPdfAsync），
    /// 文件即唯一真相源。此处保留为空实现以兼容调用方（撤销、属性变更仅更新内存叠加层与
    /// HasUnsavedChanges，不落任何草稿文件——不再有 JSON 侧栏）。
    /// </summary>
    public Task SaveAnnotationsAsync() => Task.CompletedTask;

    /// <summary>
    /// 从 PDF 文件读回 Glance 写入的真实 /Ink 注解对象，重建叠加层模型。文件即唯一真相源：
    /// 无 JSON 侧栏、无副本。"不保存退出"不再需要回滚——未保存的笔迹从未落盘，文件始终是
    /// 上次保存态，重开即读到已保存批注。
    /// </summary>
    private async Task LoadAnnotationsAsync(string pdfPath)
    {
        SuppressAnnotationDirty = true;
        try
        {
            // 清空旧叠加层（初始加载时为空；放弃未保存更改重载时确保回到文件态）
            foreach (var p in _pages) p.Annotations.Clear();
            if (string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath)) return;

            // 后台线程读 PDF 真实注解对象（MuPDF 按需打开文件读取、
            // 不长期占用文件句柄，此处并行读安全；跨线程只返回纯数据，UI 集合在 UI 线程填充）
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

    /// <summary>
    /// 判定指定文件是否可被覆盖写入。加密文档由调用方另行处理；此处覆盖两类"不能覆盖保存"的普通情形：
    /// ① NTFS 只读属性；② 无写权限（属性未标只读但写探测失败，如受限目录）。两者任一成立即不可覆盖。
    /// </summary>
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

    /// <summary>
    /// "不保存退出"：批注只写进 PDF 文件（文件即真相源），未保存的笔迹从未落盘，文件始终是
    /// "上次保存态"。因此放弃未保存更改无需回滚任何数据——清空内存叠加层、复位脏标记即可，
    /// 下次打开自然读到已保存批注（加密/只读走另存为、普通可写走原地保存，行为一致）。
    /// </summary>
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
    private ImageSource? _thumbnailSource = null; // 侧栏缩略图位图(常驻)。旧路径的高清/预览位图字段已随 GPU 化移除。
    private Microsoft.UI.Xaml.Media.Brush? _annotationsBackground = null;
    private bool _isBookmarked;

    // 侧栏缩略图行内书签星的两态：灰边空心(E734) / 黄实心(E735)。
    // 由 MainPage.SyncBookmarkFlags() 统一按 _bookmarks 的成员关系刷新，勿在别处单独赋值。
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
    // UI 线程往 PointsCollection 加点，与 Win2D 渲染线程遍历集合存在竞争，
    // 必须加锁 + 快照，否则枚举被修改 / 数组半扩容读野指针 → AccessViolation 闪退。
    internal readonly object PointsLock = new();
    private int _pointsVersion;      // 每次修改 PointsCollection 时递增（渲染线程据此判断缓存是否过期）
    private Point[]? _pointsSnapshot;// 上次快照副本（渲染线程直接用，不怕并发修改）
    private int _snapshotVersion;    // 快照对应的版本号

    /// <summary>Points 版本号：每次修改集合后 +1，供渲染层判断几何缓存是否过期。</summary>
    internal int PointsVersion
    {
        get { lock (PointsLock) return _pointsVersion; }
    }

    /// <summary>
    /// 线程安全地获取当前 Points 的快照副本（供渲染线程使用）。
    /// 若版本号未变则复用上次快照，避免每帧 new 数组。
    /// </summary>
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

    /// <summary>
    /// 向画笔追加一个点（仅 UI 线程调用）。自动递增版本号，使渲染侧缓存失效。
    /// </summary>
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