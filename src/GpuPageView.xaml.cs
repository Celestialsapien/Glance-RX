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
using GlanceRX.Services;
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

/// <summary>
/// 页面连续布局方向。两种模式【都是连续布局】（不按页切分、不做整页翻页），
/// 区别只在「主滚动轴」：纵向沿 Y 串页，横向沿 X 串页。
/// 除滚轮轴向与页间距外，缩放/拖拽/标注/旋转等交互逻辑两种模式完全一致。
/// </summary>
public enum PageLayoutMode
{
    /// <summary>纵向单页连续：页自上而下排列，在文档内水平居中（默认）。</summary>
    VerticalContinuous = 0,

    /// <summary>横向单页连续：页自左而右排列，在文档内垂直居中；页间距更紧凑，适合书本/杂志。</summary>
    HorizontalContinuous = 1,
}

/// <summary>
/// 纵向布局下的"列数/排版"轴，与 PageLayoutMode（横/纵主轴）相互独立。
/// 仅影响【纵向】布局：横向胶片条模式忽略此值（始终单列）。
/// 移植自 sumatrapdf-master/src/DocumentLayout.cpp 的 CollectFacingRows / RelayoutFacingWithSpreads
/// （本应用无 spread，故省略 spread 分支）。
/// </summary>
public enum PageArrangement
{
    /// <summary>单页视图：纵向单列（默认，与原单列行为完全一致）。</summary>
    Single = 0,
    /// <summary>双页视图（facing）：页配对为 (1,2)(3,4)… 左右两列并排。</summary>
    Facing = 1,
    /// <summary>书籍视图（book）：封面页 1 独占右栏，其后 (2,3)(4,5)… 左右两列。</summary>
    Book = 2,
}

/// <summary>
/// 标注指针事件参数：视口坐标已换算为【页局部坐标，PDF 点单位，未旋转】，
/// 与旧路径 Canvas_Pointer* 的坐标空间一致 → MainPage 的标注数据逻辑可原样复用。
/// </summary>
public sealed class GpuAnnotationPointerArgs
{
    public int PageIndex { get; init; }
    /// <summary>页局部 X（PDF 点，未旋转坐标系，原点=页左上角）。</summary>
    public double X { get; init; }
    /// <summary>页局部 Y（PDF 点，未旋转坐标系，原点=页左上角）。</summary>
    public double Y { get; init; }
    /// <summary>按下位置（GpuPageView 视口坐标），供便签模式下直接定位编辑 Flyout。</summary>
    public Point ViewerPosition { get; init; }
}

/// <summary>
/// GPU 阅读器（Phase 2.3：视口画布架构 —— 内存硬上限）。
///
/// 之前"整文档一块 CanvasVirtualControl"的教训：虚拟表面对画过的分块只标脏、不释放分配，
/// Invalidate 远处区域无法回收显存 → 滚完全本必然爬到 文档总面积×DPI×4B（实测 ~950MB）。
///
/// 本架构：画布只有【视口】那么大（CanvasControl，~25MB 固定），每次滚动/缩放把
/// LRU 缓存的页位图按当前偏移画上去（画 2-3 张 GPU 位图 = 微秒级，60fps 无压力）。
/// ScrollViewer 只承载一个透明占位元素提供滚动条/惯性/缩放手势。
///
/// 内存模型（与滚动距离、文档长度无关）：
///   视口画布(固定 ~25MB) + 页位图 LRU(6 页封顶, 4000px 长边上限, ~288MB) + 像素池(~64MB封顶) ≈ 稳定 500-700MB；滚动停下后空闲回收可回落峰值。
/// </summary>
public sealed partial class GpuPageView : UserControl, GlanceRX.Rendering.ITileRenderHost
{
    // 横向连续布局的页间距(DIP)：书本/杂志横向串页时相邻页应更"贴"，
    // 视觉上接近跨页装订；沿用纵向的 16 会把左右两页拉开、破坏跨页阅读的连贯感。
    // ---------------- Sumatra RenderCache 瓦片渲染 ----------------
    // 渲染与内存管理 1:1 走 sumatrapdf-master/src/RenderCache.{h,cpp}（TileRenderCache）：
    // 瓦片按视口渲染（Paint BFS）、maxTileSize=屏幕尺寸、FreeNotVisible 按视口释放、
    // 队列 LIFO cap 8、惰性线程 ≤min(8,cores)、预测链一次一页。本视图 = DisplayModel 对应物，
    // MuPDF 会累积已解析的页数据，空闲巡检(EnsureWorkerCacheFlush)按时间冷却重载文档以清空该缓存。

    public PdfRenderService? RenderService { get; private set; }

    /// <summary>只读快照：加载时整体替换，后台线程遍历安全。</summary>
    private IReadOnlyList<GpuPageInfo> _pages = Array.Empty<GpuPageInfo>();

    private readonly object _lock = new();

    // 视口状态（UI 线程写、后台读，_lock 保护）
    // 可见区域按【四边】记录（文档坐标 DIP）：纵向布局主轴是 Top/Bottom，横向布局主轴是 Left/Right，
    // 另一轴作交叉轴裁剪。两轴齐备后，剔除/预取/当前页判定只需按当前主轴取用即可。
    private double _visTop;
    private double _visBottom = 4000;
    private double _visLeft;
    private double _visRight = 4000;
    private double _zoom = 1.0;
    private double _dpiScale = 1.0;

    // 滚动方向（预测链/可见页请求排序沿用）
    private double _lastDirLead = double.NaN; // 上次主轴 lead，用于判滚动方向
    private int _scrollDir;                    // +1=向下/向右前进, -1=后退, 0=未知

    private bool _didInitialFit;
    private int _docGeneration; // 换文档时 +1，丢弃旧文档的迟到渲染结果

    // ================= 统一运动引擎（vsync 帧泵 + 临界阻尼弹簧） =================
    //
    // 旧实现有两个结构性病根，任何参数微调都治不好：
    //
    //  ① 帧时钟错了。滚动/缩放/拖拽各自开一个 DispatcherQueueTimer(16ms) 当心跳。
    //     这个心跳与合成器的 vsync【既不同相也不同频】(60Hz=16.67ms / 120Hz=8.33ms)，
    //     于是"算出位置的时刻"和"这一帧真正上屏的时刻"每帧错开的量都不一样：
    //     即便 dt 归一化算得再准，屏幕上呈现的位移序列依旧是抖的。更致命的是 16 与 16.67
    //     的拍频——每隔十几帧必然出现一次"两个 tick 挤进同一帧(丢一个位置)"或
    //     "一帧内没有 tick(位置停一拍)"。密集短距离滚动时这种丢帧/重帧最密集，
    //     就是用户说的"很强的跳跃感"。
    //
    //  ② 运动模型不连续。滚轮用 lerp 0.6/帧（时间常数≈17ms，几乎等于瞬移），
    //     每来一个滚轮档速度就从 0 猛冲再急停，速度曲线是一串锯齿；缩放用固定 220ms
    //     的 smoothstep，中途重新触发必须重启时钟、速度在接缝处跳变。
    //
    // 现在统一成：
    //  ① 帧泵 = Win2D 交换链自身的 vsync（不是 CompositionTarget.Rendering）。模拟步进放在
    //     ScreenCanvas_Draw 里、与上屏同帧完成，循环由 Draw 末尾的自持续 Invalidate 维持。
    //     两个 vsync 队列相位脱节是 120Hz 屏 judder 的真因；合并后 60/120/144Hz 都按真实刷新率步进、且当帧即上屏。
    //  ② 运动模型 = 临界阻尼弹簧（解析解积分，任意 dt 稳定；阻尼比=1 → 最快收敛且绝不过冲）。
    //     新输入只改目标、不打断当前速度 → 连续滚动是一条光滑曲线而不是一段段重启的动画。
    //     缩放在 log(zoom) 空间做弹簧：人眼对缩放的感知是【等比】的，线性插值会让
    //     放大显得"先猛后爬"、缩小反过来；log 空间下放缩完全对称、速率均匀。
    //
    // 参数即手感，只需调这三个 omega（rad/s，99% 收敛时长 ≈ 6.6/omega）。
    // 滚轮位移系数：MouseWheelDelta 前推为 +120，而"前推 = 往回看"对应【偏移减小】，故取负号。
    // 幅度 3.0 → 一档 ≈ 360px（约视口 1/3，滚轮更跟手、减少"一格一格"的颗粒感；想调只改这里：
    // 2.0≈240px 偏稳、4.0≈480px 更奔放）。弹簧在更长行程上能展开连续曲线，短距微跳的颗粒感随之消失。

    private Spring1D _scrollH, _scrollV; // 滚动：Target=累加终点，Value=当前显示偏移
    private Spring1D _zoomLog;           // 缩放：在 log(zoom) 空间运动

    // 收敛阈值 —— 判据是"屏幕上还看不看得见"，不是"数学上收敛到第几位"。
    //
    // 临界阻尼的尾巴是 (1+ωt)·e^(-ωt)：从 99% 走到 99.99% 还要再花掉近 40% 的总时长，
    // 而这段位移在屏幕上不到 1px、肉眼完全无感。旧阈值(0.25px / log 1e-4 ≈ 0.01% 倍率)
    // 正卡在这条尾巴的末梢：双击缩放数学上要 ~680ms 才判定落定，但 ~370ms 后画面就已经不动了
    // —— 中间 300ms 是"看着停了、其实动画还在跑"的死区，也就是所谓的拖尾。
    //
    // 现在按"残差 ≤ 约 1~2 屏幕像素"取阈值，落在这条线以内就 Snap 到目标一帧补齐（不可察觉）：
    // 缩放 log 残差 2e-3 → 1600px 宽视口上约 1.6px 位移。双击缩放落定 ~440ms（省 240ms）、
    // 滚轮 ~375ms（省 75ms），观感（omega 不变）一模一样，但动画"真正结束"提前了。

    // ---------------- 帧泵 ----------------
    private bool _framePumpHooked;                 // 动画循环是否活动（Draw 自持续帧，而非 CompositionTarget.Rendering 订阅）
    private long _lastDrawTicks;                   // 上一帧 Draw 的真实时间戳(Stopwatch)，用于算模拟 dt
    private long _lastFrameTicks;                  // Stopwatch 时间戳，供看门狗判活
    private DispatcherTimer? _framePumpWatchdog;   // Win2D 自身停帧(窗口被遮挡/最小化)时兜底收尾

    // ---------------- 缩放状态（字段名沿用，供文件内既有逻辑复用） ----------------
    private bool _isSmoothZooming;
    private double _zoomOmega = ZoomSpringOmega;   // 当前这段缩放手势用的弹簧硬度
    private bool _zoomFromRightDrag;               // 本段缩放由右键拖拽驱动（目标随鼠标持续改写）
    private double _zoomGestureStartZoom = 1.0;    // 手势起点缩放，用于判定"这是放大还是缩小"
    private bool _zoomAnimCommitting; // ChangeView 异步落地瞬间保用提交值，避免闪回缩放前画面
    // 最近一次提交给 ScrollViewer 的目标视图。用于判定"提交是否真的落到位"——
    // 放大时 ChangeView 常分两步落地（先换 zoom、extent 下一轮布局才变大），中间那次
    // ViewChanged 回吐的 Offset 是被【旧 extent】夹断的值，绝不能当作权威视图采信。
    private double _committedH, _committedV, _committedZoom;
    // 两个计数器必须分开：等待 zoom 落地是"什么都不做只是等"，补发偏移才是"真的又发了一次请求"。
    // 混用一个计数器会让等待轮次迅速吃光补发配额，偏移永远推不到位。
    private int _commitNudges;                        // 已补发偏移次数
    private long _commitStartTicks;                   // 本次提交起始刻度，用于墙钟兜底(见 TryAdvanceCommit)
    private bool _commitWatchdogQueued;               // 防止连续提交排出多条并行看门狗链
    private double _zAnimContentX, _zAnimContentY; // 锚点对应文档坐标(缩放=1 DIP)，一段手势内固定
    private Point _zAnimAnchor;                    // 视口坐标不动点
    private double _zoomAnimTargetFactor, _zoomAnimTargetH, _zoomAnimTargetV;
    private double _zoomAnimCurrentFactor, _zoomAnimCurrentH, _zoomAnimCurrentV;

    // 画布视口快照：偏移/居中量必须与【同一时刻的视口尺寸】配套，否则滚动条出现/消失
    // （缩放落定瞬间视口变化）会让居中量跳变 → 起止闪一帧"同一页内容、位置错误"。
    // 手势期冻结（StartSmoothZoom 捕获），落定时用预测的后落地视口重解目标偏移，闲置期随 _live* 同步。
    private double _vpW = 1000, _vpH = 800;

    private DateTime _lastZoomEndTime = DateTime.MinValue;
    private DateTime _doubleTapStartPending = DateTime.MinValue;
    // 双击去重窗口(ms)：只用来吃掉手势识别器的重复派发。
    // 弹簧支持"中途改目标且速度连续"，所以动画进行中的双击不再需要一律丢弃 ——
    // 它会平滑反向，比旧的"整段动画期间无响应"跟手得多。

    // ---------------- 滚动状态 ----------------
    private bool _isWheelScrolling;

    // ---------------- 对外操作 API（供 MainPage 工具栏/缩略图/页码框调用） ----------------

    private int _currentPage = -1;      // 当前页（与视口重叠最大的页）
    private double _lastZoomNotified = -1;

    /// <summary>滚动导致当前页变化时触发（参数 = 0-based 页索引）。UI 线程回调。</summary>
    public event EventHandler<int>? CurrentPageChanged;

    /// <summary>缩放百分比变化时触发（参数 = 百分比，如 100.0）。UI 线程回调。</summary>
    public event EventHandler<double>? ZoomPercentChanged;

    /// <summary>视口滚动/缩放导致可见区域变化时触发（供 MainPage 同步滚动条等消费）。UI 线程回调。</summary>
    public event EventHandler? ViewportChanged;

    /// <summary>当前页索引（0-based）。未加载时为 0。</summary>
    public int CurrentPageIndex => Math.Max(0, _currentPage);

    /// <summary>总页数。</summary>
    public int PageCount => _pages.Count;

    /// <summary>页尺寸信息（供 MainPage 构建缩略图占位等复用，避免重复 FFI 取尺寸）。</summary>
    public IReadOnlyList<GpuPageInfo> PageInfos => _pages;

    // ---------------- 布局方向（纵向 / 横向 连续） ----------------

    private PageLayoutMode _layoutMode = PageLayoutMode.VerticalContinuous;

    /// <summary>纵向布局下的列数/排版（单页 / 双页 / 书籍），与 _layoutMode（横纵主轴）轴独立。
    /// 横向胶片条模式忽略此值（始终单列）。Arrangement 变化时复用 _layoutSwitch* 稳定窗口与
    /// ReanchorAfterLayoutChange 重锚。</summary>
    private PageArrangement _arrangement = PageArrangement.Single;

    /// <summary>
    /// 布局切换的"稳定窗口"。重排会改 Spacer 尺寸，ScrollViewer 的可滚动范围要等下一轮布局才刷新，
    /// 期间 ChangeView 的偏移可能被旧 extent 夹断、随后 ViewChanged 同步回读一个被夹断的偏移（多为 0）——
    /// 若据此覆盖 Reanchor 已播种的正确 _live*/_vis*，会触发"页码/侧边栏跳到 1"且视图不动。
    /// 此窗口内 ViewChanged 一律不读 Viewer.*（保留 Reanchor 的权威镜像），直到 Low 优先级收尾锚定完成。
    /// </summary>
    private bool _layoutSwitchInFlight;

    /// <summary>布局切换的目标页（切换前视图所在页）。窗口内页码保持此处，收尾据此钉死。</summary>
    private int _layoutSwitchAnchor = -1;

    /// <summary>布局切换落定期锚定的目标偏移/缩放（Reanchor 播种的 ChangeView 目标）。</summary>
    private double _layoutTargetH, _layoutTargetV, _layoutTargetZoom;

    /// <summary>布局切换启动时刻（看门狗兜底判超时用）。</summary>
    private long _layoutSwitchStartTicks;

    /// <summary>布局切换看门狗：万一 ScrollViewer 目标被极端夹断且再无 ViewChanged，兜底强制收尾。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _layoutWatchdog;

    /// <summary>宿主视口尺寸变化（侧边栏挤开）前的"阅读位置不变量"快照是否有效；见 CaptureHostResizeAnchor。</summary>
    private bool _hostResizeCaptureValid;

    /// <summary>快照时的缩放（重锚的等比基准）。</summary>
    private double _hostResizeZ0;

    /// <summary>快照时的视口尺寸（新旧比例的"旧"端）。缩放比**恒取 vpW** —— 两种布局都锁左右边线
    ///（见 <see cref="BeginHostResizeReanchor"/> 的"三条不动边线"）。</summary>
    private double _hostResizeVpW0, _hostResizeVpH0;

    /// <summary>快照时视口【左缘】切到的文档 X —— 三条不动边线之「左」。</summary>
    private double _hostResizeDocLeftX;

    /// <summary>快照时视口【上缘】切到的文档 Y —— 三条不动边线之「上」。
    /// 原名"竖直中心"（交叉轴中心），09-23 改为上缘：语义由"上下对称展开"改为
    /// **上边钉住、只让下边露出更多内容**。</summary>
    private double _hostResizeDocTopY;

    /// <summary>
    /// 当前连续布局方向。切换时：重排布局 → 若原本处于"适应"缩放则换算为新主轴的适应值
    /// （否则保留用户手动缩放）→ 锚定切换前的当前页，使视口不飘走。
    /// </summary>
    public PageLayoutMode LayoutMode
    {
        get => _layoutMode;
        set
        {
            if (_layoutMode == value) return;

            int anchor = CurrentPageIndex;
            // 【布局切换稳定窗口】从此刻起 ViewChanged 不读 Viewer.*（见字段注释），
            // 直到渲染/布局完全落幕（含滚动条出现消失、extent 刷新的后续回读事件）后才关闭，
            // 防止被夹断偏移污染正确镜像 → 页码/侧边栏瞬跳 1 而视图不动。
            _layoutSwitchInFlight = true;
            _layoutSwitchAnchor = anchor;
            _layoutSwitchStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            StopLayoutSwitchWatchdog();
            // 【视口中心保持】切换前记录"视口中心在 anchor 页内的局部偏移 (dx,dy)"：
            // 布局切换会重排所有页的 LayoutRect（页沿主轴串接位置互换），但页内容不变——
            // 切换后把视口中心对齐到"新 LayoutRect 的同一页内位置"，而不是只锚定页首
            // （否则视口中心会跳到当页左上角，用户正在看的位置丢失）。
            double zOld = (_liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor);
            if (zOld <= 0) zOld = 1.0;
            double vpW = _vpW, vpH = _vpH;
            double cx = _liveH / zOld + vpW / 2.0 / zOld;   // 当前视口中心的文档 X
            double cy = _liveV / zOld + vpH / 2.0 / zOld;   // 当前视口中心的文档 Y
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
            // 偏移可能被旧 extent 夹断。这里排一个 Low 重试幂等重锚一次；稳定窗口【不】在此按时间关闭——
            // 改由 Viewer_ViewChanged 在确认 ScrollViewer 真实到达目标偏移时才关闭（见窗口分支），
            // 并配看门狗定时器兜底，杜绝"页码/侧边栏瞬跳 1 而视图不动"（旧双 Low 时序无法跨过
            // 滚动范围刷新的后续回读事件，收尾后又一条 ViewChanged 把夹断偏移当权威写进 _vis*）。
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_pages.Count == 0 || _layoutMode != value || !_layoutSwitchInFlight) return;
                if (_layoutSwitchAnchor != anchor) return; // 已被更新的切换取代
                ReanchorAfterLayoutChange(anchor, newZoomPercent, pageLocalX, pageLocalY);
            });
            StartLayoutSwitchWatchdog();
        }
    }

    /// <summary>
    /// 纵向布局下的列数/排版（单页 / 双页 / 书籍），与 LayoutMode（横纵主轴）轴独立。
    /// 切换时：重排布局 → 若原本处于"适应"缩放则按新列宽重算适应值（否则保留用户手动缩放）
    /// → 锚定切换前的当前页，使视口不飘走。复用布局切换稳定窗口机制。
    /// </summary>
    public event EventHandler<PageArrangement>? ArrangementChanged;

    /// <summary>
    /// 纵向布局下的列数/排版。设置时复用 _layoutSwitch* 稳定窗口与 ReanchorAfterLayoutChange 重锚，
    /// 行为对齐 LayoutMode setter。横向胶片条模式忽略此值（RecomputeLayout 的横向分支不读它）。
    /// </summary>
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

    /// <summary>启动布局切换看门狗：若落定期内 ScrollViewer 偏移始终被钳制在非目标值（极端夹断），
    /// 定时器到期后强制按真实落点收尾，避免稳定窗口永久悬挂、页码永不刷新。</summary>
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
        NotifyViewState(); // 页码按真落点重算 + 缩放回显
    }

    /// <summary>布局方向变化时触发（供 MainPage 更新按钮图标/提示）。UI 线程回调。</summary>
    public event EventHandler<PageLayoutMode>? LayoutModeChanged;

    /// <summary>是否为横向连续布局（主滚动轴 = 水平）。</summary>
    public bool IsHorizontalLayout => _layoutMode == PageLayoutMode.HorizontalContinuous;


    /// <summary>
    /// 布局重排后把视图落到「指定缩放 + 指定页内位置对齐视口中心」，交叉轴同理由页内偏移决定
    /// （内容窄/矮于视口时 ScrollViewer 会自动居中呈现）。
    /// pageLocalX/pageLocalY：anchor 页内的局部偏移（切换前视口中心在该页内的位置）——
    /// 切换后视口中心对齐"新 LayoutRect 的同一页内位置"，而非页首（页首会让视口中心跳到当页左上角）。
    /// 不读 Viewer.*（其在 ChangeView 落地前是旧值），全部按目标值播种实时镜像与可见范围。
    /// </summary>
    private void ReanchorAfterLayoutChange(int anchor, double zoomPercent, double pageLocalX, double pageLocalY)
    {
        var pages = _pages;
        if (pages.Count == 0) return;
        if (anchor < 0 || anchor >= pages.Count) anchor = 0;

        float z = (float)Math.Clamp(zoomPercent / 100.0, MinZoom, MaxZoom);
        double vpW = _vpW, vpH = _vpH;
        if (vpW <= 0 || vpH <= 0) { vpW = 1000; vpH = 800; }
        // 目标视口中心的文档坐标 = 新页位置 + 切换前的页内偏移
        double cx = pages[anchor].LayoutRect.X + pageLocalX;
        double cy = pages[anchor].LayoutRect.Y + pageLocalY;
        // 【两轴都按中心对齐】只设主轴会丢失交叉轴的页内偏移（纵向转横向时 v 被置 0 → 跳到页顶端；
        // 横向转纵向时 h 被置 0 → 跳到页左端）。两轴都用"页内偏移×z − 视口/2"计算；
        // 内容窄于视口时 ScrollViewer 会把交叉轴偏移夹到 0 并自动居中呈现，行为不变。
        double h = Math.Max(0, cx * z - vpW / 2.0);
        double v = Math.Max(0, cy * z - vpH / 2.0);

        // 【布局切换防夹断】RecomputeLayout 已改 Spacer 尺寸，但 ScrollViewer 的可滚动范围要等
        // 下一轮布局才刷新。若立即 ChangeView 大偏移（如横向→纵向锚定 500 页，v≈42万px），偏移会被
        // 旧 extent（横向下纵向几乎不超高，maxV≈0）当场夹断 → 视口跳回首页；原 Low 优先级再锚定
        // 若仍赶在 extent 就绪前执行则再无重试 → "从横向转回纵向有时跳回首页"。这里先强制布局
        // 刷新 extent，再一次性原子落定缩放+偏移，首次锚定即成功（Low 兜底幂等无害）。
        try { Viewer.UpdateLayout(); } catch { }
        // 记录本次播种的目标（供布局切换稳定窗口判定"真实到达"）。布局再布局可能微调吊平，
        // 故目标在每次 Reanchor（含 Low 重试）都刷新为最新播种值。
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

    /// <summary>跳到指定页（页首对齐视口主轴起点，保留半个页间距）。两种布局分别对齐 X / Y。</summary>
    public void GoToPage(int index)
    {
        var pages = _pages;
        if (index < 0 || index >= pages.Count) return;
        // 缩放动画进行中收到 Home/End 直接跳页：先落定当前插值态，避免与缩放引擎抢视图
        // （交互第一原则：用户随时可打断动画）。
        if (_isSmoothZooming) StopSmoothZoom();
        double z = _liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor;
        if (z <= 0) z = 1.0;
        if (IsHorizontalLayout)
        {
            // 末列保护（横向布局）：最后一列的【右缘】永远贴着视口右端（与纵向末页同理），
            // 消除小缩放下该列短于视口时"先按列首对齐、再被排版/交互 re-clamp 到末端"的一次瞬移。
            double targetX = index == pages.Count - 1
                ? EndScrollOffset(horizontal: true, z)
                : Math.Max(0, (pages[index].LayoutRect.Left - PageGapHorizontal / 2.0) * z);
            // 直接跳：必须把滚动弹簧权威镜像同步到新位置（并同步 _live*），否则后续 GoToPageSmooth
            // 经 EnsureScrollSprings 把弹簧复位到陈旧 _live 值，弹簧 Value 仍是旧位置被帧泵回写，
            // 视图先弹回旧页再动画过去（Home/End 后首按 PgDn/PgUp 异常 up/down 一次的根因）。
            _scrollH.Reset(targetX);
            _scrollV.Reset(_liveV);
            SyncLiveView(targetX, _liveV, z);
            // 缩放动画被打断后 StopSmoothZoom 可能开了提交窗；关掉它，否则 GetViewState 会返回缩放插值态
            // 而非刚跳到的首页/末页，下一帧画布闪/跳回缩放前位置。
            AbandonZoomCommitWindow();
            // 带上 z（非 null）：缩放被打断后其 zoom 提交可能尚未落地，null 会让偏移被旧 extent 夹断、
            // 随后缩放落地把视图拽回缩放停止处而非目标页。显式 z 让缩放+偏移原子落地。
            Viewer.ChangeView(targetX, null, (float)z, disableAnimation: true);
            // 【大距离跳转加速】ChangeView 是异步落地，目标页的排队渲染若只依赖 ViewChanged 回吐，
            // 会白白空等一帧以上（且 _zoom/_vis* 镜像滞后于目标值）。这里立即按目标播种可见范围并
            // 排队渲染——画布按 _live* 绘制（已同步目标），worker 按 _zoom 算分辨率（已同步），
            // 渲染立即启动；随后 ViewChanged 落地时窗口相同、缓存命中判定直接跳过，不产生中间态渲染。
            SetVisibleRangeFor(targetX, _liveV, z);
            QueueVisiblePages();
            InvalidateBoth();
        }
        else
        {
            // 末页保护：无论缩放倍率，最后一页的【页尾】永远贴着视口底部。小缩放下整页短于视口，
            // 若按页首对齐会留空隙、且随后被排版/交互 re-clamp 到文档末端，形成一次瞬移；直接贴末端消除跳变。
            // 较大缩放下整页高于视口时 targetY 本就应被夹到 maxY，本行与之结果一致（无回归）。
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

    /// <summary>方向键视口导航：按一下等同于对应方向滚轮滚一格，走统一弹簧引擎（与 Ctrl+滚轮同源）。</summary>
    public void ScrollByArrow(Windows.System.VirtualKey key)
    {
        double notch = 120.0 * WheelScrollFactor; // 一格 = 一个鼠标滚轮 notch(120)
        // 符号约定与滚轮处理器保持一致（WinUI MouseWheelDelta 正值=上滚/远离用户，负值=下滚/朝向用户）：
        // 滚轮下滚走 AddWheelScroll(0, 负)，方向键据此镜像，避免上下左右反转。
        switch (key)
        {
            case Windows.System.VirtualKey.Up:    AddWheelScroll(0,  notch); break;
            case Windows.System.VirtualKey.Down:  AddWheelScroll(0, -notch); break;
            case Windows.System.VirtualKey.Left:  AddWheelScroll( notch, 0); break;
            case Windows.System.VirtualKey.Right: AddWheelScroll(-notch, 0); break;
        }
    }

    /// <summary>空格：下翻一个视口（视口高度的弹簧滚动，与缩放无关）。
    /// 一次 = 当前视口正下方那一屏；连点无缝接续；到末屏由 AddWheelScroll 的弹簧自夹到底。</summary>
    public void ScrollViewportDown()
    {
        if (_vpH <= 0) return;
        // 下滚 = 正 dv（与 ScrollByArrow 的 Down 同向，因 WheelScrollFactor 为负，Down 实际传 +|notch|）。
        // 步长 = 一个视口高度(_vpH，屏幕 DIP)：滚动偏移与视口同单位，故一次恰为一整屏，与缩放无关。
        AddWheelScroll(0, _vpH);
    }

    /// <summary>平滑翻到指定页（弹簧引擎动画过去）；越界忽略。供 PageDown/PageUp 使用。</summary>
    public void GoToPageSmooth(int index)
    {
        var pages = _pages;
        if (index < 0 || index >= pages.Count) return;
        // 缩放动画进行中收到平滑翻页：先【落定到当前插值态】再算目标（交互第一原则：随时可打断动画）。
        // 这样下方算目标用的 CurrentZoom / _live* 才是落定后的权威值，与 GoToPage(Home/End) 同源；
        // 否则按动画中途的插值态读到的倍率/偏移与落定后不一致，终点错位乱飞。AddWheelScroll 内会再
        // 调一次 StopSmoothZoom/AbandonZoomCommitWindow，但此时 _isSmoothZooming 已 false，幂等无害。
        if (_isSmoothZooming) StopSmoothZoom();
        AbandonZoomCommitWindow();
        // 必须用权威实时镜像 CurrentZoom(=_liveZoom)：缩放动画进行中它是【当前插值态】倍率；
        // 若读 Viewer.ZoomFactor 则是【异步提交未落地】的旧倍率 —— 目标偏移按旧倍率算、视图却落在新倍率，
        // 页位置错位 → 缩放中按空格/PageUp/PageDn 终点乱飞（滚轮/方向键只加固定 notch 不依赖页布局故不踩此坑，
        // 但本方法按页布局 LayoutRect*z 算目标，倍率错一档就飞）。
        double z = CurrentZoom;
        if (z <= 0) z = 1.0;
        // 末页/末列：同 GoToPage 的末页保护，把结尾钉在视口末端，避免小缩放下瞬移（大缩放下结果不变）。
        double target = index == pages.Count - 1
            ? EndScrollOffset(IsHorizontalLayout, z)
            : (IsHorizontalLayout
                ? Math.Max(0, (pages[index].LayoutRect.Left - PageGapHorizontal / 2.0) * z)
                : Math.Max(0, (pages[index].LayoutRect.Top - PageGap / 2.0) * z));
        // 复用滚轮弹簧：以"当前【显示位置】(_live* 权威镜像) → 该页起点"的差量作为一次滚动步进，
        // 中途再触发则无缝接续；边界由 AddWheelScroll 自夹（不会越过文档末端）。
        // 关键：差量基准必须用 _live*（当前真实显示偏移），【不能】用弹簧 _scroll*.Target —— 缩放/
        // 锚点缩放、直接 ChangeView 跳页等路径都不会更新弹簧 Target，用陈旧的 Target 算差量会飞回旧位置
        // 甚至首页。AddWheelScroll 内部先把弹簧复位到 _live*，再用本差量，陈旧值正好抵消，结果恒等于 target。
        if (IsHorizontalLayout) AddWheelScroll(target - _liveH, 0);
        else                     AddWheelScroll(0, target - _liveV);
    }

    /// <summary>
    /// 设置缩放百分比（10–400），以视口中心为锚。
    /// animate=true 走逐帧 ChangeView + 实时重光栅平滑缩放（锚点严格不动、缩小不黑边、渐进清晰）；
    /// animate=false 瞬时跳变（右键拖拽缩放等连续交互用，紧跟鼠标）。
    /// </summary>
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

    /// <summary>以【当前目标缩放】为基准步进（累积语义）：连点 / 连按可精确落到
    /// base + n×delta，而不是每次都基于尚未落地的中间插值态（那样连点会看起来"没反应"）。
    /// 返回夹取到 [MinZoomPercent, MaxZoomPercent] 后的最终目标。</summary>
    public double ZoomStepPercent(double deltaPercent)
    {
        double basePercent = _isSmoothZooming ? _zoomAnimTargetFactor * 100.0 : CurrentZoom * 100.0;
        double target = Math.Clamp(basePercent + deltaPercent, MinZoomPercent, MaxZoomPercent);
        StartSmoothZoom(target, null);
        return target;
    }

    /// <summary>
    /// 按给定 偏移/缩放 更新【可见矩形四边】(文档 DIP)与光栅缩放。内部自取 _lock。
    /// 动画/滚轮/拖拽等自管视图的帧循环统一走这里，保证横向布局下水平可见范围同样实时准确
    /// （否则横向滚动时后台按陈旧的 _visLeft/_visRight 选页，会渲染错页 / 出现空白页）。
    /// </summary>
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
            _zoom = zoom;
            UpdateScrollDirLocked();
        }
    }

    /// <summary>
    /// 按主轴 lead 的变化更新滚动方向（预渲染窗口据此把空闲坑位偏向前方）。
    /// 带死区，避免像素级抖动来回翻转方向导致窗口反复重排。须在持有 _lock 时调用。
    /// </summary>
    private void UpdateScrollDirLocked()
    {
        double lead = IsHorizontalLayout ? _visLeft : _visTop;
        if (double.IsNaN(_lastDirLead)) { _lastDirLead = lead; return; }
        double d = lead - _lastDirLead;
        if (Math.Abs(d) < ScrollDirDeadzone) return;
        _scrollDir = d > 0 ? 1 : -1;
        _lastDirLead = lead;
    }

    /// <summary>
    /// 启动（或接续）平滑缩放到 percent%，以 anchor（视口坐标，null=视口中心）为不动点。
    /// 逐帧推进：围绕锚点重算偏移，ChangeView 真正改缩放与范围，并按插值缩放实时重光栅。
    /// 进行中再次调用（按钮连点 / Ctrl+滚轮连发）采用"无缝接续"：以当前插值状态为新起点、
    /// 沿用原锚点与文档不动点，仅改写终点并重置缓动时钟，故连续点击平滑累加、无抽搐、无"只缩放一次"。
    /// reuseAnchor=true（双击切换专用）：当上一段缩放仍在进行或刚落定(宽限窗口内)时，复用其固定锚点
    /// 而非用本次鼠标位置重算——否则快速连点(三连击)每段新缩放起步瞬间因锚点切换而跳变(内容乱飞、不以鼠标为中心)。
    /// </summary>
    public void StartSmoothZoom(double percent, Point? anchor, bool reuseAnchor = false)
    {
        StopWheelScroll(); // Ctrl+滚轮缩放优先：取消进行中的滚轮滚动
        CancelPendingRestore(); // 用户缩放（双击/Ctrl+滚轮/预设）优先于未落定的还原，防止回拽
        // 【关键修复·双击起止闪帧】双击手势会"吃掉"第二次指针抬起（WinUI 手势识别器吞掉该 release），
        // 于是 Viewer_PointerReleased 不触发、_isDragPanning 卡在 true 直到下一个按下。这造成两处错位闪：
        //  ① 画布走 GetViewState 的 [pan] 分支，用陈旧的拖拽起点偏移(_panDisplayH/V=起跳时的 0,404)绘制，
        //     而缩放已插值到 0.5x~0.7x → "开始闪一帧错误位置"；
        //  ② PanApplyTick 每 16ms 仍在跑，持续发 ChangeView(旧偏移, 插值倍率) 与动画打架，把 Viewer
        //     渐变带偏(本例 0.5176→0.7149) → 提交窗口读到错位的中间态 → "结束闪一帧错误位置"。
        // 平滑缩放手势与拖拽平移互斥，这里主动清掉拖拽平移状态；后续真正的左键拖拽会重新置位，不影响手感。
        _isDragPanning = false;
        StopPanApplyTimer();
        StopZoomApplyTimer(); // 新一段手势重新播种：清残留的右键下发定时器（若非右键手势，StepZoomFrame 不会重开）
        _zoomAnimCommitting = false; // 清提交标志：避免读到上一动画残留的 _zoomAnimCurrent*(可能=0) 造成首帧闪到顶部
        // 冻结画布视口快照：整段手势的偏移与居中量都用同一视口，杜绝滚动条出现/消失引发的起止错位闪帧。
        // （滚轮/拖拽等闲置态路径会在各自起点用实时值覆盖。）
        _vpW = Viewer.ViewportWidth;
        _vpH = Viewer.ViewportHeight;
        if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
        double target = Math.Clamp(percent / 100.0, MinZoom, MaxZoom);
        // 起点：进行中用当前插值状态（无缝接续）；否则用【实时视图镜像 _live*】而非 Viewer.*——
        // 还原重试窗口里 Viewer.VerticalOffset 尚未落地(=0)，直接用会"从顶部开始动画"造成闪动。
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
            // 起点偏移(startH/V)即该不动点在上一段动画中的当前屏显位置，重算必然回到同一点 → 零跳变、严格不动。
            // 注意：此时【不】用本次传入的 anchor 重算，否则起点偏移对应的仍是旧不动点、新锚点会逼出起步跳变。
        }
        else
        {
            // 全新手势：以本次鼠标位置(或视口中心)为不动点，符合用户"以鼠标为中心缩放"的预期。
            Point a = anchor ?? new Point(_vpW / 2.0, _vpH / 2.0);
            _zAnimAnchor = a;
            // 锚点对应的文档坐标（缩放=1 DIP），全程固定；终态偏移由它推出 → 落定后锚点严格不动。
            // 居中补偿不可省：内容缩放后窄/矮于视口时 ScrollViewer(及画布)把内容居中呈现，
            // 视口坐标→文档坐标必须先扣掉这段居中量，否则低缩放整页落入视口时锚点算偏、
            // 缩放焦点不跟鼠标（与 TryViewportToPage / 右键缩放同款算法）。
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

    /// <summary>
    /// 起一段全新的缩放手势：弹簧从 z0 起步（位置与速度归零）、目标 z1，并播种重光栅节流基准。
    /// </summary>
    private void BeginZoomGesture(double z0, double z1, double omega, bool fromRightDrag)
    {
        _vpW = Viewer.ViewportWidth; // 手势期冻结画布视口快照（偏移与居中量配套）
        _vpH = Viewer.ViewportHeight;
        if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
        _zoomLog.Reset(Math.Log(z0));
        _zoomLog.Target = Math.Log(z1);
        _zoomOmega = omega;
        _zoomFromRightDrag = fromRightDrag;
        _zoomGestureStartZoom = z0;
        // 【必须播种画布插值态】GetViewState 在 _isSmoothZooming 期间【只】读 _zoomAnimCurrent*。
        // 原先本方法不播种，右键在缩放尽头(MaxZoom/MinZoom)按下并推动时 target==当前缩放 →
        // 弹簧当帧即判收敛 → StepZoomFrame 走 _isRightZooming 早退分支（该分支【不写】
        // _zoomAnimCurrent*）并停帧泵，于是画布拿【上一段动画残留】的陈旧偏移作画、且再无
        // 后续帧纠正 → 画面直接停在别处不动。这正是"放大到尽头后继续点右键，画面会移动"。
        // 播种取权威实时镜像 _live*：与 StartSmoothZoom 的 startH/startV 完全同源
        //（那里 !_isSmoothZooming 分支取的就是 _liveH/_liveV），故对双击/按钮缩放零行为变化。
        _zoomAnimCurrentFactor = z0;
        _zoomAnimCurrentH = _liveZoom > 0 ? _liveH : Viewer.HorizontalOffset;
        _zoomAnimCurrentV = _liveZoom > 0 ? _liveV : Viewer.VerticalOffset;
    }

    /// <summary>
    /// 当前权威缩放。一律取 _liveZoom（实时镜像）而非 Viewer.ZoomFactor：
    /// ChangeView 是异步的，缩放刚提交/动画刚被打断的那几帧里 Viewer.ZoomFactor 仍是【上一个】缩放，
    /// 拿它去画画布或算范围，就会先闪回旧倍率、等落地后再跳回来。
    /// </summary>
    private double CurrentZoom
    {
        get
        {
            if (_liveZoom > 0) return _liveZoom;
            double z = Viewer.ZoomFactor;
            return z > 0 ? z : 1.0;
        }
    }

    /// <summary>
    /// 给定缩放下的合法滚动范围（等价于 ScrollViewer 的 Scrollable{Width,Height}）。
    ///
    /// 关键：【绝不读 Viewer.Scrollable*】。那两个值由 ExtentWidth(=Spacer.Width×ZoomFactor) 推出，
    /// 在 ChangeView 尚未异步落地的窗口里仍是【上一个缩放】下的范围 —— 双击缩放动画中途按下左键时，
    /// 起点按新缩放播种、却被旧缩放的范围夹取，视图就会猛地飞到别处。
    /// Spacer.Width/Height 是未缩放的文档尺寸（RecomputeLayout 写入，与 zoom 无关）、随时可信；
    /// ViewportWidth/Height 是控件尺寸、同样与 zoom 无关，可直接读。
    /// </summary>
    private void GetScrollBounds(double zoom, out double maxH, out double maxV, double vpW, double vpH)
    {
        double w = double.IsNaN(Spacer.Width) ? 0 : Spacer.Width;
        double hh = double.IsNaN(Spacer.Height) ? 0 : Spacer.Height;
        maxH = Math.Max(0, w * zoom - vpW);
        maxV = Math.Max(0, hh * zoom - vpH);
    }

    /// <summary>
    /// 末页/末列对齐用的"文档末端"滚动偏移（缩放后像素，等价 GetScrollBounds 的对应轴上界）。
    /// 跳转到最后一页/列时把结尾钉在视口末端：小缩放下整页短于视口，若按页首对齐会留空隙，
    /// 随后被排版/交互（滚轮、翻页键、re-clamp）拉到末端，形成一次可见瞬移。
    /// 大缩放下整页高于视口，末端偏移本就是 ChangeView 的夹取下界结果 —— 两种情形结果一致，无回归。
    /// </summary>
    private double EndScrollOffset(bool horizontal, double z)
    {
        GetScrollBounds(z, out double maxH, out double maxV, Viewer.ViewportWidth, Viewer.ViewportHeight);
        return horizontal ? maxH : maxV;
    }

    /// <summary>内容小于视口时 ScrollViewer 的居中偏移（视口坐标 = 居中量 + 文档坐标×缩放 − 滚动偏移）。</summary>
    private void GetCenterOffsets(double zoom, out double centerX, out double centerY, double vpW, double vpH)
    {
        double extentW = (double.IsNaN(Spacer.Width) ? 0 : Spacer.Width) * zoom;
        double extentH = (double.IsNaN(Spacer.Height) ? 0 : Spacer.Height) * zoom;
        centerX = extentW < vpW ? (vpW - extentW) / 2.0 : 0;
        centerY = extentH < vpH ? (vpH - extentH) / 2.0 : 0;
    }

    /// <summary>
    /// 预测给定缩放落地后 ScrollViewer 的实际视口尺寸（= 控件尺寸 − Auto 滚动条占位）。
    /// 落地瞬间滚动条出现/消失会让 ViewportWidth/Height 跳变，而画布居中量随之跳变就是
    /// "开始/结束闪一帧错位"的来源；用预测值算目标偏移，使目标与落地状态自洽。
    /// 滚动条互相占用空间，迭代两次即收敛。
    /// 滚动条占位【实测】：滚动条可见时 ViewportWidth&lt;ActualWidth；overlay 样式恒相等(0)，
    /// 自动适配两种系统行为。预测误差 ≤1-2px 时居中量只差一半，肉眼无感。
    /// </summary>
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
            if (cw > vpW) vpH = Math.Max(0, ctrlH - sbH); // 横滚条占纵向空间
            if (ch > vpH) vpW = Math.Max(0, ctrlW - sbW); // 纵滚条占横向空间
        }
        pvW = Math.Max(1, vpW);
        pvH = Math.Max(1, vpH);
    }

    /// <summary>
    /// 由固定不动点（_zAnimContentX/Y 文档坐标 + _zAnimAnchor 视口坐标）反解给定缩放下的滚动偏移，
    /// 并夹到合法范围。这是"锚点全程不动"的唯一来源，动画帧与收尾帧都走它，故绝不会出现末帧位移。
    /// </summary>
    private void ResolveZoomOffsets(double zoom, out double h, out double v)
    {
        // 用画布视口快照 _vpW/_vpH：偏移与居中量必须配套（见 _vpW 注释），
        // 否则 ScrollViewer 落定瞬间滚动条出现/消失 → 居中量跳变 → 起止闪一帧错位。
        GetCenterOffsets(zoom, out double cx, out double cy, _vpW, _vpH);
        GetScrollBounds(zoom, out double maxX, out double maxY, _vpW, _vpH);
        h = Math.Clamp(_zAnimContentX * zoom + cx - _zAnimAnchor.X, 0, maxX);
        v = Math.Clamp(_zAnimContentY * zoom + cy - _zAnimAnchor.Y, 0, maxY);
    }

    /// <summary>
    /// 缩放帧：在 log(zoom) 空间推进弹簧 → 换算回缩放 → 围绕固定锚点反解偏移。
    /// 返回是否需要重画画布。
    /// </summary>
    private bool StepZoomFrame(double dt)
    {
        _zoomLog.Step(dt, _zoomOmega);

        if (_zoomLog.IsSettled(ZoomSettleLog, ZoomSettleVel))
        {
            // 右键拖拽【按钮仍按住】时绝不收尾退出：
            //
            // 原先这里一律 FinishZoom —— 慢速右键拖拽/停顿中，弹簧每停稳一次就解冻并按"中间密度"
            // 重光栅一次（FinishZoom → QueueVisiblePages），下一次鼠标移动又从新密度重新起跑。
            // 于是慢拖全程都在【冻结→解冻重铺→再冻结】间反复切换：每次解冻 = 一次密度换层
            // （糊→清脉冲 + 瓦片锐度边界移动）→ 恰是"瓦片之间波动/蠕动"在慢拖时最明显的根因。
            // 同时中途 FinishZoom 会换 _vpW/_vpH、退役合成层、开提交窗口，与仍在跑的下发定时器
            // 竞态 → 也是"偶发闪下一页画面"的来源。
            //
            // 现在按住期间一律保持手势存活：冻结不解除（单一密度层恒稳），只停帧泵省电、停下发
            // 定时器；松手/移出时由 EndRightZoomGesture 统一 FinishZoom，一次性精确落定 + 重光栅。
            // 落定瞬间先把【显示态】吸附到精确目标并重绘最终帧：否则按住停顿期间停泵冻结的是
            // 弹簧收敛残差态 zp（log 空间距目标 ≈ZoomSettleLog=0.002 → 缩放差 ≈0.2%），松手
            // FinishZoom 的 SnapToTarget 才跳到 zt → 内容 rescale 子像素 → 视觉"边缘动一下"。
            // 现在冻结帧 == 提交帧，松手零位移。
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
                // 冻结的就是上面已绘制的精确落定帧 —— 松手 FinishZoom 的 SnapToTarget 已是 no-op。
                StopZoomApplyTimer();
                StopFramePump();
                InvalidateBoth();
                return false;
            }
            // 收敛即收尾：FinishZoom 精确提交目标视图，并补一次可见页瓦片队列。
            FinishZoom();
            return false; // FinishZoom 内部已重画
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
        // 短程动画（双击/按钮/Ctrl+滚轮）则【不】逐帧 ChangeView：ZoomMode=Enabled 的
        // ScrollViewer 每次 ChangeView 都要重排版并回吐 ViewChanged，这一路开销全压在 UI 线程，
        // 正是"双击缩放看着只有十几帧"的另一半原因。滚动条位置由 FinishZoom 一次性对齐。
        if (_zoomFromRightDrag)
        {
            // 这里原先每个 Draw 帧都直接 ChangeView + ViewportChanged。120Hz 帧泵下逐帧
            // ChangeView 会让 ScrollViewer 反复异步重排版 + 回吐 ViewChanged，把这一路重活
            // 全压回 UI 线程 → 右键快速/长距离拖拽"鼠标跟不上"。改为【节流】到 16ms 定时器
            // 统一下发：画布绘制仍走 _zoomAnimCurrent*(GetViewState)，完全不受节流影响。
            EnsureZoomApplyTimer();
        }
        return true;
    }

    /// <summary>
    /// 提交最终视图，并开启"提交窗口"(<see cref="_zoomAnimCommitting"/>)：该窗口内画布继续读
    /// _zoomAnimCurrent*，避免 ChangeView 异步落地前闪回缩放前的画面。窗口由 Viewer_ViewChanged 关闭。
    ///
    /// 【这里曾经是一个会让左右键彻底失效的死锁】ChangeView 在目标与当前值完全一致时【不会】触发
    /// ViewChanged（ScrollViewer 没有变化就不发事件）。而右键拖拽缩放期间每帧都已 ChangeView 过，
    /// 松手收尾时提交的正是同一个值 → ViewChanged 永远不来 → _zoomAnimCommitting 永久为 true
    /// → Viewer_PointerPressed 永久 e.Handled=true → 左键拖拽、右键缩放全部无响应，只能重启。
    ///
    /// 故此处双保险：ChangeView 返回 false（未产生变化）就根本不开窗口；即使返回 true 也挂一次
    /// 低优先级兜底关闭 —— Low 回调在布局/渲染之后执行，那时 ViewChanged 该到的早就到了。
    /// 这个标志从此不可能卡死。
    /// </summary>
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

    /// <summary>
    /// 提交是否已真正落到位。ZoomFactor 与 Offset 都要匹配 —— 只看 zoom 是不够的，
    /// 恰恰是"zoom 已换、offset 还被旧 extent 夹着"这个中间态制造了闪帧。
    /// 提交偏移可能本就超出可达范围（内容不够长），故按当前缩放的合法范围夹一次再比。
    /// </summary>
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

    /// <summary>
    /// 提交尚未到位时推进一步。返回 false = 配额耗尽，调用方须强制收尾。
    ///
    /// 【这里曾经让双击切换缩放彻底失效】补发用的是 ChangeView(h, v, null)，null 意为"保持当前倍率"。
    /// 在 zoom 尚未落地的窗口内，"当前倍率"还是【旧】倍率 —— 这一发会把正在进行的缩放请求整个顶掉：
    /// 倍率回退到旧值，而偏移是按新倍率算的、配上旧 extent 又被夹断 → 表现为"比例变化错误 + 落点错误"。
    /// 看门狗是 Low 优先级、紧跟提交执行，那一刻 zoom 几乎必然还没落地，所以它每次都会踩中。
    ///
    /// 因此这里的铁律是：【zoom 没落地就只能等，绝不补发】。只有 zoom 已经到位、extent 随之就绪，
    /// 单推偏移才是安全的（此时 null 恰好等于正确倍率）。
    /// </summary>
    private bool TryAdvanceCommit()
    {
        if (!IsCommittedZoomReached())
        {
            // zoom 仍在异步落地：只等，绝不补发（补发会把正在进行的缩放顶掉，见上）。
            // 用【墙钟】兜底而非"轮次"计数——ScrollViewer 在 snap 期间会密集回吐 ViewChanged，
            // 轮次配额会在缩放尚未落地前就被耗尽，导致 ForceClose 过早关窗、把 _live* 回退到旧倍率
            // → 画布闪一帧错位（双击收尾闪的根因）。落地通常 <20ms，250ms 纯防真死锁。
            long elapsedTicks = Stopwatch.GetTimestamp() - _commitStartTicks;
            if (elapsedTicks >= (long)(CommitWaitDeadlineMs * Stopwatch.Frequency / 1000.0)) return false;
            return true;
        }

        if (++_commitNudges > MaxCommitNudges) return false;
        Viewer.ChangeView(_committedH, _committedV, null, disableAnimation: true);
        return true;
    }

    /// <summary>
    /// 强制收尾：关窗，并把权威镜像拉回 Viewer 真值。
    /// 走到这里说明提交最终没能完全落地（例如目标被 ScrollViewer 钳制），此时若放任 _live* 停在
    /// 提交值，它就与真实视图长期脱节 —— 下一次双击的 toggle 判定读的正是 _liveZoom，会判反。
    /// 宁可承认现实，也不要留一个骗人的镜像。
    /// </summary>
    private void ForceCloseCommitWindow()
    {
        _zoomAnimCommitting = false;
        // 仅当提交的缩放已真正落地(Viewer 此刻倍率=目标)才把 _live* 拉回 Viewer 真值；
        // 否则是"缩放还在异步落地就被迫关窗"，Viewer 仍停在旧倍率——此时若把 _live* 回退到旧倍率，
        // 画布会闪一帧错位、且 _live* 与真实视图长期脱节(下次双击 toggle 判反)。宁可保留
        // FinishZoom 写入的提交目标，等 Viewer 落地后由闲置同步纠正。
        // 布局切换 / 宿主几何变更的稳定窗口(_layoutSwitchInFlight)期间一律不同步：那两个窗口的权威
        // 镜像是 Reanchor 播种的目标值，此刻 Viewer.* 可能仍是被旧 extent 夹断的中间值(常为 0)，
        // 读了就会把它写进 _live* 并**永不自愈**（CurrentZoom 恒取 _liveZoom）。窗口自己负责收尾。
        if (_pendingRestore == null && !_layoutSwitchInFlight && IsCommittedZoomReached())
            SyncLiveView(Viewer.HorizontalOffset, Viewer.VerticalOffset, Viewer.ZoomFactor);
        InvalidateBoth();
        NotifyViewState();
    }

    /// <summary>
    /// 提交看门狗：Low 优先级在布局/渲染之后执行。到位即关窗；未到位交给 TryAdvanceCommit
    /// 决定"等"还是"补发偏移"，配额耗尽则强制收尾。有限轮内必然关窗，_zoomAnimCommitting 不会卡死。
    /// </summary>
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

    /// <summary>缩放收尾：精确落到目标视图，一次性对齐 ScrollViewer，再补一次可见页
    /// 瓦片队列 —— 动画期只做 1/3 倍频程量化的近似键，落定后按精确倍率补齐。</summary>
    private void FinishZoom()
    {
        _isSmoothZooming = false;
        _zoomFromRightDrag = false;
        StopZoomApplyTimer();               // 停掉右键拖拽的下发定时器，避免与提交竞态/残留空转
        _lastZoomEndTime = DateTime.UtcNow; // 记录落定时刻，供快速连点复用锚点判断
        _zoomLog.SnapToTarget();

        // RenderCache（1:1 Sumatra）：缩放落定 = zoomReal 变化。旧 zoom 的瓦片留作替身
        // （Find 任意 zoom 的同位瓦片按 factor 缩放显示），新 zoom 的瓦片请求由下一帧
        // Draw 的 renderMissing 与 QueueVisiblePages 发出；旧分辨率瓦片由 Paint 的
        // gConserveMemory 收尾（目标层全覆盖后 FreePage 其他分辨率）按视口释放。
        double zt = _zoomAnimTargetFactor;
        // 目标偏移按【落定时刻】的视口/内容尺寸重解：动画期间窗口可能被拉伸过。
        // 关键：先预测落地后 ScrollViewer 的视口（滚动条出现/消失会改变 ViewportWidth/Height），
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
        // 收尾：补一次可见页队列，把最终视口进入的页的瓦片补齐（已有瓦片幂等跳过）。
        QueueVisiblePages();
        InvalidateBoth();
        NotifyViewState();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        StopFramePumpIfIdle();
    }

    /// <summary>立即落定到【当前插值态】并停掉动画（拖拽/滚轮/快速连点开始前调用）。</summary>
    private void StopSmoothZoom()
    {
        if (!_isSmoothZooming) return;
        _isSmoothZooming = false;
        _zoomFromRightDrag = false;
        StopZoomApplyTimer();               // 停掉右键拖拽的下发定时器，避免与提交竞态/残留空转
        _lastZoomEndTime = DateTime.UtcNow; // 记录落定时刻，供快速连点复用锚点判断
        _zoomLog.Reset(_zoomLog.Value);     // 就地停住：保留当前值、清零速度、目标=当前
        // 关键：落定到【当前插值态】而非目标态——否则快速双击/三连击时，每次按下(PointerPressed)
        // 杀动画会把视图"跳"到目标位置造成乱飞；落定到当前态则视图停在鼠标松开那一刻、无跳变，
        // 随后的拖拽/右键缩放/新一段缩放都从当前视口无缝接续。
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

    /// <summary>
    /// 滚动 / 直接跳页接管视图前：关闭可能仍开着的缩放提交窗口。
    ///
    /// 关键：StopSmoothZoom 会经 CommitViewWithGuard 把 _zoomAnimCommitting 置 true 并等异步 ChangeView 落地。
    /// 但 GetViewState 的优先级是「_isSmoothZooming || _zoomAnimCommitting ＞ 滚动 ＞ 闲置」，提交窗未关时
    /// 它会继续返回缩放插值态 _zoomAnimCurrent*（=缩放被停住的旧位置），而本帧起视图已由滚动弹簧驱动 _live*
    /// 下发 —— 二者脱节 → 画布画在旧位置、Viewer 在被滚动，画面乱飞/跳回缩放前位置。
    /// 关窗后 GetViewState 落到 _isWheelScrolling / 闲置分支返回 _live*，与帧泵下发一致。
    /// 关窗后看门狗(ScheduleCommitWatchdog)在 _zoomAnimCommitting 判定处早退，不会再把视图推回提交值。
    /// 同时把 _zoomAnimCurrent* 同步到当前权威 _live*，确保万一仍被读到也是正确位置。
    /// </summary>
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

    // ================= 滚动：临界阻尼弹簧 =================

    /// <summary>
    /// 起程时把滚动弹簧播种到当前真实位置。用 _live*（权威实时镜像）而非 Viewer.*：
    /// 刚落定的缩放/拖拽其 ChangeView 还没异步落地，读 Viewer.* 会拿到上一帧的旧偏移，
    /// 于是第一档滚轮会先"倒退一下"再前进 —— 这正是短距离连续滚动里最刺眼的那一跳。
    /// </summary>
    private void EnsureScrollSprings()
    {
        if (_isWheelScrolling) return;
        double h0 = _liveZoom > 0 ? _liveH : Viewer.HorizontalOffset;
        double v0 = _liveZoom > 0 ? _liveV : Viewer.VerticalOffset;
        _scrollH.Reset(h0);
        _scrollV.Reset(v0);
        // 滚轮期视口快照与 _live* 配套（缩放不变 → 滚动条状态不变，捕获一次即可）。
        _vpW = Viewer.ViewportWidth;
        _vpH = Viewer.ViewportHeight;
        if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
        _isWheelScrolling = true;
    }

    /// <summary>
    /// 把一次滚轮位移并入滚动目标。只改目标、【不碰速度】：连发滚轮时目标持续向前延伸，
    /// 弹簧的速度是连续累积上去的 → 一条越滚越快、松手后平滑收住的曲线。
    /// 旧的 lerp 每来一档都从 0 重新起速再急停，速度曲线是一串锯齿，那就是"跳跃感"的来源。
    /// </summary>
    private void AddWheelScroll(double dh, double dv)
    {
        // 滚动与缩放动画互斥：缩放进行中收到滚动输入，先【落定到当前插值态】再滚动
        // （交互第一原则：用户随时可打断动画）。否则缩放与滚动两套引擎抢 Viewer.ChangeView
        // 与 _live* 镜像，缩放动画结束后会出现"缩放倍率/位置都错误"的幽灵滚动。
        if (_isSmoothZooming) StopSmoothZoom();
        // 关闭可能仍开着的缩放提交窗口：缩放刚落定(ChangeView 未落地)或动画进行中都被滚动打断时，
        // 若留着提交窗，GetViewState 会返回缩放插值态、与滚动下发的 _live* 脱节 → 画面乱飞。见 AbandonZoomCommitWindow。
        AbandonZoomCommitWindow();
        EnsureScrollSprings();
        // 范围自算（同 GetScrollBounds 注释）：缩放刚落定、ChangeView 未落地时读 Viewer.Scrollable*
        // 会拿到旧倍率下的范围，把滚动目标夹断在半路。
        GetScrollBounds(CurrentZoom, out double maxH, out double maxV, _vpW, _vpH);
        _scrollH.Target = Math.Clamp(_scrollH.Target + dh, 0, maxH);
        _scrollV.Target = Math.Clamp(_scrollV.Target + dv, 0, maxV);
        EnsureFramePump();
    }

    /// <summary>立即停止滚轮滚动（拖拽/缩放/失焦等抢走视图时调用）。当前位置原地保留。</summary>
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
        // 范围按权威缩放自算，不读 Viewer.Scrollable*（异步落地期间是旧倍率的范围）。
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
        // 滚动常紧跟在缩放打断之后：StopSmoothZoom 的 zoom 提交是异步的，可能尚未落地，
        // 此刻若用 null，Viewer 仍停在旧缩放、偏移被旧(更小的) extent 夹断；随后缩放落地又把视图
        // 拽回缩放停止处(hp,vp)而非滚动目标 → "动画结束位置飞到不知哪"的根因。带上 z 让缩放与偏移
        // 原子化一起落地，彻底消除这条竞态（正常滚动时 z==当前倍率，与传 null 等价，无副作用）。
        Viewer.ChangeView(h, v, (float)z, disableAnimation: true);   // 驱动原生滚动条
        SetVisRect(h, v, z);
        // 滚动不改变缩放 → 缓存键（页 + res + 瓦片坐标）不变，已有瓦片仍精确，
        // 只补齐新进入视口页的瓦片。
        QueueVisiblePages();
        NotifyViewState();
        ViewportChanged?.Invoke(this, EventArgs.Empty);

        if (doneH && doneV) FinishScroll();
        return true;
    }

    /// <summary>滚动收尾：停稳后补一次可见页队列（进入视口的页构建进来）。</summary>
    private void FinishScroll()
    {
        if (!_isWheelScrolling) return;
        _isWheelScrolling = false;
        QueueVisiblePages();
        StopFramePumpIfIdle();
    }

    // ================= 帧泵：Win2D 交换链 vsync 自持续循环（滚动与缩放共用） =================

    /// <summary>
    /// 触发首帧 Draw，之后由 Draw 末尾的自持续 Invalidate 维持。模拟步进与"上屏"在同一个 Draw 调用里
    /// 完成，节奏完全由 Win2D 交换链 vsync 决定（60/120/144Hz 各按真实刷新率），且当帧步进值当帧即上屏，
    /// 从根上消除"CompositionTarget.Rendering 步进 + 独立 Win2D 上屏"两套 vsync 队列的相位脱节 judder。
    /// </summary>
    private void EnsureFramePump()
    {
        if (_framePumpHooked) return;
        _framePumpHooked = true;
        _lastDrawTicks = 0; // 首帧 Draw 用 1/60 作 dt，不打断连续输入
        _lastFrameTicks = Stopwatch.GetTimestamp();

        // 看门狗：窗口被完全遮挡/最小化时 Win2D 也会停发 Draw，动画就冻在半路，
        // 等窗口恢复再"跳"完剩下的路。检测到断帧就地收尾，不留半截状态。
        _framePumpWatchdog ??= CreateFramePumpWatchdog();
        _framePumpWatchdog.Start();

        // 触发首帧 Draw；之后由 Draw 末尾的自持续 Invalidate 维持，节奏 = Win2D 交换链 vsync
        // （120Hz 屏即 120Hz）。模拟步进与上屏在同一个 Draw 调用里完成，彻底消除双循环相位脱节 judder。
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

    /// <summary>
    /// 取两帧 Draw 之间的真实耗时（秒）作为模拟 dt。用 Stopwatch 实钟而非 RenderingTime：
    /// 因为步进现已并入 Draw、随 Win2D vsync 跑，实钟 dt 即"本帧到上一帧的真实墙钟间隔"，
    /// 天然等于当前刷新周期（120Hz≈8.3ms，60Hz≈16.6ms），spring 按真实 dt 积分 → 速度观感与刷新率无关。
    /// 单帧过长（切后台/GC 停顿/换页卡顿）钳到 50ms：解析解对大 dt 仍稳定，但一步跨 300ms 像瞬移，
    /// 钳小让它用几帧追上，观感连贯。
    /// </summary>
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

    /// <summary>
    /// 子级(透明 Spacer)上的滚轮阻断器：在事件冒泡到 ScrollViewer 之前把事件标为已处理，
    /// 跳过原生 ScrollViewer 的滚轮类处理程序（其 handledEventsToo=false，只能在它【之前】——
    /// 即子级冒泡阶段——标 Handled 才能跳过；标在 Viewer 自身则已在原生之后，无效）。
    /// 原生滚轮只移动透明 Spacer，画布是兄弟元素、根本碰不到，故阻断它不会产生任何视觉丢失，
    /// 真正的滚动由 Viewer_PointerWheelChanged 走统一弹簧引擎接管，滚动条也由弹簧逐帧 ChangeView 对齐。
    /// </summary>
    private void Viewer_PointerWheelBlocker(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void Viewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
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
            // 连发滚轮会一直以同一个旧基准算出几乎相同的目标 → 表现为"连滚好几档只缩放了一档"。
            double baseZoom = _isSmoothZooming ? _zoomAnimTargetFactor
                                               : (_liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor);
            if (baseZoom <= 0) baseZoom = 1.0;
            double next = baseZoom * (1.0 + delta / 1000.0 * CtrlWheelZoomStrength) * 100.0;
            StartSmoothZoom(Math.Clamp(next, MinZoomPercent, MaxZoomPercent), null);
            e.Handled = true;
            return;
        }

        // 普通滚轮 → 并入滚动弹簧目标；缩放动画进行中先停掉。
        if (_isSmoothZooming) StopSmoothZoom();

        // 轴向路由（横/纵布局统一走自管临界阻尼弹簧，与双击/右键缩放、横向滚轮同一引擎）：
        //   • 可见内容(GpuPageView 的 CanvasControl)是 ScrollViewer 的【兄弟】，原生滚轮只会移动透明
        //     Spacer，根本碰不到画布——故"原生一档 + 我们一档 双滚"在现架构下不存在（画布只由 _live* 驱动）。
        //   • 原生滚轮的动力学（逐档瞬跳 / 加速度曲线）与统一弹簧不一致，正是"滚轮相比双击/右键缩放卡顿"
        //     的根因；交给我们自己的弹簧后，纵/横滚轮与缩放动画手感完全统一。
        //   • 原生滚动由 Spacer 上 Viewer_PointerWheelBlocker 在冒泡早期标 Handled 阻断，不与弹簧争抢滚动条。
        // 横/纵共用同一套"下滚累加正向偏移"映射，Shift 走交叉轴，方向天然一致；不对 MouseWheelDelta 符号做假设。
        bool crossAxis = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0;
        double step = delta * WheelScrollFactor;
        if (IsHorizontalLayout)
        {
            // 横向：普通滚轮走水平主轴；Shift 走交叉轴（看放大后的纵向内容）。
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

    /// <summary>当前缩放百分比。</summary>
    public double ZoomPercent => Viewer.ZoomFactor * 100.0;

    /// <summary>
    /// "适应视口"对应的缩放百分比 —— 始终作用于【交叉轴】（非滚动方向），
    /// 使单页在该方向完整可见、只沿主轴滚动：
    ///   纵向连续 → 内容宽 → 视口宽（即传统的"适应宽度"）
    ///   横向连续 → 内容高 → 视口高（等价动作："适应高度"）
    /// 横向布局若仍按宽度适应，会拿【整份文档的总宽】去除，结果被钳到最小缩放，页面小到看不清。
    /// </summary>
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

    /// <summary>适应视口（纵向=适应宽度 / 横向=适应高度）。</summary>
    public void FitWidth(bool animate = true) => SetZoomPercent(FitToViewportPercent, animate);

    /// <summary>
    /// 宿主视口尺寸变化（侧边栏 Inline 挤开主视图）【之前】同步冻结"阅读位置不变量"。
    /// 必须在翻转 IsPaneOpen / 改列宽之前调用：列宽在过渡起点(KeyTime=0)即变，事后读不到旧几何。
    ///
    /// 只冻结两个与缩放无关的量，落定后据此一次性重解，全程不跟帧：
    ///   · <see cref="_hostResizeDocLeftX"/> —— 视口**左缘**切到的文档 X
    ///   · <see cref="_hostResizeDocTopY"/>  —— 视口**上缘**切到的文档 Y
    /// 正是要钉住的那两条边线本身（第三条"右缘"由缩放比自动跟随，见 BeginHostResizeReanchor）。
    /// 两个量都按"视口坐标 = 居中量 + 文档坐标×缩放 − 滚动偏移"先扣掉居中量（GetCenterOffsets），
    /// 低缩放整页落入视口时同样算得准。
    /// </summary>
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
        _hostResizeVpH0 = vpH0;
        _hostResizeDocLeftX = (h0 - cx0) / z0;
        _hostResizeDocTopY = (v0 - cy0) / z0;
        _hostResizeCaptureValid = true;
    }

    /// <summary>
    /// 宿主几何已生效后调用（侧边栏过渡起点之后一帧）：把阅读位置不变量当作**固定锚点**，
    /// 用弹簧把 zoom 平滑推到"主轴视口新旧比例"的目标值（而不是瞬时落定）。
    ///
    /// 【语义（用户拍板 · 09-23 实测定案）】两种布局一律 —— **三条边线不动，只有下边内容变化**：
    ///   · 「左」「右」不动：zoom 恒按 **vpW** 新旧比例缩放 ⇒ vpW₁/z₁ = vpW₀/z₀，可见文档区间长度不变；
    ///   · 「上」不动：锚点取视口**左缘 × 上缘** = (0,0)，文档坐标直接用开关前冻结的两个不变量：
    ///       h(z) = _zAnimContentX·z + Cx(z) − 0  ⇒ 左缘文档 X ≡ _zAnimContentX（对任意 z 恒成立）
    ///       v(z) = _zAnimContentY·z + Cy(z) − 0  ⇒ 上缘文档 Y ≡ _zAnimContentY（对任意 z 恒成立）
    ///     ⇒ **动画每一帧**这三条边线切到的内容都不动，终点与"瞬时公式"逐字等价。
    ///   · 「下」是唯一变化的边：下缘文档 Y = 上缘文档 Y + vpH/z，侧边栏不改高（vpH 不变）而 z 变小
    ///     ⇒ vpH/z 变大 ⇒ 下方露出更多内容。这正是要的效果（原来锚竖直中心，是上下各露一半）。
    /// ⚠ 缩放比不随布局方向变：横向连续同样锁左右边线（与"适应视口"按 vpH 是两回事，别混）。
    ///
    /// 【为什么这里能再用弹簧，而第 7 轮那版不能】
    /// 旧版的锚点是在【几何已变之后】由 startH 反解的 `_zAnimContentX = (0 − Cx₀ + startH)/z₀`，
    /// 而且当时 `_vpW` 还是旧视口 —— 同一个量被 StartSmoothZoom 与 FinishZoom(PredictViewport)
    /// 用两个不同视口解算两次，漂移就藏在缝隙里。现在锚点文档坐标是**开关前冻结的标量**，与新视口无关，
    /// `_vpW/_vpH` 在起动画前就换成新视口，`ResolveZoomOffsets` 只解一次。
    /// 收尾仍靠**布局切换稳定窗口**(_layoutSwitchInFlight)：未确认 ScrollViewer 真到达前一律不读 Viewer.*，
    /// 避免回读到被旧 extent 夹断的 0 而把 `_live*` 永久污染（那正是"偏移到 0,0、影响之后全部开关"的根因）。
    /// </summary>
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
        // 永远由宽度决定，与页面沿哪个方向排布无关（用户 09-23 拍板：纵横都锁左+右+上三条边线）。
        //   z₁ = z₀ × vpW₁/vpW₀ ⇒ vpW₁/z₁ = vpW₀/z₀，可见文档区间长度自动不变 ⇒ 右缘跟随左缘不动。
        // ⚠ 别与"适应视口"混：横向连续的 FitToViewportPercent 按【高度】（适应高度）是另一套语义；
        //   这里横向同样按宽度缩放，改完之后不再因"高不变"而早退。
        double mainOld = vpW0;
        double mainNew = vpW1;
        // 宽度没变就是无关调用，不动。
        if (mainNew <= 0 || Math.Abs(mainNew - mainOld) <= 0.5) return;

        float z1 = (float)Math.Clamp(z0 * (mainNew / mainOld), MinZoom, MaxZoom);

        // 画布绘制与居中量都按新视口算：SplitView 之后那 0.15s 只是渲染滑动，布局不再变。
        // （必须先换，下面的 ResolveZoomOffsets 用的是这两个快照。）
        _vpW = vpW1;
        _vpH = vpH1;

        // 进行中的缩放手势必须作废：它的帧泵会拿自管的 _zoomAnimCurrent* 覆盖下面播种的锚点状态。
        // 随之可能被 StopSmoothZoom→CommitViewWithGuard 打开的提交窗口也一并关掉 —— 该窗口的
        // TryAdvanceCommit 在"缩放已到位"时只推偏移、不推缩放，残留下来会拿"停手那一刻"的旧偏移
        // 把重锚结果顶掉（ScheduleCommitWatchdog 以 _zoomAnimCommitting 为链头，置 false 即断链）。
        StopSmoothZoom();
        _zoomAnimCommitting = false;

        // 锚点 = 视口【左缘 × 上缘】= (0,0)：两条要钉的边线本身就取作锚点，
        // 文档坐标直接用开关前冻结的两个不变量 ⇒ 全程对任意 z 恒成立。
        // 「右」不用单独锚：vpW/z 不变，右缘 = 左缘 + vpW/z 自动跟随。
        // 「下」不锚：下缘 = 上缘 + vpH/z，vpH 不变而 z 变小 ⇒ 下缘下移 ⇒ 只有下方多露出内容。
        // 两布局共用这一组锚点，无需按方向分支。
        _zAnimAnchor = new Point(0, 0);
        _zAnimContentX = _hostResizeDocLeftX;
        _zAnimContentY = _hostResizeDocTopY;
        _zoomAnimTargetFactor = z1;
        ResolveZoomOffsets(z1, out double h1, out double v1);

        // 稳定窗口：改几何 + 提交期间 ViewChanged 回读到的是被旧 extent 夹断的中间值（常为 0），
        // 到达判定前一律不读 Viewer.*；收尾由 Viewer_ViewChanged 的窗口分支负责，看门狗兜底。
        _layoutSwitchInFlight = true;
        _layoutSwitchAnchor = Math.Clamp(CurrentPageIndex, 0, pages.Count - 1);
        _layoutSwitchStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        StopLayoutSwitchWatchdog();
        _layoutTargetH = h1;
        _layoutTargetV = v1;
        _layoutTargetZoom = z1;

        // 弹簧：用代码里现成的"快"档（右键拖拽缩放那档，≈165ms），与侧边栏 0.15s 滑窗同一量级。
        // BeginZoomGesture 会用 Viewer.Viewport*（此刻已是新值）再覆写一次 _vpW/_vpH —— 与我上面设的一致。
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

    /// <summary>实际大小（100%）。</summary>
    public new void ActualSize(bool animate = true) => SetZoomPercent(100, animate);

    // ---------------- 同步滚动（供 MainPage 中间 SyncScrollBar 驱动） ----------------

    /// <summary>当前垂直滚动偏移（像素，文档坐标）。</summary>
    public double VerticalOffset => Viewer.VerticalOffset;

    /// <summary>可滚动高度（像素）。</summary>
    public double ScrollableHeight => Viewer.ScrollableHeight;

    /// <summary>视口高度（像素）。</summary>
    public double ViewportHeight => Viewer.ViewportHeight;

    /// <summary>水平滚动偏移（像素），备用。</summary>
    public double HorizontalOffset => Viewer.HorizontalOffset;

    /// <summary>可滚动宽度（像素），横向布局主轴用。</summary>
    public double ScrollableWidth => Viewer.ScrollableWidth;

    /// <summary>视口宽度（像素），横向布局主轴用。</summary>
    public double ViewportWidth => Viewer.ViewportWidth;

    /// <summary>按绝对偏移滚动（像素）。animate=true 用系统平滑滚动。</summary>
    public void SetVerticalOffset(double offset, bool animate = false)
    {
        double v = Math.Max(0, offset);
        Viewer.ChangeView(null, v, null, !animate);
        // 另一轴与缩放取权威镜像，不读 Viewer.*：缩放提交尚未落地时它们仍是旧值，
        // 写回 _live* 会把镜像污染成旧倍率（污染不自愈，此后所有以 _live* 播种处全错）。
        SyncLiveView(_liveH, v, CurrentZoom);
    }

    /// <summary>按绝对水平偏移滚动（像素）。横向布局主轴用。animate=true 用系统平滑滚动。</summary>
    public void SetHorizontalOffset(double offset, bool animate = false)
    {
        double h = Math.Max(0, offset);
        Viewer.ChangeView(h, null, null, !animate);
        SyncLiveView(h, _liveV, CurrentZoom); // 同上：不拿可能陈旧的 Viewer.* 回写权威镜像
    }

    /// <summary>
    /// 取当前视图快照，供退出/切换文档时持久化。
    /// 平滑缩放或拖拽平移进行中返回插值后的实时状态，避免依赖 ChangeView 异步落点造成的 1 帧错位。
    /// </summary>
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

    /// <summary>
    /// 排队还原：若视图已首次适配（_didInitialFit 且视口就绪）则立即还原；
    /// 否则由首次 TryInitialFit（视口就绪后）应用——确保不被「适应宽度」覆盖。
    /// </summary>
    public void RestoreView(ViewStateData vs)
    {
        if (vs == null) return;
        // 布局必须在偏移之前还原（SavedAtUtc 注释已说明）：重建 Spacer 尺寸，使后续偏移按正确主轴对称落定。
        // 注意：不走 LayoutMode 公共 setter（它会重排+重锚定当前页，覆盖保存的精确偏移），
        // 这里只改内部字段 + 重排 + 通知工具栏图标，偏移由下方 TryApplyPendingRestore 精确应用。
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
        // 交给 TryInitialFit 裁决：它见到 _pendingRestore!=null 会优先还原、跳过"适应宽度"。
        // 【关键】100% 恰好等于 ScrollViewer 默认 ZoomFactor(1.0)。若此处未守卫、提前直接应用，
        // 阶段1 因 |1.0−1.0|≤阈值被跳过、阶段2 落定后清掉 _pendingRestore，随后 TryInitialFit
        // 看到 pending==null → 应用"适应宽度"，把刚还原的 100% 覆盖 → 记忆位置丢失。
        // 适应宽度(~0.52)≠默认1.0，阶段1 必触发、保留 pending，故不受影响 —— 这正是"只在100%失效"的不对称来源。
        if (_didInitialFit)
            TryApplyPendingRestore();
    }

    /// <summary>
    /// 尝试精确还原保存的缩放/偏移/旋转（瞬时，无动画）。仅用于加载阶段。
    /// - 范围未就绪（Viewer.ViewportWidth&lt;=0）或偏移被尚未就绪的滚动范围夹断时，保留 _pendingRestore，
    ///   并通过【Low 优先级调度自行重试】（不依赖用户交互），直至偏移精确落定或重试耗尽。
    /// - 不读取 Viewer.VerticalOffset 作为权威值（窗口隐藏会清零）；滚动范围就绪与否以"ChangeView 后
    ///   偏移是否被夹断"判定。
    /// - 用户正交互（缩放/滚轮/拖拽）期间不强行落定，避免与动画打架，仅顺延重试；交互结束后再应用。
    /// - 旋转会改变文档布局坐标系，须先于偏移还原（保存的偏移即处于该旋转坐标系下）。
    /// </summary>
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
        // 让"应用后立即排的校验重试"能可靠确认落定而非无限重投；同时兜住保存/另存为后
        // 任何把视口又拽回首页/适应宽度的异步 ChangeView（如 LoadAsync 残留的 fit 分支）。
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
        // 避免"大跨度 zoom 跳变 + 偏移"同一次 ChangeView 被旧(小) extent 夹断 → 回到 xy=0 / 顶端（老问题复现）。
        // 画布(ScreenCanvas)按 _live* 绘制、与 ScrollViewer 实际偏移解耦，故阶段1仅设 zoom 不闪（canvas 已呈还原位置）。
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
                // 后续 idle 同步（第2332行）把 _live* 拉回 0 → 重开飞到左上角。ChangeView 会按当前就绪的
                // extent 自行夹断，至少落到尽可能接近的还原位置，远比停在顶端安全。
                Viewer.ChangeView(h, v, target, disableAnimation: true);
                SyncLiveView(h, v, target);
                _pendingRestore = null; _restoreRetries = 0;
            } // 极端：放弃精确重试，但已落定视图
            return; // 保留 _pendingRestore
        }

        // 阶段2：缩放已对齐 target，extent 已就位 → 偏移可精确落定（不会被旧 extent 夹断）。
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
            return; // 保留 _pendingRestore
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

        // 【不立即清除 _pendingRestore】改为交给上方"校验重试"在视口真正落定后清除——
        // 否则异步 fit/失焦等 ChangeView 可能在还原之后又把视口拽回首页/适应宽度
        // （保存/另存为后视口归位保护）。下一次 Low 优先级重试会校验：已精确落定即收尾，
        // 被回拽则重新落定；重试耗尽也强制落定并收尾。
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

    /// <summary>用户交互优先：一旦用户开始操作（双击/滚动/拖拽/缩放），取消未落定的还原，
    /// 避免还原在用户操作之后"回拽"视图到保存位置。</summary>
    private void CancelPendingRestore()
    {
        _pendingRestore = null;
        _restoreRetries = 0;
    }

    /// <summary>Low 优先级重投 TryApplyPendingRestore：在 Layout/绘制之后运行，
    /// 此时 ScrollViewer 可滚动范围已随布局刷新，偏移可精确落定；且不依赖用户交互。</summary>
    private void ScheduleRestoreRetry()
        => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, TryApplyPendingRestore);

    /// <summary>关闭/换文档时调用：释放所有缓存位图与渲染队列，避免显存滞留。</summary>
    public void ResetView()
    {
        lock (_lock)
        {
            _docGeneration++;
            _lastDirLead = double.NaN;
            _scrollDir = 0;
        }
        // DisplayModel 析构对应物：CancelRenderingBlocking + FreeForDisplayModel + Dispose。
        ShutdownRenderCache();
        _currentPage = -1;
        InvalidateBoth();
    }

    // ---------------- 旋转（纯呈现层：位图缓存不作废，绘制时 GPU 变换） ----------------

    private int _rotation; // 0/90/180/270，全文档统一（与旧路径行为一致）

    /// <summary>当前旋转角（度）。</summary>
    public int RotationDegrees => _rotation;

    /// <summary>顺时针旋转 90°。位图与缓存都按【未旋转页帧】分键（rotation 恒 0）→ 旋转只需
    /// 在 DrawingSession 上补一次 CreateRotation，不重建任何瓦片。
    ///
    /// 锚点（与 Sumatra 的有意分歧，已文档化）：Sumatra 的 DisplayModel::RotateBy 收尾是
    /// `GoToPage(currPageNo, 0)`（锚到页首）——因为它的位图把 rotation 烘进出图，转到任何角度
    /// 都得整页重出，锚在哪都无所谓。Glance 的位图与 rotation 无关，锚到页首会把整屏可见区换掉：
    /// 放大态下几乎每一颗可见瓦片都变成"没渲染过的新瓦片"→ 全屏先发糊再变清晰（实测症状）。
    /// 这里改成【让视口中心那个页点在旋转后原地不动】：新可见区与旧可见区重心重合，
    /// 180° 完全复用已缓存瓦片（零请求），90/270° 只剩旋转外扩的两条带需要出图。</summary>
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

    /// <summary>记录【视口中心】对应的页内相对位置 (0..1，未旋转页帧)。几何与 ComputeVisiblePages /
    /// ITileRenderHost.PageOnScreen 同式 —— 三者必须同源，否则锚点与绘制/请求的页帧对不上。
    /// 返回 false = 视口中心不在该页内（跨页缝/页比视口小且偏心），调用方走页首对齐。</summary>
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

    /// <summary>反解偏移：让未旋转页矩形里相对位置 (fx, fy) 的点落在视口中心（= 旋转前后同一页点
    /// 原地不动）。坐标全部用【未旋转页帧】的页尺寸（旋转只交换呈现宽高，页内容尺寸不变）。</summary>
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

        // 目标：未旋转页矩形的中心 → 由 (fx, fy) 反推（中心 == 旋转后 dest 中心，同 PageUnrotatedRect）。
        double needCenterX = vpW / 2.0 - (fx - 0.5) * uw;
        double needCenterY = vpH / 2.0 - (fy - 0.5) * uh;
        var lr = p.LayoutRect;
        hOff = Math.Max(0, centerX + lr.X * z + lr.Width * z / 2.0 - needCenterX);
        vOff = Math.Max(0, centerY + lr.Y * z + lr.Height * z / 2.0 - needCenterY);
        return true;
    }

    // ---------------- 标注叠加层（GPU Phase 5：五模式移植） ----------------
    // 设计：数据/保存/撤销全部留在 MainPage（复用旧路径的 AnnotationViewModel 管线），
    // GpuPageView 只负责两件事：
    //   ① 绘制 —— 每帧在页位图之上按「页变换矩阵」画高亮/画笔/便签（自动跟随缩放与旋转）；
    //   ② 输入 —— 标注模式下把左键指针事件换算成【页局部坐标（PDF 点，未旋转）】回调出去，
    //      与旧路径 Canvas_Pointer* 坐标空间完全一致，MainPage 旧数据逻辑原样复用。

    /// <summary>标注输入模式（画笔时为 true）：左键交给标注管线，禁用拖动平移与双击缩放。</summary>
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

    /// <summary>标注叠加层可见性（默认 true，恒显示）。隐藏只影响显示，数据模型与输入管线不变；
    /// 不持久化 —— 重新打开文档/应用后回到默认显示。切换即时重画。</summary>
    public bool AnnotationsVisible
    {
        get => _annotationsVisible;
        set
        {
            if (_annotationsVisible == value) return;
            _annotationsVisible = value;
            InvalidateOverlay();
        }
    }
    private bool _annotationsVisible = true;

    /// <summary>按页取标注列表（MainPage 注入：idx → _pages[idx].Annotations）。</summary>
    public Func<int, IReadOnlyList<AnnotationViewModel>?>? AnnotationProvider { get; set; }

    // ---------- 画笔渲染性能 & 稳定性修复：节流刷新 + 几何缓存 + 笔画累积层 ----------
    // 1000Hz 鼠标每秒触发 ~1000 次 PointerMoved。手段把 GPU 压到可接受：
    //   ① 节流：8ms 限速合并，120Hz 屏每帧至多刷新一次（不会跑得比 vsync 还快）；
    //   ② 标注与页位图画在同一交换链（单表面原子合成），消除双画布相位脱节的震动/闪光；
    //   ③ 累积层：已完成笔画一次性烘入离屏位图 _annoLayer，静止/板书期只实时重画"当前一笔"。
    private static readonly int AnnoRefreshIntervalMs = 8;   // 120Hz 适应：8ms≈125fps，被 vsync 封顶到120
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _annoRefreshTimer;
    private long _lastAnnoRefreshTicks;
    private bool _annoRefreshPending;
    private readonly object _annoRefreshLock = new();

    /// <summary>节流版 InvalidateOverlay：8ms 限速合并高频调用，速率最快 = 刷新率。
    /// 只刷 ScreenCanvas（页位图 + 标注同一交换链）。仅"笔画内容变化"走此增量路径。</summary>
    public void InvalidateOverlayThrottled()
    {
        // 限速判断：若距离上次实际刷新 >= 8ms 直接刷；否则挂起一个一次性定时器稍后放行。
        // 这样既不会让不断到来的鼠标事件把刷新无限推后（持续输入仍按时刷），又合并了同刻突发的多次请求。
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_annoRefreshLock)
        {
            if (_annoRefreshPending) return; // 已有挂起刷新，跳过（合并）
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

    /// <summary>标注结构变化（加载/撤销/重做/改色/旋转/设备重建）→ 整层重建并刷新画面。</summary>
    public void InvalidateOverlay()
    {
        _annoLayerNeedsFullRebuild = true;
        try { ScreenCanvas.Invalidate(); } catch { }
    }

    /// <summary>刷新页位图层与标注层（滚动/缩放/旋转/主题/尺寸等视口变化时用）。整层重建标注。
    /// 标注与页位图共用 ScreenCanvas 单交换链 → 双画布相位脱节不再存在。</summary>
    private void InvalidateBoth()
    {
        _annoLayerNeedsFullRebuild = true;
        ScreenCanvas.Invalidate();
    }

    // 单条标注的已编译 CanvasGeometry 缓存：
    //   PointsVersion/Color/Thickness 任一变化 → 重建
    // 标注不变化时（绝大多数帧）跳过 PathBuilder→Geometry 的重建，直接 DrawGeometry 零分配。
    private sealed class CachedAnnoGeometry
    {
        public int PointsVersion;
        public string? ColorHex;
        public double Thickness;
        public Microsoft.Graphics.Canvas.Geometry.CanvasGeometry? Geometry; // IDisposable
        public Windows.UI.Color ParsedColor;
        // 累积层已烘入的点数：当前笔画增量烘入时，只画 [BakingPoints, Count) 这一段，
        // 避免每帧整体重建整条长笔画（长笔画 → 几何巨大 → GPU 顶点负载飙升）。
        public int BakingPoints;
    }
    // Key = AnnotationViewModel 的引用哈希（不重写 GetHashCode，用 RuntimeHelpers）
    // 用 ConditionalWeakTable 不用 Dictionary：标注被 GC 回收时缓存自动释放，不会内存泄漏
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<AnnotationViewModel, CachedAnnoGeometry>
        _annoGeoCache = new();

    // ---- 已完成笔画累积层：一次性烘入离屏位图，绘画时只实时画"当前一笔" ----
    // 若不累积，每帧都要对所有已完成笔画做 GetPointsSnapshot(分配)+GetValue+DrawGeometry，
    // 笔画一多 + 120fps 就是纯浪费。累积后每帧 = clear + 1 次 DrawImage(+ 当前笔)，与笔画总量无关。
    private Microsoft.Graphics.Canvas.CanvasRenderTarget? _annoLayer;
    private bool _annoLayerNeedsFullRebuild = true;                    // 视口/结构变化 → 整层重建

    /// <summary>
    /// </summary>
    public double PenThicknessPoints
    {
        get => _penThicknessPoints;
        set => _penThicknessPoints = value;
    }
    private double _penThicknessPoints = 3.5;

    // 手势（右键拖拽缩放 / 左键标注笔画）是否正在进行（见各 Pointer 处理函数）。
    // 保留空实现作为"手势进行中"的规范化标记点；环形光标作用域由 MainPage 的
    // UpdateRingScope（SetSystemCursor + 窗口内/前台判定）负责，这里无需额外标志。
    private void StartPenCursorHold()
    { }

    private void StopPenCursorHold()
    { }

    /// <summary>标注指针事件（页局部坐标，PDF 点）。Pressed 仅在按点落在某页内时触发。</summary>
    public event EventHandler<GpuAnnotationPointerArgs>? AnnotationPointerPressed;
    public event EventHandler<GpuAnnotationPointerArgs>? AnnotationPointerMoved;
    public event EventHandler<GpuAnnotationPointerArgs>? AnnotationPointerReleased;

    private bool _isAnnotating;      // 左键标注笔画进行中
    private int _annotatingPage = -1; // 笔画锁定的页（拖出页面仍算这页，与旧路径捕获行为一致）

    /// <summary>视口坐标 → 命中页 + 页局部坐标（PDF 点，未旋转）。requireInside=true 时点必须落在某页内。</summary>
    private bool TryViewportToPage(Point vp, bool requireInside,
        out int pageIndex, out double px, out double py)
    {
        pageIndex = -1; px = 0; py = 0;
        var pages = _pages;
        double z = Viewer.ZoomFactor;
        if (pages.Count == 0 || z <= 0) return false;

        // 与 Draw 相同的居中补偿（内容窄于视口时 ScrollViewer 居中呈现）。
        double extentW = (double.IsNaN(Spacer.Width) ? 0 : Spacer.Width) * z;
        double extentH = (double.IsNaN(Spacer.Height) ? 0 : Spacer.Height) * z;
        double centerX = extentW < Viewer.ViewportWidth ? (Viewer.ViewportWidth - extentW) / 2.0 : 0;
        double centerY = extentH < Viewer.ViewportHeight ? (Viewer.ViewportHeight - extentH) / 2.0 : 0;

        double docX = (vp.X - centerX + Viewer.HorizontalOffset) / z;
        double docY = (vp.Y - centerY + Viewer.VerticalOffset) / z;

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

    /// <summary>已锁定页：视口坐标 → 该页局部坐标（笔画拖出页面边界时继续跟踪）。</summary>
    private bool ViewportToLockedPage(int pageIndex, Point vp, out double px, out double py)
    {
        px = 0; py = 0;
        var pages = _pages;
        double z = Viewer.ZoomFactor;
        if (pageIndex < 0 || pageIndex >= pages.Count || z <= 0) return false;

        double extentW = (double.IsNaN(Spacer.Width) ? 0 : Spacer.Width) * z;
        double extentH = (double.IsNaN(Spacer.Height) ? 0 : Spacer.Height) * z;
        double centerX = extentW < Viewer.ViewportWidth ? (Viewer.ViewportWidth - extentW) / 2.0 : 0;
        double centerY = extentH < Viewer.ViewportHeight ? (Viewer.ViewportHeight - extentH) / 2.0 : 0;

        double docX = (vp.X - centerX + Viewer.HorizontalOffset) / z;
        double docY = (vp.Y - centerY + Viewer.VerticalOffset) / z;
        PageLocalFromDoc(pages[pageIndex], docX, docY, out px, out py);
        return true;
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

    // ---- 叠加层绘制 ----

    private static readonly Microsoft.Graphics.Canvas.Geometry.CanvasStrokeStyle PenStrokeStyle = new()
    {
        StartCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round,
        EndCap = Microsoft.Graphics.Canvas.Geometry.CanvasCapStyle.Round,
        LineJoin = Microsoft.Graphics.Canvas.Geometry.CanvasLineJoin.Round,
    };

    /// <summary>设置"页点坐标(DIP) → 屏幕"的变换矩阵。入参 ds 通常为恒等变换的会话。</summary>
    private void ApplyPageTransform(CanvasDrawingSession ds, Rect dest, double z)
    {
        int rot = _rotation;
        bool swap = rot == 90 || rot == 270;
        double w = swap ? dest.Height : dest.Width;   // 未旋转屏幕宽高
        double h = swap ? dest.Width : dest.Height;
        float cx = (float)(dest.X + dest.Width / 2.0);
        float cy = (float)(dest.Y + dest.Height / 2.0);
        // 吸入设备像素，抑止缩放动画期"笔迹震动"：页位图在共一 dest 上被 GPU 线性采样 → 亚像素平滑，
        // 而矢量化笔迹是锐利细线，若原点停在亚像素上会在相邻像素行间来回"游泳"。把未旋转页的
        // 左上原点取整到整像素，几何整体贴到像素网格 —— 与页面的偏差恒 <1px 且随缩放缓变，无高频抖动。
        var unrotOrigin = new Vector2(
            (float)Math.Round(cx - w / 2.0),
            (float)Math.Round(cy - h / 2.0));
        float s = (float)(z * 96.0 / 72.0); // 页点 → 屏幕像素

        var old = ds.Transform;
        // System.Numerics 是行向量约定（v·M，左矩阵先作用）：页点先 ×s 缩放，再平移到
        // dest 原点，最后绕 dest 中心补旋转 —— 与内核底图绘制 DrawPreviewScene 的放置规则完全一致。
        // 注意勿写成 Translation*Scale：那会把 unrotOrigin 也乘 s，标注随缩放漂移。
        var m = Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(unrotOrigin);
        if (rot != 0)
            m *= Matrix3x2.CreateRotation((float)(rot * Math.PI / 180.0), new Vector2(cx, cy));
        ds.Transform = m * old;
    }

    /// <summary>绘制单条笔画（当前会话须已施加正确的页变换）。几何已缓存则零分配。</summary>
    private void DrawAnnotation(CanvasDrawingSession ds, AnnotationViewModel a)
    {
        // 跨线程安全取点快照（Points 内部可能正被 UI 线程 AppendPoint 修改）
        Point[] pts = a.GetPointsSnapshot();
        if (pts.Length == 0) return;

        // 颜色/粗细/版本一致则复用缓存的 CanvasGeometry：省下 PathBuilder 构建 + 几何扁平化
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
            // 缓存失效 → 释放旧几何，重新解析颜色 + 重建几何
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
            // 单点不缓存（无 Geometry，下面直接 FillCircle 即可）
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

    /// <summary>当前可见页中是否存在画笔标注（含当前正在書写的笔画）。无则跳过整个标注渲染块。</summary>
    private bool HasVisiblePenAnnotations(List<(GpuPageInfo Page, Rect Dest)> visible)
    {
        foreach (var (p, _) in visible)
        {
            var annos = SafeAnnos(p.Index);
            if (annos == null) continue;
            foreach (var a in annos)
                if (a.Type == AnnotationType.Pen) return true;
        }
        return false;
    }

    /// <summary>按画布尺寸懒创建累积层（缩放/画布尺寸变化时重建并整层重排）。</summary>
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
        }
    }

    /// <summary>所有可见页标注整层重排（视口结构/视口变化后调用），并重置各笔画"已烘入点数"。</summary>
    private void RebuildAnnoLayer(List<(GpuPageInfo Page, Rect Dest)> visible, double z)
    {
        if (_annoLayer == null) return;
        using var lds = _annoLayer.CreateDrawingSession();
        lds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        foreach (var (p, dest) in visible)
        {
            var annos = SafeAnnos(p.Index);
            if (annos == null) continue;
            ApplyPageTransform(lds, dest, z);
            foreach (var a in annos)
            {
                if (a.Type != AnnotationType.Pen) continue;
                var cache = _annoGeoCache.GetValue(a, _ => new CachedAnnoGeometry());
                DrawAnnotation(lds, a);              // 整段烘入（含当前一笔当前已有点）
                cache.BakingPoints = a.GetPointsSnapshot().Length; // 重置已烘入点数
            }
        }
        _annoLayerNeedsFullRebuild = false;
    }

    /// <summary>增量：把"尚未超过已烘入点数的笔画"追加烘入累积层。
    /// 已完成的笔画再整段烘入一次；当前正在画的笔画只烘入【新增长的那一小段】，
    /// 从而每帧成本与笔画长度无关（长笔画不再每帧整体重建 → GPU 顶点负载骤降）。</summary>
    private void BakeUnbakedStrokes(List<(GpuPageInfo Page, Rect Dest)> visible, double z)
    {
        if (_annoLayer == null) return;
        using var lds = _annoLayer.CreateDrawingSession();
        foreach (var (p, dest) in visible)
        {
            var annos = SafeAnnos(p.Index);
            if (annos == null) continue;
            // 先探测本页是否有笔画需要追加，有才设变换并烘入，减少每页无谓设置变换。
            bool any = false;
            foreach (var a in annos)
            {
                if (a.Type != AnnotationType.Pen) continue;
                var cache = _annoGeoCache.GetValue(a, _ => new CachedAnnoGeometry());
                if (a.GetPointsSnapshot().Length > 0 && a.GetPointsSnapshot().Length > cache.BakingPoints)
                { any = true; break; }
            }
            if (!any) continue;
            ApplyPageTransform(lds, dest, z);
            foreach (var a in annos)
            {
                if (a.Type != AnnotationType.Pen) continue;
                var cache = _annoGeoCache.GetValue(a, _ => new CachedAnnoGeometry());
                DrawStrokeIntoLayer(lds, a, cache);
            }
        }
    }

    /// <summary>把某笔画"尚未烘入"的部分追加画进累积层，并推进 BakingPoints。
    /// 完全没烘过 → 整段烘入（首笔）；已烘过 → 只画增量段（当前一笔持续增长）。</summary>
    private void DrawStrokeIntoLayer(CanvasDrawingSession lds, AnnotationViewModel a, CachedAnnoGeometry cache)
    {
        Point[] pts = a.GetPointsSnapshot();
        if (pts.Length == 0) return;
        if (pts.Length <= cache.BakingPoints) return; // 无新增

        // 颜色/粗细变化需重解析并丢弃旧几何缓存
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
            // 单点：画一个圆点
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

    /// <summary>解析标注颜色（6 位 hex 补 FF 全不透明度）。</summary>
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
        // 切换前的 _layoutSwitchAnchor，由布局切换的 Low 收尾钉死。
        if (!_layoutSwitchInFlight)
        {
            // 当前页 = 与视口主轴重叠量最大的页（pages 按主轴递增，起点超出视口即可停）。
            int best = _currentPage >= 0 ? Math.Min(_currentPage, pages.Count - 1) : 0;
            double bestOverlap = -1;
            foreach (var p in pages)
            {
                double a, b; // 该页在主轴上的 [a, b]
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

    public GpuPageView()
    {
        InitializeComponent();
        // 滚轮：逻辑处理器挂在 Viewer(ScrollViewer 自身) 上、且用 HandledEventsToo 订阅，
        // 保证必定接管（ScrollViewer 原生处理时会把路由事件标 Handled，普通订阅会被整段吞掉——
        // “横向下滚不动”正是这个根因）。纵/横滚轮现在都走统一弹簧引擎，不再交还原生滚动。
        // 另在子级透明 Spacer 上挂一个阻断器：它在冒泡【早期】(子级先于 ScrollViewer) 把事件标 Handled，
        // 跳过原生 ScrollViewer 滚轮类处理程序，避免原生去移动那块透明 Spacer（画布是兄弟元素、碰不到，
        // 阻断它零视觉丢失）。阻断器无逻辑、不会与 Viewer 上的逻辑处理器重复处理。
        Viewer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Viewer_PointerWheelChanged), true);
        Spacer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Viewer_PointerWheelBlocker), true);
        // 关键：手势指针事件订阅在控件自身（this），而非内部 Viewer。
        this.PointerPressed += Viewer_PointerPressed;
        this.PointerMoved += Viewer_PointerMoved;
        this.PointerReleased += Viewer_PointerReleased;
        this.PointerCaptureLost += Viewer_PointerCaptureLost;
        // （窗口外/标题栏/非子元素区域）后 PointerMoved 不再触发、手势无法自愈。
        // 订阅 PointerExited 主动收尾：一旦指针离开本视图立即终止右键缩放，避免回来后
        // 右键仍按住、以陈旧起点继续缩放造成视点跳变（用户报告的拖出区域不停止 bug）。
        this.PointerExited += Viewer_PointerExited;
        // ── 滚动条滑块拖拽抑制（对齐 Sumatra 快拖静止行为；2026-09-13 CPU 风暴根因修复）──
        // ScrollViewer 内置 ScrollBar 的 Thumb 拖拽期间抑制渲染请求（缓存瓦片照画、视口照常移动），
        // 静止 ≥ScrollDragSettleDelayMs 或松手时才对当前视口补发 —— 消灭"每个中间位置都全速解码
        // 白跑"的 CPU 风暴。分屏 SyncScrollBar 由 MainPage 在其 Scroll 事件里转发（ThumbTrack/
        // EndScroll），见 SyncScrollBar_Scroll。
        // 注意：WinUI3 的 Thumb 事件是普通 CLR 事件（无 WPF 式可 AddHandler 的路由 RoutedEvent），
        // 只能在模板树中找到 Thumb 实例后订阅；模板在 Loaded 时才就绪，故挂 Viewer.Loaded。
        Viewer.Loaded += (s, e) => HookScrollBarThumbs(Viewer);
        this.Unloaded += (s, e) => _hookedThumbs.Clear(); // 视图重挂载后模板 Thumb 是新实例，清旧引用
        UpdateThemeColors();
        ActualThemeChanged += (s, e) => { UpdateThemeColors(); InvalidateBoth(); };
        // 冷启动(文件关联)加载 PDF 时 GpuViewer 由 Collapsed → Visible：此刻窗口已按保存主题激活，
        // 但 ActualTheme 可能尚未经布局通道传播到本控件。Visible 时强制用确定性主题源刷新一次
        // RootGrid/画布底色，杜绝首帧以浅色(系统主题)首现的闪白光。
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

    // ---------------- 主题背景色（跟随深浅模式，画布内绘制无法用 ThemeResource，须手动解析） ----------------

    private Windows.UI.Color _gapColor;         // 页间/画布背景
    private Windows.UI.Color _placeholderColor; // 未出图页占位

    private void UpdateThemeColors()
    {
        bool isDark;
        try
        {
            // 确定性主题源：优先取 MainWindow.WindowRequestedTheme（ApplySavedTheme 已按保存
            // 主题设置），Default 时回落系统主题。不用 ActualTheme——它在 RequestedTheme 设置后要经
            // 布局通道异步传播，冷启动(文件关联)加载 PDF 时 GpuViewer 首帧可见前可能仍停留在系统主题
            // (浅色)，导致 RootGrid/画布以浅色首现而闪白光。
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
            _placeholderColor = Windows.UI.Color.FromArgb(255, 42, 42, 42);  // 比背景略亮，暗示页面轮廓，降低白图落地的亮度落差
        }
        else
        {
            _gapColor = Windows.UI.Color.FromArgb(255, 243, 243, 243);       // #F3F3F3 = SolidBackgroundFillColorBase(Light)
            _placeholderColor = Windows.UI.Color.FromArgb(255, 249, 249, 249); // 接近纸面的柔和灰白
        }

        // RootGrid 背景：显式写入主题色，替代 ThemeResource(SolidBackgroundFillColorBaseBrush)，
        // 消除 ActualTheme 异步传播竞态，保证画布区域首帧前即为正确主题色。
        if (RootGrid.Background is SolidColorBrush rootBrush && rootBrush.Color == _gapColor)
        {
            // 颜色未变，无需重设
        }
        else
        {
            RootGrid.Background = new SolidColorBrush(_gapColor);
        }

        // CanvasControl 忽略 XAML Background 属性；其 ClearColor 决定 Draw 前(含首帧前)的画布底色
        // (默认透明黑)。显式设为主题色，并仅在变化时设置，避免多余 Invalidate 重绘。
        try
        {
            if (ScreenCanvas.ClearColor != _gapColor)
                ScreenCanvas.ClearColor = _gapColor;
        }
        catch { }
    }

    // ---------------- 加载与布局 ----------------

    /// <summary>载入 PDF：取页数与每页尺寸，计算文档布局，撑出滚动范围。</summary>
    public async Task LoadAsync(PdfRenderService svc, ViewStateData? restoreView = null)
    {
        RenderService = svc;
        _didInitialFit = false;
        _pendingRestore = null; // 切换文档：丢弃上一文档可能排队的待还原状态
        _restoreRetries = 0;    // 同上：丢弃上一文档可能排队的还原重试
        _liveH = 0; _liveV = 0; _liveZoom = 1.0; // 重置实时镜像，等待本次文档的 fit/还原写入
        _currentPage = -1;
        _lastZoomNotified = -1;
        lock (_lock)
        {
            _docGeneration++;
            _lastDirLead = double.NaN;
            _scrollDir = 0;
        }
        uint count = await svc.GetPageCountAsync();
        // 批量取全部页尺寸：一次 FFI 调用 + 一次引擎锁持有，替代 N 次逐页往返
        //（大 PDF 数千页时省去 N 次跨语言调用/锁竞争，冷启动更快）。
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
        RecomputeLayout(pages);

        // RenderCache：新文档 = 新缓存（Sumatra 打开新文档 = 新 DisplayModel + FreeForDisplayModel 旧缓存）。
        ShutdownRenderCache();
        _renderCache = new TileRenderCache(this, DispatcherQueue);

        // 若本次加载后需精确还原（如保存/另存为后重载同一文档），先排好待还原，
        // 使下方 TryInitialFit 跳过"适应宽度"、直接由还原落定视口——
        // 这样就不会有异步 fit ChangeView 在还原之后落地、把视口拽回首页/适应宽度。
        // 保证保存前后缩放与滚动位置完全一致，批注阅读位置不丢失。
        // ⚠ 必须走 RestoreView 而非直接赋 _pendingRestore：RestoreView 会先还原【布局模式】
        // （横向 LayoutMode=1 → RecomputeLayout + LayoutModeChanged），偏移才能按正确主轴落定。
        // 第41轮改成 LoadAsync 直接传 restoreView 后曾只赋 _pendingRestore，LayoutMode 还原丢失
        // → 重开横向布局 PDF 时按默认纵向布局 + 横向偏移错轴 = "横向布局关闭后无法记忆"。
        // _didInitialFit=false 时 RestoreView 仅还原 LayoutMode + 排队，不立即应用，语义与原来一致。
        if (restoreView != null)
            RestoreView(restoreView);

        // 冷启动热页：按还原位置预解析目标页 DisplayList（使该页首块瓦片渲染直接命中缓存、
        // 跳过内容流解析）。仅需还原偏移 + 当前布局即可推导，不必等视口落定；第 0 页已在
        // 批量取尺寸阶段由 native 顺带预解析，故还原到非首页时这里才补热。失败/越界静默兜底。
        if (_pendingRestore != null)
        {
            int warmPage = RestoreTargetPage(_pendingRestore);
            if (warmPage > 0)
                _ = svc.WarmupPageAsync((uint)warmPage);
        }

        // 【重开闪首页修复】视口可能尚未就绪（TryInitialFit 会直接 return），若此刻不先播种
        // _live*，下方 QueueVisiblePages 会按顶部视口排队渲染首页；随后 Viewer_SizeChanged
        // 才触发还原，首页已出图/上屏 → "刚打开闪一下首页"。提前把实时镜像与可见范围播种到
        // 还原位置，使首帧渲染/绘制都从记忆位置开始（TryApplyPendingRestore 阶段1 会再次
        // SyncLiveView，幂等无害）。
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

    /// <summary>
    /// 冷启动热页用：由还原偏移 + 目标缩放推导主轴视口起点落在哪一页（连续布局下页首尾相接，
    /// 起点所在的页即首屏目标页）。UI 线程调用，要求 _pages 与布局已就绪（RestoreView 之后）。
    /// 旋转文档的偏移坐标系与当前（未旋转）布局不一致，可能推导偏差一页——热页仅为提示，
    /// 偏了也只是回退为"首瓦片渲染时自行解析"，无副作用。
    /// </summary>
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
            if (lead < b) { best = p.Index; break; } // 该页主轴区间覆盖 lead（a ≤ lead < b）
        }
        return best;
    }

    /// <summary>
    /// 保存/另存为成功后重新挂接底层渲染引擎：仅替换引擎句柄，【刻意保留】旧瓦片缓存，
    /// 【刻意不】重建布局、不触碰视口、不重构建可见页。保存对画面完全无感——
    /// 不触发淡入、不出现"笔迹浮在淡入位图之上"的闪烁。
    ///
    /// 为什么旧瓦片可保留（核心依据）：
    ///   1. 瓦片像素是内核按【未旋转页帧】渲染出的 native BGRA 缓冲 + Win2D CanvasBitmap，
    ///      与已 Dispose 的 PdfRenderService(引擎) 无关——换引擎只影响后续瓦片的来源。
    ///   2. 批注是真实 PDF 注解对象（非内容流像素），渲染引擎不渲染批注（render_annotations=off）——
    ///      保存只增删注解对象、不改页面内容流 → 新旧引擎渲染出的瓦片完全一致 → 旧瓦片仍有效。
    ///   3. 瓦片始终按"未旋转"页渲染（旋转靠 GPU 变换），故旋转归零后瓦片依然匹配新布局
    ///      （RecomputeLayout 只交换 LayoutRect 维度、不改瓦片几何）。
    /// </summary>
    public void ReattachRenderService(PdfRenderService svc)
    {
        RenderService = svc;

        // 引擎句柄已更换（保存/另存为后 reload 同一文档）：在途渲染必须排空到旧引擎上完成，
        // 否则旧 worker 还持着已 Dispose 的引擎在渲染。缓存条目保留——瓦片像素与引擎无关
        // （同一文档同一内容），等效 Sumatra KeepForDisplayModel 的"可见图保留、不闪重铺"。
        _renderCache?.CancelRenderingBlocking(this);
        _renderCache?.AbortRendering(this);

        // 旋转已烘焙进导出文件（保存流已把 page.RotationAngle 归零），视图旋转同步归零以匹配
        // 新引擎（pristine 未旋转副本）。_rotation 原即为 0 时 RecomputeLayout 是 no-op，视口零扰动；
        // 仅当原文档带旋转时才重算布局（Spacer 维度随旋转交换），属必要的几何同步。
        // 注：重算只改 LayoutRect 维度，不清位图缓存——位图按未旋转渲染、旋转归零后仍匹配。
        if (_rotation != 0)
        {
            _rotation = 0;
            if (_pages.Count > 0) RecomputeLayout(_pages);
        }

        lock (_lock)
        {
            _docGeneration++;      // 使旧引擎的在途渲染任务自废（gen 不匹配即 return）
            _lastDirLead = double.NaN;
            _scrollDir = 0;
        }

        // 仅按"当前（未变的）滚动偏移/缩放"重新评估可见窗口——已缓存页直接命中、不重光栅；
        // 未缓存页（极少，如刚滚入尚未渲染的）才用新引擎正常渲染。绝不重排视口。
        UpdateVisibleRange();
        QueueVisiblePages();
        InvalidateBoth();
        NotifyViewState();
    }

    /// <summary>双列对开的"行"：FirstPage..LastPage（1-based，含端点）；单行单页时两者相等。</summary>
    private readonly struct FacingRow
    {
        public FacingRow(int first, int last) { FirstPage = first; LastPage = last; }
        public int FirstPage { get; }
        public int LastPage { get; }
    }

    /// <summary>
    /// 把页配对成双列对开的"行"。移植自 sumatrapdf-master/src/DocumentLayout.cpp CollectFacingRows
    /// （省略 spread 分支：本应用无横向跨页 spread）。
    /// - bookView：封面页 1 独占一行 (1,1)，其后 (2,3)(4,5)…
    /// - 否则（facing）：(1,2)(3,4)…；末尾单页独占一行 (n,n)。
    /// </summary>
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

    /// <summary>
    /// 按当前【布局方向 + 旋转角】计算文档布局（90/270° 交换宽高），并撑出滚动范围。UI 线程调用。
    /// 两种方向都是连续布局：所有页首尾相接串在主轴上，交叉轴居中。
    /// </summary>
    private void RecomputeLayout(IReadOnlyList<GpuPageInfo> pages)
    {
        bool swap = _rotation == 90 || _rotation == 270;

        if (_layoutMode == PageLayoutMode.HorizontalContinuous)
        {
            // 横向连续：页自左而右串接，在文档内垂直居中；页间距用更紧凑的 PageGapHorizontal。
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
            // 单页：垂直单列、文档内水平居中（原单列行为，零回归）。
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
        // RelayoutFacingWithSpreads（省略 spread / R2L 分支）。每行顶对齐、行高取行内最大页高；
        // 左列右对齐到右栏左缘、右列左对齐；整体以 DocMargin 为左右边距，不随视口缩放（与单列一致）。
        var rows = CollectFacingRows(pages.Count, _arrangement == PageArrangement.Book);
        double colMax0 = 0, colMax1 = 0;
        double currPosY = DocMargin;
        // 第一遍：确定每列最大宽与每行高度（顶对齐，x 暂置 0）。
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
        // 第二遍：按列位置定 x（左列右对齐、右列左对齐；封面页放右栏）。
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
        UpdateVisibleRange(); // 内部同步 _vpW/_vpH 快照
        InvalidateBoth();
    }

    /// <summary>首次拿到有效视口时：有保存的阅读位置则还原，否则按当前布局主轴适应（均只跑一次）。</summary>
    private void TryInitialFit()
    {
        if (_didInitialFit) return;
        var pages = _pages;
        if (pages.Count == 0) return;
        // 主轴视口/内容：纵向→宽/Spacer.Width（适应宽度）；横向→高/Spacer.Height（适应高度）。
        double viewportMain, contentMain;
        if (IsHorizontalLayout) { viewportMain = Viewer.ViewportHeight; contentMain = Spacer.Height; }
        else { viewportMain = Viewer.ViewportWidth; contentMain = Spacer.Width; }
        if (viewportMain <= 0 || double.IsNaN(contentMain) || contentMain <= 0) return;

        _didInitialFit = true;

        // 有保存的阅读位置 → 还原优先于适应，且因 _didInitialFit 已置位不会被后续覆盖。
        // 范围已就绪则 TryApplyPendingRestore 同步精确应用（首帧即从该位置绘制、无闪）；
        // 若本方法在 Viewer_SizeChanged 内同步执行时 ScrollViewer 的可滚动范围尚未随本次布局刷新
        // （非 0 偏移会被 ChangeView 当场夹到 0，缩放不受影响）→ 保留 _pendingRestore，
        // 待 Viewer_ViewChanged（范围已就绪）一次性重落定。该机制只在加载时触发、无低优先级迟发，
        // 故绝不会在用户交互（如双击切换缩放）之后把视图猛拽回保存位置。
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

    // ---------------- 视口跟踪 ----------------

    private void Viewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_zoomAnimCommitting)
        {
            // 【这里曾经制造"双击缩放闪一帧别处画面"】原先无条件关窗并往下同步 _live*。
            // 但双击/按钮缩放全程【不】逐帧 ChangeView（见 StepZoomFrame 注释，为性能刻意为之），
            // ScrollViewer 一直停在起始视图，直到 FinishZoom 一次性跳到目标 —— 而目标偏移是按
            // 【新 zoom】算的，提交那一刻 ScrollViewer 的 extent 还是【旧 zoom】下的小尺寸，
            // 于是偏移被夹断。第一次回吐的 ViewChanged 带着这个夹断值，被当成权威写进 _live*，
            // 画布下一帧照它画出来 = 闪一帧别的位置；下一次 ViewChanged 落到真值后又跳回来。
            //
            // 现在：没落到位就地返回 —— 不关窗（画布继续读 _zoomAnimCurrent*，即正确的目标态）、
            // 更不拿这个中间值污染 _live*。若 zoom 已就位只是偏移没跟上，说明 extent 此刻已经变大，
            // 顺手补发一次把偏移推到目标。真正到位的那次 ViewChanged 才关窗并走完下面的常规流程。
            if (!IsCommittedViewReached())
            {
                // 与看门狗共用同一套推进判定。上一轮正是因为看门狗漏了"zoom 未落地不得补发"
                // 这道守卫（本处有、那处没有），双击缩放被 null-zoom 的补发整个顶掉。
                if (!TryAdvanceCommit()) ForceCloseCommitWindow();
                else ScheduleCommitWatchdog(); // 万一后续不再有 ViewChanged，仍有兜底收尾
                return;
            }
            _zoomAnimCommitting = false; // ChangeView 确认落地 → 画布可切回读 Viewer 状态
        }

        // 注意：待还原(_pendingRestore)的落定完全由 TryApplyPendingRestore 的 Low 优先级自重试驱动，
        // 不在本处理用户交互的 ViewChanged 里应用，避免双击/缩放时把视图猛拽回保存位置（闪动）。

        // 注：滚动弹簧的目标不需要在这里清 —— 每段新滚动都由 EnsureScrollSprings 从
        // 当前真实位置(_live*)重新播种，外部来源（滚动条拖动/键盘导航）改动的偏移会自动被吸收。

        // 平滑缩放 / 滚轮平滑滚动 / 左键拖拽平移的动画帧：视图由各自定时器/指针处理器推进
        //（自管 _zoom/_vis* 并重绘），这里直接跳过，避免与 PointerMoved/PanApplyTick 重复干活。
        if (_isSmoothZooming || _isWheelScrolling || _isDragPanning) return;

        // 布局切换稳定窗口(_layoutSwitchInFlight)：仅在 ScrollViewer 真实到达 Reanchor 播种的目标
        // 偏移时才关窗并把真落点读入 _live*。此前一律不读 Viewer.*（保留 Reanchor 的权威镜像）——
        // 否则把被旧 extent 夹断的 0/中间值当权威写进 _vis* → 页码/侧边栏瞬跳 1 而视图不动。
        // 收尾改在"到达"判定里（而非固定 Low 时序），可跨过滚动范围刷新引发的后续回读事件；
        // 若目标被极端夹断再无 ViewChanged 到达，由看门狗定时器兜底强制收尾。
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
            if (!landed && !timedOut) return; // 未落定 + 未超时：继续等，绝不污染 _live*

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
            NotifyViewState(); // 缩放回显（页码已钉死到锚页，不会回跳）
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // 停稳（idle 滚动/缩放落定）：把实时镜像同步成 Viewer 当前值，确保 _live* 始终是权威最近视图
        // （双击/还原起点统一读 _live*，避免读到异步 ChangeView 未落地的 0）。
        // 还原进行中(_pendingRestore!=null)不同步：还原窗口内 Viewer.* 可能仍是被夹断的旧值(0)，
        // 同步会把已置为还原值的 _live* 覆盖回 0（闪到顶部）。
        // 布局切换稳定窗口(_layoutSwitchInFlight)同理不同步/不 UpdateVisibleRange：Reanchor 已在
        // 切换后播种正确的 _live*/_vis*，此时 Viewer.* 可能仍是被旧 extent 夹断的偏移，读了会覆盖成 0 →
        // 触发"页码/侧边栏跳到 1 而视图不动"；该窗口由布局切换的 Low 收尾锚定关闭。
        if (_pendingRestore == null && !_layoutSwitchInFlight)
        {
            SyncLiveView(Viewer.HorizontalOffset, Viewer.VerticalOffset, Viewer.ZoomFactor);
            // 闲置同步：画布视口快照跟到实时值，与 _live* 配套（落定后滚动条状态已稳定）。
            _vpW = Viewer.ViewportWidth;
            _vpH = Viewer.ViewportHeight;
            if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
            UpdateVisibleRange();
        }
        else if (!_layoutSwitchInFlight)
        {
            // 还原窗口(_pendingRestore!=null)：不读 ScrollViewer 可能被夹断的偏移，仅更新视口快照，
            // 保持还原路径自己播种的 _vis*（维持原有语义）。
            _vpW = Viewer.ViewportWidth;
            _vpH = Viewer.ViewportHeight;
            if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
            UpdateVisibleRange();
        }
        // 布局切换稳定窗口(_layoutSwitchInFlight)：一律不读 Viewer.*（连视口快照也不覆盖），
        // _vpW/_vpH 与 _vis*/_live* 均已由 Reanchor 的 SetVisibleRangeFor 播种为目标值。

        // 缩放/滚动进行中只排"缺图"的页；停稳后才排"分辨率升级"的重光栅（旧图 GPU 缩放顶着）。
        // 右键拖拽缩放期间同样视为"进行中"，松手时统一升级。（左键拖拽已在上方早退，不会走到这里）
        QueueVisiblePages();
        // 视口画布轻量重画：只画 2-3 张已缓存位图，惯性滚动逐帧调用无压力。
        InvalidateBoth();
        NotifyViewState(); // 页码/缩放回显（仅变化时发事件）
        ViewportChanged?.Invoke(this, EventArgs.Empty); // 驱动中间同步滚动条
        // 注意：实时镜像 _live* 的 idle 同步已在上方「if (_pendingRestore == null)」块内完成（第2332行）。
        // 此处【绝不】再无条件 SyncLiveView(Viewer.*) —— 还原窗口(_pendingRestore!=null)内 ScrollViewer 可能仍
        // 停在顶端(阶段1只落缩放、阶段2才落偏移)，若在此覆盖会把已置为还原值的 _live* 拉回 0 → 重开飞到左上角
        // （缩放不受影响，因为 Viewer.ZoomFactor 已对）。第2332行的守卫是唯一的同步点。

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
        // 闲置状态：视口快照与 Viewer 同步（滚动条状态已稳定，画布据此绘制）。
        _vpW = Viewer.ViewportWidth;
        _vpH = Viewer.ViewportHeight;
        if (_vpW <= 0 || _vpH <= 0) { _vpW = 1000; _vpH = 800; }
        lock (_lock)
        {
            _visLeft = hOff / z;
            _visRight = (hOff + _vpW) / z;
            _visTop = vOff / z;
            _visBottom = (vOff + _vpH) / z;
            _zoom = z;
            _dpiScale = dpi;
            UpdateScrollDirLocked();
        }
    }

    /// <summary>
    /// 按指定 偏移/缩放 直接设置可见范围与光栅缩放（不读 Viewer.*）。
    /// 用于还原路径：还原的 ChangeView 是异步落地，此期间 Viewer.* 仍是旧值（顶部），
    /// 若用 UpdateVisibleRange 会把 _visTop/_zoom 设成顶部/旧缩放 → 光栅化错页面。
    /// </summary>
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
            _zoom = zoom;
            _dpiScale = dpi;
            // 还原/跳页是"瞬移"，不代表滚动方向 → 重置基准，避免窗口被一次大跳变带偏。
            _lastDirLead = IsHorizontalLayout ? _visLeft : _visTop;
            _scrollDir = 0;
        }
    }

        // ---- 首开早期内存回收（把「静止/滚动才触发的那次回升」提前到载入落定后）----
    // 实测：单进程 MuPDF 首开时解析各页 DisplayList，进程内存冲到 ~1.2GB，几次 GC 后
    //（或滚动触发 realloc）回落。本巡检把「GC 整理 + 引擎 reload」一并提前执行：
    //   静态且无在途构建短暂后：先对主进程做一次强制 GC（把首开一次性大分配当垃圾回收），
    //   再 reload 引擎一次（释放 MuPDF 累积的逐页对象/图像缓存）。
    private const int WorkerCacheFlushIntervalMs = 2500;   // 巡检间隔（更密，让首开回落更早发生）
    private const long WorkerCacheIdleMs = 1000;           // 静止且无在途构建多久触发首刷
    private const long WorkerCacheReFlushMs = 20000;       // 两次 GC/reload 的最小间隔（防止静止时每轮重放）
    private long _lastRenderActivityTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    // 初始把 _lastFlushTicks 拨到很久以前，使首刷不被 20s（WorkerCacheReFlushMs）冷却锁住。
    // 否则 sinceFlush 从载入时刻算起 <20000ms 恒为 "cool" skip，首开前 20s 一次都不 flush，
    // 首开那 ~750MB 临时原生缓冲只能等自然 gen2 GC（~5s），"提前" 也就无从谈起。
    private long _lastFlushTicks =
        System.Diagnostics.Stopwatch.GetTimestamp()
        - System.Diagnostics.Stopwatch.Frequency * (WorkerCacheReFlushMs / 1000);
    private static int _cacheFlushArmed = 1;

    /// <summary>
    /// 空闲巡检：静态且无在途瓦片渲染时，①GC 整理主进程 ②重载文档释放 MuPDF 页数据累积缓存。
    /// 只影响后台回收，不触碰已上屏的瓦片。首刷提前到载入落定后；再用 20s 冷却避免每次停顿重放。
    /// </summary>
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

                    // ②重载文档，释放 MuPDF 累积的页数据缓存
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

    // ---------------- 视口画布绘制 ----------------

    private void ScreenCanvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        // 设备(重)建：GPU 位图绑定特定设备 → 全部作废（CPU 瓦片缓存与 Sumatra 一致地保留，
        // 下一帧 Draw 惰性重建 GPU 副本）。复用旧 GPU 资源会抛 WrongResourceDomain → 闪退。
        _renderCache?.DropGpuResources();
        lock (_lock)
        {
            _lastDirLead = double.NaN;
            _scrollDir = 0;
        }
        // 标注几何/累积层也绑定该设备：复用旧资源在新设备上 Draw 会抛 WrongResourceDomain → 闪退，全部作废重排。
        foreach (var kv in _annoGeoCache)
        {
            kv.Value.Geometry?.Dispose();
        }
        _annoGeoCache.Clear();
        _annoLayer?.Dispose();
        _annoLayer = null;
        _annoLayerNeedsFullRebuild = true;
        // 设备就绪：重新排队可见页瓦片。
        // 修复首开概率性白屏：LoadAsync 排队时若设备未就绪，构建会因设备快照为 null 而退出；
        // 此前 CreateResources 只清不排 → 画面一直空白，直到用户点击触发 ViewChanged。
        //
        // 先从 ScrollViewer 读取真实缩放/DPI 再排队渲染（避免首屏用默认 _zoom=1.0 排队）。
        // 上屏恒 NearestNeighbor 直映（零信息丢失、无 Linear 抹圆/发虚）；内核按目标
        // 分辨率出图，故无密度阶梯。
        UpdateVisibleRange();
        QueueVisiblePages();
        InvalidateBoth();
    }

    /// <summary>
    /// 取当前视图状态（缩放/偏移）。平滑缩放进行中返回插值后的动画状态，
    /// 避免依赖 ScrollViewer.ChangeView 的异步落点（其在 60Hz 下会与画布绘制产生 1 帧错位抖动）。
    /// </summary>
    private void GetViewState(out double zoom, out double hOff, out double vOff)
    {
        // 优先级：平滑缩放/提交窗口 ＞ 拖拽平移 ＞ 闲置。
        // 必须让缩放态压过拖拽平移：双击手势会吞掉第二次指针抬起，_isDragPanning 可能仍卡 true，
        // 若先判拖拽平移会让画布画出陈旧拖拽偏移(起止错位闪帧)。二者互斥，缩放期间一律用插值态。
        if (_isSmoothZooming || _zoomAnimCommitting)
        {
            // 动画中/提交落地瞬间：正常返回插值态；但若插值态偏移≈0 而实时镜像 _live* 显著非0，
            // 说明 _zoomAnimCurrent* 是陈旧初值(还原从不赋值)或提交错位 → 回退 _live*，避免首帧/落地瞬间闪到顶部。
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
            // zoom 必须取 CurrentZoom(_liveZoom) 而非 Viewer.ZoomFactor：在缩放动画中途按下左键的场景里，
            // StopSmoothZoom 刚把新倍率提交给 ChangeView（异步、尚未落地），此刻 Viewer.ZoomFactor 还是旧倍率，
            // 而偏移 _panDisplay* 已按新倍率播种 —— 一旧一新配在一起，画面就会先错位闪一下、落地后再跳回来。
            zoom = CurrentZoom;
            hOff = _panDisplayH;
            vOff = _panDisplayV;
        }
        else if (_isWheelScrolling)
        {
            // 滚动进行中：以自管滚动镜像 _live* 为准，覆盖可能仍开着的缩放提交窗口
            // （缩放被滚动打断后 StopSmoothZoom 开了提交窗、但自此帧起视图由滚动弹簧驱动）。
            // 否则 GetViewState 仍返回缩放插值态、与帧泵每帧下发的滚动位置脱节，缩放动画结束后出现
            // 位置/倍率错乱的幽灵滚动。拖拽平移优先级更高（见上方 _isDragPanning），保持旧行为不变。
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
            // （异步 ChangeView 未落地/被夹断），_live* 才是权威最近视图 → 用 _live* 避免读到 0（闪到顶部）。
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

    /// <summary>每帧把可见页位图按当前 偏移/缩放 画到视口画布上（纯 GPU 拷贝+缩放）。</summary>
    private void ScreenCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        // ---- 动画步进：与本次上屏同帧完成，循环由 Win2D vsync 自持续 ----
        // 旧实现把步进放在 CompositionTarget.Rendering、上屏放在本 Draw，两套 vsync 队列相位脱节，
        // 在 120Hz 屏上表现为"一半帧是上一帧的重复"→ 肉眼 judder。现在步进紧跟在 Draw 之首、
        // 下面 GetViewState 即读刚算出的位置，且末尾 Invalidate 维持的节奏就是 Win2D 交换链 vsync。
        if (_isSmoothZooming || _isWheelScrolling)
        {
            _lastFrameTicks = Stopwatch.GetTimestamp();
            double dt = ComputeFrameDt();
            if (_isSmoothZooming) StepZoomFrame(dt);
            if (_isWheelScrolling) StepScrollFrame(dt);
            StopFramePumpIfIdle();                         // 本帧内已落定 → 停看门狗/循环标志
            if (_isSmoothZooming || _isWheelScrolling)
            {
                InvalidateBoth();                 // 仍在进行 → 续帧（Win2D 交换链 vsync 节奏）
            }
        }

        var ds = args.DrawingSession;
        ds.Clear(_gapColor); // 页间背景（跟随深浅主题）

        if (!ComputeVisiblePages(out double z, out var visible)) return;

        // ---- RenderCache::Paint（1:1 RenderCache.cpp Paint）：每可见页跑一次目标层瓦片 BFS。
        // 只画与视口相交的瓦片；未命中 → 任意 zoom 同位瓦片替身（factor 缩放显示）；
        // 目标层未命中且无在途请求 → RequestRendering（视口驱动的请求源）。
        // gConserveMemory 收尾在 Paint 内：目标层全覆盖后 FreePage 其他分辨率 + FreeNotVisible。
        // 缩放弹簧动画期 renderMissing=false（Sumatra 无逐帧动画；落定后 QueueVisiblePages 补发），
        // 滚动/静止期照常 —— 与 Sumatra 滚动中持续请求同一行为。
        _drawDs = ds;
        var cache = _renderCache;
        if (cache != null)
        {
            // 瓦片引擎内部全是物理像素、且几何在【未旋转页帧】（与 Sumatra 同单位同帧）：
            // zoom = 每点物理像素数，pageOnScreen = 未旋转页矩形。换算只在这里发生一次。
            // 旋转：把 DrawingSession 绕页中心转 rot 度，引擎给的未旋转几何就精确落到旋转后的
            // dest 上 —— 位图不需要重渲染，也不需要去映射每颗瓦片的位置（上一版就是那么错的）。
            double dpi = _dpiScale > 0 ? _dpiScale : 1.0;
            float zoomPx = (float)(z * 96.0 / 72.0 * dpi);
            bool renderMissing = !_isSmoothZooming && !_isRightZooming;
            int rot = _rotation;
            var savedTransform = ds.Transform;
            foreach (var (p, dest) in visible)
            {
                ds.FillRectangle(dest, _placeholderColor);   // 占位色铺在旋转后的矩形上
                PageFrameForTiles(dest, out var pageOnScreenPx, out var pivot);
                if (rot != 0)
                {
                    var rotM = Matrix3x2.CreateRotation((float)(rot * Math.PI / 180.0), pivot);
                    if (rot == 90 || rot == 270)
                    {
                        // ⚠ 90/270 半像素对齐修正（2026-09-20 根因，"旋转后落定整页糊"的真凶）：
                        // pivot=页中心（整数或 x.5 物理像素）。绕 pivot 转 90° 的落点
                        //   x' = cx+cy-y, y' = x+cy-cx —— 要全为整数（=零插值 1:1 直拷），
                        //   当且仅当 cx+cy 与 cy-cx 都是整数，即 cx、cy【同奇偶】。
                        // 页宽高奇偶不同 → cx/cy 一整一半 → 整页内容落半像素网格
                        //   → GPU 对每个像素双线性插值 → 整页均匀糊。
                        //   （C# 侧 factor/ratio/target 全部正确=1.0，糊发生在 GPU 光栅化
                        //   最后一刻，任何 C# 侧日志都看不见——这就是四轮日志分析扑空的原因。）
                        // 180° 天然免疫：x'=2cx-x，2cx 恒为整数。0° 无旋转同样免疫。
                        // 修复：补 δ=(-frac(cx+cy), -frac(cy-cx)) ∈ {0,±0.5}² 物理像素平移
                        //   （矩阵顺序：先旋转后平移 = R * T），把落点拉回整数网格。
                        //   页面整体最多挪 0.5 物理像素（≈0.4 DIP），不可见；换来零重采样。
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
        // 动画期逐帧变换 → 直接画（几何缓存），跳过渲染目标往返，省整屏 Clear+blit；
        // 静止/板书期变换不变 → 累积层一次 DrawImage，成本与笔画数量无关。
        // 无标注的文档直接跳过，避免无谓分配累积层与遍历。
        if (AnnotationsVisible && HasVisiblePenAnnotations(visible))
        {
            if (IsViewAnimating)
            {
                DrawAnnotationsOnCanvas(ds, z, visible);
            }
            else
            {
                EnsureAnnoLayer();
                if (_annoLayer != null)
                {
                    if (_annoLayerNeedsFullRebuild) RebuildAnnoLayer(visible, z);
                    else BakeUnbakedStrokes(visible, z);
                    ds.DrawImage(_annoLayer);
                }
            }
        }

    }

    /// <summary>缩放/滚动动画是否进行中。此时标注【逐帧随变换改变】，累积层无收益；
    /// 且每帧走累积层 = 全视口渲染目标 Clear + 重画全部笔画 + DrawImage 整屏 blit，
    /// 双向全屏带宽会把标注渲染线程拖出 vsync，表现为标注相对平滑页位图滞后/闪光。
    /// 动画期改为直接按当前变换画（几何已缓存），成本从「整屏往返」降到「每笔画一次 DrawGeometry」。</summary>
    private bool IsViewAnimating =>
        _isSmoothZooming || _isWheelScrolling || _zoomAnimCommitting;

    /// <summary>动画期快速路径：跳过 _annoLayer 渲染目标，直接把标注按当前变换画到画布会话。
    /// 每页复用缓存的 CanvasGeometry，仅 ApplyPageTransform + DrawGeometry，与页位图同帧上屏。</summary>
    private void DrawAnnotationsOnCanvas(CanvasDrawingSession ds, double z,
        List<(GpuPageInfo Page, Rect Dest)> visible)
    {
        foreach (var (p, dest) in visible)
        {
            var annos = SafeAnnos(p.Index);
            if (annos == null) continue;
            bool any = false;
            foreach (var a in annos)
                if (a.Type == AnnotationType.Pen) { any = true; break; }
            if (!any) continue;
            ApplyPageTransform(ds, dest, z);
            foreach (var a in annos)
                if (a.Type == AnnotationType.Pen)
                    DrawAnnotation(ds, a);
        }
    }

    /// <summary>计算当前视口下所有可见页及其屏幕 dest 矩形。</summary>
    private bool ComputeVisiblePages(out double z, out List<(GpuPageInfo Page, Rect Dest)> visible)
    {
        visible = new();
        var pages = _pages;
        if (pages.Count == 0) { z = 0; return false; }

        GetViewState(out z, out double hOff, out double vOff);
        if (z <= 0) return false;

        // 用画布视口快照而非实时 Viewer.Viewport*：偏移与居中量必须同一时刻（否则滚动条
        // 出现/消失的瞬间居中量跳变 → 起止闪一帧"同一页内容、位置错误"）。快照由手势起点
        // 捕获/落定预测/闲置同步维护，与 GetViewState 返回的偏移配套。
        double vpW = _vpW;
        double vpH = _vpH;

        // 内容缩放后窄于视口时 ScrollViewer 会居中呈现，画布须加同样的居中补偿。
        double extentW = Spacer.Width * z;
        double extentH = Spacer.Height * z;
        double centerX = extentW < vpW ? (vpW - extentW) / 2.0 : 0;
        double centerY = extentH < vpH ? (vpH - extentH) / 2.0 : 0;

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

            // 文档坐标 → 屏幕坐标
            var dest = new Rect(
                centerX + p.LayoutRect.X * z - hOff,
                centerY + p.LayoutRect.Y * z - vOff,
                p.LayoutRect.Width * z,
                p.LayoutRect.Height * z);

            visible.Add((p, dest));
        }
        return true;
    }

    /// <summary>页 dest（旋转后屏幕矩形）对应的【未旋转】绘制矩形：交换宽高、同中心。
    /// rot==0 时即 dest 本身。瓦片引擎（TileRenderCache）的一切几何都在这个帧里 ——
    /// 出图恒未旋转，旋转由 DrawingSession 上的一次 CreateRotation 补足
    /// （与标注层 ApplyPageTransform 同一约定）。</summary>
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

    /// <summary>交给瓦片引擎的页几何：未旋转页帧的物理像素矩形 + 该帧对应的旋转枢轴（DIP）。
    /// 枢轴取【取整后】矩形的中心，必须与 TileRenderCache.Paint 内部对 pageOnScreen 的取整
    /// 严格一致 —— 否则旋转页会有半像素偏移，位图又被重采样（回到"发糊"）。取整后 90/180/270
    /// 绕整像素中心旋转，几何仍落在整像素上，故旋转页同样保持 1:1 直拷。</summary>
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
    // 1:1 移植 sumatrapdf-master/src/RenderCache.{h,cpp}（DisplayModel 的渲染引擎）：
    // 瓦片按视口渲染、maxTileSize=屏幕尺寸封顶单瓦片内存、FreeNotVisible 按视口释放、
    // 队列 LIFO cap 8、惰性线程 ≤min(8,cores)、预测链一次一页。本视图 = DisplayModel 对应物。

    private TileRenderCache? _renderCache;

    /// <summary>当前绘制帧的 DrawingSession（DrawTileBitmap 在 Paint 期间使用；仅 UI 线程 Draw 帧内非空）。</summary>
    private CanvasDrawingSession? _drawDs;

    /// <summary>native→CanvasBitmap 上传用的【唯一】托管暂存缓冲（UI 线程专用，只增不减）。
    /// 存在的理由：Win2D 只有 CreateFromBytes(byte[]) 这条入口，必须先落到托管内存上。
    /// 全程复用 → 每块瓦片不再 new byte[]（那正是 GC 提交段从 13MB 棘轮到 676MB 的来源）。</summary>
    private byte[] _uploadScratch = Array.Empty<byte>();

    private byte[] EnsureUploadScratch(int length)
    {
        if (_uploadScratch.Length < length)
            _uploadScratch = new byte[length];
        return _uploadScratch;
    }

    /// <summary>DisplayModel 析构对应物：CancelRenderingBlocking + FreeForDisplayModel + Dispose。</summary>
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

    /// <summary>dm->GetZoomReal()：每 PDF 点的【物理像素】数（z×96/72×DpiScale）。
    /// 与 Sumatra 同单位——瓦片引擎内部不再出现 DIP。</summary>
    float ITileRenderHost.ZoomReal(int pageNo) => (float)(CurrentZoomForKernel * 96.0 / 72.0 * _dpiScale);

    /// <summary>dm->GetViewPort().Size()：物理像素。</summary>
    Windows.Foundation.Size ITileRenderHost.ViewPortSize
    {
        get { lock (_lock) return new Windows.Foundation.Size(_vpW * _dpiScale, _vpH * _dpiScale); }
    }

    /// <summary>pageInfo->pageOnScreen 对应物：LayoutRect×z 平移到视口坐标（含 ScrollViewer 居中补偿，
    /// 与 ComputeVisiblePages 同式），取【未旋转】页矩形（引擎内部帧），最后 ×DpiScale 换成物理像素。
    /// 仅 UI 线程调用（读 Spacer 尺寸做居中补偿）。</summary>
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

    /// <summary>DisplayModel::PageVisibleNearby：本行或相邻行任一页可见。
    /// 纵向布局每页一行 → 本页 ±1；横向连续全部页同属一行 → 任一页可见。</summary>
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

    /// <summary>engine->RenderPage(RenderPageArgs{pageRect}) 对应物：只光栅化瓦片矩形
    /// （密度 = DIP 比例 × DpiScale = 物理 px，HiDPI 不糊）。走 PdfRenderService 并发槽。</summary>
    (IntPtr Data, int Length, int W, int H) ITileRenderHost.RenderTile(
        IntPtr cookie, int pageNo, int devW, int devH, int x0, int y0, int x1, int y1)
    {
        // 同步、零拷贝：native 缓冲的所有权直接交给缓存（= Sumatra 的 Pixmap*）。
        // cookie = FitzAbortCookie 对应物，MuPDF 中途自查中止。
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
            // pixmap 分配失败等）→ 绝不能冒泡成未处理异常杀进程。返回空缓冲让缓存跳过该瓦片
            // （= Sumatra 该块不画），其余瓦片照常。详情已记入 crash 日志，便于定位是哪块/哪页。
            Diagnostics.CrashLog.CaptureHandled(
                $"RenderTile page={pageNo} dev={devW}x{devH} rect=({x0},{y0})-({x1},{y1})", ex);
            return (IntPtr.Zero, 0, 0, 0);
        }
    }

    /// <summary>FreePixmap 对应物。</summary>
    void ITileRenderHost.FreeTilePixels(IntPtr data, int length)
    {
        if (data == IntPtr.Zero) return;
        try { GlanceRXNative.memory_free(data, (ulong)length); } catch { }
    }

    /// <summary>BlitPixmapRegion 对应物：GPU 位图惰性创建（CPU 位图仍是缓存真身，与 Sumatra 一致），
    /// 上采样 NN / 缩小 Linear（硬性规则）。仅 UI 线程 Draw 帧内被 Paint 调用。
    /// target 入参是【物理像素】（引擎单位）→ 画布是 DIP，故 ÷DpiScale；
    /// source 是【位图像素】索引，必须原样传（一旦也除 DpiScale，源区域就错位取到 1/S 的内容）。</summary>
    void ITileRenderHost.DrawTileBitmap(BitmapCacheEntry entry, Rect target, Rect source)
    {
        var ds = _drawDs;
        if (ds == null) return;
        if (entry.Gpu == null)
        {
            // native 缓冲可能已被释放而 GPU 副本又不在（设备重建作废）→ 本条目已无像素可画。
            if (entry.Pixels == IntPtr.Zero) return;
            var device = ScreenCanvas?.Device;
            if (device == null) return;
            int len = entry.BitmapW * entry.BitmapH * 4;
            if (len <= 0 || len > entry.PixelsLen) return;
            try
            {
                // Win2D 必须先有一块托管内存才能建 CanvasBitmap（CreateFromBytes），
                // 所以这里做【一次】native→复用的 UI 线程 scratch 的拷贝，且 scratch 全程复用、
                // 不随瓦片数增长 —— 与之前「每块瓦片 new byte[]」相比不再有任何 MB 级分配。
                var scratch = EnsureUploadScratch(len);
                System.Runtime.InteropServices.Marshal.Copy(entry.Pixels, scratch, 0, len);
                entry.Gpu = CanvasBitmap.CreateFromBytes(device, scratch, entry.BitmapW, entry.BitmapH,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized);
                entry.GpuBytes = len;
            }
            catch { return; }
            // GPU 副本已就位 → native 那份再无任何读取点（Sumatra 必须留是因为它 GDI 直 blit
            // CPU pixmap；Win2D 画的是 CanvasBitmap）。此处释放 = 去掉整整一倍冗余。
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

    /// <summary>DisplayModel::RenderVisibleParts 的 1:1 对应物（= 原 QueueVisiblePages 职责）：
    /// 可见页正向请求 → 预测链（next/prev 各一页，一次一页 chained）→ 可见页反向重发
    /// （LIFO 保证可见页先于预测页渲染）。【禁止在持有 _lock 时调用 cache.*】（锁序：_lock
    /// 与 cache 内部锁分离，worker 线程会持 cache 锁反向等 _lock）。UI 线程调用。</summary>
    // ---------------- 滚动条滑块拖拽期：抑制渲染请求（对齐 Sumatra 快拖静止行为） ----------------
    // 根因（2026-09-13 定案）：400% 快拖 115 页时每个中间视口都全速发请求，而 MuPDF JPEG
    // 逐行顺序解码使瓦片成本 ∝ 瓦片 Y 偏移（page 114 实测页顶 36ms → 页底 352~454ms），
    // 飞行期间 1290 次回放仅 296 入库 —— 白跑放大成 CPU 风暴。Sumatra 快拖时静止旧画面、
    // 落定才出图，中间位置几乎不发请求。对齐：拖拽中不发请求（缓存瓦片照画、视口照常移动），
    // 静止 ≥ScrollDragSettleDelayMs 或松手时才补当前视口（后者覆盖"按住滑块不松手"场景）。

    private const int ScrollDragSettleDelayMs = 180;
    private bool _isScrollBarDragging;
    private bool _scrollDragSettleQueued;
    private long _scrollDragLastMoveTicks;
    private double _scrollDragLastH = double.NaN;
    private double _scrollDragLastV = double.NaN;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _scrollDragSettleTimer;
    private readonly HashSet<Microsoft.UI.Xaml.Controls.Primitives.Thumb> _hookedThumbs = new();

    // 递归查找 root 模板树内的全部 Thumb（ScrollViewer 内置纵/横 ScrollBar 的滑块）并订阅拖拽事件。
    // 幂等：同一 Thumb 实例只订阅一次（Loaded 可能多次触发）。
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
        // 未送达而卡在 true，下一次拖拽仍能重新播种移动时钟，抑制机制不会永久失效。
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

    /// <summary>滚动条滑块松手：立即按最终视口补渲染请求（Sumatra"落定到终点"行为）。</summary>
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
    // 再移动则重新抑制（重置移动时钟）。与 QueueVisiblePages 顶部的抑制门共用同一时钟判定，
    // 保证"静止放行 / 移动抑制"两处判定严格同源。
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
        // InvalidateBoth 照常重画）。静止 ≥ScrollDragSettleDelayMs（settle 定时器已补发）或松手
        // （EndScrollBarDrag 已补发）后放行。移动时钟由 ScrollDragSettleTick 检测位移复位。
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
        // 中间密度的瓦片请求全是废渲染。落定帧（FinishZoom→本函数）按精确 z 请求。
        if (!_isSmoothZooming && !_isRightZooming)
        {
            for (int i = first; i <= last; i++) cache.RequestRendering(this, i);

            // 预测链：kMaxPredictiveRequests=2（单页行布局，Sumatra 双页模式才是 ±2）。
            int[] pred = new int[2];
            int nPred = 0;
            if (last + 1 < _pages.Count) pred[nPred++] = last + 1;
            if (first - 1 >= 0) pred[nPred++] = first - 1;
            if (nPred > 0) cache.RequestPredictiveRendering(this, last, pred, nPred);

            // 反向重发：LIFO 队列下可见页排到预测页之前（RenderVisibleParts 原样）。
            for (int i = last; i >= first; i--) cache.RequestRendering(this, i);
        }

        _lastRenderActivityTicks = Stopwatch.GetTimestamp();
        EnsureWorkerCacheFlush();
    }

    // ---------------- 交互 ----------------

    /// <summary>双击切换 适应宽度 ↔ 100%，以双击点为锚，平滑动画过渡。标注模式下禁用。</summary>
    private void Viewer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (AnnotationInputActive) { e.Handled = true; return; }
        // 适应 = 按当前布局主轴适应（纵向=适应宽度 / 横向=适应高度），与 FitWidth 按钮完全一致。
        // 严禁用整份文档的 Spacer.Width 去除：横向布局总宽＝全部页面串接，数值巨大
        // → 视口宽/总宽 被钳到最小缩放，双击"适应"直接塌缩到最小。FitToViewportPercent 横向取
        // Spacer.Height(单页高)/视口高，即"当前页"维度，不会出现全宽塌缩。
        double fitPercent = FitToViewportPercent;
        if (fitPercent <= 0) return;

        // 只拦"重复派发"，不再拦"动画进行中"。
        // 旧实现整段动画期间对双击完全无响应（怕三连击乱飞），代价是连着双击第二下像丢了一样。
        // 现在弹簧支持中途改目标且速度连续，而 StartSmoothZoom 在 _isSmoothZooming 时会复用同一
        // 固定锚点 → 动画中的双击表现为"围绕原锚点平滑反向"，既不乱飞也不再吞输入。
        if ((DateTime.UtcNow - _lastZoomEndTime).TotalMilliseconds < DoubleTapIgnoreMs
            || (DateTime.UtcNow - _doubleTapStartPending).TotalMilliseconds < DoubleTapIgnoreMs)
        {
            e.Handled = true;
            return;
        }

        float fit = (float)(fitPercent / 100.0);
        // 以【当前实时缩放 _liveZoom】判定 toggle 目标：≈适应宽度 → 去 100%，否则 → 去适应宽度。
        // 用 _liveZoom 而非 Viewer.ZoomFactor：重开后 Viewer.ZoomFactor 异步落地滞后，会误判。
        // 此前用"确定性 _doubleTapToggleTarget 取反"在重开时初始为未知(-1)、首次双击恒判去适应宽度，
        // 导致"以适应宽度关闭→重开→首次双击失效"。现动画中的按下/双击已被按下拦截+时间窗拦死，
        // 被受理的双击必然发生在动画落定后、_liveZoom 是准确值 → 可安全据当前缩放判定。
        double curZoom = _liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor;
        float target = Math.Abs((float)curZoom - fit) < 0.005f ? 1.0f : fit;

        var pos = e.GetPosition(Viewer);
        e.Handled = true;

        // 同步记录派发时刻：即使下面推迟到下一调度周期才真正启动动画，这段"尚未启动"的极短窗口内
        // 到达的后续双击也会被上面的时间窗拦住，不会漏拦导致重触发。
        _doubleTapStartPending = DateTime.UtcNow;
        // 不在事件内同步启动（与双击手势收尾打架 → 轻微振动），推迟一个调度周期。
        // 全新手势：以本次鼠标位置为不动点(符合"以鼠标为中心缩放"的预期)；不再复用旧锚点——
        // 因为重触发已被上面的时间窗完全拦截，快速连点场景下复用逻辑不再被触发。
        DispatcherQueue.TryEnqueue(() => StartSmoothZoom(target * 100.0, pos, reuseAnchor: false));
    }


    // ---------------- 指针交互：左键拖动平移 / 右键拖拽缩放 / Ctrl+滚轮 ----------------

    /// <summary>是否启用拖动平移与右键缩放（后续标注模式下由 MainPage 关闭）。</summary>
    public bool IsPanZoomEnabled { get; set; } = true;

    private bool _isDragPanning;
    private bool _isRightZooming;
    private Point _dragStartPos;
    private double _dragStartH;
    private double _dragStartV;
    // 拖拽平移自管显示偏移：直接按鼠标位移计算并即时驱动画布重画，
    // 跳过 ScrollViewer.ChangeView 的异步落点（其会让画布视觉比鼠标慢约 1 帧）。
    private double _panDisplayH;
    private double _panDisplayV;

    // 实时视图镜像：与绘制/滚动/缩放同源，作为持久化快照的权威来源。
    // 关键：窗口隐藏/卸载时 ScrollViewer 可滚动范围失效、VerticalOffset 被清零，
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

    // 把自管偏移同步给 ScrollViewer（驱动原生滚动条/回显）的每帧定时器：
    // 与 CanvasControl 的即时重画解耦，高轮询率鼠标下也不会因 ChangeView/ViewChanged 风暴拖慢主线程。
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _panApplyTimer;
    private double _rightZoomStartY;
    private double _rightZoomStartPercent;
    private Point _rightZoomAnchorPos;       // 按下时的鼠标位置（视口坐标），缩放期间该点屏幕位置不动
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

    // 右键拖拽缩放期间把真实视图下发给 ScrollViewer（驱动原生滚动条/外部同步条）的 16ms 定时器：
    // 与帧泵(Draw)解耦。见 StepZoomFrame 的右键分支注释——逐帧 ChangeView 的重排版/ViewChanged
    // 会占满 UI 线程，节流后以 60Hz 刷新滚动条位置，跟手观感不变。
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
        // 手势已结束（落定/被打断）→ 停表；最终真实视图由 FinishZoom 一次性对齐。
        if (!_isSmoothZooming || !_zoomFromRightDrag) { StopZoomApplyTimer(); return; }
        double zp = _zoomAnimCurrentFactor;
        double hp = _zoomAnimCurrentH;
        double vp = _zoomAnimCurrentV;
        if (zp <= 0) return;
        Viewer.ChangeView(hp, vp, (float)zp, disableAnimation: true);
        ViewportChanged?.Invoke(this, EventArgs.Empty); // 外部同步条按 60Hz 刷新
    }

    /// <summary>
    /// 每帧把自管显示偏移下发给 ScrollViewer（驱动原生滚动条位置），并接手拖拽期间的缺图预取/页码/同步回显。
    /// 拖拽期间 Viewer_ViewChanged 直接早退（见该处），由本定时器统一做这些重活 —— 每帧最多一次，
    /// 与 PointerMoved 的即时画布重画解耦：画布随鼠标满速跟手，高轮询率鼠标也不会因
    /// ChangeView→ViewChanged 风暴占满 UI 线程而拖慢绘制。未移动时空转跳过。
    /// </summary>
    private double _lastPanAppliedH = double.NaN;
    private double _lastPanAppliedV = double.NaN;

    private void PanApplyTick(object? sender, object? e)
    {
        if (!_isDragPanning) { StopPanApplyTimer(); _lastPanAppliedH = _lastPanAppliedV = double.NaN; return; }
        // 鼠标未移动则空转跳过（画布已由 PointerMoved 即时重画，无需重复下发/预取）。
        if (_panDisplayH == _lastPanAppliedH && _panDisplayV == _lastPanAppliedV) return;
        _lastPanAppliedH = _panDisplayH;
        _lastPanAppliedV = _panDisplayV;
        // 下发真实偏移（驱动原生滚动条），并接手拖拽期间的预取/回显（ViewChanged 在拖拽中已跳过）。
        //
        // zoom 传 CurrentZoom 而非 Viewer.ZoomFactor，且必要时【显式带上】而不是传 null：
        //  · 传 Viewer.ZoomFactor 给 SyncLiveView 会把陈旧的旧倍率写回 _liveZoom —— 权威镜像被污染后，
        //    所有以 _live* 播种的地方（下一次缩放起点、双击 toggle 判定、GetViewState）全部错乱，
        //    而且这个污染在 ChangeView 落地后也不会自愈。这正是"有时"才复现、且一错就乱的原因。
        //  · 打断缩放动画时新倍率的 ChangeView 可能尚未落地，此时再发一个 zoom=null 的 ChangeView
        //    会让 ScrollViewer 按【当前(旧)倍率】处理，把未落地的缩放请求顶掉 → 缩放回退。
        //    故仅在两者尚有差距（未落地）时显式补上新倍率；常态下仍传 null，行为与原先一致。
        double z = CurrentZoom;
        float? zArg = Math.Abs(z - Viewer.ZoomFactor) > 1e-4 ? (float)z : (float?)null;
        Viewer.ChangeView(_panDisplayH, _panDisplayV, zArg, disableAnimation: true);
        SyncLiveView(_panDisplayH, _panDisplayV, z);
        QueueVisiblePages(); // 补齐拖入视口的未构建页
        NotifyViewState();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Viewer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_pages.Count == 0) return;

        // 【这里曾经拦掉动画期间的一切按下，是"动画没结束就点不动"的直接原因，现已放行】
        //
        // 当初拦截的根因是：放行会 StopSmoothZoom 杀动画，又立刻用【ChangeView 尚未异步落地的】
        // Viewer.* 陈旧偏移去播种 _dragStartH/_panDisplayH/_rightZoomAnchorDoc*，鼠标一动视口就
        // 跳回动画起点 → 三连击"内容乱飞"。但那个前提已经不成立了：下面两个分支的起点现在一律
        // 从权威实时镜像 _live* 播种，而 _live* 由动画每一帧的 SyncLiveView 更新为【当前插值态】，
        // StopSmoothZoom 也是就地落定到当前插值态（不是目标态）。陈旧偏移这个源头没有了，
        // 接管点就是用户眼睛看到的那一帧，无跳变。
        //
        // 而拦截的代价很实在：弹簧尾巴哪怕只剩几十毫秒，用户此刻按下也会被整个吞掉 ——
        // 观感上就是"动画拖很久、期间左右键全失灵"。交互的第一原则是任何时刻都能被用户打断。
        //
        // 仍然拦 _pendingRestore：那是打开文档时的还原窗口，Viewer.Scrollable* 尚未在新缩放下就绪，
        // 放行拖拽会用陈旧范围夹取偏移 → 重开后双击"视口乱飞"。该窗口只存在于加载瞬间，不影响手感；
        // 双击的 DoubleTapped 独立触发，由 StartSmoothZoom 从 _live* 正确起动并 CancelPendingRestore。
        if (_pendingRestore != null)
        {
            e.Handled = true;
            return;
        }

        var pt = e.GetCurrentPoint(Viewer);

        // ── 标注模式：左键交给标注管线，右键仍可拖拽缩放 ──
        if (AnnotationInputActive && pt.Properties.IsLeftButtonPressed)
        {
            StopSmoothZoom(); // 标注笔画开始时停掉进行中的缩放动画
            if (TryViewportToPage(pt.Position, requireInside: true,
                out int pageIdx, out double px, out double py))
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

        if (pt.Properties.IsLeftButtonPressed)
        {
            StopSmoothZoom(); // 开始拖动平移前停掉缩放动画
            StopWheelScroll(); // 取消进行中的滚轮滚动，避免抢视图
            _isDragPanning = true;
            _dragStartPos = pt.Position;
            // 用权威实时镜像 _live* 播种拖拽起点，而非 Viewer.*——还原未落地/被 CancelPendingRestore
            // 中断时 Viewer.* 可能仍是 0（陈旧），直接播种会让拖拽起点跳到顶部 → 重开后首次双击一闪到顶端。
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
            StopSmoothZoom(); // 开始右键拖拽缩放前停掉缩放动画
            StopWheelScroll(); // 取消进行中的滚轮滚动，避免抢视图
            _isRightZooming = true;
            _rightZoomStartY = pt.Position.Y;
            // 记录鼠标锚点：按下点对应的文档坐标，整个拖拽期间绕它缩放。
            // 同样用权威实时镜像 _live* 播种缩放与锚点（Viewer.* 在还原窗口内可能陈旧=0 → 锚点算错跳到顶部）。
            double zf = _liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor;
            if (zf <= 0) zf = 1.0;
            double curH = _liveZoom > 0 ? _liveH : Viewer.HorizontalOffset;
            double curV = _liveZoom > 0 ? _liveV : Viewer.VerticalOffset;
            _rightZoomStartPercent = zf * 100.0;
            _rightZoomAnchorPos = pt.Position;
            // 居中补偿：内容窄/矮于视口时 ScrollViewer 默认居中呈现，视口坐标→文档坐标必须扣除该
            // 居中偏移（详见 TryViewportToPage 同款算法）。此前漏算，导致低缩放下整页落入视口内
            // (centerX/Y>0) 时锚点被算偏右/下 → 缩放焦点不再跟鼠标、整页向一侧漂移。这是把下限放宽
            // 到 10% 后才暴露的回归：≥25% 时宽杂志页超出视口、centerX=0 故看不出。
            // 用当前视口快照（按下时即闲置同步过的值，与 _live* 配套）。
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
            // PointerMoved/PointerReleased 照常触发、缩放逻辑不受影响，且 WM_SETCURSOR 正常发来、
            StartPenCursorHold();
            e.Handled = true;
        }
    }

    private void Viewer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // 命中元素是滚动条(或其子部件)→视为 chrome，交还真实箭头；其余交给下方画笔逻辑。
        // 放在最前：即便下面 _isAnnotating / _isRightZooming / _isDragPanning 提前 return 也要先更新。

        if (_isAnnotating)
        {
            var pt = e.GetCurrentPoint(Viewer);
            if (ViewportToLockedPage(_annotatingPage, pt.Position, out double px, out double py))
                AnnotationPointerMoved?.Invoke(this,
                    new GpuAnnotationPointerArgs { PageIndex = _annotatingPage, X = px, Y = py });
            e.Handled = true;
            return;
        }

        if (_isRightZooming)
        {
            // 上推放大、下拉缩小。锚点 = 按下时的鼠标位置：该文档点在屏幕上保持不动。
            //
            // 旧实现把每一个鼠标点直接当成缩放值下发（ChangeView + 整页重光栅）。两个后果：
            //   ① 高轮询率鼠标一帧能来十几个点，一帧内做十几次 ChangeView 和重光栅，UI 线程被打满；
            //   ② 手抖的 1-2px 会原样变成缩放值的抖动，缩放本身就是"毛"的。
            // 现在只把鼠标位置换算成【弹簧目标】，实际缩放由帧泵按 omega=40 推进：
            // 高频抖动被滤掉，相位滞后约 25ms（肉眼判定仍是完全跟手），重光栅另有节流。
            var pt = e.GetCurrentPoint(Viewer);
            double deltaY = _rightZoomStartY - pt.Position.Y;
            double raw = (_rightZoomStartPercent + deltaY) / 100.0;
            double target = Math.Clamp(raw, MinZoom, MaxZoom);
            if (raw != target)
            {
                // 【抗积分饱和】本映射是"按下点百分比 + 累计位移"的绝对式：撞到上/下限后继续同向推，
                // 越界量会一直累加。推过头 300px，就得先反向拉回 300px 才有任何反应 —— 中间这一大段
                // 完全不跟手，且每个 PointerMoved 都在"target==当前缩放"下重启一次空手势（配合
                // StepZoomFrame 的收敛早退分支，正是"到尽头继续点右键画面乱动"的触发器）。
                // 这里把起点重定基到边界处：越界位移不入账，反向移动第一像素即刻生效，手感对称。
                _rightZoomStartPercent = target * 100.0;
                _rightZoomStartY = pt.Position.Y;
            }

            if (!_isSmoothZooming)
            {
                // 起跑（或中途停手收敛后重新起跑）：从【当前实际缩放】接续，绝不能回到按下时的值。
                double cur = _liveZoom > 0 ? _liveZoom : Viewer.ZoomFactor;
                if (cur <= 0) cur = _rightZoomStartPercent / 100.0;
                // 已在尽头且目标==当前：这是一次【空手势】。起跑只会白白置 _isSmoothZooming、
                // 启停帧泵、并把画布切到插值态读取路径，毫无位移收益。直接吞掉该事件。
                if (Math.Abs(target - cur) < 1e-6) { e.Handled = true; return; }
                BeginZoomGesture(cur, target, RightZoomSpringOmega, fromRightDrag: true);
                _zoomGestureStartZoom = _rightZoomStartPercent / 100.0; // 放大/缩小按整段手势判定
                _isSmoothZooming = true;
                EnsureFramePump();
            }
            _zoomAnimTargetFactor = target;
            _zoomLog.Target = Math.Log(target);
            // 弹簧可能已在"按住停顿"中停稳并停泵（见 StepZoomFrame 收敛分支）：此时 _isSmoothZooming
            // 仍为 true，不会走上面起跑分支 → 这里无条件再起泵，保证每次鼠标移动都能重新推进弹簧。
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
        //
        // 范围按 CurrentZoom 自算，【不读 Viewer.Scrollable*】：缩放动画中途按下左键接管时，
        // 新倍率的 ChangeView 还没落地，Viewer.Scrollable* 仍是旧倍率下的范围。起点是按新倍率
        // 播种的（可能远超旧范围），一移动鼠标就被夹回旧上界 —— 那正是"视点乱飞到别的地方"。
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
        // 避免高轮询率鼠标下 ChangeView→ViewChanged 风暴拖慢主线程绘制。
        InvalidateBoth();
        EnsurePanApplyTimer();
        e.Handled = true;
    }

    /// <summary>右键拖拽缩放手势结束（松手/移出/捕获丢失）统一收尾。
    /// 弹簧仍在推进 → 保证帧泵在跑，由 StepZoomFrame 停稳后收尾 FinishZoom；
    /// 已停稳（按住停顿中停泵的空闲态）→ 也走帧泵：等最后一跳密度批落定后由
    /// StepZoomFrame 收尾分支原样 FinishZoom（放大向零重铺，画面不二次变化）。</summary>
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
        if (_isAnnotating)
        {
            var pt = e.GetCurrentPoint(Viewer);
            int page = _annotatingPage;
            _isAnnotating = false;
            _annotatingPage = -1;
            this.ReleasePointerCapture(e.Pointer);
            StopPenCursorHold();
            if (ViewportToLockedPage(page, pt.Position, out double px, out double py))
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
            // zoom 同 PanApplyTick：仅当新倍率尚未落地时显式补上，避免 null 把未落地的缩放请求顶回旧值。
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
        StopWheelScroll(); // 失焦等打断时取消进行中的滚轮滚动
        StopPanApplyTimer(); // 同上：取消进行中的拖拽平移同步定时器

        // 捕获可能是被 ScrollViewer / 系统"抢走"的，而用户的手指还按在鼠标键上 ——
        // 真正松键后，保活循环（CompositionTarget.Rendering / 定时器）会自己收尾。
        if (!CursorInterop.AnyMouseButtonDown())
        {
            StopPenCursorHold();                             // 手势结束，清除手势状态标志
        }
        if (_isAnnotating)
        {
            // 笔画被系统打断（窗口失焦等）：以最后位置收尾，MainPage 会照常存盘。
            int page = _annotatingPage;
            _isAnnotating = false;
            _annotatingPage = -1;
            AnnotationPointerReleased?.Invoke(this,
                new GpuAnnotationPointerArgs { PageIndex = page, X = 0, Y = 0 });
        }
    }

    /// <summary>指针离开本视图（窗口外/标题栏/非子元素区域）时收尾手势。当前主要用于右键拖拽缩放：
    /// 该手势刻意不捕获指针，指针移出后 PointerMoved 停发、手势无法自愈，必须在此主动终止，
    /// 否则回来后右键仍按住会以陈旧起点继续缩放、造成视点跳变（用户报告的"拖出区域不停止" bug）。</summary>
    private void Viewer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // 左键拖拽平移对指针做了 CapturePointer，离开窗口也能收到事件、由 PointerCaptureLost 收尾，无需在此处理。
        if (_isRightZooming)
        {
            EndRightZoomGesture();
            e.Handled = true;
        }
    }

}
