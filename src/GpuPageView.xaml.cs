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
//
// Portions derived from Sumatra PDF (GPL-3.0), Copyright (C) the Sumatra PDF
// authors - https://www.sumatrapdfreader.org/. The rendering kernel
// (TileRenderCache) is a 1:1 port of sumatrapdf-master/src/RenderCache.{h,cpp}.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Diagnostics;
using GlanceRX.Rendering;
using GlanceRX.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.System;
using GlanceRX.Diagnostics;
using Windows.UI.Core;
using GlanceRX.Interop;
using static GlanceRX.Tuning;

namespace GlanceRX;

/// <summary>每页信息：PDF 点尺寸 + 在文档坐标系（DIP, 缩放=1）中的布局矩形。</summary>
public sealed class GpuPageInfo
{
    public int Index { get; init; }
    public double WidthPoints { get; init; }
    public double HeightPoints { get; init; }
    public Rect LayoutRect { get; set; }
}

/// <summary>页面连续布局方向。两种模式【都是连续布局】（不按页切分、不做整页翻页）， 区别只在「主滚动轴」：纵向沿 Y 串页，横向沿 X 串页。 除滚轮轴向与页间距外，缩放/拖拽/标注/旋转等交互逻辑两种模式完全一致。</summary>
public enum PageLayoutMode
{
    VerticalContinuous = 0,

    HorizontalContinuous = 1,
}

/// 纵向布局下的"列数/排版"轴，与 PageLayoutMode（横/纵主轴）相互独立。
public enum PageArrangement
{
    Single = 0,
    Facing = 1,
    Book = 2,
}

/// <summary>标注指针事件参数：视口坐标已换算为【页局部坐标，PDF 点单位，未旋转】， 与旧路径 Canvas_Pointer* 的坐标空间一致 → MainPage 的标注数据逻辑可原样复用。</summary>
public sealed class GpuAnnotationPointerArgs
{
    public int PageIndex { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public Point ViewerPosition { get; init; }
}

/// <summary>白板（Canvas 模式）交互事件参数：坐标一律为【页局部未旋转 PDF 点】，与节点存储坐标同源。 仅按需填字段（拖动/新建填 X/Y，编辑填 Text，删除/编辑仅用 Id）。</summary>
public sealed class CanvasNodeEventArgs : EventArgs
{
    public string NodeId { get; init; } = "";
    public int PageIndex { get; init; } = -1;
    public double X { get; init; }
    public double Y { get; init; }
    public double OrigX { get; init; }
    public double OrigY { get; init; }
    public string? Text { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public double OrigWidth { get; init; }
    public double OrigHeight { get; init; }
    public string FromSide { get; init; } = "";
    public string TargetNodeId { get; init; } = "";
    public string ToSide { get; init; } = "";
    public string EdgeId { get; init; } = "";
    public string? ArrowStyle { get; init; }
    public string? EdgeColor { get; init; }
    public string? NodeColor { get; init; }
    /// <summary>改线事件：为 true 时改的是连线起点(from)端，否则改终点(to)端。</summary>
    public bool RerouteFromEnd { get; init; }
}

/// GPU 阅读器（视口画布架构 —— 内存硬上限）。
/// Invalidate 远处区域无法回收显存 → 滚完全本必然爬到 文档总面积×DPI×4B（实测 ~950MB）。
public sealed partial class GpuPageView : UserControl, GlanceRX.Rendering.ITileRenderHost
{
    // 横向连续布局的页间距(DIP)：书本/杂志横向串页时相邻页应更"贴"，
    // 视觉上接近跨页装订；沿用纵向的 16 会把左右两页拉开、破坏跨页阅读的连贯感。

    public PdfRenderService? RenderService { get; private set; }

    /// <summary>只读快照：加载时整体替换，后台线程遍历安全。</summary>
    private IReadOnlyList<GpuPageInfo> _pages = Array.Empty<GpuPageInfo>();

    private readonly object _lock = new();

    // 视口状态（UI 线程写、后台读，_lock 保护）
    private double _visTop;
    private double _visBottom = 4000;
    private double _visLeft;
    private double _visRight = 4000;
    private double _dpiScale = 1.0;

    private bool _didInitialFit;

    // ================= 统一运动引擎（vsync 帧泵 + 临界阻尼弹簧） =================

    private Spring1D _scrollH, _scrollV;
    private Spring1D _zoomLog;

    // 收敛阈值 —— 判据是"屏幕上还看不看得见"，不是"数学上收敛到第几位"。
    // 临界阻尼的尾巴是 (1+ωt)·e^(-ωt)：从 99% 走到 99.99% 还要再花掉近 40% 的总时长，

    // ---------------- 帧泵 ----------------
    private bool _framePumpHooked;                 // 动画循环是否活动（Draw 自持续帧，而非 CompositionTarget.Rendering 订阅）
    private long _lastDrawTicks;                   // 上一帧 Draw 的真实时间戳(Stopwatch)，用于算模拟 dt
    private long _lastFrameTicks;
    private DispatcherTimer? _framePumpWatchdog;   // Win2D 自身停帧(窗口被遮挡/最小化)时兜底收尾

    // ---------------- 编辑态选区活动端跟踪（WinUI ITextSelection 无 ActiveEnd，需差分推断） ----------------
    private int _canvasSelLastStart = -1;   // 上一帧选区起点（-1 = 无基线）
    private int _canvasSelLastEnd = -1;     // 上一帧选区终点
    private int _canvasSelLastActive = -1;  // 上一帧推断出的活动端
    // 控件【整段设定】选区（双击选词 / 三击选段）时的活动端：这类选区不是拖出来的，差分推断必然猜错 ⇒ 直接记录。
    private int _canvasSelPinStart = -1;
    private int _canvasSelPinEnd = -1;
    private int _canvasSelPinActive = -1;
    // ---------------- 编辑态"卡内=工字"光标作用域（对齐画笔圆环：25ms 轮询 + 状态标志 + 只替换箭头槽） ----------------
    private bool _ibeamInstalled;                  // 工字作用域是否已安装（对齐画笔 _ringInstalled；变化才动资源）
    private bool _canvasCursorLocked;              // 画布手势光标是否处于锁定态（只有锁过才允许 Unlock 去还原资源）
    private bool _canvasEditControlGesture;        // 本次按下命中【编辑卡】的边框控件 ⇒ 属控件交互而非"点卡外退出编辑"
    private bool _canvasPointerOnEditCard;         // 指针是否落在编辑卡上（编辑框通道置 true，其 Exited/卡外置 false）
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _ibeamScopeTimer;   // 25ms 作用域轮询（对齐画笔 _ringScopeTimer）
    private bool _handInstalled;                  // 连线手型作用域是否已安装（对齐 _ibeamInstalled；变化才动资源）
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _handScopeTimer;   // 25ms 作用域轮询（对齐 _ibeamScopeTimer）

    private bool _isSmoothZooming;
    private double _zoomOmega = ZoomSpringOmega;
    private bool _zoomFromRightDrag;
    private bool _zoomAnimCommitting; // ChangeView 异步落地瞬间保用提交值，避免闪回缩放前画面
    // 最近一次提交给 ScrollViewer 的目标视图。用于判定"提交是否真的落到位"——
    // ViewChanged 回吐的 Offset 是被【旧 extent】夹断的值，绝不能当作权威视图采信。
    private double _committedH, _committedV, _committedZoom;
    // 两个计数器必须分开：等待 zoom 落地是"什么都不做只是等"，补发偏移才是"真的又发了一次请求"。
    private int _commitNudges;
    private long _commitStartTicks;                   // 本次提交起始刻度，用于墙钟兜底(见 TryAdvanceCommit)
    private bool _commitWatchdogQueued;               // 防止连续提交排出多条并行看门狗链
    private double _zAnimContentX, _zAnimContentY; // 锚点对应文档坐标(缩放=1 DIP)，一段手势内固定
    private Point _zAnimAnchor;
    private double _zoomAnimTargetFactor, _zoomAnimTargetH, _zoomAnimTargetV;
    private double _zoomAnimCurrentFactor, _zoomAnimCurrentH, _zoomAnimCurrentV;

    // 画布视口快照：偏移/居中量必须与【同一时刻的视口尺寸】配套，否则滚动条出现/消失
    private double _vpW = 1000, _vpH = 800;

    private DateTime _lastZoomEndTime = DateTime.MinValue;
    private DateTime _doubleTapStartPending = DateTime.MinValue;
    // 双击去重窗口(ms)：只用来吃掉手势识别器的重复派发。

    private bool _isWheelScrolling;

    private int _currentPage = -1;
    private double _lastZoomNotified = -1;

    /// <summary>滚动导致当前页变化时触发（参数 = 0-based 页索引）。UI 线程回调。</summary>
    public event EventHandler<int>? CurrentPageChanged;

    /// <summary>缩放百分比变化时触发（参数 = 百分比，如 100.0）。UI 线程回调。</summary>
    public event EventHandler<double>? ZoomPercentChanged;

    /// <summary>视口滚动/缩放导致可见区域变化时触发（供 MainPage 同步滚动条等消费）。UI 线程回调。</summary>
    public event EventHandler? ViewportChanged;

    public int CurrentPageIndex => Math.Max(0, _currentPage);

    public int PageCount => _pages.Count;

    /// <summary>页尺寸信息（供 MainPage 构建缩略图占位等复用，避免重复 FFI 取尺寸）。</summary>
    public IReadOnlyList<GpuPageInfo> PageInfos => _pages;

    private PageLayoutMode _layoutMode = PageLayoutMode.VerticalContinuous;

    /// <summary>纵向布局下的列数/排版（单页 / 双页 / 书籍），与 _layoutMode（横纵主轴）轴独立。 横向胶片条模式忽略此值（始终单列）。Arrangement 变化时复用 _layoutSwitch* 稳定窗口与 ReanchorAfterLayoutChange 重锚。</summary>
    private PageArrangement _arrangement = PageArrangement.Single;

    /// 布局切换的"稳定窗口"。
    private bool _layoutSwitchInFlight;

    private int _layoutSwitchAnchor = -1;

    private double _layoutTargetH, _layoutTargetV, _layoutTargetZoom;

    /// <summary>布局切换启动时刻（看门狗兜底判超时用）。</summary>
    private long _layoutSwitchStartTicks;

    /// <summary>布局切换看门狗：万一 ScrollViewer 目标被极端夹断且再无 ViewChanged，兜底强制收尾。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _layoutWatchdog;

    /// <summary>宿主视口尺寸变化（侧边栏挤开）前的"阅读位置不变量"快照是否有效；见 CaptureHostResizeAnchor。</summary>
    private bool _hostResizeCaptureValid;

    private double _hostResizeZ0;

    /// <summary>快照时的视口尺寸（新旧比例的"旧"端）。缩放比**恒取 vpW** —— 两种布局都锁左右边线 （见 的"三条不动边线"）。</summary>
    private double _hostResizeVpW0;

    private double _hostResizeDocLeftX;

    /// <summary>快照时视口【上缘】切到的文档 Y —— 三条不动边线之「上」：上边钉住、只让下边露出更多内容。</summary>
    private double _hostResizeDocTopY;

    /// <summary>当前连续布局方向。切换时：重排布局 → 若原本处于"适应"缩放则换算为新主轴的适应值 （否则保留用户手动缩放）→ 锚定切换前的当前页，使视口不飘走。</summary>
    public PageLayoutMode LayoutMode
    {
        get => _layoutMode;
        set
        {
            if (_layoutMode == value) return;

            int anchor = CurrentPageIndex;
            // 【布局切换稳定窗口】从此刻起 ViewChanged 不读 Viewer.*（见字段注释），
            _layoutSwitchInFlight = true;
            _layoutSwitchAnchor = anchor;
            _layoutSwitchStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            StopLayoutSwitchWatchdog();
            // 【视口中心保持】切换前记录"视口中心在 anchor 页内的局部偏移 (dx,dy)"。
            // （否则视口中心会跳到当页左上角，用户正在看的位置丢失）。
            double zOld = (_liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor);
            if (zOld <= 0) zOld = 1.0;
            double vpW = _vpW, vpH = _vpH;
            double cx = _liveH / zOld + vpW / 2.0 / zOld;
            double cy = _liveV / zOld + vpH / 2.0 / zOld;
            double pageLocalX = 0, pageLocalY = 0;
            if (anchor >= 0 && anchor < _pages.Count)
            {
                var ar = _pages[anchor].LayoutRect;
                pageLocalX = cx - ar.X;
                pageLocalY = cy - ar.Y;
            }
            // 先按【旧布局】判定当前是否处于"适应"缩放（适应宽度/适应高度）。
            double oldFit = FitToViewportPercent;
            double curZoomPercent = (_liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor) * 100.0;
            bool wasFitted = Math.Abs(curZoomPercent - oldFit) < 1.0;

            StopSmoothZoom();
            StopWheelScroll();
            CancelPendingRestore(); // 用户主动切换布局 → 放弃未落定的还原，防止回拽

            _layoutMode = value;
            LayoutModeChanged?.Invoke(this, value);
            if (_pages.Count == 0) { _layoutSwitchInFlight = false; return; }

            RecomputeLayout(_pages);

            // 适应缩放要按【新主轴】重算：纵向的"适应宽度"在横向对应"适应高度"，
            // 否则切到横向后页高溢出视口，只能看到每页顶部一条，跨页阅读毫无意义。
            double newZoomPercent = wasFitted ? FitToViewportPercent : curZoomPercent;
            ReanchorAfterLayoutChange(anchor, newZoomPercent, pageLocalX, pageLocalY);

            // Spacer 尺寸刚改，ScrollViewer 的可滚动范围要等下一轮布局才刷新 → 首轮 ChangeView 的
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_pages.Count == 0 || _layoutMode != value || !_layoutSwitchInFlight) return;
                if (_layoutSwitchAnchor != anchor) return;
                ReanchorAfterLayoutChange(anchor, newZoomPercent, pageLocalX, pageLocalY);
            });
            StartLayoutSwitchWatchdog();
        }
    }

    /// <summary>纵向布局下的列数/排版（单页 / 双页 / 书籍），与 LayoutMode（横纵主轴）轴独立。 切换时：重排布局 → 若原本处于"适应"缩放则按新列宽重算适应值（否则保留用户手动缩放） → 锚定切换前的当前页，使视口不飘走。复用布局切换稳定窗口机制。</summary>
    public event EventHandler<PageArrangement>? ArrangementChanged;

    /// <summary>纵向布局下的列数/排版。设置时复用 _layoutSwitch* 稳定窗口与 ReanchorAfterLayoutChange 重锚， 行为对齐 LayoutMode setter。横向胶片条模式忽略此值（RecomputeLayout 的横向分支不读它）。</summary>
    public PageArrangement Arrangement
    {
        get => _arrangement;
        set
        {
            if (_arrangement == value) return;

            int anchor = CurrentPageIndex;
            // 【布局切换稳定窗口】与 LayoutMode 共用，防止重排期间 ViewChanged 把夹断偏移当权威写回。
            _layoutSwitchInFlight = true;
            _layoutSwitchAnchor = anchor;
            _layoutSwitchStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            StopLayoutSwitchWatchdog();

            double zOld = (_liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor);
            if (zOld <= 0) zOld = 1.0;
            double vpW = _vpW, vpH = _vpH;
            double cx = _liveH / zOld + vpW / 2.0 / zOld;
            double cy = _liveV / zOld + vpH / 2.0 / zOld;
            double pageLocalX = 0, pageLocalY = 0;
            if (anchor >= 0 && anchor < _pages.Count)
            {
                var ar = _pages[anchor].LayoutRect;
                pageLocalX = cx - ar.X;
                pageLocalY = cy - ar.Y;
            }
            double oldFit = FitToViewportPercent;
            double curZoomPercent = (_liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor) * 100.0;
            bool wasFitted = Math.Abs(curZoomPercent - oldFit) < 1.0;

            StopSmoothZoom();
            StopWheelScroll();
            CancelPendingRestore();

            _arrangement = value;
            ArrangementChanged?.Invoke(this, value);
            if (_pages.Count == 0) { _layoutSwitchInFlight = false; return; }

            RecomputeLayout(_pages);

            // 切列数不改变主轴，故"适应"仍按【纵向适应宽度】重算（列数变多时自动缩到两列都可见）。
            double newZoomPercent = wasFitted ? FitToViewportPercent : curZoomPercent;
            ReanchorAfterLayoutChange(anchor, newZoomPercent, pageLocalX, pageLocalY);

            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_pages.Count == 0 || _arrangement != value || !_layoutSwitchInFlight) return;
                if (_layoutSwitchAnchor != anchor) return;
                ReanchorAfterLayoutChange(anchor, newZoomPercent, pageLocalX, pageLocalY);
            });
            StartLayoutSwitchWatchdog();
        }
    }

    /// <summary>启动布局切换看门狗：若落定期内 ScrollViewer 偏移始终被钳制在非目标值（极端夹断）， 定时器到期后强制按真实落点收尾，避免稳定窗口永久悬挂、页码永不刷新。</summary>
    private void StartLayoutSwitchWatchdog()
    {
        if (_layoutWatchdog == null)
        {
            _layoutWatchdog = DispatcherQueue.CreateTimer();
            _layoutWatchdog.Interval = TimeSpan.FromMilliseconds(500);
            _layoutWatchdog.Tick += (s, e) => ForceCloseLayoutSwitchWindow();
        }
        _layoutWatchdog.Stop();
        _layoutWatchdog.Start();
    }

    private void StopLayoutSwitchWatchdog()
    {
        _layoutWatchdog?.Stop();
    }

    /// <summary>看门狗兜底：不依赖 ViewChanged 到达判定，直接按当前真实值收尾布局切换稳定窗口。</summary>
    private void ForceCloseLayoutSwitchWindow()
    {
        _layoutWatchdog?.Stop();
        if (!_layoutSwitchInFlight) return;
        var pages = _pages;
        if (pages.Count == 0) { _layoutSwitchInFlight = false; return; }
        _layoutSwitchInFlight = false;
        SyncLiveView(Viewer.HorizontalOffset, Viewer.VerticalOffset, Viewer.ZoomFactor);
        _vpW = Viewer.ViewportWidth; _vpH = Viewer.ViewportHeight;
        if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
        UpdateVisibleRange();
        QueueVisiblePages();
        InvalidateBoth();
        NotifyViewState();
    }

    /// <summary>布局方向变化时触发（供 MainPage 更新按钮图标/提示）。UI 线程回调。</summary>
    public event EventHandler<PageLayoutMode>? LayoutModeChanged;

    public bool IsHorizontalLayout => _layoutMode == PageLayoutMode.HorizontalContinuous;

    /// 布局重排后把视图落到「指定缩放 + 指定页内位置对齐视口中心」，交叉轴同理由页内偏移决定 （内容窄/矮于视口时 ScrollViewer 会自动居中呈现）。
    private void ReanchorAfterLayoutChange(int anchor, double zoomPercent, double pageLocalX, double pageLocalY)
    {
        var pages = _pages;
        if (pages.Count == 0) return;
        if (anchor < 0 || anchor >= pages.Count) anchor = 0;

        float z = (float)Math.Clamp(zoomPercent / 100.0, MinZoom, MaxZoom);
        double vpW = _vpW, vpH = _vpH;
        if (vpW <= 0 || vpH <= 0) { vpW = 1000; vpH = 800; }
        double cx = pages[anchor].LayoutRect.X + pageLocalX;
        double cy = pages[anchor].LayoutRect.Y + pageLocalY;
        // 【两轴都按中心对齐】只设主轴会丢失交叉轴的页内偏移（纵向转横向时 v 被置 0 → 跳到页顶端；
        double h = Math.Max(0, cx * z - vpW / 2.0);
        double v = Math.Max(0, cy * z - vpH / 2.0);

        // 【布局切换防夹断】RecomputeLayout 已改 Spacer 尺寸，但 ScrollViewer 的可滚动范围要等
        try { Viewer.UpdateLayout(); } catch { }
        // 记录本次播种的目标（供布局切换稳定窗口判定"真实到达"）。布局再布局可能微调吊平，
        _layoutTargetH = h;
        _layoutTargetV = v;
        _layoutTargetZoom = z;
        Viewer.ChangeView(h, v, z, disableAnimation: true);
        SyncLiveView(h, v, z);
        // 同步播种动画当前态，避免随后的双击/缩放读到陈旧值而闪回。
        _zoomAnimCurrentFactor = z;
        _zoomAnimCurrentH = h;
        _zoomAnimCurrentV = v;
        SetVisibleRangeFor(h, v, z);
        QueueVisiblePages();
        InvalidateBoth();
        NotifyViewState();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public void GoToPage(int index)
    {
        var pages = _pages;
        if (index < 0 || index >= pages.Count) return;
        // 缩放动画进行中收到 Home/End 直接跳页：先落定当前插值态，避免与缩放引擎抢视图
        if (_isSmoothZooming) StopSmoothZoom();
        double z = _liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor;
        if (z <= 0) z = 1.0;
        if (IsHorizontalLayout)
        {
            // 末列保护（横向布局）：最后一列的【右缘】永远贴着视口右端（与纵向末页同理），
            double targetX = index == pages.Count - 1
                ? EndScrollOffset(horizontal: true, z)
                : Math.Max(0, (pages[index].LayoutRect.Left - PageGapHorizontal / 2.0) * z);
            // 直接跳：必须把滚动弹簧权威镜像同步到新位置（并同步 _live*），否则后续 GoToPageSmooth
            // 会先弹回旧页再动画过去（Home/End 后首按 PgDn/PgUp 会多跳一次）。
            _scrollH.Reset(targetX);
            _scrollV.Reset(_liveV);
            SyncLiveView(targetX, _liveV, z);
            // 缩放动画被打断后 StopSmoothZoom 可能开了提交窗；关掉它，否则 GetViewState 会返回缩放插值态
            AbandonZoomCommitWindow();
            // 带上 z（非 null）：缩放被打断后其 zoom 提交可能尚未落地，null 会让偏移被旧 extent 夹断、
            Viewer.ChangeView(targetX, null, (float)z, disableAnimation: true);
            // 【大距离跳转加速】ChangeView 是异步落地，目标页的排队渲染若只依赖 ViewChanged 回吐，
            SetVisibleRangeFor(targetX, _liveV, z);
            QueueVisiblePages();
            InvalidateBoth();
        }
        else
        {
            // 末页保护：无论缩放倍率，最后一页的【页尾】永远贴着视口底部。小缩放下整页短于视口，
            double targetY = index == pages.Count - 1
                ? EndScrollOffset(horizontal: false, z)
                : Math.Max(0, (pages[index].LayoutRect.Top - PageGap / 2.0) * z);
            _scrollV.Reset(targetY);
            _scrollH.Reset(_liveH);
            SyncLiveView(_liveH, targetY, z);
            AbandonZoomCommitWindow();
            // 带上 z（非 null）：同上，缩放+偏移原子落地，避免被旧 extent 夹断/被缩放落地拽回。
            Viewer.ChangeView(null, targetY, (float)z, disableAnimation: true);
            // 同上：立即按目标播种可见范围并排队渲染，不等 ViewChanged 异步落地。
            SetVisibleRangeFor(_liveH, targetY, z);
            QueueVisiblePages();
            InvalidateBoth();
        }
    }

    public void ScrollByArrow(Windows.System.VirtualKey key)
    {
        double notch = 120.0 * WheelScrollFactor;
        // 符号约定与滚轮处理器保持一致（WinUI MouseWheelDelta 正值=上滚/远离用户，负值=下滚/朝向用户）。
        switch (key)
        {
            case Windows.System.VirtualKey.Up:    AddWheelScroll(0,  notch); break;
            case Windows.System.VirtualKey.Down:  AddWheelScroll(0, -notch); break;
            case Windows.System.VirtualKey.Left:  AddWheelScroll( notch, 0); break;
            case Windows.System.VirtualKey.Right: AddWheelScroll(-notch, 0); break;
        }
    }

    /// <summary>空格：下翻一个视口（视口高度的弹簧滚动，与缩放无关）。 一次 = 当前视口正下方那一屏；连点无缝接续；到末屏由 AddWheelScroll 的弹簧自夹到底。</summary>
    public void ScrollViewportDown()
    {
        if (_vpH <= 0) return;
        // 下滚 = 正 dv（与 ScrollByArrow 的 Down 同向，因 WheelScrollFactor 为负，Down 实际传 +|notch|）。
        AddWheelScroll(0, _vpH);
    }

    public void GoToPageSmooth(int index)
    {
        var pages = _pages;
        if (index < 0 || index >= pages.Count) return;
        // 缩放动画进行中收到平滑翻页：先【落定到当前插值态】再算目标（交互第一原则：随时可打断动画）。
        // 否则按动画中途的插值态读到的倍率/偏移与落定后不一致，终点错位乱飞。AddWheelScroll 内会再
        if (_isSmoothZooming) StopSmoothZoom();
        AbandonZoomCommitWindow();
        // 必须用权威实时镜像 CurrentZoom(=_liveZoom)：缩放动画进行中它是【当前插值态】倍率；
        // 页位置错位 → 缩放中按空格/PageUp/PageDn 终点乱飞（滚轮/方向键只加固定 notch 不依赖页布局故不踩此坑，
        double z = CurrentZoom;
        if (z <= 0) z = 1.0;
        // 末页/末列：同 GoToPage 的末页保护，把结尾钉在视口末端，避免小缩放下瞬移（大缩放下结果不变）。
        double target = index == pages.Count - 1
            ? EndScrollOffset(IsHorizontalLayout, z)
            : (IsHorizontalLayout
                ? Math.Max(0, (pages[index].LayoutRect.Left - PageGapHorizontal / 2.0) * z)
                : Math.Max(0, (pages[index].LayoutRect.Top - PageGap / 2.0) * z));
        // 复用滚轮弹簧：以"当前【显示位置】(_live* 权威镜像) → 该页起点"的差量作为一次滚动步进，
        // 关键：差量基准必须用 _live*（当前真实显示偏移），【不能】用弹簧 _scroll*.Target —— 缩放/
        if (IsHorizontalLayout) AddWheelScroll(target - _liveH, 0);
        else                     AddWheelScroll(0, target - _liveV);
    }

    /// <summary>设置缩放百分比（10–400），以视口中心为锚。 animate=true 走逐帧 ChangeView + 实时重光栅平滑缩放（锚点严格不动、缩小不黑边、渐进清晰）； animate=false 瞬时跳变（右键拖拽缩放等连续交互用，紧跟鼠标）。</summary>
    public void SetZoomPercent(double percent, bool animate = false)
    {
        if (animate)
        {
            StartSmoothZoom(percent, null);
            return;
        }
        float target = (float)Math.Clamp(percent / 100.0, MinZoom, MaxZoom);
        float current = Viewer.ZoomFactor;
        if (current <= 0 || Math.Abs(target - current) < 0.001f) return;

        // 视口中心在文档坐标系的位置 → 换算新缩放下的偏移并 clamp 到可滚动范围。
        double cx = (Viewer.HorizontalOffset + Viewer.ViewportWidth / 2.0) / current;
        double cy = (Viewer.VerticalOffset + Viewer.ViewportHeight / 2.0) / current;
        double extentW = (double.IsNaN(Spacer.Width) ? 0 : Spacer.Width) * target;
        double extentH = (double.IsNaN(Spacer.Height) ? 0 : Spacer.Height) * target;
        double maxX = Math.Max(0, extentW - Viewer.ViewportWidth);
        double maxY = Math.Max(0, extentH - Viewer.ViewportHeight);
        double newX = Math.Clamp(cx * target - Viewer.ViewportWidth / 2.0, 0, maxX);
        double newY = Math.Clamp(cy * target - Viewer.ViewportHeight / 2.0, 0, maxY);

        Viewer.ChangeView(newX, newY, target, disableAnimation: true);
    }

    /// <summary>以【当前目标缩放】为基准步进（累积语义）：连点 / 连按可精确落到 base + n×delta，而不是每次都基于尚未落地的中间插值态（那样连点会看起来"没反应"）。 返回夹取到 [MinZoomPercent, MaxZoomPercent] 后的最终目标。</summary>
    public double ZoomStepPercent(double deltaPercent)
    {
        double basePercent = _isSmoothZooming ? _zoomAnimTargetFactor * 100.0 : CurrentZoom * 100.0;
        double target = Math.Clamp(basePercent + deltaPercent, MinZoomPercent, MaxZoomPercent);
        StartSmoothZoom(target, null);
        return target;
    }

    /// <summary>按给定 偏移/缩放 更新【可见矩形四边】(文档 DIP)与光栅缩放。内部自取 _lock。 动画/滚轮/拖拽等自管视图的帧循环统一走这里，保证横向布局下水平可见范围同样实时准确 （否则横向滚动时后台按陈旧的 _visLeft/_visRight 选页，</summary>
    private void SetVisRect(double hOff, double vOff, double zoom)
    {
        if (zoom <= 0) return;
        // 用画布视口快照（与偏移配套），不自读 Viewer.* —— 滚动条出现/消失瞬间两者会错开一帧。
        double vpW = _vpW;
        double vpH = _vpH;
        lock (_lock)
        {
            _visLeft = hOff / zoom;
            _visRight = (hOff + vpW) / zoom;
            _visTop = vOff / zoom;
            _visBottom = (vOff + vpH) / zoom;
        }
    }

    /// 启动（或接续）平滑缩放到 percent%，以 anchor（视口坐标，null=视口中心）为不动点。
    /// 而非用本次鼠标位置重算——否则快速连点(三连击)每段新缩放起步瞬间因锚点切换而跳变(内容乱飞、不以鼠标为中心)。
    public void StartSmoothZoom(double percent, Point? anchor, bool reuseAnchor = false)
    {
        StopWheelScroll(); // Ctrl+滚轮缩放优先：取消进行中的滚轮滚动
        CancelPendingRestore(); // 用户缩放（双击/Ctrl+滚轮/预设）优先于未落定的还原，防止回拽
        // 【关键修复·双击起止闪帧】双击手势会"吃掉"第二次指针抬起（WinUI 手势识别器吞掉该 release），
        // 于是 Viewer_PointerReleased 不触发、_isDragPanning 卡在 true 直到下一个按下。这造成两处错位闪。
        _isDragPanning = false;
        StopPanApplyTimer();
        StopZoomApplyTimer(); // 新一段手势重新播种：清残留的右键下发定时器（若非右键手势，StepZoomFrame 不会重开）
        _zoomAnimCommitting = false; // 清提交标志：避免读到上一动画残留的 _zoomAnimCurrent*(可能=0) 造成首帧闪到顶部
        // 冻结画布视口快照：整段手势的偏移与居中量都用同一视口，杜绝滚动条出现/消失引发的起止错位闪帧。
        _vpW = Viewer.ViewportWidth;
        _vpH = Viewer.ViewportHeight;
        if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
        double target = Math.Clamp(percent / 100.0, MinZoom, MaxZoom);
        // 起点：进行中用当前插值状态（无缝接续）；否则用【实时视图镜像 _live*】而非 Viewer.*——
        double z0 = _isSmoothZooming ? _zoomAnimCurrentFactor : (_liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor);
        if (z0 <= 0) z0 = 1.0;
        double startH = _isSmoothZooming ? _zoomAnimCurrentH : (_liveZoom > 0 ? _liveH : Viewer.HorizontalOffset);
        double startV = _isSmoothZooming ? _zoomAnimCurrentV : (_liveZoom > 0 ? _liveV : Viewer.VerticalOffset);

        // 是否复用上一段的固定锚点：仅当"仍在进行中"或"双击专用且刚落定(宽限窗口内)"。
        // 这是修复三连击乱飞的关键——快速连点属同一手势，应围绕同一点平滑续接，不能用每次鼠标位置重算导致跳变。
        bool shouldReuseAnchor = _isSmoothZooming ||
            (reuseAnchor && (DateTime.UtcNow - _lastZoomEndTime).TotalMilliseconds < ZoomReuseAnchorGraceMs);

        if (shouldReuseAnchor)
        {
            // 复用上一段动画的固定文档不动点(_zAnimContentX/Y)与视口锚点(_zAnimAnchor)。
            // 注意：此时【不】用本次传入的 anchor 重算，否则起点偏移对应的仍是旧不动点、新锚点会逼出起步跳变。
        }
        else
        {
            Point a = anchor ?? new Point(_vpW / 2.0, _vpH / 2.0);
            _zAnimAnchor = a;
            // 锚点对应的文档坐标（缩放=1 DIP），全程固定；终态偏移由它推出 → 落定后锚点严格不动。
            // 视口坐标→文档坐标必须先扣掉这段居中量，否则低缩放整页落入视口时锚点算偏、
            GetCenterOffsets(z0, out double cx0, out double cy0, _vpW, _vpH);
            _zAnimContentX = (a.X - cx0 + startH) / z0;
            _zAnimContentY = (a.Y - cy0 + startV) / z0;
        }

        _zoomAnimTargetFactor = target;
        ResolveZoomOffsets(target, out _zoomAnimTargetH, out _zoomAnimTargetV);
        _zoomAnimCurrentFactor = z0;
        _zoomAnimCurrentH = startH;
        _zoomAnimCurrentV = startV;

        if (_isSmoothZooming)
        {
            // 接续：只改终点，【不动】当前速度 —— 连点/连发时曲线是接着往前长的。
            // 旧的定时长缓动必须重启时钟，速度在接缝处跳变，那一下就是"卡"的观感来源。
            _zoomLog.Target = Math.Log(target);
        }
        else
        {
            BeginZoomGesture(z0, target, ZoomSpringOmega, fromRightDrag: false);
        }
        _zoomOmega = ZoomSpringOmega;
        _isSmoothZooming = true;
        EnsureFramePump();
    }

    /// <summary>起一段全新的缩放手势：弹簧从 z0 起步（位置与速度归零）、目标 z1，并播种重光栅节流基准。</summary>
    private void BeginZoomGesture(double z0, double z1, double omega, bool fromRightDrag)
    {
        _vpW = Viewer.ViewportWidth;
        _vpH = Viewer.ViewportHeight;
        if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
        _zoomLog.Reset(Math.Log(z0));
        _zoomLog.Target = Math.Log(z1);
        _zoomOmega = omega;
        _zoomFromRightDrag = fromRightDrag;
        // 【必须播种画布插值态】GetViewState 在 _isSmoothZooming 期间【只】读 _zoomAnimCurrent*。
        // 这些字段默认 0，不播种则缩放尽头(MaxZoom/MinZoom)按下并推动时起点按 0 换算。
        _zoomAnimCurrentFactor = z0;
        _zoomAnimCurrentH = _liveZoom > 0 ? _liveH : Viewer.HorizontalOffset;
        _zoomAnimCurrentV = _liveZoom > 0 ? _liveV : Viewer.VerticalOffset;
    }

    /// <summary>当前权威缩放。一律取 _liveZoom（实时镜像）而非 Viewer.ZoomFactor： ChangeView 是异步的，缩放刚提交/动画刚被打断的那几帧里 Viewer.ZoomFactor 仍是【上一个】缩放， 拿它去画画布或算范围，就会先闪回旧倍率、等落地后再跳回来。</summary>
    private double CurrentZoom
    {
        get
        {
            if (_liveZoom > 0) return _liveZoom;
            double z = Viewer.ZoomFactor;
            return z > 0 ? z : 1.0;
        }
    }

    /// 给定缩放下的合法滚动范围（等价于 ScrollViewer 的 Scrollable{Width,Height}）。
    private void GetScrollBounds(double zoom, out double maxH, out double maxV, double vpW, double vpH)
    {
        double w = double.IsNaN(Spacer.Width) ? 0 : Spacer.Width;
        double hh = double.IsNaN(Spacer.Height) ? 0 : Spacer.Height;
        maxH = Math.Max(0, w * zoom - vpW);
        maxV = Math.Max(0, hh * zoom - vpH);
    }

    /// 末页/末列对齐用的"文档末端"滚动偏移（缩放后像素，等价 GetScrollBounds 的对应轴上界）。
    private double EndScrollOffset(bool horizontal, double z)
    {
        GetScrollBounds(z, out double maxH, out double maxV, Viewer.ViewportWidth, Viewer.ViewportHeight);
        return horizontal ? maxH : maxV;
    }

    private void GetCenterOffsets(double zoom, out double centerX, out double centerY, double vpW, double vpH)
    {
        double extentW = (double.IsNaN(Spacer.Width) ? 0 : Spacer.Width) * zoom;
        double extentH = (double.IsNaN(Spacer.Height) ? 0 : Spacer.Height) * zoom;
        centerX = extentW < vpW ? (vpW - extentW) / 2.0 : 0;
        centerY = extentH < vpH ? (vpH - extentH) / 2.0 : 0;
    }

    /// 预测给定缩放落地后 ScrollViewer 的实际视口尺寸（= 控件尺寸 − Auto 滚动条占位）。
    /// 滚动条占位【实测】：滚动条可见时 ViewportWidth&lt;ActualWidth；overlay 样式恒相等(0)，
    private void PredictViewport(double zoom, out double pvW, out double pvH)
    {
        double ctrlW = Viewer.ActualWidth, ctrlH = Viewer.ActualHeight;
        if (ctrlW <= 0 || ctrlH <= 0) { pvW = Viewer.ViewportWidth; pvH = Viewer.ViewportHeight; return; }
        // 实测当前滚动条占位（当前态有滚动条才测得；目标态新增的滚动条所在轴内容必溢出 → cx=0，无居中跳变）。
        double sbW = Math.Max(0, ctrlW - Viewer.ViewportWidth);
        double sbH = Math.Max(0, ctrlH - Viewer.ViewportHeight);
        double cw = (double.IsNaN(Spacer.Width) ? 0 : Spacer.Width) * zoom;
        double ch = (double.IsNaN(Spacer.Height) ? 0 : Spacer.Height) * zoom;
        double vpW = ctrlW, vpH = ctrlH;
        for (int i = 0; i < 2; i++)
        {
            if (cw > vpW) vpH = Math.Max(0, ctrlH - sbH);
            if (ch > vpH) vpW = Math.Max(0, ctrlW - sbW);
        }
        pvW = Math.Max(1, vpW);
        pvH = Math.Max(1, vpH);
    }

    /// <summary>由固定不动点（_zAnimContentX/Y 文档坐标 + _zAnimAnchor 视口坐标）反解给定缩放下的滚动偏移， 并夹到合法范围。这是"锚点全程不动"的唯一来源，动画帧与收尾帧都走它，故绝不会出现末帧位移。</summary>
    private void ResolveZoomOffsets(double zoom, out double h, out double v)
    {
        // 用画布视口快照 _vpW/_vpH：偏移与居中量必须配套（见 _vpW 注释），
        // 否则 ScrollViewer 落定瞬间滚动条出现/消失 → 居中量跳变 → 起止闪一帧错位。
        GetCenterOffsets(zoom, out double cx, out double cy, _vpW, _vpH);
        GetScrollBounds(zoom, out double maxX, out double maxY, _vpW, _vpH);
        h = Math.Clamp(_zAnimContentX * zoom + cx - _zAnimAnchor.X, 0, maxX);
        v = Math.Clamp(_zAnimContentY * zoom + cy - _zAnimAnchor.Y, 0, maxY);
    }

    /// <summary>缩放帧：在 log(zoom) 空间推进弹簧 → 换算回缩放 → 围绕固定锚点反解偏移。 返回是否需要重画画布。</summary>
    private bool StepZoomFrame(double dt)
    {
        _zoomLog.Step(dt, _zoomOmega);

        if (_zoomLog.IsSettled(ZoomSettleLog, ZoomSettleVel))
        {
            // 右键拖拽【按钮仍按住】时绝不收尾退出。
            // 收尾会解冻并按"中间密度"重取瓦片（糊→清脉冲 + 瓦片锐度边界移动），
            // 慢拖停滞时就表现为瓦片之间波动/蠕动。
            // 落定瞬间先把【显示态】吸附到精确目标并重绘最终帧：否则按住停顿期间停泵冻结的是
            _zoomLog.SnapToTarget();
            double zt = Math.Exp(_zoomLog.Value);
            ResolveZoomOffsets(zt, out double hpT, out double vpT);
            _zoomAnimCurrentFactor = zt;
            _zoomAnimCurrentH = hpT;
            _zoomAnimCurrentV = vpT;
            SetVisRect(hpT, vpT, zt);
            SyncLiveView(hpT, vpT, zt);
            if (_isRightZooming)
            {
                // 按住期间保持手势存活：冻结不解除（单一密度层恒稳），只停帧泵省电。
                StopZoomApplyTimer();
                StopFramePump();
                InvalidateBoth();
                return false;
            }
            FinishZoom();
            return false;
        }

        double zp = Math.Exp(_zoomLog.Value);
        ResolveZoomOffsets(zp, out double hp, out double vp);
        _zoomAnimCurrentFactor = zp;
        _zoomAnimCurrentH = hp;
        _zoomAnimCurrentV = vp;
        SetVisRect(hp, vp, zp);
        SyncLiveView(hp, vp, zp);
        NotifyViewState(); // 缩放百分比/页码回显跟着动，而不是等落定才跳一下

        // 右键拖拽是长交互，滚动条与外部同步条要跟着走 → 每帧下发一次真实视图。
        if (_zoomFromRightDrag)
        {
            // 但逐帧 ChangeView + ViewportChanged 在 120Hz 帧泵下代价过高 ⇒ 合并到应用定时器统一发。
            EnsureZoomApplyTimer();
        }
        return true;
    }

    /// 提交最终视图，并开启"提交窗口"()：该窗口内画布继续读 _zoomAnimCurrent*，避免 ChangeView 异步落地前闪回缩放前的画面。
    /// ⚠ ChangeView 在目标与当前值完全一致时【不会】触发 ViewChanged ⇒ 落地判定必须自带兜底，
    ///   否则等待会永久挂起（左右键全失效）。
    private void CommitViewWithGuard(double h, double v, double zoom)
    {
        _committedH = h;
        _committedV = v;
        _committedZoom = zoom;
        _commitNudges = 0;
        _commitStartTicks = Stopwatch.GetTimestamp();

        bool changing = Viewer.ChangeView(h, v, (float)zoom, disableAnimation: true);
        if (!changing)
        {
            _zoomAnimCommitting = false; // 视图没变 → 不会有 ViewChanged，绝不能开窗口
            return;
        }
        _zoomAnimCommitting = true;
        ScheduleCommitWatchdog();
    }

    /// <summary>提交是否已真正落到位。ZoomFactor 与 Offset 都要匹配 —— 只看 zoom 是不够的， 恰恰是"zoom 已换、offset 还被旧 extent 夹着"这个中间态制造了闪帧。 提交偏移可能本就超出可达范围（内容不够长），故按当前缩放的合法范围夹一次再比。</summary>
    private bool IsCommittedViewReached()
    {
        if (_committedZoom <= 0) return true;
        if (!IsCommittedZoomReached()) return false;
        // 判定与 Viewer 实际落地状态比：用实时视口（而非画布快照），它才是 ScrollViewer 的真相。
        GetScrollBounds(_committedZoom, out double maxH, out double maxV,
            Viewer.ViewportWidth, Viewer.ViewportHeight);
        double th = Math.Clamp(_committedH, 0, maxH);
        double tv = Math.Clamp(_committedV, 0, maxV);
        return Math.Abs(Viewer.HorizontalOffset - th) <= CommitOffsetEps
            && Math.Abs(Viewer.VerticalOffset - tv) <= CommitOffsetEps;
    }

    /// <summary>提交的缩放是否已落地。extent 要等它落地后才会按新倍率重排。</summary>
    private bool IsCommittedZoomReached()
        => _committedZoom <= 0 || Math.Abs(Viewer.ZoomFactor - _committedZoom) <= CommitZoomEps;

    /// 提交尚未到位时推进一步。
    private bool TryAdvanceCommit()
    {
        if (!IsCommittedZoomReached())
        {
            // zoom 仍在异步落地：只等，绝不补发（补发会把正在进行的缩放顶掉，见上）。
            // → 画布闪一帧错位（双击收尾闪的根因）。落地通常 <20ms，250ms 纯防真死锁。
            long elapsedTicks = Stopwatch.GetTimestamp() - _commitStartTicks;
            if (elapsedTicks >= (long)(CommitWaitDeadlineMs * Stopwatch.Frequency / 1000.0)) return false;
            return true;
        }

        if (++_commitNudges > MaxCommitNudges) return false;
        Viewer.ChangeView(_committedH, _committedV, null, disableAnimation: true);
        return true;
    }

    /// 强制收尾：关窗，并把权威镜像拉回 Viewer 真值。
    private void ForceCloseCommitWindow()
    {
        _zoomAnimCommitting = false;
        // 仅当提交的缩放已真正落地(Viewer 此刻倍率=目标)才把 _live* 拉回 Viewer 真值；
        // 否则是"缩放还在异步落地就被迫关窗"，Viewer 仍停在旧倍率——此时若把 _live* 回退到旧倍率，
        if (_pendingRestore == null && !_layoutSwitchInFlight && IsCommittedZoomReached())
            SyncLiveView(Viewer.HorizontalOffset, Viewer.VerticalOffset, Viewer.ZoomFactor);
        InvalidateBoth();
        NotifyViewState();
    }

    /// <summary>提交看门狗：Low 优先级在布局/渲染之后执行。到位即关窗；未到位交给 TryAdvanceCommit 决定"等"还是"补发偏移"，配额耗尽则强制收尾。有限轮内必然关窗，_zoomAnimCommitting 不会卡死。</summary>
    private void ScheduleCommitWatchdog()
    {
        if (_commitWatchdogQueued) return; // 连续提交（FinishZoom 后紧跟 StopSmoothZoom）不排第二条链
        _commitWatchdogQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _commitWatchdogQueued = false;
            if (!_zoomAnimCommitting) return;
            if (IsCommittedViewReached())
            {
                _zoomAnimCommitting = false;
                return;
            }
            if (!TryAdvanceCommit()) { ForceCloseCommitWindow(); return; }
            ScheduleCommitWatchdog();
        });
    }

    /// <summary>缩放收尾：精确落到目标视图，一次性对齐 ScrollViewer，再补一次可见页 瓦片队列 —— 动画期只做 1/3 倍频程量化的近似键，落定后按精确倍率补齐。</summary>
    private void FinishZoom()
    {
        _isSmoothZooming = false;
        _zoomFromRightDrag = false;
        StopZoomApplyTimer();               // 停掉右键拖拽的下发定时器，避免与提交竞态/残留空转
        _lastZoomEndTime = DateTime.UtcNow;
        _zoomLog.SnapToTarget();

        // RenderCache（1:1 Sumatra）：缩放落定 = zoomReal 变化。旧 zoom 的瓦片留作替身
        double zt = _zoomAnimTargetFactor;
        // 目标偏移按【落定时刻】的视口/内容尺寸重解：动画期间窗口可能被拉伸过。
        // 用预测视口解目标 → 目标与落地状态自洽，滚动条跳变不再让画布居中量跳变（起止闪错位帧的根因）。
        PredictViewport(zt, out double pvW, out double pvH);
        _vpW = pvW;
        _vpH = pvH;
        ResolveZoomOffsets(zt, out _zoomAnimTargetH, out _zoomAnimTargetV);
        _zoomAnimCurrentFactor = zt;
        _zoomAnimCurrentH = _zoomAnimTargetH;
        _zoomAnimCurrentV = _zoomAnimTargetV;

        SetVisRect(_zoomAnimTargetH, _zoomAnimTargetV, zt);
        CommitViewWithGuard(_zoomAnimTargetH, _zoomAnimTargetV, zt);
        SyncLiveView(_zoomAnimTargetH, _zoomAnimTargetV, zt);
        QueueVisiblePages();
        InvalidateBoth();
        NotifyViewState();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        StopFramePumpIfIdle();
    }

    private void StopSmoothZoom()
    {
        if (!_isSmoothZooming) return;
        _isSmoothZooming = false;
        _zoomFromRightDrag = false;
        StopZoomApplyTimer();               // 停掉右键拖拽的下发定时器，避免与提交竞态/残留空转
        _lastZoomEndTime = DateTime.UtcNow;
        _zoomLog.Reset(_zoomLog.Value);
        // 关键：落定到【当前插值态】而非目标态——否则快速双击/三连击时，每次按下(PointerPressed)
        double zp = _zoomAnimCurrentFactor, hp = _zoomAnimCurrentH, vp = _zoomAnimCurrentV;
        if (zp <= 0) zp = _liveZoom > 0 ? _liveZoom : 1.0;
        SetVisRect(hp, vp, zp);
        CommitViewWithGuard(hp, vp, zp); // 带兜底：视图无变化时不开提交窗口，避免标志卡死锁死输入
        SyncLiveView(hp, vp, zp);
        QueueVisiblePages();
        InvalidateBoth();
        NotifyViewState();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        StopFramePumpIfIdle();
    }

    /// 滚动 / 直接跳页接管视图前：关闭可能仍开着的缩放提交窗口。
    private void AbandonZoomCommitWindow()
    {
        _zoomAnimCommitting = false;
        if (_liveZoom > 0)
        {
            _zoomAnimCurrentFactor = _liveZoom;
            _zoomAnimCurrentH = _liveH;
            _zoomAnimCurrentV = _liveV;
        }
        else
        {
            _zoomAnimCurrentFactor = Viewer.ZoomFactor > 0 ? Viewer.ZoomFactor : 1.0;
            _zoomAnimCurrentH = Viewer.HorizontalOffset;
            _zoomAnimCurrentV = Viewer.VerticalOffset;
        }
    }

    /// <summary>起程时把滚动弹簧播种到当前真实位置。用 _live*（权威实时镜像）而非 Viewer.*： 刚落定的缩放/拖拽其 ChangeView 还没异步落地，读 Viewer.* 会拿到上一帧的旧偏移， 于是第一档滚轮会先"倒退一下"再前进 —— 这正是短距离连续滚动里最刺眼的那一跳。</summary>
    private void EnsureScrollSprings()
    {
        if (_isWheelScrolling) return;
        double h0 = _liveZoom > 0 ? _liveH : Viewer.HorizontalOffset;
        double v0 = _liveZoom > 0 ? _liveV : Viewer.VerticalOffset;
        _scrollH.Reset(h0);
        _scrollV.Reset(v0);
        _vpW = Viewer.ViewportWidth;
        _vpH = Viewer.ViewportHeight;
        if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
        _isWheelScrolling = true;
    }

    /// <summary>把一次滚轮位移并入滚动目标。只改目标、【不碰速度】：连发滚轮时目标持续向前延伸， 弹簧的速度是连续累积上去的 → 一条越滚越快、松手后平滑收住的曲线。 旧的 lerp 每来一档都从 0 重新起速再急停，速度曲线是一串锯齿，那就是"跳跃感"的来源。</summary>
    private void AddWheelScroll(double dh, double dv)
    {
        // 滚动与缩放动画互斥：缩放进行中收到滚动输入，先【落定到当前插值态】再滚动
        // （交互第一原则：用户随时可打断动画）。否则缩放与滚动两套引擎抢 Viewer.ChangeView
        if (_isSmoothZooming) StopSmoothZoom();
        // 关闭可能仍开着的缩放提交窗口：缩放刚落定(ChangeView 未落地)或动画进行中都被滚动打断时，
        AbandonZoomCommitWindow();
        EnsureScrollSprings();
        // 范围自算（同 GetScrollBounds 注释）：缩放刚落定、ChangeView 未落地时读 Viewer.Scrollable*
        GetScrollBounds(CurrentZoom, out double maxH, out double maxV, _vpW, _vpH);
        _scrollH.Target = Math.Clamp(_scrollH.Target + dh, 0, maxH);
        _scrollV.Target = Math.Clamp(_scrollV.Target + dv, 0, maxV);
        EnsureFramePump();
    }

    private void StopWheelScroll()
    {
        if (!_isWheelScrolling) return;
        _isWheelScrolling = false;
        _scrollH.Reset(_scrollH.Value);
        _scrollV.Reset(_scrollV.Value);
        StopFramePumpIfIdle();
    }

    /// <summary>滚动帧：两轴弹簧各推进一步并下发视图。返回是否需要重画画布。</summary>
    private bool StepScrollFrame(double dt)
    {
        // 目标可能因窗口尺寸/缩放变化而越界，每帧夹一次（弹簧只负责跟随，不负责边界）。
        double zCur = CurrentZoom;
        GetScrollBounds(zCur, out double maxH, out double maxV, _vpW, _vpH);
        _scrollH.Target = Math.Clamp(_scrollH.Target, 0, maxH);
        _scrollV.Target = Math.Clamp(_scrollV.Target, 0, maxV);

        _scrollH.Step(dt, ScrollSpringOmega);
        _scrollV.Step(dt, ScrollSpringOmega);

        bool doneH = _scrollH.IsSettled(ScrollSettlePx, ScrollSettleVel);
        bool doneV = _scrollV.IsSettled(ScrollSettlePx, ScrollSettleVel);
        if (doneH) _scrollH.SnapToTarget();
        if (doneV) _scrollV.SnapToTarget();

        double h = _scrollH.Value, v = _scrollV.Value;
        double z = zCur;

        SyncLiveView(h, v, z);                                   // 画布按自管偏移绘制，零帧延迟
        // 关键：必须显式带上当前权威缩放 z（而非 null="保持当前倍率"）。
        // 拽回缩放停止处(hp,vp)而非滚动目标 → "动画结束位置飞到不知哪"的根因。带上 z 让缩放与偏移
        Viewer.ChangeView(h, v, (float)z, disableAnimation: true);
        SetVisRect(h, v, z);
        // 滚动不改变缩放 → 缓存键（页 + res + 瓦片坐标）不变，已有瓦片仍精确，
        QueueVisiblePages();
        NotifyViewState();
        ViewportChanged?.Invoke(this, EventArgs.Empty);

        if (doneH && doneV) FinishScroll();
        return true;
    }

    private void FinishScroll()
    {
        if (!_isWheelScrolling) return;
        _isWheelScrolling = false;
        QueueVisiblePages();
        StopFramePumpIfIdle();
    }

    // ================= 帧泵：Win2D 交换链 vsync 自持续循环（滚动与缩放共用） =================

    /// <summary>触发首帧 Draw，之后由 Draw 末尾的自持续 Invalidate 维持。模拟步进与"上屏"在同一个 Draw 调用里 完成，节奏完全由 Win2D 交换链 vsync 决定（60/120/144Hz 各按真实刷新率），且当帧步进值当帧即上屏，</summary>
    private void EnsureFramePump()
    {
        if (_framePumpHooked) return;
        _framePumpHooked = true;
        _lastDrawTicks = 0; // 首帧 Draw 用 1/60 作 dt，不打断连续输入
        _lastFrameTicks = Stopwatch.GetTimestamp();

        // 看门狗：窗口被完全遮挡/最小化时 Win2D 也会停发 Draw，动画就冻在半路，
        _framePumpWatchdog ??= CreateFramePumpWatchdog();
        _framePumpWatchdog.Start();

        // 触发首帧 Draw；之后由 Draw 末尾的自持续 Invalidate 维持，节奏 = Win2D 交换链 vsync
        InvalidateBoth();
    }

    private DispatcherTimer CreateFramePumpWatchdog()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        t.Tick += (_, _) =>
        {
            if (!_framePumpHooked) return;
            double idleMs = (Stopwatch.GetTimestamp() - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency;
            if (idleMs < 240) return;
            if (_isSmoothZooming && !_isRightZooming) FinishZoom();
            if (_isWheelScrolling) { _scrollH.SnapToTarget(); _scrollV.SnapToTarget(); FinishScroll(); }
            StopFramePump();
        };
        return t;
    }

    private void StopFramePump()
    {
        if (!_framePumpHooked) return;
        _framePumpHooked = false;
        _framePumpWatchdog?.Stop();
    }

    /// <summary>没有任何运动在跑就摘掉帧回调（不空转、不吃电）。</summary>
    private void StopFramePumpIfIdle()
    {
        if (_isSmoothZooming || _isWheelScrolling) return;
        StopFramePump();
    }

    /// 取两帧 Draw 之间的真实耗时（秒）作为模拟 dt。
    private double ComputeFrameDt()
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (_lastDrawTicks == 0)
            ? (1.0 / 60.0)
            : (now - _lastDrawTicks) * 1.0 / Stopwatch.Frequency;
        _lastDrawTicks = now;
        if (dt <= 0) dt = 1.0 / 60.0;
        if (dt > 0.05) dt = 0.05;
        return dt;
    }

    /// 子级(透明 Spacer)上的滚轮阻断器：在事件冒泡到 ScrollViewer 之前把事件标为已处理，
    private void Viewer_PointerWheelBlocker(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void Viewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e) => HandlePointerWheel(e);

    /// <summary>捕获期兜底：拉线期间 this 持着指针捕获，wheel 被投到捕获元素（不在 Viewer 的冒泡路径上），挂 Viewer 的处理器收不到。
    /// 只接管拉线 —— 卡片拖不出绑定页，拖动中的滚轮没有意义；其余手势与编辑框原生滚动一律不管。</summary>
    private void Root_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (e.Handled) return;                       // 未捕获时已由 Viewer 路径处理，避免重复消费
        if (!_isCanvasConnecting) return;
        HandlePointerWheel(e);
    }

    private void HandlePointerWheel(PointerRoutedEventArgs e)
    {
        CancelPendingRestore(); // 用户滚轮（滚动/Ctrl缩放）优先于未落定的还原，防止回拽
        var props = e.GetCurrentPoint(Viewer).Properties;
        double delta = props.MouseWheelDelta;

        if (props.IsHorizontalMouseWheel)
        {
            AddWheelScroll(delta * WheelScrollFactor, 0);
            e.Handled = true;
            return;
        }

        // Ctrl+滚轮 → 平滑缩放（等比步进）；先停掉进行中的滚轮滚动。
        if ((e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0)
        {
            StopWheelScroll();
            // 基准取【当前目标】而非 Viewer.ZoomFactor：动画进行中 Viewer.ZoomFactor 是陈旧值，
            double baseZoom = _isSmoothZooming ? _zoomAnimTargetFactor
                                               : (_liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor);
            if (baseZoom <= 0) baseZoom = 1.0;
            double next = baseZoom * (1.0 + delta / 1000.0 * CtrlWheelZoomStrength) * 100.0;
            StartSmoothZoom(Math.Clamp(next, MinZoomPercent, MaxZoomPercent), null);
            e.Handled = true;
            return;
        }

        // 白板：选中态 / 编辑态下指针停在"内容溢出的卡片"上 → 滚轮滚【卡片内容】而非翻页
        // （手势进行中一律让位：拖卡片时指针就在被拖卡上，滚内容会让"滚动翻页"失效；
        //   触发键待接态同理必须让位 —— 它的全部意义就是"滚到另一页去点终点"，不能停在卡片里滚内容）
        if (!AnyCanvasGestureActive && !_connArmActive && CanvasInputActive &&
            (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) == 0 &&
            TryScrollCanvasByWheel(e.GetCurrentPoint(Viewer).Position, delta))
        {
            e.Handled = true;
            return;
        }

        // 普通滚轮 → 并入滚动弹簧目标；缩放动画进行中先停掉。
        if (_isSmoothZooming) StopSmoothZoom();

        // 轴向路由（横/纵布局统一走自管临界阻尼弹簧，与双击/右键缩放、横向滚轮同一引擎）。
        //   • 可见内容(GpuPageView 的 CanvasControl)是 ScrollViewer 的【兄弟】，原生滚轮只会移动透明
        bool crossAxis = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0;
        double step = delta * WheelScrollFactor;
        if (IsHorizontalLayout)
        {
            if (crossAxis) AddWheelScroll(0, step);
            else           AddWheelScroll(step, 0);
        }
        else
        {
            // 纵向：普通滚轮走垂直主轴；Shift 走水平交叉轴。都走自管弹簧，不再交还原生。
            if (crossAxis) AddWheelScroll(step, 0);
            else           AddWheelScroll(0, step);
        }

        e.Handled = true;
    }

    public double ZoomPercent => Viewer.ZoomFactor * 100.0;

    /// "适应视口"对应的缩放百分比 —— 始终作用于【交叉轴】（非滚动方向）， 使单页在该方向完整可见、只沿主轴滚动： 纵向连续 → 内容宽 → 视口宽（即传统的"适应宽度"） 横向连续 → 内容高 → 视…
    public double FitToViewportPercent
    {
        get
        {
            if (IsHorizontalLayout)
            {
                double contentH = Spacer.Height;
                if (double.IsNaN(contentH) || contentH <= 0 || Viewer.ViewportHeight <= 0) return 100;
                return Math.Clamp(Viewer.ViewportHeight / contentH, MinZoom, MaxZoom) * 100.0;
            }
            double contentW = Spacer.Width;
            if (double.IsNaN(contentW) || contentW <= 0 || Viewer.ViewportWidth <= 0) return 100;
            return Math.Clamp(Viewer.ViewportWidth / contentW, MinZoom, MaxZoom) * 100.0;
        }
    }

    /// <summary>适应宽度对应的缩放百分比（保留旧名，语义已按布局主轴泛化）。</summary>
    public double FitWidthPercent => FitToViewportPercent;

    public void FitWidth(bool animate = true) => SetZoomPercent(FitToViewportPercent, animate);

    /// 宿主视口尺寸变化（侧边栏 Inline 挤开主视图）【之前】同步冻结"阅读位置不变量"。
    /// 必须在翻转 IsPaneOpen / 改列宽之前调用：列宽在过渡起点(KeyTime=0)即变，事后读不到旧几何。
    public void CaptureHostResizeAnchor()
    {
        double z0 = CurrentZoom;
        double vpW0 = Viewer.ViewportWidth, vpH0 = Viewer.ViewportHeight;
        if (z0 <= 0 || vpW0 <= 0 || vpH0 <= 0) return;
        double h0 = _liveZoom > 0 ? _liveH : Viewer.HorizontalOffset;
        double v0 = _liveZoom > 0 ? _liveV : Viewer.VerticalOffset;
        GetCenterOffsets(z0, out double cx0, out double cy0, vpW0, vpH0);

        _hostResizeZ0 = z0;
        _hostResizeVpW0 = vpW0;
        _hostResizeDocLeftX = (h0 - cx0) / z0;
        _hostResizeDocTopY = (v0 - cy0) / z0;
        _hostResizeCaptureValid = true;
    }

    /// 宿主几何已生效后调用（侧边栏过渡起点之后一帧）：把阅读位置不变量当作**固定锚点**， 用弹簧把 zoom 平滑推到"主轴视口新旧比例"的目标值（而不是瞬时落定）。
    /// 【语义】两种布局一律 —— **三条边线不动，只有下边内容变化**：
    /// ⚠ 缩放比不随布局方向变：横向连续同样锁左右边线（与"适应视口"按 vpH 是两回事，别混）。
    public void BeginHostResizeReanchor()
    {
        if (!_hostResizeCaptureValid) return;
        _hostResizeCaptureValid = false;   // 用掉即清：一次快照只对应一次重锚
        var pages = _pages;
        if (pages.Count == 0) return;

        double z0 = _hostResizeZ0, vpW0 = _hostResizeVpW0;
        // 快照不全（采集时几何还没排好）⇒ 这次重锚不可信，宁可不动。
        if (z0 <= 0 || vpW0 <= 0) return;

        // 列宽已由 SplitView 在过渡起点离散改到最终值，但 Viewport* 要等这一轮布局才刷新 → 强制刷新。
        try { Viewer.UpdateLayout(); } catch { }

        double vpW1 = Viewer.ViewportWidth, vpH1 = Viewer.ViewportHeight;
        if (vpW1 <= 0 || vpH1 <= 0) return;
        // 【唯一判据】缩放比恒按 **vpW**（两种布局都一样）：要钉住的是「左右边线」，而左右边线
        // ⚠ 别与"适应视口"混：横向连续的 FitToViewportPercent 按【高度】（适应高度）是另一套语义；
        double mainOld = vpW0;
        double mainNew = vpW1;
        if (mainNew <= 0 || Math.Abs(mainNew - mainOld) <= 0.5) return;

        float z1 = (float)Math.Clamp(z0 * (mainNew / mainOld), MinZoom, MaxZoom);

        // 画布绘制与居中量都按新视口算：SplitView 之后那 0.15s 只是渲染滑动，布局不再变。
        // （必须先换，下面的 ResolveZoomOffsets 用的是这两个快照。）
        _vpW = vpW1;
        _vpH = vpH1;

        // 进行中的缩放手势必须作废：它的帧泵会拿自管的 _zoomAnimCurrent* 覆盖下面播种的锚点状态。
        StopSmoothZoom();
        _zoomAnimCommitting = false;

        // 锚点 = 视口【左缘 × 上缘】= (0,0)：两条要钉的边线本身就取作锚点，
        _zAnimAnchor = new Point(0, 0);
        _zAnimContentX = _hostResizeDocLeftX;
        _zAnimContentY = _hostResizeDocTopY;
        _zoomAnimTargetFactor = z1;
        ResolveZoomOffsets(z1, out double h1, out double v1);

        // 稳定窗口：改几何 + 提交期间 ViewChanged 回读到的是被旧 extent 夹断的中间值（常为 0），
        _layoutSwitchInFlight = true;
        _layoutSwitchAnchor = Math.Clamp(CurrentPageIndex, 0, pages.Count - 1);
        _layoutSwitchStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        StopLayoutSwitchWatchdog();
        _layoutTargetH = h1;
        _layoutTargetV = v1;
        _layoutTargetZoom = z1;

        // 弹簧：用代码里现成的"快"档（右键拖拽缩放那档，≈165ms），与侧边栏 0.15s 滑窗同一量级。
        BeginZoomGesture(z0, z1, RightZoomSpringOmega, fromRightDrag: false);
        _zoomOmega = RightZoomSpringOmega;
        _isSmoothZooming = true;
        EnsureFramePump();
        // 起手先把显示态落到锚点当前帧（z=z₀ 处）：不画的话首个帧泵回调前，画布仍可能拿旧视口的 _vis*。
        SetVisRect(_liveH, _liveV, z0);
        SyncLiveView(_liveH, _liveV, z0);
        InvalidateBoth();
        NotifyViewState();
        StartLayoutSwitchWatchdog();
    }

    public new void ActualSize(bool animate = true) => SetZoomPercent(100, animate);

    public double VerticalOffset => Viewer.VerticalOffset;

    public double ScrollableHeight => Viewer.ScrollableHeight;

    public double ViewportHeight => Viewer.ViewportHeight;

    public double HorizontalOffset => Viewer.HorizontalOffset;

    public double ScrollableWidth => Viewer.ScrollableWidth;

    public double ViewportWidth => Viewer.ViewportWidth;

    public void SetVerticalOffset(double offset, bool animate = false)
    {
        double v = Math.Max(0, offset);
        Viewer.ChangeView(null, v, null, !animate);
        // 另一轴与缩放取权威镜像，不读 Viewer.*：缩放提交尚未落地时它们仍是旧值，
        SyncLiveView(_liveH, v, CurrentZoom);
    }

    public void SetHorizontalOffset(double offset, bool animate = false)
    {
        double h = Math.Max(0, offset);
        Viewer.ChangeView(h, null, null, !animate);
        SyncLiveView(h, _liveV, CurrentZoom);
    }

    /// <summary>取当前视图快照，供退出/切换文档时持久化。 平滑缩放或拖拽平移进行中返回插值后的实时状态，避免依赖 ChangeView 异步落点造成的 1 帧错位。</summary>
    public void GetCurrentViewState(out double zoomPercent, out double horizontalOffset, out double verticalOffset, out int pageIndex)
    {
        // 直接返回实时镜像：窗口隐藏/卸载时 Viewer.VerticalOffset 会被布局清零，不能读它。
        zoomPercent = _liveZoom * 100.0;
        horizontalOffset = _liveH;
        verticalOffset = _liveV;
        pageIndex = CurrentPageIndex;
    }

    /// <summary>待还原的阅读状态（LoadAsync 时视口可能尚未就绪，排队等首次适应宽度成功后应用）。</summary>
    private ViewStateData? _pendingRestore;

    /// <summary>还原重试计数：滚动范围未就绪时由 Low 优先级调度自行重试，不依赖用户交互。</summary>
    private int _restoreRetries;
    private const int RestoreRetryMax = 24;

    /// <summary>排队还原：若视图已首次适配（_didInitialFit 且视口就绪）则立即还原； 否则由首次 TryInitialFit（视口就绪后）应用——确保不被「适应宽度」覆盖。</summary>
    public void RestoreView(ViewStateData vs)
    {
        if (vs == null) return;
        // 布局必须在偏移之前还原（SavedAtUtc 注释已说明）：重建 Spacer 尺寸，使后续偏移按正确主轴对称落定。
        if (_pages.Count > 0)
        {
            bool layoutChanged = false;
            var lm = (PageLayoutMode)vs.LayoutMode;
            if (lm != _layoutMode) { _layoutMode = lm; layoutChanged = true; }
            var ar = (PageArrangement)vs.Arrangement;
            if (ar != _arrangement) { _arrangement = ar; layoutChanged = true; }
            if (layoutChanged)
            {
                // 布局必须在偏移之前还原（SavedAtUtc 注释已说明）：重建 Spacer 尺寸，使后续偏移按正确布局落定。
                RecomputeLayout(_pages);
                LayoutModeChanged?.Invoke(this, lm);
                ArrangementChanged?.Invoke(this, ar);
            }
        }
        _pendingRestore = vs;
        // 已首次适配（文档已展示、缩放/偏移可安全应用）→ 立即精确还原。
        // 尚未首次适配（正在 LoadAsync 后布局，_didInitialFit 仍为 false）→ 仅排队，
        if (_didInitialFit)
            TryApplyPendingRestore();
    }

    /// 尝试精确还原保存的缩放/偏移/旋转（瞬时，无动画）。
    private void TryApplyPendingRestore()
    {
        if (_pendingRestore == null) return;
        if (Viewer.ViewportWidth <= 0) { ScheduleRestoreRetry(); return; }

        // 用户正在交互：此时强行落定会与平滑缩放/拖拽动画打架 → 顺延到交互结束后再试（重试受上限约束）。
        if (_isSmoothZooming || _isWheelScrolling || _isDragPanning || _isRightZooming)
        {
            ScheduleRestoreRetry();
            return;
        }

        var vs = _pendingRestore;
        float target = (float)Math.Clamp(vs.ZoomPercent / 100.0, MinZoom, MaxZoom);
        double h = Math.Max(0, vs.HorizontalOffset);
        double v = Math.Max(0, vs.VerticalOffset);

        // 校验：视口已精确落定到目标（异步 ChangeView 已生效、无后续回拽）→ 直接收尾清除待还原。
        if (Math.Abs(Viewer.ZoomFactor - target) <= 0.002 &&
            Math.Abs(Viewer.HorizontalOffset - h) <= 1.0 &&
            Math.Abs(Viewer.VerticalOffset - v) <= 1.0)
        {
            _pendingRestore = null;
            _restoreRetries = 0;
            return;
        }

        // 旋转优先：旋转改变布局坐标与 extent，须先于缩放/偏移。
        int rot = ((vs.RotationDegrees % 360) + 360) % 360;
        if (rot != _rotation && _pages.Count > 0)
        {
            _rotation = rot;
            RecomputeLayout(_pages);
        }

        // 阶段1：先升到目标缩放以扩展滚动范围。当前缩放与目标不同 → 仅设 zoom（偏移留到阶段2），
        if (Math.Abs(Viewer.ZoomFactor - target) > 0.001)
        {
            Viewer.ChangeView(null, null, target, disableAnimation: true);
            SyncLiveView(h, v, target); // 画布/双击起点立即按还原位置呈现（ScrollViewer 暂在顶部，不可见）
            // 同步播种动画当前态：还原从不给 _zoomAnimCurrent* 赋值，避免首帧双击读到初始化残留的 0 → 闪到顶部。
            _zoomAnimCurrentFactor = target;
            _zoomAnimCurrentH = h;
            _zoomAnimCurrentV = v;
            SetVisibleRangeFor(h, v, target); // 按还原值设可见范围（不读滞后的 Viewer.*），光栅化正确页面
            QueueVisiblePages();
            InvalidateBoth();
            if (_restoreRetries < RestoreRetryMax)
            {
                _restoreRetries++;
                ScheduleRestoreRetry(); // 布局完成后(低优先级)回到阶段2精确落定偏移
            }
            else
            {
                // 极端：重试耗尽仍未精确落定，也【必须】把缩放+偏移真正落下去——否则 ScrollViewer 停在顶端、
                Viewer.ChangeView(h, v, target, disableAnimation: true);
                SyncLiveView(h, v, target);
                _pendingRestore = null; _restoreRetries = 0;
            } // 极端：放弃精确重试，但已落定视图
            return;
        }

        bool rangeReady =
            h <= Viewer.ScrollableWidth + 1.0 &&
            v <= Viewer.ScrollableHeight + 1.0;
        if (!rangeReady)
        {
            SyncLiveView(h, v, target); // 画布/双击起点在还原窗口内即按还原位置呈现（ScrollViewer 暂不动）
            _zoomAnimCurrentFactor = target;
            _zoomAnimCurrentH = h;
            _zoomAnimCurrentV = v;
            SetVisibleRangeFor(h, v, target);
            QueueVisiblePages();
            InvalidateBoth();
            if (_restoreRetries < RestoreRetryMax)
            {
                _restoreRetries++;
                ScheduleRestoreRetry(); // 等布局完成后的 Low 调度再试，绝不依赖用户交互
            }
            else
            {
                // 极端：重试耗尽仍未精确落定，也把缩放+偏移真正落下去（理由同上：停在顶端会污染 _live* 致重开飞左上角）。
                Viewer.ChangeView(h, v, target, disableAnimation: true);
                SyncLiveView(h, v, target);
                _pendingRestore = null; _restoreRetries = 0;
            } // 文档变短等极端：放弃精确重试，但已落定视图
            return;
        }

        Viewer.ChangeView(h, v, target, disableAnimation: true);
        SyncLiveView(h, v, target);
        // 同步播种动画当前态：还原从不给 _zoomAnimCurrent* 赋值，避免首帧双击读到初始化残留的 0 → 闪到顶部。
        _zoomAnimCurrentFactor = target;
        _zoomAnimCurrentH = h;
        _zoomAnimCurrentV = v;

        // 刷新可见范围与重光栅队列，确保还原后立即正确绘制（按还原值设，不读滞后的 Viewer.*）。
        SetVisibleRangeFor(h, v, target);
        QueueVisiblePages();
        InvalidateBoth();

        // 【不立即清除 _pendingRestore】交给上方"校验重试"在视口真正落定后清除——
        // 否则异步 fit/失焦等 ChangeView 可能在还原之后又把视口拽回首页/适应宽度
        if (_restoreRetries < RestoreRetryMax)
        {
            _restoreRetries++;
            ScheduleRestoreRetry();
        }
        else
        {
            _pendingRestore = null;
            _restoreRetries = 0;
        }
    }

    /// <summary>用户交互优先：一旦用户开始操作（双击/滚动/拖拽/缩放），取消未落定的还原， 避免还原在用户操作之后"回拽"视图到保存位置。</summary>
    private void CancelPendingRestore()
    {
        _pendingRestore = null;
        _restoreRetries = 0;
    }

    /// <summary>Low 优先级重投 TryApplyPendingRestore：在 Layout/绘制之后运行， 此时 ScrollViewer 可滚动范围已随布局刷新，偏移可精确落定；且不依赖用户交互。</summary>
    private void ScheduleRestoreRetry()
        => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, TryApplyPendingRestore);

    /// <summary>关闭/换文档时调用：释放所有缓存位图与渲染队列，避免显存滞留。</summary>
    public void ResetView()
    {
        ShutdownRenderCache();
        _currentPage = -1;
        InvalidateBoth();
    }

    private int _rotation;

    public int RotationDegrees => _rotation;

    /// 顺时针旋转 90°。
    /// 放大态下几乎每一颗可见瓦片都变成"没渲染过的新瓦片"→ 全屏先发糊再变清晰（实测症状）。
    public void RotateClockwise()
    {
        var pages = _pages;
        if (pages.Count == 0) return;
        if (_isSmoothZooming) StopSmoothZoom();   // 与 GoToPage 同：先落定插值态，避免与缩放引擎抢视图

        int anchor = CurrentPageIndex;
        double z = _liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor;
        if (z <= 0) z = 1.0;

        // 旋转前：记录【视口中心】在锚定页内的相对位置（0..1，未旋转页帧）。
        bool haveAnchor = ComputeViewAnchor(anchor, z, out double fx, out double fy);

        _rotation = (_rotation + 90) % 360;
        RecomputeLayout(pages);

        if (haveAnchor && RestoreViewAnchor(anchor, z, fx, fy, out double hOff, out double vOff))
        {
            _scrollH.Reset(hOff);
            _scrollV.Reset(vOff);
            SyncLiveView(hOff, vOff, z);
            AbandonZoomCommitWindow();
            // 缩放+偏移原子落地（z 非 null）；随后立即按目标播种 _vis*，不等 ChangeView 异步回吐
            // （否则 QueueVisiblePages 会按旧视口算可见页 → 排了非可见页、漏了可见页 = 白重出）。
            Viewer.ChangeView(hOff, vOff, (float)z, disableAnimation: true);
            SetVisibleRangeFor(hOff, vOff, z);
        }
        else
        {
            // 视口中心不在锚定页内（跨页缝/页外）→ 退回 Sumatra 的页首对齐；其内部已播种可见范围。
            GoToPage(anchor);
            return;
        }
        QueueVisiblePages();
        InvalidateBoth();
    }

    /// <summary>记录【视口中心】对应的页内相对位置 (0..1，未旋转页帧)。几何与 ComputeVisiblePages / ITileRenderHost.PageOnScreen 同式 —— 三者必须同源，否则锚点与绘制/请求的页帧对不上。</summary>
    private bool ComputeViewAnchor(int pageNo, double z, out double fx, out double fy)
    {
        fx = fy = 0.5;
        var pages = _pages;
        if (pageNo < 0 || pageNo >= pages.Count) return false;

        double vpW, vpH, hOff, vOff;
        lock (_lock) { vpW = _vpW; vpH = _vpH; hOff = _liveH; vOff = _liveV; }
        if (vpW <= 0 || vpH <= 0) return false;

        double extentW = Spacer.Width * z, extentH = Spacer.Height * z;
        double centerX = extentW < vpW ? (vpW - extentW) / 2.0 : 0;
        double centerY = extentH < vpH ? (vpH - extentH) / 2.0 : 0;
        var lr = pages[pageNo].LayoutRect;
        var dest = new Rect(centerX + lr.X * z - hOff, centerY + lr.Y * z - vOff,
            lr.Width * z, lr.Height * z);
        var un = PageUnrotatedRect(dest);
        if (un.Width <= 0 || un.Height <= 0) return false;

        fx = (vpW / 2.0 - un.X) / un.Width;
        fy = (vpH / 2.0 - un.Y) / un.Height;
        return fx >= 0 && fx <= 1 && fy >= 0 && fy <= 1;
    }

    /// <summary>反解偏移：让未旋转页矩形里相对位置 (fx, fy) 的点落在视口中心（= 旋转前后同一页点 原地不动）。坐标全部用【未旋转页帧】的页尺寸（旋转只交换呈现宽高，页内容尺寸不变）。</summary>
    private bool RestoreViewAnchor(int pageNo, double z, double fx, double fy,
        out double hOff, out double vOff)
    {
        hOff = vOff = 0;
        var pages = _pages;
        if (pageNo < 0 || pageNo >= pages.Count) return false;

        double vpW, vpH;
        lock (_lock) { vpW = _vpW; vpH = _vpH; }
        if (vpW <= 0 || vpH <= 0) return false;

        var p = pages[pageNo];
        double extentW = Spacer.Width * z, extentH = Spacer.Height * z;
        double centerX = extentW < vpW ? (vpW - extentW) / 2.0 : 0;
        double centerY = extentH < vpH ? (vpH - extentH) / 2.0 : 0;

        // 未旋转页帧的页尺寸（DIP）：旋转不改变页内容尺寸，只是呈现时被交换。
        double uw = p.WidthPoints * 96.0 / 72.0 * z;
        double uh = p.HeightPoints * 96.0 / 72.0 * z;
        if (uw <= 0 || uh <= 0) return false;

        double needCenterX = vpW / 2.0 - (fx - 0.5) * uw;
        double needCenterY = vpH / 2.0 - (fy - 0.5) * uh;
        var lr = p.LayoutRect;
        hOff = Math.Max(0, centerX + lr.X * z + lr.Width * z / 2.0 - needCenterX);
        vOff = Math.Max(0, centerY + lr.Y * z + lr.Height * z / 2.0 - needCenterY);
        return true;
    }

    // ---------------- 标注叠加层（五模式移植） ----------------
    // 设计：数据/保存/撤销全部留在 MainPage（复用旧路径的 AnnotationViewModel 管线），

    public bool AnnotationInputActive
    {
        get => _annotationInputActive;
        set
        {
            if (_annotationInputActive == value) return;
            _annotationInputActive = value;
        }
    }
    private bool _annotationInputActive;

    /// <summary>标注叠加层可见性（默认 true，恒显示）。隐藏只影响显示，数据模型与输入管线不变； 不持久化 —— 重新打开文档/应用后回到默认显示。切换即时重画。</summary>
    public bool AnnotationsVisible
    {
        get => _annotationsVisible;
        set
        {
            if (_annotationsVisible == value) return;
            _annotationsVisible = value;
            // G 键隐藏白板层时，选中框/浮动控件/编辑框必须一起收起：否则卡片看不见了，
            if (!value) HideCanvasInteraction();
            InvalidateOverlay();
        }
    }
    private bool _annotationsVisible = true;

    public Func<int, IReadOnlyList<AnnotationViewModel>?>? AnnotationProvider { get; set; }

    // ---------- 画笔渲染性能 & 稳定性修复：节流刷新 + 几何缓存 + 笔画累积层 ----------
    private static readonly int AnnoRefreshIntervalMs = 8;   // 120Hz 适应：8ms≈125fps，被 vsync 封顶到120
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _annoRefreshTimer;
    private long _lastAnnoRefreshTicks;
    private bool _annoRefreshPending;
    private readonly object _annoRefreshLock = new();

    /// <summary>节流版 InvalidateOverlay：8ms 限速合并高频调用，速率最快 = 刷新率。 只刷 ScreenCanvas（页位图 + 标注同一交换链）。仅"笔画内容变化"走此增量路径。</summary>
    public void InvalidateOverlayThrottled()
    {
        // 限速判断：若距离上次实际刷新 >= 8ms 直接刷；否则挂起一个一次性定时器稍后放行。
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_annoRefreshLock)
        {
            if (_annoRefreshPending) return;
            double elapsedMs = (now - _lastAnnoRefreshTicks)
                               * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (elapsedMs >= AnnoRefreshIntervalMs)
            {
                _lastAnnoRefreshTicks = now;
            }
            else
            {
                _annoRefreshPending = true;
                _annoRefreshTimer ??= DispatcherQueue?.CreateTimer();
                if (_annoRefreshTimer != null)
                {
                    _annoRefreshTimer.Interval = TimeSpan.FromMilliseconds(AnnoRefreshIntervalMs);
                    _annoRefreshTimer.IsRepeating = false;
                    _annoRefreshTimer.Tick -= OnAnnoRefreshTick;
                    _annoRefreshTimer.Tick += OnAnnoRefreshTick;
                    _annoRefreshTimer.Start();
                }
                return;
            }
        }
        try { ScreenCanvas.Invalidate(); } catch { }
    }

    private void OnAnnoRefreshTick(object? sender, object e)
    {
        lock (_annoRefreshLock)
        {
            _annoRefreshPending = false;
            _lastAnnoRefreshTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        try { ScreenCanvas.Invalidate(); } catch { }
    }

    public void InvalidateOverlay()
    {
        _annoLayerNeedsFullRebuild = true;
        // 墨集合可能整批换过（载入 / 换画布 / 撤销）：有墨页索引下次绘制重扫一遍。
        _inkPagesDirty = true;
        try { ScreenCanvas.Invalidate(); } catch { }
    }

    /// <summary>只重绘合成结果：已烘好的笔迹层原样复用，不重新栅格化笔迹。
    /// 白板卡片/连线/浮层的改动都不改变笔迹内容，走这条即可；走 InvalidateOverlay 会把整屏笔迹重画一遍。</summary>
    private void InvalidateComposite()
    {
        try { ScreenCanvas.Invalidate(); } catch { }
    }

    /// <summary>刷新页位图层与标注层（滚动/缩放/旋转/主题/尺寸等视口变化时用）。整层重建标注。 标注与页位图共用 ScreenCanvas 单交换链 → 双画布相位脱节不再存在。</summary>
    private void InvalidateBoth()
    {
        _annoLayerNeedsFullRebuild = true;
        ScreenCanvas.Invalidate();
    }

    // 单条标注的已编译 CanvasGeometry 缓存。
    private sealed class CachedAnnoGeometry
    {
        public int PointsVersion;
        public string? ColorHex;
        public double Thickness;
        public Microsoft.Graphics.Canvas.Geometry.CanvasGeometry? Geometry;
        public Windows.UI.Color ParsedColor;
        // 累积层已烘入的点数：当前笔画增量烘入时，只画 [BakingPoints, Count) 这一段，
        public int BakingPoints;
        // 笔的页局部包围盒（PDF 点，可越出页框）：判断"笔是否还压在视口里"用，按 PointsVersion 失效。
        public Rect Bounds;
        public bool HasBounds;
        public int BoundsVersion = -1;
    }
    // Key = AnnotationViewModel 的引用哈希（不重写 GetHashCode，用 RuntimeHelpers）
    // 用 ConditionalWeakTable 不用 Dictionary：标注被 GC 回收时缓存自动释放，不会内存泄漏
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<AnnotationViewModel, CachedAnnoGeometry>
        _annoGeoCache = new();

    // ---- 已完成笔画累积层：一次性烘入离屏位图，绘画时只实时画"当前一笔" ----
    private Microsoft.Graphics.Canvas.CanvasRenderTarget? _annoLayer;
    private bool _annoLayerNeedsFullRebuild = true;
    // 层里像素对应的视图参数（z 与 center−offset）。增量烘入只能往"按同一视图烘的层"里追加像素，
    // 否则新笔画会按新变换画进旧变换的层里（整层错位），所以每次增量前都要拿它比对。
    // _annoBaseZoom == 0 表示层里还没有可用像素（尚未烘焙过）。
    private double _annoBaseZoom;
    private double _annoBaseShiftX;
    private double _annoBaseShiftY;

    /// <summary><summary> </summary></summary>
    public double PenThicknessPoints
    {
        get => _penThicknessPoints;
        set => _penThicknessPoints = value;
    }
    private double _penThicknessPoints = 3.5;

    // 手势（右键拖拽缩放 / 左键标注笔画）是否正在进行（见各 Pointer 处理函数）。
    private void StartPenCursorHold()
    { }

    private void StopPenCursorHold()
    { }

    public event EventHandler<GpuAnnotationPointerArgs>? AnnotationPointerPressed;
    public event EventHandler<GpuAnnotationPointerArgs>? AnnotationPointerMoved;
    public event EventHandler<GpuAnnotationPointerArgs>? AnnotationPointerReleased;

    private bool _isAnnotating;
    private int _annotatingPage = -1;

    /// <summary>视口坐标 → 命中页 + 页局部坐标（PDF 点，未旋转）。指针不在任何页矩形内时返回 false。
    /// ⚠ 必须与【绘制】同一套视图参数（<see cref="GetViewState"/> + <see cref="_vpW"/><see cref="_vpH"/>）。
    /// Viewer.ZoomFactor/Offset 是被 ChangeView 异步落点的值，在平滑缩放收尾、滚轮镜像、拖拽平移
    /// 这些自管动画期间落后于屏幕实际画出来的变换 ⇒ 用它换算会让笔尖与光标错开，且要等下一次
    /// 滚动/缩放把 Viewer 对齐才回正。</summary>
    private bool TryViewportToPage(Point vp,
        out int pageIndex, out double px, out double py)
    {
        pageIndex = -1; px = 0; py = 0;
        var pages = _pages;
        if (pages.Count == 0) return false;

        GetViewState(out double z, out double hOff, out double vOff);
        if (z <= 0) return false;
        CanvasDocToLocal(vp, z, hOff, vOff, out double docX, out double docY);

        foreach (var p in pages)
        {
            if (docY >= p.LayoutRect.Top && docY <= p.LayoutRect.Bottom &&
                docX >= p.LayoutRect.Left && docX <= p.LayoutRect.Right)
            {
                pageIndex = p.Index;
                PageLocalFromDoc(p, docX, docY, out px, out py);
                return true;
            }
        }
        return false;
    }

    /// <summary>文档坐标(DIP) → 页局部坐标（PDF 点，未旋转坐标系）。旋转时先绕页中心逆旋回去。</summary>
    private void PageLocalFromDoc(GpuPageInfo p, double docX, double docY, out double px, out double py)
    {
        int rot = _rotation;
        double lx, ly;
        if (rot == 0)
        {
            lx = docX - p.LayoutRect.X;
            ly = docY - p.LayoutRect.Y;
        }
        else
        {
            // 逆旋转（-rot）绕 LayoutRect 中心，再相对未旋转矩形原点取局部坐标。
            double cx = p.LayoutRect.X + p.LayoutRect.Width / 2.0;
            double cy = p.LayoutRect.Y + p.LayoutRect.Height / 2.0;
            double rad = -rot * Math.PI / 180.0;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);
            double dx = docX - cx, dy = docY - cy;
            double qx = dx * cos - dy * sin;
            double qy = dx * sin + dy * cos;
            bool swap = rot == 90 || rot == 270;
            double uw = swap ? p.LayoutRect.Height : p.LayoutRect.Width; // 未旋转 DIP 宽高
            double uh = swap ? p.LayoutRect.Width : p.LayoutRect.Height;
            lx = qx + uw / 2.0;
            ly = qy + uh / 2.0;
        }
        // 页局部 DIP → PDF 点（布局 DIP = 点 × 96/72）。
        px = lx * 72.0 / 96.0;
        py = ly * 72.0 / 96.0;
    }

    private static readonly Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle PenStrokeStyle = new()
    {
        StartCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round,
        EndCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round,
        LineJoin = Microsoft.Graphics.Canvas.Geometry.CanvasLineJoin.Round,
    };

    /// 虚线：按住波浪键两次点击建线的"待接"引导（与实线预览区分：尚未落到任何一次拖拽手势上）。
    /// 【段长单位 = 线宽倍数，不是 DIP】：Win2D 把 CustomDashStyle 原样转交 D2D CreateStrokeStyle 的 dashArray，
    /// D2D 该参数按 stroke width 的倍数解释（dashOffset 文档亦写 "in units of stroke width"）。
    /// 内置 Dash 是 4 实 2 空，端帽又给每段两端各外扩 w/2 ⇒ 有效实段 5w、有效空隙只剩 1w；
    /// 缩到线宽下限 0.75 DIP 时空隙不足 1px，抗锯齿一糊就成实线。
    /// 故改 6 实 4 空：有效 7w 实 / 3w 空 —— 实段更长、空隙三倍于旧值，最小缩放下也数得清段数。
    private static readonly Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle DashStrokeStyle = new()
    {
        StartCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round,
        EndCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round,
        DashCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round,
        CustomDashStyle = new float[] { 6f, 4f },
    };

    /// <summary>设置"页点坐标(DIP) → 屏幕"的变换矩阵。 【必须以绝对值覆盖，不可与旧变换复合】：同一会话/渲染目标内会连续绘制多页（每页各调一次）， 若写成 m * ds.Transform，第二页的矩阵会叠加第一页的矩阵 → 该页笔画被二次缩放/平移，</summary>
    private void ApplyPageTransform(CanvasDrawingSession ds, Rect dest, double z)
    {
        ds.Transform = MakePageXform(dest, z).ToMatrix();
    }

    /// <summary>「页局部坐标（PDF 点）→ 屏幕坐标」的一次性变换。卡片用它整体作为会话变换， 白板连线用它的 逐点换算端点/控制点 —— 二者必须同源，否则连线端点会与卡片脱节（尤其旋转/缩放时）。</summary>
    private readonly struct PageXform
    {
        public readonly float S;
        public readonly Vector2 Origin;
        public readonly float Cx, Cy;
        public readonly float RotRad;
        public readonly bool HasRot;

        public PageXform(float s, Vector2 origin, float cx, float cy, float rotRad, bool hasRot)
        {
            S = s; Origin = origin; Cx = cx; Cy = cy; RotRad = rotRad; HasRot = hasRot;
        }

        /// <summary>页局部 PDF 点 → 屏幕坐标（DIP）。</summary>
        public Vector2 Map(double px, double py)
        {
            var v = new Vector2((float)(px * S) + Origin.X, (float)(py * S) + Origin.Y);
            return HasRot ? Rotate(v) : v;
        }

        /// <summary>页局部方向 → 屏幕方向（仅旋转、保长度）。传单位向量即得单位向量。 ⚠ 方向向量须绕【原点】旋转，绝不可复用 （那是绕页心转点， 会把单位向量减去页心坐标后变成上千像素的巨向量，控制点被推出屏幕）。</summary>
        public Vector2 MapDirection(double dx, double dy)
        {
            if (!HasRot) return new Vector2((float)dx, (float)dy);
            float cos = MathF.Cos(RotRad), sin = MathF.Sin(RotRad);
            return new Vector2(
                (float)(dx * cos - dy * sin),
                (float)(dx * sin + dy * cos));
        }

        private Vector2 Rotate(Vector2 v)
        {
            float cos = MathF.Cos(RotRad), sin = MathF.Sin(RotRad);
            float dx = v.X - Cx, dy = v.Y - Cy;
            return new Vector2(Cx + dx * cos - dy * sin, Cy + dx * sin + dy * cos);
        }

        public Matrix3x2 ToMatrix()
        {
            // 直接以绝对值覆盖会话变换（会话基变换恒为恒等）：多页顺序绘制时互不影响。
            var m = Matrix3x2.CreateScale(S) * Matrix3x2.CreateTranslation(Origin);
            if (HasRot)
                m *= Matrix3x2.CreateRotation(RotRad, new Vector2(Cx, Cy));
            return m;
        }
    }

    private PageXform MakePageXform(Rect dest, double z)
    {
        int rot = _rotation;
        bool swap = rot == 90 || rot == 270;
        double w = swap ? dest.Height : dest.Width;
        double h = swap ? dest.Width : dest.Height;
        float cx = (float)(dest.X + dest.Width / 2.0);
        float cy = (float)(dest.Y + dest.Height / 2.0);
        // 吸入设备像素，抑止缩放动画期"笔迹震动"：页位图在共一 dest 上被 GPU 线性采样 → 亚像素平滑，
        var unrotOrigin = new Vector2(
            (float)Math.Round(cx - w / 2.0),
            (float)Math.Round(cy - h / 2.0));
        float s = (float)(z * 96.0 / 72.0);
        return new PageXform(s, unrotOrigin, cx, cy, (float)(rot * Math.PI / 180.0), rot != 0);
    }

    /// <summary>某页在当前视图下的屏幕目标矩形（DIP，含居中补偿）。卡片/连线/手柄定位共用同一算法， 保证三者同源对齐；绘制与命中必须共用这一份，各处内联必然漂移。</summary>
    private Rect ComputePageDest(int pageIdx, double z, double hOff, double vOff)
    {
        double vpW = _vpW, vpH = _vpH;
        double extentW = Spacer.Width * z, extentH = Spacer.Height * z;
        double centerX = extentW < vpW ? (vpW - extentW) / 2.0 : 0;
        double centerY = extentH < vpH ? (vpH - extentH) / 2.0 : 0;
        var lr = _pages[pageIdx].LayoutRect;
        return new Rect(centerX + lr.X * z - hOff, centerY + lr.Y * z - vOff,
            lr.Width * z, lr.Height * z);
    }

    private void DrawAnnotation(CanvasDrawingSession ds, AnnotationViewModel a)
    {
        // 跨线程安全取点快照（Points 内部可能正被 UI 线程 AppendPoint 修改）
        Point[] pts = a.GetPointsSnapshot();
        if (pts.Length == 0) return;

        CachedAnnoGeometry cache = _annoGeoCache.GetValue(a, _ => new CachedAnnoGeometry());
        int curVer = a.PointsVersion;
        bool cacheHit =
            cache.Geometry != null &&
            cache.PointsVersion == curVer &&
            cache.ColorHex == a.ColorHex &&
            Math.Abs(cache.Thickness - a.Thickness) < 1e-6;

        Windows.UI.Color color;
        if (cacheHit)
        {
            color = cache.ParsedColor;
        }
        else
        {
            cache.Geometry?.Dispose();
            cache.Geometry = null;
            color = ParseAnnoColor(a.ColorHex);
            cache.ParsedColor = color;
            cache.ColorHex = a.ColorHex;
            cache.Thickness = a.Thickness;
            cache.PointsVersion = curVer;

            if (pts.Length >= 2)
            {
                using var pb = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(ds);
                pb.BeginFigure((float)pts[0].X, (float)pts[0].Y);
                for (int i = 1; i < pts.Length; i++)
                    pb.AddLine((float)pts[i].X, (float)pts[i].Y);
                pb.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Open);
                cache.Geometry = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(pb);
            }
        }

        float thick = (float)Math.Max(a.Thickness, 0.5);
        if (pts.Length == 1)
        {
            ds.FillCircle((float)pts[0].X, (float)pts[0].Y, thick / 2f, color);
        }
        else if (cache.Geometry != null)
        {
            ds.DrawGeometry(cache.Geometry, Vector2.Zero, color, thick, PenStrokeStyle);
        }
    }

    /// <summary>跨线程安全取某页标注的引用快照（null = 无提供者/异常）。</summary>
    private AnnotationViewModel[]? SafeAnnos(int idx)
    {
        var provider = AnnotationProvider;
        if (provider == null) return null;
        IReadOnlyList<AnnotationViewModel>? annos;
        try { annos = provider(idx); } catch { return null; }
        if (annos == null || annos.Count == 0) return null;
        try { return annos.ToArray(); } catch { return null; }
    }

    /// <summary>有墨的页索引。墨集合只在"载入/换画布/撤销"这类整批变化时才变，故按需重建；
    /// 落笔时新页由 <see cref="NotifyInkPageAdded"/> 增量补一条，不必扫全文档。</summary>
    private readonly List<int> _inkPages = new();
    private bool _inkPagesDirty = true;

    private void RebuildInkPages()
    {
        _inkPages.Clear();
        var provider = AnnotationProvider;
        if (provider != null)
        {
            for (int i = 0; i < _pages.Count; i++)
            {
                IReadOnlyList<AnnotationViewModel>? a;
                try { a = provider(i); } catch { continue; }
                if (a != null && a.Count > 0) _inkPages.Add(i);
            }
        }
        _inkPagesDirty = false;
        _inkOrderDirty = true;
    }

    /// <summary>全局绘制顺序 = 落笔序号升序（跨页统一）。逐页画时层级只能按页排，
    /// 一条从第 1 页画到第 2 页的长笔就会永远压在第 2 页的笔底下 —— 与"后画的在上"相违，
    /// 故单独维护这份顺序表：笔画按它的顺序提交，页变换就在该页第一笔出现时切一次
    /// （同一页的笔连续，切页次数不会多于页数）。</summary>
    private readonly List<(AnnotationViewModel Anno, int Page)> _inkOrder = new();
    private bool _inkOrderDirty = true;

    private void EnsureInkOrder()
    {
        if (_inkPagesDirty) RebuildInkPages();   // 内部会把 _inkOrderDirty 置真
        if (!_inkOrderDirty) return;

        _inkOrder.Clear();
        foreach (int idx in _inkPages)
        {
            if (idx < 0 || idx >= _pages.Count) continue;
            var annos = SafeAnnos(idx);
            if (annos == null) continue;
            foreach (var a in annos)
                if (a.Type == AnnotationType.Pen) _inkOrder.Add((a, idx));
        }
        // 升序即"后画的压在上面"。static lambda 不会每次重建都新分配一个委托。
        _inkOrder.Sort(static (x, y) => x.Anno.ZOrder.CompareTo(y.Anno.ZOrder));
        _inkOrderDirty = false;
    }

    /// <summary>在"本帧要画的页集合"里查某页的屏幕矩形。页数是个位数，线性扫比建字典便宜。</summary>
    private static bool TryFindPageDest(List<(GpuPageInfo Page, Rect Dest)> pages, int pageIndex, out Rect dest)
    {
        foreach (var (p, d) in pages)
        {
            if (p.Index != pageIndex) continue;
            dest = d;
            return true;
        }
        dest = default;
        return false;
    }

    /// <summary>有人刚在某页上落了笔（这一页可能原本没有墨）。只加不删：多余的页只会被多查一次，
    /// 它的笔若不在视口里就画不着 —— 索引的"多"是无害的，"少"才会让墨凭空消失。</summary>
    public void NotifyInkPageAdded(int pageIndex)
    {
        _inkOrderDirty = true;   // 新落一笔要进顺序表（序号最大 ⇒ 排在最后、压在最上）
        if (_inkPagesDirty || pageIndex < 0) return;   // 反正下次绘制会整份重建
        if (!_inkPages.Contains(pageIndex)) _inkPages.Add(pageIndex);
    }

    /// <summary>笔的页局部包围盒（PDF 点）。按 PointsVersion 缓存 —— 落笔过程中点数在变，
    /// 重算也就只在这时发生；空笔返回 false。</summary>
    private static bool TryStrokeBounds(AnnotationViewModel a, CachedAnnoGeometry cache, out Rect bounds)
    {
        int ver = a.PointsVersion;
        if (cache.BoundsVersion == ver)
        {
            bounds = cache.Bounds;
            return cache.HasBounds;
        }

        var pts = a.GetPointsSnapshot();
        if (pts.Length == 0)
        {
            cache.HasBounds = false;
            cache.BoundsVersion = ver;
            bounds = default;
            return false;
        }
        double minX = pts[0].X, maxX = minX, minY = pts[0].Y, maxY = minY;
        for (int i = 1; i < pts.Length; i++)
        {
            double x = pts[i].X, y = pts[i].Y;
            if (x < minX) minX = x; else if (x > maxX) maxX = x;
            if (y < minY) minY = y; else if (y > maxY) maxY = y;
        }
        cache.Bounds = new Rect(minX, minY, maxX - minX, maxY - minY);
        cache.HasBounds = true;
        cache.BoundsVersion = ver;
        bounds = cache.Bounds;
        return true;
    }

    /// <summary>这一页上是否还有笔压在视口里。整条笔挂在发起页、可以越出页框很远（跨到第 3 页之后），
    /// 所以判据是【笔的屏幕包围盒 ∩ 视口】，不是"页距几页"。</summary>
    private bool PageHasStrokeInViewport(int pageIndex, Rect dest, double z)
    {
        var annos = SafeAnnos(pageIndex);
        if (annos == null) return false;

        var xf = MakePageXform(dest, z);
        float vw = (float)_vpW, vh = (float)_vpH;
        const float Margin = 32f;   // 线宽与抗锯齿的余量：擦着视口边也算看得见

        foreach (var a in annos)
        {
            if (a.Type != AnnotationType.Pen) continue;
            var cache = _annoGeoCache.GetValue(a, _ => new CachedAnnoGeometry());
            if (!TryStrokeBounds(a, cache, out Rect b)) continue;

            // 四角全映射：旋转不是 90 的整数倍时，只取对角两点会算窄了包围盒。
            Vector2 c0 = xf.Map(b.Left, b.Top);
            Vector2 c1 = xf.Map(b.Right, b.Top);
            Vector2 c2 = xf.Map(b.Right, b.Bottom);
            Vector2 c3 = xf.Map(b.Left, b.Bottom);
            float minX = MathF.Min(MathF.Min(c0.X, c1.X), MathF.Min(c2.X, c3.X));
            float maxX = MathF.Max(MathF.Max(c0.X, c1.X), MathF.Max(c2.X, c3.X));
            float minY = MathF.Min(MathF.Min(c0.Y, c1.Y), MathF.Min(c2.Y, c3.Y));
            float maxY = MathF.Max(MathF.Max(c0.Y, c1.Y), MathF.Max(c2.Y, c3.Y));

            if (maxX + Margin >= 0 && minX - Margin <= vw &&
                maxY + Margin >= 0 && minY - Margin <= vh) return true;
        }
        return false;
    }

    /// <summary>标注要画的页集合 = 可见页 + "笔仍压在视口里的有墨页"。整条笔挂在【发起页】上，
    /// 溢出那一截可能在发起页早已滚出视口、甚至跨到第 3 页之后 —— 按页的邻近关系猜迟早漏，
    /// 所以直接拿每笔的屏幕包围盒与视口求交：**只要视口能看见就画**，与隔了几页无关。
    /// 顺序不参与层级（层级由 <see cref="_inkOrder"/> 的落笔序决定），保持页索引升序只为让集合本身
    /// 有确定性次序。⚠ 集合是页的【全集】语义：某页入选即画它全部笔，不做单笔剔除。</summary>
    private List<(GpuPageInfo Page, Rect Dest)> ExpandPagesForInk(
        List<(GpuPageInfo Page, Rect Dest)> visible, double z, double hOff, double vOff)
    {
        if (_inkPagesDirty) RebuildInkPages();
        if (_inkPages.Count == 0) return visible;

        List<(GpuPageInfo Page, Rect Dest)>? list = null;
        foreach (int idx in _inkPages)
        {
            if (idx < 0 || idx >= _pages.Count) continue;
            bool already = false;
            foreach (var (p, _) in visible) if (p.Index == idx) { already = true; break; }
            if (already) continue;

            var dest = ComputePageDest(idx, z, hOff, vOff);
            if (!PageHasStrokeInViewport(idx, dest, z)) continue;

            list ??= new List<(GpuPageInfo Page, Rect Dest)>(visible);
            // 插入到"第一个页索引大于它"的位置：升序不依赖 _inkPages 的遍历顺序
            //（NotifyInkPageAdded 是追加，可能给出比已有项更小的索引）。
            int pos = list.Count;
            while (pos > 0 && list[pos - 1].Page.Index > idx) pos--;
            list.Insert(pos, (_pages[idx], dest));
        }
        return list ?? visible;
    }

    /// <summary>这些页里是否至少有一条笔（决定这一帧要不要碰标注层）。</summary>
    private bool HasPenAnnotations(List<(GpuPageInfo Page, Rect Dest)> pages)
    {
        foreach (var (p, _) in pages)
        {
            var annos = SafeAnnos(p.Index);
            if (annos == null) continue;
            foreach (var a in annos)
                if (a.Type == AnnotationType.Pen) return true;
        }
        return false;
    }

    private void EnsureAnnoLayer()
    {
        float w = (float)ScreenCanvas.ActualWidth;
        float h = (float)ScreenCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;
        if (_annoLayer == null ||
            Math.Abs(_annoLayer.Size.Width - w) > 0.5f ||
            Math.Abs(_annoLayer.Size.Height - h) > 0.5f)
        {
            _annoLayer?.Dispose();
            _annoLayer = new Microsoft.Graphics.Canvas.CanvasRenderTarget(ScreenCanvas, w, h);
            _annoLayerNeedsFullRebuild = true;
            _annoBaseZoom = 0;   // 新层没有像素：下一帧静止绘制必须整层重烘（增量烘入比对锚点会失败）
        }
    }

    private void RebuildAnnoLayer(List<(GpuPageInfo Page, Rect Dest)> visible, double z, double hOff, double vOff)
    {
        if (_annoLayer == null) return;
        EnsureInkOrder();
        using var lds = _annoLayer.CreateDrawingSession();
        lds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        // 按全局落笔序逐笔烘入：层的像素与"动画期逐笔矢量"必须同序，否则停稳/起动的瞬间层级会翻。
        int curPage = -1;
        foreach (var (a, page) in _inkOrder)
        {
            if (!TryFindPageDest(visible, page, out Rect dest)) continue;
            if (page != curPage) { ApplyPageTransform(lds, dest, z); curPage = page; }
            var cache = _annoGeoCache.GetValue(a, _ => new CachedAnnoGeometry());
            DrawAnnotation(lds, a);
            cache.BakingPoints = a.GetPointsSnapshot().Length;
        }
        _annoLayerNeedsFullRebuild = false;

        // 记下这批像素对应的视图，供增量烘入前比对"层是否仍对应当前视图"。
        ComputeViewAnchor(z, out double cx, out double cy);
        _annoBaseZoom = z;
        _annoBaseShiftX = cx - hOff;
        _annoBaseShiftY = cy - vOff;
    }

    /// <summary>增量：把"尚未超过已烘入点数的笔画"追加烘入累积层。 已完成的笔画再整段烘入一次；当前正在画的笔画只烘入【新增长的那一小段】， 从而每帧成本与笔画长度无关（长笔画不再每帧整体重建 → GPU 顶点负载骤降）。 追加只对"最新的一笔"发生，而最新的一笔本就该压在最上 —— 与整层重烘的结果同序。</summary>
    private void BakeUnbakedStrokes(List<(GpuPageInfo Page, Rect Dest)> visible, double z, double hOff, double vOff)
    {
        if (_annoLayer == null) return;

        // 层里像素必须与当前视图对应：视图变了却没被要求重烘时，增量烘入会把新笔画按新变换
        // 画进旧变换的层里（整层错位）。宁可整层重烘一次。
        ComputeViewAnchor(z, out double cx, out double cy);
        if (_annoBaseZoom != z || _annoBaseShiftX != cx - hOff || _annoBaseShiftY != cy - vOff)
        {
            RebuildAnnoLayer(visible, z, hOff, vOff);
            return;
        }

        EnsureInkOrder();
        using var lds = _annoLayer.CreateDrawingSession();
        int curPage = -1;
        foreach (var (a, page) in _inkOrder)
        {
            if (!TryFindPageDest(visible, page, out Rect dest)) continue;
            var cache = _annoGeoCache.GetValue(a, _ => new CachedAnnoGeometry());
            // ⚠ GetPointsSnapshot() 每次调用都新分配一份点数组（板书期每帧都会走到这里），
            int n = a.GetPointsSnapshot().Length;
            if (n <= 0 || n <= cache.BakingPoints) continue;

            if (page != curPage) { ApplyPageTransform(lds, dest, z); curPage = page; }
            DrawStrokeIntoLayer(lds, a, cache);
        }
    }

    /// <summary>把某笔画"尚未烘入"的部分追加画进累积层，并推进 BakingPoints。 完全没烘过 → 整段烘入（首笔）；已烘过 → 只画增量段（当前一笔持续增长）。</summary>
    private void DrawStrokeIntoLayer(CanvasDrawingSession lds, AnnotationViewModel a, CachedAnnoGeometry cache)
    {
        Point[] pts = a.GetPointsSnapshot();
        if (pts.Length == 0) return;
        if (pts.Length <= cache.BakingPoints) return;

        if (cache.ColorHex != a.ColorHex || Math.Abs(cache.Thickness - a.Thickness) > 1e-6)
        {
            cache.Geometry?.Dispose();
            cache.Geometry = null;
            cache.ColorHex = a.ColorHex;
            cache.Thickness = a.Thickness;
            cache.ParsedColor = ParseAnnoColor(a.ColorHex);
        }
        var color = cache.ParsedColor;
        float thick = (float)Math.Max(a.Thickness, 0.5);

        // 起步点：from-1 让连接段被包含（避免两份相邻子路径间出现缝隙）；首帧从 0 开始。
        int from = cache.BakingPoints;
        int start = Math.Max(0, from - 1);

        if (start >= pts.Length - 1 && pts.Length == 1)
        {
            lds.FillCircle((float)pts[0].X, (float)pts[0].Y, thick / 2f, color);
        }
        else if (start < pts.Length)
        {
            using var pb = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(lds);
            pb.BeginFigure((float)pts[start].X, (float)pts[start].Y);
            for (int i = start + 1; i < pts.Length; i++)
                pb.AddLine((float)pts[i].X, (float)pts[i].Y);
            pb.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Open);
            using var seg = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(pb);
            lds.DrawGeometry(seg, Vector2.Zero, color, thick, PenStrokeStyle);
        }

        cache.BakingPoints = pts.Length;
    }

    private static Windows.UI.Color ParseAnnoColor(string colorHex)
    {
        try
        {
            string hex = (colorHex ?? "#F2C200").Replace("#", "");
            if (hex.Length == 6) hex = "FF" + hex;
            return Windows.UI.Color.FromArgb(
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16),
                Convert.ToByte(hex.Substring(6, 2), 16));
        }
        catch
        {
            return Windows.UI.Color.FromArgb(255, 255, 255, 0);
        }
    }

    /// <summary>计算当前页/缩放并在变化时发事件。UI 线程调用。</summary>
    private void NotifyViewState()
    {
        var pages = _pages;
        if (pages.Count == 0) return;

        // 主轴可见范围 [lead, trail]：横向布局→X(_visLeft/_visRight)，纵向→Y(_visTop/_visBottom)。
        double lead, trail;
        lock (_lock)
        {
            if (IsHorizontalLayout) { lead = _visLeft; trail = _visRight; }
            else { lead = _visTop; trail = _visBottom; }
        }

        // 布局切换进行中（_layoutSwitchInFlight）：此时 _vis* 可能是被夹断/瞬态的中间值，
        // 不适配重算当前页（否则会 emit 错的页码→侧边栏跳到 1）。当前页在窗口内保持为
        if (!_layoutSwitchInFlight)
        {
            int best = _currentPage >= 0 ? Math.Min(_currentPage, pages.Count - 1) : 0;
            double bestOverlap = -1;
            foreach (var p in pages)
            {
                double a, b;
                if (IsHorizontalLayout) { a = p.LayoutRect.X; b = p.LayoutRect.X + p.LayoutRect.Width; }
                else { a = p.LayoutRect.Top; b = p.LayoutRect.Bottom; }
                if (b < lead) continue;
                if (a > trail) break;
                double ov = Math.Min(b, trail) - Math.Max(a, lead);
                if (ov > bestOverlap) { bestOverlap = ov; best = p.Index; }
            }
            if (best != _currentPage)
            {
                _currentPage = best;
                CurrentPageChanged?.Invoke(this, best);
            }
        }

        GetViewState(out double zsv, out _, out _);
        double zp = zsv * 100.0;
        if (Math.Abs(zp - _lastZoomNotified) > 0.5)
        {
            _lastZoomNotified = zp;
            ZoomPercentChanged?.Invoke(this, zp);
        }
    }

    private bool _cardToolbarShown;

    public GpuPageView()
    {
        InitializeComponent();
        ButtonVisualOverride.Attach(CanvasEditButton, transparentAtRest: true);
        ButtonVisualOverride.Attach(CanvasColorButton, transparentAtRest: true);
        ButtonVisualOverride.Attach(CanvasDeleteButton, transparentAtRest: true);
        // 连线胶囊与卡片胶囊【同一套三态】：主按钮 3 颗（箭头/颜色/删除）+ 二级箭头三选 3 颗。
        ButtonVisualOverride.Attach(CanvasEdgeArrowButton, transparentAtRest: true);
        ButtonVisualOverride.Attach(CanvasEdgeColorButton, transparentAtRest: true);
        ButtonVisualOverride.Attach(CanvasEdgeDeleteButton, transparentAtRest: true);
        ButtonVisualOverride.Attach(CanvasEdgeArrowNoneBtn, transparentAtRest: true);
        ButtonVisualOverride.Attach(CanvasEdgeArrowSingleBtn, transparentAtRest: true);
        ButtonVisualOverride.Attach(CanvasEdgeArrowBothBtn, transparentAtRest: true);
        AttachCanvasColorDotHover();   // 二级颜色胶囊圆点的悬浮/选中放大反馈（照抄 PenOptionsCapsule；卡片 + 连线两副）
        // 胶囊展开/收起会让按钮"滑到静止指针下方"，而 WinUI 不会因此主动派发 hover 事件
        CanvasCardToolbar.PointerMoved += CanvasCardToolbar_PointerMoved;
        CanvasCardToolbar.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(CanvasCardToolbar_PointerPressed), true);
        CanvasEdgeToolbar.PointerMoved += CanvasCardToolbar_PointerMoved;
        CanvasEdgeToolbar.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(CanvasCardToolbar_PointerPressed), true);
        // 滚轮：逻辑处理器挂在 Viewer(ScrollViewer 自身) 上、且用 HandledEventsToo 订阅，
        // 保证必定接管（ScrollViewer 原生处理时会把路由事件标 Handled，普通订阅会被整段吞掉——
        Viewer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Viewer_PointerWheelChanged), true);
        // 捕获期兜底：拉线时持指针捕获，wheel 随该指针改投到捕获元素 = this，挂在 Viewer 上的处理器根本收不到
        // ⇒ 抬不起滚轮（跨视口连线时必须能翻页）。挂根、只在拉线手势里接管（见 Root_PointerWheelChanged）。
        this.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Root_PointerWheelChanged), true);
        // 按住波浪键两次点击建线：松开它 = 取消待接态。挂根上，只要焦点在白板里就能收到（KeyUp）。
        this.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(Root_KeyUp), true);
        Spacer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Viewer_PointerWheelBlocker), true);
        // 编辑态【文本按下】通道：RichEditBox 会把 PointerPressed 标记 Handled ⇒ Viewer_PointerPressed 收不到。
        // ⚠ 边框控件（滚动条 / 四角 / 四边中点 / 四边中段）的按下【不在这里】：其命中带已被 SyncCanvasEditHitCarve
        CanvasEditBox.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnCanvasEditBoxPointerPressed), true);
        // 文本按下松手：按松手点重判"指针是否仍在卡内"，供工字作用域轮询下一拍装/卸工字。
        CanvasEditBox.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnCanvasEditBoxPointerReleased), true);
        // 根级按下监听（handledEventsToo 常驻）：记录右键时刻 + 文本菜单外点击即收起。
        // 必须挂在根上：卡片胶囊/连线工具条等子控件会把 PointerPressed 标 Handled，普通订阅收不到。
        this.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnRootPointerPressed), true);
        // 指针移动（文本区悬停）：补喂白板交互层（手柄显形/悬停）+ 同步指针位置 + 维持挖洞与元素级光标。
        // ⚠ 必须 handledEventsToo=true —— RichEditBox 内部把 PointerMoved 标 Handled，普通 `+=` 订阅收不到。
        CanvasEditBox.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(OnCanvasEditBoxPointerMoved), true);
        CanvasEditBox.SelectionChanged += (_, __) =>
        {
            if (_canvasEditNodeId == null) return;
            ResetCaretBlink();
            ScreenCanvas.Invalidate();
        };
        // 关键：手势指针事件订阅在控件自身（this），而非内部 Viewer。
        this.PointerPressed += Viewer_PointerPressed;
        this.PointerMoved += Viewer_PointerMoved;
        this.PointerReleased += Viewer_PointerReleased;
        this.PointerCaptureLost += Viewer_PointerCaptureLost;
        // （窗口外/标题栏/非子元素区域）后 PointerMoved 不再触发、手势无法自愈。
        this.PointerExited += Viewer_PointerExited;
        // ── 滚动条滑块拖拽抑制（对齐 Sumatra 快拖静止行为，避免长时间 CPU 风暴）──
        // ScrollViewer 内置 ScrollBar 的 Thumb 拖拽期间抑制渲染请求（缓存瓦片照画、视口照常移动），
        Viewer.Loaded += (s, e) => HookScrollBarThumbs(Viewer);
        this.Unloaded += (s, e) => _hookedThumbs.Clear(); // 视图重挂载后模板 Thumb 是新实例，清旧引用
        // 箭头三态符号的「恒定 2 物理像素」描边：Loaded 后应用一次，并挂 RasterizationScale 变化重算
        Loaded += (s, e) =>
        {
            ApplyCanvasEdgeArrowGlyphStrokeThickness();
            if (!_canvasEdgeGlyphScaleHooked && this.XamlRoot != null)
            {
                _canvasEdgeGlyphScaleHooked = true;
                this.XamlRoot.Changed += (_, __) => ApplyCanvasEdgeArrowGlyphStrokeThickness();
            }
        };
        UpdateThemeColors();
        ActualThemeChanged += (s, e) => { UpdateThemeColors(); InvalidateBoth(); };
        // 冷启动(文件关联)加载 PDF 时 GpuViewer 由 Collapsed → Visible：此刻窗口已按保存主题激活，
        RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
    }

    /// <summary>Visibility 变化（Collapsed → Visible）时用确定性主题源刷新底色，防冷启动首帧闪白。</summary>
    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (Visibility == Visibility.Visible)
            UpdateThemeColors();
    }

    /// <summary>根 Grid 裁剪到自身边界：缩放动画中 Visual.Scale&gt;1 时画布不会溢出到视口/工具栏之外。</summary>
    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RootGrid.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
    }

    private Windows.UI.Color _gapColor;
    private Windows.UI.Color _placeholderColor;

    private void UpdateThemeColors()
    {
        bool isDark;
        try
        {
            // 确定性主题源：优先取 MainWindow.WindowRequestedTheme（ApplySavedTheme 已按保存
            var mw = (Application.Current as App)?.MainWindow as MainWindow;
            isDark = mw?.IsDarkTheme ?? (ActualTheme == ElementTheme.Dark);
        }
        catch
        {
            isDark = ActualTheme == ElementTheme.Dark;
        }

        if (isDark)
        {
            _gapColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);          // #202020 = SolidBackgroundFillColorBase(Dark)
            _placeholderColor = Windows.UI.Color.FromArgb(255, 42, 42, 42);
            _canvasAccentColor = CanvasAccentDark;
        }
        else
        {
            _gapColor = Windows.UI.Color.FromArgb(255, 243, 243, 243);       // #F3F3F3 = SolidBackgroundFillColorBase(Light)
            _placeholderColor = Windows.UI.Color.FromArgb(255, 249, 249, 249);
            _canvasAccentColor = CanvasAccentLight;
        }

        // RootGrid 背景：显式写入主题色，替代 ThemeResource(SolidBackgroundFillColorBaseBrush)，
        if (RootGrid.Background is SolidColorBrush rootBrush && rootBrush.Color == _gapColor)
        {
        }
        else
        {
            RootGrid.Background = new SolidColorBrush(_gapColor);
        }

        // CanvasControl 忽略 XAML Background 属性；其 ClearColor 决定 Draw 前(含首帧前)的画布底色
        try
        {
            if (ScreenCanvas.ClearColor != _gapColor)
                ScreenCanvas.ClearColor = _gapColor;
        }
        catch { }
    }

    /// <summary>载入 PDF：取页数与每页尺寸，计算文档布局，撑出滚动范围。</summary>
    public async Task LoadAsync(PdfRenderService svc, ViewStateData? restoreView = null)
    {
        RenderService = svc;
        _didInitialFit = false;
        _pendingRestore = null;
        _restoreRetries = 0;    // 同上：丢弃上一文档可能排队的还原重试
        _liveH = 0; _liveV = 0; _liveZoom = 1.0;
        _currentPage = -1;
        _lastZoomNotified = -1;
        uint count = await svc.GetPageCountAsync();
        // 批量取全部页尺寸：一次 FFI 调用 + 一次引擎锁持有，替代 N 次逐页往返
        var dims = await svc.GetAllPageDimensionsAsync(count);
        var pages = new List<GpuPageInfo>((int)count);
        for (int i = 0; i < (int)count; i++)
        {
            pages.Add(new GpuPageInfo
            {
                Index = i,
                WidthPoints = dims[i * 2],
                HeightPoints = dims[i * 2 + 1],
            });
        }

        _rotation = 0;
        _pages = pages;
        // 页集合换了：有墨页索引与全局顺序表都是按旧文档的页算的，下次绘制整份重扫。
        _inkPagesDirty = true;
        RecomputeLayout(pages);

        ShutdownRenderCache();
        _renderCache = new TileRenderCache(this, DispatcherQueue);

        // 若本次加载后需精确还原（如保存/另存为后重载同一文档），先排好待还原，
        // 使下方 TryInitialFit 跳过"适应宽度"、直接由还原落定视口——
        // ⚠ 必须走 RestoreView 而非直接赋 _pendingRestore：RestoreView 会先还原【布局模式】
        if (restoreView != null)
            RestoreView(restoreView);

        // 冷启动热页：按还原位置预解析目标页 DisplayList（使该页首块瓦片渲染直接命中缓存、
        if (_pendingRestore != null)
        {
            int warmPage = RestoreTargetPage(_pendingRestore);
            if (warmPage > 0)
                _ = svc.WarmupPageAsync((uint)warmPage);
        }

        // 【重开闪首页修复】视口可能尚未就绪（TryInitialFit 会直接 return），若此刻不先播种
        if (_pendingRestore != null)
        {
            var rv = _pendingRestore;
            double rvZ = Math.Clamp(rv.ZoomPercent / 100.0, MinZoom, MaxZoom);
            if (rvZ <= 0) rvZ = 1.0;
            SyncLiveView(Math.Max(0, rv.HorizontalOffset), Math.Max(0, rv.VerticalOffset), rvZ);
        }

        TryInitialFit();
        if (_pendingRestore != null)
        {
            // 还原尚未落定（视口未就绪或阶段1/2 重试中）：按还原位置播种可见范围，
            // 绝不能让下方 QueueVisiblePages 读到 UpdateVisibleRange 覆盖的顶部视口而渲染首页。
            var rv = _pendingRestore;
            double rvZ = Math.Clamp(rv.ZoomPercent / 100.0, MinZoom, MaxZoom);
            if (rvZ <= 0) rvZ = 1.0;
            SetVisibleRangeFor(Math.Max(0, rv.HorizontalOffset), Math.Max(0, rv.VerticalOffset), rvZ);
        }
        else
        {
            UpdateVisibleRange();
        }
        QueueVisiblePages();
        InvalidateBoth();
        NotifyViewState();
    }

    /// 冷启动热页用：由还原偏移 + 目标缩放推导主轴视口起点落在哪一页（连续布局下页首尾相接， 起点所在的页即首屏目标页）。
    private int RestoreTargetPage(ViewStateData vs)
    {
        var pages = _pages;
        if (pages.Count == 0) return 0;
        double zoom = Math.Clamp(vs.ZoomPercent / 100.0, MinZoom, MaxZoom);
        if (zoom <= 0) zoom = 1.0;
        double lead = IsHorizontalLayout
            ? Math.Max(0, vs.HorizontalOffset) / zoom
            : Math.Max(0, vs.VerticalOffset) / zoom;
        int best = pages[pages.Count - 1].Index;
        foreach (var p in pages)
        {
            double a = IsHorizontalLayout ? p.LayoutRect.X : p.LayoutRect.Top;
            double b = IsHorizontalLayout ? p.LayoutRect.X + p.LayoutRect.Width : p.LayoutRect.Bottom;
            if (lead < b) { best = p.Index; break; }
        }
        return best;
    }

    private readonly struct FacingRow
    {
        public FacingRow(int first, int last) { FirstPage = first; LastPage = last; }
        public int FirstPage { get; }
        public int LastPage { get; }
    }

    /// 把页配对成双列对开的"行"。
    private static List<FacingRow> CollectFacingRows(int pageCount, bool bookView)
    {
        var rows = new List<FacingRow>();
        if (pageCount < 1) return rows;
        int page = 1;
        if (bookView)
        {
            rows.Add(new FacingRow(1, 1));
            page = 2;
        }
        while (page <= pageCount)
        {
            if (page + 1 <= pageCount)
            {
                rows.Add(new FacingRow(page, page + 1));
                page += 2;
            }
            else
            {
                rows.Add(new FacingRow(page, page));
                page += 1;
            }
        }
        return rows;
    }

    /// <summary>按当前【布局方向 + 旋转角】计算文档布局（90/270° 交换宽高），并撑出滚动范围。UI 线程调用。 两种方向都是连续布局：所有页首尾相接串在主轴上，交叉轴居中。</summary>
    private void RecomputeLayout(IReadOnlyList<GpuPageInfo> pages)
    {
        bool swap = _rotation == 90 || _rotation == 270;

        if (_layoutMode == PageLayoutMode.HorizontalContinuous)
        {
            double maxDipH = 0;
            foreach (var p in pages)
            {
                double h0 = (swap ? p.WidthPoints : p.HeightPoints) * 96.0 / 72.0;
                maxDipH = Math.Max(maxDipH, h0);
            }
            double contentH = maxDipH + DocMargin * 2;
            double x = DocMargin;
            foreach (var p in pages)
            {
                double dipW = (swap ? p.HeightPoints : p.WidthPoints) * 96.0 / 72.0;
                double dipH = (swap ? p.WidthPoints : p.HeightPoints) * 96.0 / 72.0;
                p.LayoutRect = new Rect(x, (contentH - dipH) / 2.0, dipW, dipH);
                x += dipW + PageGapHorizontal;
            }
            double totalW = x - PageGapHorizontal + DocMargin;

            Spacer.Width = Math.Max(totalW, 1);
            Spacer.Height = contentH;
            return;
        }

        if (_arrangement == PageArrangement.Single)
        {
            double maxDipW = 0;
            foreach (var p in pages)
            {
                double dipW = (swap ? p.HeightPoints : p.WidthPoints) * 96.0 / 72.0;
                maxDipW = Math.Max(maxDipW, dipW);
            }
            double contentW = maxDipW + DocMargin * 2;
            double y = DocMargin;
            foreach (var p in pages)
            {
                double dipW = (swap ? p.HeightPoints : p.WidthPoints) * 96.0 / 72.0;
                double dipH = (swap ? p.WidthPoints : p.HeightPoints) * 96.0 / 72.0;
                p.LayoutRect = new Rect((contentW - dipW) / 2.0, y, dipW, dipH);
                y += dipH + PageGap;
            }
            double totalH = y - PageGap + DocMargin;
            Spacer.Width = contentW;
            Spacer.Height = Math.Max(totalH, 1);
            return;
        }

        // 双页 / 书籍：双列对开，移植自 sumatrapdf-master/src/DocumentLayout.cpp
        var rows = CollectFacingRows(pages.Count, _arrangement == PageArrangement.Book);
        double colMax0 = 0, colMax1 = 0;
        double currPosY = DocMargin;
        foreach (var row in rows)
        {
            double rowMaxDy = 0;
            for (int pageNo = row.FirstPage; pageNo <= row.LastPage; pageNo++)
            {
                var p = pages[pageNo - 1];
                double dipW = (swap ? p.HeightPoints : p.WidthPoints) * 96.0 / 72.0;
                double dipH = (swap ? p.WidthPoints : p.HeightPoints) * 96.0 / 72.0;
                p.LayoutRect = new Rect(0, currPosY, dipW, dipH);
                rowMaxDy = Math.Max(rowMaxDy, dipH);
                if (row.FirstPage == row.LastPage)
                {
                    bool cover = _arrangement == PageArrangement.Book && row.FirstPage == 1;
                    if (cover) colMax1 = Math.Max(colMax1, dipW);
                    else colMax0 = Math.Max(colMax0, dipW);
                }
                else
                {
                    if (pageNo == row.FirstPage) colMax0 = Math.Max(colMax0, dipW);
                    else colMax1 = Math.Max(colMax1, dipW);
                }
            }
            currPosY += rowMaxDy + PageGap;
        }
        double contentW2 = colMax0 + PageGap + colMax1 + DocMargin * 2;
        double pageOffX = DocMargin;
        foreach (var row in rows)
        {
            if (row.FirstPage == row.LastPage)
            {
                var p = pages[row.FirstPage - 1];
                double dipW = p.LayoutRect.Width;
                bool cover = _arrangement == PageArrangement.Book && row.FirstPage == 1;
                double x = cover ? (pageOffX + colMax0 + PageGap) : (pageOffX + colMax0 - dipW);
                p.LayoutRect = new Rect(x, p.LayoutRect.Y, dipW, p.LayoutRect.Height);
            }
            else
            {
                int col = 0;
                double x = pageOffX;
                for (int pageNo = row.FirstPage; pageNo <= row.LastPage; pageNo++)
                {
                    var p = pages[pageNo - 1];
                    double dipW = p.LayoutRect.Width;
                    double px = (col == 0) ? (x + colMax0 - dipW) : x;
                    p.LayoutRect = new Rect(px, p.LayoutRect.Y, dipW, p.LayoutRect.Height);
                    x += (col == 0 ? colMax0 : colMax1) + PageGap;
                    col++;
                }
            }
        }
        double totalH2 = currPosY - PageGap + DocMargin;
        Spacer.Width = Math.Max(contentW2, 1);
        Spacer.Height = Math.Max(totalH2, 1);
    }

    private void Viewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        TryInitialFit();
        UpdateVisibleRange();
        InvalidateBoth();
    }

    /// <summary>首次拿到有效视口时：有保存的阅读位置则还原，否则按当前布局主轴适应（均只跑一次）。</summary>
    private void TryInitialFit()
    {
        if (_didInitialFit) return;
        var pages = _pages;
        if (pages.Count == 0) return;
        double viewportMain, contentMain;
        if (IsHorizontalLayout) { viewportMain = Viewer.ViewportHeight; contentMain = Spacer.Height; }
        else { viewportMain = Viewer.ViewportWidth; contentMain = Spacer.Width; }
        if (viewportMain <= 0 || double.IsNaN(contentMain) || contentMain <= 0) return;

        _didInitialFit = true;

        // 有保存的阅读位置 → 还原优先于适应，且因 _didInitialFit 已置位不会被后续覆盖。
        // 范围已就绪则 TryApplyPendingRestore 同步精确应用（首帧即从该位置绘制、无闪）；
        if (_pendingRestore != null)
        {
            TryApplyPendingRestore();
        }
        else
        {
            double fit = Math.Clamp(viewportMain / contentMain, MinZoom, MaxZoom);
            Viewer.ChangeView(0, 0, (float)fit, disableAnimation: true);
            SyncLiveView(0, 0, fit);
        }
    }

    private void Viewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // 视图动了（滚动/缩放）⇒ 文本菜单的位置就失效了，直接收起（用户下一次右键会重新定位）。
        HideCanvasEditContextMenu();
        if (_zoomAnimCommitting)
        {
            // 不能无条件关窗并往下同步 _live*：双击/按钮缩放全程【不】逐帧 ChangeView
            // （见 StepZoomFrame 注释，为性能刻意为之），关窗过早会闪一帧别处画面。
            if (!IsCommittedViewReached())
            {
                // 与看门狗共用同一套推进判定。上一轮正是因为看门狗漏了"zoom 未落地不得补发"
                if (!TryAdvanceCommit()) ForceCloseCommitWindow();
                else ScheduleCommitWatchdog(); // 万一后续不再有 ViewChanged，仍有兜底收尾
                return;
            }
            _zoomAnimCommitting = false; // ChangeView 确认落地 → 画布可切回读 Viewer 状态
        }

        // 注意：待还原(_pendingRestore)的落定完全由 TryApplyPendingRestore 的 Low 优先级自重试驱动，

        // 注：滚动弹簧的目标不需要在这里清 —— 每段新滚动都由 EnsureScrollSprings 从

        // 平滑缩放 / 滚轮平滑滚动 / 左键拖拽平移的动画帧：视图由各自定时器/指针处理器推进
        if (_isSmoothZooming || _isWheelScrolling || _isDragPanning) return;

        // 布局切换稳定窗口(_layoutSwitchInFlight)：仅在 ScrollViewer 真实到达 Reanchor 播种的目标
        // 否则把被旧 extent 夹断的 0/中间值当权威写进 _vis* → 页码/侧边栏瞬跳 1 而视图不动。
        if (_layoutSwitchInFlight)
        {
            double zz = Viewer.ZoomFactor;
            double hh = Viewer.HorizontalOffset;
            double vv = Viewer.VerticalOffset;
            bool zoomLanded = Math.Abs(zz - _layoutTargetZoom) < 1e-3;
            double tolH = Math.Max(6.0, Math.Abs(_layoutTargetH) * 1e-4);
            double tolV = Math.Max(6.0, Math.Abs(_layoutTargetV) * 1e-4);
            bool landed = zoomLanded &&
                          Math.Abs(hh - _layoutTargetH) <= tolH &&
                          Math.Abs(vv - _layoutTargetV) <= tolV;
            bool timedOut = (System.Diagnostics.Stopwatch.GetTimestamp() - _layoutSwitchStartTicks) * 1e-6 >= 600.0;
            if (!landed && !timedOut) return;

            _layoutSwitchInFlight = false;
            StopLayoutSwitchWatchdog();
            SyncLiveView(hh, vv, zz);
            _vpW = Viewer.ViewportWidth;
            _vpH = Viewer.ViewportHeight;
            if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
            UpdateVisibleRange();
            // 页码/侧边栏钉回锚页：真落点=目标时与下方重算一致；极端夹断兜底时以锚页优先，绝不含混跳 1。
            var lpages = _pages;
            int finalPage = _layoutSwitchAnchor >= 0 ? Math.Min(_layoutSwitchAnchor, lpages.Count - 1) : 0;
            if (finalPage != _currentPage)
            {
                _currentPage = finalPage;
                CurrentPageChanged?.Invoke(this, finalPage);
            }
            QueueVisiblePages();
            InvalidateBoth();
            NotifyViewState();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // 停稳（idle 滚动/缩放落定）：把实时镜像同步成 Viewer 当前值，确保 _live* 始终是权威最近视图
        // （双击/还原起点统一读 _live*，避免读到异步 ChangeView 未落地的 0）。
        if (_pendingRestore == null && !_layoutSwitchInFlight)
        {
            SyncLiveView(Viewer.HorizontalOffset, Viewer.VerticalOffset, Viewer.ZoomFactor);
            _vpW = Viewer.ViewportWidth;
            _vpH = Viewer.ViewportHeight;
            if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
            UpdateVisibleRange();
        }
        else if (!_layoutSwitchInFlight)
        {
            // 还原窗口(_pendingRestore!=null)：不读 ScrollViewer 可能被夹断的偏移，仅更新视口快照，
            _vpW = Viewer.ViewportWidth;
            _vpH = Viewer.ViewportHeight;
            if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
            UpdateVisibleRange();
        }
        // 布局切换稳定窗口(_layoutSwitchInFlight)：一律不读 Viewer.*（连视口快照也不覆盖），

        // 缩放/滚动进行中只排"缺图"的页；停稳后才排"分辨率升级"的重光栅（旧图 GPU 缩放顶着）。
        QueueVisiblePages();
        // 视口画布轻量重画：只画 2-3 张已缓存位图，惯性滚动逐帧调用无压力。
        InvalidateBoth();
        NotifyViewState();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        // 注意：实时镜像 _live* 的 idle 同步已在上方「if (_pendingRestore == null)」块内完成（第2332行）。

    }

    /// <summary>把当前视口换算到文档坐标(DIP)。UI 线程调用。两条轴都更新（横向布局靠 _visLeft/_visRight 选页）。</summary>
    private void UpdateVisibleRange()
    {
        double z = Viewer.ZoomFactor;
        if (z <= 0) return;
        double hOff = Viewer.HorizontalOffset;
        double vOff = Viewer.VerticalOffset;
        double dpi = 1.0;
        try { dpi = XamlRoot?.RasterizationScale ?? 1.0; } catch { }
        _vpW = Viewer.ViewportWidth;
        _vpH = Viewer.ViewportHeight;
        if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
        lock (_lock)
        {
            _visLeft = hOff / z;
            _visRight = (hOff + _vpW) / z;
            _visTop = vOff / z;
            _visBottom = (vOff + _vpH) / z;
            _dpiScale = dpi;
        }
    }

    /// <summary>按指定 偏移/缩放 直接设置可见范围与光栅缩放（不读 Viewer.*）。 用于还原路径：还原的 ChangeView 是异步落地，此期间 Viewer.* 仍是旧值（顶部），</summary>
    private void SetVisibleRangeFor(double hOff, double vOff, double zoom)
    {
        if (zoom <= 0) return;
        double dpi = 1.0;
        try { dpi = XamlRoot?.RasterizationScale ?? 1.0; } catch { }
        _vpW = Viewer.ViewportWidth;
        _vpH = Viewer.ViewportHeight;
        if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
        lock (_lock)
        {
            _visLeft = hOff / zoom;
            _visRight = hOff / zoom + _vpW / zoom;
            _visTop = vOff / zoom;
            _visBottom = vOff / zoom + _vpH / zoom;
            _dpiScale = dpi;
        }
    }

        // ---- 首开早期内存回收（把「静止/滚动才触发的那次回升」提前到载入落定后）----
    // 实测：单进程 MuPDF 首开时解析各页 DisplayList，进程内存冲到 ~1.2GB，几次 GC 后
    private const int WorkerCacheFlushIntervalMs = 2500;
    private const long WorkerCacheIdleMs = 1000;
    private const long WorkerCacheReFlushMs = 20000;       // 两次 GC/reload 的最小间隔（防止静止时每轮重放）
    private long _lastRenderActivityTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    // 初始把 _lastFlushTicks 拨到很久以前，使首刷不被 20s（WorkerCacheReFlushMs）冷却锁住。
    // 否则 sinceFlush 从载入时刻算起 <20000ms 恒为 "cool" skip，首开前 20s 一次都不 flush，
    private long _lastFlushTicks =
        System.Diagnostics.Stopwatch.GetTimestamp()
        - System.Diagnostics.Stopwatch.Frequency * (WorkerCacheReFlushMs / 1000);
    private static int _cacheFlushArmed = 1;

    /// <summary>空闲巡检：静态且无在途瓦片渲染时，①GC 整理主进程 ②重载文档释放 MuPDF 页数据累积缓存。 只影响后台回收，不触碰已上屏的瓦片。首刷提前到载入落定后；再用 20s 冷却避免每次停顿重放。</summary>
    private void EnsureWorkerCacheFlush()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _cacheFlushArmed, 0, 1) != 1) return;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(WorkerCacheFlushIntervalMs).ConfigureAwait(false);
                try
                {
                    // 未滚动/缩放 + 内核无在途渲染 + 停足够久才触发；另用冷却限制重放频率
                    bool busyZoom = _isSmoothZooming || _isWheelScrolling;
                    if (_renderCache?.IsBusy() ?? false) { continue; }
                    if (busyZoom) { continue; }
                    long idleMs = (long)((Stopwatch.GetTimestamp() - _lastRenderActivityTicks) * 1000.0 / Stopwatch.Frequency);
                    long sinceFlush = (long)((Stopwatch.GetTimestamp() - _lastFlushTicks) * 1000.0 / Stopwatch.Frequency);
                    if (idleMs < WorkerCacheIdleMs || sinceFlush < WorkerCacheReFlushMs) { continue; }

                    // ①主进程 GC 整理（把首开一次性大分配当垃圾回收，避免一直挂着 ~1.2GB）
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();

                    var svcFlush = RenderService;
                    if (svcFlush == null) { _lastFlushTicks = Stopwatch.GetTimestamp(); _lastRenderActivityTicks = _lastFlushTicks; continue; }
                    await svcFlush.ReloadDocumentAsync().ConfigureAwait(false);
                    _lastFlushTicks = Stopwatch.GetTimestamp();
                    _lastRenderActivityTicks = _lastFlushTicks;
                }
                catch
                {
                    return;
                }
            }
        });
    }

    private void ScreenCanvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        // 设备(重)建：GPU 位图绑定特定设备 → 全部作废（CPU 瓦片缓存与 Sumatra 一致地保留，
        _renderCache?.DropGpuResources();
        foreach (var kv in _annoGeoCache)
        {
            kv.Value.Geometry?.Dispose();
        }
        _annoGeoCache.Clear();
        _annoLayer?.Dispose();
        _annoLayer = null;
        _annoLayerNeedsFullRebuild = true;
        _annoBaseZoom = 0;   // 层没了：下一帧静止绘制必须整层重烘
        // 白板卡片的文本排版同样绑定该设备：全部作废，下一帧按需重建。
        foreach (var kv in _canvasTextCache)
            kv.Value.Layout?.Dispose();
        _canvasTextCache.Clear();
        _canvasTextFormat = null;
        // 设备就绪：重新排队可见页瓦片。
        // 修复首开概率性白屏：LoadAsync 排队时若设备未就绪，构建会因设备快照为 null 而退出；
        UpdateVisibleRange();
        QueueVisiblePages();
        InvalidateBoth();
    }

    /// <summary>取当前视图状态（缩放/偏移）。平滑缩放进行中返回插值后的动画状态， 避免依赖 ScrollViewer.ChangeView 的异步落点（其在 60Hz 下会与画布绘制产生 1 帧错位抖动）。</summary>
    private void GetViewState(out double zoom, out double hOff, out double vOff)
    {
        // 优先级：平滑缩放/提交窗口 ＞ 拖拽平移 ＞ 闲置。
        // 必须让缩放态压过拖拽平移：双击手势会吞掉第二次指针抬起，_isDragPanning 可能仍卡 true，
        if (_isSmoothZooming || _zoomAnimCommitting)
        {
            // 动画中/提交落地瞬间：正常返回插值态；但若插值态偏移≈0 而实时镜像 _live* 显著非0，
            if ((_zoomAnimCurrentV <= 1.0 && _liveV > 1.0) || (_zoomAnimCurrentH <= 1.0 && _liveH > 1.0))
            {
                zoom = _liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor;
                hOff = _liveH;
                vOff = _liveV;
            }
            else
            {
                zoom = _zoomAnimCurrentFactor;
                hOff = _zoomAnimCurrentH;
                vOff = _zoomAnimCurrentV;
            }
        }
        else if (_isDragPanning)
        {
            // 拖拽平移：画布按自管显示偏移即时绘制，跳过 ChangeView 异步落点造成的 1 帧错位（与滚轮同原理）。
            zoom = CurrentZoom;
            hOff = _panDisplayH;
            vOff = _panDisplayV;
        }
        else if (_isWheelScrolling)
        {
            // 滚动进行中：以自管滚动镜像 _live* 为准，覆盖可能仍开着的缩放提交窗口
            // 否则 GetViewState 仍返回缩放插值态、与帧泵每帧下发的滚动位置脱节，缩放动画结束后出现
            if (_liveZoom > 0)
            {
                zoom = _liveZoom;
                hOff = _liveH;
                vOff = _liveV;
            }
            else
            {
                zoom = Viewer.ZoomFactor;
                hOff = Viewer.HorizontalOffset;
                vOff = Viewer.VerticalOffset;
            }
        }
        else
        {
            // 平时 _live* 与 Viewer.* 同步；但在"还原重试窗口"等瞬间 Viewer.* 可能尚未反映
            if (_liveZoom > 0)
            {
                zoom = _liveZoom;
                hOff = _liveH;
                vOff = _liveV;
            }
            else
            {
                zoom = Viewer.ZoomFactor;
                hOff = Viewer.HorizontalOffset;
                vOff = Viewer.VerticalOffset;
            }
        }
        if (zoom <= 0) zoom = 1.0;
    }

    // =====================================================================
    // 白板层（Canvas 模式）：卡片 + 连线

    private readonly object _canvasLock = new();
    private CanvasNode[] _canvasNodes = Array.Empty<CanvasNode>();
    private CanvasEdge[] _canvasEdges = Array.Empty<CanvasEdge>();
    private Dictionary<string, CanvasNode> _canvasIndex = new(StringComparer.Ordinal);

    private CanvasTextFormat? _canvasTextFormat;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<CanvasNode, CachedCanvasText>
        _canvasTextCache = new();

    private const float CanvasCardRadius = 8f;
    private const float CanvasCardPadX = 10f;
    private const float CanvasCardPadY = 0f;        // 卡片上/下内边距（页局部 PDF 点）—— 对齐 Obsidian：上下完全不留空
    // 卡片内滚动条（阅读/选中/编辑三态统一，由 Win2D 绘制）：
    private const float CanvasScrollBarWidth = 8f;
    private const float CanvasScrollBarGap = 2f;
    private const float CanvasScrollBarMinH = 20f;
    private const float CanvasScrollBarHitTol = 3f;   // 滚动条命中容差（屏幕 DIP）：只在其矩形外扩一点点，
                                                      // 避免把贴着卡片边框的缩放/连线手柄整体抢走
    private static readonly Windows.UI.Color CanvasScrollBarColor = Windows.UI.Color.FromArgb(51, 0, 0, 0);
    // 卡片正文唯一字体：Win2D 排版（EnsureCanvasTextFormat）与隐形编辑框（XAML FontFamily、
    // ApplyCanvasEditCharacterFormatting）必须同族，否则两侧换行点/行高各算一套。
    private const string CanvasCardFontFamily = "Segoe UI";
    // 单行行高（页局部 PDF 点）= 1.35 × 字号 = 16.2。两态的排版行距都被显式钉到该值
    // （见 EnsureCanvasTextFormat 的 Uniform 行距）⇒ 它就是【真实】行高，留白公式、内容高估算、
    // 滚轮步长、滚动条长度与两个文本引擎的行距全部以它为准，不再有"标称值 vs 实测值"两套口径。
    private const float CanvasLineHeight = CanvasCardFontSize * 1.35f;
    private const float CanvasCardFontSize = 12f;
    private const float CanvasCardStrokeWidth = 1.0f;
    private const float CanvasCardSelectedStrokeWidth = 2.0f;
    private const float CanvasEdgeStrokeWidth = 1.6f;
    private const float CanvasEdgeMinWidth = 0.75f;   // 连线描边下限（屏幕 DIP）：小缩放时仍较清晰，不无限变细
    // 箭头尺寸以【页局部 PDF 点】计：调用处再乘缩放 z 得到屏幕 DIP，从而随页面缩放（与卡片手柄 CanvasHandleRadius 同约定）。
    private const float CanvasArrowLen = 9f;
    private const float CanvasArrowMinLen = 6f;   // 箭头高下限（屏幕 DIP）：小缩放时仍完全清晰可辨（覆盖 CanvasArrowLen·z），与命中下限同量级
    private static readonly Windows.UI.Color CanvasCardFill = Windows.UI.Color.FromArgb(255, 255, 255, 255);
    private static readonly Windows.UI.Color CanvasCardStroke = Windows.UI.Color.FromArgb(255, 138, 143, 152);
    private static readonly Windows.UI.Color CanvasCardText = Windows.UI.Color.FromArgb(255, 32, 32, 34);
    // 行内 Markdown 的底纹（卡片恒为不透明白卡面，故这两色不随主题变化）。
    private static readonly Windows.UI.Color CanvasMarkdownCodeBackdrop = Windows.UI.Color.FromArgb(255, 236, 236, 241);
    private static readonly Windows.UI.Color CanvasMarkdownHighlightBackdrop = Windows.UI.Color.FromArgb(255, 255, 236, 140);
    // 底纹竖向边界（相对【基线】，按字号的比例）。中文字面大致在 -0.88em..+0.12em，拉丁升部/降部约
    // ⚠ 不能直接用 GetCharacterRegions 的整行框（约 1.33em）：中文会在色块里上下各浮约 2pt（用户报告）。
    private const float CanvasMarkdownBackdropAscent = 0.92f;
    private const float CanvasMarkdownBackdropDescent = 0.25f;
    private const string CanvasMarkdownCodeFontFamily = "Consolas";
    private static readonly Windows.UI.Text.FontWeight CanvasMarkdownBoldWeight = new() { Weight = 700 };

    /// <summary>卡片"淡色底纹"的 alpha（0-255，约 10%）。只给「后 6 色」用：在白色卡面之上再薄薄叠一层本色， 使卡片内部带一点同色氛围；首位默认灰不上底纹（灰底纹会显脏，且用户明确排除）。</summary>
    private const byte CanvasCardTintAlpha = 26;

    /// <summary>卡片边框色解析缓存：绘制期每帧、每卡都会问一次，绝不能在 Draw 里反复解析字符串。 键 = 原始 hex 串（大小写不敏感），值为解析结果（非法值也缓存成默认灰，避免反复解析）。 规模天然有界（调色板 7 色 + 外部导入的少量颜色）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Windows.UI.Color> CanvasStrokeColorCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>把 node.Color（"#RRGGBB"）解析成卡片边框色；空串 / 非法值一律回落默认灰 （= 二级胶囊首位灰点 #8A8F98，两处语义一致）。</summary>
    private static Windows.UI.Color ResolveCanvasCardStroke(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return CanvasCardStroke;
        if (CanvasStrokeColorCache.TryGetValue(hex, out var cached)) return cached;

        var color = CanvasCardStroke;
        string s = hex[0] == '#' ? hex.Substring(1) : hex;
        if (s.Length == 6
            && int.TryParse(s.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out int r)
            && int.TryParse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out int g)
            && int.TryParse(s.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                             System.Globalization.CultureInfo.InvariantCulture, out int b))
        {
            color = Windows.UI.Color.FromArgb(255, (byte)r, (byte)g, (byte)b);
        }
        CanvasStrokeColorCache[hex] = color;
        return color;
    }

    // 编辑态选区高亮（Win2D 自绘）：与工具栏页码框 / 书签编辑框（SelfDrawnTextBox 的 SelRect）
    // 共用同一主题色 ⇒ 三处拖选颜色一致。
    // ⚠ 必须走主题字典取：该键【只】定义在 ThemeDictionaries 里、顶层字典没有
    // （与 ToggleButton* / GlanceAccent* 同一类坑，见 VisualOverrideBase.Resource 的注释）。
    // ⚠ 主题键用 MainWindow.IsDarkTheme 这个【确定性同步】源（暗色值在 "Default" 档、顶层无 "Dark"），
    private static Windows.UI.Color GetCanvasSelectionColor()
    {
        const byte Alpha = 89;   // 0.35 × 255，与 SelfDrawnTextBox 的 SelRect Opacity 一致
        try
        {
            var resources = Application.Current?.Resources;
            if (resources != null)
            {
                string themeKey = ((Application.Current as App)?.MainWindow is MainWindow mw && !mw.IsDarkTheme)
                    ? "Light" : "Default";
                foreach (var kv in resources.ThemeDictionaries)
                {
                    if (kv.Key is not string k || k != themeKey || kv.Value is not ResourceDictionary rd) continue;
                    if (rd.TryGetValue("GlanceAccentBrush", out var v)
                        && v is Microsoft.UI.Xaml.Media.SolidColorBrush b)
                    {
                        return Windows.UI.Color.FromArgb(Alpha, b.Color.R, b.Color.G, b.Color.B);
                    }
                    break;
                }
            }
        }
        catch { }
        return Windows.UI.Color.FromArgb(Alpha, 0xA8, 0x82, 0xFF);   // 兜底：深色档 Obsidian 紫
    }
    private static readonly Windows.UI.Color CanvasEdgeColor = Windows.UI.Color.FromArgb(255, 96, 102, 112);

    // 强调色（选中边框 / 连线选中 / 手柄）：对齐 App.xaml 的 GlanceAccent（Obsidian 紫），
    private static readonly Windows.UI.Color CanvasAccentDark = Windows.UI.Color.FromArgb(255, 0xA8, 0x82, 0xFF);
    private static readonly Windows.UI.Color CanvasAccentLight = Windows.UI.Color.FromArgb(255, 0x98, 0x73, 0xF7);
    private Windows.UI.Color _canvasAccentColor = CanvasAccentLight;

    // 手柄（P3 连线 + P4 缩放）：四边中点=连线起点，四角=缩放。统一为【圆点】，
    private static readonly string[] CanvasSides = { "top", "bottom", "left", "right" };
    private static readonly string[] CanvasCorners = { "tl", "tr", "bl", "br" };
    private const double CanvasMinNodeWidth = 60;        // 缩放下限（页局部 PDF 点）
    // 高度下限 = 新建卡片默认高：下限不得大于默认高，否则新建空卡一旦放大就再也缩不回
    private const double CanvasMinNodeHeight = CanvasNode.DefaultHeight;
    private static readonly Windows.UI.Color CanvasHandleFill = Windows.UI.Color.FromArgb(255, 255, 255, 255);
    // 手柄尺寸一律用【页局部 PDF 点】（与卡片边框/圆角同一坐标空间）→ 随页面缩放一起放大/缩小，
    private const float CanvasHandleRadius = 6.0f;
    // 四角缩放 / 四边单轴缩放控件：尺寸仍用【页局部 PDF 点】随缩放（它们有光标变换指示，用户要求不改）。
    private const float CanvasHandleHitRadius = 8.0f;
    // ⚠ 四边中点连线控件【落屏尺寸 = clamp(页局部半径 × 缩放, Min, Max)】：对齐 Obsidian ——
    //   · 中缩放区间：页局部半径×缩放 落在 [Min,Max] 内 ⇒ 控件与内容同比例随动（"先随动"）；
    private const float CanvasConnHandleRadiusMinDip = 5.5f;   // 圆点屏幕半径下限（DIP）
    private const float CanvasConnHandleRadiusMaxDip = 14.0f;  // 圆点屏幕半径上限（DIP）
    private const float CanvasConnHandleHitMinDip = 10.0f;     // 触发/显形屏幕半径下限（DIP）
    private const float CanvasConnHandleHitMaxDip = 26.0f;     // 触发/显形屏幕半径上限（DIP）
    private const float CanvasCornerGap = 3.0f;
    // 四角弧边线宽直接复用 CanvasCardSelectedStrokeWidth(=2.0)，保证与选中边框同粗细。
    private const double CanvasToolbarGap = 6;       // 浮动控件与卡片间距（视口 DIP）

    /// <summary>卡片文本排版缓存（文本/可用宽度任一变化才重建；其余帧零重建）。</summary>
    private sealed class CachedCanvasText
    {
        public string? Text;
        public double MaxWidth;
        public double MaxHeight;
        public int LineCount;            // 真实行数（不限高，溢出行也计入）——滚动条显隐/长度用
        public CanvasTextLayout? Layout;
        // 行内 Markdown（只作用于阅读态）：排版用显示串，样式表与底纹矩形随 layout 一起缓存，
        public string DisplayText = "";
        public IReadOnlyList<CanvasMarkdownSpan> Spans = Array.Empty<CanvasMarkdownSpan>();
        public IReadOnlyList<int>? SourceIndexMap;
        public List<(Rect Bounds, CanvasMarkdownSpanKind Kind)> Backdrops = new();
    }

    /// <summary>安装/替换白板数据（UI 线程调用）。节点数组与 id 索引一起原子换入，绘制侧加锁读取。</summary>
    public void SetCanvasData(IReadOnlyList<CanvasNode>? nodes, IReadOnlyList<CanvasEdge>? edges)
    {
        var n = CopyArray(nodes);
        var e = CopyArray(edges);
        var idx = new Dictionary<string, CanvasNode>(n.Length, StringComparer.Ordinal);
        foreach (var node in n)
            if (!string.IsNullOrEmpty(node.Id)) idx[node.Id] = node;
        lock (_canvasLock) { _canvasNodes = n; _canvasEdges = e; _canvasIndex = idx; }

        // 清掉已不存在卡片的滚动偏移（删卡/换文档后避免字典里留垃圾 key）。
        if (_canvasScrollY.Count > 0)
        {
            var keys = new string[_canvasScrollY.Count];
            _canvasScrollY.Keys.CopyTo(keys, 0);
            foreach (var k in keys)
                if (!idx.ContainsKey(k)) _canvasScrollY.Remove(k);
        }

        // 被删/被撤销掉的节点若正被选中或编辑 → 一并作废，避免浮层挂在空气上。
        if (_selectedCanvasNodeId != null && !idx.ContainsKey(_selectedCanvasNodeId))
        {
            _selectedCanvasNodeId = null;
            HideCanvasCardToolbar();
        }
        if (_canvasEditNodeId != null && !idx.ContainsKey(_canvasEditNodeId))
        {
            // 正在编辑的卡被删/撤销 ⇒ 走【完整】退出编辑流程（commit=false：卡已不存在，未提交文本作废）。
            // ⚠ 不能只清标志 + 折叠编辑框：WinUI 里折叠的 RichEditBox 仍握着键盘焦点，会把 F/G/Ctrl+Z
            EndCanvasEdit(commit: false);
        }
        // 被删/被撤销掉的连线若正被选中或悬停 → 一并作废。
        if (_selectedCanvasEdgeId != null && !ContainsEdge(e, _selectedCanvasEdgeId))
        {
            _selectedCanvasEdgeId = null;
            HideCanvasEdgeToolbar();
        }
        if (_hoverCanvasEdgeId != null && !ContainsEdge(e, _hoverCanvasEdgeId))
            _hoverCanvasEdgeId = null;
        if (_hoverCanvasNodeId != null && !idx.ContainsKey(_hoverCanvasNodeId))
            _hoverCanvasNodeId = null;
        // 撤销/重做可能改到【正在编辑】卡片的文本 → 同步编辑框内容，并把光标放到**改动区末尾**。
        // 否则编辑框残留旧文本，用户失焦提交时会用旧文本覆盖掉刚撤销/重做的结果。
        // ⚠ 光标必须落在【改动区末尾】：一律 `SetRange(end,end)` 会让一按 Ctrl+Z 光标就飞到全文末尾，
        //   撤销后想接着改前一句必须重新点回去。
        // ⚠ 判据必须是"模型文本 ≠ 我们最后一次提交的文本"：SetCanvasData 会被拖动/缩放/置顶等操作
        //   连带把整个数组推回来，此时模型里的文本还停在最后一次提交（未提交的输入只活在编辑框里）——
        //   若照样同步，就会把刚删掉的字"复活"，并顺手取消挂起中的去抖提交（用户报告：删了偶尔又出现）。
        //   只有模型文本真被改过（撤销/重做）才说明它是新真相，才允许盖掉编辑框。
        if (_canvasEditNodeId != null && CanvasEditBox != null &&
            idx.TryGetValue(_canvasEditNodeId, out var editNode))
        {
            string t = editNode.Text ?? "";
            if (!string.Equals(t, _canvasLastCommittedText, StringComparison.Ordinal))
            {
                string oldText = GetCanvasEditText();
                if (!string.Equals(oldText, t, StringComparison.Ordinal))
                {
                    SetCanvasEditText(t);
                    // 光标落在【改动区末尾】，而不是"原地保留偏移"：
                    // 原地保留时，撤销"删掉一个字"会把光标留在恢复字**左边**（用户报告），必须再按一次→才到右边。
                    int caret = Math.Clamp(CanvasTextChangeEnd(oldText, t), 0, t.Length);
                    try
                    {
                        var sel = CanvasEditBox.Document.Selection;
                        sel.SetRange(caret, caret);
                    }
                    catch { }
                    // 基线直接写成落点（而不是清成 -1）：清 -1 会让绘制层走"默认右端"的兜底，
                    SetCanvasSelBaseline(caret, caret, caret);
                    ScreenCanvas.Invalidate();   // 自绘光标/选区当帧跟上（SetText 不保证触发 TextChanged）
                }
                // 撤销/重做改到的文本即新的"已入史基线"，并撤销挂起中的去抖提交 ——
                // 否则停顿 400ms 后会把刚撤销掉的文本当成一次新输入又提交回去。
                _canvasLastCommittedText = t;
                StopCanvasEditCommitTimer();
            }
        }
        // 撤销/重做可能正好取消了选中 ⇒ 浮层按"空闲才收"的口径处理。
        CollapseCanvasOverlayIfIdle();

        InvalidateComposite();
        RequestCanvasOverlaySync();
    }

    private static bool ContainsEdge(CanvasEdge[] edges, string id)
    {
        foreach (var e in edges)
            if (string.Equals(e.Id, id, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsArrowEnd(string? end) =>
        !string.IsNullOrEmpty(end) && !string.Equals(end, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析 "#RRGGBB" / "#AARRGGBB" 颜色串；失败返回 null。</summary>
    private static Windows.UI.Color? ParseCanvasColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        string s = hex.TrimStart('#');
        try
        {
            if (s.Length == 6)
                return Windows.UI.Color.FromArgb(255,
                    Convert.ToByte(s.Substring(0, 2), 16),
                    Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16));
            if (s.Length == 8)
                return Windows.UI.Color.FromArgb(
                    Convert.ToByte(s.Substring(0, 2), 16),
                    Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16),
                    Convert.ToByte(s.Substring(6, 2), 16));
        }
        catch { }
        return null;
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color c, float factor)
    {
        factor = Math.Clamp(factor, 0f, 1f);
        return Windows.UI.Color.FromArgb((byte)(c.A * factor), c.R, c.G, c.B);
    }

    private static T[] CopyArray<T>(IReadOnlyList<T>? src)
    {
        if (src == null || src.Count == 0) return Array.Empty<T>();
        var arr = new T[src.Count];
        for (int i = 0; i < src.Count; i++) arr[i] = src[i];
        return arr;
    }

    private bool HasCanvasContent()
    {
        lock (_canvasLock) return _canvasNodes.Length > 0 || _canvasEdges.Length > 0;
    }

    /// <summary>绘制整个白板层。visible 为本次帧的可见页（含 dest），卡片逐页复用页变换。</summary>
    private void DrawWhiteboardLayer(CanvasDrawingSession ds, double z,
        List<(GpuPageInfo Page, Rect Dest)> visible)
    {
        CanvasNode[] nodes;
        CanvasEdge[] edges;
        Dictionary<string, CanvasNode> index;
        lock (_canvasLock)
        {
            nodes = _canvasNodes; edges = _canvasEdges; index = _canvasIndex;
        }
        if (nodes.Length == 0 && edges.Length == 0) return;

        var pages = _pages;
        var savedTransform = ds.Transform;

        // ---- 连线（底层）：必须先于卡片绘制 ⇒ 卡片永远压在连线之上 ----
        // 对齐 Obsidian Canvas：连线只是"卡片之间的说明文字"，卡片才是主体。卡面是不透明的
        // ⚠ 这条绘制顺序同时定死了命中层级 —— 挡在上面的先命中，所以卡片的滚动条/四角/边手柄/
        if (pages.Count > 0 && edges.Length > 0)
        {
            GetViewState(out _, out double eHOff, out double eVOff);
            Rect EdgeDestOf(int idx) => ComputePageDest(idx, z, eHOff, eVOff);
            // 连线在屏幕坐标绘制：会话变换必须为恒等，防止任何上游泄漏的页变换把端点/控制点二次映射 → 连线脱离卡片。
            ds.Transform = Matrix3x2.Identity;
            DrawCanvasEdges(ds, z, pages, EdgeDestOf, index, edges);
            ds.Transform = savedTransform;
        }

        foreach (var (p, dest) in visible)
        {
            bool pageHasCard = false;
            foreach (var node in nodes)
                if (node.GlancePage == p.Index) { pageHasCard = true; break; }
            if (!pageHasCard) continue;

            var pageXf = MakePageXform(dest, z);
            ds.Transform = pageXf.ToMatrix();

            // 逐卡片视口裁剪：屏外卡片本就看不见，不必浪费 fill/描边/文字栅格化（高强度多卡片白板的真瓶颈）。
            const float nodeCullMargin = 48f;
            bool CardVisibleOnScreen(CanvasNode n)
            {
                var a = pageXf.Map(n.X, n.Y);
                var b = pageXf.Map(n.X + n.Width, n.Y + n.Height);
                float minX = MathF.Min(a.X, b.X), maxX = MathF.Max(a.X, b.X);
                float minY = MathF.Min(a.Y, b.Y), maxY = MathF.Max(a.Y, b.Y);
                return !(maxX < -nodeCullMargin || minX > _vpW + nodeCullMargin
                      || maxY < -nodeCullMargin || minY > _vpH + nodeCullMargin);
            }

            string? sel = _selectedCanvasNodeId;
            string? hov = _hoverCanvasNodeId;
            // 手势进行中（拖角缩放 / 拖边连线）：只有"正在操作的那张卡"参与手柄绘制 ——
            bool gesturing = _isCanvasResizing || _isCanvasConnecting;
            string? gestureNodeId = _isCanvasResizing ? _canvasResizeNode?.Id
                                 : (_isCanvasConnecting ? _connFromNodeId : null);
            // 叠放层级：z 序 = _canvasNodes 数组顺序（jsoncanvas 无独立 z 字段）。选中卡片的【永久】置顶
            string? topId = gesturing ? gestureNodeId : sel;
            void DrawCardNode(CanvasNode node)
            {
                bool selected = sel != null && node.Id == sel;
                bool hovered = !selected && hov != null && node.Id == hov;

                // 编辑中的卡片也由 Win2D 绘制（含强调边框与手柄）：三态同引擎 ⇒ 文字不再因"切换渲染引擎"而位移。

                // 强调（选中/悬停/正在操作）只作用于【卡片边框本身】。
                DrawCanvasCard(ds, node, selected || hovered || (gesturing && node.Id == gestureNodeId), z);
                DrawCanvasScrollBar(ds, node);
                if (gesturing)
                {
                    if (node.Id == gestureNodeId) DrawNearestCanvasHandle(ds, node);
                }
                else if (selected || hovered)
                {
                    DrawNearestCanvasHandle(ds, node);
                }
            }

            foreach (var node in nodes)
            {
                if (node.GlancePage != p.Index) continue;
                if (topId != null && node.Id == topId) continue;
                if (!CardVisibleOnScreen(node)) continue;
                DrawCardNode(node);
            }
            if (topId != null && index.TryGetValue(topId, out var topNode) && topNode.GlancePage == p.Index)
                DrawCardNode(topNode);
            ds.Transform = savedTransform;
        }

        // ---- 拉线预览 / 触发键待接引导（顶层）：橡皮筋 + 吸附环，唯一压在卡片之上的"线"----
        // 两者互斥：_isCanvasConnecting 是"按住拖"的重型手势，_connArmActive 是"点两下"的轻量待接态。
        if (pages.Count > 0 && (_isCanvasConnecting || _connArmActive))
        {
            ds.Transform = Matrix3x2.Identity;
            if (_isCanvasConnecting) DrawCanvasConnecting(ds);
            else DrawCanvasConnArmGuide(ds);
            ds.Transform = savedTransform;
        }
    }

    /// <summary>画一张卡片。=true（选中/悬停/手势中）时把【边框本身】加粗—— 不在卡片外另套一层描边（对齐 Obsidian：就是那根边框变色）。未上色卡片同时换成强调色；</summary>
    private void DrawCanvasCard(CanvasDrawingSession ds, CanvasNode node, bool emphasized, double z)
    {
        double w = Math.Max(12, node.Width);
        double h = Math.Max(12, node.Height);
        var rect = new Rect(node.X, node.Y, w, h);
        // 卡面：先铺【不透明白底】（必须不透明 —— 卡片要完全遮住身后的连线/其他卡片），
        // ⚠ 不要把白底直接换成"半透明本色"：那样会变成半透明卡面、透出背景。
        ds.FillRoundedRectangle(rect, CanvasCardRadius, CanvasCardRadius, CanvasCardFill);

        // 边框色（步骤 5）：卡片已上色（node.Color 非空 = "#RRGGBB"）时用它自己的颜色；未上色回落默认灰。
        // 强调态的取舍（刻意如此）。
        // ⚠ "选了首位默认灰"（= #8A8F98）按【未上色】处理：它与未着色在视觉上等价，若当成"已上色"，
        bool customColor = !string.IsNullOrEmpty(node.Color)
            && !string.Equals(node.Color, CanvasCardDefaultGrayHex, StringComparison.OrdinalIgnoreCase);
        var cardColor = customColor ? ResolveCanvasCardStroke(node.Color) : CanvasCardStroke;
        if (customColor)
        {
            // 淡色底纹：与边框同色相、只降低不透明度；叠在白底上 ⇒ 结果仍是不透明卡面。
            ds.FillRoundedRectangle(rect, CanvasCardRadius, CanvasCardRadius,
                Windows.UI.Color.FromArgb(CanvasCardTintAlpha, cardColor.R, cardColor.G, cardColor.B));
        }
        var strokeColor = customColor ? cardColor : (emphasized ? _canvasAccentColor : CanvasCardStroke);
        float strokeWidth = emphasized ? CanvasCardSelectedStrokeWidth : CanvasCardStrokeWidth;
        // ⚠ 边框【不在这里画】：卡片上下内边距为 0（CanvasCardPadY），内容溢出被滚动时首/末行会顶到边线上；

        bool editing = node.Id == _canvasEditNodeId;
        // 编辑态用【实时文本】(_canvasEditText)：打字后到提交前的渲染不再滞后；其余态用节点文本。
        string? drawText = editing ? _canvasEditText : node.Text;
        double tw = w - 2 * CanvasCardPadX;
        double th = h - 2 * CanvasCardPadY;
        CanvasTextLayout? layout = !string.IsNullOrEmpty(drawText)
            ? (editing ? GetCanvasEditTextLayout(tw, th) : GetCanvasTextLayout(node, tw, th))
            : null;
        if (layout == null && !editing)
        {
            DrawCanvasCardFrame(ds, rect, strokeColor, strokeWidth);   // 空卡：只画边框
            return;
        }

        double topPad, scrollY;
        if (editing)
        {
            // 上方自适应留空与阅读态【同一公式、同一口径】（实时文本真实行数 × 标称行高）——
            topPad = ComputeCanvasEditTopPad(tw, th);
            // 滚动由 RichEditBox 内部 ScrollViewer 驱动 ⇒ 读其偏移，但夹到【真实可视滚动范围 visMax】。
            scrollY = TryGetCanvasEditScrollMetrics(node, out _, out _, out double visMax)
                ? Math.Clamp(_canvasEditScrollViewer!.VerticalOffset, 0, visMax)
                : 0;
        }
        else
        {
            // Obsidian 式垂直留白：上方【最多留半行】——空间足够时留半行，不够就挤上去（可为 0）；
            // ⚠ 内容高必须用【不限高的真实行数】（缓存里那个），不能用 layout.LineCount —— 后者已被 maxH 截断，
            double realContentH = GetCachedCanvasLineCount(node, tw, th) * CanvasLineHeight;
            topPad = Math.Min(0.5 * CanvasLineHeight, Math.Max(0, (th - Math.Min(th, realContentH)) / 2.0));
            double maxScroll = Math.Max(0, realContentH - th);
            scrollY = Math.Clamp(GetCanvasScrollY(node.Id), 0, maxScroll);
        }

        // CanvasTextLayout 只排版、不裁剪，超长文本会画到卡片外 → 用图层 clip 兜在卡片内边距区域内。
        var textRect = new Rect(node.X + CanvasCardPadX, node.Y + CanvasCardPadY, tw, th);
        using (ds.CreateLayer(1f, textRect))
        {
            if (layout != null)
            {
                // 行内代码/高亮底纹先铺，文字压在上面。两态各有各的排版串（编辑态标记是显示的），故各自取缓存。
                if (editing) DrawCanvasEditMarkdownBackdrops(ds, textRect, topPad, scrollY);
                else DrawCanvasMarkdownBackdrops(ds, node, textRect, topPad, scrollY);
                // 选区【必须在底纹之后】：底纹不透明，先画选区会被高亮/行内代码整块盖住（拖选看不出来）。
                if (editing) DrawCanvasEditSelection(ds, layout, textRect, topPad, scrollY);
                ds.DrawTextLayout(layout,
                    new Vector2((float)textRect.X, (float)(textRect.Y + topPad - scrollY)), CanvasCardText);
            }
            if (editing) DrawCanvasEditCaret(ds, layout, textRect, topPad, scrollY, z);
        }

        // 边框最后画 ⇒ 内容（含溢出滚动时的首/末行、底纹、选区、光标）一律从边框【下面】滑出去，
        DrawCanvasCardFrame(ds, rect, strokeColor, strokeWidth);
    }

    /// <summary>卡片边框（圆角描边）。单独成方法是因为它必须在【内容绘制之后】调用： 圆角矩形描边以卡片边线为中心，半边落在卡片内侧；若先画边框，溢出滚动的文字会压住它。</summary>
    private static void DrawCanvasCardFrame(CanvasDrawingSession ds, Rect rect,
        Windows.UI.Color strokeColor, float strokeWidth)
    {
        ds.DrawRoundedRectangle(rect, CanvasCardRadius, CanvasCardRadius, strokeColor, strokeWidth);
    }

    /// <summary>编辑态选区高亮：遍历 layout 的每一行，取与选区 [s,e) 的交集段，用 Win2D 画半透明矩形。 与可见文字同一 layout ⇒ 选区与文字像素级对齐（不再是"两引擎各自排版"的近似）。</summary>
    private void DrawCanvasEditSelection(CanvasDrawingSession ds, CanvasTextLayout? layout,
        Rect textRect, double topPad, double scrollY)
    {
        if (_canvasEditNodeId == null || CanvasEditBox == null || layout == null) return;
        if (AnyCanvasGestureActive) return;   // 编辑态手势期间抑制选区绘制（RichEditBox 会顺手选中文字，收尾还原）
        try
        {
            var selColor = GetCanvasSelectionColor();
            // 拖选色（0.35 alpha）覆盖到不透明底纹上的两个"新色"：高亮拖选 / 行内代码拖选各一。
            var selOnHighlight = BlendCanvasSelectionOver(selColor, CanvasMarkdownHighlightBackdrop);
            var selOnCode = BlendCanvasSelectionOver(selColor, CanvasMarkdownCodeBackdrop);
            if (!TryGetCanvasEditSelectionDisplay(out int a, out int b)) return;
            var live = _canvasEditLive;
            string display = live?.DisplayText ?? _canvasEditText;
            var lms = layout.LineMetrics;
            if (lms == null) return;
            float dTop = (float)(textRect.Y + topPad - scrollY);
            // CanvasLineMetrics 没有 CharacterIndex/Length/NewlineLength：行首下标按 CharacterCount（含换行符）逐行累加，
            // 选区跨行时右界取"本行文本末尾"（换行符的 leading）而非换行符之后，否则光标会跳到下一行行首 → 高亮塌成 1px。
            int lineStart = 0;
            // 行矩形按累积 rowTop 平铺：第 i 行 = [rowTop_i, rowTop_i + hgt_i)，相邻行【零重叠零缝隙】。
            float rowTop = 0;
            foreach (var lm in lms)
            {
                int lineEnd = lineStart + lm.CharacterCount;   // CharacterCount 含行尾换行符 ⇒ 即下一行起点
                float hgt = lm.Height > 0 ? lm.Height : (float)CanvasLineHeight;
                int ovs = Math.Max(lineStart, a);
                int ovEnd = (b >= lineEnd) ? lineEnd : b;
                if (ovEnd <= ovs) { lineStart = lineEnd; rowTop += hgt; continue; }
                Vector2 pL = layout.GetCaretPosition(ovs, false);
                Vector2 pR;
                if (b >= lineEnd)
                {
                    // 整行选中：右界 = 本行【文本末尾】，不能取换行符之后（那会跳到下一行行首 → 高亮塌成 1px，
                    int last = lineEnd - 1;
                    bool lastIsNewline = last >= 0 && last < display.Length && display[last] == '\n';
                    pR = layout.GetCaretPosition(last, !lastIsNewline);
                }
                else
                {
                    pR = layout.GetCaretPosition(b - 1, true);
                }
                // 零宽段（选区恰好只含换行符等）不画：Math.Max(1f,…) 会把它强画成 1px 的选色竖条残留（用户报告）。
                float segW = pR.X - pL.X;
                if (segW > 0.05f)
                {
                    var segRect = new Rect(textRect.X + pL.X, dTop + rowTop, segW, hgt);
                    ds.FillRectangle(segRect, selColor);
                    DrawCanvasSelectionBlendOverBackdrops(ds, segRect, textRect.X, rowTop, hgt,
                        selOnHighlight, selOnCode);
                }
                lineStart = lineEnd;
                rowTop += hgt;
            }
        }
        catch { }
    }

    /// <summary>取当前选区在【显示串】里的 [a,b)（已排序、夹紧）。无选区返回 false。 选区是【源串】下标、排版是【显示串】⇒ 隐藏的标记会让两边差若干个字符，必须先映射。 抽出成方法是为了让"画选区"只有一个下标口径（源串混进排版会整段错位）。</summary>
    private bool TryGetCanvasEditSelectionDisplay(out int a, out int b)
    {
        a = b = 0;
        if (_canvasEditNodeId == null || CanvasEditBox == null) return false;
        try
        {
            var live = _canvasEditLive;
            string display = live?.DisplayText ?? _canvasEditText;
            int len = display.Length;
            var sel = CanvasEditBox.Document.Selection;
            int s = Math.Min(sel.StartPosition, sel.EndPosition);
            int e = Math.Max(sel.StartPosition, sel.EndPosition);
            a = live != null ? live.ToDisplay(Math.Clamp(s, 0, _canvasEditText.Length)) : Math.Clamp(s, 0, len);
            b = live != null ? live.ToDisplay(Math.Clamp(e, 0, _canvasEditText.Length)) : Math.Clamp(e, 0, len);
            a = Math.Clamp(a, 0, len);
            b = Math.Clamp(b, 0, len);
            return b > a;
        }
        catch { return false; }
    }

    /// 把本行选区块里【与底纹重叠】的水平段改画合成色（底纹不透明，已把下面的普通选区盖住）。
    private void DrawCanvasSelectionBlendOverBackdrops(CanvasDrawingSession ds, Rect segRect,
        double originX, double rowTop, double rowHeight,
        Windows.UI.Color selOnHighlight, Windows.UI.Color selOnCode)
    {
        if (_canvasEditBackdrops.Count == 0) return;
        foreach (var (bounds, kind) in _canvasEditBackdrops)
        {
            var local = CanvasBackdropRect(bounds, kind);
            // 竖向：与本行选区块零交叠 ⇒ 不是本行的底纹
            if (local.Y + local.Height <= rowTop || local.Y >= rowTop + rowHeight) continue;
            // 水平只取字面框：选区边界 = 字边界 = 光标位置，合成色越出去就是"拖选色超出光标"（用户报告）。
            double gx0 = originX + bounds.X;
            double gx1 = gx0 + bounds.Width;
            double ix0 = Math.Max(segRect.X, gx0);
            double ix1 = Math.Min(segRect.X + segRect.Width, gx1);
            if (ix1 <= ix0) continue;
            // 合成色不透明 ⇒ 直接覆盖刚画的普通选区，边界不叠色、不半透明累积
            ds.FillRectangle(new Rect(ix0, segRect.Y, ix1 - ix0, rowHeight),
                kind == CanvasMarkdownSpanKind.Code ? selOnCode : selOnHighlight);
        }
    }

    /// <summary>拖选色按自身 alpha 合成到【不透明】底纹色之上，返回不透明结果色。 用途：高亮/行内代码底纹在选区之下的场景 —— 重叠处画这个色，于是"高亮被拖选"（偏暖）与 "无高亮拖选"（偏紫）一眼可分。</summary>
    private static Windows.UI.Color BlendCanvasSelectionOver(Windows.UI.Color sel, Windows.UI.Color under)
    {
        float a = sel.A / 255f;
        byte Mix(byte fg, byte bg) =>
            (byte)Math.Clamp((int)Math.Round(fg * a + bg * (1f - a)), 0, 255);
        return Windows.UI.Color.FromArgb(255, Mix(sel.R, under.R), Mix(sel.G, under.G), Mix(sel.B, under.B));
    }

    /// <summary>显示串下标 → 所属行盒（行顶 + 行高），口径与选区/底纹完全一致：按 LineMetrics 逐行累加。 LineMetrics.CharacterCount【含行尾换行符】⇒ 累加值就是下一行的起点；下标落在最后一行（或文本末尾）时取最后一行。</summary>
    private static bool TryGetCanvasLineBox(CanvasTextLayout layout, int displayIndex,
        out float top, out float height)
    {
        top = 0;
        height = 0;
        var lms = layout.LineMetrics;
        if (lms == null || lms.Length == 0) return false;
        int lineStart = 0;
        float rowTop = 0;
        for (int i = 0; i < lms.Length; i++)
        {
            float h = lms[i].Height > 0 ? lms[i].Height : (float)CanvasLineHeight;
            int lineEnd = lineStart + Math.Max(0, lms[i].CharacterCount);
            if (displayIndex < lineEnd || i == lms.Length - 1)
            {
                top = rowTop;
                height = h;
                return true;
            }
            lineStart = lineEnd;
            rowTop += h;
        }
        return false;
    }

    /// <summary>写"选区基线"（绘制层据此差分推断活动端），同时作废整段设定的活动端 —— 两种来源不能并存。</summary>
    private void SetCanvasSelBaseline(int start, int end, int active)
    {
        _canvasSelLastStart = Math.Min(start, end);
        _canvasSelLastEnd = Math.Max(start, end);
        _canvasSelLastActive = active;
        _canvasSelPinActive = -1;
    }

    private void ResetCanvasSelBaseline() => SetCanvasSelBaseline(-1, -1, -1);

    /// <summary>取控件整段设定的活动端（双击选词/三击选段）。选区一旦被别的方式改过（拖选/方向键/点击）就不再匹配 ⇒ 自动失效。</summary>
    private bool TryGetCanvasPinnedActive(int st, int en, out int active)
    {
        active = en;
        if (_canvasSelPinActive < 0) return false;
        if (st != _canvasSelPinStart || en != _canvasSelPinEnd)
        {
            _canvasSelPinActive = -1;
            return false;
        }
        active = _canvasSelPinActive;
        return true;
    }

    /// <summary>控件【整段设定】选区之后确定活动端（双击选词 / 三击选段 / Ctrl+A）。 原生命中在本事件之后才落定选区 ⇒ 必须排到队尾读。活动端一律记【右端】：与 Windows 文本框一致（锚点在词/段首，活动端在末尾）。</summary>
    private void PinCanvasEditWholeSelectionActive()
    {
        if (_canvasEditNodeId == null || CanvasEditBox?.Document == null) return;
        try
        {
            var sel = CanvasEditBox.Document.Selection;
            int len = _canvasEditText.Length;
            int st = Math.Clamp(Math.Min(sel.StartPosition, sel.EndPosition), 0, len);
            int en = Math.Clamp(Math.Max(sel.StartPosition, sel.EndPosition), 0, len);
            if (en <= st) return;   // 点空处没选中任何字 ⇒ 光标位置本身就是真相，不需要 pin
            SetCanvasSelBaseline(st, en, en);   // 先写基线（顺带清掉旧 pin），再记这一次的 pin
            _canvasSelPinStart = st;
            _canvasSelPinEnd = en;
            _canvasSelPinActive = en;
        }
        catch { }
        ResetCaretBlink();
        ScreenCanvas.Invalidate();
    }

    /// <summary>编辑态光标：用 Win2D 自身的 layout（与文字同源）取光标矩形，保证不偏移。1 屏幕像素宽换算成页局部宽度。 仅当编辑框持有焦点且光标处于"亮"相位时才画（亮灭由闪烁定时器控制）。</summary>
    private void DrawCanvasEditCaret(CanvasDrawingSession ds, CanvasTextLayout? layout,
        Rect textRect, double topPad, double scrollY, double z)
    {
        if (_canvasEditNodeId == null || CanvasEditBox == null) return;
        // 手势期间不抑制：边框控件交互不应改动编辑态 —— 插入光标原地照常闪烁（选区在按下瞬间已还原、捕获已交还，
        if (!_canvasCaretVisible || CanvasEditBox.FocusState == FocusState.Unfocused) return;
        try
        {
            int len = _canvasEditText.Length;
            var sel = CanvasEditBox.Document.Selection;
            // 活动端推断：ITextSelection（WinUI 3）没有 ActiveEnd，只有规范化 Start/End ⇒ 只能按"哪种操作会这么改选区"判断。
            int st = Math.Clamp(sel.StartPosition, 0, len);
            int en = Math.Clamp(sel.EndPosition, 0, len);
            int active;
            if (st == en)
            {
                active = st;
                _canvasSelPinActive = -1;   // 选区塌了 ⇒ 整段设定的活动端作废
            }
            // 整段设定的选区（双击选词/三击选段）优先：差分在这条路径上必然误判 ——
            // 上一帧光标恰在词尾时，差分看到的是"起点动了、终点没动"，形状与拖选一模一样。
            else if (TryGetCanvasPinnedActive(st, en, out int pinned)) active = pinned;
            else if (_canvasSelLastStart < 0) active = en;
            else if (st != _canvasSelLastStart && en == _canvasSelLastEnd) active = st;
            else if (en != _canvasSelLastEnd && st == _canvasSelLastStart) active = en;
            else active = Math.Abs(st - _canvasSelLastActive) <= Math.Abs(en - _canvasSelLastActive) ? st : en;
            _canvasSelLastStart = st;
            _canvasSelLastEnd = en;
            _canvasSelLastActive = active;
            int ci = active;
            // 活动端是【源串】偏移，layout 是【显示串】⇒ 过映射后再取坐标（被隐藏的标记会少若干个字符）。
            if (_canvasEditLive is { } live)
                ci = Math.Clamp(live.ToDisplay(active), 0, live.DisplayText.Length);
            float cx, cy, ch;
            if (layout != null)
            {
                Vector2 p = layout.GetCaretPosition(ci, false);
                cx = p.X;
                cy = p.Y;
                // 高度取本行行高（layout 统一行距，所有行 Height 相同）；兜底 CanvasLineHeight。
                var lms = layout.LineMetrics;
                ch = (lms != null && lms.Length > 0 && lms[0].Height > 0) ? lms[0].Height : (float)CanvasLineHeight;
                // 竖向【只认行盒】：GetCaretPosition 的 Y 是按"该字符所属字体"的字面度量给的，
                // 中文字形回落雅黑(winAscent 2080/2048)与拉丁(Segoe UI 2210/2048)差 0.76 DIP@12px
                // ⇒ 打中文时光标下移、打英文复原。行盒来自 LineMetrics（Uniform 行距，与字体无关），
                // 与文字/选区/底纹同一口径 ⇒ 换成它，光标竖向不再随输入内容变。
                if (TryGetCanvasLineBox(layout, ci, out float boxTop, out float boxH))
                {
                    cy = boxTop;
                    ch = boxH;
                }
            }
            else { cx = 0; cy = 0; ch = (float)CanvasLineHeight; }
            float cw = (float)(1.0 / Math.Max(z, 0.01));
            ds.FillRectangle(new Rect(textRect.X + cx, (float)(textRect.Y + topPad - scrollY) + cy,
                cw, Math.Max(1f, ch)), CanvasCardText);
        }
        catch { }
    }

    /// <summary>卡片内滚动条的几何（页局部坐标）；返回是否应当显示，rect 为条的矩形。 **两条绘制路径共用**：阅读/选中态由 Win2D 画、编辑态由 XAML 层画 —— 该函数保证两者位置/尺寸/圆角口径完全一致。</summary>
    private bool TryGetCanvasScrollBarRect(CanvasNode node, out Rect rect)
    {
        rect = default;
        double w = Math.Max(12, node.Width);
        double h = Math.Max(12, node.Height);

        // 内容高 / 可视高 / 滚动比例。
        // · 编辑态 → 由宿主 ScrollViewer 的 ScrollableHeight 换算【真实内容/可视/最大滚动】
        //   （见 TryGetCanvasEditScrollMetrics）。仍以宿主实测为准，⛔ 绝不用"Win2D 量出的行数 × 行高"
        //   近似：两者换行点不同（字体度量/可用宽度/字距都有差），行数会算错 ⇒ 表现为"输入到溢出时
        //   出现偏晚、删到不溢出了却迟迟不消失"。
        var sv = _canvasEditScrollViewer;
        double contentH, bodyH, travel;
        if (node.Id == _canvasEditNodeId && sv != null &&
            TryGetCanvasEditScrollMetrics(node, out double visH, out double txtH, out double visMax))
        {
            // 宿主底部那圈"手柄命中带"padding 不是真实内容 ⇒ 新建空卡（只有 1 行）不会因它被判为
            // 溢出而弹出滚动条（用户报告的"新建即见滚动条"）。
            if (visMax <= 0.5) return false;   // 真实文本未溢出可视区 → 不显示
            contentH = txtH;
            bodyH = visH;
            travel = Math.Clamp(Math.Max(0, sv.VerticalOffset) / visMax, 0, 1);
        }
        else
        {
            bodyH = Math.Max(8, h - 2 * CanvasCardPadY);
            double contentW = Math.Max(1, w - 2 * CanvasCardPadX);
            contentH = GetCachedCanvasLineCount(node, contentW, bodyH) * CanvasLineHeight;
            travel = Math.Clamp(GetCanvasScrollY(node.Id) / Math.Max(0.0001, contentH - bodyH), 0, 1);
        }
        if (contentH <= bodyH + 0.5) return false;                      // 未溢出 → 不显示

        double barW = CanvasScrollBarWidth;
        double inset = CanvasCardSelectedStrokeWidth + CanvasScrollBarGap;
        double trackTop = CanvasCardPadY + inset;
        double trackH = (h - CanvasCardPadY - inset) - trackTop;
        double barRight = w - inset;
        if (trackH <= 4 || barRight - barW <= 0) return false;

        double ratio = Math.Clamp(bodyH / contentH, 0.05, 1.0);
        double barH = Math.Clamp(trackH * ratio, Math.Min(CanvasScrollBarMinH, trackH), trackH);
        double x = node.X + barRight - barW;
        double y = node.Y + trackTop + (trackH - barH) * travel;
        rect = new Rect(x, y, barW, barH);
        return true;
    }

    private int GetCachedCanvasLineCount(CanvasNode node, double contentW, double boxH)
    {
        _ = GetCanvasTextLayout(node, contentW, Math.Max(1, boxH));
        return _canvasTextCache.GetValue(node, _ => new CachedCanvasText()).LineCount;
    }

    /// 编辑态下的指针按下（handledEventsToo 通道）。
    private void OnCanvasEditBoxPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_canvasEditNodeId == null) return;
        var pt = e.GetCurrentPoint(Viewer);
        bool isRight = pt.Properties.IsRightButtonPressed;
        // 与根级记录同源的 4 个字段（这里只补设备/左右键，时刻由 RecordCanvasEditPressOrigin 记）。
        RecordCanvasEditPressOrigin(e);
        if (isRight) return;   // 右键 = 要菜单，交给 ContextMenuOpening（见 IsCanvasEditMenuRequestAuthorized）
        if (!pt.Properties.IsLeftButtonPressed) return;
        // 左键落在文本区 = 改光标 / 重开拖选 ⇒ 右键菜单的落点与可用性都失效，先收起。
        HideCanvasEditContextMenu();
        var p = pt.Position;
        // 双击的第二下：**什么都不做**。
        bool entryDoubleTap = false;
        if (_canvasEditEntryVp is { } entryVp && _canvasEditEntryTick != 0)
        {
            double elapsedMs = (Stopwatch.GetTimestamp() - _canvasEditEntryTick) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs <= CanvasDoubleClickWindowMs &&
                Math.Abs(p.X - entryVp.X) <= CanvasDoubleClickSlopDip &&
                Math.Abs(p.Y - entryVp.Y) <= CanvasDoubleClickSlopDip)
            {
                entryDoubleTap = true;
                _canvasEditEntryTick = 0;   // 只处理一次（第三下起交回原生行为）
                e.Handled = true;           // 别再冒泡给 Viewer（那边会把这次按下当成新的卡片手势）
                int restore = _canvasEditEntryCaret;
                if (restore >= 0)
                {
                    // 原生在本事件处理之后才落定选区 ⇒ 排到队尾覆盖回去，才能赢。
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_canvasEditNodeId == null) return;
                        try
                        {
                            var sel = CanvasEditBox.Document.Selection;
                            sel.SetRange(restore, restore);
                            ResetCanvasSelBaseline();
                        }
                        catch { }
                        ResetCaretBlink();
                        ScreenCanvas.Invalidate();
                    });
                }
            }
        }
        // 点击定位接管（C）：可见串已按光标隐藏了部分标记 ⇒ 原生命中测试按【文档排版】算会偏，
        if (!entryDoubleTap)
        {
            int docOffset = MapCanvasEditPointToDocOffset(p);
            if (docOffset >= 0)
            {
                _canvasEditDragAnchor = docOffset;
                _canvasEditDragLast = docOffset;
                // 双击/三击的语义不同（选词 vs 选段），必须分清次数。
                int clicks = CountCanvasEditClicks(p);
                if (clicks == 2)
                {
                    ExpandCanvasEditLine(docOffset);
                    // CJK 卡片：RichEdit 的 tomWord 对 SetText 灌入的中文只按【单字】断词（给 run 设语言标签
                    // 也纠正不了，实测无效）⇒ 自己用系统分词器取词。拉丁文本原生就选整词，只补记活动端。
                    if (ContainsCjk(_canvasEditText)) SelectCanvasEditWordAt(docOffset);
                    else DispatcherQueue.TryEnqueue(PinCanvasEditWholeSelectionActive);
                }
                else if (clicks >= 3)
                {
                    // 三击 = 整段（tomParagraph，与语言无关）⇒ 交给原生，只补记活动端。
                    ExpandCanvasEditLine(docOffset);
                    DispatcherQueue.TryEnqueue(PinCanvasEditWholeSelectionActive);
                }
                else ApplyCanvasEditCaretAt(docOffset);
            }
        }
        _canvasPointerPos = p;
        _canvasPointerValid = true;
        UpdateCanvasNodeHover(p);
        ResetCaretBlink();
        ScreenCanvas.Invalidate();
        // 指针在编辑卡文本区：工字作用域轮询（25ms）会自愈维持（含按住捕获期间）；此处只同步标志与元素级光标。
        _canvasPointerOnEditCard = true;
        CanvasEditBox.OverrideCursor(CanvasIBeamCursor);
    }

    /// <summary>视口坐标 → 命中某张卡的滚动条。命中区 = 滚动条矩形（页局部）外扩一点屏幕容差， 只认【真的画了滚动条】的卡片（TryGetCanvasScrollBarRect 为准）⇒ 不会误抢边框手柄。</summary>
    private bool TryHitCanvasScrollBar(Point vp, out CanvasNode? node)
    {
        node = null;
        if (!_annotationsVisible) return false;
        if (!CanvasViewportToPage(vp, out int pageIndex, out double px, out double py)) return false;
        GetViewState(out double z, out double hOff, out double vOff);
        if (z <= 0) return false;
        var xf = MakePageXform(ComputePageDest(pageIndex, z, hOff, vOff), z);
        double tol = CanvasScrollBarHitTol / Math.Max(0.0001f, xf.S);   // 屏幕 DIP → 页局部

        CanvasNode[] nodes;
        CanvasNode? selNode = null;
        string? sel = _selectedCanvasNodeId;
        lock (_canvasLock)
        {
            nodes = _canvasNodes;
            if (sel != null) _canvasIndex.TryGetValue(sel, out selNode);
        }
        // 命中顺序与绘制顺序一致：选中的卡片被置顶绘制，其滚动条也压在最上层 ⇒ 先判它。
        if (selNode != null && selNode.GlancePage == pageIndex && TryGetCanvasScrollBarRect(selNode, out var sr) &&
            px >= sr.X - tol && px <= sr.X + sr.Width + tol &&
            py >= sr.Y - tol && py <= sr.Y + sr.Height + tol)
        {
            node = selNode;
            return true;
        }
        for (int i = nodes.Length - 1; i >= 0; i--)       // 取绘制顺序最上层者
        {
            var n = nodes[i];
            if (n.GlancePage != pageIndex) continue;
            if (!TryGetCanvasScrollBarRect(n, out var r)) continue;
            if (px >= r.X - tol && px <= r.X + r.Width + tol &&
                py >= r.Y - tol && py <= r.Y + r.Height + tol)
            {
                node = n;
                return true;
            }
        }
        return false;
    }

    private static void GetCanvasScrollTrackGeom(CanvasNode node, out double trackTop, out double trackH)
    {
        double h = Math.Max(12, node.Height);
        double inset = CanvasCardSelectedStrokeWidth + CanvasScrollBarGap;
        trackTop = CanvasCardPadY + inset;
        trackH = (h - CanvasCardPadY - inset) - trackTop;
    }

    private void BeginCanvasScrollDrag(CanvasNode node, Point vp)
    {
        if (!CanvasViewportToPage(vp, out _, out _, out double py)) return;
        _isCanvasScrollDragging = true;
        LockCanvasCursor(CanvasIdcArrow, node.Id == _canvasEditNodeId);
        _canvasScrollDragNodeId = node.Id;
        // 拖动期间不更新 _canvasPointerPos（防手柄随光标漂移），但若拖动前【已经显示了】某个边缘控件，
        _canvasPointerValid = false;
        _canvasScrollDragStartPointerY = py;
        _canvasScrollDragStartScrollY = node.Id == _canvasEditNodeId
            ? (_canvasEditScrollViewer?.VerticalOffset ?? 0)
            : GetCanvasScrollY(node.Id);
    }

    /// <summary>进入卡片拖动：一次写全基准（页局部起点 + 卡片原始位置供撤销）。</summary>
    private void SeedCanvasDrag(CanvasNode node, int page, double px, double py)
    {
        _isCanvasDragging = true;
        _canvasDragNode = node;
        _canvasDragPage = page;
        _canvasDragStartPx = px;
        _canvasDragStartPy = py;
        _canvasDragOrigX = node.X;
        _canvasDragOrigY = node.Y;
    }

    /// 【编辑态边框控件的信号切割】把"编辑卡边框控件命中带"从编辑框的命中区里【挖掉】。
    /// 否则用控件时编辑态会被改动；事后用快照把光标跳回去只是补丁，信号仍然进了编辑框。
    private void SyncCanvasEditHitCarve(Point vp)
    {
        if (_canvasEditNodeId == null || CanvasEditBox == null) return;
        if (AnyCanvasGestureActive) return;
        // 角/边/滚动条带 → 挖除编辑框命中（其光标由 ProtectedCursor 经系统槽显示）；
        // 拉线控件（四边中点圆形手柄）同样挖除：否则其卡片内半区被编辑框盖住 → 显示原生工字且吃掉按下、无法拉线。
        bool onBand = ResolveEditHoverCursor(vp) != null || TryHitCanvasHandle(vp, out _, out _);
        if (CanvasEditBox.IsHitTestVisible != !onBand) CanvasEditBox.IsHitTestVisible = !onBand;
        // 命中带内 = 指针在"控件"上而非文本上 ⇒ 工字作用域立即让出（单一真相源，避免挖除生效前的 1 帧工字↔手型互切）。
        if (onBand) _canvasPointerOnEditCard = false;
        // 把命中区还回编辑框时，元素级光标同步回工字（否则会残留上一个控件带的 resize/箭头光标一帧）。
        if (!onBand) CanvasEditBox.OverrideCursor(CanvasIBeamCursor);
    }

    private bool AnyCanvasGestureActive =>
        _isCanvasScrollDragging || _isCanvasResizing || _isCanvasConnecting || _isCanvasDragging;

    /// <summary>拖动中：把指针位移按「轨道可移动长度 : 可滚动量」线性映射成滚动偏移变化。 编辑态驱动 TextBox 内部的 ScrollViewer，其余态驱动本卡内存偏移。</summary>
    private void UpdateCanvasScrollDrag(Point vp)
    {
        var id = _canvasScrollDragNodeId;
        if (id == null) return;
        CanvasNode? node = null;
        lock (_canvasLock) _canvasIndex.TryGetValue(id, out node);
        if (node == null) return;
        if (!CanvasViewportToPage(vp, out _, out _, out double py)) return;
        double delta = py - _canvasScrollDragStartPointerY;

        // 编辑态：驱动 TextBox 内部 ScrollViewer；长度/行程都用【真实可视口径】（见 TryGetCanvasEditScrollMetrics），
        if (id == _canvasEditNodeId)
        {
            var sv = _canvasEditScrollViewer;
            if (sv == null ||
                !TryGetCanvasEditScrollMetrics(node, out double visH, out double txtH, out double visMax) ||
                visMax <= 0.5) return;   // 真实文本未溢出（如新建空卡）⇒ 没有条可拖
            GetCanvasScrollTrackGeom(node, out _, out double th);
            double bar = Math.Clamp(th * Math.Clamp(visH / txtH, 0.05, 1.0), Math.Min(CanvasScrollBarMinH, th), th);
            double movable = Math.Max(1, th - bar);
            double next = Math.Clamp(_canvasScrollDragStartScrollY + delta * visMax / movable, 0, visMax);
            sv.ChangeView(null, next, null, true);
            return;
        }

        double h = Math.Max(12, node.Height);
        double bodyH = Math.Max(8, h - 2 * CanvasCardPadY);
        double contentW = Math.Max(1, Math.Max(12, node.Width) - 2 * CanvasCardPadX);
        double contentH = GetCachedCanvasLineCount(node, contentW, bodyH) * CanvasLineHeight;
        double maxScroll = Math.Max(0, contentH - bodyH);
        if (maxScroll <= 0) return;

        GetCanvasScrollTrackGeom(node, out _, out double trackH);
        double barH = Math.Clamp(trackH * Math.Clamp(bodyH / contentH, 0.05, 1.0),
            Math.Min(CanvasScrollBarMinH, trackH), trackH);
        double movable2 = Math.Max(1, trackH - barH);

        double newScroll = _canvasScrollDragStartScrollY + delta * maxScroll / movable2;
        if (SetCanvasScrollY(id, newScroll, maxScroll))
            InvalidateOverlayThrottled();
    }

    /// <summary>滚轮滚动卡片内容。**仅在【选中态 / 编辑态】且指针停在内容溢出的卡片上时消费**本次滚轮； 阅读态（未选中）返回 false ⇒ 照常翻页 —— 与 Obsidian 一致。</summary>
    private bool TryScrollCanvasByWheel(Point vp, double wheelDelta)
    {
        if (!_annotationsVisible || wheelDelta == 0) return false;
        if (!CanvasViewportToPage(vp, out int pageIndex, out double px, out double py)) return false;

        CanvasNode? hit = null;
        CanvasNode[] nodes;
        lock (_canvasLock) nodes = _canvasNodes;
        for (int i = nodes.Length - 1; i >= 0; i--)
        {
            var n = nodes[i];
            if (n.GlancePage != pageIndex) continue;
            if (px >= n.X && px <= n.X + n.Width && py >= n.Y && py <= n.Y + n.Height) { hit = n; break; }
        }
        if (hit == null) return false;

        bool editing = hit.Id == _canvasEditNodeId;
        // 权限：编辑态恒可滚；非编辑态只有【选中卡】可滚（阅读态点它只选中、不滚动）。
        if (!editing && hit.Id != _selectedCanvasNodeId) return false;

        double step = CanvasLineHeight * 3;
        if (editing)
        {
            var sv = _canvasEditScrollViewer;
            // 按【真实可视滚动范围】判定：宿主那圈"手柄命中带"padding 会凭空多出一小段可滚动量 ⇒
            // 新建空卡（1 行）其实没溢出却"能滚"。没真实溢出就不消费，交给页面翻页。
            if (sv == null ||
                !TryGetCanvasEditScrollMetrics(hit, out _, out _, out double visMax) || visMax <= 0.5)
                return false;
            double next = Math.Clamp(sv.VerticalOffset - wheelDelta / 120.0 * step, 0, visMax);
            sv.ChangeView(null, next, null, true);
            return true;
        }

        double h = Math.Max(12, hit.Height);
        double bodyH = Math.Max(8, h - 2 * CanvasCardPadY);
        double contentW = Math.Max(1, Math.Max(12, hit.Width) - 2 * CanvasCardPadX);
        double contentH = GetCachedCanvasLineCount(hit, contentW, bodyH) * CanvasLineHeight;
        double maxScroll = Math.Max(0, contentH - bodyH);
        if (maxScroll <= 0) return false;        // 未溢出 → 不消费，交给页面翻页
        if (SetCanvasScrollY(hit.Id, GetCanvasScrollY(hit.Id) - wheelDelta / 120.0 * step, maxScroll))
            InvalidateOverlayThrottled();
        return true;
    }

    /// <summary>Win2D 侧绘制滚动条 —— 【阅读/选中/编辑三态共用】。编辑框是 Opacity=0 的不可见宿主， 不存在"不透明白底遮住画布"，故滚动条一律画在 Win2D 上（三态同一口径、同一观感）。</summary>
    private void DrawCanvasScrollBar(CanvasDrawingSession ds, CanvasNode node)
    {
        if (!TryGetCanvasScrollBarRect(node, out var rect)) return;
        float r = (float)(rect.Width / 2);
        ds.FillRoundedRectangle(rect, r, r, CanvasScrollBarColor);
    }

    /// <summary>取（阅读/选中态）卡片文本的 Win2D layout（走 ConditionalWeakTable 缓存）。 顺带用"高度不限"的 layout 缓存真实行数，供滚动条显隐/长度与上方留白公式使用。</summary>
    private CanvasTextLayout? GetCanvasTextLayout(CanvasNode node, double maxW, double maxH)
    {
        if (maxW <= 0 || maxH <= 0) return null;
        var cache = _canvasTextCache.GetValue(node, _ => new CachedCanvasText());
        if (string.IsNullOrEmpty(node.Text))
        {
            // 空文本必须【显式清空缓存】：早退不走下面的重建分支，缓存里的 Layout/LineCount/底纹会停在
            // 上一次的内容上 ⇒ GetCachedCanvasLineCount 拿旧行数算出"内容比卡片高"，于是已经清空的卡片
            // 仍画出滚动条、还能滚（用户报告）。清空后行数记 0：没有文本就不存在溢出。
            cache.Layout?.Dispose();
            cache.Layout = null;
            cache.LineCount = 0;
            cache.Text = node.Text;
            cache.DisplayText = string.Empty;
            cache.Spans = Array.Empty<CanvasMarkdownSpan>();
            cache.SourceIndexMap = null;
            cache.Backdrops.Clear();
            cache.MaxWidth = maxW;
            cache.MaxHeight = maxH;
            return null;
        }
        if (cache.Layout != null && cache.Text == node.Text &&
            Math.Abs(cache.MaxWidth - maxW) < 0.5 && Math.Abs(cache.MaxHeight - maxH) < 0.5)
            return cache.Layout;

        cache.Layout?.Dispose();
        var format = EnsureCanvasTextFormat(ScreenCanvas);
        var md = CanvasMarkdown.Parse(node.Text);
        cache.DisplayText = md.DisplayText;
        cache.Spans = md.Spans;
        cache.SourceIndexMap = md.SourceIndexMap;
        cache.Layout = new CanvasTextLayout(ScreenCanvas, md.DisplayText, format, (float)maxW, (float)maxH);
        ApplyCanvasMarkdownFormatting(cache.Layout, md.DisplayText.Length, md.Spans);
        BuildCanvasMarkdownBackdrops(cache);
        // 顺带缓存【真实行数】：用高度不限的 layout 量，溢出的行也计入 —— 滚动条的显隐与长度要用它。
        try
        {
            using var full = new CanvasTextLayout(ScreenCanvas, md.DisplayText, format, (float)maxW, 1e5f);
            cache.LineCount = Math.Max(1, full.LineCount);
        }
        catch { cache.LineCount = 1; }
        cache.Text = node.Text;
        cache.MaxWidth = maxW;
        cache.MaxHeight = maxH;
        return cache.Layout;
    }

    /// <summary>把区间样式套到 layout 上。只做【不改变行高】的样式（字重/字形/删除线/等宽字体）—— 一旦改字号（标题等），"行数 × 标称行高"的滚动与留白公式就会失准，那部分属 P2，另行处理。</summary>
    private static void ApplyCanvasMarkdownFormatting(CanvasTextLayout layout, int textLength,
        IReadOnlyList<CanvasMarkdownSpan> spans)
    {
        if (spans.Count == 0) return;
        foreach (var span in spans)
        {
            int start = Math.Clamp(span.Start, 0, textLength);
            int count = Math.Clamp(span.Length, 0, textLength - start);
            if (count <= 0) continue;
            switch (span.Kind)
            {
                case CanvasMarkdownSpanKind.Bold:
                    layout.SetFontWeight(start, count, CanvasMarkdownBoldWeight);
                    break;
                case CanvasMarkdownSpanKind.Italic:
                    layout.SetFontStyle(start, count, Windows.UI.Text.FontStyle.Italic);
                    break;
                case CanvasMarkdownSpanKind.Strike:
                    layout.SetStrikethrough(start, count, true);
                    break;
                case CanvasMarkdownSpanKind.Code:
                    layout.SetFontFamily(start, count, CanvasMarkdownCodeFontFamily);
                    break;
                // Highlight 无字体属性可设，只在 Draw 阶段垫底纹 —— 见 BuildCanvasMarkdownBackdrops。
            }
        }
    }

    /// <summary>预先算好行内代码/高亮的底纹矩形（layout 坐标，绘制时再平移到文本原点）。 横向用 GetCharacterRegions 给的实际字框（代码跨行时自然分成多个矩形）； 竖向**不用**它给的整行高，而是按该行基线收到贴合字面。</summary>
    private static void BuildCanvasMarkdownBackdrops(CachedCanvasText cache)
    {
        BuildCanvasMarkdownBackdrops(cache.Layout, cache.DisplayText.Length, cache.Spans, cache.Backdrops);
    }

    /// 底纹矩形计算的唯一实现（阅读态 / 编辑态共用，两态绝不会各算一套）。
    private static void BuildCanvasMarkdownBackdrops(CanvasTextLayout? layout, int textLength,
        IReadOnlyList<CanvasMarkdownSpan> spans, List<(Rect Bounds, CanvasMarkdownSpanKind Kind)> into)
    {
        into.Clear();
        if (layout == null || spans.Count == 0) return;

        // 每行的行顶与基线（CanvasLineMetrics.Baseline = 从【本行行顶】到基线，故基线要加行顶）。
        // ⚠ LineMetrics 是【数组】（CanvasLineMetrics[]），取长度用 Length —— 写成 Count 会被解析成 LINQ 的
        var lms = layout.LineMetrics;
        float[]? lineTops = null;
        float[]? lineBaselines = null;
        if (lms != null && lms.Length > 0)
        {
            lineTops = new float[lms.Length];
            lineBaselines = new float[lms.Length];
            float top = 0;
            for (int i = 0; i < lms.Length; i++)
            {
                lineTops[i] = top;
                lineBaselines[i] = lms[i].Baseline;
                top += lms[i].Height > 0 ? lms[i].Height : CanvasLineHeight;
            }
        }

        foreach (var span in spans)
        {
            if (span.Kind != CanvasMarkdownSpanKind.Code && span.Kind != CanvasMarkdownSpanKind.Highlight) continue;
            int start = Math.Clamp(span.Start, 0, textLength);
            int count = Math.Clamp(span.Length, 0, textLength - start);
            if (count <= 0) continue;
            foreach (var region in layout.GetCharacterRegions(start, count))
            {
                var bounds = region.LayoutBounds;
                if (bounds.Width <= 0 || bounds.Height <= 0) continue;

                double y = bounds.Y;
                double h = bounds.Height;
                if (lineTops != null && lineBaselines != null)
                {
                    // 判定 region 落在哪一行：拿它的【竖直中点】去比行区间 —— 不依赖 region 顶边是否
                    float mid = (float)(bounds.Y + bounds.Height * 0.5);
                    int line = -1;
                    for (int i = 0; i < lineTops.Length; i++)
                    {
                        float lineBottom = i + 1 < lineTops.Length ? lineTops[i + 1] : float.MaxValue;
                        if (mid >= lineTops[i] && mid < lineBottom) { line = i; break; }
                    }
                    if (line < 0) line = lineTops.Length - 1;   // 越界（负坐标等）兜底到最后一行

                    float baseline = lineTops[line] + lineBaselines[line];
                    if (baseline > lineTops[line])
                    {
                        y = baseline - CanvasMarkdownBackdropAscent * CanvasCardFontSize;
                        h = (CanvasMarkdownBackdropAscent + CanvasMarkdownBackdropDescent) * CanvasCardFontSize;
                    }
                }
                into.Add((new Rect(bounds.X, y, bounds.Width, h), span.Kind));
            }
        }
    }

    /// <summary>画行内代码/高亮底纹（必须在文字之前调，才在文字下面）。</summary>
    private void DrawCanvasMarkdownBackdrops(CanvasDrawingSession ds, CanvasNode node, Rect textRect,
        double topPad, double scrollY)
    {
        if (!_canvasTextCache.TryGetValue(node, out var cache) || cache.Backdrops.Count == 0) return;
        DrawCanvasMarkdownBackdropRects(ds, cache.Backdrops, textRect, topPad, scrollY);
    }

    private void DrawCanvasEditMarkdownBackdrops(CanvasDrawingSession ds, Rect textRect,
        double topPad, double scrollY)
    {
        if (_canvasEditBackdrops.Count == 0) return;
        DrawCanvasMarkdownBackdropRects(ds, _canvasEditBackdrops, textRect, topPad, scrollY);
    }

    /// 底纹矩形的唯一口径（layout 坐标，未加文字原点）。
    /// 想"不咬字"只能靠字体自身的边距，不能加宽矩形。
    private static Rect CanvasBackdropRect(Rect bounds, CanvasMarkdownSpanKind kind)
    {
        if (kind == CanvasMarkdownSpanKind.Code)
            return new Rect(bounds.X, bounds.Y - 0.5, bounds.Width, bounds.Height + 1.0);
        return new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    /// <summary>画底纹矩形（<b>必须在文字之前</b>调，才在文字下面；也必须在选区之前，否则不透明底纹会盖掉选区）。</summary>
    private static void DrawCanvasMarkdownBackdropRects(CanvasDrawingSession ds,
        List<(Rect Bounds, CanvasMarkdownSpanKind Kind)> backdrops, Rect textRect, double topPad,
        double scrollY)
    {
        double ox = textRect.X;
        double oy = textRect.Y + topPad - scrollY;
        // ⚠ 底纹【不做】"内缩半个描边"的夹边：卡片边框现在画在内容之上（见 DrawCanvasCard），
        //   （用户报告）。底纹与文字必须共用同一个裁剪区（外层 layer = textRect）同一口径。
        foreach (var (bounds, kind) in backdrops)
        {
            var local = CanvasBackdropRect(bounds, kind);
            var rect = new Rect(local.X + ox, local.Y + oy, local.Width, local.Height);

            if (kind == CanvasMarkdownSpanKind.Code)
                ds.FillRoundedRectangle(rect, 2f, 2f, CanvasMarkdownCodeBackdrop);
            else
                ds.FillRectangle(rect, CanvasMarkdownHighlightBackdrop);
        }
    }

    // 编辑态用单独缓存：编辑中的卡片文字以【实时文本】(_canvasEditText) 排版，不能与节点文本(node.Text)的缓存混用
    // （否则提交前的实时文本会污染节点缓存）。每次编辑进入只可能有一张卡 ⇒ 单槽即可。
    private CanvasTextLayout? _canvasEditTextLayout;
    private int _canvasEditTextLayoutKey = -1;
    private double _canvasEditTextLayoutW, _canvasEditTextLayoutH;
    private int _canvasEditTextLayoutLineCount = 1;
    private readonly List<(Rect Bounds, CanvasMarkdownSpanKind Kind)> _canvasEditBackdrops = new();

    // ---- 实时渲染（C，Obsidian 式逐处展开）----
    private CanvasMarkdownLiveResult? _canvasEditLive;
    private string _canvasEditLiveSrc = "";
    private int _canvasEditLiveVersion;             // 每次重算 +1：布局缓存据此失效（不拼字符串 key，避免每帧分配）
    private int _canvasEditLiveA = -1, _canvasEditLiveB = -1;
    private int _canvasEditLiveE0 = -2, _canvasEditLiveE1 = -2;
    private bool _canvasEditLiveAll;
    // 双击展开整行：只记"行首偏移 + 是否生效"，展开范围每次按【当前光标行】现算 ——
    private bool _canvasEditExpandActive;
    private int _canvasEditExpandLineStart = -1;
    private int _canvasEditDragAnchor = -1;
    private int _canvasEditDragLast = -1;
    private int _canvasEditPendingCaret = -1;
    private long _canvasEditClickTick;
    private Point _canvasEditClickVp;
    private int _canvasEditClicks;   // 连击计数：1 = 单击，2 = 双击（选词），≥3 = 三击（选段）

    /// <summary>按当前光标/选区/展开状态算出这一帧的可见串（含双向映射）。参数全没变时直接返回缓存 —— 绘制每帧都会调用它，故这里刻意零字符串分配（不拼 key，改比"源串引用 + 四个下标 + 全选标志"）。</summary>
    private CanvasMarkdownLiveResult BuildCanvasEditLive(string text)
    {
        int a = 0, b = 0;
        try
        {
            var sel = CanvasEditBox.Document.Selection;
            a = Math.Clamp(sel.StartPosition, 0, text.Length);
            b = Math.Clamp(sel.EndPosition, 0, text.Length);
        }
        catch { }
        if (a > b) (a, b) = (b, a);

        // 光标所在行（展开范围按它现算）
        int lineStart = a;
        while (lineStart > 0 && text[lineStart - 1] != '\n') lineStart--;
        // 双击展开的行只在"光标仍在该行内"时有效：点/移到别的行 = 自动收起。
        if (_canvasEditExpandActive && lineStart != _canvasEditExpandLineStart) _canvasEditExpandActive = false;
        int e0 = -1, e1 = -1;
        if (_canvasEditExpandActive)
        {
            e0 = lineStart;
            e1 = lineStart;
            while (e1 < text.Length && text[e1] != '\n') e1++;
        }

        // 全选 = 展开全文标记（用户口径："全选才是展开全部文字的语法"）。
        bool expandAll = text.Length > 0 && a == 0 && b == text.Length;
        if (_canvasEditLive != null && ReferenceEquals(_canvasEditLiveSrc, text) &&
            a == _canvasEditLiveA && b == _canvasEditLiveB &&
            e0 == _canvasEditLiveE0 && e1 == _canvasEditLiveE1 &&
            expandAll == _canvasEditLiveAll)
            return _canvasEditLive;

        _canvasEditLive = CanvasMarkdown.ParseLive(text, a, b, e0, e1, expandAll);
        _canvasEditLiveSrc = text;
        _canvasEditLiveA = a;
        _canvasEditLiveB = b;
        _canvasEditLiveE0 = e0;
        _canvasEditLiveE1 = e1;
        _canvasEditLiveAll = expandAll;
        _canvasEditLiveVersion++;
        return _canvasEditLive;
    }

    private CanvasTextLayout? GetCanvasEditTextLayout(double maxW, double maxH)
    {
        if (maxW <= 0 || maxH <= 0) return null;
        var live = BuildCanvasEditLive(_canvasEditText);
        string display = live.DisplayText;
        if (display.Length == 0) return null;
        if (_canvasEditTextLayout != null && _canvasEditTextLayoutKey == _canvasEditLiveVersion &&
            Math.Abs(_canvasEditTextLayoutW - maxW) < 0.5 && Math.Abs(_canvasEditTextLayoutH - maxH) < 0.5)
            return _canvasEditTextLayout;
        _canvasEditTextLayout?.Dispose();
        var format = EnsureCanvasTextFormat(ScreenCanvas);
        _canvasEditTextLayout = new CanvasTextLayout(ScreenCanvas, display, format, (float)maxW, (float)maxH);
        // 样式区间由 ParseLive 直接给在【显示串】坐标上（含单边语法延到文末），这里不用再换算。
        ApplyCanvasMarkdownFormatting(_canvasEditTextLayout, display.Length, live.Spans);
        BuildCanvasEditBackdrops(_canvasEditTextLayout, display.Length, live.Spans);
        try
        {
            using var full = new CanvasTextLayout(ScreenCanvas, display, format, (float)maxW, 1e5f);
            _canvasEditTextLayoutLineCount = Math.Max(1, full.LineCount);
        }
        catch { _canvasEditTextLayoutLineCount = 1; }
        _canvasEditTextLayoutKey = _canvasEditLiveVersion;
        _canvasEditTextLayoutW = maxW;
        _canvasEditTextLayoutH = maxH;
        return _canvasEditTextLayout;
    }

    /// <summary>编辑态版本：底纹矩形按【编辑 layout】预算（文本与文档 1:1，不含剔除）。 与阅读态那份分开缓存 —— 两态的字符串很可能不同（标记在编辑态是显示的）。 算法与阅读态共用同一个实现 ⇒ 进/出编辑底纹不会跳变。</summary>
    private void BuildCanvasEditBackdrops(CanvasTextLayout layout, int textLength,
        IReadOnlyList<CanvasMarkdownSpan> spans)
    {
        BuildCanvasMarkdownBackdrops(layout, textLength, spans, _canvasEditBackdrops);
    }

    private int GetCanvasEditLineCount(double tw, double th)
    {
        if (string.IsNullOrEmpty(_canvasEditText)) return 1;
        _ = GetCanvasEditTextLayout(tw, th);
        return _canvasEditTextLayoutLineCount;
    }

    private double ComputeCanvasEditTopPad(double tw, double th)
    {
        double realContentH = GetCanvasEditLineCount(tw, th) * CanvasLineHeight;
        return Math.Min(0.5 * CanvasLineHeight, Math.Max(0, (th - Math.Min(th, realContentH)) / 2.0));
    }

    /// 编辑态滚动口径（与宿主底部那圈"手柄命中带"padding 解耦）。
    /// Extent/Viewport/ScrollableHeight 画滚动条，会让【新建空卡（只有 1 行）】也判定为溢出而弹条（用户报告）。
    /// ⚠ 与宿主上报口径无关地成立：ScrollableHeight = 真实内容高 − 宿主可用高（算式中 padding 项两边相消）
    private bool TryGetCanvasEditScrollMetrics(CanvasNode node, out double visH, out double contentH, out double visMax)
    {
        visH = 0; contentH = 0; visMax = 0;
        var sv = _canvasEditScrollViewer;
        if (sv == null) return false;
        double w = Math.Max(12, node.Width);
        double h = Math.Max(12, node.Height);
        double tw = w - 2 * CanvasCardPadX;
        double th = h - 2 * CanvasCardPadY;
        double topPad = ComputeCanvasEditTopPad(tw, th);
        double hostH = Math.Max(1, th - topPad - CanvasHandleHitRadius);
        double scrollable = Math.Max(0, sv.ScrollableHeight);
        contentH = hostH + scrollable;
        visH = Math.Max(1, th - topPad);
        visMax = Math.Max(0, contentH - visH);
        return true;
    }

    /// <summary>把编辑态"上方自适应留空"同步到编辑框 Padding.Top（⚠ 只改 top：左右变化会重排文字、可能形成布局回环）。 必须与 Win2D 绘制用同一 ComputeCanvasEditTopPad ⇒ "点击落光标"的命中映射与可见渲染一致。</summary>
    private void SyncCanvasEditTopPad()
    {
        if (CanvasEditBox == null || _canvasEditNodeId == null) return;
        var node = _canvasEditNodeRef;
        if (node == null) return;
        double w = Math.Max(12, node.Width);
        double h = Math.Max(12, node.Height);
        double tw = w - 2 * CanvasCardPadX;
        double th = h - 2 * CanvasCardPadY;
        double top = CanvasCardPadY + ComputeCanvasEditTopPad(tw, th);
        var cur = CanvasEditBox.Padding;
        if (Math.Abs(cur.Top - top) > 0.5)
            CanvasEditBox.Padding = new Thickness(CanvasCardPadX, top, CanvasCardPadX, CanvasCardPadY + CanvasHandleHitRadius);
    }

    private void DrawCanvasEdges(CanvasDrawingSession ds, double z,
        IReadOnlyList<GpuPageInfo> pages, Func<int, Rect> destOf,
        Dictionary<string, CanvasNode> index, CanvasEdge[] edges)
    {
        string? selEdge = _selectedCanvasEdgeId;
        string? hovEdge = _hoverCanvasEdgeId;    // 纯指针位置决定显隐（判据见 UpdateCanvasEdgeHover）
        foreach (var edge in edges)
        {
            // 改线中的连线由预览橡皮筋替代绘制（避免旧位置 + 预览同时出现）。
            if (_connRerouteEdgeId != null &&
                string.Equals(edge.Id, _connRerouteEdgeId, StringComparison.Ordinal))
                continue;
            if (!ComputeEdgeScreenGeometry(edge, pages, destOf, index, z,
                    out var a1, out var c1, out var c2, out var a2))
                continue;

            // 可见段裁剪：跨页连线两端可能都在视口外（如从第 1 页连到第 20 页，用户在第 10 页阅读）。
            float bbMinX = MathF.Min(MathF.Min(a1.X, a2.X), MathF.Min(c1.X, c2.X));
            float bbMaxX = MathF.Max(MathF.Max(a1.X, a2.X), MathF.Max(c1.X, c2.X));
            float bbMinY = MathF.Min(MathF.Min(a1.Y, a2.Y), MathF.Min(c1.Y, c2.Y));
            float bbMaxY = MathF.Max(MathF.Max(a1.Y, a2.Y), MathF.Max(c1.Y, c2.Y));
            const float edgeCullMargin = 48f;
            if (bbMaxX < -edgeCullMargin || bbMinX > _vpW + edgeCullMargin ||
                bbMaxY < -edgeCullMargin || bbMinY > _vpH + edgeCullMargin)
                continue;

            // 强调态（学 Obsidian）：线自身颜色全程不变；强调时主线变粗到箭头三角底边一半，
            var edgeColor = ParseCanvasColor(edge.Color) ?? CanvasEdgeColor;
            bool emph = selEdge == edge.Id || hovEdge == edge.Id;
            // 箭头高随缩放：页局部点 × z = 屏幕 DIP（与卡片手柄同约定），缩小时箭头变小、放大时变大。
            float arrowLen = MathF.Max(CanvasArrowMinLen, CanvasArrowLen * (float)z);
            // 线宽随缩放（沿用上方箭头/卡片手柄的 ×z 约定）：卡片边框经页变换、箭头经 arrowLen·z 都已随 z 缩放，
            float edgeW = MathF.Max(CanvasEdgeMinWidth, CanvasEdgeStrokeWidth * (float)z);
            bool toArrow = IsArrowEnd(edge.ToEnd);
            bool fromArrow = IsArrowEnd(edge.FromEnd);

            float halfBase = arrowLen / MathF.Sqrt(3f);
            float emphMainW = halfBase;
            float emphHaloW = 4f * halfBase;

            // 端点收缩到箭头三角底边：箭头端时线止于底边，箭头（同色实心）从底边画到卡片边框，
            Vector2 p0 = a1, p3 = a2;
            if (toArrow)
            {
                var d = a2 - c2;
                if (d.Length() > 1e-3f) p3 = a2 - (d / d.Length()) * arrowLen;
            }
            if (fromArrow)
            {
                var d = a1 - c1;
                if (d.Length() > 1e-3f) p0 = a1 - (d / d.Length()) * arrowLen;
            }

            // 接缝消除：线端越过箭头底边向顶点再延伸一小段，与三角重叠。平头线帽与三角底边两条共边
            // 注意：箭头底边仍用 p0/p3（尖端必须压边框），只有线的几何用 l0/l3。
            float seamOv = MathF.Max(1.5f, 0.15f * arrowLen);
            Vector2 l0 = p0, l3 = p3;
            if (fromArrow)
            {
                var d = a1 - c1;
                if (d.Length() > 1e-3f) l0 = p0 + (d / d.Length()) * seamOv;
            }
            if (toArrow)
            {
                var d = a2 - c2;
                if (d.Length() > 1e-3f) l3 = p3 + (d / d.Length()) * seamOv;
            }

            // 基础细线：静止态即完整可见；端点收缩到箭头底边后再向顶点延伸 seamOv 消缝（箭头端不伸到卡片）。
            using (var baseGeo = BuildBezier(l0, c1, c2, l3))
            {
                if (baseGeo != null)
                    ds.DrawGeometry(baseGeo, edgeColor, edgeW);
            }

            // 强调层（学 Obsidian）：主线变粗到箭头底边一半 + 同色极淡光晕（两倍底边）。
            // 强调态瞬时切换（无补间）：几何始终是整条线，只换线宽与光晕不透明度。
            if (emph)
            {
                using (var haloGeo = BuildBezier(a1, c1, c2, a2))
                {
                    if (haloGeo != null)
                        ds.DrawGeometry(haloGeo, WithAlpha(edgeColor, 0.3f), emphHaloW);
                }
                // 主线：收缩到箭头底边再向顶点延伸 seamOv（消缝），宽 = 箭头底边一半，仍为线自身颜色。
                using (var mainGeo = BuildBezier(l0, c1, c2, l3))
                {
                    if (mainGeo != null)
                        ds.DrawGeometry(mainGeo, edgeColor, emphMainW);
                }
            }

            // 箭头：阅读/悬浮/选中态均绘制，实心同色、尖点落在卡片边框、底与线收尾处衔接成连续尖点
            if (toArrow) DrawArrowHead(ds, p3, a2, edgeColor);
            if (fromArrow) DrawArrowHead(ds, p0, a1, edgeColor);
        }
    }

    /// <summary>计算一条连线在屏幕空间的几何（端点 + 控制点）。命中测试与渲染共用同一套算法， 保证"看得到就能点到"。边名/页变换与 DrawCanvasEdges 一致。</summary>
    private bool ComputeEdgeScreenGeometry(CanvasEdge edge, IReadOnlyList<GpuPageInfo> pages,
        Func<int, Rect> destOf, Dictionary<string, CanvasNode> index, double z,
        out Vector2 a1, out Vector2 c1, out Vector2 c2, out Vector2 a2)
    {
        a1 = c1 = c2 = a2 = default;
        if (!index.TryGetValue(edge.FromNode, out var fn)) return false;
        if (!index.TryGetValue(edge.ToNode, out var tn)) return false;
        if (fn.GlancePage < 0 || fn.GlancePage >= pages.Count) return false;
        if (tn.GlancePage < 0 || tn.GlancePage >= pages.Count) return false;

        var fPage = pages[fn.GlancePage];
        var tPage = pages[tn.GlancePage];

        // 文档坐标（未旋转）比较两张卡片中心 → 选边。页局部坐标系与文档坐标系轴向一致
        double fcx = fPage.LayoutRect.X + fn.X + fn.Width / 2.0;
        double fcy = fPage.LayoutRect.Y + fn.Y + fn.Height / 2.0;
        double tcx = tPage.LayoutRect.X + tn.X + tn.Width / 2.0;
        double tcy = tPage.LayoutRect.Y + tn.Y + tn.Height / 2.0;
        // 自动吸附的连接点（例如「拉线到空白新建卡片」场景：新建边未带 ToSide）在算出的当下写回模型，
        string fSide = edge.FromSide ?? AutoSide(fcx, fcy, tcx, tcy, true);
        string tSide = edge.ToSide ?? AutoSide(fcx, fcy, tcx, tcy, false);
        if (edge.FromSide == null) edge.FromSide = fSide;
        if (edge.ToSide == null) edge.ToSide = tSide;

        var fxf = MakePageXform(destOf(fn.GlancePage), z);
        var txf = MakePageXform(destOf(tn.GlancePage), z);

        var fp = SideAnchor(fn, fSide);
        var tp = SideAnchor(tn, tSide);
        a1 = fxf.Map(fp.X, fp.Y);
        a2 = txf.Map(tp.X, tp.Y);

        float dist = Vector2.Distance(a1, a2);
        float k = Math.Clamp(dist * 0.4f, 24f, 120f);
        var n1 = fxf.MapDirection(SideNormal(fSide).X, SideNormal(fSide).Y);
        var n2 = txf.MapDirection(SideNormal(tSide).X, SideNormal(tSide).Y);
        c1 = a1 + n1 * k;
        c2 = a2 + n2 * k;
        return true;
    }

    private static Vector2 BezierPoint(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1 - t;
        float uu = u * u, tt = t * t;
        return uu * u * p0 + 3 * uu * t * p1 + 3 * u * tt * p2 + tt * t * p3;
    }

    private static float DistancePointSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-6f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    private static float DistanceToBezier(Vector2 p, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
    {
        const int steps = 24;
        float min = float.MaxValue;
        Vector2 prev = p0;
        for (int i = 1; i <= steps; i++)
        {
            Vector2 cur = BezierPoint(p0, p1, p2, p3, (float)i / steps);
            min = MathF.Min(min, DistancePointSegment(p, prev, cur));
            prev = cur;
        }
        return min;
    }

    private bool TryHitCanvasEdge(Point vp, out string edgeId)
    {
        edgeId = "";
        if (!_annotationsVisible) return false;
        var pages = _pages;
        if (pages.Count == 0) return false;
        CanvasEdge[] edges;
        Dictionary<string, CanvasNode> index;
        lock (_canvasLock) { edges = _canvasEdges; index = _canvasIndex; }
        if (edges.Length == 0) return false;
        GetViewState(out double z, out double hOff, out double vOff);
        if (z <= 0) return false;
        Rect DestOf(int idx) => ComputePageDest(idx, z, hOff, vOff);

        // 命中带 = 悬浮态最粗那层（光晕）的半径，与绘制严格同源。
        float arrowLen = MathF.Max(CanvasArrowMinLen, CanvasArrowLen * (float)z);
        float best = 2f * arrowLen / MathF.Sqrt(3f);
        var p = new Vector2((float)vp.X, (float)vp.Y);
        string bestId = "";
        // 逆序：后绘制者在上层，重叠时优先命中后绘制的那条。
        for (int i = edges.Length - 1; i >= 0; i--)
        {
            var edge = edges[i];
            if (!ComputeEdgeScreenGeometry(edge, pages, DestOf, index, z,
                    out var a1, out var c1, out var c2, out var a2))
                continue;
            float d = DistanceToBezier(p, a1, c1, c2, a2);
            if (d <= best) { best = d; bestId = edge.Id; }
        }
        if (bestId.Length == 0) return false;
        edgeId = bestId;
        return true;
    }

    /// <summary>选中连线【边工具栏锚点】的屏幕坐标（仿 Obsidian Canvas）： X 取连线贝塞尔中点 X，Y 取靠上方的那张卡片连接点 Y（即两连接点中较小者）。 位置出厂在视口外时由调用方双向钳制到视口边缘。</summary>
    private bool TryGetCanvasEdgeToolbarAnchorScreen(string edgeId, out Vector2 anchor)
    {
        anchor = default;
        var pages = _pages;
        if (pages.Count == 0) return false;
        CanvasEdge[] edges;
        Dictionary<string, CanvasNode> index;
        lock (_canvasLock) { edges = _canvasEdges; index = _canvasIndex; }
        CanvasEdge? target = null;
        foreach (var e in edges)
            if (string.Equals(e.Id, edgeId, StringComparison.Ordinal)) { target = e; break; }
        if (target == null) return false;
        GetViewState(out double z, out double hOff, out double vOff);
        if (z <= 0) return false;
        Rect DestOf(int idx) => ComputePageDest(idx, z, hOff, vOff);
        if (!ComputeEdgeScreenGeometry(target, pages, DestOf, index, z,
                out var a1, out var c1, out var c2, out var a2))
            return false;
        float midX = BezierPoint(a1, c1, c2, a2, 0.5f).X;
        float upperY = MathF.Min(a1.Y, a2.Y);
        anchor = new Vector2(midX, upperY);
        return true;
    }

    /// <summary>指针是否落在"卡片本体或其边框控件命中带"上 —— 即按下会落到卡片/控件上的全部区域。 ⚠ 判据必须与 的前四支、 **完全同源**，不能只测卡片矩形：边框控件的命中带向卡外探出，</summary>
    private bool IsPointOnCanvasCardOrControl(Point vp) =>
        TryHitCanvasNode(vp, out _, out _, out _, out _) ||
        TryHitCanvasCorner(vp, out _, out _) ||
        TryHitCanvasHandle(vp, out _, out _) ||
        TryHitCanvasEdge(vp, out _, out _);

    /// <summary>指针是否落在白板【浮层控件】上（卡片悬浮胶囊 / 连线工具条）。 它们是盖在 Win2D 画布之上的 XAML 元素，指针在上面时属于"操作 chrome"而不是操作画布 —— 与卡片遮罩同一原则： 挡在指针下面的连线不许亮（否则鼠标停在胶囊上、后面的连线却在闪，用户报告）。</summary>
    private bool IsPointOverCanvasOverlayChrome(Point vp) =>
        IsPointInsideVisibleElement(CanvasCardToolbar, vp) ||
        IsPointInsideVisibleElement(CanvasEdgeToolbar, vp) ||
        // 编辑态文本菜单：也算浮层 chrome，否则指针停在菜单上时背后的卡片/连线会跟着高亮。
        IsPointInsideVisibleElement(CanvasEditContextMenu, vp);

    /// <summary>vp 是否落在某个【可见】元素的当前布局矩形内（坐标换算到 Viewer 系）。 Collapsed / 尚未布局（ActualWidth=0）/ 抛异常 一律算"不在"，绝不外抛。</summary>
    private bool IsPointInsideVisibleElement(FrameworkElement? el, Point vp)
    {
        try
        {
            if (el == null || el.Visibility != Visibility.Visible) return false;
            if (el.ActualWidth <= 0 || el.ActualHeight <= 0) return false;
            var r = el.TransformToVisual(Viewer)
                .TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            return vp.X >= r.X && vp.X <= r.X + r.Width && vp.Y >= r.Y && vp.Y <= r.Y + r.Height;
        }
        catch { return false; }
    }

    /// 无手势时的连线悬停高亮（指针靠近即变强调色）。
    /// ⚠ 悬浮态"慢半拍"已四轮探针定论：**本层与换架构都不可优化，勿再尝试**。
    /// 数据：静止期 Invalidate→Draw 仅 ~0.9ms；鼠标移动期 P50 21.7 / P90 78.7 / MAX 92.8ms。
    /// 指针事件不陈旧（事件坐标 vs 真实光标恒差 0.8px）；UI 线程空转（轮询入队 P50 0.08ms）。
    /// ⇒ 延迟由"提交→呈现"节拍决定：合成器 Offset / XAML 变换 / 画布重绘三条路径全都落后光标，
    ///   绕开事件的"轮询+合成器"反而更慢（多等一个提交周期）。加时钟、改节流、补采样、剥离画布均无效。
    /// 附："快速掠过被跳过"同源 —— 8ms × 1200px/s ≈ 9.6px 步长 &lt; 命中带 ≈20px，不是采样丢失。
    private void UpdateCanvasEdgeHover(Point vp)
    {
        string? hover = (IsPointOnCanvasCardOrControl(vp) || IsPointOverCanvasOverlayChrome(vp))
            ? null
            : (TryHitCanvasEdge(vp, out var id) ? id : null);

        if (string.Equals(hover, _hoverCanvasEdgeId, StringComparison.Ordinal)) return;
        _hoverCanvasEdgeId = hover;
        InvalidateOverlayThrottled();
    }

    /// <summary>无手势时的卡片悬停判定：指针进入卡片（或其边缘手柄外扩区）即置 ， 渲染侧据此显出四边连线圆点/四角缩放块与淡描边；指针移开即清空。仅在状态变化时重绘。</summary>
    private void UpdateCanvasNodeHover(Point vp)
    {
        // 胶囊叠层遮罩：悬停在卡片胶囊 / 连线工具条上时，背后卡片的边框控件（角/边手柄）不显形。
        // 否则胶囊压在另一张卡片之上时，指针明明在胶囊里，那张背后卡片的控件却亮起、光标也变（用户实测）。
        if (IsPointOverCanvasOverlayChrome(vp))
        {
            if (_hoverCanvasNodeId == null) return;
            _hoverCanvasNodeId = null;
            InvalidateOverlayThrottled();
            return;
        }
        string? hover = null;
        if (_canvasInputActive && _annotationsVisible &&
            TryHitCanvasNodeForHandles(vp, out var node) && node != null)
            hover = node.Id;
        if (string.Equals(hover, _hoverCanvasNodeId, StringComparison.Ordinal)) return;
        _hoverCanvasNodeId = hover;
        InvalidateOverlayThrottled();
    }

    /// <summary>命中"手柄可及"的卡片：卡片矩形在【页局部空间】外扩「手柄命中半径」（页局部点）后再测， 使指针靠近卡片边缘（手柄所在处）即视为悬停该卡 —— 手柄因此能"指针一来就出现"， 与 Obsidian 一致（悬停边缘即可直接拖连线/缩放，无需先单击）。取绘制顺序最上层者。</summary>
    private bool TryHitCanvasNodeForHandles(Point vp, out CanvasNode? node)
    {
        node = null;
        if (!CanvasViewportToPage(vp, out int pageIndex, out double px, out double py)) return false;
        // 悬停判定外扩半径：取"连线控件落屏半径 clamp 值"换算回页局部（页局部 = 屏幕DIP / 缩放），
        GetViewState(out double z, out _, out _);
        double margin = Math.Clamp(CanvasHandleHitRadius * z, CanvasConnHandleHitMinDip, CanvasConnHandleHitMaxDip) / z;
        CanvasNode[] nodes;
        lock (_canvasLock) nodes = _canvasNodes;
        for (int i = nodes.Length - 1; i >= 0; i--)
        {
            var n = nodes[i];
            if (n.GlancePage != pageIndex) continue;
            if (px >= n.X - margin && px <= n.X + n.Width + margin &&
                py >= n.Y - margin && py <= n.Y + n.Height + margin)
            {
                node = n;
                return true;
            }
        }
        return false;
    }

    private static Vector2 SideAnchor(CanvasNode n, string side) => side switch
    {
        "top" => new Vector2((float)(n.X + n.Width / 2), (float)n.Y),
        "bottom" => new Vector2((float)(n.X + n.Width / 2), (float)(n.Y + n.Height)),
        "left" => new Vector2((float)n.X, (float)(n.Y + n.Height / 2)),
        _ => new Vector2((float)(n.X + n.Width), (float)(n.Y + n.Height / 2)),
    };

    private static Vector2 CornerAnchor(CanvasNode n, string corner)
    {
        double l = n.X, t = n.Y, r = n.X + n.Width, b = n.Y + n.Height;
        return corner switch
        {
            "tl" => new Vector2((float)l, (float)t),
            "tr" => new Vector2((float)r, (float)t),
            "bl" => new Vector2((float)l, (float)b),
            _ => new Vector2((float)r, (float)b),
        };
    }

    /// <summary>某条边的外法向（页局部，单位向量）。</summary>
    private static Vector2 SideNormal(string side) => side switch
    {
        "top" => new Vector2(0, -1),
        "bottom" => new Vector2(0, 1),
        "left" => new Vector2(-1, 0),
        _ => new Vector2(1, 0),
    };

    private static string AutoSide(double fx, double fy, double tx, double ty, bool from)
    {
        double dx = tx - fx, dy = ty - fy;
        bool horizontal = Math.Abs(dx) >= Math.Abs(dy);
        if (from)
            return horizontal ? (dx >= 0 ? "right" : "left") : (dy >= 0 ? "bottom" : "top");
        return horizontal ? (dx >= 0 ? "left" : "right") : (dy >= 0 ? "top" : "bottom");
    }

    private Microsoft.Graphics.Canvas.Geometry.CanvasGeometry? BuildBezier(
        Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3)
    {
        using var pb = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(ScreenCanvas);
        pb.BeginFigure(p0);
        pb.AddCubicBezier(c1, c2, p3);
        pb.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Open);
        return Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(pb);
    }

    /// <summary>画实心箭头：底边中心落在 baseC，尖端(tip)在 baseC 指向 tip 的方向上，屏幕坐标。 调用方负责让 baseC = 连线终点、tip = 卡片边框边中点，使尖端正好压在边框上、连线不穿过三角内部。</summary>
    private void DrawArrowHead(CanvasDrawingSession ds, Vector2 baseC, Vector2 tip)
        => DrawArrowHead(ds, baseC, tip, CanvasEdgeColor);

    private void DrawArrowHead(CanvasDrawingSession ds, Vector2 baseC, Vector2 tip, Windows.UI.Color color)
    {
        var dir = tip - baseC;
        float len = dir.Length();
        if (len < 1e-3f) return;
        dir /= len;
        var perp = new Vector2(-dir.Y, dir.X);
        float half = len / MathF.Sqrt(3f);
        using var pb = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(ScreenCanvas);
        pb.BeginFigure(tip);
        pb.AddLine(baseC + perp * half);
        pb.AddLine(baseC - perp * half);
        pb.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Closed);
        using var geo = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(geo, color);
    }

    /// <summary>只绘制【指针最接近】的那一个手柄：靠近某条边中点 → 连线圆点；靠近某个角 → 缩放弧边。 其余位置一律不画，画面保持干净（对齐 Obsidian：选中不再全露 8 个点）。 全部在页局部空间比较与绘制，尺寸即页局部 PDF 点 → 随页面缩放，与卡片边框保持固定比例。</summary>
    private void DrawNearestCanvasHandle(CanvasDrawingSession ds, CanvasNode node)
    {
        if (_isCanvasResizing)
        {
            if (_canvasResizeMode != "edge" && _canvasResizeCorner.Length > 0)
                DrawCanvasCornerControl(ds, node, _canvasResizeCorner);
            return;
        }
        if (_isCanvasConnecting)
        {
            if (_connFromSide.Length > 0) DrawCanvasSideHandle(ds, node, _connFromSide);
            return;
        }

        // 指针接近度显形只在"没有手势在跑"时成立：手势期间 _canvasPointerPos 是冻结值（拖动起点后不再更新），
        // 拿冻结点探测会让卡片扫过它时凭空显出控件 —— 各手势自己的控件由上方分支负责画。
        if (!_canvasPointerValid || AnyCanvasGestureActive) return;
        if (!CanvasViewportToLockedPage(node.GlancePage, _canvasPointerPos, out double ppx, out double ppy))
            return;

        // 四边中点连线控件：落屏显形半径 = clamp(页局部8pt × 缩放, Min, Max)（中缩放随动、极缩不消失、极放不撑爆）；
        double z = CurrentZoom;
        double pickSide = Math.Clamp(CanvasHandleHitRadius * z, CanvasConnHandleHitMinDip, CanvasConnHandleHitMaxDip) / z;
        double pickCorner = CanvasHandleHitRadius;
        double bestSideD2 = pickSide * pickSide;
        double bestCornerD2 = pickCorner * pickCorner;
        string bestSide = "";
        string bestCorner = "";

        foreach (var side in CanvasSides)
        {
            var a = SideAnchor(node, side);
            double dx = a.X - ppx, dy = a.Y - ppy;
            double d2 = dx * dx + dy * dy;
            if (d2 <= bestSideD2) { bestSideD2 = d2; bestSide = side; }
        }
        foreach (var corner in CanvasCorners)
        {
            var c = CornerAnchor(node, corner);
            double dx = c.X - ppx, dy = c.Y - ppy;
            double d2 = dx * dx + dy * dy;
            if (d2 <= bestCornerD2) { bestCornerD2 = d2; bestCorner = corner; }
        }
        if (bestCorner.Length > 0) DrawCanvasCornerControl(ds, node, bestCorner);
        else if (bestSide.Length > 0) DrawCanvasSideHandle(ds, node, bestSide);
    }

    /// <summary>四边中点手柄（连线起点）：强调色实心圆点 + 浅色描边。 落屏尺寸 = clamp(页局部半径 × 缩放, Min, Max)：中缩放随内容同比例、缩太小不消失、放太大不撑爆 （Obsidian 式：先随动、再回弹到合适）。绘制空间是页局部，故页局部半径 = 落屏半径 / 缩放；</summary>
    private void DrawCanvasSideHandle(CanvasDrawingSession ds, CanvasNode node, string side)
    {
        var a = SideAnchor(node, side);
        float screenR = Math.Clamp(CanvasHandleRadius * (float)CurrentZoom,
            CanvasConnHandleRadiusMinDip, CanvasConnHandleRadiusMaxDip);
        float r = screenR / (float)CurrentZoom;
        float stroke = r * 0.18f;
        ds.FillCircle(a, r, _canvasAccentColor);
        ds.DrawCircle(a, r, CanvasHandleFill, stroke);
    }

    /// <summary>四角缩放控件：贴合卡片圆角、同心的【弧形边】（半径 = 卡片圆角 + 外扩量，覆盖该角 90° 象限）， 线宽与选中边框一致 → 与"四边中点圆点"形成形状区分。</summary>
    private void DrawCanvasCornerControl(CanvasDrawingSession ds, CanvasNode node, string corner)
    {
        double w = Math.Max(12, node.Width);
        double h = Math.Max(12, node.Height);
        float cr = CanvasCardRadius;
        float arcR = cr + CanvasCornerGap;

        Vector2 c; float startDeg;
        switch (corner)
        {
            case "tl": c = new Vector2((float)(node.X + cr), (float)(node.Y + cr)); startDeg = 180f; break;
            case "tr": c = new Vector2((float)(node.X + w - cr), (float)(node.Y + cr)); startDeg = 270f; break;
            case "bl": c = new Vector2((float)(node.X + cr), (float)(node.Y + h - cr)); startDeg = 90f; break;
            default: c = new Vector2((float)(node.X + w - cr), (float)(node.Y + h - cr)); startDeg = 0f; break;
        }

        // 折线逼近 90° 圆弧：24 段，弦偏差 ≈ r·(1−cos(1.875°)) ≈ 0.0005·r，远小于线宽，肉眼不可辨。
        const int Segments = 24;
        float a0 = startDeg * MathF.PI / 180f;
        const float Sweep = MathF.PI / 2f;
        using var pb = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(ds);
        pb.BeginFigure(new Vector2(c.X + arcR * MathF.Cos(a0), c.Y + arcR * MathF.Sin(a0)));
        for (int i = 1; i <= Segments; i++)
        {
            float a = a0 + Sweep * i / Segments;
            pb.AddLine(new Vector2(c.X + arcR * MathF.Cos(a), c.Y + arcR * MathF.Sin(a)));
        }
        pb.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Open);
        using var geo = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(geo, _canvasAccentColor, CanvasCardSelectedStrokeWidth);
    }

    private void DrawCanvasConnecting(CanvasDrawingSession ds)
    {
        // 改线预览沿用【该连线自身】的颜色与箭头样式；紫色强调 + 接线指示只属于"从拉线控件新建连线"。
        var strokeColor = _canvasAccentColor;
        bool reroute = _connRerouteEdgeId != null;
        bool arrowAtFixed = false, arrowAtMovingStyle = false;
        if (reroute)
        {
            CanvasEdge[] edges;
            lock (_canvasLock) edges = _canvasEdges;
            CanvasEdge? re = null;
            foreach (var ce in edges)
                if (string.Equals(ce.Id, _connRerouteEdgeId, StringComparison.Ordinal)) { re = ce; break; }
            if (re != null)
            {
                strokeColor = ParseCanvasColor(re.Color) ?? CanvasEdgeColor;
                // 断 from 端 ⇒ 动端是 from、定端是 to；两端各按该线原有箭头样式绘制
                // （原线在拖动期间不画，故定端的箭头也必须由预览补上，否则会凭空消失一截）。
                bool fromArrow = IsArrowEnd(re.FromEnd), toArrow = IsArrowEnd(re.ToEnd);
                arrowAtMovingStyle = _connRerouteDetachFrom ? fromArrow : toArrow;
                arrowAtFixed = _connRerouteDetachFrom ? toArrow : fromArrow;
            }
        }

        // 两端都按【当前视口】现场派生（见 TryGetCanvasConnOriginScreen / TryGetCanvasConnTargetScreen）：
        // 滚轮翻页、键盘翻页、Ctrl+滚轮缩放、窗口尺寸变化之后，预览都自动贴在卡片上。
        if (!TryGetCanvasConnOriginScreen(out var a1, out var beginNormal)) return;
        Vector2 a2;
        Vector2 endNormal;
        bool hasTarget = TryGetCanvasConnTargetScreen(_connCursorScreen, _connFromNodeId,
            out var anchor, out var tgNormal);
        if (hasTarget)
        {
            a2 = anchor;
            endNormal = tgNormal;
        }
        else
        {
            a2 = new Vector2((float)_connCursorScreen.X, (float)_connCursorScreen.Y);
            var d = a2 - a1;
            float dl = d.Length();
            endNormal = dl > 1e-3f ? d / dl : new Vector2(1, 0);
        }

        // 动端一律画箭头：新建连线的默认样式就是单箭头（指向 to 端），拉出即带；
        // 改线则按"原线在该端的箭头样式"，无箭头的那端就不画。
        bool arrowAtMoving = reroute ? arrowAtMovingStyle : true;
        DrawConnBezier(ds, a1, beginNormal, a2, hasTarget, endNormal,
            arrowAtFixed, arrowAtMoving, strokeColor, null);

        // 接线指示：吸附目标时画 7px 圆环；动端【没有箭头】时补 3.5px 落点（有箭头时它会压住箭头，故只在无箭头端出现）。
        // 两者可同时存在（改线拖到卡片上：圆环 + 落点），松手收尾后一并消失。
        if (hasTarget) ds.DrawCircle(a2, 7f, strokeColor, 2f);
        if (!arrowAtMoving) ds.FillCircle(a2, 3.5f, strokeColor);
    }

    /// <summary>预览/引导共用的连线几何：两端按箭头收缩到三角底边、再向顶点延伸 seamOv 消缝（与 DrawCanvasEdges 同一约定）。 strokeStyle 传 null = 实线。</summary>
    private void DrawConnBezier(CanvasDrawingSession ds, Vector2 a1, Vector2 beginNormal,
        Vector2 a2, bool hasTarget, Vector2 endNormal,
        bool arrowAtStart, bool arrowAtEnd, Windows.UI.Color color,
        Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle? strokeStyle)
    {
        float k = Math.Clamp(Vector2.Distance(a1, a2) * 0.4f, 24f, 120f);
        var c1 = a1 + beginNormal * k;
        var c2 = hasTarget ? a2 + endNormal * k : a2 - endNormal * k;

        float arrowLen = MathF.Max(CanvasArrowMinLen, CanvasArrowLen * (float)CurrentZoom);
        float seamOv = MathF.Max(1.5f, 0.15f * arrowLen);
        Vector2 p1 = a1, p2 = a2;
        if (arrowAtStart)
        {
            var d = a1 - c1;
            if (d.Length() > 1e-3f) p1 = a1 - (d / d.Length()) * arrowLen;
        }
        if (arrowAtEnd)
        {
            var d = a2 - c2;
            if (d.Length() > 1e-3f) p2 = a2 - (d / d.Length()) * arrowLen;
        }
        Vector2 l1 = p1, l2 = p2;
        if (arrowAtStart)
        {
            var d = a1 - c1;
            if (d.Length() > 1e-3f) l1 = p1 + (d / d.Length()) * seamOv;
        }
        if (arrowAtEnd)
        {
            var d = a2 - c2;
            if (d.Length() > 1e-3f) l2 = p2 + (d / d.Length()) * seamOv;
        }

        float w = MathF.Max(CanvasEdgeMinWidth, CanvasEdgeStrokeWidth * (float)CurrentZoom);
        using (var geo = BuildBezier(l1, c1, c2, l2))
        {
            if (geo != null)
            {
                if (strokeStyle == null) ds.DrawGeometry(geo, color, w);
                else ds.DrawGeometry(geo, color, w, strokeStyle);
            }
        }
        if (arrowAtStart) DrawArrowHead(ds, p1, a1, color);
        if (arrowAtEnd) DrawArrowHead(ds, p2, a2, color);
    }

    /// <summary>按住波浪键两次点击建线的"待接"引导：源锚点 → 指针（或悬停到的目标卡该边），虚线 + 单箭头。 不画圆环/落点：这不是拖拽手势，只是提示"还差一次点击"。</summary>
    private void DrawCanvasConnArmGuide(CanvasDrawingSession ds)
    {
        CanvasNode? src = null;
        lock (_canvasLock) _canvasIndex.TryGetValue(_connArmNodeId ?? "", out src);
        if (src == null || src.GlancePage < 0 || src.GlancePage >= _pages.Count) return;
        GetViewState(out double z, out double hOff, out double vOff);
        if (z <= 0) return;

        // 源边：波浪键+点拉线控件 = 指定该边；波浪键+点卡片本体 = 按指针方向自动选边（与落定后 AutoSide 同一算法）
        string side = _connArmSide;
        if (side.Length == 0)
        {
            var sPage = _pages[src.GlancePage];
            double scx = sPage.LayoutRect.X + src.X + src.Width / 2.0;
            double scy = sPage.LayoutRect.Y + src.Y + src.Height / 2.0;
            double tcx = scx + 1, tcy = scy;
            if (CanvasViewportToPage(_connArmCursorVp, out int tp, out double tpx, out double tpy) &&
                tp >= 0 && tp < _pages.Count)
            {
                var tPage = _pages[tp];
                tcx = tPage.LayoutRect.X + tpx;
                tcy = tPage.LayoutRect.Y + tpy;
            }
            side = AutoSide(scx, scy, tcx, tcy, true);
        }
        if (!TryGetCanvasHandleScreen(src.Id, side, out var a1)) return;
        var xf = MakePageXform(ComputePageDest(src.GlancePage, z, hOff, vOff), z);
        var sn = SideNormal(side);
        var beginNormal = xf.MapDirection(sn.X, sn.Y);

        bool hasTarget = TryGetCanvasConnTargetScreen(_connArmCursorVp, src.Id, out var anchor, out var tgNormal);
        Vector2 a2;
        Vector2 endNormal;
        if (hasTarget) { a2 = anchor; endNormal = tgNormal; }
        else
        {
            a2 = new Vector2((float)_connArmCursorVp.X, (float)_connArmCursorVp.Y);
            var d = a2 - a1;
            float dl = d.Length();
            endNormal = dl > 1e-3f ? d / dl : new Vector2(1, 0);
        }
        DrawConnBezier(ds, a1, beginNormal, a2, hasTarget, endNormal,
            arrowAtStart: false, arrowAtEnd: true, _canvasAccentColor, DashStrokeStyle);
    }

    // =====================================================================

    /// <summary>白板输入模式（Canvas 模式为 true）：左键优先命中卡片→选中/拖动；未命中则照常拖动平移。</summary>
    public bool CanvasInputActive
    {
        get => _canvasInputActive;
        set
        {
            if (_canvasInputActive == value) return;
            _canvasInputActive = value;
            if (!value) ExitCanvasInteraction();
            else EnterHandScope();   // 进入白板：启动连线手型作用域轮询（对齐编辑态 EnterIbeamScope）
        }
    }
    private bool _canvasInputActive;

    private string? _selectedCanvasNodeId;

    /// <summary>指针悬停的卡片 id（null=无）。与选中无关：指针靠近卡片（含边缘手柄外扩区）时置位， 渲染侧据此把该卡边框染成强调色；指针移开即清空 → 平时画面保持干净。</summary>
    private string? _hoverCanvasNodeId;

    private Point _canvasPointerPos;
    private bool _canvasPointerValid;

    private string? _selectedCanvasEdgeId;
    private string? _hoverCanvasEdgeId;

    private bool _isCanvasDragging;
    private CanvasNode? _canvasDragNode;
    private int _canvasDragPage = -1;
    private double _canvasDragStartPx, _canvasDragStartPy;
    private double _canvasDragOrigX, _canvasDragOrigY;

    private bool _isCanvasConnecting;
    private string? _connFromNodeId;
    private string _connFromSide = "";
    // 拉线的两端【不缓存屏幕坐标】：源手柄由 (node, side) + 当前视口现场派生，目标由指针位置现场命中
    // ⇒ 滚轮/键盘翻页/缩放/窗口尺寸任何变化都自动跟着走（缓存屏幕坐标会让起点与卡片脱离）。
    private Point _connCursorScreen;          // 指针屏幕坐标（手势期间不随视口变化，故可缓存）

    // ---- 按住 Tab 上方的波浪键（`~）+ 两次点击建线（armed 态【不算手势】：不捕获指针、不冻结指针、不阻塞悬停）----
    // 选键依据：既不是系统修饰键、也不是字母 ⇒ 不触发系统快捷键（Alt 会被系统当助记键吃掉），
    // 也不参与输入法组字（字母键在输入框有焦点时会被编辑框/输入法收走）。
    // 波浪键 = Tab 上方、VK_OEM_3 = 0xC0；白板画布非文本输入上下文，键会正常送到本视图。
    private const VirtualKey ConnArmTriggerKey = (VirtualKey)0xC0;   // VK_OEM_3
    private bool _connArmActive;
    private string? _connArmNodeId;
    private string _connArmSide = "";         // "" = 起点落在卡片本体，边由终点方向自动决定
    private Point _connArmCursorVp;           // 虚线引导的动端（随指针走，无需按住键）

    /// 触发键当前是否物理按下。用【系统按键状态】而非 e.KeyModifiers：该键不是修饰键，
    /// 且这个判据同时供 PointerMoved / 按下链两处使用（与 Ctrl/Shift 的既有写法同源）。
    /// ⚠ VK_OEM_3 不在 `Windows.System.VirtualKey` 的具名成员里（WinRT 没暴露 OEM 键）⇒ 直接按 VK 码强转，
    /// 投影层就是 int，`GetKeyStateForCurrentThread` 照常接受。
    private static bool IsConnArmTriggerDown()
        => (InputKeyboardSource.GetKeyStateForCurrentThread(ConnArmTriggerKey)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    // ---- 连线"改线"手势：拖动已有连线改接它的某一端（复用 _isCanvasConnecting 的预览状态机）----
    private bool _canvasEdgePressPending;     // 按下在连线上、尚未判定单击(选中)还是拖动(改线)
    private string? _canvasEdgePressEdgeId;
    private Point _canvasEdgePressVp;
    private string? _connRerouteEdgeId;       // 正在改线的连线 id（_isCanvasConnecting 期间有效）
    private bool _connRerouteDetachFrom;      // true=断开 from 端（改起点），false=断开 to 端（改终点）

    private bool _isCanvasResizing;
    private CanvasNode? _canvasResizeNode;
    private int _canvasResizePage = -1;
    private string _canvasResizeMode = "corner";   // "corner" = 四角对角缩放；"edge" = 单边单轴缩放
    private string _canvasResizeCorner = "";
    private string _canvasResizeEdge = "";
    private double _canvasResizeOrigX, _canvasResizeOrigY, _canvasResizeOrigW, _canvasResizeOrigH;

    private const int CanvasIdcArrow = 32512;
    private const int CanvasIdcSizeNwSe = 32642;
    private const int CanvasIdcSizeNeSw = 32643;
    private const int CanvasIdcSizeNs = 32645;
    private const int CanvasIdcSizeWe = 32644;
    // 编辑态"卡内=工字"作用域轮询间隔（对齐画笔圆环的 25ms _ringScopeTimer）。
    private const int IbeamScopeIntervalMs = 25;

    private string? _canvasEditNodeId;
    private CanvasNode? _canvasEditNodeRef;
    private ScrollViewer? _canvasEditScrollViewer;

    // 每张卡片的滚动偏移（页局部 PDF 点，0 = 顶部）。**仅内存态**，不写进 .gcanvas。
    // 非编辑态卡片由 Win2D 绘制、没有内建滚动，必须靠这个值把文字整体上移来表现滚动。
    private readonly Dictionary<string, double> _canvasScrollY = new(StringComparer.Ordinal);

    private double GetCanvasScrollY(string nodeId)
        => _canvasScrollY.TryGetValue(nodeId, out double v) && v > 0 ? v : 0;

    private bool SetCanvasScrollY(string nodeId, double value, double maxScroll)
    {
        double v = Math.Clamp(value, 0, Math.Max(0, maxScroll));
        double old = GetCanvasScrollY(nodeId);
        if (Math.Abs(v - old) < 0.01) return false;
        if (v <= 0) _canvasScrollY.Remove(nodeId);
        else _canvasScrollY[nodeId] = v;
        return true;
    }

    private bool _isCanvasScrollDragging;
    private string? _canvasScrollDragNodeId;
    private double _canvasScrollDragStartPointerY;
    private double _canvasScrollDragStartScrollY;

    // ---- Obsidian 对齐：单击【已选中卡片】松手无拖动 → 进入编辑态（光标落点击点） ----
    private bool _canvasClickEditPending;
    private Point _canvasClickEditStartVp;
    private const double CanvasClickEditMoveThreshold = 4.0;   // 视口 DIP：超过即视为拖动而非点击
    private const double CanvasEdgeRerouteStartThreshold = 8.0;   // 视口 DIP：连线拖动超过此值才进入改线（防止一点拖动就删掉整条线）

    private bool _canvasOverlaySyncPending;
    // 浮动控件缓存：实测尺寸 + 上次写入的位置（避免拖动时每帧重复 Measure / 无效 Margin 写入）。
    private double _canvasToolbarW, _canvasToolbarH;
    private double _canvasToolbarLeft, _canvasToolbarTop;
    private bool _canvasToolbarPlaced;
    // 展开 7 色后的【真实宽度】（窄态宽度 _canvasToolbarW 只用于跟踪卡片中心，不重新居中）。
    private double _canvasToolbarExpandedW;
    // 边工具栏缓存（同卡片工具栏，避免拖动/缩放时每帧 Measure）。
    private double _canvasEdgeToolbarW, _canvasEdgeToolbarH;
    private double _canvasEdgeToolbarLeft, _canvasEdgeToolbarTop;
    private bool _canvasEdgeToolbarPlaced;
    // ── 浮层安全区（上层遮挡带）──
    // MainPage 的顶层浮层盖在本视图【之上】并吞掉指针：顶栏宿主（透明底、整条顶边，约一条工具栏高；
    // ⇒ 钳制必须避开它们。
    private double _canvasInsetL, _canvasInsetT, _canvasInsetR, _canvasInsetB;
    // 连线胶囊的显隐去重（与 _cardToolbarShown 同源）：避免重复写 Visibility。
    private bool _edgeToolbarShown;
    // 连线胶囊内两个二级面板：默认折叠、互斥（同一时刻只开一个）；展开态真实宽度用于右缘钳制。
    private bool _canvasEdgeArrowPickerShown;
    private bool _canvasEdgeColorPickerShown;
    private double _canvasEdgeToolbarExpandedW;
    private Button? _canvasEdgeColorSelectedDot;
    // 箭头符号描边是否已挂 RasterizationScale 变化（Loaded 后只挂一次；跨不同 DPI 显示器时重算描边宽）。
    private bool _canvasEdgeGlyphScaleHooked;
    private bool _canvasColorPickerShown;
    private const double CanvasColorDotBaseSize = 16.0;
    private const double CanvasColorDotEnlargedSize = 16.0 * 1.6;
    private const double CanvasColorDotBaseRadius = CanvasColorDotBaseSize / 2.0;
    private const double CanvasColorDotEnlargedRadius = CanvasColorDotEnlargedSize / 2.0;
    private const double CanvasColorDotEnlargedInset = (CanvasColorDotEnlargedSize - CanvasColorDotBaseSize) / 2.0;
    private const string CanvasCardDefaultGrayHex = "#8A8F98";
    private Button? _canvasColorSelectedDot;

    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeDragged;
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeDragCompleted;
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeCreateRequested;
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeDeleteRequested;
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeTextCommitted;
    /// <summary>连线建立请求：从 NodeId 的 FromSide 手柄拖到目标卡片 TargetNodeId（ToSide 为吸附边）。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeConnectionRequested;
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeResized;
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeResizeCompleted;
    public event EventHandler<CanvasNodeEventArgs>? CanvasEdgeDeleteRequested;
    public event EventHandler<CanvasNodeEventArgs>? CanvasEdgeStyleChanged;
    /// <summary>改线请求：把连线 EdgeId 的某一端（RerouteFromEnd ? 起点 : 终点）改接到 TargetNodeId 的 ToSide 边。 只改节点/边，FromEnd/ToEnd/Color 等样式保持不变。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasEdgeRerouteRequested;
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeColorChanged;
    /// <summary>从连线手柄拖到空白处：请求在该处新建卡片并与源节点连成一条线 （NodeId=源节点、FromSide=源出边、PageIndex/X/Y=落点页与已 clamp 的左上角）。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeCreateConnectedRequested;
    /// <summary>选中卡片 = 请求提升到叠放顶层（jsoncanvas 无独立 z 字段，nodes 数组顺序即 z 序）。 MainPage 收到后重排模型数组并置脏（落盘统一在 FlushCanvas）⇒ 取消选中后层级保持，不再只是绘制期临时置顶。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeBringToFrontRequested;
    /// <summary>编辑框内请求撤销（Ctrl+Z）：MainPage 统一走撤销栈，避免编辑框原生撤销与全局历史脱节。</summary>
    public event EventHandler? CanvasUndoRequested;
    public event EventHandler? CanvasRedoRequested;

    /// <summary>选中/取消选中卡片（null=取消）。命中测试只认页内坐标，跨页卡片各自独立。 与连线选中互斥：选中卡片即清掉连线选中。</summary>
    public void SelectCanvasNode(string? nodeId)
    {
        string? id = string.IsNullOrEmpty(nodeId) ? null : nodeId;
        if (_selectedCanvasNodeId == id && _selectedCanvasEdgeId == null) { SyncCanvasOverlayNow(); return; }
        // 选中目标变了（含取消选中）→ 收起二级颜色胶囊，避免它停在原卡片的旧位置。
        if (id != _selectedCanvasNodeId) HideCanvasColorPicker();
        _selectedCanvasNodeId = id;
        _selectedCanvasEdgeId = null;
        HideCanvasEdgePickers();       // 选中卡片即收起连线胶囊的二级面板（胶囊本身随后由定位链路隐藏）
        // 选中即置顶，且必须是【永久】的：这里只发请求，真正的重排与置脏由 MainPage 改模型数组完成
        // （jsoncanvas 的 z 序 = nodes 数组顺序）。否则取消选中后 topId 失效，卡片会掉回出生层级。
        if (id != null)
            CanvasNodeBringToFrontRequested?.Invoke(this, new CanvasNodeEventArgs { NodeId = id });
        InvalidateComposite();
        SyncCanvasOverlayNow();
    }

    public bool HasSelectedCanvasEdge => _selectedCanvasEdgeId != null;

    public bool HasSelectedCanvasNode => _selectedCanvasNodeId != null;

    public bool IsCanvasEditing => _canvasEditNodeId != null;

    public void SelectCanvasEdge(string? edgeId)
    {
        string? id = string.IsNullOrEmpty(edgeId) ? null : edgeId;
        if (_selectedCanvasEdgeId == id && _selectedCanvasNodeId == null) { SyncCanvasOverlayNow(); return; }
        // 换一条线（或取消选中）：上一个二级面板立即收起 —— 否则"上一条线展开着的颜色面板"会带过来。
        HideCanvasEdgePickers();
        _selectedCanvasEdgeId = id;
        _selectedCanvasNodeId = null;
        InvalidateComposite();
        SyncCanvasOverlayNow();
    }

    public void ClearCanvasEdgeSelection()
    {
        if (_selectedCanvasEdgeId == null && _hoverCanvasEdgeId == null) return;
        _selectedCanvasEdgeId = null;
        _hoverCanvasEdgeId = null;
        HideCanvasEdgePickers();
        InvalidateComposite();
    }

    public bool DeleteSelectedCanvasEdge()
    {
        string? id = _selectedCanvasEdgeId;
        if (string.IsNullOrEmpty(id)) return false;
        _selectedCanvasEdgeId = null;
        _hoverCanvasEdgeId = null;
        // 与"取消选中"同一条路径：收胶囊 → 空闲才收整层浮层。
        HideCanvasEdgeToolbar();
        CollapseCanvasOverlayIfIdle();
        CanvasEdgeDeleteRequested?.Invoke(this, new CanvasNodeEventArgs { EdgeId = id });
        InvalidateComposite();
        return true;
    }

    public bool DeleteSelectedCanvasNode()
    {
        string? id = _selectedCanvasNodeId;
        if (string.IsNullOrEmpty(id)) return false;
        _selectedCanvasNodeId = null;
        HideCanvasCardToolbar();
        CollapseCanvasOverlayIfIdle();   // 两颗胶囊都收了才收整层浮层
        CanvasNodeDeleteRequested?.Invoke(this, new CanvasNodeEventArgs { NodeId = id });
        InvalidateComposite();
        return true;
    }

    public void BeginCanvasEdit(string nodeId, Point? clickVp = null)
    {
        CanvasNode? node;
        lock (_canvasLock) _canvasIndex.TryGetValue(nodeId, out node);
        if (node == null) return;
        _canvasEditNodeId = nodeId;
        _canvasEditEntryVp = clickVp;
        _canvasEditEntryTick = clickVp is null ? 0 : Stopwatch.GetTimestamp();
        // ⚠ 时序：_canvasEditNodeRef 必须在 SetCanvasEditText 之前赋值 —— SetCanvasEditText 内部会调
        //    EnsureCanvasEditLineSpacing，而那要用 _canvasEditNodeRef 去取"阅读态那份 layout 的实测行高"
        _canvasEditNodeRef = node;
        SelectCanvasNode(nodeId);       // 进入编辑即置为选中 ⇒ 退出编辑后自然回到【选中态】（与 Obsidian 一致）
        // 记录"已入史的文本基线"：打字过程中按停顿分段提交，未变的段不再产生历史条目。
        _canvasLastCommittedText = node.Text ?? "";
        StopCanvasEditCommitTimer();
        int editDocEnd = SetCanvasEditText(node.Text);
        CanvasEditBox.LayoutUpdated -= OnCanvasEditBoxLayoutUpdated;
        CanvasEditBox.LayoutUpdated += OnCanvasEditBoxLayoutUpdated;   // 每帧兜底：取内部 ScrollViewer + 刷新画面
        // 指针移动处理器已在构造时以 handledEventsToo 常驻订阅（见构造函数）——普通 `+=` 会被 RichEditBox
        CanvasEditBox.IsHitTestVisible = true;   // 每次进编辑都从"整卡可命中"起（清除上一次被挖除的命中带状态）
        PositionCanvasEditBox();
        // 显式把编辑框根光标固定为系统工字：控件（滚动条/四角/四边）悬停时由 OnCanvasEditBoxPointerMoved
        CanvasEditBox.OverrideCursor(CanvasIBeamCursor);
        ResetCanvasSelBaseline();
        _canvasEditExpandActive = false;
        _canvasEditExpandLineStart = -1;
        _canvasEditClickTick = 0;
        _canvasEditClicks = 0;
        _canvasEditDragAnchor = _canvasEditDragLast = _canvasEditPendingCaret = -1;
        _canvasEditLive = null;
        _canvasEditTextLayoutKey = -1;
        StartCaretBlink();
        StartCanvasEditPoll();
        InvalidateComposite();
        DispatcherQueue.TryEnqueue(() =>
        {
            CanvasEditBox.Focus(FocusState.Programmatic);
            // 带双击/点击落点：光标落在点击位置（Obsidian 行为）；映射失败回退文本末尾。
            int caret = clickVp is { } cvp ? MapCanvasClickToEditOffset(node, cvp) : -1;
            if (caret < 0) caret = editDocEnd;
            _canvasEditEntryCaret = caret;   // 双击第二下要恢复到这个位置（见 OnCanvasEditBoxPointerPressed）
            CanvasEditBox.Document.Selection.SetRange(caret, caret);
            ResetCaretBlink();
            ScreenCanvas.Invalidate();
            // 指针此刻是否在编辑卡上 —— 按【实际坐标】落在卡片屏幕矩形内判定，不再"有落点就假定在卡内"。
            //  · 带落点（双击卡内 / 单击已选中卡）：落点即指针位置；
            Point? entryPoint = clickVp;
            if (entryPoint == null && _canvasPointerValid) entryPoint = _canvasPointerPos;
            bool entryOnCard = entryPoint is { } ep &&
                TryGetCanvasNodeScreenRect(node.Id, out var entryRect) &&
                ep.X >= entryRect.X && ep.X <= entryRect.X + entryRect.Width &&
                ep.Y >= entryRect.Y && ep.Y <= entryRect.Y + entryRect.Height;
            // 启动"卡内=工字"光标作用域（对齐画笔）：轮询自愈，卡内（含按住/捕获/停下不动）恒工字，
            EnterIbeamScope(entryOnCard);
            // 强制刷一次工字：只对"指针确实在卡内"的入口做。工具栏按钮在卡外，刷了就会闪一下再被轮询改回来（用户报告）。
            if (entryOnCard)
            { try { CursorInterop.SetCursor(CursorInterop.LoadSystemCursor(CursorInterop.IdcIbeam)); } catch { } }
        });
    }

    /// 视口点击点 → 编辑框文档的光标偏移；-1 = 无法映射（落点在卡外/排版不可用，调用方回退文本末尾）。
    /// ⚠ 必须用【阅读态】layout 命中，再换算回源串（原文）下标：
    private int MapCanvasClickToEditOffset(CanvasNode node, Point vp)
    {
        if (node == null || string.IsNullOrEmpty(node.Text)) return 0;
        if (!CanvasViewportToPage(vp, out int pageIdx, out double px, out double py) ||
            pageIdx != node.GlancePage)
            return -1;
        double w = Math.Max(12, node.Width);
        double h = Math.Max(12, node.Height);
        double tw = w - 2 * CanvasCardPadX;
        double th = h - 2 * CanvasCardPadY;
        var layout = GetCanvasTextLayout(node, tw, th);
        if (layout == null) return -1;

        double realContentH = GetCachedCanvasLineCount(node, tw, th) * CanvasLineHeight;
        double topPad = Math.Min(0.5 * CanvasLineHeight, Math.Max(0, (th - Math.Min(th, realContentH)) / 2.0));
        double maxScroll = Math.Max(0, realContentH - th);
        double scrollY = Math.Clamp(GetCanvasScrollY(node.Id), 0, maxScroll);

        float lx = (float)(px - (node.X + CanvasCardPadX));
        float ly = (float)(py - (node.Y + CanvasCardPadY + topPad - scrollY));
        try
        {
            if (ly < 0) return 0;
            float textBottom = 0;
            var lms = layout.LineMetrics;
            if (lms != null) foreach (var lm in lms) textBottom += lm.Height;
            if (ly >= textBottom) return Math.Clamp(node.Text.Length, 0, _canvasEditText.Length);
            layout.HitTest(lx, ly, out var region, out bool trailingSide);
            int displayIndex = region.CharacterIndex + (trailingSide ? 1 : 0);
            int src = MapCanvasDisplayIndexToSource(node, displayIndex);
            // 命中点越到换行符（点击落在某行末尾右侧的空白）→ 光标钉在该行末尾（'\n' 之前），
            // 绝不越过换行 —— 否则表现成"在 line1 末尾点击/双击，光标跳到 line2 开头"。
            if (src >= 0 && src < _canvasEditText.Length && _canvasEditText[src] == '\n')
                return src;
            return Math.Clamp(src, 0, _canvasEditText.Length);
        }
        catch { return -1; }
    }

    private int MapCanvasDisplayIndexToSource(CanvasNode node, int displayIndex)
    {
        if (displayIndex <= 0) return 0;
        if (!_canvasTextCache.TryGetValue(node, out var cache) || cache.SourceIndexMap == null)
            return displayIndex;
        var map = cache.SourceIndexMap;
        return displayIndex >= map.Count ? map[map.Count - 1] : map[displayIndex];
    }

    /// 【编辑态】视口点击点 → 源串（文档）偏移；-1 = 取不到（卡外 / 排版未就绪）。
    /// 为什么必须自己算：可见串已按光标隐藏了部分标记 ⇒ 与原文档排版不同宽，
    private int MapCanvasEditPointToDocOffset(Point vp)
    {
        if (_canvasEditNodeId == null) return -1;
        CanvasNode? node;
        lock (_canvasLock) _canvasIndex.TryGetValue(_canvasEditNodeId, out node);
        if (node == null) return -1;
        if (!CanvasViewportToPage(vp, out int pageIdx, out double px, out double py) || pageIdx != node.GlancePage)
            return -1;

        double w = Math.Max(12, node.Width);
        double h = Math.Max(12, node.Height);
        double tw = Math.Max(1, w - 2 * CanvasCardPadX);
        double th = Math.Max(1, h - 2 * CanvasCardPadY);
        CanvasTextLayout? layout;
        try { layout = GetCanvasEditTextLayout(tw, th); }
        catch { return -1; }
        if (layout == null) return 0;
        double topPad = ComputeCanvasEditTopPad(tw, th);
        double scrollY = TryGetCanvasEditScrollMetrics(node, out _, out _, out double visMax)
            ? Math.Clamp(_canvasEditScrollViewer!.VerticalOffset, 0, visMax)
            : 0;

        float lx = (float)(px - (node.X + CanvasCardPadX));
        float ly = (float)(py - (node.Y + CanvasCardPadY + topPad - scrollY));
        try
        {
            if (ly < 0) return 0;
            float textBottom = 0;
            var lms = layout.LineMetrics;
            if (lms != null) foreach (var lm in lms) textBottom += lm.Height;
            if (ly >= textBottom) return _canvasEditText.Length;   // 文字下方 → 末尾（与系统文本框语义一致）

            layout.HitTest(lx, ly, out var region, out bool trailingSide);
            int dispIndex = region.CharacterIndex + (trailingSide ? 1 : 0);
            var live = _canvasEditLive;
            if (live == null) return Math.Clamp(dispIndex, 0, _canvasEditText.Length);
            return Math.Clamp(live.ToDoc(dispIndex), 0, _canvasEditText.Length);
        }
        catch { return -1; }
    }

    /// <summary>把选区设成 [start,end]（源串偏移）。调用方约定： 是**活动端**—— 拖选时它就是跟着指针走的那一端，单击落光标时两端相同。 ⚠ 基线必须写成"这次设的选区 + 活动端 = end"，绝不能清成 -1：清掉之后 会走"无基线 ⇒ 默认右端"的兜底，</summary>
    private void SetCanvasEditSelection(int start, int end)
    {
        try
        {
            var sel = CanvasEditBox.Document.Selection;
            sel.SetRange(start, end);
            SetCanvasSelBaseline(start, end, end);
        }
        catch { }
        ResetCaretBlink();
        ScreenCanvas.Invalidate();
    }

    /// <summary>单击落光标：原生命中处理在类处理器里先跑（且按文档排版算）⇒ 排到队尾覆盖，才能赢。 与"双击补救"同一手法。</summary>
    private void ApplyCanvasEditCaretAt(int docOffset)
    {
        _canvasEditPendingCaret = docOffset;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_canvasEditNodeId == null) return;
            // 排队期间若已开始拖选 / 又点了一下 ⇒ 这个请求作废（否则会把新选区又塌回收缩光标）。
            if (_canvasEditPendingCaret != docOffset) return;
            _canvasEditPendingCaret = -1;
            SetCanvasEditSelection(docOffset, docOffset);
        });
    }

    /// <summary>
    /// 双击选词：自己用系统分词器（`WordsSegmenter`，中文走词典分词）算出词边界再设选区。
    /// 位置落在空白/标点上时什么都不做（返回前已判定）—— 交回原生的单字行为即可。
    /// ⚠ 必须排队：原生命中在本次按下之后才落定选区（它会按单字选一个），排队才能覆盖它。
    /// </summary>
    private void SelectCanvasEditWordAt(int docOffset)
    {
        string text = _canvasEditText;
        if (text.Length == 0) return;
        if (!TryGetCanvasWordRange(text, docOffset, out int start, out int end)) return;
        if (end <= start) return;
        _canvasEditPendingCaret = -1;   // 作废可能排队中的"单击落光标"，否则它会把选区又塌掉
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_canvasEditNodeId == null) return;
            if (!string.Equals(_canvasEditText, text, StringComparison.Ordinal)) return;   // 期间文本变了 ⇒ 偏移作废
            SetCanvasEditSelection(start, end);
        });
    }

    /// <summary>取 at 所在词的 [start,end)（源串偏移）。 ⚠ 点击给的是【插入位置】：字左半 → 该字下标，字右半 → 下一个字下标。 故先按 at 找，找不到再按 at-1 找（等价于"点在词尾也算这个词"）；都不中说明落在空白/标点上。</summary>
    private static bool TryGetCanvasWordRange(string text, int at, out int start, out int end)
    {
        start = end = 0;
        if (text.Length == 0) return false;
        try
        {
            var segmenter = new Windows.Data.Text.WordsSegmenter(CanvasSegmenterLanguage(text));
            var tokens = segmenter.GetTokens(text);
            if (TryMatchCanvasWord(tokens, text, at, out start, out end)) return true;
            return at > 0 && TryMatchCanvasWord(tokens, text, at - 1, out start, out end);
        }
        catch { return false; }   // 分词器不可用（语言包缺失等）⇒ 退回原生的单字行为
    }

    /// <summary>在分词结果里找包含 index 的那个词。 token 的 StartPosition 走投影取不到（IWordSegment 是 internal）⇒ 按顺序在原文里定位：token 本身有序， 游标单调前进，每次取"从游标起的第一个匹配"，位置即唯一。</summary>
    private static bool TryMatchCanvasWord(IReadOnlyList<Windows.Data.Text.WordSegment> tokens, string text,
        int index, out int start, out int end)
    {
        start = end = 0;
        int cursor = 0;
        foreach (var token in tokens)
        {
            string word = token.Text;
            if (string.IsNullOrEmpty(word)) continue;
            int s = text.IndexOf(word, cursor, StringComparison.Ordinal);
            if (s < 0) continue;
            int e = s + word.Length;
            cursor = e;
            if (index >= s && index < e)
            {
                start = s;
                end = e;
                return true;
            }
            if (s > index) break;   // 已经越过 index
        }
        return false;
    }

    /// <summary>双击该行 ⇒ 展开这一行【所有】成对标记（收起条件：光标离开该行，见 BuildCanvasEditLive）。</summary>
    private void ExpandCanvasEditLine(int docOffset)
    {
        string t = _canvasEditText;
        if (t.Length == 0) return;
        int start = Math.Clamp(docOffset, 0, t.Length);
        while (start > 0 && t[start - 1] != '\n') start--;
        if (_canvasEditExpandActive && start == _canvasEditExpandLineStart) return;
        _canvasEditExpandActive = true;
        _canvasEditExpandLineStart = start;
        ScreenCanvas.Invalidate();
    }

    /// <summary>累积"同一处的连击"次数（1 = 单击，2 = 双击，≥3 = 三击）：超时间窗或超位置容差就从 1 重新数。 双击/三击的语义不同（选词 vs 选段），故这里要的是次数而不是"是不是连击"。</summary>
    private int CountCanvasEditClicks(Point p)
    {
        long now = Stopwatch.GetTimestamp();
        double ms = _canvasEditClickTick == 0 ? double.MaxValue
            : (now - _canvasEditClickTick) * 1000.0 / Stopwatch.Frequency;
        bool repeat = ms <= CanvasDoubleClickWindowMs &&
            Math.Abs(p.X - _canvasEditClickVp.X) <= CanvasDoubleClickSlopDip &&
            Math.Abs(p.Y - _canvasEditClickVp.Y) <= CanvasDoubleClickSlopDip;
        _canvasEditClicks = repeat ? _canvasEditClicks + 1 : 1;
        _canvasEditClickTick = now;
        _canvasEditClickVp = p;
        return _canvasEditClicks;
    }

    private void EndCanvasEdit(bool commit)
    {
        if (_canvasEditNodeId == null) return;
        if (commit) FlushCanvasEditText();   // 提交最后一段尚未入史的文字
        else StopCanvasEditCommitTimer();
        _canvasEditNodeId = null;
        _canvasEditControlGesture = false;
        _canvasEditMenuClickPending = false;
        _canvasEditEntryTick = 0;
        _canvasEditEntryCaret = -1;
        _canvasEditMenuKeyTick = 0;
        _canvasEditLastPressWasRight = false;
        _canvasEditLastPressWasLeft = false;
        _canvasEditLastPressTick = 0;
        _canvasEditLastPressDevice = Microsoft.UI.Input.PointerDeviceType.Touch;
        _canvasEditExpandActive = false;
        _canvasEditExpandLineStart = -1;
        _canvasEditClickTick = 0;
        _canvasEditClicks = 0;
        ResetCanvasSelBaseline();
        _canvasEditDragAnchor = _canvasEditDragLast = _canvasEditPendingCaret = -1;
        _canvasEditLive = null;
        _canvasEditTextLayoutKey = -1;
        StopCaretBlink();
        StopCanvasEditPoll();
        ExitIbeamScope();
        CanvasEditBox.OverrideCursor(null);
        this.ProtectedCursor = null;                                   // 同步清根光标，退出编辑后首帧不残留缩放箭头
        CanvasEditBox.LayoutUpdated -= OnCanvasEditBoxLayoutUpdated;   // 退出编辑即退订重试钩子
        CanvasEditBox.IsHitTestVisible = true;   // 复位命中区（可能停在"被挖除"态）⇒ 下次进编辑是干净的整卡可命中
        CanvasEditBox.Visibility = Visibility.Collapsed;
        // 折叠的 RichEditBox 仍会保持键盘焦点（WinUI 已知行为），会吞掉 F/G/Ctrl+Z 这类页面级按键：
        // 表现为白板模式下快捷键整体失效、字母键进入输入法。必须把焦点显式移回滚动宿主。
        RestoreViewerFocus();
        SyncCanvasOverlayNow();
        InvalidateComposite();
    }

    private void RestoreViewerFocus()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_canvasEditNodeId != null) return;
            // ⚠ 只有焦点【还滞留在】白板浮层内、或已经落空时才交还视口。白板浮层 = CanvasOverlay 的
            //    整棵可视子树（编辑框 CanvasEditBox、卡片/连线胶囊及其中按钮）。这些元素都随「退出编辑」
            //    否则会把刚被点开的 Flyout 顶掉，表现为"设置按钮第一次点没反应，第二次才展开"（用户报告）。
            //    ⚠ 只判 ReferenceEquals(CanvasEditBox) 会漏掉"焦点落在胶囊删除按钮上"这条。
            //      用户实测：双击建卡 → 点胶囊删除（误创建）→ 焦点留在胶囊/折叠编辑框 ⇒ F/G 变打字。
            object? focused = FocusManager.GetFocusedElement(XamlRoot);
            if (focused != null &&
                !ReferenceEquals(focused, CanvasEditBox) &&
                !IsWithinVisual(focused as DependencyObject, CanvasOverlay))
                return;
            if (Viewer != null && Viewer.Focus(FocusState.Programmatic)) return;
            FocusManager.TryMoveFocus(FocusNavigationDirection.Next);
        });
    }

    /// <summary>判断 是否落在 的可视子树内（含自身）。 用于"焦点是否滞留在白板浮层内"的判定：折叠的元素仍在树里，GetParent 依旧可达。</summary>
    private static bool IsWithinVisual(DependencyObject? element, DependencyObject? ancestor)
    {
        if (element == null || ancestor == null) return false;
        for (var cur = element; cur != null;
             cur = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cur))
        {
            if (ReferenceEquals(cur, ancestor)) return true;
        }
        return false;
    }

    /// <summary>把键盘焦点交给视口宿主（若尚未在它身上）。页面级按键里，Delete/Backspace 走的是 MainPage 上依赖焦点的 KeyDown 路由，鼠标点击画布并不会让 ScrollViewer 自动获焦 ⇒ 需在画布左键 按下时显式补齐（否则首次打开带卡片的 PDF，</summary>
    private void EnsureViewerFocus()
    {
        if (Viewer == null) return;
        if (ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), Viewer)) return;
        Viewer.Focus(FocusState.Programmatic);
    }

    private void HideCanvasInteraction()
    {
        _selectedCanvasNodeId = null;                 // 先清选中，收尾时浮层不会又把控件显出来
        _selectedCanvasEdgeId = null;
        _hoverCanvasNodeId = null;
        _hoverCanvasEdgeId = null;
        if (_canvasEditNodeId != null) EndCanvasEdit(commit: true); // 正在编辑 → 提交后再退出
        _isCanvasDragging = false;
        _canvasDragNode = null;
        _canvasDragPage = -1;
        _isCanvasConnecting = false;
        _connRerouteEdgeId = null;
        _connFromNodeId = null;
        _canvasEdgePressPending = false;
        _canvasEdgePressEdgeId = null;
        _isCanvasScrollDragging = false;
        _canvasScrollDragNodeId = null;
        CancelCanvasResize();
        CancelCanvasConnArm();      // 待接态不捕获指针 ⇒ 不会被 CaptureLost 兜到，必须显式清
        HideCanvasCardToolbar();
        HideCanvasEdgeToolbar();
        CollapseCanvasOverlayIfIdle();   // 选中/编辑都已清 ⇒ 整层浮层直接收
        if (CanvasEditBox != null) CanvasEditBox.Visibility = Visibility.Collapsed;
    }

    private void ExitCanvasInteraction()
    {
        ExitHandScope();   // 停手型作用域轮询并卸载（对齐 ExitIbeamScope）
        _isCanvasDragging = false;
        _canvasDragNode = null;
        _canvasDragPage = -1;
        _isCanvasConnecting = false;
        _connRerouteEdgeId = null;
        _connFromNodeId = null;
        _canvasEdgePressPending = false;
        _canvasEdgePressEdgeId = null;
        _isCanvasScrollDragging = false;
        _canvasScrollDragNodeId = null;
        CancelCanvasResize();
        CancelCanvasConnArm();      // 退出白板 ⇒ 待接态一并作废（同上：没有捕获可兜底）
        // 编辑态必须走 EndCanvasEdit 正式收尾：它会提交最后一段尚未入史的文字、收起文本菜单，
        // ⚠ 不能直接清 `_canvasEditNodeId` —— 编辑框折叠了但**焦点还留在它身上**，切到阅读/画笔模式后
        if (_canvasEditNodeId != null)
        {
            EndCanvasEdit(commit: true);
        }
        else
        {
            ExitIbeamScope();
            CanvasEditBox.LayoutUpdated -= OnCanvasEditBoxLayoutUpdated;   // 直接清 id 的路径也要退订（否则钩子残留）
        }
        _selectedCanvasNodeId = null;
        _selectedCanvasEdgeId = null;
        _hoverCanvasNodeId = null;
        _hoverCanvasEdgeId = null;
        _canvasPointerValid = false;
        HideCanvasCardToolbar();
        HideCanvasEdgeToolbar();
        CollapseCanvasOverlayIfIdle();   // 同上：两颗胶囊都收了才收整层浮层
        if (CanvasEditBox != null) CanvasEditBox.Visibility = Visibility.Collapsed;
        InvalidateComposite();
    }

    /// <summary>视口坐标 → 命中卡片（页内坐标测试，取绘制顺序最上层者）+ 该页局部坐标。 命中顺序必须与绘制顺序一致：选中的卡片被置顶绘制（见 DrawWhiteboardLayer），故先判它， 否则点在与下层卡片重叠的区域会选到被盖住的那张。</summary>
    private bool TryHitCanvasNode(Point vp, out CanvasNode? node, out int pageIndex,
        out double px, out double py)
    {
        node = null;
        if (!_annotationsVisible) { pageIndex = -1; px = 0; py = 0; return false; }
        if (!CanvasViewportToPage(vp, out pageIndex, out px, out py))
        {
            pageIndex = -1; px = 0; py = 0;
            return false;
        }
        CanvasNode[] nodes;
        CanvasNode? selNode = null;
        string? sel = _selectedCanvasNodeId;
        lock (_canvasLock)
        {
            nodes = _canvasNodes;
            if (sel != null) _canvasIndex.TryGetValue(sel, out selNode);
        }
        if (selNode != null && selNode.GlancePage == pageIndex &&
            px >= selNode.X && px <= selNode.X + selNode.Width &&
            py >= selNode.Y && py <= selNode.Y + selNode.Height)
        {
            node = selNode;
            return true;
        }
        for (int i = nodes.Length - 1; i >= 0; i--)
        {
            var n = nodes[i];
            if (n.GlancePage != pageIndex) continue;
            if (px >= n.X && px <= n.X + n.Width && py >= n.Y && py <= n.Y + n.Height)
            {
                node = n;
                return true;
            }
        }
        pageIndex = -1; px = 0; py = 0;
        return false;
    }

    /// <summary>选中卡片某条边中点手柄的屏幕坐标（视口 DIP）。</summary>
    private bool TryGetCanvasHandleScreen(string nodeId, string side, out Vector2 screen)
    {
        screen = default;
        var pages = _pages;
        if (pages.Count == 0) return false;
        CanvasNode? node = null;
        int pageIdx = -1;
        lock (_canvasLock)
        {
            if (_canvasIndex.TryGetValue(nodeId, out var n)) { node = n; pageIdx = n.GlancePage; }
        }
        if (node == null || pageIdx < 0 || pageIdx >= pages.Count) return false;
        GetViewState(out double z, out double hOff, out double vOff);
        if (z <= 0) return false;
        var xf = MakePageXform(ComputePageDest(pageIdx, z, hOff, vOff), z);
        var a = SideAnchor(node, side);
        screen = xf.Map(a.X, a.Y);
        return true;
    }

    private bool TryHitCanvasHandle(Point vp, out string nodeId, out string side)
    {
        nodeId = ""; side = "";
        if (!_canvasInputActive || !_annotationsVisible) return false;
        // 手柄可及的目标卡：优先【指针悬停的卡】（Obsidian 式"悬停边缘即可直接拖"，无需先选中），
        string? target = _hoverCanvasNodeId ?? _selectedCanvasNodeId;
        if (target == null) return false;
        CanvasNode? node = null;
        lock (_canvasLock) _canvasIndex.TryGetValue(target, out node);
        if (node == null || node.GlancePage < 0) return false;
        if (!CanvasViewportToLockedPage(node.GlancePage, vp, out double px, out double py)) return false;
        GetViewState(out double z, out _, out _);
        double r = Math.Clamp(CanvasHandleHitRadius * z, CanvasConnHandleHitMinDip, CanvasConnHandleHitMaxDip) / z;
        double r2 = r * r;
        foreach (var s in CanvasSides)
        {
            var a = SideAnchor(node, s);
            double dx = a.X - px, dy = a.Y - py;
            if (dx * dx + dy * dy <= r2) { nodeId = target; side = s; return true; }
        }
        return false;
    }

    /// <summary>目标卡片中【离指针最近】的边中点 → 吸附边（屏幕距离比较）。 不用"卡片中心相对方位"决定：那样用户在卡片任意位置悬停都只吸到同一条边， 无法自由选边；按指针到四个中点手柄的屏幕距离取最小，手指在哪就吸哪条边，直观可控。</summary>
    private string ComputeTargetSide(string toId, Point vp)
    {
        string best = "left";
        double bestD2 = double.MaxValue;
        foreach (var s in CanvasSides)
        {
            if (!TryGetCanvasHandleScreen(toId, s, out var c)) continue;
            double dx = vp.X - c.X, dy = vp.Y - c.Y;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = s; }
        }
        return best;
    }

    private void BeginCanvasConnection(string nodeId, string side, Point vp)
    {
        _isCanvasConnecting = true;
        HideCanvasCardToolbar();   // 拉线开始：立即暂隐卡片悬浮胶囊，防止挡住连线过程
        LockCanvasCursor(CanvasIdcArrow, _canvasEditNodeId != null);
        _connFromNodeId = nodeId;
        _connFromSide = side;
        _connCursorScreen = vp;
        InvalidateOverlayThrottled();
    }

    private void UpdateCanvasConnecting(Point vp)
    {
        _connCursorScreen = vp;   // 目标与源锚点都在绘制时按当前视口现场派生，这里只记指针位置
        InvalidateOverlayThrottled();
    }

    /// <summary>拉线固定端（源手柄）的屏幕锚点 + 法线：由 (node, side) + 当前视口现场派生，绝不缓存 —— 视口一变即自动跟随。</summary>
    private bool TryGetCanvasConnOriginScreen(out Vector2 anchor, out Vector2 normal)
    {
        anchor = normal = default;
        string? nodeId = _connFromNodeId;
        if (string.IsNullOrEmpty(nodeId) || _connFromSide.Length == 0) return false;
        CanvasNode? node = null;
        lock (_canvasLock) _canvasIndex.TryGetValue(nodeId, out node);
        if (node == null || node.GlancePage < 0 || node.GlancePage >= _pages.Count) return false;
        GetViewState(out double z, out double hOff, out double vOff);
        if (z <= 0) return false;
        var xf = MakePageXform(ComputePageDest(node.GlancePage, z, hOff, vOff), z);
        var a = SideAnchor(node, _connFromSide);
        anchor = xf.Map(a.X, a.Y);
        var nrm = SideNormal(_connFromSide);
        normal = xf.MapDirection(nrm.X, nrm.Y);
        return true;
    }

    /// <summary>拉线动端在给定屏幕点命中的目标卡（排除源卡自身）：返回该边锚点 + 法线。同样按当前视口现场派生。</summary>
    private bool TryGetCanvasConnTargetScreen(Point vp, string? excludeNodeId,
        out Vector2 anchor, out Vector2 normal)
    {
        anchor = normal = default;
        if (!TryHitCanvasNode(vp, out var hit, out _, out _, out _) || hit == null) return false;
        if (excludeNodeId != null && string.Equals(hit.Id, excludeNodeId, StringComparison.Ordinal)) return false;
        string side = ComputeTargetSide(hit.Id, vp);
        if (!TryGetCanvasHandleScreen(hit.Id, side, out anchor)) return false;
        if (hit.GlancePage < 0 || hit.GlancePage >= _pages.Count) return false;
        GetViewState(out double z, out double hOff, out double vOff);
        if (z <= 0) return false;
        var xf = MakePageXform(ComputePageDest(hit.GlancePage, z, hOff, vOff), z);
        var nrm = SideNormal(side);
        normal = xf.MapDirection(nrm.X, nrm.Y);
        return true;
    }

    public bool CancelCanvasConnection()
    {
        if (!_isCanvasConnecting) return false;
        _isCanvasConnecting = false;
        _connRerouteEdgeId = null;
        SyncCanvasOverlayNow();   // 取消连线 ⇒ 恢复卡片悬浮胶囊（若仍有卡片选中）
        _connFromNodeId = null;
        UnlockCanvasCursor();   // 连线结束 ⇒ 解锁光标（恢复框架默认）
        InvalidateComposite();
        return true;
    }

    // ================= 按住波浪键两次点击建线（跨视口） =================

    /// <summary>取消"待接"态（松开触发键 / 点空白 / 同卡再点 / 退出白板 都走这里）。 状态只活在触发键按住期间，故不存在"没人选终点还一直占着"的悬空态。 ⚠ 刻意【不】挂 Esc：它不是本机制的一部分，交给全局 Esc 处理（拉线取消、退出别的态）。</summary>
    private void CancelCanvasConnArm()
    {
        if (!_connArmActive) return;
        _connArmActive = false;
        _connArmNodeId = null;
        _connArmSide = "";
        InvalidateComposite();
    }

    private void ArmCanvasConn(string nodeId, string side, Point vp)
    {
        _connArmActive = true;
        _connArmNodeId = nodeId;
        _connArmSide = side ?? "";
        _connArmCursorVp = vp;
        InvalidateComposite();
    }

    private void CompleteCanvasConnArm(string targetNodeId, string targetSide)
    {
        string? fromNode = _connArmNodeId;
        string fromSide = _connArmSide;
        CancelCanvasConnArm();
        if (string.IsNullOrEmpty(fromNode)) return;
        if (string.Equals(fromNode, targetNodeId, StringComparison.Ordinal)) return;
        CanvasNodeConnectionRequested?.Invoke(this, new CanvasNodeEventArgs
        {
            NodeId = fromNode, FromSide = fromSide, TargetNodeId = targetNodeId, ToSide = targetSide
        });
    }

    /// <summary>按住触发键（Tab 上方波浪键）时的点击：第一次选起点，第二次选终点。始/终都可以是"拉线控件"（指定边）或"卡片本体"（自动选边），可混用。 返回 true = 本次按下已被本机制消费（调用方直接 return）。</summary>
    private bool TryHandleCanvasConnArmPress(Point vp, PointerRoutedEventArgs e)
    {
        // 只接管左键：右键必须留给"右键拖拽缩放"。
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        bool armedKey = IsConnArmTriggerDown();
        if (!armedKey)
        {
            // armed 但触发键已松（KeyUp 漏收时的兜底）⇒ 取消后【照常】处理这次点击，不吞掉 —— 用户这时要的是普通点击。
            CancelCanvasConnArm();
            return false;
        }
        StopSmoothZoom();
        StopWheelScroll();
        EnsureViewerFocus();   // 松键取消靠键盘事件判定 ⇒ 视口必须持有焦点
        // 命中顺序与正常链一致：四边中点拉线控件（指定边）优先于卡片本体（自动边）
        if (TryHitCanvasHandle(vp, out var hNode, out var hSide))
        {
            if (!_connArmActive) { ArmCanvasConn(hNode, hSide, vp); return true; }
            if (string.Equals(hNode, _connArmNodeId, StringComparison.Ordinal)) { CancelCanvasConnArm(); return true; }
            CompleteCanvasConnArm(hNode, hSide);
            return true;
        }
        if (TryHitCanvasNode(vp, out var node, out _, out _, out _) && node != null)
        {
            if (!_connArmActive) { ArmCanvasConn(node.Id, "", vp); return true; }
            if (string.Equals(node.Id, _connArmNodeId, StringComparison.Ordinal)) { CancelCanvasConnArm(); return true; }
            // 终点边与预览同一套算法（指针最近的手柄）⇒ 预览看到吸在哪条边，落定就是那条边
            CompleteCanvasConnArm(node.Id, ComputeTargetSide(node.Id, vp));
            return true;
        }
        // 空白：armed 时取消；未 armed 时也消费掉 —— 触发键按住时不该顺手开始平移/选卡
        CancelCanvasConnArm();
        return true;
    }

    /// <summary>松开触发键（波浪键）⇒ 取消待接态。挂在根上（焦点在白板里即可收到）。 ⚠ OEM 键在 WinUI 里可能被映射成别的值，故 Key / OriginalKey 都认。</summary>
    private void Root_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == ConnArmTriggerKey || e.OriginalKey == ConnArmTriggerKey) CancelCanvasConnArm();
    }

    /// <summary>开始"改线"：拖动已有连线时断开离指针更近的一端（等距则断终点 to），复用新建连线的预览状态机（固定端 → 指针）。 起手即取消全部选中（本线或别的卡片/线）：胶囊随选中态一起收起，拖动期间不再随动；完成后按指针位置重判悬浮态（见 CommitCanvasEdgeReroute）。</summary>
    private void BeginCanvasEdgeReroute(string edgeId, Point vp)
    {
        var pages = _pages;
        if (pages.Count == 0) return;
        CanvasEdge[] edges;
        Dictionary<string, CanvasNode> index;
        lock (_canvasLock) { edges = _canvasEdges; index = _canvasIndex; }
        CanvasEdge? edge = null;
        foreach (var ee in edges)
            if (string.Equals(ee.Id, edgeId, StringComparison.Ordinal)) { edge = ee; break; }
        if (edge == null) return;
        GetViewState(out double z, out double hOff, out double vOff);
        if (z <= 0) return;
        Rect DestOf(int idx) => ComputePageDest(idx, z, hOff, vOff);
        if (!ComputeEdgeScreenGeometry(edge, pages, DestOf, index, z,
                out var a1, out _, out _, out var a2))
            return;
        var p = new Vector2((float)vp.X, (float)vp.Y);
        // 断开离指针更近的一端；两边等距时断终点（to），与用户口径一致。
        bool detachFrom = Vector2.Distance(p, a1) < Vector2.Distance(p, a2);
        // 固定端 = 另一端。ComputeEdgeScreenGeometry 已把 null 的 Side 写回模型，故此处非空。
        string fixedNode = detachFrom ? edge.ToNode : edge.FromNode;
        string fixedSide = detachFrom ? (edge.ToSide ?? "") : (edge.FromSide ?? "");
        if (string.IsNullOrEmpty(fixedNode) || fixedSide.Length == 0) return;
        // 改线：取消全部选中态（本线/其他线都一样），回到"悬浮态"工作；胶囊随选中态一起收起（拖动期间不再随动）。
        SelectCanvasNode(null);
        _hoverCanvasEdgeId = edgeId;
        // 复用新建连线预览：源 = 固定端（预览从固定端锚点连到指针/目标）。
        BeginCanvasConnection(fixedNode, fixedSide, vp);
        _connRerouteEdgeId = edgeId;
        _connRerouteDetachFrom = detachFrom;
    }

    /// <summary>改线收尾：落到卡片（且不是固定端所属卡片）→ 改接该端；落到空白 → 删除整条连线（不新建卡片）。 全程无选中态（改线开始时已清），完成后按指针位置重判悬浮态；不改动 FromEnd/ToEnd/Color（箭头样式）。</summary>
    private void CommitCanvasEdgeReroute(string edgeId, bool detachFrom, string? fixedNode, Point dropPos)
    {
        _canvasPointerPos = dropPos;
        _canvasPointerValid = true;
        bool onCard = TryHitCanvasNode(dropPos, out var hit, out _, out _, out _) && hit != null;
        // 落到固定端所属卡片 = 自连，不接受：连线保持原样（不删）。
        if (onCard && hit != null && fixedNode != null &&
            string.Equals(hit.Id, fixedNode, StringComparison.Ordinal))
        {
            UpdateCanvasEdgeHover(dropPos);
            InvalidateComposite();
            return;
        }
        if (onCard && hit != null)
        {
            string side = ComputeTargetSide(hit.Id, dropPos);
            CanvasEdgeRerouteRequested?.Invoke(this, new CanvasNodeEventArgs
            {
                EdgeId = edgeId,
                RerouteFromEnd = detachFrom,
                TargetNodeId = hit.Id,
                ToSide = side
            });
            UpdateCanvasEdgeHover(dropPos);      // 完成后按当前指针位置重判悬浮态
            InvalidateComposite();
            return;
        }
        // 空白 → 删除整条连线（不新建卡片）。
        if (string.Equals(_hoverCanvasEdgeId, edgeId, StringComparison.Ordinal))
            _hoverCanvasEdgeId = null;
        CanvasEdgeDeleteRequested?.Invoke(this, new CanvasNodeEventArgs { EdgeId = edgeId });
        UpdateCanvasEdgeHover(dropPos);
        InvalidateComposite();
    }

    private bool TryHitCanvasCorner(Point vp, out string nodeId, out string corner)
    {
        nodeId = ""; corner = "";
        if (!_canvasInputActive || !_annotationsVisible) return false;
        // 与连线手柄同规则：优先指针悬停的卡（悬停四角即可直接缩放），其次回退选中卡；
        string? target = _hoverCanvasNodeId ?? _selectedCanvasNodeId;
        if (target == null) return false;
        CanvasNode? node = null;
        lock (_canvasLock) _canvasIndex.TryGetValue(target, out node);
        if (node == null || node.GlancePage < 0) return false;
        if (!CanvasViewportToLockedPage(node.GlancePage, vp, out double px, out double py)) return false;
        double r2 = CanvasHandleHitRadius * CanvasHandleHitRadius;
        foreach (var c in CanvasCorners)
        {
            var a = CornerAnchor(node, c);
            double dx = a.X - px, dy = a.Y - py;
            if (dx * dx + dy * dy <= r2) { nodeId = target; corner = c; return true; }
        }
        return false;
    }

    /// <summary>视口坐标 → 命中选中卡片某条边【中段】（单轴缩放区）。 区域 = 卡片四条边线段上"垂直距离 ≤ 命中半径"，且落在【四角圆域】与【四边中点圆域】之间 （即"边中点与四角之间的带状区"）。调用方按 角 &gt; 中点 &gt; 边 的顺序判定，故此处无需排除两端。</summary>
    private bool TryHitCanvasEdge(Point vp, out string nodeId, out string edge)
    {
        nodeId = ""; edge = "";
        if (!_canvasInputActive || !_annotationsVisible) return false;
        // 与角/中点手柄同规则：优先指针悬停的卡（悬停边即可直接单轴缩放），其次回退选中卡。
        string? target = _hoverCanvasNodeId ?? _selectedCanvasNodeId;
        if (target == null) return false;
        CanvasNode? node = null;
        lock (_canvasLock) _canvasIndex.TryGetValue(target, out node);
        if (node == null || node.GlancePage < 0) return false;
        if (!CanvasViewportToLockedPage(node.GlancePage, vp, out double px, out double py)) return false;
        double r = CanvasHandleHitRadius;
        double r2 = r * r;
        double l = node.X, t = node.Y, rr = node.X + node.Width, b = node.Y + node.Height;
        double bestD2 = double.MaxValue; string bestE = "";
        // 每条边：取指针在该边线段上的最近点，比较平方距离。
        {
            double cx = Math.Clamp(px, l, rr), cy = t;
            double dx = px - cx, dy = py - cy, d2 = dx * dx + dy * dy;
            if (d2 <= r2 && d2 < bestD2) { bestD2 = d2; bestE = "top"; }
        }
        {
            double cx = Math.Clamp(px, l, rr), cy = b;
            double dx = px - cx, dy = py - cy, d2 = dx * dx + dy * dy;
            if (d2 <= r2 && d2 < bestD2) { bestD2 = d2; bestE = "bottom"; }
        }
        {
            double cx = l, cy = Math.Clamp(py, t, b);
            double dx = px - cx, dy = py - cy, d2 = dx * dx + dy * dy;
            if (d2 <= r2 && d2 < bestD2) { bestD2 = d2; bestE = "left"; }
        }
        {
            double cx = rr, cy = Math.Clamp(py, t, b);
            double dx = px - cx, dy = py - cy, d2 = dx * dx + dy * dy;
            if (d2 <= r2 && d2 < bestD2) { bestD2 = d2; bestE = "right"; }
        }
        if (bestE.Length == 0) return false;
        nodeId = target; edge = bestE;
        return true;
    }

    private void BeginCanvasResize(string nodeId, string corner, int pageIndex)
    {
        CanvasNode? node = null;
        lock (_canvasLock) _canvasIndex.TryGetValue(nodeId, out node);
        if (node == null) return;
        _isCanvasResizing = true;
        _canvasResizeNode = node;
        _canvasResizePage = pageIndex;
        _canvasResizeMode = "corner";
        _canvasResizeCorner = corner;
        _canvasResizeOrigX = node.X;
        _canvasResizeOrigY = node.Y;
        _canvasResizeOrigW = node.Width;
        _canvasResizeOrigH = node.Height;
        LockCanvasCursor((corner == "tl" || corner == "br") ? CanvasIdcSizeNwSe : CanvasIdcSizeNeSw, _canvasEditNodeId != null);
    }

    /// <summary>开始【单轴】缩放：只动一条边（edge = top/bottom/left/right），对侧固定。 用于卡片"边中点与四角之间"的带状区拖拽。</summary>
    private void BeginCanvasEdgeResize(string nodeId, string edge, int pageIndex)
    {
        CanvasNode? node = null;
        lock (_canvasLock) _canvasIndex.TryGetValue(nodeId, out node);
        if (node == null) return;
        _isCanvasResizing = true;
        _canvasResizeNode = node;
        _canvasResizePage = pageIndex;
        _canvasResizeMode = "edge";
        _canvasResizeEdge = edge;
        _canvasResizeOrigX = node.X;
        _canvasResizeOrigY = node.Y;
        _canvasResizeOrigW = node.Width;
        _canvasResizeOrigH = node.Height;
        LockCanvasCursor((edge == "top" || edge == "bottom") ? CanvasIdcSizeNs : CanvasIdcSizeWe, _canvasEditNodeId != null);
    }

    /// <summary>锁定画布手势光标：把"系统箭头（编辑态保留系统 I 形）"资源替换为该标准光标（对齐画笔圆环的 SetSystemCursor 方案）。 框架每帧直接 SetCursor(箭头/I形)，资源被替换后画出的就是手势光标，拖动全程（含指针停下）稳定不回退。</summary>
    private void LockCanvasCursor(int idcId, bool keepIBeam = false)
    {
        _canvasCursorLocked = true;
        CursorInterop.OverrideSystemCursorTo(idcId, keepIBeam);
    }

    /// 解锁画布手势光标：用 SPI_SETCURSORS 一次性恢复全部系统光标（箭头/I形还原为系统默认）。
    /// ⚠ 只在【确实锁过】时还原：编辑态的文本点击/拖选是编辑框通道驱动的，松手与编辑框丢捕获都会让
    private void UnlockCanvasCursor()
    {
        if (!_canvasCursorLocked) return;
        _canvasCursorLocked = false;
        CursorInterop.RestoreSystemCursors();
        // SPI_SETCURSORS 已把箭头槽还原成真箭头 ⇒ 安装标志同步复位，工字/手型作用域轮询下一拍（≤25ms）重新评估
        _ibeamInstalled = false;
        _handInstalled = false;
    }

    /// 启动编辑态"卡内=工字"光标作用域（对齐画笔 EnterPenRingMode）：建 25ms 轮询并立刻评估一次。
    /// ⚠ **绝不能无条件写 true**：进编辑有两条【不带落点】的入口 —— 卡片悬浮工具栏的「编辑」按钮（在卡片外面）
    private void EnterIbeamScope(bool pointerOnCard)
    {
        _canvasPointerOnEditCard = pointerOnCard;
        if (_ibeamScopeTimer == null)
        {
            _ibeamScopeTimer = DispatcherQueue.CreateTimer();
            _ibeamScopeTimer.Interval = TimeSpan.FromMilliseconds(IbeamScopeIntervalMs);
            _ibeamScopeTimer.Tick += IbeamScopeTick;
        }
        try { _ibeamScopeTimer.Stop(); _ibeamScopeTimer.Start(); } catch { }
        UpdateIbeamScope();
    }

    private void ExitIbeamScope()
    {
        try { _ibeamScopeTimer?.Stop(); } catch { }
        SetIbeamInstalled(false);
        _canvasPointerOnEditCard = false;
    }

    private void IbeamScopeTick(object? sender, object e) => UpdateIbeamScope();

    /// <summary>作用域判定（每 25ms 一拍，对齐画笔 UpdateRingScope）：编辑态 + 指针在编辑卡内 + 非边框控件命中带 ⇒ 装工字；否则卸下。手势进行中不干预（手势光标由 LockCanvasCursor/UnlockCanvasCursor 负责）。</summary>
    private void UpdateIbeamScope()
    {
        if (_canvasEditNodeId == null) { SetIbeamInstalled(false); return; }
        if (AnyCanvasGestureActive) return;
        // 按住左键（拖选文本）时恒为工字；否则看指针是否落在文本区（落在控件命中带 → 卸下，让 resize/箭头正常）。
        bool pressed = CursorInterop.AnyMouseButtonDown();
        bool hoverNull = ResolveEditHoverCursor(_canvasPointerPos) == null;
        SetIbeamInstalled(_canvasPointerOnEditCard && (pressed || !_canvasPointerValid || hoverNull));
    }

    /// <summary>安装/卸下工字作用域（仅状态变化才动资源，幂等，对齐画笔 _ringInstalled）： 安装 = 把系统箭头槽替换为工字副本（keepIBeam 只动箭头槽、不动 I 形槽）。</summary>
    private void SetIbeamInstalled(bool on)
    {
        if (on == _ibeamInstalled) return;
        _ibeamInstalled = on;
        try
        {
            if (on)
                CursorInterop.OverrideSystemCursorTo(CursorInterop.IdcIbeam, keepIBeam: true);
            else
            {
                _canvasCursorLocked = false;   // Restore 一次性还原全部槽 ⇒ 锁定/作用域标志同步复位（不变量：Restore 后必为未锁/未装）
                _handInstalled = false;
                CursorInterop.RestoreSystemCursors();
            }
        }
        catch { }
    }

    /// 启动白板连线"手型"光标作用域（对齐画笔 EnterPenRingMode / 编辑态 EnterIbeamScope）：建 25ms 轮询并立刻评估一次。
    private void EnterHandScope()
    {
        if (_handScopeTimer == null)
        {
            _handScopeTimer = DispatcherQueue.CreateTimer();
            _handScopeTimer.Interval = TimeSpan.FromMilliseconds(IbeamScopeIntervalMs);
            _handScopeTimer.Tick += HandScopeTick;
        }
        try { _handScopeTimer.Stop(); _handScopeTimer.Start(); } catch { }
        UpdateHandScope();
    }

    /// 停手型作用域轮询并卸载（对齐 ExitIbeamScope）。
    private void ExitHandScope()
    {
        try { _handScopeTimer?.Stop(); } catch { }
        SetHandInstalled(false);
    }

    private void HandScopeTick(object? sender, object e) => UpdateHandScope();

    /// <summary>作用域判定（每 25ms 一拍，对齐画笔 UpdateRingScope / 编辑态 UpdateIbeamScope）： 白板交互态 + 指针在画布上 + 非手势 ⇒ 命中连线或卡片四边中点圆形拉线控件则装手型、否则卸下；其余一律卸下。 编辑态下只在连线/拉线控件上装手型（文本区命中带为 false，不装），卡片文本区仍走编辑框 I 形，互不覆盖。</summary>
    private void UpdateHandScope()
    {
        // 手势进行中：手型作用域不介入（手势光标由 LockCanvasCursor 负责，避免覆盖）。
        if (AnyCanvasGestureActive) return;
        if (!_canvasInputActive || !_canvasPointerValid) { SetHandInstalled(false); return; }
        // 命中任意连线，或命中卡片四边中点圆形拉线控件（命中带均与绘制同源）⇒ 手型出现 ⟺ 点击必选中/拉出连线（对齐 Obsidian）。
        bool overLine = TryHitCanvasEdge(_canvasPointerPos, out _)
            || TryHitCanvasHandle(_canvasPointerPos, out _, out _);
        SetHandInstalled(overLine);
    }

    /// <summary>安装/卸下白板连线"手型"作用域（仅状态变化才动资源，幂等，对齐画笔 _ringInstalled / 编辑态 _ibeamInstalled）： 安装 = 把系统箭头槽替换为手型(IDC_HAND)副本（keepIBeam 只动箭头槽、不动 I 形槽，编辑文本仍走工字）； 卸下 = 还原全部系统光标槽。不变量：还原后任何光标锁定/作用域标志都必须复位。</summary>
    private void SetHandInstalled(bool on)
    {
        if (on == _handInstalled) return;
        _handInstalled = on;
        try
        {
            if (on)
                CursorInterop.OverrideSystemCursorTo(CursorInterop.IdcHand, keepIBeam: true);
            else
            {
                _canvasCursorLocked = false;   // Restore 一次性还原全部槽 ⇒ 锁定/作用域标志同步复位（不变量）
                _ibeamInstalled = false;
                CursorInterop.RestoreSystemCursors();
            }
        }
        catch { }
    }

    /// <summary>视口点是否命中【当前编辑卡】的某个边框控件（滚动条 / 四角缩放 / 四边中点连线 / 四边中段缩放）。 判据与 的四条分支同源，故"编辑框内通道"与"编辑框外 Viewer 通道" 对同一控件的认定完全一致。</summary>
    private bool PressHitsEditedCardControl(Point vp)
    {
        string editId = _canvasEditNodeId!;
        if (editId == null) return false;
        if (TryHitCanvasScrollBar(vp, out var sb) && sb != null &&
            string.Equals(sb.Id, editId, StringComparison.Ordinal)) return true;
        if (TryHitCanvasCorner(vp, out var cn, out _) &&
            string.Equals(cn, editId, StringComparison.Ordinal)) return true;
        if (TryHitCanvasHandle(vp, out var hn, out _) &&
            string.Equals(hn, editId, StringComparison.Ordinal)) return true;
        if (TryHitCanvasEdge(vp, out var en, out _) &&
            string.Equals(en, editId, StringComparison.Ordinal)) return true;
        return false;
    }

    private bool IsPointOnCanvasEditCard(Point vp)
    {
        try
        {
            if (CanvasEditBox == null || CanvasEditBox.Visibility != Visibility.Visible) return false;
            var r = CanvasEditBox.TransformToVisual(Viewer)
                .TransformBounds(new Rect(0, 0, CanvasEditBox.ActualWidth, CanvasEditBox.ActualHeight));
            return vp.X >= r.X && vp.X <= r.X + r.Width && vp.Y >= r.Y && vp.Y <= r.Y + r.Height;
        }
        catch { return false; }
    }

    /// <summary>缩放拖拽中：对角固定，移动角跟随指针；按最小尺寸与页边界 clamp（页局部空间）。 页局部坐标系与卡片存储坐标同源，旋转态下指针换算已含旋转逆变换，故此处无需感知旋转。</summary>
    private void UpdateCanvasResize(Point vp)
    {
        var node = _canvasResizeNode;
        if (node == null || _canvasResizePage < 0 || _canvasResizePage >= _pages.Count) return;
        if (!CanvasViewportToLockedPage(_canvasResizePage, vp, out double px, out double py)) return;

        var page = _pages[_canvasResizePage];
        double pw = page.WidthPoints, ph = page.HeightPoints;

        // ── 单轴（边中段）缩放：只动一条边，对侧固定；其余两条边保持原值 ──
        if (_canvasResizeMode == "edge")
        {
            double ox = _canvasResizeOrigX, oy = _canvasResizeOrigY,
                   ow = _canvasResizeOrigW, oh = _canvasResizeOrigH;
            double oR = ox + ow, oB = oy + oh;
            double eLeft = ox, eTop = oy, eRight = oR, eBottom = oB;
            switch (_canvasResizeEdge)
            {
                case "left":   eLeft   = Math.Clamp(px, 0, oR - CanvasMinNodeWidth);    break;
                case "right":  eRight  = Math.Clamp(px, ox + CanvasMinNodeWidth, pw);    break;
                case "top":    eTop    = Math.Clamp(py, 0, oB - CanvasMinNodeHeight);   break;
                default:       eBottom = Math.Clamp(py, oy + CanvasMinNodeHeight, ph);   break;
            }
            if (eRight - eLeft < CanvasMinNodeWidth || eBottom - eTop < CanvasMinNodeHeight) return;
            CanvasNodeResized?.Invoke(this, new CanvasNodeEventArgs
            {
                NodeId = node.Id, PageIndex = _canvasResizePage,
                X = eLeft, Y = eTop, Width = eRight - eLeft, Height = eBottom - eTop
            });
            SyncCanvasOverlayNow();
            return;
        }

        string c = _canvasResizeCorner;
        bool moveLeft = c == "tl" || c == "bl";
        bool moveTop = c == "tl" || c == "tr";

        double fixedX = moveLeft ? _canvasResizeOrigX + _canvasResizeOrigW : _canvasResizeOrigX;
        double fixedY = moveTop ? _canvasResizeOrigY + _canvasResizeOrigH : _canvasResizeOrigY;

        double moveX = Math.Clamp(px, 0, pw);
        double moveY = Math.Clamp(py, 0, ph);
        if (moveLeft) moveX = Math.Clamp(moveX, 0, fixedX - CanvasMinNodeWidth);
        else moveX = Math.Clamp(moveX, fixedX + CanvasMinNodeWidth, pw);
        if (moveTop) moveY = Math.Clamp(moveY, 0, fixedY - CanvasMinNodeHeight);
        else moveY = Math.Clamp(moveY, fixedY + CanvasMinNodeHeight, ph);

        double left = Math.Min(fixedX, moveX), right = Math.Max(fixedX, moveX);
        double top = Math.Min(fixedY, moveY), bottom = Math.Max(fixedY, moveY);
        if (right - left < CanvasMinNodeWidth) return;
        if (bottom - top < CanvasMinNodeHeight) return;

        CanvasNodeResized?.Invoke(this, new CanvasNodeEventArgs
        {
            NodeId = node.Id, PageIndex = _canvasResizePage,
            X = left, Y = top, Width = right - left, Height = bottom - top
        });
        SyncCanvasOverlayNow();
    }

    private void CancelCanvasResize()
    {
        _isCanvasResizing = false;
        _canvasResizeNode = null;
        _canvasResizePage = -1;
        _canvasResizeMode = "corner";
        _canvasResizeCorner = "";
        _canvasResizeEdge = "";
        UnlockCanvasCursor();   // 缩放结束 ⇒ 解锁光标（恢复框架默认）
    }

    private bool CanvasViewportToPage(Point vp, out int pageIndex, out double px, out double py)
    {
        pageIndex = -1; px = 0; py = 0;
        var pages = _pages;
        if (pages.Count == 0) return false;
        GetViewState(out double z, out double hOff, out double vOff);
        if (z <= 0) return false;
        double docX, docY;
        CanvasDocToLocal(vp, z, hOff, vOff, out docX, out docY);
        foreach (var p in pages)
        {
            if (docX >= p.LayoutRect.Left && docX <= p.LayoutRect.Right &&
                docY >= p.LayoutRect.Top && docY <= p.LayoutRect.Bottom)
            {
                pageIndex = p.Index;
                PageLocalFromDoc(p, docX, docY, out px, out py);
                return true;
            }
        }
        return false;
    }

    private bool CanvasViewportToLockedPage(int pageIndex, Point vp, out double px, out double py)
    {
        px = 0; py = 0;
        var pages = _pages;
        if (pageIndex < 0 || pageIndex >= pages.Count) return false;
        GetViewState(out double z, out double hOff, out double vOff);
        if (z <= 0) return false;
        CanvasDocToLocal(vp, z, hOff, vOff, out double docX, out double docY);
        PageLocalFromDoc(pages[pageIndex], docX, docY, out px, out py);
        return true;
    }

    /// <summary>视口坐标 → 文档坐标（DIP，缩放=1）；扣居中补偿，与 ComputeVisiblePages 同一套算法。</summary>
    private void CanvasDocToLocal(Point vp, double z, double hOff, double vOff,
        out double docX, out double docY)
    {
        double vpW = _vpW, vpH = _vpH;
        double extentW = Spacer.Width * z, extentH = Spacer.Height * z;
        double centerX = extentW < vpW ? (vpW - extentW) / 2.0 : 0;
        double centerY = extentH < vpH ? (vpH - extentH) / 2.0 : 0;
        docX = (vp.X - centerX + hOff) / z;
        docY = (vp.Y - centerY + vOff) / z;
    }

    private void HandleCanvasDoubleTap(Point vp)
    {
        if (TryHitCanvasNode(vp, out var hit, out _, out _, out _))
        {
            // 已在编辑该卡 →【什么都不做】：RichEditBox 原生双击选词是权威（与系统文本框一致）。
            if (hit == null || hit.Id == _canvasEditNodeId) return;
            BeginCanvasEdit(hit.Id, vp);
            return;
        }
        if (!CanvasViewportToPage(vp, out int pageIdx, out double px, out double py)) return;
        var page = _pages[pageIdx];
        // 落点以鼠标位置为卡片【中心】（对齐 Obsidian），再 clamp 到页内。
        double w = CanvasNode.DefaultWidth, h = CanvasNode.DefaultHeight;
        double x = Math.Clamp(px - w / 2.0, 0, Math.Max(0, page.WidthPoints - w));
        double y = Math.Clamp(py - h / 2.0, 0, Math.Max(0, page.HeightPoints - h));
        CanvasNodeCreateRequested?.Invoke(this, new CanvasNodeEventArgs
        {
            PageIndex = pageIdx, X = x, Y = y
        });
    }

    private void CanvasEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCanvasNodeId != null) BeginCanvasEdit(_selectedCanvasNodeId);
    }

    private void CanvasDeleteButton_Click(object sender, RoutedEventArgs e)
        => DeleteSelectedCanvasNode();

    private void CanvasEdgeDeleteButton_Click(object sender, RoutedEventArgs e)
        => DeleteSelectedCanvasEdge();

    /// <summary>箭头样式按钮：切换二级箭头面板的展开 / 收起（与颜色面板互斥 —— 同一时刻只开一个）。</summary>
    private void CanvasEdgeArrowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCanvasEdgeId == null) return;
        double oldLeft = _canvasEdgeToolbarLeft;
        if (_canvasEdgeArrowPickerShown) HideCanvasEdgeArrowPicker();
        else { HideCanvasEdgeColorPicker(); ShowCanvasEdgeArrowPicker(); }
        SyncCanvasOverlayNow();   // 展开态按真实宽度做右缘钳制（正常不位移、越界才最小左移）
        ClearCanvasEdgeToolbarHoverIfMoved(oldLeft, sender as Button);
        RequestCapsuleHoverSyncAfterLayout();
    }

    private void CanvasEdgeColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCanvasEdgeId == null) return;
        double oldLeft = _canvasEdgeToolbarLeft;
        if (_canvasEdgeColorPickerShown) HideCanvasEdgeColorPicker();
        else { HideCanvasEdgeArrowPicker(); ShowCanvasEdgeColorPicker(); }
        SyncCanvasOverlayNow();
        ClearCanvasEdgeToolbarHoverIfMoved(oldLeft, sender as Button);
        RequestCapsuleHoverSyncAfterLayout();
    }

    private void CanvasEdgeArrowOption_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCanvasEdgeId == null) return;
        string style = (sender as FrameworkElement)?.Tag as string ?? "single";
        CanvasEdgeStyleChanged?.Invoke(this, new CanvasNodeEventArgs
        {
            EdgeId = _selectedCanvasEdgeId,
            ArrowStyle = style
        });
        RefreshEdgeArrowButtons();
        double oldLeft = _canvasEdgeToolbarLeft;
        HideCanvasEdgeArrowPicker();
        SyncCanvasOverlayNow();   // 收起回窄态居中（若之前被钳制左移过，这里复原）
        ClearCanvasEdgeToolbarHoverIfMoved(oldLeft, sender as Button);
        RequestCapsuleHoverSyncAfterLayout();
        InvalidateComposite();
    }

    /// <summary>颜色圆点：Tag = "#RRGGBB"，7 色一视同仁（首位灰也只是其中一色），选完即收起面板。</summary>
    private void CanvasEdgeColorDot_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCanvasEdgeId == null) return;
        string hex = (sender as FrameworkElement)?.Tag as string ?? "";
        CanvasEdgeStyleChanged?.Invoke(this, new CanvasNodeEventArgs
        {
            EdgeId = _selectedCanvasEdgeId,
            EdgeColor = hex
        });
        double oldLeft = _canvasEdgeToolbarLeft;
        HideCanvasEdgeColorPicker();
        SyncCanvasOverlayNow();
        // ⚠ 传入的是圆点：圆点不归 ButtonVisualOverride 接管 ⇒ 不会触发任何冷处理（与卡片胶囊同理）。
        ClearCanvasEdgeToolbarHoverIfMoved(oldLeft, sender as Button);
        RequestCapsuleHoverSyncAfterLayout();
        InvalidateComposite();
    }

    private void HideCanvasEdgePickers()
    {
        HideCanvasEdgeArrowPicker();
        HideCanvasEdgeColorPicker();
    }

    private void ShowCanvasEdgeArrowPicker()
    {
        if (CanvasEdgeArrowPanel == null || CanvasEdgeToolbar == null) return;
        CanvasEdgeArrowPanel.Visibility = Visibility.Visible;
        _canvasEdgeArrowPickerShown = true;
        RefreshEdgeArrowButtons();
        MeasureCanvasEdgeToolbarExpanded();
    }

    private void HideCanvasEdgeArrowPicker()
    {
        if (CanvasEdgeArrowPanel == null) return;
        CanvasEdgeArrowPanel.Visibility = Visibility.Collapsed;
        _canvasEdgeArrowPickerShown = false;
    }

    private void ShowCanvasEdgeColorPicker()
    {
        if (CanvasEdgeColorDotPanel == null || CanvasEdgeToolbar == null) return;
        CanvasEdgeColorDotPanel.Visibility = Visibility.Visible;
        _canvasEdgeColorPickerShown = true;
        SyncCanvasEdgeColorDotSelection();
        MeasureCanvasEdgeToolbarExpanded();
    }

    private void HideCanvasEdgeColorPicker()
    {
        if (CanvasEdgeColorDotPanel == null) return;
        CanvasEdgeColorDotPanel.Visibility = Visibility.Collapsed;
        _canvasEdgeColorPickerShown = false;
    }

    /// <summary>量一次【展开态真实宽度】缓存起来，供定位时按真实宽度做右缘钳制（只在越界时左移，正常不位移）。 ⚠ DesiredSize 含 Margin：测前把 Margin 归零，否则上次定位的 left/top 会被算进宽度、测出畸大值。</summary>
    private void MeasureCanvasEdgeToolbarExpanded()
    {
        if (CanvasEdgeToolbar == null) return;
        var saved = CanvasEdgeToolbar.Margin;
        CanvasEdgeToolbar.Margin = new Thickness(0);
        CanvasEdgeToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _canvasEdgeToolbarExpandedW = CanvasEdgeToolbar.DesiredSize.Width;
        CanvasEdgeToolbar.Margin = saved;
    }

    /// <summary>展开时按当前选中连线的颜色高亮对应圆点。7 色一视同仁按 Tag 比较； 连线未着色（null/空）以默认灰呈现，故高亮首位灰圆点。</summary>
    private void SyncCanvasEdgeColorDotSelection()
    {
        if (CanvasEdgeColorDotPanel == null) return;
        string current = CanvasCardDefaultGrayHex;
        var id = _selectedCanvasEdgeId;
        if (id != null)
        {
            lock (_canvasLock)
            {
                foreach (var e in _canvasEdges)
                    if (string.Equals(e.Id, id, StringComparison.Ordinal))
                    { current = string.IsNullOrEmpty(e.Color) ? CanvasCardDefaultGrayHex : e.Color; break; }
            }
        }
        Button? match = null;
        foreach (var child in CanvasEdgeColorDotPanel.Children)
        {
            if (child is Button b && b.Tag?.ToString() is string tag &&
                string.Equals(tag, current, StringComparison.OrdinalIgnoreCase))
            { match = b; break; }
        }
        HighlightCanvasColorDot(match, edge: true);
    }

    private void CanvasColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCanvasNodeId == null) return;
        double oldLeft = _canvasToolbarLeft;
        if (_canvasColorPickerShown) HideCanvasColorPicker();
        else ShowCanvasColorPicker();
        SyncCanvasOverlayNow();   // 展开态按真实宽度做右缘钳制（正常不位移、越界才最小左移）
        ClearCanvasToolbarHoverIfMoved(oldLeft, sender as Button);
        RequestCapsuleHoverSyncAfterLayout();   // 先挡迟到事件，再按实时指针权威补判一次
    }

    private void CanvasColorDot_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCanvasNodeId == null) return;
        string hex = (sender as FrameworkElement)?.Tag as string ?? "";
        CanvasNodeColorChanged?.Invoke(this, new CanvasNodeEventArgs
        {
            NodeId = _selectedCanvasNodeId,
            NodeColor = hex
        });
        double oldLeft = _canvasToolbarLeft;
        HideCanvasColorPicker();
        SyncCanvasOverlayNow();   // 收起回窄态居中（若之前被钳制左移过，这里复原）
        // ⚠ 传入的是圆点：圆点不归 ButtonVisualOverride 接管 ⇒ 不会触发任何冷处理，
        ClearCanvasToolbarHoverIfMoved(oldLeft, sender as Button);
        RequestCapsuleHoverSyncAfterLayout();
        InvalidateComposite();
    }

    /// <summary>展开二级颜色胶囊：颜色按钮保留（作二次点击收起开关），7 色圆点在它右侧展开。 量一次【展开态真实宽度】缓存起来，供定位时按真实宽度做右缘钳制（只在越界时左移，正常不位移）。</summary>
    private void ShowCanvasColorPicker()
    {
        if (CanvasColorDotPanel == null || CanvasCardToolbar == null) return;
        CanvasColorDotPanel.Visibility = Visibility.Visible;
        _canvasColorPickerShown = true;
        SyncCanvasColorDotSelection();
        var saved = CanvasCardToolbar.Margin;
        CanvasCardToolbar.Margin = new Thickness(0);
        CanvasCardToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _canvasToolbarExpandedW = CanvasCardToolbar.DesiredSize.Width;
        CanvasCardToolbar.Margin = saved;
    }

    private void HideCanvasColorPicker()
    {
        if (CanvasColorDotPanel == null) return;
        CanvasColorDotPanel.Visibility = Visibility.Collapsed;
        _canvasColorPickerShown = false;
    }

    /// 展开/收起（含钳制）导致胶囊实际位移后，对刚才被按下的那颗按钮开启 hover 冷处理 —— 挡住 ButtonBase 松手约 380ms 后补发的那次"圆谎" PointerEntered（带松手…
    /// 挡住 ButtonBase 松手约 380ms 后补发的那次"圆谎" PointerEntered（带松手时的旧坐标），否则会把已滑出
    private void ClearCanvasToolbarHoverIfMoved(double oldLeft, Button? clicked)
    {
        if (Math.Abs(_canvasToolbarLeft - oldLeft) > 0.5)
            ButtonVisualOverride.SuppressHoverFor(clicked);
    }

    private void ClearCanvasEdgeToolbarHoverIfMoved(double oldLeft, Button? clicked)
    {
        if (Math.Abs(_canvasEdgeToolbarLeft - oldLeft) > 0.5)
            ButtonVisualOverride.SuppressHoverFor(clicked);
    }

    // ---- 胶囊位移后按"最后一次指针位置"补判 hover ----

    private Point? _canvasChromePointer;
    private bool _capsuleHoverSyncArmed;   // 已挂上"布局落定后补判 hover"的双通道回调（见 RequestCapsuleHoverSyncAfterLayout）

    private void CanvasCardToolbar_PointerMoved(object sender, PointerRoutedEventArgs e)
        => RecordCanvasChromePointer(e);

    private void CanvasCardToolbar_PointerPressed(object sender, PointerRoutedEventArgs e)
        => RecordCanvasChromePointer(e);

    private void RecordCanvasChromePointer(PointerRoutedEventArgs e)
    {
        if (CanvasOverlay == null) return;
        try { _canvasChromePointer = e.GetCurrentPoint(CanvasOverlay).Position; }
        catch { /* 坐标系不可达（元素脱树）时保持旧值 */ }
    }

    /// <summary>请求在【本次布局落定后】补判一次胶囊 3 颗按钮的 hover（一次性，触发即退订）。 双保险：① LayoutUpdated（布局落定即触发，同帧内）；</summary>
    private void RequestCapsuleHoverSyncAfterLayout()
    {
        if (CanvasCardToolbar == null || _canvasChromePointer == null) return;
        if (!_capsuleHoverSyncArmed)
        {
            _capsuleHoverSyncArmed = true;
            CanvasCardToolbar.LayoutUpdated += CanvasCardToolbar_LayoutUpdatedOnce;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += CompositionTarget_RenderingOnce;
        }
        CanvasCardToolbar.InvalidateArrange();   // 促成一轮布局，让 ① 尽量同帧触发
    }

    private void CanvasCardToolbar_LayoutUpdatedOnce(object? sender, object e) => FinishCapsuleHoverSync();

    private void CompositionTarget_RenderingOnce(object? sender, object e) => FinishCapsuleHoverSync();

    private void FinishCapsuleHoverSync()
    {
        if (!_capsuleHoverSyncArmed) return;
        _capsuleHoverSyncArmed = false;
        if (CanvasCardToolbar != null)
            CanvasCardToolbar.LayoutUpdated -= CanvasCardToolbar_LayoutUpdatedOnce;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= CompositionTarget_RenderingOnce;
        ApplyCapsuleHoverFromRecordedPointer();
    }

    private void ApplyCapsuleHoverFromRecordedPointer()
    {
        if (_canvasChromePointer is not Point p || CanvasOverlay == null) return;
        ApplyCapsuleHoverOne(CanvasEditButton, p);
        ApplyCapsuleHoverOne(CanvasColorButton, p);
        ApplyCapsuleHoverOne(CanvasDeleteButton, p);
        // 连线胶囊（同一套补判）：主按钮 3 颗 + 二级箭头三选。收在折叠面板里的按钮 ActualWidth=0 ⇒ 恒判"不在内"，无副作用。
        ApplyCapsuleHoverOne(CanvasEdgeArrowButton, p);
        ApplyCapsuleHoverOne(CanvasEdgeColorButton, p);
        ApplyCapsuleHoverOne(CanvasEdgeDeleteButton, p);
        ApplyCapsuleHoverOne(CanvasEdgeArrowNoneBtn, p);
        ApplyCapsuleHoverOne(CanvasEdgeArrowSingleBtn, p);
        ApplyCapsuleHoverOne(CanvasEdgeArrowBothBtn, p);
    }

    private void ApplyCapsuleHoverOne(Button? button, Point p)
    {
        if (button == null) return;
        bool inside = false;
        if (button.Visibility == Visibility.Visible && button.ActualWidth > 0 && button.ActualHeight > 0)
        {
            try
            {
                var origin = button.TransformToVisual(CanvasOverlay).TransformPoint(new Point(0, 0));
                inside = p.X >= origin.X && p.X < origin.X + button.ActualWidth
                      && p.Y >= origin.Y && p.Y < origin.Y + button.ActualHeight;
            }
            catch { inside = false; }
        }
        ButtonVisualOverride.ForceHover(button, inside);
    }

    private void SetCanvasColorDotSize(Button b, bool enlarged)
    {
        b.Width = b.Height = enlarged ? CanvasColorDotEnlargedSize : CanvasColorDotBaseSize;
        b.CornerRadius = new CornerRadius(enlarged ? CanvasColorDotEnlargedRadius : CanvasColorDotBaseRadius);
        double inset = enlarged ? CanvasColorDotEnlargedInset : 0.0;
        b.Margin = new Thickness(-inset);
    }

    /// <summary>圆点属于哪一张面板（卡片 / 连线）⇒ 取它那张面板的"当前选中圆点"。 两副圆点共用同一套悬浮/离开处理器，靠父元素区分，避免为连线再抄一份。</summary>
    private Button? SelectedDotOf(Button b)
        => ReferenceEquals(b.Parent, CanvasEdgeColorDotPanel) ? _canvasEdgeColorSelectedDot : _canvasColorSelectedDot;

    private void RefreshCanvasColorDotState(Button b)
        => SetCanvasColorDotSize(b, b == SelectedDotOf(b));

    private void CanvasColorDot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button b) SetCanvasColorDotSize(b, true);
    }

    private void CanvasColorDot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button b) RefreshCanvasColorDotState(b);
    }

    /// <summary>把"当前已应用颜色"对应的圆点置为选中(放大)态。7 色一视同仁：只放大、不改底色。 = true 时作用于连线胶囊的圆点面板，否则作用于卡片胶囊的圆点面板。</summary>
    private void HighlightCanvasColorDot(Button? selected, bool edge = false)
    {
        var panel = edge ? CanvasEdgeColorDotPanel : CanvasColorDotPanel;
        if (edge) _canvasEdgeColorSelectedDot = selected; else _canvasColorSelectedDot = selected;
        if (panel == null) return;
        foreach (var child in panel.Children)
        {
            if (child is Button b) RefreshCanvasColorDotState(b);
        }
    }

    /// <summary>展开时按当前选中卡片的颜色高亮对应圆点。7 色一视同仁按 Tag 比较； 未着色（空）等价于默认灰 #8A8F98，故默认卡片高亮首位灰圆点。</summary>
    private void SyncCanvasColorDotSelection()
    {
        if (CanvasColorDotPanel == null) return;
        string current = CanvasCardDefaultGrayHex;
        if (_selectedCanvasNodeId != null)
        {
            lock (_canvasLock)
            {
                foreach (var n in _canvasNodes)
                    if (string.Equals(n.Id, _selectedCanvasNodeId, StringComparison.Ordinal))
                    { current = string.IsNullOrEmpty(n.Color) ? CanvasCardDefaultGrayHex : n.Color; break; }
            }
        }
        Button? match = null;
        foreach (var child in CanvasColorDotPanel.Children)
        {
            if (child is Button b && b.Tag?.ToString() is string tag &&
                string.Equals(tag, current, StringComparison.OrdinalIgnoreCase))
            { match = b; break; }
        }
        HighlightCanvasColorDot(match);
    }

    /// <summary>给【两副】7 色圆点（卡片胶囊 / 连线胶囊）逐个挂悬浮/离开事件（悬浮预览放大用）。 绝不在此写 element.Resources（运行时写 Resources 在主题切换会闪退； 底色档已在 XAML 解析期逐按钮声明）。</summary>
    private void AttachCanvasColorDotHover()
    {
        AttachCanvasColorDotHoverIn(CanvasColorDotPanel);
        AttachCanvasColorDotHoverIn(CanvasEdgeColorDotPanel);
    }

    private void AttachCanvasColorDotHoverIn(StackPanel? panel)
    {
        if (panel == null) return;
        foreach (var child in panel.Children)
        {
            if (child is Button b)
            {
                b.PointerEntered += CanvasColorDot_PointerEntered;
                b.PointerExited += CanvasColorDot_PointerExited;
            }
        }
    }

    /// <summary>按选中连线的实际样式刷新箭头相关显示：① 胶囊主按钮的字形 = 当前样式（收起态也能看出当前值）； ② 展开面板里三项的强调态（当前 1.0 / 其余 0.4）。</summary>
    private void RefreshEdgeArrowButtons()
    {
        if (CanvasEdgeToolbar == null) return;
        // 每次刷新都按当前 DPI 重算描边宽（幂等、同值不触发失效）：见方法注释。
        ApplyCanvasEdgeArrowGlyphStrokeThickness();
        string style = "single";
        var id = _selectedCanvasEdgeId;
        if (id != null)
        {
            CanvasEdge[] edges;
            lock (_canvasLock) edges = _canvasEdges;
            foreach (var e in edges)
                if (string.Equals(e.Id, id, StringComparison.Ordinal)) { style = EdgeArrowStyleOf(e); break; }
        }
        if (CanvasEdgeArrowNoneBtn != null) CanvasEdgeArrowNoneBtn.Opacity = style == "none" ? 1.0 : 0.4;
        if (CanvasEdgeArrowSingleBtn != null) CanvasEdgeArrowSingleBtn.Opacity = style == "single" ? 1.0 : 0.4;
        if (CanvasEdgeArrowBothBtn != null) CanvasEdgeArrowBothBtn.Opacity = style == "both" ? 1.0 : 0.4;
        SetCanvasEdgeArrowGlyph(style);
    }

    /// <summary>按当前箭头样式切换主按钮三态符号（none=直线 / single=右箭头 / both=双向箭头，均自绘 Path 描边，照抄 -/+ 方法）。 三态互斥：只显示当前样式对应的那个，避免叠加。</summary>
    private void SetCanvasEdgeArrowGlyph(string style)
    {
        if (CanvasEdgeArrowNoneGlyph != null)
            CanvasEdgeArrowNoneGlyph.Visibility = style == "none" ? Visibility.Visible : Visibility.Collapsed;
        if (CanvasEdgeArrowSingleGlyph != null)
            CanvasEdgeArrowSingleGlyph.Visibility = style == "single" ? Visibility.Visible : Visibility.Collapsed;
        if (CanvasEdgeArrowBothGlyph != null)
            CanvasEdgeArrowBothGlyph.Visibility = style == "both" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// 连线箭头三态符号的描边宽：照抄缩放胶囊 -/+ 的「恒定 2 物理像素」规则 （StrokeThickness = 2 / RasterizationScale，
    private void ApplyCanvasEdgeArrowGlyphStrokeThickness()
    {
        double scale = 1.0;
        try { scale = XamlRoot?.RasterizationScale ?? 1.0; } catch { }
        if (!(scale > 0)) scale = 1.0;
        double t = 2.0 / scale;

        if (CanvasEdgeArrowNoneGlyph != null) CanvasEdgeArrowNoneGlyph.StrokeThickness = t;
        if (CanvasEdgeArrowSingleGlyph != null) CanvasEdgeArrowSingleGlyph.StrokeThickness = t;
        if (CanvasEdgeArrowBothGlyph != null) CanvasEdgeArrowBothGlyph.StrokeThickness = t;

        // 三选按钮的内容就是符号 Path 本身（XAML 里未命名）⇒ 直接从 Content 取。
        if (CanvasEdgeArrowNoneBtn?.Content is Microsoft.UI.Xaml.Shapes.Path pNone) pNone.StrokeThickness = t;
        if (CanvasEdgeArrowSingleBtn?.Content is Microsoft.UI.Xaml.Shapes.Path pSingle) pSingle.StrokeThickness = t;
        if (CanvasEdgeArrowBothBtn?.Content is Microsoft.UI.Xaml.Shapes.Path pBoth) pBoth.StrokeThickness = t;
    }

    private static string EdgeArrowStyleOf(CanvasEdge e)
    {
        bool to = IsArrowEnd(e.ToEnd);
        bool from = IsArrowEnd(e.FromEnd);
        if (to && from) return "both";
        if (to || from) return "single";
        return "none";
    }

    private void CanvasEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // 编辑态手势（拖滚动条 / 拖角缩放 / 拖边连线）期间系统会把焦点从 RichEditBox 夺走：这**不算**"退出编辑"。
        // ⚠ 必须覆盖【全部】手势 —— 否则缩放/连线中途 `EndCanvasEdit` 会清掉手势状态、并折叠编辑框导致手势抖动。
        if (AnyCanvasGestureActive || _canvasEditControlGesture || _canvasEditMenuClickPending)
        {
            // 焦点被夺走会让插入光标停止闪烁（DrawCanvasEditCaret 以 FocusState 为前提）。用法是"控件交互期间
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_canvasEditNodeId == null) return;
                if (CanvasEditBox.FocusState == FocusState.Unfocused)
                    CanvasEditBox.Focus(FocusState.Programmatic);
            });
            return;
        }
        EndCanvasEdit(commit: true);
    }

    /// <summary>编辑期间拦截 Ctrl+Z / Ctrl+Y，交给 MainPage 的统一撤销栈（与画笔/白板操作共用同一历史）。 必须用 PreviewKeyDown（隧道事件）：RichEditBox 在 KeyDown 的类处理里就吃掉 Ctrl+Z（原生撤销） 并把事件标记为 Hand…</summary>
    private void CanvasEditBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 键盘请求文本菜单（Shift+F10 / Application 键）：先记一笔放行 —— 紧随其后的 ContextMenuOpening
        if (IsCanvasEditMenuKey(e)) _canvasEditMenuKeyTick = System.Diagnostics.Stopwatch.GetTimestamp();
        bool ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                     & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (!ctrl) return;
        if (e.Key == VirtualKey.Z)
        {
            e.Handled = true;
            // 先把编辑框"最后一段尚未入史"的文字提交为一步，再撤销 —— 等价于"撤回到上一段"；
            FlushCanvasEditText();
            CanvasUndoRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (e.Key == VirtualKey.Y)
        {
            e.Handled = true;
            CanvasRedoRequested?.Invoke(this, EventArgs.Empty);
        }
        // 行内 Markdown 快捷键（全部 Ctrl + 单字母，且尽量聚在 B/I 一带）。
        //   Ctrl+B 加粗 / Ctrl+I 斜体 / Ctrl+U 删除线 / Ctrl+H 高亮 / Ctrl+J 行内代码。
        // 必须拦在 PreviewKeyDown：RichEditBox 虽已 DisabledFormattingAccelerators="All"，但让按键落到
        else if (e.Key == VirtualKey.B) { e.Handled = true; ApplyCanvasMarkdownShortcut("**"); }
        else if (e.Key == VirtualKey.I) { e.Handled = true; ApplyCanvasMarkdownShortcut("*"); }
        else if (e.Key == VirtualKey.U) { e.Handled = true; ApplyCanvasMarkdownShortcut("~~"); }
        else if (e.Key == VirtualKey.H) { e.Handled = true; ApplyCanvasMarkdownShortcut("=="); }
        else if (e.Key == VirtualKey.J) { e.Handled = true; ApplyCanvasMarkdownShortcut("`"); }
        // Ctrl+A：全选照旧交给控件（不拦），但它是【整段设定】的选区 —— 活动端读不出来，
        // 与双击选词/三击选段同一类，必须排队补记，否则差分推断会把光标画到左端。
        else if (e.Key == VirtualKey.A) DispatcherQueue.TryEnqueue(PinCanvasEditWholeSelectionActive);
    }

    /// <summary>编辑期间必须【就地终结】的光标导航键。RichEditBox 移动完光标后**不**把事件标为 Handled， 于是它继续冒泡到全局 KeyboardAccelerator（MainPage.</summary>
    private static bool IsCanvasEditCaretKey(VirtualKey key) =>
        key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down
            or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown;

    private void CanvasEditBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 文本菜单开着时：Esc 只收起菜单（不退出编辑）；按其他任何键都视为继续输入/移动光标，菜单一并收起。
        if (_canvasEditMenuOpen)
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                HideCanvasEditContextMenu();
                return;
            }
            HideCanvasEditContextMenu();
        }
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            EndCanvasEdit(commit: true);
            return;
        }
        // 光标导航键：光标已由 RichEditBox 处理完 ⇒ 把事件终结在本控件内，不让它冒泡成视口导航
        if (IsCanvasEditCaretKey(e.Key)) e.Handled = true;
        // 方向键/Home/End/PageUp/PageDown 移动光标 ⇒ Win2D 自绘光标需当帧刷新。
        ResetCaretBlink();
        ScreenCanvas.Invalidate();
    }

    private const int CanvasEditCommitDebounceMs = 400;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _canvasEditCommitTimer;
    /// <summary>已提交入史的文本基线（与它相同则不再产生历史条目）。</summary>
    private string _canvasLastCommittedText = "";

    private void CanvasEditBox_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_canvasEditNodeId == null) return;
        _canvasEditText = GetCanvasEditText();  // 刷新缓存（绘制/提交都读它，避免每帧去读 TOM 文档）
        // 文字变化 → 立刻刷新 Win2D 绘制（文字/光标/滚动条皆由 Win2D 画，当帧反映，不再慢半拍）。
        ResetCaretBlink();                     // 打字时光标保持亮，停手后才开始闪烁
        ScreenCanvas.Invalidate();
        // ⚠ 贴字符格式 / 同步上方留白都【不能】在这个通知里做：它们会改文档或改宿主布局，
        //    而输入法组字正发生在这条通知期间，改文档会把组合打断（漏字、退格删不掉、要退出编辑才看到结果）。
        ScheduleCanvasEditSync();
        ScheduleCanvasEditCommit();
    }

    private bool _canvasEditSyncPending;

    /// <summary>把"贴字符格式 + 同步编辑框上方留白"合并成一次延后执行（至多一次在途）：TextChanged 里只更新文本快照并请求重绘。</summary>
    private void ScheduleCanvasEditSync()
    {
        if (_canvasEditSyncPending) return;
        _canvasEditSyncPending = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _canvasEditSyncPending = false;
                if (_canvasEditNodeId == null) return;
                ApplyCanvasEditCharacterFormatting();
                SyncCanvasEditTopPad();
                ScreenCanvas.Invalidate();
            }))
            _canvasEditSyncPending = false;
    }

    /// <summary>程序化改文档（剪切/粘贴这类走 TOM 的路径）之后必须调用：RichEditBox 对 Selection.SetText 不保证触发 TextChanged，只 Invalidate 会拿旧快照重画 ⇒ 表现为"删掉/粘上的文字要退出编辑才生效"（用户报告）。</summary>
    private void RefreshCanvasEditAfterProgrammaticEdit()
    {
        if (_canvasEditNodeId == null) return;
        _canvasEditText = GetCanvasEditText();
        ApplyCanvasEditCharacterFormatting();
        SyncCanvasEditTopPad();
        ResetCaretBlink();
        ScreenCanvas.Invalidate();
        ScheduleCanvasEditCommit();
    }

    /// <summary>打字过程中不逐字符入史（否则 Ctrl+Z 变成逐字符退格）：停顿 400ms 视为一段输入结束， 提交为一步；连续输入仍合并为一步。这样"多次输入"才能分成多步撤销。</summary>
    private void ScheduleCanvasEditCommit()
    {
        _canvasEditCommitTimer ??= DispatcherQueue.CreateTimer();
        _canvasEditCommitTimer.Stop();
        _canvasEditCommitTimer.Interval = TimeSpan.FromMilliseconds(CanvasEditCommitDebounceMs);
        _canvasEditCommitTimer.IsRepeating = false;
        _canvasEditCommitTimer.Tick -= OnCanvasEditCommitTick;
        _canvasEditCommitTimer.Tick += OnCanvasEditCommitTick;
        _canvasEditCommitTimer.Start();
    }

    private void OnCanvasEditCommitTick(object? sender, object e) => FlushCanvasEditText();

    private void StopCanvasEditCommitTimer() => _canvasEditCommitTimer?.Stop();

    /// <summary>编辑框文本的缓存：RichEditBox 没有 Text 属性，每次读都要走 TOM 文档并分配字符串， 而留白/滚动条/提交都在高频路径上 ⇒ 统一缓存。所有写入路径（SetCanvasEditText / TextChanged）都会刷新它。</summary>
    private string _canvasEditText = "";
    /// <summary>已成功锁定的编辑态行距（磅）；-1 = 尚未锁定。与"当前目标值"不符时就重新锁一次 —— 目标值依赖阅读态那份 layout 的实测自然行高，会随卡片尺寸/字体就绪而变。</summary>
    private float _canvasEditLineSpacingSet = -1f;

    private const int CanvasCaretBlinkMs = 530;                 // 亮/灭各 530ms（与系统文本框节奏一致）
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _canvasCaretBlinkTimer;
    private bool _canvasCaretVisible = true;
    private static readonly Microsoft.UI.Input.InputCursor? CanvasArrowCursor = MakeCanvasCursor(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
    private static readonly Microsoft.UI.Input.InputCursor? CanvasResizeNwSeCursor = MakeCanvasCursor(Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast);
    private static readonly Microsoft.UI.Input.InputCursor? CanvasResizeNeSwCursor = MakeCanvasCursor(Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest);
    private static readonly Microsoft.UI.Input.InputCursor? CanvasResizeNsCursor = MakeCanvasCursor(Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth);
    private static readonly Microsoft.UI.Input.InputCursor? CanvasResizeWeCursor = MakeCanvasCursor(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
    // 编辑态文本区显式工字：不能依赖 RichEditBox 内部文本表面的隐式 I 形 —— 按下时它捕获指针，
    private static readonly Microsoft.UI.Input.InputCursor? CanvasIBeamCursor = MakeCanvasCursor(Microsoft.UI.Input.InputSystemCursorShape.IBeam);
    private static Microsoft.UI.Input.InputCursor? MakeCanvasCursor(Microsoft.UI.Input.InputSystemCursorShape shape)
    {
        try { return Microsoft.UI.Input.InputSystemCursor.Create(shape); }
        catch { return null; }   // 失败则退化为控件默认光标，绝不因静态初始化抛异常
    }
    private void StartCaretBlink()
    {
        if (_canvasCaretBlinkTimer == null)
            _canvasCaretBlinkTimer = DispatcherQueue.CreateTimer();
        _canvasCaretBlinkTimer.Interval = TimeSpan.FromMilliseconds(CanvasCaretBlinkMs);
        _canvasCaretBlinkTimer.IsRepeating = true;
        _canvasCaretBlinkTimer.Tick -= OnCaretBlinkTick;
        _canvasCaretBlinkTimer.Tick += OnCaretBlinkTick;
        _canvasCaretVisible = true;
        _canvasCaretBlinkTimer.Start();
    }
    private void StopCaretBlink()
    {
        _canvasCaretBlinkTimer?.Stop();
        _canvasCaretVisible = true;
    }
    private void OnCaretBlinkTick(object? sender, object e)
    {
        if (_canvasEditNodeId == null) { StopCaretBlink(); return; }
        _canvasCaretVisible = !_canvasCaretVisible;
        ScreenCanvas.Invalidate();    // 仅重绘（非整层重建），单卡编辑期开销可忽略
    }
    /// <summary>用户有输入/移动光标时把光标重置为"亮"并重启闪烁计时，避免停手前就灭。</summary>
    private void ResetCaretBlink()
    {
        _canvasCaretVisible = true;
        _canvasCaretBlinkTimer?.Start();   // 重新开始 530ms 倒计时
    }

    // ---- 编辑期"文档真相"轮询（≈2 帧一次）----
    // 为什么不能只靠 TextChanged：它由控件内部按"文本已变"抛出，但输入法组字/删除这些路径上实测会漏发，
    // 而绘制侧的文本快照（_canvasEditText）只由它刷新 ⇒ 漏一次就一直画旧文字，表现为"字删掉了但还在"、
    // 要退出编辑才消失（用户报告）。这里按固定间隔直接向文档要真相：文本变了就更新快照并请求重绘，
    // 事件通路保留 —— 它负责零延迟的那条快路，这里只做兜底。轮询本身只读文本，不改文档/不改布局。
    private const int CanvasEditPollMs = 30;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _canvasEditPollTimer;

    private void StartCanvasEditPoll()
    {
        if (_canvasEditPollTimer == null)
            _canvasEditPollTimer = DispatcherQueue.CreateTimer();
        _canvasEditPollTimer.Interval = TimeSpan.FromMilliseconds(CanvasEditPollMs);
        _canvasEditPollTimer.IsRepeating = true;
        _canvasEditPollTimer.Tick -= OnCanvasEditPollTick;
        _canvasEditPollTimer.Tick += OnCanvasEditPollTick;
        _canvasEditPollTimer.Start();
    }

    private void StopCanvasEditPoll() => _canvasEditPollTimer?.Stop();

    private void OnCanvasEditPollTick(object? sender, object e)
    {
        if (_canvasEditNodeId == null) { StopCanvasEditPoll(); return; }
        if (!TryGetCanvasEditText(out string live)) return;   // 读失败不当成"空文本"，什么都不做
        if (string.Equals(live, _canvasEditText, StringComparison.Ordinal)) return;
        _canvasEditText = live;
        _canvasEditLive = null;          // 可见串/映射/布局缓存一并作废
        _canvasEditTextLayoutKey = -1;
        ResetCaretBlink();
        ScreenCanvas.Invalidate();
        ScheduleCanvasEditSync();
        ScheduleCanvasEditCommit();
    }

    /// <summary>读编辑框文本；失败返回 false（绝不把"读失败"伪装成"空文本"，否则会把卡片内容当成被清空）。 RichEdit 用 \r 当段落符，且文档【末尾恒有一个】段落符 ⇒ 归一成 \n 后去掉末尾那一个。</summary>
    private bool TryGetCanvasEditText(out string text)
    {
        text = "";
        if (CanvasEditBox?.Document == null) return false;
        try
        {
            CanvasEditBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.None, out string s);
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            if (s.Length > 0 && s[s.Length - 1] == '\n') s = s.Substring(0, s.Length - 1);
            text = s;
            return true;
        }
        catch { return false; }
    }

    /// <summary>读编辑框文本（读失败按空串；需要区分失败用 TryGetCanvasEditText）。</summary>
    private string GetCanvasEditText() => TryGetCanvasEditText(out string t) ? t : "";

    /// <summary>两串文本的"改动区末尾"在新串里的下标（撤销/重做后光标放这里）。 做法：掐掉公共前缀与公共后缀，剩下的就是改动区，光标取其**末端** —— 撤销"删掉一个字"时正好落在恢复出来的字右边（用户期望的位置）；</summary>
    private static int CanvasTextChangeEnd(string oldText, string newText)
    {
        int minLen = Math.Min(oldText.Length, newText.Length);
        int prefix = 0;
        while (prefix < minLen && oldText[prefix] == newText[prefix]) prefix++;
        int suffix = 0, maxSuffix = minLen - prefix;
        while (suffix < maxSuffix
               && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix])
            suffix++;
        return newText.Length - suffix;
    }

    /// <summary>写编辑框文本（内部顺带锁定行距）；返回【文档全文长度】，供 SetRange 把光标落到末尾。 RichEdit 的段落符是 \r，故把文本里的 \n 全部转成 \r；末尾那个段落符也计入长度。</summary>
    private int SetCanvasEditText(string? text)
    {
        string plain = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        _canvasEditText = plain;
        _canvasEditLive = null;          // 文本换了 ⇒ 可见串/映射/布局缓存一律作废
        _canvasEditTextLayoutKey = -1;
        _canvasEditLineSpacingSet = -1f;   // 下面 SetText 会重置段落格式 ⇒ 已锁值作废，需重新锁
        string doc = plain.Replace('\n', '\r');
        try
        {
            CanvasEditBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, doc);
        }
        catch { return doc.Length + 1; }
        EnsureCanvasEditLineSpacing();   // SetText 会把段落格式重置成默认值 ⇒ 每次重建文档后都要重新锁一次行距
        _canvasEditFormattedFor = null;  // 文档刚被重写 ⇒ 字符格式也回到默认，必须重贴
        _canvasEditFormattedSpans = Array.Empty<CanvasMarkdownSpan>();
        ApplyCanvasEditCharacterFormatting();
        return doc.Length + 1;
    }

    // 编辑态字符格式（只为"度量对齐"，不可见）：隐形 RichEditBox 负责点击落光标/拖选/双击选词的命中测试，
    private bool _canvasEditApplyingFormat;
    private string? _canvasEditFormattedFor;
    /// <summary>上一次真正写进文档的样式区间（与 _canvasEditFormattedFor 配套）。</summary>
    private IReadOnlyList<CanvasMarkdownSpan> _canvasEditFormattedSpans = Array.Empty<CanvasMarkdownSpan>();

    /// <summary>
    /// 分词器要用的语言标签：含 CJK ⇒ 取中日韩标签（词典分词只在这种语言下生效）；纯拉丁 ⇒ en-US。
    /// 取值顺序：用户首选语言里的中日韩 → 当前输入法语言 → 固定 zh-CN 兜底。
    /// ⚠ 不要指望给 run 设 <c>CharacterFormat.LanguageTag</c> 能让 RichEdit 的 <c>tomWord</c> 按词断字 ——
    /// 实测对 SetText 灌入的中文无效（英文本来就按字母串断，看不出差别），故双击选词改为自己做（见 SelectCanvasEditWordAt）。
    /// </summary>
    private static string CanvasSegmenterLanguage(string text)
    {
        if (!ContainsCjk(text)) return "en-US";
        try
        {
            foreach (string tag in Windows.Globalization.ApplicationLanguages.Languages)
                if (IsCjkLanguage(tag)) return tag;
            string ime = Windows.Globalization.Language.CurrentInputMethodLanguageTag;
            if (IsCjkLanguage(ime)) return ime;
        }
        catch { }   // 取语言列表失败不致命：落到下面的固定标签
        return "zh-CN";
    }

    private static bool IsCjkLanguage(string? tag) =>
        tag != null && (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                        || tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
                        || tag.StartsWith("ko", StringComparison.OrdinalIgnoreCase));

    /// <summary>文本里有没有需要词典分词的字符（汉字 / 假名 / 谚文）。</summary>
    private static bool ContainsCjk(string text)
    {
        foreach (char c in text)
        {
            if (c >= '\u3040' && c <= '\u30FF') return true;    // 平假名 / 片假名
            if (c >= '\u3400' && c <= '\u4DBF') return true;    // 汉字扩展 A
            if (c >= '\u4E00' && c <= '\u9FFF') return true;    // 汉字基本区
            if (c >= '\uF900' && c <= '\uFAFF') return true;    // 兼容汉字
            if (c >= '\uAC00' && c <= '\uD7AF') return true;    // 谚文
        }
        return false;
    }

    /// <summary>
    /// 把行内 Markdown 样式同步成编辑框的【字符格式】。只动字符级（Bold/Italic/Strikethrough/Name），
    /// 绝不碰段落级（行距归 EnsureCanvasEditLineSpacing）。⚠ 格式改动可能再次触发 TextChanged ⇒ 用
    /// _canvasEditApplyingFormat 防重入。
    /// ⚠ 样式区间没变就【直接返回、不碰文档】：纯文本打字（尤其输入法组字）期间一次全文重排都不该发生 ——
    /// 在 TextChanged 通知里改文档会打断输入法组合（表现为漏字、退格删不掉），且每键一次全文重排本身就很贵。
    /// 区间不变 ⇒ 每个字符应有的样式没变，而新插入的字符只会继承"同一样式区间内"的邻字格式。
    /// </summary>
    private void ApplyCanvasEditCharacterFormatting()
    {
        if (_canvasEditNodeId == null || CanvasEditBox?.Document == null) return;
        if (_canvasEditApplyingFormat) return;
        if (string.Equals(_canvasEditFormattedFor, _canvasEditText, StringComparison.Ordinal)) return;

        var spans = CanvasMarkdown.ParseStyling(_canvasEditText);
        // 文档被整体重写过（_canvasEditFormattedFor == null）时必须整篇重贴，不能走这条捷径。
        if (_canvasEditFormattedFor != null && SameCanvasSpans(_canvasEditFormattedSpans, spans))
        {
            _canvasEditFormattedFor = _canvasEditText;
            return;
        }

        _canvasEditApplyingFormat = true;
        try
        {
            var doc = CanvasEditBox.Document;
            int len = _canvasEditText.Length;
            var all = doc.GetRange(0, len);
            all.CharacterFormat.Bold = Microsoft.UI.Text.FormatEffect.Off;
            all.CharacterFormat.Italic = Microsoft.UI.Text.FormatEffect.Off;
            all.CharacterFormat.Strikethrough = Microsoft.UI.Text.FormatEffect.Off;
            all.CharacterFormat.Name = CanvasCardFontFamily;

            foreach (var span in spans)
            {
                int start = Math.Clamp(span.Start, 0, len);
                int end = Math.Clamp(span.Start + span.Length, start, len);
                if (end <= start) continue;
                var r = doc.GetRange(start, end);
                switch (span.Kind)
                {
                    case CanvasMarkdownSpanKind.Bold:
                        r.CharacterFormat.Bold = Microsoft.UI.Text.FormatEffect.On;
                        break;
                    case CanvasMarkdownSpanKind.Italic:
                        r.CharacterFormat.Italic = Microsoft.UI.Text.FormatEffect.On;
                        break;
                    case CanvasMarkdownSpanKind.Strike:
                        r.CharacterFormat.Strikethrough = Microsoft.UI.Text.FormatEffect.On;
                        break;
                    case CanvasMarkdownSpanKind.Code:
                        r.CharacterFormat.Name = CanvasMarkdownCodeFontFamily;
                        break;
                }
            }
            _canvasEditFormattedFor = _canvasEditText;
            _canvasEditFormattedSpans = spans;
        }
        catch { }
        finally { _canvasEditApplyingFormat = false; }
    }

    private static bool SameCanvasSpans(IReadOnlyList<CanvasMarkdownSpan> a, IReadOnlyList<CanvasMarkdownSpan> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Start != b[i].Start || a[i].Length != b[i].Length || a[i].Kind != b[i].Kind)
                return false;
        }
        return true;
    }

    /// <summary>编辑态目标行高（页局部单位 = DIP）：取【阅读态那份 layout 的实测行高】(LineMetrics[0].Height)。 两态的行距都被 Uniform 钉到 CanvasLineHeight ⇒ 实测值即 CanvasLineHeight；仍读实测是为了让"两态同一口径"这件事由代码保证 —— 改行高常量时这里自动跟随，不会各留一份。 文字缺失 / 拿不到该节点时退回 CanvasLineHeight 兜底。</summary>
    private double GetCanvasEditTargetLineHeight(CanvasNode? node)
    {
        if (node != null)
        {
            try
            {
                double contentW = Math.Max(1, Math.Max(12, node.Width) - 2 * CanvasCardPadX);
                double contentH = Math.Max(1, Math.Max(12, node.Height));
                if (GetCanvasTextLayout(node, contentW, contentH) is { } layout)
                {
                    var lm = layout.LineMetrics;
                    if (lm != null && lm.Length > 0 && lm[0].Height > 0)
                        return lm[0].Height;
                }
            }
            catch { }
        }
        return CanvasLineHeight;
    }

    /// <summary>把编辑态行距【显式锁】到阅读态的实测行高，使两态行距一致（进出编辑不再看到行距突变）。 ⚠ 单位：SetLineSpacing 的线性值是【磅】(1/72 英寸)，而目标行高是页局部单位(≡DIP，1/96 英寸) ⇒ ×72/96。</summary>
    private void EnsureCanvasEditLineSpacing()
    {
        if (CanvasEditBox?.Document == null) return;
        float want = (float)(GetCanvasEditTargetLineHeight(_canvasEditNodeRef) * 72.0 / 96.0);
        if (Math.Abs(_canvasEditLineSpacingSet - want) < 0.05f) return;
        try
        {
            var range = CanvasEditBox.Document.GetRange(0, int.MaxValue);
            var pf = range.ParagraphFormat;
            pf.SpaceBefore = 0;
            pf.SpaceAfter = 0;
            pf.SetLineSpacing(Microsoft.UI.Text.LineSpacingRule.Exactly, want);
            range.ParagraphFormat = pf;
            // 读回确认（同时作为"已按此值锁好"的依据，避免每帧重复写）
            var chk = CanvasEditBox.Document.GetRange(0, int.MaxValue).ParagraphFormat;
            if (chk.LineSpacingRule == Microsoft.UI.Text.LineSpacingRule.Exactly
                && Math.Abs(chk.LineSpacing - want) < 0.05f)
                _canvasEditLineSpacingSet = want;
        }
        catch { /* 文档尚未就绪：不记已锁值，由 OnCanvasEditBoxLayoutUpdated 在后续布局帧重试 */ }
    }

    /// <summary>粘贴一律按【纯文本】插入：RichEditBox 默认会把剪贴板里的 RTF/HTML 格式一起贴进来， 让卡片文字变成混排字号/字体（TextBox 时代没有这个问题）。这里拦掉默认粘贴，只取纯文本。</summary>
    private async void CanvasEditBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        e.Handled = true;
        string paste;
        try
        {
            var dp = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!dp.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
            paste = await dp.GetTextAsync();
        }
        catch { return; }
        if (string.IsNullOrEmpty(paste)) return;
        CanvasEditBox.Document.Selection.SetText(
            Microsoft.UI.Text.TextSetOptions.None, paste.Replace("\r\n", "\r").Replace('\n', '\r'));
        CanvasEditBox.Document.Selection.Collapse(false);
    }

    // ───────────────────── 编辑态文本右键菜单（自绘，非弹层）─────────────────────

    /// <summary>菜单是否开着。只用于收尾判断（按下/按键/视图变化时收起），不参与焦点逻辑。</summary>
    private bool _canvasEditMenuOpen;

    /// <summary>正在执行一次菜单项点击。这段时间内的失焦【不算退出编辑】—— 点击行为发生在编辑框之外（菜单可以摆在卡片下方），框架会把焦点从编辑框挪走， 若照常 EndCanvasEdit 就会"全选刚做完就退出编辑、选区看不见"，等于动作没做（用户报告）。</summary>
    private bool _canvasEditMenuClickPending;

    // ---- 菜单来源判定：左键只有选取语义（单击/双击/多击/拖选），菜单只由 右键 / 键盘 / 触控长按 触发。
    private bool _canvasEditLastPressWasRight;
    /// <summary>键盘（Shift+F10 / Application 菜单键）明确请求了文本菜单的时刻：这一次请求放行。 键盘触发没有指针按下，不能靠"最近一次按下"判来源，故显式记一笔（ContextMenuOpening 消费即清，</summary>
    private long _canvasEditMenuKeyTick;

    // ---- 双击补救：选中态下"第一次单击"就进编辑（与 Obsidian 一致），于是双击的第二下其实落在编辑态里。
    private Point? _canvasEditEntryVp;
    private long _canvasEditEntryTick;
    private int _canvasEditEntryCaret = -1;
    private const int CanvasDoubleClickWindowMs = 500;
    private const double CanvasDoubleClickSlopDip = 4;
    private Microsoft.UI.Input.PointerDeviceType _canvasEditLastPressDevice;
    private bool _canvasEditLastPressWasLeft;
    /// <summary>最近一次按下的时刻。菜单请求只在"这次按下的窗口内"才可能被授权，避免用很久以前的一次右键放行。</summary>
    private long _canvasEditLastPressTick;
    /// <summary>键盘菜单请求标记的有效期（1s）：过期标记绝不能放行后面某次左键手势的请求。</summary>
    private static readonly long CanvasEditLeftMenuBlockTicks = System.Diagnostics.Stopwatch.Frequency;

    // ---- 菜单落点：右键/触控按下时就地记下（覆盖层坐标）----
    private Point _canvasEditMenuAnchor;
    private bool _canvasEditMenuAnchorValid;
    private long _canvasEditMenuAnchorTick;      // 记下时刻：只认"刚刚那一下"的按下（触控长按要等 ~0.6s 才请求菜单，窗口不能太窄）
    private static readonly long CanvasEditMenuAnchorTicks = System.Diagnostics.Stopwatch.Frequency * 2;

    /// <summary>这一次菜单请求有没有"该弹菜单"的来源。用**允许清单**判，不做排除法： ① 键盘（Shift+F10 / Application 键，KeyDown 时记的标记）；② 鼠标右键（含笔筒键）；</summary>
    private bool IsCanvasEditMenuRequestAuthorized()
    {
        bool keyboard = _canvasEditMenuKeyTick != 0 &&
            System.Diagnostics.Stopwatch.GetTimestamp() - _canvasEditMenuKeyTick < CanvasEditLeftMenuBlockTicks;
        _canvasEditMenuKeyTick = 0;   // 一次请求只消费一次
        if (keyboard) return true;
        // 触控 / 笔：没有左右键之分，长按就是合法的菜单入口（可能按住 >2s，不能套鼠标的时间窗）。
        if (_canvasEditLastPressDevice != Microsoft.UI.Input.PointerDeviceType.Mouse) return true;
        // 鼠标：只认"右键按下"，并且必须还在窗口内 —— 否则一次旧的右键会给后续请求长期授权。
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool fresh = _canvasEditLastPressTick != 0 && now - _canvasEditLastPressTick < CanvasEditMenuAnchorTicks;
        return fresh && _canvasEditLastPressWasRight && !_canvasEditLastPressWasLeft && !IsCanvasLeftButtonDown();
    }

    /// <summary>鼠标左键此刻是否按着（VK_LBUTTON）。包 try：鼠标键非常规入参，不支持时只当"没按着"。</summary>
    private static bool IsCanvasLeftButtonDown()
    {
        try
        {
            return (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftButton)
                    & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        }
        catch { return false; }
    }

    /// <summary>根级记下"最近一次按下"的来源（设备 / 左右键 / 时刻）。必须在**根**上记，不能只在编辑框上记： 点自绘菜单的行时按下点根本到不了编辑框，编辑框那条通道会一直持有上一次右键的旧值， 从而把随后"左键点菜单"补发的请求误判成右键（用户报告：点复制/全选不断弹出新菜单）。</summary>
    private void RecordCanvasEditPressOrigin(PointerRoutedEventArgs e)
    {
        if (_canvasEditNodeId == null) return;
        try
        {
            var pt = e.GetCurrentPoint(Viewer);
            _canvasEditLastPressDevice = e.Pointer.PointerDeviceType;
            _canvasEditLastPressWasRight = pt.Properties.IsRightButtonPressed;
            _canvasEditLastPressWasLeft = pt.Properties.IsLeftButtonPressed;
            _canvasEditLastPressTick = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        catch { }
    }

    private static bool IsCanvasEditMenuKey(KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Application) return true;
        if (e.Key != VirtualKey.F10) return false;
        try
        {
            return (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                    & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        }
        catch { return false; }
    }

    /// <summary>根级按下监听（handledEventsToo 常驻）：菜单开着时，**菜单外的任何按下都立即收起**。 挂在根上而不是 Viewer 上，是因为卡片胶囊 / 连线工具条这类子控件会自己把 PointerPressed 标 Handled，</summary>
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 先记"这次按下的来源"，与菜单是否已开无关：菜单请求的授权判据完全依赖它（见 IsCanvasEditMenuRequestAuthorized）。
        RecordCanvasEditPressOrigin(e);
        // 再记落点：右键 / 触控长按随后请求菜单时，要靠它把菜单摆到"按下的那一点"。
        RecordCanvasEditMenuAnchor(e);
        if (!_canvasEditMenuOpen) return;
        var pt = e.GetCurrentPoint(Viewer);
        if (IsPointInsideVisibleElement(CanvasEditContextMenu, pt.Position)) return;
        HideCanvasEditContextMenu();
    }

    /// <summary>记下用于摆菜单的按下点（覆盖层坐标）。只认右键 / 触控 / 笔：左键按下是"选文本"， 即便随后真把菜单调起来也该压在激活端或卡片内侧，不该拿它摆位。</summary>
    private void RecordCanvasEditMenuAnchor(PointerRoutedEventArgs e)
    {
        if (_canvasEditNodeId == null) return;
        try
        {
            var pt = e.GetCurrentPoint(CanvasOverlay);
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
                && !pt.Properties.IsRightButtonPressed) return;
            // 只认落在编辑框上的点：卡外的右键会结束编辑（菜单根本不会弹），记下来只会污染下一次键盘触发。
            if (!IsPointInsideVisibleElement(CanvasEditBox, pt.Position)) return;
            SetCanvasEditMenuAnchor(pt.Position);
        }
        catch { _canvasEditMenuAnchorValid = false; }
    }

    /// <summary>无条件把落点刷成当前指针位置（松手时用：那一刻右键已经抬起，按键判据不再成立）。</summary>
    private void RefreshCanvasEditMenuAnchor(PointerRoutedEventArgs e)
    {
        if (_canvasEditNodeId == null) return;
        try { SetCanvasEditMenuAnchor(e.GetCurrentPoint(CanvasOverlay).Position); }
        catch { _canvasEditMenuAnchorValid = false; }
    }

    private void SetCanvasEditMenuAnchor(Point p)
    {
        _canvasEditMenuAnchor = p;
        _canvasEditMenuAnchorValid = true;
        _canvasEditMenuAnchorTick = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>落点是否"刚刚那一下按下"留下的：右键/触控请求与按下几乎同拍，键盘请求（Shift+F10） 根本没有按下 ⇒ 不能用陈旧点，否则菜单会回到上一次右键的位置。</summary>
    private bool IsCanvasEditMenuAnchorFresh() =>
        _canvasEditMenuAnchorValid &&
        System.Diagnostics.Stopwatch.GetTimestamp() - _canvasEditMenuAnchorTick < CanvasEditMenuAnchorTicks;

    /// <summary>右键/键盘请求弹出文本菜单。**必须在 ContextMenuOpening 里拦**：RichEditBox 的内置菜单走的是 控件自己的这个事件；</summary>
    private void CanvasEditBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_canvasEditNodeId == null) return;
        e.Handled = true;   // ← 系统菜单不再出现（框架据此根本不构建它）；本方法返回后不再有任何菜单工作
        if (!IsCanvasEditMenuRequestAuthorized()) return;
        Point pos;
        if (IsCanvasEditMenuAnchorFresh())
        {
            pos = _canvasEditMenuAnchor;
        }
        else if (!TryGetCanvasEditMenuAnchor(e, out pos))
        {
            // ③ 拿不到可信落点：指针若仍在卡上就用它，否则退到卡片左上角内侧 ——
            pos = (_canvasPointerValid && IsPointInsideVisibleElement(CanvasEditBox, _canvasPointerPos))
                ? _canvasPointerPos
                : CanvasEditBoxOriginInOverlay(12, 12);
        }
        _canvasEditMenuAnchorValid = false;   // 一次请求只消费一次
        ShowCanvasEditContextMenu(pos);
    }

    /// <summary>编辑框左上角在覆盖层坐标系里的位置（可加偏移）。抛异常一律按 (8,8) 兜底。</summary>
    private Point CanvasEditBoxOriginInOverlay(double dx, double dy)
    {
        try
        {
            var r = CanvasEditBox.TransformToVisual(CanvasOverlay)
                .TransformBounds(new Rect(0, 0, CanvasEditBox.ActualWidth, CanvasEditBox.ActualHeight));
            return new Point(r.X + dx, r.Y + dy);
        }
        catch { return new Point(8, 8); }
    }

    /// <summary>菜单落点。`CursorLeft/CursorTop` 在 WinUI 3 里**不是**"相对控件"的坐标（UWP 文档那套写法 在这里会算飞，表现为菜单被钳到视口右下角），故用【多候选 + 必须落在编辑框内】自校验： 分别以编辑框 / 本控件 / 窗口根为原点换算一次，</summary>
    private bool TryGetCanvasEditMenuAnchor(ContextMenuEventArgs e, out Point anchor)
    {
        anchor = default;
        double cx = e.CursorLeft, cy = e.CursorTop;
        if (double.IsNaN(cx) || double.IsNaN(cy)) return false;
        Rect cardRect;
        try
        {
            var r = CanvasEditBox.TransformToVisual(CanvasOverlay)
                .TransformBounds(new Rect(0, 0, CanvasEditBox.ActualWidth, CanvasEditBox.ActualHeight));
            if (r.Width <= 0 || r.Height <= 0) return false;
            cardRect = new Rect(r.X - 16, r.Y - 16, r.Width + 32, r.Height + 32);
        }
        catch { return false; }
        var origins = new UIElement?[] { CanvasEditBox, this, XamlRoot?.Content as UIElement };
        foreach (var origin in origins)
        {
            if (origin == null) continue;
            try
            {
                var p = origin.TransformToVisual(CanvasOverlay).TransformPoint(new Point(cx, cy));
                if (cardRect.Contains(p)) { anchor = p; return true; }
            }
            catch { }
        }
        return false;
    }

    private void ShowCanvasEditContextMenu(Point pos)
    {
        if (CanvasEditContextMenu == null || _canvasEditNodeId == null) return;
        // 快照可能落后一拍（TOM 路径不保证触发 TextChanged）⇒ 弹出前重读一次，可用性判断才不会用到旧串。
        _canvasEditText = GetCanvasEditText();
        // 文案每次弹出时取（语言可在运行期变化；弹出不是高频路径，字典查找无感）。
        CanvasEditMenuSelectAllText.Text = GlanceRX.Services.LocalizationService.Get("CanvasSelectAll");
        CanvasEditMenuBoldText.Text = GlanceRX.Services.LocalizationService.Get("CanvasBold");
        CanvasEditMenuItalicText.Text = GlanceRX.Services.LocalizationService.Get("CanvasItalic");
        CanvasEditMenuStrikeText.Text = GlanceRX.Services.LocalizationService.Get("CanvasStrike");
        CanvasEditMenuCodeText.Text = GlanceRX.Services.LocalizationService.Get("CanvasInlineCode");
        CanvasEditMenuHighlightText.Text = GlanceRX.Services.LocalizationService.Get("CanvasHighlight");
        CanvasEditMenuClearFormatText.Text = GlanceRX.Services.LocalizationService.Get("CanvasClearFormatting");
        // 高亮色块：直接取渲染用的同一个常量，且**不透明**。半透明色块会跟菜单底色混合（浅色菜单偏白、
        CanvasEditMenuHighlightSwatch.Background = new SolidColorBrush(CanvasMarkdownHighlightBackdrop);
        SetCanvasEditMenuItemEnabled(CanvasEditMenuCut, CanvasEditHasSelection());
        SetCanvasEditMenuItemEnabled(CanvasEditMenuCopy, CanvasEditHasSelection());
        SetCanvasEditMenuItemEnabled(CanvasEditMenuPaste, CanvasClipboardHasText());
        SetCanvasEditMenuItemEnabled(CanvasEditMenuSelectAll, _canvasEditText.Length > 0);
        // 格式组：空卡片没东西可包 ⇒ 灰掉；有文字就可用（无选区时点一下 = 插入一对标记并把光标放中间）。
        bool hasText = _canvasEditText.Length > 0;
        SetCanvasEditMenuItemEnabled(CanvasEditMenuBold, hasText);
        SetCanvasEditMenuItemEnabled(CanvasEditMenuItalic, hasText);
        SetCanvasEditMenuItemEnabled(CanvasEditMenuStrike, hasText);
        SetCanvasEditMenuItemEnabled(CanvasEditMenuCode, hasText);
        SetCanvasEditMenuItemEnabled(CanvasEditMenuHighlight, hasText);
        // 清除格式是"对一段范围剥标记"的操作 ⇒ 没有选区时不可用（零宽光标无从下手）。
        SetCanvasEditMenuItemEnabled(CanvasEditMenuClearFormat, CanvasEditHasSelection());
        CanvasEditContextMenu.Visibility = Visibility.Visible;
        // 先量出实际尺寸再钳制落点：菜单贴右/贴下会溢出视口。UpdateLayout 是同步的，
        CanvasEditContextMenu.UpdateLayout();
        double w = CanvasEditContextMenu.ActualWidth > 0 ? CanvasEditContextMenu.ActualWidth : 192;
        double h = CanvasEditContextMenu.ActualHeight > 0 ? CanvasEditContextMenu.ActualHeight : 284;
        // 同样走安全区：菜单若被钳到顶栏宿主底下，里面的行也一样点不动。
        CanvasEditContextMenu.Margin = new Thickness(ClampOverlayX(pos.X, w), ClampOverlayY(pos.Y, h), 0, 0);
        _canvasEditMenuOpen = true;
    }

    private void HideCanvasEditContextMenu()
    {
        if (CanvasEditContextMenu == null) return;
        _canvasEditMenuOpen = false;
        CanvasEditContextMenu.Visibility = Visibility.Collapsed;
        // 清悬停底色：点中某一行会把它连同菜单一起 Collapsed，那一行收不到 PointerExited，
        SetCanvasEditMenuRowHover(CanvasEditMenuCut, false);
        SetCanvasEditMenuRowHover(CanvasEditMenuCopy, false);
        SetCanvasEditMenuRowHover(CanvasEditMenuPaste, false);
        SetCanvasEditMenuRowHover(CanvasEditMenuBold, false);
        SetCanvasEditMenuRowHover(CanvasEditMenuItalic, false);
        SetCanvasEditMenuRowHover(CanvasEditMenuStrike, false);
        SetCanvasEditMenuRowHover(CanvasEditMenuCode, false);
        SetCanvasEditMenuRowHover(CanvasEditMenuHighlight, false);
        SetCanvasEditMenuRowHover(CanvasEditMenuClearFormat, false);
        SetCanvasEditMenuRowHover(CanvasEditMenuSelectAll, false);
    }

    /// <summary>菜单项可用性用 Opacity 表达（1 = 可用 / 0.4 = 灰掉）：行是 Grid（不可聚焦）， 灰掉只影响观感与是否执行，不引入额外的状态字段。</summary>
    private static void SetCanvasEditMenuItemEnabled(Grid row, bool enabled)
    {
        if (row == null) return;
        row.Opacity = enabled ? 1.0 : 0.4;
    }

    private static bool IsCanvasEditMenuItemEnabled(Grid row) => row != null && row.Opacity >= 0.99;

    /// <summary>悬停底色：每行第一个子元素就是那块置底的 Border（Opacity 0/1 切换； 颜色来自主题资源，深浅主题都不用另配）。</summary>
    private static void SetCanvasEditMenuRowHover(Grid row, bool on)
    {
        if (row != null && row.Children.Count > 0 && row.Children[0] is Border hover)
            hover.Opacity = on ? 1.0 : 0.0;
    }

    private void CanvasEditMenuItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid row && IsCanvasEditMenuItemEnabled(row)) SetCanvasEditMenuRowHover(row, true);
    }

    private void CanvasEditMenuItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid row) SetCanvasEditMenuRowHover(row, false);
    }

    private void CanvasEditContextMenu_PointerPressed(object sender, PointerRoutedEventArgs e) => e.Handled = true;

    private void CanvasEditMenuItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (!e.GetCurrentPoint(CanvasEditContextMenu).Properties.IsLeftButtonPressed) return;   // 只认左键
        if (sender is not Grid row || !IsCanvasEditMenuItemEnabled(row) || row.Tag is not string action) return;
        // 菜单可以摆在卡片下方，点菜单本身就在编辑框之外 —— 框架会把焦点从编辑框挪走。先立"点击进行中"标记，
        // 让 CanvasEditBox_LostFocus 这段时间内不结束编辑（否则"全选刚做完就退出编辑、选区看不见"，等于没做）。
        MarkCanvasEditMenuClickPending();
        // 先执行、后收起：执行期间菜单仍可见，万一同一次按下的后续处理落到 Viewer，
        ExecuteCanvasEditMenuAction(action);
        HideCanvasEditContextMenu();
    }

    /// <summary>"菜单项点击进行中"窗口（一次点击的按下→抬起跨度）。用一次性定时器收尾而不是队列尾： 焦点可能在抬起的另一拍才被挪走，队列尾太早。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _canvasEditMenuClickTimer;

    private void MarkCanvasEditMenuClickPending()
    {
        _canvasEditMenuClickPending = true;
        _canvasEditMenuClickTimer ??= DispatcherQueue.CreateTimer();
        _canvasEditMenuClickTimer.Stop();
        _canvasEditMenuClickTimer.Interval = TimeSpan.FromMilliseconds(500);
        _canvasEditMenuClickTimer.IsRepeating = false;
        _canvasEditMenuClickTimer.Tick -= OnCanvasEditMenuClickTick;
        _canvasEditMenuClickTimer.Tick += OnCanvasEditMenuClickTick;
        _canvasEditMenuClickTimer.Start();
    }

    private void OnCanvasEditMenuClickTick(object? sender, object e) => _canvasEditMenuClickPending = false;

    private void ExecuteCanvasEditMenuAction(string action)
    {
        if (_canvasEditNodeId == null) return;
        try
        {
            switch (action)
            {
                case "cut":
                    CopyCanvasSelectionToClipboard();
                    // 选区替换成空串 = 删除选区（与粘贴用同一 API，语义一致）
                    CanvasEditBox.Document.Selection.SetText(Microsoft.UI.Text.TextSetOptions.None, "");
                    // ⚠ TOM 改动不保证触发 TextChanged ⇒ 必须显式刷新文本快照，否则 Win2D 仍按旧串重画
                    RefreshCanvasEditAfterProgrammaticEdit();
                    break;
                case "copy":
                    CopyCanvasSelectionToClipboard();
                    break;
                case "paste":
                    PasteCanvasClipboardText();
                    break;
                // ---- Markdown 快捷格式：对当前选区做"包/拆"切换（同一标记再点一次即取消）----
                case "bold": ToggleCanvasMarkdownWrap("**"); break;
                case "italic": ToggleCanvasMarkdownWrap("*"); break;
                case "strike": ToggleCanvasMarkdownWrap("~~"); break;
                case "code": ToggleCanvasMarkdownWrap("`"); break;
                case "highlight": ToggleCanvasMarkdownWrap("=="); break;
                case "clearformat": ClearCanvasMarkdownFormatting(); break;
                case "selectall":
                {
                    int len = GetCanvasEditText().Length;
                    var sel = CanvasEditBox.Document.Selection;
                    // 先把【活动端】放到文末，再把另一端拉回文首 ⇒ 选中全文，且插入光标停在末尾。
                    sel.SetRange(len, len);
                    sel.StartPosition = 0;
                    // 自绘光标的活动端是"与上一帧比较"推出来的：清掉基线，下一帧直接取 End（见 DrawCanvasEditCaret）。
                    ResetCanvasSelBaseline();
                    break;
                }
            }
        }
        catch { }
        // 选区/文字变化不经过 TextChanged（全选只动选区）⇒ 这里统一补一次重绘与光标重置。
        ResetCaretBlink();
        ScreenCanvas.Invalidate();
    }

    /// 把选区用 包起来；若已经带着这一对标记，则改为拆掉（同一项再点一次 = 取消）。
    private void ToggleCanvasMarkdownWrap(string marker)
    {
        if (_canvasEditNodeId == null || CanvasEditBox?.Document == null) return;
        int ml = marker.Length;
        if (ml == 0) return;
        string text = GetCanvasEditText();
        int start, end;
        try
        {
            var sel = CanvasEditBox.Document.Selection;
            start = Math.Min(sel.StartPosition, sel.EndPosition);
            end = Math.Max(sel.StartPosition, sel.EndPosition);
        }
        catch { return; }
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);

        // ① 外侧已成对：先删右侧再删左侧（右侧位置在后，删左侧不会影响它）。
        if (start >= ml && end + ml <= text.Length
            && string.CompareOrdinal(text, start - ml, marker, 0, ml) == 0
            && string.CompareOrdinal(text, end, marker, 0, ml) == 0)
        {
            CanvasDocReplace(end, end + ml, "");
            CanvasDocReplace(start - ml, start, "");
            FinishCanvasMarkdownToggle(start - ml, end - ml);
            return;
        }

        // ② 选区自身首尾带标记：同样先删尾部那一对，避免前面的删除挪动后面的索引。
        if (end - start >= ml * 2
            && string.CompareOrdinal(text, start, marker, 0, ml) == 0
            && string.CompareOrdinal(text, end - ml, marker, 0, ml) == 0)
        {
            CanvasDocReplace(end - ml, end, "");
            CanvasDocReplace(start, start + ml, "");
            FinishCanvasMarkdownToggle(start, end - ml * 2);
            return;
        }

        if (end == start)
        {
            CanvasDocReplace(start, start, marker + marker);
            FinishCanvasMarkdownToggle(start + ml, start + ml);
            return;
        }

        // ③ 包起来。尾部空白（尤其"拖到行尾"带上的段落符）不纳入标记：否则 closer 落到下一行行首，
        int wrapEnd = end;
        while (wrapEnd > start && char.IsWhiteSpace(text[wrapEnd - 1])) wrapEnd--;
        if (wrapEnd <= start) wrapEnd = end;
        CanvasDocReplace(wrapEnd, wrapEnd, marker);   // 先加尾，再加头：尾部插入不影响 start
        CanvasDocReplace(start, start, marker);
        FinishCanvasMarkdownToggle(start + ml, wrapEnd + ml);
    }

    /// <summary>快捷键入口：菜单开着就先收起（与"按任意键即收起菜单"一致），再做同一次包/拆。</summary>
    private void ApplyCanvasMarkdownShortcut(string marker)
    {
        if (_canvasEditMenuOpen) HideCanvasEditContextMenu();
        ToggleCanvasMarkdownWrap(marker);
        ResetCaretBlink();
        ScreenCanvas.Invalidate();
    }

    /// <summary>用 TOM 区段把 [start,end) 换成 text（start==end 即纯插入）。失败静默：格式只是锦上添花， 不能因一次 TOM 调用失败把整个编辑会话打断。</summary>
    private void CanvasDocReplace(int start, int end, string text)
    {
        try
        {
            if (end < start) (start, end) = (end, start);
            CanvasEditBox.Document.GetRange(start, end)
                .SetText(Microsoft.UI.Text.TextSetOptions.None, text ?? "");
        }
        catch { }
    }

    /// <summary>文本已被程序化改动 ⇒ 刷新快照/字符格式/留白，再把选区落到 [start,end)（活动端在末尾）， 并作废"自绘选区活动端"基线（下一帧直接按 End 取活动端）。</summary>
    private void FinishCanvasMarkdownToggle(int start, int end)
    {
        RefreshCanvasEditAfterProgrammaticEdit();
        try
        {
            var sel = CanvasEditBox.Document.Selection;
            // 与"全选"同一手法：先把活动端放到 end，再把另一端拉回 start——直接 SetRange(start,end)
            sel.SetRange(end, end);
            sel.StartPosition = start;
        }
        catch { }
        ResetCanvasSelBaseline();
    }

    /// 清除格式：把选区里的行内标记一律剥掉（粗/斜/斜粗/删/高亮/行内代码/转义反斜杠）。
    /// 否则会留下"半对标记"（比如只清掉后半段的 closer，前半段的 opener 变成字面量）。
    private void ClearCanvasMarkdownFormatting()
    {
        if (_canvasEditNodeId == null || CanvasEditBox?.Document == null) return;
        string text = GetCanvasEditText();
        if (text.Length == 0) return;
        int start, end;
        try
        {
            var sel = CanvasEditBox.Document.Selection;
            start = Math.Min(sel.StartPosition, sel.EndPosition);
            end = Math.Max(sel.StartPosition, sel.EndPosition);
        }
        catch { return; }
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        if (end <= start) return;

        var parsed = CanvasMarkdown.Parse(text);
        var map = parsed.SourceIndexMap;
        if (map == null || map.Count != parsed.DisplayText.Length + 1) return;

        foreach (var span in parsed.Spans)
        {
            int last = span.Start + span.Length - 1;
            if (span.Length <= 0 || last < 0 || last >= map.Count) continue;
            int ds = map[Math.Clamp(span.Start, 0, map.Count - 1)];
            int de = map[last] + 1;
            if (de <= start || ds >= end) continue;
            start = Math.Min(start, ds);
            end = Math.Max(end, de);
        }

        while (true)
        {
            bool expanded = false;
            for (int len = 3; len >= 1; len--)
            {
                if (start < len || end + len > text.Length) continue;
                string left = text.Substring(start - len, len);
                if (!string.Equals(left, text.Substring(end, len), StringComparison.Ordinal)) continue;
                if (!IsCanvasMarkdownDelimiterRun(left)) continue;
                start -= len;
                end += len;
                expanded = true;
                break;
            }
            if (!expanded) break;
        }

        // ③ 范围内凡是"标记字符"就删掉（从后往前，前面的下标不受影响）
        var isMarkup = new bool[text.Length];
        Array.Fill(isMarkup, true);
        for (int i = 0; i < parsed.DisplayText.Length; i++)
        {
            int src = map[i];
            if (src >= 0 && src < text.Length) isMarkup[src] = false;
        }
        int removed = 0;
        for (int i = end - 1; i >= start; i--)
        {
            if (!isMarkup[i]) continue;
            CanvasDocReplace(i, i + 1, "");
            removed++;
        }
        if (removed == 0) return;   // 选区里本来就没有标记 ⇒ 不刷新、不产生历史记录
        FinishCanvasMarkdownToggle(start, end - removed);
    }

    /// <summary>这串字符是不是纯 Markdown 定界符（用于"外侧成对标记"识别，避免把普通字符当成标记）。</summary>
    private static bool IsCanvasMarkdownDelimiterRun(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
            if (c is not ('*' or '~' or '=' or '`')) return false;
        return true;
    }

    private bool CanvasEditHasSelection()
    {
        try
        {
            var sel = CanvasEditBox.Document.Selection;
            return Math.Abs(sel.EndPosition - sel.StartPosition) > 0;
        }
        catch { return false; }
    }

    private static bool CanvasClipboardHasText()
    {
        try
        {
            return Windows.ApplicationModel.DataTransfer.Clipboard.GetContent()
                .Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text);
        }
        catch { return false; }
    }

    private void CopyCanvasSelectionToClipboard()
    {
        string text;
        try { text = CanvasEditBox.Document.Selection.Text ?? ""; } catch { return; }
        if (text.Length == 0) return;
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n"));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }
        catch { }
    }

    private async void PasteCanvasClipboardText()
    {
        string text;
        try
        {
            var data = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!data.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
            text = await data.GetTextAsync();
        }
        catch { return; }
        if (_canvasEditNodeId == null || string.IsNullOrEmpty(text)) return;
        CanvasEditBox.Document.Selection.SetText(Microsoft.UI.Text.TextSetOptions.None,
            text.Replace("\r\n", "\r").Replace('\n', '\r'));
        CanvasEditBox.Document.Selection.Collapse(false);
        RefreshCanvasEditAfterProgrammaticEdit();
    }

    /// <summary>把编辑框当前文本作为一步提交（不关闭编辑框）；与上次提交相同则忽略。 文本未变时 MainPage 侧也会自动忽略，这里提前判断避免无谓事件。</summary>
    private void FlushCanvasEditText()
    {
        StopCanvasEditCommitTimer();
        if (_canvasEditNodeId == null) return;
        _canvasEditText = GetCanvasEditText();
        string t = _canvasEditText;
        if (string.Equals(t, _canvasLastCommittedText, StringComparison.Ordinal)) return;
        _canvasLastCommittedText = t;
        CanvasNodeTextCommitted?.Invoke(this, new CanvasNodeEventArgs
        {
            NodeId = _canvasEditNodeId,
            Text = t
        });
    }

    /// <summary>合并式浮层同步：Draw 每帧调用也只排一次 UI 队列任务，避免布局在绘制中重入。</summary>
    private void RequestCanvasOverlaySync()
    {
        if (_canvasOverlaySyncPending) return;
        _canvasOverlaySyncPending = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _canvasOverlaySyncPending = false;
            SyncCanvasOverlayNow();
        });
    }

    /// <summary>设置视口四边被【上层浮层】占掉的量（DIP），悬浮胶囊/文本菜单的钳制会避开这些带。 由 MainPage 在布局变化（窗口尺寸 / 顶栏尺寸 / 侧栏开合）时推送。全 0 = 不避让（旧行为）。</summary>
    public void SetCanvasViewportInsets(double left, double top, double right, double bottom)
    {
        if (Math.Abs(_canvasInsetL - left) < 0.5 && Math.Abs(_canvasInsetT - top) < 0.5 &&
            Math.Abs(_canvasInsetR - right) < 0.5 && Math.Abs(_canvasInsetB - bottom) < 0.5) return;
        _canvasInsetL = left; _canvasInsetT = top; _canvasInsetR = right; _canvasInsetB = bottom;
        // 安全区变了 ⇒ 按新边界重新钳制（两张胶囊都要重落位，否则会停在旧的越界位置）。
        _canvasToolbarPlaced = false;
        _canvasEdgeToolbarPlaced = false;
        SyncCanvasOverlayNow();
    }

    /// <summary>水平钳制：左/右各让开安全区 + 4 DIP 余量。</summary>
    private double ClampOverlayX(double x, double w)
    {
        double min = 4 + _canvasInsetL;
        return Math.Clamp(x, min, Math.Max(min, _vpW - w - 4 - _canvasInsetR));
    }

    /// <summary>垂直钳制：上/下各让开安全区 + 4 DIP 余量。</summary>
    private double ClampOverlayY(double y, double h)
    {
        double min = 4 + _canvasInsetT;
        return Math.Clamp(y, min, Math.Max(min, _vpH - h - 4 - _canvasInsetB));
    }

    private void SyncCanvasOverlayNow()
    {
        if (CanvasOverlay == null) return;

        bool editing = _canvasEditNodeId != null;
        // 编辑态：先摆不可见编辑框（输入宿主）。⚠ 这里【不能 return】—— 编辑卡上方的卡片工具栏也要摆，
        // 否则编辑态缩放卡片时它会停在原地、不随卡片上沿移动（用户报告）。
        if (editing) PositionCanvasEditBox();

        if (!editing && _selectedCanvasEdgeId != null)
        {
            PositionCanvasEdgeToolbar();
            return;
        }

        if (_selectedCanvasNodeId == null ||
            !TryGetCanvasNodeScreenRect(_selectedCanvasNodeId, out var rect))
        {
            if (editing) return;
            HideCanvasCardToolbar();
            HideCanvasEdgeToolbar();
            CollapseCanvasOverlayIfIdle();   // 两颗胶囊都收了才收整层
            return;
        }

        CanvasOverlay.Visibility = Visibility.Visible;
        ShowCanvasCardToolbar();
        HideCanvasEdgeToolbar();
        // 浮动控件尺寸固定（两颗按钮），测一次即缓存：拖动时每次指针事件都会走到这里，
        if (_canvasToolbarW <= 0)
        {
            // ⚠ Measure 的 DesiredSize【含 Margin】：胶囊上次定位写入的 (left,top) 会被算进宽高，
            CanvasCardToolbar.Margin = new Thickness(0);
            _canvasToolbarPlaced = false;
            CanvasCardToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var measured = CanvasCardToolbar.DesiredSize;
            _canvasToolbarW = measured.Width;
            _canvasToolbarH = measured.Height;
        }
        double szW = _canvasToolbarW, szH = _canvasToolbarH;
        // 展开 7 色时右缘钳制要用【展开态真实宽】：否则越界时内容被视口截断、圆角还在，观感很怪。
        double clampW = (_canvasColorPickerShown && _canvasToolbarExpandedW > 0) ? _canvasToolbarExpandedW : szW;
        double left = rect.X + rect.Width / 2.0 - szW / 2.0;
        // 与卡片顶边边框控件（缩放手柄）的命中带分离：手柄带屏幕高 ≈ clamp(CanvasHandleHitRadius*z, 10, 26) DIP，
        // 工具栏底部须高于该带 + 余量，否则 XAML 工具栏会盖住手柄命中区 ⇒ 指针事件被吞、光标错乱。
        double handleBand = Math.Clamp(CanvasHandleHitRadius * CurrentZoom,
            (double)CanvasConnHandleHitMinDip, (double)CanvasConnHandleHitMaxDip);
        double gap = Math.Max(CanvasToolbarGap, handleBand + 4);
        double top = rect.Y - szH - gap;
        // ⚠ 钳制必须走【安全区】：顶栏宿主/底部胶囊等上层浮层会吞掉指针，钳到它们底下的胶囊按钮点不动。
        left = ClampOverlayX(left, clampW);
        // 双向钳制：上方空间不够时【不再翻到卡片下方】（避免一步跳变），而是停在视口上缘、允许侵入卡片内部；
        top = ClampOverlayY(top, szH);
        if (!_canvasToolbarPlaced ||
            Math.Abs(left - _canvasToolbarLeft) > 0.5 || Math.Abs(top - _canvasToolbarTop) > 0.5)
        {
            CanvasCardToolbar.Margin = new Thickness(left, top, 0, 0);
            _canvasToolbarLeft = left;
            _canvasToolbarTop = top;
            _canvasToolbarPlaced = true;
        }
    }

    // 卡片悬浮控件显隐：**不做动画**，直接 Visible ↔ Collapsed（用户口径：高频点卡片时淡入淡出太喧宾夺主）。
    private void ShowCanvasCardToolbar()
    {
        if (CanvasCardToolbar == null) return;
        if (_isCanvasConnecting) return;   // 拉线（连线）进行中：暂隐胶囊，避免遮挡连线交互
        if (_cardToolbarShown) return;
        _cardToolbarShown = true;
        CanvasCardToolbar.Visibility = Visibility.Visible;
    }

    private void HideCanvasCardToolbar()
    {
        if (CanvasCardToolbar == null) return;
        HideCanvasColorPicker();
        _cardToolbarShown = false;
        CanvasCardToolbar.Visibility = Visibility.Collapsed;
    }

    private void ShowCanvasEdgeToolbar()
    {
        if (CanvasEdgeToolbar == null) return;
        if (_edgeToolbarShown) return;
        _edgeToolbarShown = true;
        CanvasEdgeToolbar.Visibility = Visibility.Visible;
    }

    private void HideCanvasEdgeToolbar()
    {
        if (CanvasEdgeToolbar == null) return;
        HideCanvasEdgePickers();   // 连线胶囊收起（取消选中/切卡片/退出模式）→ 两个二级面板一并收起
        _edgeToolbarShown = false;
        CanvasEdgeToolbar.Visibility = Visibility.Collapsed;
    }

    /// <summary>浮层里的东西都收干净了（无选中 / 无编辑 / 两颗胶囊都收起）才把整层 <c>Collapsed</c>。</summary>
    private void CollapseCanvasOverlayIfIdle()
    {
        if (CanvasOverlay == null) return;
        if (_cardToolbarShown || _edgeToolbarShown) return;
        if (_canvasEditNodeId != null || _selectedCanvasNodeId != null || _selectedCanvasEdgeId != null) return;
        CanvasOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>把边工具栏摆到选中连线锚点附近（仿 Obsidian Canvas）： X=连线贝塞尔中点 X，Y=靠上方卡片连接点 Y；出厂在视口外时【双向钳制】到视口边缘， 并尽可能满足原 XY（向下滚→钉视口上缘，向右滚→钉视口左缘，皆不够则停最近角）。</summary>
    private void PositionCanvasEdgeToolbar()
    {
        if (_selectedCanvasEdgeId == null ||
            !TryGetCanvasEdgeToolbarAnchorScreen(_selectedCanvasEdgeId, out var anchor))
        {
            HideCanvasEdgeToolbar();
            CollapseCanvasOverlayIfIdle();   // 同卡片胶囊：两颗都收了才收整层
            return;
        }
        CanvasOverlay.Visibility = Visibility.Visible;
        HideCanvasCardToolbar();
        ShowCanvasEdgeToolbar();
        RefreshEdgeArrowButtons();
        if (_canvasEdgeToolbarW <= 0)
        {
            // ⚠ Measure 的 DesiredSize【含 Margin】（卡片胶囊踩过同一个坑）：上次定位写入的 (left,top) 会被算进
            var saved = CanvasEdgeToolbar.Margin;
            CanvasEdgeToolbar.Margin = new Thickness(0);
            _canvasEdgeToolbarPlaced = false;
            CanvasEdgeToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var m = CanvasEdgeToolbar.DesiredSize;
            _canvasEdgeToolbarW = m.Width;
            _canvasEdgeToolbarH = m.Height;
            CanvasEdgeToolbar.Margin = saved;
        }
        // 锚点在工具栏上方留出间隙；双向各自钳制到 [4, 视口 - 工具栏尺寸 - 4]
        // 展开二级面板时右缘钳制要用【展开态真实宽】，否则内容被视口截断、圆角还在（同卡片胶囊）。
        double clampW = ((_canvasEdgeArrowPickerShown || _canvasEdgeColorPickerShown) && _canvasEdgeToolbarExpandedW > 0)
            ? _canvasEdgeToolbarExpandedW : _canvasEdgeToolbarW;
        double left = anchor.X - _canvasEdgeToolbarW / 2.0;
        double top = anchor.Y - _canvasEdgeToolbarH - CanvasToolbarGap;
        left = ClampOverlayX(left, clampW);
        top = ClampOverlayY(top, _canvasEdgeToolbarH);
        if (!_canvasEdgeToolbarPlaced ||
            Math.Abs(left - _canvasEdgeToolbarLeft) > 0.5 || Math.Abs(top - _canvasEdgeToolbarTop) > 0.5)
        {
            CanvasEdgeToolbar.Margin = new Thickness(left, top, 0, 0);
            _canvasEdgeToolbarLeft = left;
            _canvasEdgeToolbarTop = top;
            _canvasEdgeToolbarPlaced = true;
        }
    }

    /// <summary>把【不可见输入宿主】编辑框贴合到卡片屏幕上：宽高/字号/内边距/变换矩阵用页局部值 + MatrixTransform， 内容起点 = 卡片内边距，与 Win2D 文字原点对齐。</summary>
    private void PositionCanvasEditBox()
    {
        CanvasNode? node = null;
        int pageIdx = -1;
        lock (_canvasLock)
        {
            if (_canvasEditNodeId != null && _canvasIndex.TryGetValue(_canvasEditNodeId, out var n))
            {
                node = n;
                pageIdx = n.GlancePage;
            }
        }
        var pages = _pages;
        if (node == null || pageIdx < 0 || pageIdx >= pages.Count)
        {
            if (CanvasEditBox != null) CanvasEditBox.Visibility = Visibility.Collapsed;
            HideCanvasCardToolbar();
            HideCanvasEdgeToolbar();
            CollapseCanvasOverlayIfIdle();   // 卡片已失效 ⇒ 收胶囊（顺带复位 _xxxToolbarShown），空闲才收整层
            return;
        }

        GetViewState(out double z, out double hOff, out double vOff);
        if (z <= 0) return;
        var xf = MakePageXform(ComputePageDest(pageIdx, z, hOff, vOff), z);
        _canvasEditNodeRef = node;

        double w = Math.Max(12, node.Width);
        double h = Math.Max(12, node.Height);
        // 编辑框是【不可见输入宿主】(Opacity=0)：仅接收输入并维护文档与光标，自身不绘制。
        // 内容起点 = 卡片内边距(PadX,PadY)，与 Win2D 文字原点对齐；行距锁到 Win2D 实测值（仅供"点击落光标"命中映射）。
        var p00 = xf.Map(node.X, node.Y);
        var p10 = xf.Map(node.X + w, node.Y);
        var p01 = xf.Map(node.X, node.Y + h);
        var m = new Matrix
        {
            M11 = (p10.X - p00.X) / w,
            M12 = (p10.Y - p00.Y) / w,
            M21 = (p01.X - p00.X) / h,
            M22 = (p01.Y - p00.Y) / h,
            OffsetX = p00.X,
            OffsetY = p00.Y,
        };

        // 先把编辑框【全部几何/变换属性】设到位，【最后】才显示——避免 Collapsed→Visible 那一下用旧值闪一帧。
        CanvasEditBox.Width = w;
        CanvasEditBox.Height = h;
        CanvasEditBox.FontSize = CanvasCardFontSize;
        // 内边距：左/右 = 卡片内边距（与 Win2D 文字原点对齐）；上 = 卡片内边距 + 上方自适应留空（同一公式）。
        // ⚠ 下【额外】预留一个"手柄命中带"高度：RichEditBox 内部有个"文字面"元素会设 I 形光标，其区域被 Padding 内缩；
        CanvasEditBox.Padding = new Thickness(CanvasCardPadX,
            CanvasCardPadY + ComputeCanvasEditTopPad(w - 2 * CanvasCardPadX, h - 2 * CanvasCardPadY),
            CanvasCardPadX, CanvasCardPadY + CanvasHandleHitRadius);
        CanvasEditBox.Margin = new Thickness(0);
        CanvasEditBox.RenderTransform = new MatrixTransform { Matrix = m };

        HideCanvasEdgeToolbar();
        CanvasOverlay.Visibility = Visibility.Visible;
        CanvasEditBox.Visibility = Visibility.Visible;
        EnsureCanvasEditLineSpacing();   // 行距锁到 Win2D 实测值（仅供点击落光标的命中映射对齐）
        EnsureCanvasEditScrollHost();
    }

    /// <summary>确保已拿到 RichEditBox 内部 ScrollViewer 并挂上 ViewChanged/SizeChanged（幂等）—— 编辑态滚动由 RichEditBox 内部 ScrollViewer 驱动，偏移变化只需刷新 Win2D 绘制（文字/光标/滚动条随内容滚动）。</summary>
    private void EnsureCanvasEditScrollHost()
    {
        if (_canvasEditScrollViewer == null)
        {
            var sv = FindDescendant<ScrollViewer>(CanvasEditBox);
            if (sv == null) return;   // 模板还没展开 → 等下一次布局（有界：每帧至多一次）
            _canvasEditScrollViewer = sv;
            _canvasEditScrollViewer.ViewChanged += (_, __) => ScreenCanvas.Invalidate();
            _canvasEditScrollViewer.SizeChanged += (_, __) => ScreenCanvas.Invalidate();
            CanvasEditBox.LayoutUpdated -= OnCanvasEditBoxLayoutUpdated;
        }
    }

    /// <summary>
    /// 卡片文本排版格式（阅读态 / 编辑态共用这一份）。字体与隐形编辑框（XAML 侧 FontFamily）一致。
    /// ⚠ 行距必须【显式钉死】：默认行距按"本行实际用到的字体"取度量，而中文走字体回退 ——
    /// Segoe UI 上行 12.95 / 行高 15.96，雅黑 UI 上行 12.19 / 行高 15.24 ⇒ 同一串文字只要在
    /// "纯中文"与"中英混排"之间切换（输入法拼音进出正好如此）基线与行高就跟着变，表现为打字时
    /// 文字忽高忽低；行高还会反过来喂给 EnsureCanvasEditLineSpacing，让编辑框反复重写全文段落格式
    /// 而打断输入法组合（漏字/删不掉）。DirectWrite 的 Uniform 行距就是为消除字体回退造成的不均匀
    /// 而设：行高与基线只由本格式决定，与内容无关。
    /// ⚠ 基线不给时默认按 1 DIP 处理 ⇒ 首行会被顶出裁切区，故先用【纯拉丁参考串】（不触发回退）
    /// 实测主字体上行，再作为 Uniform 行距的基线写入。
    /// </summary>
    private CanvasTextFormat EnsureCanvasTextFormat(ICanvasResourceCreator creator)
    {
        if (_canvasTextFormat != null) return _canvasTextFormat;
        float baseline = 0;
        try
        {
            var probeFormat = new CanvasTextFormat
            {
                FontFamily = CanvasCardFontFamily,
                FontSize = CanvasCardFontSize,
                WordWrapping = CanvasWordWrapping.Wrap,
            };
            using var probe = new CanvasTextLayout(creator, "Ag", probeFormat, 64f, 64f);
            var lm = probe.LineMetrics;
            if (lm is { Length: > 0 }) baseline = lm[0].Baseline;
        }
        catch { }
        if (!(baseline > 0)) baseline = CanvasCardFontSize * 1.079f;   // 兜底：Segoe UI 的 hhea/win 上行（2210/2048）
        float pitch = CanvasLineHeight;
        if (baseline >= pitch) baseline = pitch * 0.9f;                 // 基线必须落在行高内，否则行间会互相压
        var format = new CanvasTextFormat
        {
            FontFamily = CanvasCardFontFamily,
            FontSize = CanvasCardFontSize,
            WordWrapping = CanvasWordWrapping.Wrap,
        };
        // ⚠ 赋值顺序：每次 set 都会拿"当前这三个值"重算一次行距。若先切 Uniform 再填行高，中间态是
        //    Uniform + 行高 -1（Win2D 的"未设"哨兵）⇒ DirectWrite 判非法。故先把行高/基线设成正数，最后切模式。
        format.LineSpacing = pitch;
        format.LineSpacingBaseline = baseline;
        format.LineSpacingMode = CanvasLineSpacingMode.Uniform;
        _canvasTextFormat = format;
        return format;
    }

    /// <summary>编辑框布局回调 —— 编辑期间持续订阅：① 模板展开前每帧重试取内部 ScrollViewer（幂等）； ② 行距自愈（进入编辑当帧文档可能未就绪，逐帧重试直到确认生效）；</summary>
    private void OnCanvasEditBoxLayoutUpdated(object? sender, object e)
    {
        if (_canvasEditNodeId == null)
        {
            CanvasEditBox.LayoutUpdated -= OnCanvasEditBoxLayoutUpdated;
            return;
        }
        EnsureCanvasEditLineSpacing();
        ApplyCanvasEditCharacterFormatting();   // 字符格式同样自愈：进入编辑当帧文档可能还没就绪（首贴会失败）
        if (_canvasEditScrollViewer == null) EnsureCanvasEditScrollHost();
        else ScreenCanvas.Invalidate();                                     // 每帧兜底刷新 Win2D 绘制
    }

    /// <summary>编辑框上的指针移动：编辑框是 XAML 元素、会吃掉 Viewer 的 PointerMoved ⇒ 编辑态下 `_canvasPointerPos` 不更新，卡片边框手柄（四角/四边）就算不出"指针最近的那一个"、无法显示与命中。</summary>
    private void OnCanvasEditBoxPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_canvasEditNodeId == null) return;
        var p = e.GetCurrentPoint(Viewer).Position;
        _canvasPointerOnEditCard = true;   // 编辑框通道收到移动 ⇒ 指针在编辑卡上
        // 文本拖选进行中（左键按住）：冻结边框控件交互 —— 不刷新手柄显形/悬停，
        if (e.GetCurrentPoint(Viewer).Properties.IsLeftButtonPressed)
        {
            // 拖选接管：可见串 ≠ 文档串，原生按自己的文档排版拖选会偏 ⇒ 按可见 layout 反查源偏移，自己维护选区。
            int off = MapCanvasEditPointToDocOffset(p);
            if (off >= 0 && _canvasEditDragAnchor >= 0 && off != _canvasEditDragLast)
            {
                _canvasEditDragLast = off;
                _canvasEditPendingCaret = -1;   // 已经在拖选 ⇒ 作废排队中的"单击落光标"
                SetCanvasEditSelection(_canvasEditDragAnchor, off);
            }
            CanvasEditBox.OverrideCursor(CanvasIBeamCursor);
            return;
        }
        _canvasPointerPos = p;
        _canvasPointerValid = true;
        UpdateCanvasNodeHover(p);                       // 先刷新悬停（手柄命中依赖 _hoverCanvasNodeId）
        // 命中边框控件带 ⇒ 把该带从编辑框命中区【挖掉】（信号完全切割，见 SyncCanvasEditHitCarve）。
        SyncCanvasEditHitCarve(p);
        var hoverCursor = ResolveEditHoverCursor(p);
        CanvasEditBox.OverrideCursor(hoverCursor ?? CanvasIBeamCursor);
        InvalidateOverlayThrottled();
    }

    private Microsoft.UI.Input.InputCursor? ResolveEditHoverCursor(Point p) => ResolveCanvasHoverCursor(p);

    /// <summary>卡片"控件"悬停光标（编辑态与非编辑态共用）：在控件上给对应光标，文本区返回 null（= 控件默认 I 形）。 优先级：滚动条 &gt; 四角缩放(斜向) &gt; 四边中点连线(手型) &gt; 四边中段单轴缩放(上下/左右)。 注：四边中点圆形拉线控件返回 null —— 手型由手型作用域经系统箭头槽提供（与连线同源），不在此抢 ProtectedCursor。</summary>
    private Microsoft.UI.Input.InputCursor? ResolveCanvasHoverCursor(Point p)
    {
        // 胶囊叠层遮罩：悬停其上时不强加背后卡片的边框控件光标（卡片角/边缩放光标等），
        // 使胶囊成为真正隔离的控件面（用户实测：胶囊区光标会随背后卡片变）。
        if (IsPointOverCanvasOverlayChrome(p)) return null;
        if (TryHitCanvasScrollBar(p, out var sb) && sb != null) return CanvasArrowCursor;
        if (TryHitCanvasCorner(p, out _, out var corner))
            return (corner == "tl" || corner == "br") ? CanvasResizeNwSeCursor : CanvasResizeNeSwCursor;
        if (TryHitCanvasHandle(p, out _, out _)) return null;   // 圆形拉线控件：手型（与连线同源，走手型作用域系统槽，不抢 ProtectedCursor）
        if (TryHitCanvasEdge(p, out _, out var edge))
            return (edge == "top" || edge == "bottom") ? CanvasResizeNsCursor : CanvasResizeWeCursor;
        return null;
    }

    /// <summary>编辑框上的指针抬起。编辑态下能落在编辑框上的只有【文本按下】—— 边框控件命中带已被 从编辑框命中区挖除，控件交互全程走 Viewer 通道（含其自有的 PointerReleased 收尾）。</summary>
    private void OnCanvasEditBoxPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _canvasEditDragAnchor = _canvasEditDragLast = _canvasEditPendingCaret = -1;
        _canvasPointerPos = e.GetCurrentPoint(Viewer).Position;
        _canvasPointerValid = true;
        _canvasPointerOnEditCard = IsPointOnCanvasEditCard(_canvasPointerPos);
        // 右键松手 = 菜单即将弹出（系统在松手时才请求）：把落点刷新到松手点，
        if (_canvasEditLastPressWasRight && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
            RefreshCanvasEditMenuAnchor(e);
    }

    /// <summary>指针离开编辑卡：清"在卡上"标志 ⇒ 工字作用域轮询下一拍卸下替换（卡外走正常悬停光标）。 按住（拖选滑出卡）时不清：按住全程恒工字，松手后 Released 按实际位置重判自愈。</summary>
    private void CanvasEditBox_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (CursorInterop.AnyMouseButtonDown()) return;
        _canvasPointerOnEditCard = false;
    }

    /// <summary>在可视化树里深度优先找第一个指定类型后代（用于取 TextBox 模板内的 ScrollViewer）。 对未接入视觉树的元素，VisualTreeHelper 可能抛异常 → 一律按"没找到"处理，绝不外抛。</summary>
    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) return null;
        int n;
        try { n = VisualTreeHelper.GetChildrenCount(root); }
        catch { return null; }
        for (int i = 0; i < n; i++)
        {
            DependencyObject child;
            try { child = VisualTreeHelper.GetChild(root, i); }
            catch { return null; }
            if (child is T hit) return hit;
            var deep = FindDescendant<T>(child);
            if (deep != null) return deep;
        }
        return null;
    }

    private bool TryGetCanvasNodeScreenRect(string nodeId, out Rect rect)
    {
        rect = default;
        var pages = _pages;
        if (pages.Count == 0) return false;
        CanvasNode? node = null;
        int pageIdx = -1;
        lock (_canvasLock)
        {
            if (_canvasIndex.TryGetValue(nodeId, out var n)) { node = n; pageIdx = n.GlancePage; }
        }
        if (node == null || pageIdx < 0 || pageIdx >= pages.Count) return false;

        GetViewState(out double z, out double hOff, out double vOff);
        if (z <= 0) return false;
        var dest = ComputePageDest(pageIdx, z, hOff, vOff);

        var xf = MakePageXform(dest, z);
        var c0 = xf.Map(node.X, node.Y);
        var c1 = xf.Map(node.X + node.Width, node.Y);
        var c2 = xf.Map(node.X, node.Y + node.Height);
        var c3 = xf.Map(node.X + node.Width, node.Y + node.Height);
        float minX = MathF.Min(MathF.Min(c0.X, c1.X), MathF.Min(c2.X, c3.X));
        float maxX = MathF.Max(MathF.Max(c0.X, c1.X), MathF.Max(c2.X, c3.X));
        float minY = MathF.Min(MathF.Min(c0.Y, c1.Y), MathF.Min(c2.Y, c3.Y));
        float maxY = MathF.Max(MathF.Max(c0.Y, c1.Y), MathF.Max(c2.Y, c3.Y));
        rect = new Rect(minX, minY, maxX - minX, maxY - minY);
        return true;
    }

    /// <summary>每帧把可见页位图按当前 偏移/缩放 画到视口画布上（纯 GPU 拷贝+缩放）。</summary>
    private void ScreenCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        // ---- 动画步进：与本次上屏同帧完成，循环由 Win2D vsync 自持续 ----
        {
            _lastFrameTicks = Stopwatch.GetTimestamp();
            double dt = ComputeFrameDt();
            bool animating = false;
            if (_isSmoothZooming) { StepZoomFrame(dt); animating = true; }
            if (_isWheelScrolling) { StepScrollFrame(dt); animating = true; }
            StopFramePumpIfIdle();
            if (animating) InvalidateBoth();               // 视图在动 ⇒ 页变换变了，标注层必须整层重建 + 重绘
        }

        var ds = args.DrawingSession;
        ds.Clear(_gapColor);

        if (!ComputeVisiblePages(out double z, out double hOff, out double vOff, out var visible)) return;

        // ---- RenderCache::Paint（1:1 RenderCache.cpp Paint）：每可见页跑一次目标层瓦片 BFS。
        // 只画与视口相交的瓦片；未命中 → 任意 zoom 同位瓦片替身（factor 缩放显示）；
        _drawDs = ds;
        var cache = _renderCache;
        if (cache != null)
        {
            // 瓦片引擎内部全是物理像素、且几何在【未旋转页帧】（与 Sumatra 同单位同帧）。
            double dpi = _dpiScale > 0 ? _dpiScale : 1.0;
            float zoomPx = (float)(z * 96.0 / 72.0 * dpi);
            bool renderMissing = !_isSmoothZooming && !_isRightZooming;
            int rot = _rotation;
            var savedTransform = ds.Transform;
            foreach (var (p, dest) in visible)
            {
                ds.FillRectangle(dest, _placeholderColor);
                PageFrameForTiles(dest, out var pageOnScreenPx, out var pivot);
                if (rot != 0)
                {
                    var rotM = Matrix3x2.CreateRotation((float)(rot * Math.PI / 180.0), pivot);
                    if (rot == 90 || rot == 270)
                    {
                        // ⚠ 90/270 旋转必须做半像素对齐修正：不做则落定后整页发糊。
                        // pivot=页中心（整数或 x.5 物理像素）。绕 pivot 转 90° 的落点
                        double px = pivot.X * dpi, py = pivot.Y * dpi;
                        double sSum = px + py, dDif = py - px;
                        float fdx = (float)((Math.Round(sSum) - sSum) / dpi);
                        float fdy = (float)((Math.Round(dDif) - dDif) / dpi);
                        if (fdx != 0f || fdy != 0f)
                            rotM = rotM * Matrix3x2.CreateTranslation(fdx, fdy);   // 行向量约定：v*(R*T)=先 R 后 T
                    }
                    ds.Transform = savedTransform * rotM;
                }
                try { cache.Paint(p.Index, pageOnScreenPx, renderMissing, zoomPx); }
                catch { }
                if (rot != 0) ds.Transform = savedTransform;
            }
        }
        else
        {
            foreach (var (p, dest) in visible)
                ds.FillRectangle(dest, _placeholderColor);
        }
        _drawDs = null;

        // ---- 标注叠加：与页位图画在【同一交换链】（原子合成，消除双画布相位脱节的震动/闪光）。
        // 页集合要带前后各一页：墨挂在发起页上，溢出到相邻页的部分在发起页出视口后仍应留在屏上。
        var inkPages = ExpandPagesForInk(visible, z, hOff, vOff);
        if (AnnotationsVisible && HasPenAnnotations(inkPages))
        {
            if (IsViewAnimating || _isDragPanning)
            {
                // 视图在动：逐笔矢量重画 —— 几何已按 PointsVersion 缓存，每帧只是换一次页变换重画，
                // 于是缩放/旋转/平移期间笔迹都按当前缩放光栅化，全程高清，与白板卡片同一性质。
                // ⚠ 刻意不做"笔数多就降级成位图"：每帧成本 ∝ 点数而非笔数，按笔数判会把
                //   "1 笔 10 万点"当成轻松负担（既掉帧又白白糊一次），而 240 笔各 5 点反而被误伤。
                DrawAnnotationsOnCanvas(ds, z, inkPages);
                // 层像素已与当前视图脱钩：停稳后必须整层重烘，否则会拿旧变换的像素贴新视图。
                _annoLayerNeedsFullRebuild = true;
            }
            else
            {
                EnsureAnnoLayer();
                if (_annoLayer != null)
                {
                    if (_annoLayerNeedsFullRebuild) RebuildAnnoLayer(inkPages, z, hOff, vOff);
                    else BakeUnbakedStrokes(inkPages, z, hOff, vOff);
                    ds.DrawImage(_annoLayer);
                }
            }
        }

        // ---- 白板层：卡片 + 连线。与页位图/标注绘于同一交换链，随页面旋转/缩放。
        if (AnnotationsVisible && HasCanvasContent())
            DrawWhiteboardLayer(ds, z, visible);

        // 视图在动 → 选中卡片 / 编辑框 / 选中连线的屏幕位置随之改变，每帧合并排一次浮层重定位。
        // ⚠ 三个条件缺一不可：漏掉 _selectedCanvasEdgeId 时，选中连线后滚动/缩放不会重排边工具栏，
        if (_selectedCanvasNodeId != null || _canvasEditNodeId != null || _selectedCanvasEdgeId != null)
            RequestCanvasOverlaySync();
    }

    /// <summary>缩放/滚动动画是否进行中。此时标注【逐帧随变换改变】：累积层是按某一变换烘好的位图，
    /// 原地 1:1 重用必然错位，所以这一帧改为逐笔矢量重画（几何有缓存，与白板卡片同一性质）。</summary>
    private bool IsViewAnimating =>
        _isSmoothZooming || _isWheelScrolling || _zoomAnimCommitting;

    /// <summary>视图移动期间走这条：不用累积层，直接逐笔描边，页变换在进入该页第一笔时切一次。
    /// 几何全部复用缓存的 CanvasGeometry，仅 ApplyPageTransform + DrawGeometry，与页位图同帧上屏。
    /// 顺序取全局落笔序（与累积层的烘入顺序同一份），所以缩放时层级与静止时完全一致。</summary>
    private void DrawAnnotationsOnCanvas(CanvasDrawingSession ds, double z,
        List<(GpuPageInfo Page, Rect Dest)> visible)
    {
        // 逐页设置的是【绝对】会话变换，绘制后必须还原为恒等：否则最后的页变换会泄漏给
        // 紧随其后的白板层 —— 连线在屏幕坐标绘制，被泄漏的页变换二次映射后会"脱落"卡片，
        var saved = ds.Transform;
        EnsureInkOrder();
        int curPage = -1;
        foreach (var (a, page) in _inkOrder)
        {
            if (!TryFindPageDest(visible, page, out Rect dest)) continue;
            if (page != curPage) { ApplyPageTransform(ds, dest, z); curPage = page; }
            DrawAnnotation(ds, a);
        }
        ds.Transform = saved;
    }

    /// <summary>文档坐标 → 屏幕坐标的居中补偿量：内容小于视口时居中，否则为 0。
    /// 可见页剔除、累积层锚点记录、增量烘入前的视图比对必须共用这一份 —— 各算一遍必然漂移。
    /// 取画布视口快照而非实时 Viewer.Viewport*：偏移与居中量必须同一时刻。</summary>
    private void ComputeViewAnchor(double z, out double cx, out double cy)
    {
        double vpW = _vpW;
        double vpH = _vpH;
        double extentW = Spacer.Width * z;
        double extentH = Spacer.Height * z;
        cx = extentW < vpW ? (vpW - extentW) / 2.0 : 0;
        cy = extentH < vpH ? (vpH - extentH) / 2.0 : 0;
    }

    private bool ComputeVisiblePages(out double z, out double hOff, out double vOff,
        out List<(GpuPageInfo Page, Rect Dest)> visible)
    {
        visible = new();
        var pages = _pages;
        z = 0; hOff = 0; vOff = 0;
        if (pages.Count == 0) return false;

        GetViewState(out z, out hOff, out vOff);
        if (z <= 0) return false;

        double vpW = _vpW;
        double vpH = _vpH;

        ComputeViewAnchor(z, out double centerX, out double centerY);

        double topDoc = vOff / z;
        double bottomDoc = (vOff + vpH) / z;
        double leftDoc = hOff / z;
        double rightDoc = (hOff + vpW) / z;

        foreach (var p in pages)
        {
            // 主轴剔除：横向布局按 X 范围、纵向按 Y 范围。
            if (IsHorizontalLayout)
            {
                if (p.LayoutRect.X + p.LayoutRect.Width < leftDoc || p.LayoutRect.X > rightDoc) continue;
            }
            else
            {
                if (p.LayoutRect.Bottom < topDoc || p.LayoutRect.Top > bottomDoc) continue;
            }

            var dest = new Rect(
                centerX + p.LayoutRect.X * z - hOff,
                centerY + p.LayoutRect.Y * z - vOff,
                p.LayoutRect.Width * z,
                p.LayoutRect.Height * z);

            visible.Add((p, dest));
        }
        return true;
    }

    /// <summary>页 dest（旋转后屏幕矩形）对应的【未旋转】绘制矩形：交换宽高、同中心。 rot==0 时即 dest 本身。瓦片引擎（TileRenderCache）的一切几何都在这个帧里 —— 出图恒未旋转，</summary>
    private Rect PageUnrotatedRect(Rect dest)
    {
        int rot = _rotation;
        if (rot == 0) return dest;
        bool swap = rot == 90 || rot == 270;
        double w = swap ? dest.Height : dest.Width;
        double h = swap ? dest.Width : dest.Height;
        double cx = dest.X + dest.Width / 2.0;
        double cy = dest.Y + dest.Height / 2.0;
        return new Rect(cx - w / 2.0, cy - h / 2.0, w, h);
    }

    /// <summary>交给瓦片引擎的页几何：未旋转页帧的物理像素矩形 + 该帧对应的旋转枢轴（DIP）。 枢轴取【取整后】矩形的中心，必须与 TileRenderCache.Paint 内部对 pageOnScreen 的取整 严格一致 —— 否则旋转页会有半像素偏移，位图又被重采样（回到"发糊"）。</summary>
    private void PageFrameForTiles(Rect destDip, out Rect pageOnScreenPx, out Vector2 pivotDip)
    {
        double dpi = _dpiScale > 0 ? _dpiScale : 1.0;
        var un = PageUnrotatedRect(destDip);
        pageOnScreenPx = new Rect(
            Math.Round(un.X * dpi), Math.Round(un.Y * dpi),
            Math.Round(un.Width * dpi), Math.Round(un.Height * dpi));
        pivotDip = new Vector2(
            (float)((pageOnScreenPx.X + pageOnScreenPx.Width / 2.0) / dpi),
            (float)((pageOnScreenPx.Y + pageOnScreenPx.Height / 2.0) / dpi));
    }

    // ---------------- Sumatra RenderCache（瓦片渲染/内存管理）外壳接线 ----------------

    private TileRenderCache? _renderCache;

    /// <summary>当前绘制帧的 DrawingSession（DrawTileBitmap 在 Paint 期间使用；仅 UI 线程 Draw 帧内非空）。</summary>
    private CanvasDrawingSession? _drawDs;

    /// <summary>native→CanvasBitmap 上传用的【唯一】托管暂存缓冲（UI 线程专用，只增不减）。 存在的理由：Win2D 只有 CreateFromBytes(byte[]) 这条入口，必须先落到托管内存上。</summary>
    private byte[] _uploadScratch = Array.Empty<byte>();

    private byte[] EnsureUploadScratch(int length)
    {
        if (_uploadScratch.Length < length)
            _uploadScratch = new byte[length];
        return _uploadScratch;
    }

    private void ShutdownRenderCache()
    {
        var cache = _renderCache;
        _renderCache = null;
        if (cache == null) return;
        try { cache.CancelRenderingBlocking(this); } catch { }
        try { cache.FreeForDisplayModel(this); } catch { }
        try { cache.Dispose(); } catch { }
    }

    // ---- ITileRenderHost 实现（DisplayModel 渲染侧语义；详见 TileRenderCache.cs 头注释）----

    private double CurrentZoomForKernel
    {
        get { lock (_lock) return _liveZoom > 0 ? _liveZoom : 1.0; }
    }

    float ITileRenderHost.PageWidthPt(int pageNo)
    {
        var pages = _pages;
        return pageNo >= 0 && pageNo < pages.Count ? (float)pages[pageNo].WidthPoints : 0f;
    }

    float ITileRenderHost.PageHeightPt(int pageNo)
    {
        var pages = _pages;
        return pageNo >= 0 && pageNo < pages.Count ? (float)pages[pageNo].HeightPoints : 0f;
    }

    /// <summary>dm->GetZoomReal()：每 PDF 点的【物理像素】数（z×96/72×DpiScale）。 与 Sumatra 同单位——瓦片引擎内部不再出现 DIP。</summary>
    float ITileRenderHost.ZoomReal(int pageNo) => (float)(CurrentZoomForKernel * 96.0 / 72.0 * _dpiScale);

    Windows.Foundation.Size ITileRenderHost.ViewPortSize
    {
        get { lock (_lock) return new Windows.Foundation.Size(_vpW * _dpiScale, _vpH * _dpiScale); }
    }

    /// <summary>pageInfo->pageOnScreen 对应物：LayoutRect×z 平移到视口坐标（含 ScrollViewer 居中补偿， 与 ComputeVisiblePages 同式），取【未旋转】页矩形（引擎内部帧），最后 ×DpiScale 换成物理像素。</summary>
    Rect ITileRenderHost.PageOnScreen(int pageNo)
    {
        lock (_lock)
        {
            var pages = _pages;
            if (pageNo < 0 || pageNo >= pages.Count) return default;
        }
        double z = CurrentZoomForKernel;
        double dpi = _dpiScale;
        double extentW, extentH;
        double vpW, vpH;
        double hOff, vOff;
        lock (_lock)
        {
            vpW = _vpW; vpH = _vpH; hOff = _liveH; vOff = _liveV;
        }
        extentW = Spacer.Width * z;
        extentH = Spacer.Height * z;
        double centerX = extentW < vpW ? (vpW - extentW) / 2.0 : 0;
        double centerY = extentH < vpH ? (vpH - extentH) / 2.0 : 0;
        var lr = _pages[pageNo].LayoutRect;
        var dest = new Rect(
            lr.X * z - hOff + centerX,
            lr.Y * z - vOff + centerY,
            lr.Width * z, lr.Height * z);
        var un = PageUnrotatedRect(dest);
        return new Rect(un.X * dpi, un.Y * dpi, un.Width * dpi, un.Height * dpi);
    }

    private bool PageVisibleLocked(int pageNo)
    {
        var pages = _pages;
        if (pageNo < 0 || pageNo >= pages.Count) return false;
        var r = pages[pageNo].LayoutRect;
        return r.Left < _visRight && r.Right > _visLeft && r.Top < _visBottom && r.Bottom > _visTop;
    }

    bool ITileRenderHost.PageVisible(int pageNo)
    {
        lock (_lock) return PageVisibleLocked(pageNo);
    }

    /// <summary>DisplayModel::PageVisibleNearby：本行或相邻行任一页可见。 纵向布局每页一行 → 本页 ±1；横向连续全部页同属一行 → 任一页可见。</summary>
    bool ITileRenderHost.PageVisibleNearby(int pageNo)
    {
        lock (_lock)
        {
            if (IsHorizontalLayout)
            {
                for (int i = 0; i < _pages.Count; i++)
                    if (PageVisibleLocked(i)) return true;
                return false;
            }
            return PageVisibleLocked(pageNo) || PageVisibleLocked(pageNo - 1) || PageVisibleLocked(pageNo + 1);
        }
    }

    bool ITileRenderHost.PauseRendering => false;

    int ITileRenderHost.Rotation => _rotation;

    void ITileRenderHost.Repaint() => InvalidateBoth();

    void ITileRenderHost.PostToUi(Action action) =>
        DispatcherQueue?.TryEnqueue(() => { try { action(); } catch { } });

    /// <summary>engine->RenderPage(RenderPageArgs{pageRect}) 对应物：只光栅化瓦片矩形 （密度 = DIP 比例 × DpiScale = 物理 px，HiDPI 不糊）。走 PdfRenderService 并发槽。</summary>
    (IntPtr Data, int Length, int W, int H) ITileRenderHost.RenderTile(
        IntPtr cookie, int pageNo, int devW, int devH, int x0, int y0, int x1, int y1)
    {
        // 同步、零拷贝：native 缓冲的所有权直接交给缓存（= Sumatra 的 Pixmap*）。
        var svc = RenderService!;
        try
        {
            return svc.RenderPageRegionOwningNative(
                (uint)pageNo, (uint)devW, (uint)devH,
                (uint)Math.Clamp(x0, 0, devW), (uint)Math.Clamp(y0, 0, devH),
                (uint)Math.Clamp(x1, 0, devW), (uint)Math.Clamp(y1, 0, devH), cookie);
        }
        catch (Exception ex)
        {
            // 单块瓦片渲染失败（native 返回 Err：本页内容 MuPDF 回放失败 / 瓦片矩形退化导致
            Diagnostics.CrashLog.CaptureHandled(
                $"RenderTile page={pageNo} dev={devW}x{devH} rect=({x0},{y0})-({x1},{y1})", ex);
            return (IntPtr.Zero, 0, 0, 0);
        }
    }

    void ITileRenderHost.FreeTilePixels(IntPtr data, int length)
    {
        if (data == IntPtr.Zero) return;
        try { GlanceRXNative.memory_free(data, (ulong)length); } catch { }
    }

    /// <summary>BlitPixmapRegion 对应物：GPU 位图惰性创建（CPU 位图仍是缓存真身，与 Sumatra 一致）， 上采样 NN / 缩小 Linear（硬性规则）。仅 UI 线程 Draw 帧内被 Paint 调用。</summary>
    void ITileRenderHost.DrawTileBitmap(BitmapCacheEntry entry, Rect target, Rect source)
    {
        var ds = _drawDs;
        if (ds == null) return;
        if (entry.Gpu == null)
        {
            if (entry.Pixels == IntPtr.Zero) return;
            var device = ScreenCanvas?.Device;
            if (device == null) return;
            int len = entry.BitmapW * entry.BitmapH * 4;
            if (len <= 0 || len > entry.PixelsLen) return;
            try
            {
                // Win2D 必须先有一块托管内存才能建 CanvasBitmap（CreateFromBytes），
                var scratch = EnsureUploadScratch(len);
                System.Runtime.InteropServices.Marshal.Copy(entry.Pixels, scratch, 0, len);
                entry.Gpu = CanvasBitmap.CreateFromBytes(device, scratch, entry.BitmapW, entry.BitmapH,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized);
                entry.GpuBytes = len;
            }
            catch { return; }
            // GPU 副本已就位 → native 那份再无任何读取点（Sumatra 必须留是因为它 GDI 直 blit
            entry.ReleaseCpuBitmap(this);
        }
        // 上采样判定：目标与源同为像素单位才可比（target 传画布前才换算）。
        bool upsample = target.Width > source.Width || target.Height > source.Height;
        var interp = upsample ? CanvasImageInterpolation.NearestNeighbor : CanvasImageInterpolation.Linear;
        double dpi = _dpiScale > 0 ? _dpiScale : 1.0;
        var targetDip = new Rect(target.X / dpi, target.Y / dpi, target.Width / dpi, target.Height / dpi);
        try { ds.DrawImage(entry.Gpu, targetDip, source, 1f, interp); }
        catch { }
    }

    /// <summary>DisplayModel::RenderVisibleParts 的 1:1 对应物（= 原 QueueVisiblePages 职责）： 可见页正向请求 → 预测链（next/prev 各一页，</summary>
    // ---------------- 滚动条滑块拖拽期：抑制渲染请求（对齐 Sumatra 快拖静止行为） ----------------
    // 400% 快拖 115 页时每个中间视口都全速发请求，而 MuPDF JPEG
    // 逐行顺序解码使瓦片成本 ∝ 瓦片 Y 偏移（page 114 实测页顶 36ms → 页底 352~454ms），

    private const int ScrollDragSettleDelayMs = 180;
    private bool _isScrollBarDragging;
    private bool _scrollDragSettleQueued;
    private long _scrollDragLastMoveTicks;
    private double _scrollDragLastH = double.NaN;
    private double _scrollDragLastV = double.NaN;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _scrollDragSettleTimer;
    private readonly HashSet<Microsoft.UI.Xaml.Controls.Primitives.Thumb> _hookedThumbs = new();

    // 递归查找 root 模板树内的全部 Thumb（ScrollViewer 内置纵/横 ScrollBar 的滑块）并订阅拖拽事件。
    private void HookScrollBarThumbs(DependencyObject root)
    {
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.Primitives.Thumb th && _hookedThumbs.Add(th))
            {
                th.DragStarted += ScrollBarThumb_DragStarted;
                th.DragCompleted += ScrollBarThumb_DragCompleted;
            }
            HookScrollBarThumbs(child);
        }
    }

    /// <summary>滚动条滑块拖拽开始。两路入口：内置 ScrollBar Thumb 冒泡（本类自捕）+ 分屏 SyncScrollBar（MainPage 转发）。</summary>
    public void BeginScrollBarDrag()
    {
        // 幂等重置（不做 _isScrollBarDragging 早退）：万一上一次 DragCompleted 因指针捕获丢失
        _scrollDragLastH = _liveH;
        _scrollDragLastV = _liveV;
        _scrollDragLastMoveTicks = Stopwatch.GetTimestamp();
        _scrollDragSettleQueued = false;
        _isScrollBarDragging = true;
        if (_scrollDragSettleTimer == null)
        {
            _scrollDragSettleTimer = DispatcherQueue.CreateTimer();
            _scrollDragSettleTimer.Interval = TimeSpan.FromMilliseconds(64);
            _scrollDragSettleTimer.Tick += ScrollDragSettleTick;
        }
        _scrollDragSettleTimer.Start();
    }

    public void EndScrollBarDrag()
    {
        if (!_isScrollBarDragging) return;
        _isScrollBarDragging = false;
        _scrollDragSettleTimer?.Stop();
        _scrollDragSettleQueued = false;
        QueueVisiblePages();
        InvalidateBoth();
    }

    // 拖拽中静止检测：位移停 ≥ScrollDragSettleDelayMs → 视为慢速/暂停，对当前视口补一次请求；
    private void ScrollDragSettleTick(object? sender, object? e)
    {
        if (!_isScrollBarDragging) { _scrollDragSettleTimer?.Stop(); return; }
        if (_scrollDragLastH != _liveH || _scrollDragLastV != _liveV)
        {
            _scrollDragLastH = _liveH;
            _scrollDragLastV = _liveV;
            _scrollDragLastMoveTicks = Stopwatch.GetTimestamp();
            _scrollDragSettleQueued = false;
            return;
        }
        if (_scrollDragSettleQueued) return;
        double idleMs = (Stopwatch.GetTimestamp() - _scrollDragLastMoveTicks) * 1000.0 / Stopwatch.Frequency;
        if (idleMs >= ScrollDragSettleDelayMs)
        {
            _scrollDragSettleQueued = true;
            QueueVisiblePages();
            InvalidateBoth();
        }
    }

    private void ScrollBarThumb_DragStarted(object? sender, Microsoft.UI.Xaml.Controls.Primitives.DragStartedEventArgs e)
        => BeginScrollBarDrag();

    private void ScrollBarThumb_DragCompleted(object? sender, Microsoft.UI.Xaml.Controls.Primitives.DragCompletedEventArgs e)
        => EndScrollBarDrag();

    private void QueueVisiblePages()
    {
        // 滚动条滑块拖拽飞行期：抑制渲染请求（缓存瓦片照画、视口照常移动，画布由 ViewChanged→
        if (_isScrollBarDragging &&
            (Stopwatch.GetTimestamp() - _scrollDragLastMoveTicks) * 1000.0 / Stopwatch.Frequency < ScrollDragSettleDelayMs)
        {
            return;
        }
        var cache = _renderCache;
        int first, last;
        lock (_lock)
        {
            first = -1; last = -1;
            var pages = _pages;
            for (int i = 0; i < pages.Count; i++)
            {
                if (PageVisibleLocked(i)) { if (first < 0) first = i; last = i; }
            }
        }
        if (cache == null || first < 0) return;

        // 缩放弹簧动画期不发请求：Sumatra 无逐帧动画，zoom 变更即发；Glance 的弹簧每帧改 z，
        if (!_isSmoothZooming && !_isRightZooming)
        {
            for (int i = first; i <= last; i++) cache.RequestRendering(this, i);

            // 预测链：kMaxPredictiveRequests=2（单页行布局，Sumatra 双页模式才是 ±2）。
            int[] pred = new int[2];
            int nPred = 0;
            if (last + 1 < _pages.Count) pred[nPred++] = last + 1;
            if (first - 1 >= 0) pred[nPred++] = first - 1;
            if (nPred > 0) cache.RequestPredictiveRendering(this, last, pred, nPred);

            for (int i = last; i >= first; i--) cache.RequestRendering(this, i);
        }

        _lastRenderActivityTicks = Stopwatch.GetTimestamp();
        EnsureWorkerCacheFlush();
    }

    private void Viewer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (CanvasInputActive)
        {
            HandleCanvasDoubleTap(e.GetPosition(Viewer));
            e.Handled = true;
            return;
        }
        if (AnnotationInputActive) { e.Handled = true; return; }
        // 适应 = 按当前布局主轴适应（纵向=适应宽度 / 横向=适应高度），与 FitWidth 按钮完全一致。
        double fitPercent = FitToViewportPercent;
        if (fitPercent <= 0) return;

        // 只拦"重复派发"，不再拦"动画进行中"。
        if ((DateTime.UtcNow - _lastZoomEndTime).TotalMilliseconds < DoubleTapIgnoreMs
            || (DateTime.UtcNow - _doubleTapStartPending).TotalMilliseconds < DoubleTapIgnoreMs)
        {
            e.Handled = true;
            return;
        }

        float fit = (float)(fitPercent / 100.0);
        // 以【当前实时缩放 _liveZoom】判定 toggle 目标：≈适应宽度 → 去 100%，否则 → 去适应宽度。
        double curZoom = _liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor;
        float target = Math.Abs((float)curZoom - fit) < 0.005f ? 1.0f : fit;

        var pos = e.GetPosition(Viewer);
        e.Handled = true;

        // 同步记录派发时刻：即使下面推迟到下一调度周期才真正启动动画，这段"尚未启动"的极短窗口内
        _doubleTapStartPending = DateTime.UtcNow;
        // 不在事件内同步启动（与双击手势收尾打架 → 轻微振动），推迟一个调度周期。
        DispatcherQueue.TryEnqueue(() => StartSmoothZoom(target * 100.0, pos, reuseAnchor: false));
    }

    public bool IsPanZoomEnabled { get; set; } = true;

    private bool _isDragPanning;
    private bool _isRightZooming;
    private Point _dragStartPos;
    private double _dragStartH;
    private double _dragStartV;
    // 拖拽平移自管显示偏移：直接按鼠标位移计算并即时驱动画布重画，
    private double _panDisplayH;
    private double _panDisplayV;

    // 实时视图镜像：与绘制/滚动/缩放同源，作为持久化快照的权威来源。
    // 故保存快照绝不能读 Viewer.VerticalOffset；这里永远保存最后一次真实交互落点。
    private double _liveH;
    private double _liveV;
    private double _liveZoom = 1.0;

    private void SyncLiveView(double h, double v, double zoom)
    {
        _liveH = h;
        _liveV = v;
        if (zoom > 0) _liveZoom = zoom;
    }

    // 把自管偏移同步给 ScrollViewer（驱动原生滚动条/回显）的每帧定时器。
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _panApplyTimer;
    private double _rightZoomStartY;
    private double _rightZoomStartPercent;
    private double _rightZoomAnchorDocX;     // 锚点的文档坐标（缩放=1 的 DIP）
    private double _rightZoomAnchorDocY;

    // ---------------- 拖拽平移：每帧把自管偏移同步给 ScrollViewer ----------------

    private void EnsurePanApplyTimer()
    {
        if (_panApplyTimer == null)
        {
            _panApplyTimer = DispatcherQueue.CreateTimer();
            _panApplyTimer.Interval = TimeSpan.FromMilliseconds(16);
            _panApplyTimer.Tick += PanApplyTick;
        }
        if (!_panApplyTimer.IsRunning) _panApplyTimer.Start();
    }

    private void StopPanApplyTimer()
    {
        _panApplyTimer?.Stop();
    }

    // 右键拖拽缩放期间把真实视图下发给 ScrollViewer（驱动原生滚动条/外部同步条）的 16ms 定时器。
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _zoomApplyTimer;

    private void EnsureZoomApplyTimer()
    {
        if (_zoomApplyTimer == null)
        {
            _zoomApplyTimer = DispatcherQueue.CreateTimer();
            _zoomApplyTimer.Interval = TimeSpan.FromMilliseconds(16);
            _zoomApplyTimer.Tick += ZoomApplyTick;
        }
        if (!_zoomApplyTimer.IsRunning) _zoomApplyTimer.Start();
    }

    private void StopZoomApplyTimer()
    {
        _zoomApplyTimer?.Stop();
    }

    private void ZoomApplyTick(object? sender, object? e)
    {
        if (!_isSmoothZooming || !_zoomFromRightDrag) { StopZoomApplyTimer(); return; }
        double zp = _zoomAnimCurrentFactor;
        double hp = _zoomAnimCurrentH;
        double vp = _zoomAnimCurrentV;
        if (zp <= 0) return;
        Viewer.ChangeView(hp, vp, (float)zp, disableAnimation: true);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// 每帧把自管显示偏移下发给 ScrollViewer（驱动原生滚动条位置），并接手拖拽期间的缺图预取/页码/同步回显。
    private double _lastPanAppliedH = double.NaN;
    private double _lastPanAppliedV = double.NaN;

    private void PanApplyTick(object? sender, object? e)
    {
        if (!_isDragPanning) { StopPanApplyTimer(); _lastPanAppliedH = _lastPanAppliedV = double.NaN; return; }
        if (_panDisplayH == _lastPanAppliedH && _panDisplayV == _lastPanAppliedV) return;
        _lastPanAppliedH = _panDisplayH;
        _lastPanAppliedV = _panDisplayV;
        // 下发真实偏移（驱动原生滚动条），并接手拖拽期间的预取/回显（ViewChanged 在拖拽中已跳过）。
        // zoom 传 CurrentZoom 而非 Viewer.ZoomFactor，且必要时【显式带上】而不是传 null。
        double z = CurrentZoom;
        float? zArg = Math.Abs(z - Viewer.ZoomFactor) > 1e-4 ? (float)z : (float?)null;
        Viewer.ChangeView(_panDisplayH, _panDisplayV, zArg, disableAnimation: true);
        SyncLiveView(_panDisplayH, _panDisplayV, z);
        QueueVisiblePages();
        NotifyViewState();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Viewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_pages.Count == 0) return;
        _canvasClickEditPending = false;     // 新按下 ⇒ 清上一次的"单击已选中卡进编辑"待定（本次按下重新判定）

        // 【动画期间不拦按下，只有待还原未落定才拦】此时 ChangeView 尚未异步落地，继续分发会读到陈旧视图。
        if (_pendingRestore != null)
        {
            e.Handled = true;
            return;
        }

        var pt = e.GetCurrentPoint(Viewer);

        // 编辑态：右键落在编辑框上 = 要弹【文本右键菜单】（自绘，见 CanvasEditContextMenu），不是右键拖拽缩放。
        // ⚠ 刻意【不】设 e.Handled：WinUI 的右键手势/ContextMenuOpening 与指针按下的 handled 状态有关，
        if (_canvasEditNodeId != null && pt.Properties.IsRightButtonPressed &&
            IsPointInsideVisibleElement(CanvasEditBox, pt.Position))
            return;
        // 菜单开着时的其他按下（菜单内的按下被菜单自己吃掉，到不了这里）⇒ 先收起再继续常规处理。
        HideCanvasEditContextMenu();

        // ── 浮层 chrome 隔离：卡片胶囊 / 连线工具条是叠在画布上的独立控件，必须先把落在它们之上的
        //    按下隔离掉，否则【画布命中链会按"画布几何"】把胶囊背后的卡片边框控件 / 卡片本体 / 连线当成命中
        //    目标（用户实测：整颗胶囊区左键都会落到背后卡片的边框控件上、拖拽直接操作背后卡片）。
        if (IsPointOverCanvasOverlayChrome(pt.Position))
        {
            if (pt.Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                return;
            }
        }

        // 白板编辑中：任何落在编辑框之外的按下都结束编辑（Obsidian 行为）。编辑框与浮动控件
        // 都在覆盖层内，落在其上的按下被它们吃掉、到不了本视图 —— 能到这里的一定是"卡片之外"。
        // ⚠ 例外：命中【当前编辑卡】边框控件的按下（卡片滚动条 / 四角 / 四边中点 / 四边中段）。这些控件的命中带
        if (_canvasEditNodeId != null)
        {
            _canvasPointerPos = pt.Position;
            _canvasPointerValid = true;
            UpdateCanvasNodeHover(pt.Position);
        }
        _canvasEditControlGesture = _canvasEditNodeId != null && PressHitsEditedCardControl(pt.Position);
        if (_canvasEditNodeId != null && !_canvasEditControlGesture) EndCanvasEdit(commit: true);

        if (AnnotationInputActive && pt.Properties.IsLeftButtonPressed)
        {
            StopSmoothZoom();
            if (TryViewportToPage(pt.Position, out int pageIdx, out double px, out double py))
            {
                _isAnnotating = true;
                _annotatingPage = pageIdx;
                this.CapturePointer(e.Pointer);
                StartPenCursorHold();          // 笔画中途停手也保持手势状态（以物理按键为准，避免被 PointerCaptureLost 清掉）
                AnnotationPointerPressed?.Invoke(this,
                    new GpuAnnotationPointerArgs { PageIndex = pageIdx, X = px, Y = py, ViewerPosition = pt.Position });
            }
            e.Handled = true;
            return;
        }

        if (!IsPanZoomEnabled) return;

        CancelPendingRestore(); // 用户开始拖拽平移/右键缩放 → 取消未落定的还原，防止回拽

        // ── 白板模式：左键优先命中卡片 → 选中并准备拖动；未命中卡片则取消选中，照常拖动平移 ──
        if (CanvasInputActive && pt.Properties.IsLeftButtonPressed)
        {
            // 手柄"显形/命中"都依据指针位置：按下瞬间先就地刷新一次（不依赖上次 PointerMoved 的残值）。
            if (_canvasEditNodeId == null)
            {
                _canvasPointerPos = pt.Position;
                _canvasPointerValid = true;
                UpdateCanvasNodeHover(pt.Position);
            }
            // 视口接管键盘焦点：鼠标点击画布不会让 ScrollViewer 自动获焦，而 MainPage 的 Delete/Backspace
            // 处理是【依赖焦点】的 KeyDown 路由（必须穿过 MainPage 视觉树才可达）⇒ 首次打开带卡片的 PDF 时
            // ⚠ 编辑态【不】抢焦点：编辑框必须继续持有键盘焦点，插入光标才照常闪烁（DrawCanvasEditCaret 以
            if (_canvasEditNodeId == null) EnsureViewerFocus();
            // 按住波浪键两次点击建线（跨视口）：命中/未命中都在机制内部决定是否消费，消费了就不进下面的拖拽命中链。
            if (TryHandleCanvasConnArmPress(pt.Position, e)) { e.Handled = true; return; }
            // 滚动条优先：它在卡片内部、意图更明确；命中区仅滚动条矩形（±小容差），
            if (TryHitCanvasScrollBar(pt.Position, out var sbNode) && sbNode != null)
            {
                StopSmoothZoom();
                StopWheelScroll();
                if (sbNode.Id != _selectedCanvasNodeId)
                {
                    // 阅读态：按住滚动条【拖动 = 移动卡片】并进入选中态；只是点一下（不移动）则仅选中。
                    SelectCanvasNode(sbNode.Id);
                    // 直接用滚动条所属卡片自身作拖动对象（不再绕经 TryHitCanvasNode：选中卡片已置顶绘制，
                    if (CanvasViewportToPage(pt.Position, out int dPage, out double dPx, out double dPy))
                    {
                        SeedCanvasDrag(sbNode, dPage, dPx, dPy);
                        // 指针位置就此冻结（拖动期间不再更新）⇒ 必须标记为"不可信"，
                        // 否则这个按下点会被当成活指针去探测手柄：卡片扫过它时凭空显出边缘控件（同 BeginCanvasScrollDrag）。
                        _canvasPointerValid = false;
                    }
                }
                else
                {
                    BeginCanvasScrollDrag(sbNode, pt.Position);
                }
                this.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
            // 命中优先级 = 绘制层级（挡在上面的先命中），严格照抄 Obsidian Canvas。
            //   滚动条 > 四角（缩放）> 四边中点（拉线手柄）> 四边中段（单轴缩放）> 卡片本体（移动/点选）> 连线。
            if (TryHitCanvasCorner(pt.Position, out var rNodeId, out var rCorner) &&
                CanvasViewportToPage(pt.Position, out int rPage, out _, out _))
            {
                StopSmoothZoom();
                StopWheelScroll();
                BeginCanvasResize(rNodeId, rCorner, rPage);
                this.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
            if (TryHitCanvasHandle(pt.Position, out var hNodeId, out var hSide))
            {
                StopSmoothZoom();
                StopWheelScroll();
                BeginCanvasConnection(hNodeId, hSide, pt.Position);
                this.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
            if (TryHitCanvasEdge(pt.Position, out var eNodeId, out var eEdge) &&
                CanvasViewportToPage(pt.Position, out int ePage, out _, out _))
            {
                StopSmoothZoom();
                StopWheelScroll();
                BeginCanvasEdgeResize(eNodeId, eEdge, ePage);
                this.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
            // 卡片本体（含移动 / 点击已选中卡片进编辑）：排在连线之前 ——
            if (TryHitCanvasNode(pt.Position, out var hitNode, out int hitPage, out double hitPx, out double hitPy)
                && hitNode != null)
            {
                StopSmoothZoom();
                StopWheelScroll();
                // Obsidian 行为：点击【已选中】卡片且松手前未拖动 → 进入编辑态（光标落点击点）。
                bool wasSelected = string.Equals(hitNode.Id, _selectedCanvasNodeId, StringComparison.Ordinal);
                _canvasClickEditPending = wasSelected && _canvasEditNodeId == null;
                _canvasClickEditStartVp = pt.Position;
                SelectCanvasNode(hitNode.Id);
                SeedCanvasDrag(hitNode, hitPage, hitPx, hitPy);
                // 同上：位置冻结在按下点 ⇒ 置为不可信，禁止拿它探测手柄（否则卡片移动经过按下点会显出控件）。
                _canvasPointerValid = false;
                this.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
            // 连线命中（最后）：只负责"卡片之外"的空白区。
            if (TryHitCanvasEdge(pt.Position, out var hitEdgeId))
            {
                StopSmoothZoom();
                StopWheelScroll();
                // 按下先挂"待定"：松手未超阈值 = 单击选中；超过阈值 = 拖动改线（不改变原选择态）。
                _canvasEdgePressPending = true;
                _canvasEdgePressEdgeId = hitEdgeId;
                _canvasEdgePressVp = pt.Position;
                this.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
            // 命中链兜底：点中"画布空白"（卡片/连线/控件之外）⇒ 取消选中（随后左键继续拖动平移）。
            SelectCanvasNode(null);
        }

        if (pt.Properties.IsLeftButtonPressed)
        {
            StopSmoothZoom();
            StopWheelScroll(); // 取消进行中的滚轮滚动，避免抢视图
            _isDragPanning = true;
            _dragStartPos = pt.Position;
            // 用权威实时镜像 _live* 播种拖拽起点，而非 Viewer.*——还原未落地/被 CancelPendingRestore
            double curH = _liveZoom > 0 ? _liveH : Viewer.HorizontalOffset;
            double curV = _liveZoom > 0 ? _liveV : Viewer.VerticalOffset;
            _dragStartH = curH;
            _dragStartV = curV;
            _panDisplayH = curH;
            _panDisplayV = curV;
            this.CapturePointer(e.Pointer);
            e.Handled = true;
        }
        else if (pt.Properties.IsRightButtonPressed)
        {
            StopSmoothZoom();
            StopWheelScroll(); // 取消进行中的滚轮滚动，避免抢视图
            _isRightZooming = true;
            _rightZoomStartY = pt.Position.Y;
            // 记录鼠标锚点：按下点对应的文档坐标，整个拖拽期间绕它缩放。
            double zf = _liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor;
            if (zf <= 0) zf = 1.0;
            double curH = _liveZoom > 0 ? _liveH : Viewer.HorizontalOffset;
            double curV = _liveZoom > 0 ? _liveV : Viewer.VerticalOffset;
            _rightZoomStartPercent = zf * 100.0;
            // 居中补偿：内容窄/矮于视口时 ScrollViewer 默认居中呈现，视口坐标→文档坐标必须扣除该
            _vpW = Viewer.ViewportWidth;
            _vpH = Viewer.ViewportHeight;
            if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
            GetCenterOffsets(zf, out double centerX, out double centerY, _vpW, _vpH);
            _rightZoomAnchorDocX = (pt.Position.X - centerX + curH) / zf;
            _rightZoomAnchorDocY = (pt.Position.Y - centerY + curV) / zf;
            // 复用平滑缩放那套锚点机制：按下点即固定不动点，之后每次 PointerMoved 只改弹簧目标。
            // 弹簧真正起跑要等到第一次实际移动（见 PointerMoved），否则"目标=当前"会当帧判定收敛。
            _zAnimAnchor = pt.Position;
            _zAnimContentX = _rightZoomAnchorDocX;
            _zAnimContentY = _rightZoomAnchorDocY;
            // 关键：右键缩放【不捕获】指针。一旦 CapturePointer，OS 在捕获期间会抑制 WM_SETCURSOR，
            StartPenCursorHold();
            e.Handled = true;
        }
    }

    private void Viewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // 命中元素是滚动条(或其子部件)→视为 chrome，交还真实箭头；其余交给下方画笔逻辑。

        // 触发键待接态：引导线动端始终跟手。本机制【不捕获指针】，下面各分支照常跑（仍可翻页、仍可悬停选目标）。
        // 兜底自愈：触发键已松却没收到 KeyUp（焦点被别处抢走，例如点了左侧缩略图跳页）⇒ 立刻取消，
        // 避免"一根虚线代表的状态"悬着不走。判据用【系统按键状态】，不依赖事件是否送到。
        if (_connArmActive)
        {
            if (!IsConnArmTriggerDown())
            {
                CancelCanvasConnArm();
            }
            else
            {
                _connArmCursorVp = e.GetCurrentPoint(Viewer).Position;
                InvalidateOverlayThrottled();
            }
        }

        // 白板模式：记录指针视口坐标（手柄"按接近度显形"依赖它），随后各分支照常处理。
        // ⚠ 手势进行中【不更新】：拖动滚动条 / 缩放 / 连线期间指针位置若跟着变，
        if (_canvasInputActive && !_isCanvasScrollDragging && !_isCanvasResizing &&
            !_isCanvasConnecting && !_isCanvasDragging)
        {
            _canvasPointerPos = e.GetCurrentPoint(Viewer).Position;
            _canvasPointerValid = true;
        }

        // 连线按下待定：超过阈值 → 进入改线；未超 → 继续等松手（单击选中）。
        if (_canvasEdgePressPending)
        {
            var mp = e.GetCurrentPoint(Viewer).Position;
            double mdx = mp.X - _canvasEdgePressVp.X, mdy = mp.Y - _canvasEdgePressVp.Y;
            if (mdx * mdx + mdy * mdy > CanvasEdgeRerouteStartThreshold * CanvasEdgeRerouteStartThreshold)
            {
                var pendEdge = _canvasEdgePressEdgeId;
                _canvasEdgePressPending = false;
                _canvasEdgePressEdgeId = null;
                if (pendEdge != null) BeginCanvasEdgeReroute(pendEdge, mp);
            }
            e.Handled = true;
            return;
        }

        if (_isCanvasScrollDragging)
        {
            UpdateCanvasScrollDrag(e.GetCurrentPoint(Viewer).Position);
            e.Handled = true;
            return;
        }

        if (_isCanvasResizing)
        {
            UpdateCanvasResize(e.GetCurrentPoint(Viewer).Position);
            e.Handled = true;
            return;
        }

        if (_isCanvasConnecting)
        {
            UpdateCanvasConnecting(e.GetCurrentPoint(Viewer).Position);
            e.Handled = true;
            return;
        }

        // ── 白板卡片拖动：按页局部增量更新位置，clamp 到页内（越出边界也只贴边不丢） ──
        if (_isCanvasDragging && _canvasDragNode != null)
        {
            var dpt = e.GetCurrentPoint(Viewer);
            // 单击已选中卡的"进编辑待定"：位移超阈值 → 判定为拖动，撤销待定（松手不再进编辑）。
            if (_canvasClickEditPending)
            {
                double mdx = dpt.Position.X - _canvasClickEditStartVp.X;
                double mdy = dpt.Position.Y - _canvasClickEditStartVp.Y;
                if (mdx * mdx + mdy * mdy > CanvasClickEditMoveThreshold * CanvasClickEditMoveThreshold)
                    _canvasClickEditPending = false;
            }
            if (CanvasViewportToLockedPage(_canvasDragPage, dpt.Position, out double mpx, out double mpy))
            {
                double nx = _canvasDragOrigX + (mpx - _canvasDragStartPx);
                double ny = _canvasDragOrigY + (mpy - _canvasDragStartPy);
                var page = _pages[_canvasDragPage];
                nx = Math.Clamp(nx, 0, Math.Max(0, page.WidthPoints - _canvasDragNode.Width));
                ny = Math.Clamp(ny, 0, Math.Max(0, page.HeightPoints - _canvasDragNode.Height));
                CanvasNodeDragged?.Invoke(this, new CanvasNodeEventArgs
                {
                    NodeId = _canvasDragNode.Id, PageIndex = _canvasDragPage, X = nx, Y = ny
                });
            }
            // 同步重定位浮动控件：与卡片同一事件节拍（不走 Draw 尾部的合并队列）——
            SyncCanvasOverlayNow();
            e.Handled = true;
            return;
        }

        // ── 白板悬停（无任何手势时）：连线靠近变强调色；卡片悬停把边框染色，手柄按指针接近度单独显形 ──
        // 否则拖选指针滑过控件带会触发边框控件悬浮显示与鼠标变化（用户报告）。
        bool canvasTextDragSelecting = _canvasEditNodeId != null &&
            e.GetCurrentPoint(Viewer).Properties.IsLeftButtonPressed;
        // 编辑态下 Viewer 通道收到移动 ⇒ 指针在编辑框之外（卡外或【已被挖除的控件命中带】）。
        if (_canvasEditNodeId != null)
        {
            var editHoverPos = e.GetCurrentPoint(Viewer).Position;
            _canvasPointerPos = editHoverPos;
            _canvasPointerValid = true;
            if (!canvasTextDragSelecting && _canvasPointerOnEditCard)
                _canvasPointerOnEditCard = false;
            if (!AnyCanvasGestureActive) SyncCanvasEditHitCarve(editHoverPos);
        }
        if (CanvasInputActive && !_isRightZooming && !_isDragPanning && !canvasTextDragSelecting)
        {
            var hoverPos = e.GetCurrentPoint(Viewer).Position;
            _canvasPointerPos = hoverPos;       // 供连线手型作用域 25ms 轮询判定（对齐编辑态 _canvasPointerPos 维护）
            _canvasPointerValid = true;
            UpdateCanvasNodeHover(hoverPos);    // 先卡片悬停：下方连线悬浮的"拉线控件优先"判定依赖 _hoverCanvasNodeId
            UpdateCanvasEdgeHover(hoverPos);
            // 手柄（角/边中点/四边中段）悬停时改 Viewer 根光标，使"所有能使用边框控件的情境"
            bool overCard = _selectedCanvasNodeId != null || _hoverCanvasNodeId != null;
            this.ProtectedCursor = overCard ? ResolveCanvasHoverCursor(hoverPos) : null;
            // 手柄"只显指针最接近的那一个"随指针连续变化：在卡片附近移动时需重绘（8ms 节流，
            if (overCard)
                InvalidateOverlayThrottled();
        }

        if (_isAnnotating)
        {
            var pt = e.GetCurrentPoint(Viewer);
            // 整条笔始终按【发起页】换算：坐标可以越出页框（负数 / 超页高都是正常轨迹），
            // 于是同一笔的点串天然连续 —— 跨页、跨页间空隙、越过左右页边，全都只是这条线的延伸，
            // 不需要按页切开、也不需要事后补线；整条随发起页同一套变换绘制，旋转与换布局时更不会拉出假线。
            if (CanvasViewportToLockedPage(_annotatingPage, pt.Position, out double px, out double py))
            {
                AnnotationPointerMoved?.Invoke(this,
                    new GpuAnnotationPointerArgs { PageIndex = _annotatingPage, X = px, Y = py });
            }
            e.Handled = true;
            return;
        }

        if (_isRightZooming)
        {
            // 【自愈】物理按键已全部抬起，却仍处在右键缩放手势里 ⇒ 那次 PointerReleased 被别处吞掉了
            // 以【物理按键】为准立刻收尾：右键缩放不捕获指针，等不到"框架通知"，必须自己校验。
            if (!CursorInterop.AnyMouseButtonDown())
            {
                EndRightZoomGesture();
                e.Handled = true;
                return;
            }
            // 上推放大、下拉缩小。锚点 = 按下时的鼠标位置：该文档点在屏幕上保持不动。
            // 缩放值取"按下点百分比 + 累计位移"的绝对目标，不是逐点直接下发 ChangeView + 整页重光栅。
            var pt = e.GetCurrentPoint(Viewer);
            double deltaY = _rightZoomStartY - pt.Position.Y;
            double raw = (_rightZoomStartPercent + deltaY) / 100.0;
            double target = Math.Clamp(raw, MinZoom, MaxZoom);
            if (raw != target)
            {
                // 【抗积分饱和】本映射是"按下点百分比 + 累计位移"的绝对式：撞到上/下限后继续同向推，
                _rightZoomStartPercent = target * 100.0;
                _rightZoomStartY = pt.Position.Y;
            }

            if (!_isSmoothZooming)
            {
                // 起跑（或中途停手收敛后重新起跑）：从【当前实际缩放】接续，绝不能回到按下时的值。
                double cur = _liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor;
                if (cur <= 0) cur = _rightZoomStartPercent / 100.0;
                // 已在尽头且目标==当前：这是一次【空手势】。起跑只会白白置 _isSmoothZooming、
                if (Math.Abs(target - cur) < 1e-6) { e.Handled = true; return; }
                BeginZoomGesture(cur, target, RightZoomSpringOmega, fromRightDrag: true);
                _isSmoothZooming = true;
                EnsureFramePump();
            }
            _zoomAnimTargetFactor = target;
            _zoomLog.Target = Math.Log(target);
            // 弹簧可能已在"按住停顿"中停稳并停泵（见 StepZoomFrame 收敛分支）：此时 _isSmoothZooming
            EnsureFramePump();

            e.Handled = true;
            return;
        }

        if (!_isDragPanning) return;
        var pos = e.GetCurrentPoint(Viewer).Position;
        // 自管显示偏移：直接按鼠标位移算目标并即时驱动画布重画（零额外帧，跟手）。
        double newH = _dragStartH + (_dragStartPos.X - pos.X);
        double newV = _dragStartV + (_dragStartPos.Y - pos.Y);
        // 夹到合法滚动范围：无水平/垂直滚动条（内容窄于视口）时上界=0 → 偏移锁 0，
        // 内容保持居中，不会因拖拽飞出主视口；有滚动条时也不会越界（与 ScrollViewer 原生行为一致）。
        double z = CurrentZoom;
        GetScrollBounds(z, out double maxH, out double maxV, _vpW, _vpH);
        newH = Math.Clamp(newH, 0, maxH);
        newV = Math.Clamp(newV, 0, maxV);
        _panDisplayH = newH;
        _panDisplayV = newV;
        if (z > 0)
        {
            // 两条轴都更新可见范围（横向布局下 newH 同样驱动选页）。
            double vpW = _vpW;
            double vpH = _vpH;
            lock (_lock)
            {
                _visLeft = newH / z;
                _visRight = (newH + vpW) / z;
                _visTop = newV / z;
                _visBottom = (newV + vpH) / z;
            }
        }
        // 画布按自管偏移即时重画（随鼠标满速跟手）；真实偏移/滚动条交给每帧定时器下发，
        InvalidateBoth();
        EnsurePanApplyTimer();
        e.Handled = true;
    }

    /// <summary>右键拖拽缩放手势结束（松手/移出/捕获丢失）统一收尾。 弹簧仍在推进 → 保证帧泵在跑，由 StepZoomFrame 停稳后收尾 FinishZoom；</summary>
    private void EndRightZoomGesture()
    {
        _isRightZooming = false;
        StopPenCursorHold();
        if (_isSmoothZooming)
        {
            EnsureFramePump(); // 交给收尾分支：终档批落定 + 零变化收尾（见 StepZoomFrame）
        }
        else
        {
            QueueVisiblePages(); // 兜底升级清晰度（弹簧早已收尾但缺最终重光栅）
        }
    }

    private void Viewer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _canvasEditControlGesture = false;
        UnlockCanvasCursor();                // 手势结束 ⇒ 还原系统光标（SPI_SETCURSORS 一次性恢复全部，覆盖滚动/缩放/连线）
        if (_isCanvasScrollDragging)
        {
            // 先清状态再释放捕获（释放会同步触发 CaptureLost，那里也会兜底清一次）。
            _isCanvasScrollDragging = false;
            _canvasScrollDragNodeId = null;
            this.ReleasePointerCapture(e.Pointer);
            // 拖动期间焦点被系统从编辑框夺走（见 CanvasEditBox_LostFocus）⇒ 这里还回去，保持仍在编辑态。
            if (_canvasEditNodeId != null) CanvasEditBox.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        if (_isCanvasResizing)
        {
            // 先取状态再 ReleasePointerCapture（释放会同步触发 CaptureLost 清空字段）。
            var node = _canvasResizeNode;
            int page = _canvasResizePage;
            double ox = _canvasResizeOrigX, oy = _canvasResizeOrigY;
            double ow = _canvasResizeOrigW, oh = _canvasResizeOrigH;
            double nx = node?.X ?? 0, ny = node?.Y ?? 0;
            double nw = node?.Width ?? 0, nh = node?.Height ?? 0;
            CancelCanvasResize();
            this.ReleasePointerCapture(e.Pointer);
            if (node != null)
                CanvasNodeResizeCompleted?.Invoke(this, new CanvasNodeEventArgs
                {
                    NodeId = node.Id, PageIndex = page,
                    X = nx, Y = ny, Width = nw, Height = nh,
                    OrigX = ox, OrigY = oy, OrigWidth = ow, OrigHeight = oh
                });
            // 编辑态用【探出编辑框外的】缩放控件时，按下与拖动期间焦点会落到视口 ⇒ 这里还回编辑框，
            if (_canvasEditNodeId != null) CanvasEditBox.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        // 连线按下待定且未进入改线 ⇒ 这是一次单击：选中该连线（保持原有交互不变）。
        if (_canvasEdgePressPending)
        {
            var clickEdge = _canvasEdgePressEdgeId;
            _canvasEdgePressPending = false;
            _canvasEdgePressEdgeId = null;
            this.ReleasePointerCapture(e.Pointer);
            if (clickEdge != null) SelectCanvasEdge(clickEdge);
            e.Handled = true;
            return;
        }

        if (_isCanvasConnecting)
        {
            if (_connRerouteEdgeId != null)
            {
                // 改线收尾：先取状态再 ReleasePointerCapture（释放会同步触发 CaptureLost 清空字段）。
                var rerouteId = _connRerouteEdgeId;
                bool detachFrom = _connRerouteDetachFrom;
                var fixedNode = _connFromNodeId;
                var rerouteDrop = e.GetCurrentPoint(Viewer).Position;
                _isCanvasConnecting = false;
                _connRerouteEdgeId = null;
                _connFromNodeId = null;
                this.ReleasePointerCapture(e.Pointer);
                CommitCanvasEdgeReroute(rerouteId, detachFrom, fixedNode, rerouteDrop);
                if (_canvasEditNodeId != null) CanvasEditBox.Focus(FocusState.Programmatic);
                e.Handled = true;
                return;
            }
            // 同拖动：必须先取状态再 ReleasePointerCapture（释放会同步触发 CaptureLost 清空字段）。
            var from = _connFromNodeId;
            var fromSide = _connFromSide;
            var dropPos = e.GetCurrentPoint(Viewer).Position;
            _isCanvasConnecting = false;
            _connFromNodeId = null;
            SyncCanvasOverlayNow();   // 连线结束 ⇒ 恢复卡片悬浮胶囊（若仍有卡片选中）
            this.ReleasePointerCapture(e.Pointer);
            InvalidateComposite();
            // ⚠ 目标在【释放点当下重新判定】（预览也是同一套现场派生，故两者必然一致）。
            bool onAnyCard = TryHitCanvasNode(dropPos, out var dropHit, out _, out _, out _) && dropHit != null;
            bool onOtherCard = onAnyCard && !string.Equals(dropHit!.Id, from, StringComparison.Ordinal);
            int dropPage = -1; double dpx = 0, dpy = 0;
            bool dropOnEmptyPage = !onAnyCard &&
                CanvasViewportToPage(dropPos, out dropPage, out dpx, out dpy);
            if (!string.IsNullOrEmpty(from) && onOtherCard)
            {
                CanvasNodeConnectionRequested?.Invoke(this, new CanvasNodeEventArgs
                {
                    NodeId = from, FromSide = fromSide,
                    TargetNodeId = dropHit!.Id, ToSide = ComputeTargetSide(dropHit.Id, dropPos)
                });
            }
            else if (!string.IsNullOrEmpty(from) && dropOnEmptyPage)
            {
                var page = _pages[dropPage];
                double w = CanvasNode.DefaultWidth, h = CanvasNode.DefaultHeight;
                double x = Math.Clamp(dpx - w / 2.0, 0, Math.Max(0, page.WidthPoints - w));
                double y = Math.Clamp(dpy - h / 2.0, 0, Math.Max(0, page.HeightPoints - h));
                CanvasNodeCreateConnectedRequested?.Invoke(this, new CanvasNodeEventArgs
                {
                    NodeId = from, FromSide = fromSide, PageIndex = dropPage, X = x, Y = y
                });
            }
            // 同缩放分支：控件探出编辑框外的连线同样会带走焦点 ⇒ 还回编辑框，保持仍在编辑态。
            if (_canvasEditNodeId != null) CanvasEditBox.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        if (_isCanvasDragging)
        {
            // 必须先取状态再 ReleasePointerCapture：释放捕获会【同步】触发 PointerCaptureLost，
            var node = _canvasDragNode;
            int page = _canvasDragPage;
            double ox = _canvasDragOrigX, oy = _canvasDragOrigY;
            _isCanvasDragging = false;
            _canvasDragNode = null;
            _canvasDragPage = -1;
            // Obsidian 行为收尾：单击已选中卡且全程未拖动（待定仍挂起、且 DoubleTapped 尚未抢先进入编辑）
            bool clickEdit = _canvasClickEditPending && _canvasEditNodeId == null;
            _canvasClickEditPending = false;
            var clickEditVp = _canvasClickEditStartVp;
            this.ReleasePointerCapture(e.Pointer);   // CaptureLost 里 _isCanvasDragging 已为 false → 不会再发一次
            if (node != null)
                CanvasNodeDragCompleted?.Invoke(this, new CanvasNodeEventArgs
                {
                    NodeId = node.Id, PageIndex = page, X = node.X, Y = node.Y,
                    OrigX = ox, OrigY = oy
                });
            if (clickEdit && node != null)
                BeginCanvasEdit(node.Id, clickEditVp);
            // 补一次重绘：双击路径里 BeginCanvasEdit 发生在 DoubleTapped（第二次【按下】）时，此刻
            ScreenCanvas.Invalidate();
            e.Handled = true;
            return;
        }

        if (_isAnnotating)
        {
            var pt = e.GetCurrentPoint(Viewer);
            int page = _annotatingPage;
            _isAnnotating = false;
            _annotatingPage = -1;
            this.ReleasePointerCapture(e.Pointer);
            StopPenCursorHold();
            if (CanvasViewportToLockedPage(page, pt.Position, out double px, out double py))
                AnnotationPointerReleased?.Invoke(this,
                    new GpuAnnotationPointerArgs { PageIndex = page, X = px, Y = py });
            e.Handled = true;
            return;
        }

        if (_isRightZooming)
        {
            // 右键缩放不再捕获指针（见按下处注释），没有捕获需要释放。
            EndRightZoomGesture();
            e.Handled = true;
            return;
        }

        if (_isDragPanning)
        {
            _isDragPanning = false;
            this.ReleasePointerCapture(e.Pointer);
            StopPanApplyTimer();
            // 末帧精确落定真实偏移（驱动原生滚动条/回显）；ViewChanged(IsIntermediate=false) 统一升级清晰度。
            double zEnd = CurrentZoom;
            float? zEndArg = Math.Abs(zEnd - Viewer.ZoomFactor) > 1e-4 ? (float)zEnd : (float?)null;
            Viewer.ChangeView(_panDisplayH, _panDisplayV, zEndArg, disableAnimation: true);
            SyncLiveView(_panDisplayH, _panDisplayV, zEnd);
            QueueVisiblePages(); // 兜底升级清晰度（万一 ChangeView 未触发 ViewChanged）
            e.Handled = true;
        }
    }

    private void Viewer_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isDragPanning = false;
        _isRightZooming = false;
        _canvasEditControlGesture = false;   // 捕获丢失 ⇒ "控件交互"窗口收尾（防标志残留导致后续失焦被误判为控件交互）
        _isCanvasScrollDragging = false;
        _canvasScrollDragNodeId = null;
        UnlockCanvasCursor();                       // 捕获丢失 ⇒ 解锁画布手势光标（覆盖滚动/连线，恢复框架默认）
        if (_isCanvasResizing && _canvasResizeNode != null)
        {
            var rn = _canvasResizeNode;
            int rp = _canvasResizePage;
            CanvasNodeResizeCompleted?.Invoke(this, new CanvasNodeEventArgs
            {
                NodeId = rn.Id, PageIndex = rp,
                X = rn.X, Y = rn.Y, Width = rn.Width, Height = rn.Height,
                OrigX = _canvasResizeOrigX, OrigY = _canvasResizeOrigY,
                OrigWidth = _canvasResizeOrigW, OrigHeight = _canvasResizeOrigH
            });
        }
        CancelCanvasResize();
        _canvasEdgePressPending = false;
        _canvasEdgePressEdgeId = null;
        if (_isCanvasConnecting)
        {
            _isCanvasConnecting = false;
            _connRerouteEdgeId = null;
            _connFromNodeId = null;
            InvalidateComposite();
            SyncCanvasOverlayNow();   // 系统打断连线（失焦等）⇒ 恢复胶囊
        }
        // 白板卡片拖动被系统打断：位置已实时写回模型 → 照常发完成事件收尾（MainPage 据此入历史）。
        _canvasClickEditPending = false;
        if (_isCanvasDragging && _canvasDragNode != null)
        {
            var node = _canvasDragNode;
            int page = _canvasDragPage;
            CanvasNodeDragCompleted?.Invoke(this, new CanvasNodeEventArgs
            {
                NodeId = node.Id, PageIndex = page, X = node.X, Y = node.Y,
                OrigX = _canvasDragOrigX, OrigY = _canvasDragOrigY
            });
        }
        _isCanvasDragging = false;
        _canvasDragNode = null;
        _canvasDragPage = -1;
        StopWheelScroll();
        StopPanApplyTimer();

        // 捕获可能是被 ScrollViewer / 系统"抢走"的，而用户的手指还按在鼠标键上 ——
        if (!CursorInterop.AnyMouseButtonDown())
        {
            StopPenCursorHold();
        }
        if (_isAnnotating)
        {
            int page = _annotatingPage;
            _isAnnotating = false;
            _annotatingPage = -1;
            AnnotationPointerReleased?.Invoke(this,
                new GpuAnnotationPointerArgs { PageIndex = page, X = 0, Y = 0 });
        }
    }

    /// <summary>指针离开本视图（窗口外/标题栏/非子元素区域）时收尾手势。当前主要用于右键拖拽缩放： 该手势刻意不捕获指针，指针移出后 PointerMoved 停发、手势无法自愈，必须在此主动终止，</summary>
    private void Viewer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 左键拖拽平移对指针做了 CapturePointer，离开窗口也能收到事件、由 PointerCaptureLost 收尾，无需在此处理。
        if (_hoverCanvasNodeId != null || _hoverCanvasEdgeId != null || _canvasPointerValid)
        {
            _hoverCanvasNodeId = null;
            _hoverCanvasEdgeId = null;
            _canvasPointerValid = false;
            InvalidateOverlayThrottled();
        }
        if (_isRightZooming)
        {
            EndRightZoomGesture();
            e.Handled = true;
        }
    }

}
