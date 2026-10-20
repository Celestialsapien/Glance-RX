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
/// 白板（Canvas 模式）交互事件参数：坐标一律为【页局部未旋转 PDF 点】，与节点存储坐标同源。
/// 仅按需填字段（拖动/新建填 X/Y，编辑填 Text，删除/编辑仅用 Id）。
/// </summary>
public sealed class CanvasNodeEventArgs : EventArgs
{
    public string NodeId { get; init; } = "";
    public int PageIndex { get; init; } = -1;
    public double X { get; init; }
    public double Y { get; init; }
    /// <summary>拖动开始时的原始位置（仅拖动完成事件填充），供 MainPage 组装移动撤销记录。</summary>
    public double OrigX { get; init; }
    public double OrigY { get; init; }
    public string? Text { get; init; }
    /// <summary>卡片尺寸（仅缩放事件填充，页局部 PDF 点）。</summary>
    public double Width { get; init; }
    public double Height { get; init; }
    /// <summary>缩放前的原始位置与尺寸（仅缩放完成事件填充，供 MainPage 组装撤销记录）。</summary>
    public double OrigWidth { get; init; }
    public double OrigHeight { get; init; }
    /// <summary>连线起始边 top/bottom/left/right（仅连线请求事件填充）。</summary>
    public string FromSide { get; init; } = "";
    /// <summary>连线目标节点 id（仅连线请求事件填充）。</summary>
    public string TargetNodeId { get; init; } = "";
    /// <summary>连线目标吸附边 top/bottom/left/right（仅连线请求事件填充）。</summary>
    public string ToSide { get; init; } = "";
    /// <summary>连线 id（仅连线删除/样式事件填充）。</summary>
    public string EdgeId { get; init; } = "";
    /// <summary>箭头样式 none/single/both（仅连线样式事件填充；null = 不变更箭头）。</summary>
    public string? ArrowStyle { get; init; }
    /// <summary>连线颜色 "#RRGGBB"（仅连线样式事件填充；null = 不变更颜色；空串 = 恢复默认色）。</summary>
    public string? EdgeColor { get; init; }
    /// <summary>卡片颜色 "#RRGGBB"（仅卡片颜色事件填充；null = 不变更颜色）。</summary>
    public string? NodeColor { get; init; }
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

    // ---------------- 连线强调动画（Obsidian 式：从中间向两边展开/收回） ----------------
    // 每边一个进度 0..1（0=未强调原样，1=完全强调）。选中/悬浮态变化驱动目标；采用固定时长 easeOutCubic 补间，
    // 末尾速度归零 → 展开/收回都平滑落定、无收尾跳变。各边独立 from/to/t0，可各自中途改向。
    private readonly Dictionary<string, float> _edgeEmph = new();       // 当前进度 0..1
    private readonly Dictionary<string, float> _edgeEmphFrom = new();  // 本段补间起点值
    private readonly Dictionary<string, float> _edgeEmphTo = new();    // 本段补间目标值（1/0）
    private readonly Dictionary<string, long> _edgeEmphT0 = new();     // 本段补间起始时钟(Stopwatch ticks)
    private bool _edgeEmphAnimating;
    private const double EdgeEmphDur = 0.12;   // 单段补间时长(秒)：展开/收回一致，较旧版(≈150ms)稍快

    // ---------------- 编辑态选区活动端跟踪（WinUI ITextSelection 无 ActiveEnd，需差分推断） ----------------
    // RichEditBox 只给规范化 Start/End；拖选中【锚端不动、活动端移动】—— 对比上一帧哪端变了即可定位活动端。
    // 两端都变（双击选词/新拖选）时，活动端取离上一活动端更近的一端（活动端连续移动、锚点跳变）。
    private int _canvasSelLastStart = -1;   // 上一帧选区起点（-1 = 无基线）
    private int _canvasSelLastEnd = -1;     // 上一帧选区终点
    private int _canvasSelLastActive = -1;  // 上一帧推断出的活动端
    // ---------------- 编辑态"卡内=工字"光标作用域（对齐画笔圆环：25ms 轮询 + 状态标志 + 只替换箭头槽） ----------------
    // 画笔圆环之所以稳：DispatcherTimer 定时轮询作用域、仅在【状态变化】时 Install/Restore、事件漏一条也能自愈。
    // 这里同款户型：编辑态下指针在编辑卡内（非边框控件命中带）时，把系统【箭头槽 IDC_ARROW】替换为工字副本 ⇒
    // 框架在编辑框内按下/捕获期间按箭头求值，画出的也是工字（文本区框架走 IDC_IBEAM 本就是系统工字）；
    // 只替换箭头槽，不动 I 形槽。控件区/卡外/退出编辑则 SPI_SETCURSORS 恢复真箭头与 resize 形状。
    private bool _ibeamInstalled;                  // 工字作用域是否已安装（对齐画笔 _ringInstalled；变化才动资源）
    private bool _canvasCursorLocked;              // 画布手势光标是否处于锁定态（只有锁过才允许 Unlock 去还原资源）
    private bool _canvasEditControlGesture;        // 本次按下命中【编辑卡】的边框控件 ⇒ 属控件交互而非"点卡外退出编辑"
    private bool _canvasPointerOnEditCard;         // 指针是否落在编辑卡上（编辑框通道置 true，其 Exited/卡外置 false）
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _ibeamScopeTimer;   // 25ms 作用域轮询（对齐画笔 _ringScopeTimer）

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
        if (_isSmoothZooming || _isWheelScrolling || _edgeEmphAnimating) return;
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

    /// <summary>逐帧推进所有连线的强调进度（与缩放/滚动同帧完成，靠帧泵维持）。
    /// 每边按各自的 from/to/t0 做固定时长 easeOutCubic 补间：目标变化即重启一段补间（以当前进度为起点），
    /// 末段速度归零、精确落定 —— 无旧版指数趋近的「长尾 + 硬阈值跳变」。</summary>
    private void StepEdgeEmphFrame(double dt)
    {
        bool animating = false;
        long now = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;
        lock (_canvasLock)
        {
            var edges = _canvasEdges;
            for (int i = 0; i < edges.Length; i++)
            {
                var e = edges[i];
                float desired = (_selectedCanvasEdgeId == e.Id || _hoverCanvasEdgeId == e.Id) ? 1f : 0f;
                _edgeEmphTo.TryGetValue(e.Id, out float to);
                if (to != desired)
                {
                    // 目标变化：以当前进度为起点重启一段补间
                    _edgeEmph.TryGetValue(e.Id, out float cur);
                    _edgeEmphFrom[e.Id] = cur;
                    _edgeEmphTo[e.Id] = desired;
                    _edgeEmphT0[e.Id] = now;
                    animating = true;
                }
                else if (_edgeEmphFrom.TryGetValue(e.Id, out float from) && _edgeEmphT0.TryGetValue(e.Id, out long t0))
                {
                    double t = ((now - t0) / freq) / EdgeEmphDur;
                    if (t < 1.0)
                    {
                        float e2 = 1f - (float)Math.Pow(1.0 - t, 3.0);   // easeOutCubic：初快末缓，t=1 时速度为 0
                        _edgeEmph[e.Id] = from + (desired - from) * e2;
                        animating = true;
                    }
                    else
                    {
                        _edgeEmph[e.Id] = desired;   // 精确落定，速度 0，无跳变
                    }
                }
            }
        }
        _edgeEmphAnimating = animating;
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

        // 白板：选中态 / 编辑态下指针停在"内容溢出的卡片"上 → 滚轮滚【卡片内容】而非翻页
        // （阅读态不响应；Shift 保留给交叉轴翻页；Ctrl 已在上面被缩放分支吃掉）。
        if (CanvasInputActive &&
            (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) == 0 &&
            TryScrollCanvasByWheel(e.GetCurrentPoint(Viewer).Position, delta))
        {
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
            // G 键隐藏白板层时，选中框/浮动控件/编辑框必须一起收起：否则卡片看不见了，
            // 浮层还挂在"空气"上，点编辑还能弹出输入框 —— 显隐语义冲突。
            if (!value) HideCanvasInteraction();
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

    /// <summary>设置"页点坐标(DIP) → 屏幕"的变换矩阵。
    /// 【必须以绝对值覆盖，不可与旧变换复合】：同一会话/渲染目标内会连续绘制多页（每页各调一次），
    /// 若写成 <c>m * ds.Transform</c>，第二页的矩阵会叠加第一页的矩阵 → 该页笔画被二次缩放/平移，
    /// 表现为「跨页笔画随另一页出现而错位、飞页、缩放错乱」（只在滚动触发整层重建、多页同会话绘制时暴露）。</summary>
    private void ApplyPageTransform(CanvasDrawingSession ds, Rect dest, double z)
    {
        ds.Transform = MakePageXform(dest, z).ToMatrix();
    }

    /// <summary>「页局部坐标（PDF 点）→ 屏幕坐标」的一次性变换。卡片用它整体作为会话变换，
    /// 白板连线用它的 <see cref="PageXform.Map"/> 逐点换算端点/控制点 ——
    /// 二者必须同源，否则连线端点会与卡片脱节（尤其旋转/缩放时）。</summary>
    private readonly struct PageXform
    {
        public readonly float S;          // 页点 → 屏幕像素
        public readonly Vector2 Origin;   // 未旋转页左上原点（已取整到设备像素）
        public readonly float Cx, Cy;     // 旋转枢轴 = dest 中心
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

        /// <summary>页局部方向 → 屏幕方向（仅旋转、保长度）。传单位向量即得单位向量。
        /// ⚠ 方向向量须绕【原点】旋转，绝不可复用 <see cref="Rotate"/>（那是绕页心转点，
        /// 会把单位向量减去页心坐标后变成上千像素的巨向量，控制点被推出屏幕）。</summary>
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
            // System.Numerics 是行向量约定（v·M，左矩阵先作用）：页点先 ×s 缩放，再平移到
            // dest 原点，最后绕 dest 中心补旋转 —— 与内核底图绘制 DrawPreviewScene 的放置规则完全一致。
            // 注意勿写成 Translation*Scale：那会把 Origin 也乘 s，几何随缩放漂移。
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
        return new PageXform(s, unrotOrigin, cx, cy, (float)(rot * Math.PI / 180.0), rot != 0);
    }

    /// <summary>某页在当前视图下的屏幕目标矩形（DIP，含居中补偿）。卡片/连线/手柄定位共用同一算法，
    /// 保证三者同源对齐；旧实现把该算法内联在绘制与命中两处，易漂移。</summary>
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

    private bool _cardToolbarShown;

    public GpuPageView()
    {
        InitializeComponent();
        ButtonVisualOverride.Attach(CanvasEditButton, transparentAtRest: true);
        ButtonVisualOverride.Attach(CanvasColorButton, transparentAtRest: true);
        ButtonVisualOverride.Attach(CanvasDeleteButton, transparentAtRest: true);
        // 连线胶囊与卡片胶囊【同一套三态】：主按钮 3 颗（箭头/颜色/删除）+ 二级箭头三选 3 颗。
        // 展开/收起会让按钮"滑到静止指针下方"，故它们同样要进下面的 hover 补判与冷处理链路。
        ButtonVisualOverride.Attach(CanvasEdgeArrowButton, transparentAtRest: true);
        ButtonVisualOverride.Attach(CanvasEdgeColorButton, transparentAtRest: true);
        ButtonVisualOverride.Attach(CanvasEdgeDeleteButton, transparentAtRest: true);
        ButtonVisualOverride.Attach(CanvasEdgeArrowNoneBtn, transparentAtRest: true);
        ButtonVisualOverride.Attach(CanvasEdgeArrowSingleBtn, transparentAtRest: true);
        ButtonVisualOverride.Attach(CanvasEdgeArrowBothBtn, transparentAtRest: true);
        AttachCanvasColorDotHover();   // 二级颜色胶囊圆点的悬浮/选中放大反馈（照抄 PenOptionsCapsule；卡片 + 连线两副）
        // 胶囊展开/收起会让按钮"滑到静止指针下方"，而 WinUI 不会因此主动派发 hover 事件
        // ⇒ 记下胶囊内的最后指针位置（CanvasOverlay 坐标），在布局落定后主动补判一次几何。
        // PointerPressed 需 handledEventsToo：ButtonBase 会把它标 Handled。
        CanvasCardToolbar.PointerMoved += CanvasCardToolbar_PointerMoved;
        CanvasCardToolbar.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(CanvasCardToolbar_PointerPressed), true);
        if (CanvasCardToolbarHideAnim != null)
            CanvasCardToolbarHideAnim.Completed += (s, e) =>
            {
                if (!_cardToolbarShown && CanvasCardToolbar != null)
                    CanvasCardToolbar.Visibility = Visibility.Collapsed;
            };
        // 连线胶囊：同一套"记最后指针位置"（展开/收起后按实时指针补判 hover）+ 淡出结束才 Collapsed。
        CanvasEdgeToolbar.PointerMoved += CanvasCardToolbar_PointerMoved;
        CanvasEdgeToolbar.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(CanvasCardToolbar_PointerPressed), true);
        if (CanvasEdgeToolbarHideAnim != null)
            CanvasEdgeToolbarHideAnim.Completed += (s, e) =>
            {
                if (!_edgeToolbarShown && CanvasEdgeToolbar != null)
                    CanvasEdgeToolbar.Visibility = Visibility.Collapsed;
            };
        // 滚轮：逻辑处理器挂在 Viewer(ScrollViewer 自身) 上、且用 HandledEventsToo 订阅，
        // 保证必定接管（ScrollViewer 原生处理时会把路由事件标 Handled，普通订阅会被整段吞掉——
        // “横向下滚不动”正是这个根因）。纵/横滚轮现在都走统一弹簧引擎，不再交还原生滚动。
        // 另在子级透明 Spacer 上挂一个阻断器：它在冒泡【早期】(子级先于 ScrollViewer) 把事件标 Handled，
        // 跳过原生 ScrollViewer 滚轮类处理程序，避免原生去移动那块透明 Spacer（画布是兄弟元素、碰不到，
        // 阻断它零视觉丢失）。阻断器无逻辑、不会与 Viewer 上的逻辑处理器重复处理。
        Viewer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Viewer_PointerWheelChanged), true);
        Spacer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Viewer_PointerWheelBlocker), true);
        // 编辑态【文本按下】通道：RichEditBox 会把 PointerPressed 标记 Handled ⇒ Viewer_PointerPressed 收不到。
        // 用 handledEventsToo=true 在编辑框上补一个处理器：收到文本区按下时同步指针位置并刷新自绘光标。
        // ⚠ 边框控件（滚动条 / 四角 / 四边中点 / 四边中段）的按下【不在这里】：其命中带已被 SyncCanvasEditHitCarve
        //   从编辑框命中区挖除，走 Viewer 手势通道 ⇒ 编辑框对控件交互零参与。
        CanvasEditBox.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnCanvasEditBoxPointerPressed), true);
        // 文本按下松手：按松手点重判"指针是否仍在卡内"，供工字作用域轮询下一拍装/卸工字。
        // 用 handledEventsToo 订阅 —— RichEditBox 会把 PointerReleased 标 Handled。
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
        // 选区变化（鼠标拖动选择 / Shift+方向键）→ 立刻刷新 Win2D 自绘选区，消除"蓝底迟钝"。
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
        // 箭头三态符号的「恒定 2 物理像素」描边：Loaded 后应用一次，并挂 RasterizationScale 变化重算
        // （窗口跨不同 DPI 显示器时 XamlRoot.Changed 触发，幂等）。理由见 ApplyCanvasEdgeArrowGlyphStrokeThickness。
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
            _canvasAccentColor = CanvasAccentDark;                           // 白板强调色：深色档 #A882FF
        }
        else
        {
            _gapColor = Windows.UI.Color.FromArgb(255, 243, 243, 243);       // #F3F3F3 = SolidBackgroundFillColorBase(Light)
            _placeholderColor = Windows.UI.Color.FromArgb(255, 249, 249, 249); // 接近纸面的柔和灰白
            _canvasAccentColor = CanvasAccentLight;                          // 白板强调色：浅色档 #9873F7
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
        // 视图动了（滚动/缩放）⇒ 文本菜单的位置就失效了，直接收起（用户下一次右键会重新定位）。
        HideCanvasEditContextMenu();
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
        // 白板卡片的文本排版同样绑定该设备：全部作废，下一帧按需重建。
        foreach (var kv in _canvasTextCache)
            kv.Value.Layout?.Dispose();
        _canvasTextCache.Clear();
        _canvasTextFormat = null;
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

    // =====================================================================
    // 白板层（Canvas 模式）：卡片 + 连线
    //   · 节点存【页局部未旋转 PDF 点】（与画笔同一坐标空间），跟随页面旋转/缩放；
    //   · 连线端点在【文档坐标】比较选边，在【屏幕坐标】绘制，跨页与布局重排自动重算；
    //   · 与页位图/标注绘于【同一交换链】（原子合成），避免双画布相位脱节。
    // =====================================================================

    private readonly object _canvasLock = new();
    private CanvasNode[] _canvasNodes = Array.Empty<CanvasNode>();
    private CanvasEdge[] _canvasEdges = Array.Empty<CanvasEdge>();
    private Dictionary<string, CanvasNode> _canvasIndex = new(StringComparer.Ordinal);

    private CanvasTextFormat? _canvasTextFormat;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<CanvasNode, CachedCanvasText>
        _canvasTextCache = new();

    private const float CanvasCardRadius = 8f;
    private const float CanvasCardPadX = 10f;       // 卡片左/右内边距（页局部 PDF 点）
    private const float CanvasCardPadY = 0f;        // 卡片上/下内边距（页局部 PDF 点）—— 对齐 Obsidian：上下完全不留空
    // 卡片内滚动条（阅读/选中/编辑三态统一，由 Win2D 绘制）：
    // 位置贴右边缘，但四边都与卡片边框留出间隙 —— 不侵入边框、也不与边框视觉相交。
    private const float CanvasScrollBarWidth = 8f;    // 条宽（页局部）；比早期 6 略粗，便于后续做拖动交互
    private const float CanvasScrollBarGap = 2f;      // 条与卡片边框之间的间隙（页局部）
    private const float CanvasScrollBarMinH = 20f;    // 条最小长度（页局部）
    private const float CanvasScrollBarHitTol = 3f;   // 滚动条命中容差（屏幕 DIP）：只在其矩形外扩一点点，
                                                      // 避免把贴着卡片边框的缩放/连线手柄整体抢走
    private static readonly Windows.UI.Color CanvasScrollBarColor = Windows.UI.Color.FromArgb(51, 0, 0, 0);   // 半透明黑（白卡片上呈灰）
    // 单行行高【标称值】（页局部 PDF 点）≈ 1.35 × 字号：只用于"上方最多留半行"的留白公式、
    // 滚动量估算与滚动条长度（这些是估算，差几 % 无感）。
    // ⚠ 它【不是】阅读态的实际行距：Win2D 实际按字体自然行高排（约 1.23 × 字号，实测差约 9%）。
    //   编辑态要对齐的是【实测值】，见 GetCanvasEditTargetLineHeight；本常量仅作拿不到 layout 时的兜底。
    private const float CanvasLineHeight = CanvasCardFontSize * 1.35f;
    private const float CanvasCardFontSize = 12f;   // 字号（页局部 PDF 点）
    // ⚠ 旧版曾设 CanvasEditFirstLineNudge(0.47) 补偿"RichEdit 编辑态 vs Win2D 阅读态"的首行基线差。
    //   现在三态文字都由 Win2D 绘制（编辑态 RichEditBox 仅作不可见输入宿主），不存在两个引擎的排版差，
    //   故该补偿已彻底删除——编辑态文字与阅读/选中态天然同位置。
    private const float CanvasCardStrokeWidth = 1.0f;
    private const float CanvasCardSelectedStrokeWidth = 2.0f;  // 选中/悬停时边框加粗
    private const float CanvasEdgeStrokeWidth = 1.6f;
    private const float CanvasEdgeMinWidth = 0.75f;   // 连线描边下限（屏幕 DIP）：小缩放时仍较清晰，不无限变细
    // 箭头尺寸以【页局部 PDF 点】计：调用处再乘缩放 z 得到屏幕 DIP，从而随页面缩放（与卡片手柄 CanvasHandleRadius 同约定）。
    // 等边三角形：高 = 页局部点 × z；半宽 = 高 / √3，由 DrawArrowHead 推导，不再单独存常量。
    private const float CanvasArrowLen = 9f;   // 箭头高（页局部 PDF 点），×z 即屏幕像素
    private const float CanvasArrowMinLen = 6f;   // 箭头高下限（屏幕 DIP）：小缩放时仍完全清晰可辨（覆盖 CanvasArrowLen·z），与命中下限同量级
    private static readonly Windows.UI.Color CanvasCardFill = Windows.UI.Color.FromArgb(255, 255, 255, 255);
    private static readonly Windows.UI.Color CanvasCardStroke = Windows.UI.Color.FromArgb(255, 138, 143, 152);
    private static readonly Windows.UI.Color CanvasCardText = Windows.UI.Color.FromArgb(255, 32, 32, 34);
    // 行内 Markdown 的底纹（卡片恒为不透明白卡面，故这两色不随主题变化）：
    // 行内代码用极浅灰垫底 + 等宽字体；高亮用柔和黄底。都是"先画底纹、再画文字"。
    private static readonly Windows.UI.Color CanvasMarkdownCodeBackdrop = Windows.UI.Color.FromArgb(255, 236, 236, 241);
    private static readonly Windows.UI.Color CanvasMarkdownHighlightBackdrop = Windows.UI.Color.FromArgb(255, 255, 236, 140);
    // 底纹竖向边界（相对【基线】，按字号的比例）。中文字面大致在 -0.88em..+0.12em，拉丁升部/降部约
    // -0.75em..+0.21em ⇒ 0.92 / 0.25 刚好包住字面、又不含字体自身的上下留白。
    // ⚠ 不能直接用 GetCharacterRegions 的整行框（约 1.33em）：中文会在色块里上下各浮约 2pt（用户报告）。
    private const float CanvasMarkdownBackdropAscent = 0.92f;
    private const float CanvasMarkdownBackdropDescent = 0.25f;
    private const string CanvasMarkdownCodeFontFamily = "Consolas";
    private static readonly Windows.UI.Text.FontWeight CanvasMarkdownBoldWeight = new() { Weight = 700 };

    /// <summary>卡片"淡色底纹"的 alpha（0-255，约 10%）。只给「后 6 色」用：在白色卡面之上再薄薄叠一层本色，
    /// 使卡片内部带一点同色氛围；首位默认灰不上底纹（灰底纹会显脏，且用户明确排除）。
    /// ⚠ 绝不能把这个 alpha 直接写进卡面底色 —— 卡面必须不透明，否则身后的连线/卡片会透出来。</summary>
    private const byte CanvasCardTintAlpha = 26;

    /// <summary>卡片边框色解析缓存：绘制期每帧、每卡都会问一次，绝不能在 Draw 里反复解析字符串。
    /// 键 = 原始 hex 串（大小写不敏感），值为解析结果（非法值也缓存成默认灰，避免反复解析）。
    /// 规模天然有界（调色板 7 色 + 外部导入的少量颜色）。并发字典：Draw 在 Win2D 线程、模型可能被 UI 线程改写。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Windows.UI.Color> CanvasStrokeColorCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>把 <c>node.Color</c>（"#RRGGBB"）解析成卡片边框色；空串 / 非法值一律回落默认灰
    /// <see cref="CanvasCardStroke"/>（= 二级胶囊首位灰点 #8A8F98，两处语义一致）。</summary>
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

    // 编辑态选区高亮（Win2D 自绘）。10-17 改：原为写死的半透明蓝 FromArgb(64,0,120,215)，
    // 与工具栏页码框 / 书签编辑框（SelfDrawnTextBox 的 SelRect）不是一回事 ⇒ 三处拖选颜色不统一。
    // 现改为与它们【同源】：取 App.xaml 主题字典里的 GlanceAccentBrush（Obsidian 紫，
    // 深 #A882FF / 浅 #9873F7），alpha 与 SelRect 的 Opacity=0.35 对齐（89/255）。
    // ⚠ 必须走主题字典取：该键【只】定义在 ThemeDictionaries 里、顶层字典没有
    // （与 ToggleButton* / GlanceAccent* 同一类坑，见 VisualOverrideBase.Resource 的注释）。
    // ⚠ 主题键用 MainWindow.IsDarkTheme 这个【确定性同步】源（暗色值在 "Default" 档、顶层无 "Dark"），
    //   绝不用 ActualTheme —— 它在 RequestedTheme 设置后经布局通道异步传播，切换瞬间会读到旧字典。
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
    // 连线命中半径（屏幕 DIP）【下限】：实际阈值 = max(此值, 悬浮态光晕半宽 2×CanvasArrowLen·z/√3)，
    // 见 TryHitCanvasEdge —— 指针进入描边区域即命中，且缩小时仍保有最小可命中范围。
    private const float CanvasEdgeHitThreshold = 6f;

    // 强调色（选中边框 / 连线选中 / 手柄）：对齐 App.xaml 的 GlanceAccent（Obsidian 紫），
    // 深浅主题各一档，由 UpdateThemeColors 按 ActualTheme 切换：深 #A882FF / 浅 #9873F7。
    private static readonly Windows.UI.Color CanvasAccentDark = Windows.UI.Color.FromArgb(255, 0xA8, 0x82, 0xFF);
    private static readonly Windows.UI.Color CanvasAccentLight = Windows.UI.Color.FromArgb(255, 0x98, 0x73, 0xF7);
    private Windows.UI.Color _canvasAccentColor = CanvasAccentLight;

    // 手柄（P3 连线 + P4 缩放）：四边中点=连线起点，四角=缩放。统一为【圆点】，
    // 恒定屏幕尺寸（页局部半径 = 屏幕半径 ÷ 页缩放），且只显"指针最接近的那一个"。
    private static readonly string[] CanvasSides = { "top", "bottom", "left", "right" };
    private static readonly string[] CanvasCorners = { "tl", "tr", "bl", "br" };
    private const double CanvasMinNodeWidth = 60;        // 缩放下限（页局部 PDF 点）
    // 高度下限 = 新建卡片默认高。此前误写为 40 > 默认高 ⇒ 新建空卡一旦放大，就再也缩不回
    // 新建时的大小（被这条下限卡住）。下限与默认高绑死，避免两者再次漂移（用户报告）。
    private const double CanvasMinNodeHeight = CanvasNode.DefaultHeight;
    private static readonly Windows.UI.Color CanvasHandleFill = Windows.UI.Color.FromArgb(255, 255, 255, 255);
    // 手柄尺寸一律用【页局部 PDF 点】（与卡片边框/圆角同一坐标空间）→ 随页面缩放一起放大/缩小，
    // 与卡片边框保持固定比例。
    private const float CanvasHandleRadius = 6.0f;        // 连线圆点半径（页局部 PDF 点，随页缩放）
    // 四角缩放 / 四边单轴缩放控件：尺寸仍用【页局部 PDF 点】随缩放（它们有光标变换指示，用户要求不改）。
    // 显形/命中半径（页局部 PDF 点）：指针靠近到该距离才显现且可选。原本 12，对小卡片而言
    // 上下两条命中带（各 12pt）几乎吃满 32pt 的默认卡高，中段留给"卡片本体拖动"的区域过窄，
    // 故向边框收紧 1/3（12→8），把更多中部空间还给本体拖动，同时保持手柄仍贴着边框易命中。
    private const float CanvasHandleHitRadius = 8.0f;
    // ⚠ 四边中点连线控件【落屏尺寸 = clamp(页局部半径 × 缩放, Min, Max)】：对齐 Obsidian ——
    //   · 中缩放区间：页局部半径×缩放 落在 [Min,Max] 内 ⇒ 控件与内容同比例随动（"先随动"）；
    //   · 缩太小：×缩放 跌破 Min ⇒ 夹在 Min（"再放大到合适"，不消失）；
    //   · 放太大：×缩放 顶破 Max ⇒ 夹在 Max（"再缩小到合适"，不撑爆）。
    // 这样任何缩放率下控件都大小合适，且 Glance 有右键拖拽连续缩放（CurrentZoom 实时更新），大小随缩放动态跟手。
    // 角/边缩放控件不参与 clamp：它们仍纯页局部、随缩放，用户要求不改。
    private const float CanvasConnHandleRadiusMinDip = 5.5f;   // 圆点屏幕半径下限（DIP）
    private const float CanvasConnHandleRadiusMaxDip = 14.0f;  // 圆点屏幕半径上限（DIP）
    private const float CanvasConnHandleHitMinDip = 10.0f;     // 触发/显形屏幕半径下限（DIP）
    private const float CanvasConnHandleHitMaxDip = 26.0f;     // 触发/显形屏幕半径上限（DIP）
    private const float CanvasCornerGap = 3.0f;           // 四角弧边相对卡片圆角的外扩量（页局部 PDF 点）
    // 四角弧边线宽直接复用 CanvasCardSelectedStrokeWidth(=2.0)，保证与选中边框同粗细。
    // 新建卡片的默认尺寸见 CanvasNode.DefaultWidth / DefaultHeight（视图落点与模型建节点共用）。
    private const double CanvasToolbarGap = 6;       // 浮动控件与卡片间距（视口 DIP）

    /// <summary>卡片文本排版缓存（文本/可用宽度任一变化才重建；其余帧零重建）。</summary>
    private sealed class CachedCanvasText
    {
        public string? Text;
        public double MaxWidth;
        public double MaxHeight;
        public int LineCount;            // 真实行数（不限高，溢出行也计入）——滚动条显隐/长度用
        public CanvasTextLayout? Layout; // IDisposable
        // 行内 Markdown（只作用于阅读态）：排版用显示串，样式表与底纹矩形随 layout 一起缓存，
        // 避免绘制期每帧重算（底纹矩形要调 GetCharacterRegions，会分配数组）。
        public string DisplayText = "";
        public IReadOnlyList<CanvasMarkdownSpan> Spans = Array.Empty<CanvasMarkdownSpan>();
        public IReadOnlyList<int>? SourceIndexMap;   // 显示下标 → 源串下标（null = 1:1）
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
            _canvasEditNodeId = null;
            ExitIbeamScope();   // 正在编辑的卡被删/撤销 ⇒ 退出编辑态：停工字作用域、恢复系统光标
            if (CanvasEditBox != null) CanvasEditBox.Visibility = Visibility.Collapsed;
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
        // 撤销/重做可能改到【正在编辑】卡片的文本 → 同步编辑框内容并把光标移到末尾。
        // 否则编辑框残留旧文本，用户失焦提交时会用旧文本覆盖掉刚撤销/重做的结果。
        if (_canvasEditNodeId != null && CanvasEditBox != null &&
            idx.TryGetValue(_canvasEditNodeId, out var editNode))
        {
            string t = editNode.Text ?? "";
            if (!string.Equals(_canvasEditText, t, StringComparison.Ordinal))
            {
                // 撤销/重做改到正在编辑的卡片 → 重写文档并把光标移到末尾（RichEditBox 没有 Text / SelectionStart）。
                int end = SetCanvasEditText(t);
                CanvasEditBox.Document.Selection.SetRange(end, end);
            }
            // 撤销/重做改到的文本即新的"已入史基线"，并撤销挂起中的去抖提交 ——
            // 否则停顿 400ms 后会把刚撤销掉的文本当成一次新输入又提交回去。
            _canvasLastCommittedText = t;
            StopCanvasEditCommitTimer();
        }
        if (CanvasOverlay != null && _selectedCanvasNodeId == null && _canvasEditNodeId == null &&
            _selectedCanvasEdgeId == null)
            CanvasOverlay.Visibility = Visibility.Collapsed;

        InvalidateOverlay();
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

    /// <summary>把颜色调淡：A 乘以 factor(0~1)，R/G/B 不变，得到同色低不透明度版本（Obsidian 式连线光晕用，白底上呈极浅的晕）。</summary>
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
        // （CanvasCardFill = #FFFFFFFF），所以盖上去就是完全遮住，不存在"半透明穿帮"。
        // ⚠ 这条绘制顺序同时定死了命中层级 —— 挡在上面的先命中，所以卡片的滚动条/四角/边手柄/
        //   本体全部优先，连线只能在卡片之外的空白处选中（见 Viewer_PointerPressed 的命中链，
        //   以及 UpdateCanvasEdgeHover 里按卡片矩形做的悬浮遮罩：看不见的东西不许亮）。
        if (pages.Count > 0 && edges.Length > 0)
        {
            GetViewState(out _, out double eHOff, out double eVOff);
            Rect EdgeDestOf(int idx) => ComputePageDest(idx, z, eHOff, eVOff);
            // 连线在屏幕坐标绘制：会话变换必须为恒等，防止任何上游泄漏的页变换把端点/控制点二次映射 → 连线脱离卡片。
            ds.Transform = Matrix3x2.Identity;
            DrawCanvasEdges(ds, z, pages, EdgeDestOf, index, edges);
            ds.Transform = savedTransform;
        }

        // ---- 卡片：逐可见页施加页变换，在页局部空间绘制（随页旋转/缩放）----
        foreach (var (p, dest) in visible)
        {
            bool pageHasCard = false;
            foreach (var node in nodes)
                if (node.GlancePage == p.Index) { pageHasCard = true; break; }
            if (!pageHasCard) continue;

            var pageXf = MakePageXform(dest, z);
            ds.Transform = pageXf.ToMatrix();

            // 逐卡片视口裁剪：屏外卡片本就看不见，不必浪费 fill/描边/文字栅格化（高强度多卡片白板的真瓶颈）。
            // 仿 PDF TileRenderCache 的 IsTileVisible/FreeNotVisible 思路，落到卡片粒度。
            // 用页变换把卡片外接矩形投到屏幕 DIP 取 AABB，与视口(0,0,_vpW,_vpH)相交测试；
            // 留 48f 余量（与连线裁剪同值）兜住边框/手柄的少量外扩，保证"贴边卡"不闪。
            // 连线独立绘制、走自己的包围盒裁剪，本裁剪不影响连线（见 DrawCanvasEdges）。
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
            // 被操作的控件恒定显示、不随指针漂移，其余卡片（含其他控件）一律不显示、不触发。
            bool gesturing = _isCanvasResizing || _isCanvasConnecting;
            string? gestureNodeId = _isCanvasResizing ? _canvasResizeNode?.Id
                                 : (_isCanvasConnecting ? _connFromNodeId : null);
            // 叠放层级：z 序 = _canvasNodes 数组顺序（jsoncanvas 无独立 z 字段）。选中卡片的【永久】置顶
            // 由 MainPage 重排模型数组并落盘完成（见 CanvasNodeBringToFrontRequested）—— 取消选中后仍保持。
            // 此处 topId 第二轮绘制是绘制期兜底：手势中把"正在操作的那张卡"置顶，不被邻居盖住。
            string? topId = gesturing ? gestureNodeId : sel;
            void DrawCardNode(CanvasNode node)
            {
                bool selected = sel != null && node.Id == sel;
                bool hovered = !selected && hov != null && node.Id == hov;

                // 编辑中的卡片也由 Win2D 绘制（含强调边框与手柄）：三态同引擎 ⇒ 文字不再因"切换渲染引擎"而位移。
                // 编辑态的 RichEditBox 仅作不可见输入宿主（Opacity=0），可见文字/光标/选区/滚动条皆由本循环 + Win2D 负责。

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
                if (topId != null && node.Id == topId) continue;   // 置顶卡片留到第二轮绘制
                if (!CardVisibleOnScreen(node)) continue;          // 屏外卡片跳过（连线独立绘制、不受影响）
                DrawCardNode(node);
            }
            if (topId != null && index.TryGetValue(topId, out var topNode) && topNode.GlancePage == p.Index)
                DrawCardNode(topNode);
            ds.Transform = savedTransform;
        }

        // ---- 拉线预览（顶层）：橡皮筋 + 吸附环，唯一压在卡片之上的"线"----
        // 它属于【操作反馈】而不是图元：沉到卡片下面会在拖过卡片时整段消失、目标吸附环被卡面切掉一半，
        // 拖拽过程直接失去反馈。它不参与命中（命中链里没有它），所以不破坏"卡片/控件全优先"。
        if (pages.Count > 0 && _isCanvasConnecting)
        {
            ds.Transform = Matrix3x2.Identity;
            DrawCanvasConnecting(ds);
            ds.Transform = savedTransform;
        }
    }

    /// <summary>画一张卡片。<paramref name="emphasized"/>=true（选中/悬停/手势中）时把【边框本身】加粗——
    /// 不在卡片外另套一层描边（对齐 Obsidian：就是那根边框变色）。未上色卡片同时换成强调色；
    /// 已上色卡片保留自身颜色（只靠加粗表达强调，理由见方法内注释）。
    /// <paramref name="z"/>=当前页缩放，仅编辑态画光标时需要把"1 屏幕像素"换算成页局部宽度。</summary>
    private void DrawCanvasCard(CanvasDrawingSession ds, CanvasNode node, bool emphasized, double z)
    {
        double w = Math.Max(12, node.Width);
        double h = Math.Max(12, node.Height);
        var rect = new Rect(node.X, node.Y, w, h);
        // 卡面：先铺【不透明白底】（必须不透明 —— 卡片要完全遮住身后的连线/其他卡片），
        // 上色卡片再薄叠一层本色的低透明度底纹（≈10%），做出"淡淡的同色氛围"。
        // ⚠ 不要把白底直接换成"半透明本色"：那样会变成半透明卡面、透出背景（见上）。
        ds.FillRoundedRectangle(rect, CanvasCardRadius, CanvasCardRadius, CanvasCardFill);

        // 边框色（步骤 5）：卡片已上色（node.Color 非空 = "#RRGGBB"）时用它自己的颜色；未上色回落默认灰。
        // 强调态的取舍（刻意如此）：
        //   ① 已上色卡片 —— 强调时【保留自身颜色、只用加粗表示】。若强调色压过卡片色，用户刚点完颜色
        //      卡片仍是强调紫，会以为"没生效"（选中态在选色后必然还在）。
        //   ② 未上色卡片 —— 沿用原强调色 + 加粗，选中/悬停反馈强度与旧版完全一致。
        // ⚠ "选了首位默认灰"（= #8A8F98）按【未上色】处理：它与未着色在视觉上等价，若当成"已上色"，
        //   同一张灰卡会随"有没有点过灰点"在选中时呈现紫/灰两种描边，属自相矛盾。
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
        ds.DrawRoundedRectangle(rect, CanvasCardRadius, CanvasCardRadius, strokeColor, strokeWidth);

        bool editing = node.Id == _canvasEditNodeId;
        // 编辑态用【实时文本】(_canvasEditText)：打字后到提交前的渲染不再滞后；其余态用节点文本。
        // node.Text 为 string?，故整体以 string? 承接（下方 IsNullOrEmpty 已处理空值）。
        string? drawText = editing ? _canvasEditText : node.Text;
        double tw = w - 2 * CanvasCardPadX;
        double th = h - 2 * CanvasCardPadY;
        CanvasTextLayout? layout = !string.IsNullOrEmpty(drawText)
            ? (editing ? GetCanvasEditTextLayout(tw, th) : GetCanvasTextLayout(node, tw, th))
            : null;
        if (layout == null && !editing) return;   // 非编辑态空卡：既不画文字也不画光标

        double topPad, scrollY;
        if (editing)
        {
            // 上方自适应留空与阅读态【同一公式、同一口径】（实时文本真实行数 × 标称行高）——
            // 进入编辑的瞬间 _canvasEditText == node.Text、行数一致 ⇒ 留白零跳变。
            topPad = ComputeCanvasEditTopPad(tw, th);
            // 滚动由 RichEditBox 内部 ScrollViewer 驱动 ⇒ 读其偏移，但夹到【真实可视滚动范围 visMax】。
            // 宿主底部人为留的"手柄命中带"padding 会凭空多出一小段可滚动量，不夹的话文字/光标会多滑这圈
            // 带高、末行永远悬在卡片底上方一段空档（口径见 TryGetCanvasEditScrollMetrics）。
            scrollY = TryGetCanvasEditScrollMetrics(node, out _, out _, out double visMax)
                ? Math.Clamp(_canvasEditScrollViewer!.VerticalOffset, 0, visMax)
                : 0;
        }
        else
        {
            // Obsidian 式垂直留白：上方【最多留半行】——空间足够时留半行，不够就挤上去（可为 0）；
            // 内容超出卡片（可滚动）时剩余为负 → 自然为 0。
            // ⚠ 内容高必须用【不限高的真实行数】（缓存里那个），不能用 layout.LineCount —— 后者已被 maxH 截断，
            //    溢出时会等于 th，导致"可滚动量恒为 0"、拖不动。
            double realContentH = GetCachedCanvasLineCount(node, tw, th) * CanvasLineHeight;
            topPad = Math.Min(0.5 * CanvasLineHeight, Math.Max(0, (th - Math.Min(th, realContentH)) / 2.0));
            double maxScroll = Math.Max(0, realContentH - th);
            scrollY = Math.Clamp(GetCanvasScrollY(node.Id), 0, maxScroll);
        }

        // CanvasTextLayout 只排版、不裁剪，超长文本会画到卡片外 → 用图层 clip 兜在卡片内边距区域内。
        var textRect = new Rect(node.X + CanvasCardPadX, node.Y + CanvasCardPadY, tw, th);
        using (ds.CreateLayer(1f, textRect))
        {
            if (editing) DrawCanvasEditSelection(ds, layout, textRect, topPad, scrollY);   // 选区高亮（layout 空时内部返回）
            if (layout != null)
            {
                // 行内代码/高亮底纹先铺，文字压在上面。两态各有各的排版串（编辑态标记是显示的），故各自取缓存。
                if (editing) DrawCanvasEditMarkdownBackdrops(ds, textRect, topPad, scrollY);
                else DrawCanvasMarkdownBackdrops(ds, node, textRect, topPad, scrollY);
                ds.DrawTextLayout(layout,
                    new Vector2((float)textRect.X, (float)(textRect.Y + topPad - scrollY)), CanvasCardText);
            }
            if (editing) DrawCanvasEditCaret(ds, layout, textRect, topPad, scrollY, z);    // 光标（空文本时画在原点）
        }
    }

    /// <summary>编辑态选区高亮：遍历 layout 的每一行，取与选区 [s,e) 的交集段，用 Win2D 画半透明矩形。
    /// 与可见文字同一 layout ⇒ 选区与文字像素级对齐（不再是"两引擎各自排版"的近似）。</summary>
    private void DrawCanvasEditSelection(CanvasDrawingSession ds, CanvasTextLayout? layout,
        Rect textRect, double topPad, double scrollY)
    {
        if (_canvasEditNodeId == null || CanvasEditBox == null || layout == null) return;
        if (AnyCanvasGestureActive) return;   // 编辑态手势期间抑制选区绘制（RichEditBox 会顺手选中文字，收尾还原）
        try
        {
            var selColor = GetCanvasSelectionColor();   // 循环外取一次（主题字典查找，不必每行重取）
            var live = _canvasEditLive;
            string display = live?.DisplayText ?? _canvasEditText;
            int len = display.Length;
            var sel = CanvasEditBox.Document.Selection;
            // 选区是【源串】下标，排版是【显示串】⇒ 先映射（隐藏的标记会让两边差若干个字符）。
            int a = live != null ? live.ToDisplay(Math.Clamp(Math.Min(sel.StartPosition, sel.EndPosition), 0, _canvasEditText.Length))
                                 : Math.Clamp(Math.Min(sel.StartPosition, sel.EndPosition), 0, len);
            int b = live != null ? live.ToDisplay(Math.Clamp(Math.Max(sel.StartPosition, sel.EndPosition), 0, _canvasEditText.Length))
                                 : Math.Clamp(Math.Max(sel.StartPosition, sel.EndPosition), 0, len);
            a = Math.Clamp(a, 0, len);
            b = Math.Clamp(b, 0, len);
            if (b <= a) return;
            var lms = layout.LineMetrics;
            if (lms == null) return;
            float dTop = (float)(textRect.Y + topPad - scrollY);
            // CanvasLineMetrics 没有 CharacterIndex/Length/NewlineLength：行首下标按 CharacterCount（含换行符）逐行累加，
            // 累计值即下一行起点，与字符串下标 1:1 对齐。GetCaretPosition(int,bool) 返回 Vector2（光标位置，非 Region）。
            // 选区跨行时右界取"本行文本末尾"（换行符的 leading）而非换行符之后，否则光标会跳到下一行行首 → 高亮塌成 1px。
            int lineStart = 0;
            // 行矩形按累积 rowTop 平铺：第 i 行 = [rowTop_i, rowTop_i + hgt_i)，相邻行【零重叠零缝隙】。
            // 之前直接用 GetCaretPosition 的 Y + lm.Height：各行 Y 取整方向不一致 ⇒ 相邻行矩形重叠 1px，
            // 半透明选区色叠加出"深蓝细线"（用户报告）。累积口径与 DWrite 行堆叠同源，天然无缝。
            float rowTop = 0;
            foreach (var lm in lms)
            {
                int lineEnd = lineStart + lm.CharacterCount;   // CharacterCount 含行尾换行符 ⇒ 即下一行起点
                float hgt = lm.Height > 0 ? lm.Height : (float)CanvasLineHeight;
                int ovs = Math.Max(lineStart, a);
                int ovEnd = (b >= lineEnd) ? lineEnd : b;      // 本行选区右界（独占下标）
                if (ovEnd <= ovs) { lineStart = lineEnd; rowTop += hgt; continue; }
                Vector2 pL = layout.GetCaretPosition(ovs, false);
                Vector2 pR;
                if (b >= lineEnd)
                {
                    // 整行选中：右界 = 本行【文本末尾】，不能取换行符之后（那会跳到下一行行首 → 高亮塌成 1px，
                    // 正是"跨行蓝底不完全"的根因）。末字符是换行符 '\n' → 取它的 leading（= 换行符之前 = 行末）；
                    // 末行无换行符 → 取其 trailing（= 文末）。
                    int last = lineEnd - 1;
                    bool lastIsNewline = last >= 0 && last < display.Length && display[last] == '\n';
                    pR = layout.GetCaretPosition(last, !lastIsNewline);
                }
                else
                {
                    pR = layout.GetCaretPosition(b - 1, true);   // 行内结束：末选中字符的 trailing
                }
                // 零宽段（选区恰好只含换行符等）不画：Math.Max(1f,…) 会把它强画成 1px 的选色竖条残留（用户报告）。
                float segW = pR.X - pL.X;
                if (segW > 0.05f)
                    ds.FillRectangle(new Rect(textRect.X + pL.X, dTop + rowTop, segW, hgt), selColor);
                lineStart = lineEnd;
                rowTop += hgt;
            }
        }
        catch { }
    }

    /// <summary>编辑态光标：用 Win2D 自身的 layout（与文字同源）取光标矩形，保证不偏移。1 屏幕像素宽换算成页局部宽度。
    /// 仅当编辑框持有焦点且光标处于"亮"相位时才画（亮灭由闪烁定时器控制）。</summary>
    private void DrawCanvasEditCaret(CanvasDrawingSession ds, CanvasTextLayout? layout,
        Rect textRect, double topPad, double scrollY, double z)
    {
        if (_canvasEditNodeId == null || CanvasEditBox == null) return;
        // 手势期间不抑制：边框控件交互不应改动编辑态 —— 插入光标原地照常闪烁（选区在按下瞬间已还原、捕获已交还，
        // 活动端不会再被 RichEditBox 带走，故不会乱跳）。
        if (!_canvasCaretVisible || CanvasEditBox.FocusState == FocusState.Unfocused) return;
        try
        {
            int len = _canvasEditText.Length;
            var sel = CanvasEditBox.Document.Selection;
            // 活动端推断：ITextSelection（WinUI 3）没有 ActiveEnd，只有规范化 Start/End。
            // 拖选中锚端不动、活动端移动 ⇒ 对比上一帧哪端变化；两端都变（双击选词/新选区）取距上一
            // 活动端更近的一端。之前用 Max(Start,End) 恒取右端 ⇒ 从右往左拖时光标钉死在右端不跟鼠标。
            int st = Math.Clamp(sel.StartPosition, 0, len);
            int en = Math.Clamp(sel.EndPosition, 0, len);
            int active;
            if (st == en) active = st;                                                   // 折叠光标：无歧义
            else if (_canvasSelLastStart < 0) active = en;                               // 无基线：默认右端
            else if (st != _canvasSelLastStart && en == _canvasSelLastEnd) active = st;  // 仅左端动 → 向左拖
            else if (en != _canvasSelLastEnd && st == _canvasSelLastStart) active = en;  // 仅右端动 → 向右拖
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
                Vector2 p = layout.GetCaretPosition(ci, false);   // 返回 Vector2（光标位置，非 Region）
                cx = p.X;
                cy = p.Y;
                // 高度取本行行高（layout 统一行距，所有行 Height 相同）；兜底 CanvasLineHeight。
                var lms = layout.LineMetrics;
                ch = (lms != null && lms.Length > 0 && lms[0].Height > 0) ? lms[0].Height : (float)CanvasLineHeight;
            }
            else { cx = 0; cy = 0; ch = (float)CanvasLineHeight; }
            float cw = (float)(1.0 / Math.Max(z, 0.01));
            ds.FillRectangle(new Rect(textRect.X + cx, (float)(textRect.Y + topPad - scrollY) + cy,
                cw, Math.Max(1f, ch)), CanvasCardText);
        }
        catch { }
    }

    /// <summary>卡片内滚动条的几何（页局部坐标）；返回是否应当显示，rect 为条的矩形。
    /// **两条绘制路径共用**：阅读/选中态由 Win2D 画、编辑态由 XAML 层画 —— 该函数保证两者位置/尺寸/圆角口径完全一致。
    /// 位置：贴右边缘，四边都与卡片边框留隙（不侵入边框、不与边框视觉相交）；长度按 可视高/内容高；位置按 scrollTravel。
    /// ⚠ 返回的是【页局部坐标】（已含 node.X/node.Y）—— 页变换下的原点仍是页左上角，漏加卡片偏移会画到页原点。</summary>
    private bool TryGetCanvasScrollBarRect(CanvasNode node, out Rect rect)
    {
        rect = default;
        double w = Math.Max(12, node.Width);
        double h = Math.Max(12, node.Height);

        // 内容高 / 可视高 / 滚动比例：
        // · 编辑态 → 由宿主 ScrollViewer 的 ScrollableHeight 换算【真实内容/可视/最大滚动】
        //   （见 TryGetCanvasEditScrollMetrics）。仍以宿主实测为准，⛔ 绝不用"Win2D 量出的行数 × 行高"
        //   近似：两者换行点不同（字体度量/可用宽度/字距都有差），行数会算错 ⇒ 表现为"输入到溢出时
        //   出现偏晚、删到不溢出了却迟迟不消失"。
        // · 其余态 → 真实行数 × 行高 + 本卡内存滚动偏移（非编辑态卡片由 Win2D 绘制，滚动靠该偏移表现）。
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
        if (trackH <= 4 || barRight - barW <= 0) return false;          // 卡片太小 → 放弃

        double ratio = Math.Clamp(bodyH / contentH, 0.05, 1.0);
        double barH = Math.Clamp(trackH * ratio, Math.Min(CanvasScrollBarMinH, trackH), trackH);
        double x = node.X + barRight - barW;
        double y = node.Y + trackTop + (trackH - barH) * travel;
        rect = new Rect(x, y, barW, barH);
        return true;
    }

    /// <summary>取卡片文本真实行数（走绘制期缓存）。顺带触发一次 layout 重建以确保缓存不过期。</summary>
    private int GetCachedCanvasLineCount(CanvasNode node, double contentW, double boxH)
    {
        _ = GetCanvasTextLayout(node, contentW, Math.Max(1, boxH));
        return _canvasTextCache.GetValue(node, _ => new CachedCanvasText()).LineCount;
    }

    /// <summary>编辑态下的指针按下（handledEventsToo 通道）。编辑框覆盖整卡，RichEditBox 会吃掉 PointerPressed。
    ///
    /// 边框控件（滚动条 / 四角 / 四边中点 / 四边中段）的指针流【不在这里】：它们所在的命中带已被
    /// <see cref="SyncCanvasEditHitCarve"/> 从编辑框命中区【挖掉】（悬停即置 IsHitTestVisible=false），
    /// 控件交互全程走 Viewer 手势通道（与阅读态完全相同，含 CapturePointer）。因此能进到这里的按下
    /// 【必然是文本区按下】—— 交给 RichEditBox 正常定位光标/选择文本即可；编辑框因此对控件交互零参与。
    /// 只补一次自绘光标刷新（按下后插入光标位置变了，Win2D 需重画）。</summary>
    private void OnCanvasEditBoxPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_canvasEditNodeId == null) return;
        var pt = e.GetCurrentPoint(Viewer);
        bool isRight = pt.Properties.IsRightButtonPressed;
        // 与根级记录同源的 4 个字段（这里只补设备/左右键，时刻由 RecordCanvasEditPressOrigin 记）：
        // 编辑框通道在本事件里能更早拿到，根级通道随后会在同一事件里再写一遍（同值，不会分叉）。
        RecordCanvasEditPressOrigin(e);
        if (isRight) return;   // 右键 = 要菜单，交给 ContextMenuOpening（见 IsCanvasEditMenuRequestAuthorized）
        if (!pt.Properties.IsLeftButtonPressed) return;
        // 左键落在文本区 = 改光标 / 重开拖选 ⇒ 右键菜单的落点与可用性都失效，先收起。
        // （右键不在此收：它要弹菜单，见 CanvasEditBox_ContextMenuOpening。）
        HideCanvasEditContextMenu();
        var p = pt.Position;
        // 双击的第二下：**什么都不做**。
        // 选中态单击就已经进编辑（与 Obsidian 一致），所以第二下其实落在编辑态内部：原生会把它当成
        // "双击选词"（甚至整段选中）。用户要的是"光标停在第一下算好的位置"，故这里把选区恢复成
        // 进编辑时的折叠光标。（这次手势顺带补发的菜单请求由 ContextMenuOpening 的"左键不开菜单"规则压掉。）
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
                            _canvasSelLastStart = _canvasSelLastEnd = _canvasSelLastActive = -1;
                        }
                        catch { }
                        ResetCaretBlink();
                        ScreenCanvas.Invalidate();
                    });
                }
            }
        }
        // 点击定位接管（C）：可见串已按光标隐藏了部分标记 ⇒ 原生命中测试按【文档排版】算会偏，
        // 一律改用"编辑 layout 命中 → 反查源偏移"。双击/三击（同点连击）不接管：交给原生的选词/选段，
        // 只把该行标记全部展开（用户口径：双击该行展开该行全部语法）。
        if (!entryDoubleTap)
        {
            int docOffset = MapCanvasEditPointToDocOffset(p);
            if (docOffset >= 0)
            {
                _canvasEditDragAnchor = docOffset;
                _canvasEditDragLast = docOffset;
                if (IsCanvasEditClickRepeat(p)) ExpandCanvasEditLine(docOffset);
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

    /// <summary>视口坐标 → 命中某张卡的滚动条。命中区 = 滚动条矩形（页局部）外扩一点屏幕容差，
    /// 只认【真的画了滚动条】的卡片（TryGetCanvasScrollBarRect 为准）⇒ 不会误抢边框手柄。</summary>
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
            // 编辑态滚动条也是 Win2D 画的、不可命中元素，命中同样走这里（由编辑框的 PointerPressed 通道触发）。
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

    /// <summary>滚动条轨道几何（页局部，与 TryGetCanvasScrollBarRect 同源）。</summary>
    private static void GetCanvasScrollTrackGeom(CanvasNode node, out double trackTop, out double trackH)
    {
        double h = Math.Max(12, node.Height);
        double inset = CanvasCardSelectedStrokeWidth + CanvasScrollBarGap;
        trackTop = CanvasCardPadY + inset;
        trackH = (h - CanvasCardPadY - inset) - trackTop;
    }

    /// <summary>开始拖动滚动条：记下按下点（页局部 Y）与起始偏移（编辑态取 TextBox 的，其余态取本卡内存偏移）。</summary>
    private void BeginCanvasScrollDrag(CanvasNode node, Point vp)
    {
        if (!CanvasViewportToPage(vp, out _, out _, out double py)) return;
        _isCanvasScrollDragging = true;
        LockCanvasCursor(CanvasIdcArrow, node.Id == _canvasEditNodeId);   // 滚动条拖拽：锁定箭头光标（编辑卡保留系统工字）
        _canvasScrollDragNodeId = node.Id;
        // 拖动期间不更新 _canvasPointerPos（防手柄随光标漂移），但若拖动前【已经显示了】某个边缘控件，
        // 它会被定格在指针最后位置、一直不消失（滚动条紧贴卡片右缘，右中点/右角手柄极易先被点亮）。
        // ⇒ 开始拖滚动条时先取消掉当前显示的那个控件。
        _canvasPointerValid = false;
        _canvasScrollDragStartPointerY = py;
        _canvasScrollDragStartScrollY = node.Id == _canvasEditNodeId
            ? (_canvasEditScrollViewer?.VerticalOffset ?? 0)
            : GetCanvasScrollY(node.Id);
    }

    /// <summary>【编辑态边框控件的信号切割】把"编辑卡边框控件命中带"从编辑框的命中区里【挖掉】。
    ///
    /// 问题本质：编辑框是覆盖整卡的 RichEditBox。控件命中带（滚动条 / 四角 / 四边中点 / 四边中段）都落在
    /// 编辑框矩形内更进一步，按下的指针流先命中编辑框、被它的类处理器吃掉（挪插入光标 / 起选区 / 抓捕获拖选）——
    /// 这正是"用控件时编辑态被改动"的根因。事后用快照把光标跳回去只是补丁，信号仍然进了编辑框。
    ///
    /// 正解：指针悬停在控件命中带上时把 <see cref="UIElement.IsHitTestVisible"/> 置 false，让这条带对编辑框
    /// 【完全不可命中】。此后该带上的指针流落到覆盖层之下的 Viewer（覆盖层无背景、不拦截命中），走的是与阅读态
    /// 【完全相同】的 Viewer 手势通道 ⇒ 编辑框全程收不到任何指针事件：插入光标不挪、不起选区、不拖选、不丢焦点。
    /// 指针移回文本区（非控件带）时再置回 true，文本编辑恢复常态。
    ///
    /// 关键性质：<c>IsHitTestVisible</c> 只影响命中测试，【不影响键盘焦点】—— 编辑框仍持有焦点，
    /// 方向键 / 打字 / 快捷键照常，插入光标照常闪烁（由 Win2D 自绘，见 DrawCanvasEditCaret）。
    /// 靠 25ms 作用域轮询（UpdateIbeamScope）持续自愈，事件漏一条下一拍也会补齐。</summary>
    private void SyncCanvasEditHitCarve(Point vp)
    {
        if (_canvasEditNodeId == null || CanvasEditBox == null) return;
        if (AnyCanvasGestureActive) return;   // 手势进行中：通道已确定，保持现状（勿在捕获中改命中区）
        bool onBand = ResolveEditHoverCursor(vp) != null;
        if (CanvasEditBox.IsHitTestVisible != !onBand) CanvasEditBox.IsHitTestVisible = !onBand;
        // 把命中区还回编辑框时，元素级光标同步回工字（否则会残留上一个控件带的 resize/箭头光标一帧）。
        if (!onBand) CanvasEditBox.OverrideCursor(CanvasIBeamCursor);
    }

    /// <summary>是否有编辑态"卡片手势"在进行（滚动条拖动 / 四角缩放 / 四边连线）。</summary>
    private bool AnyCanvasGestureActive =>
        _isCanvasScrollDragging || _isCanvasResizing || _isCanvasConnecting || _isCanvasDragging;

    /// <summary>拖动中：把指针位移按「轨道可移动长度 : 可滚动量」线性映射成滚动偏移变化。
    /// 编辑态驱动 TextBox 内部的 ScrollViewer，其余态驱动本卡内存偏移。</summary>
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
        // 以撇开宿主底部那圈"手柄命中带"padding 带来的虚假可滚动量。
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

    /// <summary>滚轮滚动卡片内容。**仅在【选中态 / 编辑态】且指针停在内容溢出的卡片上时消费**本次滚轮；
    /// 阅读态（未选中）返回 false ⇒ 照常翻页 —— 与 Obsidian 一致。
    /// wheelDelta 用 WinUI 的 MouseWheelDelta（+120 = 远离用户/上滚 ⇒ 内容下移 ⇒ 偏移减小）。</summary>
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

        double step = CanvasLineHeight * 3;      // 一格滚轮 = 3 行
        if (editing)
        {
            var sv = _canvasEditScrollViewer;
            // 按【真实可视滚动范围】判定：宿主那圈"手柄命中带"padding 会凭空多出一小段可滚动量 ⇒
            // 新建空卡（1 行）其实没溢出却"能滚"。没真实溢出就不消费，交给页面翻页。
            if (sv == null ||
                !TryGetCanvasEditScrollMetrics(hit, out _, out _, out double visMax) || visMax <= 0.5)
                return false;
            double next = Math.Clamp(sv.VerticalOffset - wheelDelta / 120.0 * step, 0, visMax);
            sv.ChangeView(null, next, null, true);   // 关闭动画：由 ViewChanged 刷新滚动条
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

    /// <summary>Win2D 侧绘制滚动条 —— 【三态共用】。编辑态现在也走这里：编辑框已改为 Opacity=0 的不可见宿主，
    /// 不再有"不透明白底遮住画布"的问题，故滚动条可直接画在 Win2D 上（与阅读/选中态同一口径、同一观感）。
    /// 纯视觉层、不参与命中（命中由 TryHitCanvasScrollBar 负责），故不影响卡片边框/手柄的交互。</summary>
    private void DrawCanvasScrollBar(CanvasDrawingSession ds, CanvasNode node)
    {
        if (!TryGetCanvasScrollBarRect(node, out var rect)) return;
        float r = (float)(rect.Width / 2);
        ds.FillRoundedRectangle(rect, r, r, CanvasScrollBarColor);
    }

    /// <summary>取（阅读/选中态）卡片文本的 Win2D layout（走 ConditionalWeakTable 缓存）。
    /// 顺带用"高度不限"的 layout 缓存真实行数，供滚动条显隐/长度与上方留白公式使用。
    /// 排版对象是【行内 Markdown 的显示串】：无标记时它与 node.Text 逐字相同 ⇒ 与未启用时像素一致。</summary>
    private CanvasTextLayout? GetCanvasTextLayout(CanvasNode node, double maxW, double maxH)
    {
        if (maxW <= 0 || maxH <= 0 || string.IsNullOrEmpty(node.Text)) return null;
        var cache = _canvasTextCache.GetValue(node, _ => new CachedCanvasText());
        if (cache.Layout != null && cache.Text == node.Text &&
            Math.Abs(cache.MaxWidth - maxW) < 0.5 && Math.Abs(cache.MaxHeight - maxH) < 0.5)
            return cache.Layout;

        cache.Layout?.Dispose();
        _canvasTextFormat ??= CreateCanvasTextFormat();
        var md = CanvasMarkdown.Parse(node.Text);
        cache.DisplayText = md.DisplayText;
        cache.Spans = md.Spans;
        cache.SourceIndexMap = md.SourceIndexMap;
        cache.Layout = new CanvasTextLayout(ScreenCanvas, md.DisplayText, _canvasTextFormat, (float)maxW, (float)maxH);
        ApplyCanvasMarkdownFormatting(cache.Layout, md.DisplayText.Length, md.Spans);
        BuildCanvasMarkdownBackdrops(cache);
        // 顺带缓存【真实行数】：用高度不限的 layout 量，溢出的行也计入 —— 滚动条的显隐与长度要用它。
        // 放在这里可复用同一套"文本或宽度变了才重算"的失效判据，避免每帧重建 layout。
        try
        {
            using var full = new CanvasTextLayout(ScreenCanvas, md.DisplayText, _canvasTextFormat, (float)maxW, 1e5f);
            cache.LineCount = Math.Max(1, full.LineCount);
        }
        catch { cache.LineCount = 1; }
        cache.Text = node.Text;
        cache.MaxWidth = maxW;
        cache.MaxHeight = maxH;
        return cache.Layout;
    }

    /// <summary>把区间样式套到 layout 上。只做【不改变行高】的样式（字重/字形/删除线/等宽字体）——
    /// 一旦改字号（标题等），"行数 × 标称行高"的滚动与留白公式就会失准，那部分属 P2，另行处理。</summary>
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

    /// <summary>预先算好行内代码/高亮的底纹矩形（layout 坐标，绘制时再平移到文本原点）。
    /// 横向用 GetCharacterRegions 给的实际字框（代码跨行时自然分成多个矩形）；
    /// 竖向**不用**它给的整行高，改为按该行基线收到贴合字面 —— 理由见
    /// <see cref="CanvasMarkdownBackdropAscent"/>。</summary>
    private static void BuildCanvasMarkdownBackdrops(CachedCanvasText cache)
    {
        BuildCanvasMarkdownBackdrops(cache.Layout, cache.DisplayText.Length, cache.Spans, cache.Backdrops);
    }

    /// <summary>
    /// 底纹矩形计算的唯一实现（阅读态 / 编辑态共用，两态绝不会各算一套）。
    /// <para>
    /// 为什么竖向要自己算：<c>GetCharacterRegions</c> 的 <c>LayoutBounds</c> 是【整行框】——字号 12 的
    /// 自然行高约 16pt，而中文字面只占其中约 12pt，上下各约 2pt 的字体留白也被涂满 ⇒ 看起来"字浮在色块里"
    /// （用户报告）。这里改用【该行基线】± 固定 em 比例，收到刚好包住字面。
    /// </para>
    /// 拿不到基线（LineMetrics 为空/异常）时退回整行框，保证不出现零高底纹。
    /// </summary>
    private static void BuildCanvasMarkdownBackdrops(CanvasTextLayout? layout, int textLength,
        IReadOnlyList<CanvasMarkdownSpan> spans, List<(Rect Bounds, CanvasMarkdownSpanKind Kind)> into)
    {
        into.Clear();
        if (layout == null || spans.Count == 0) return;

        // 每行的行顶与基线（CanvasLineMetrics.Baseline = 从【本行行顶】到基线，故基线要加行顶）。
        // ⚠ LineMetrics 是【数组】（CanvasLineMetrics[]），取长度用 Length —— 写成 Count 会被解析成 LINQ 的
        //   Count() 方法组而编译不过（本轮踩过）。
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
                    // 恰好等于行顶（region 框是整行框还是字面框、有没有上下伸部，中点都仍落在本行内）。
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

    /// <summary>编辑态版本：底纹来自编辑 layout 的缓存。</summary>
    private void DrawCanvasEditMarkdownBackdrops(CanvasDrawingSession ds, Rect textRect,
        double topPad, double scrollY)
    {
        if (_canvasEditBackdrops.Count == 0) return;
        DrawCanvasMarkdownBackdropRects(ds, _canvasEditBackdrops, textRect, topPad, scrollY);
    }

    private static void DrawCanvasMarkdownBackdropRects(CanvasDrawingSession ds,
        List<(Rect Bounds, CanvasMarkdownSpanKind Kind)> backdrops, Rect textRect, double topPad, double scrollY)
    {
        double ox = textRect.X;
        double oy = textRect.Y + topPad - scrollY;
        // 卡片上下内边距为 0（CanvasCardPadY）⇒ 文字区域与卡片边线重合，而描边【以边线为中心】、
        // 内半边落在卡片内侧（选中态 2pt ⇒ 内伸 1pt）。底纹再内缩这么多，才不会压到边框本身
        // （用户报告："首行文字的底色盖到卡片边框上"）。
        const float borderInner = CanvasCardSelectedStrokeWidth * 0.5f + 0.25f;
        float minTop = (float)textRect.Y + borderInner;
        float maxBottom = (float)(textRect.Y + textRect.Height) - borderInner;
        foreach (var (bounds, kind) in backdrops)
        {
            double rx = bounds.X + ox;
            double rw = bounds.Width;
            double ry, rh;
            if (kind == CanvasMarkdownSpanKind.Code)
            {
                // 行内代码保留一点点内边距，读起来像"胶囊"；竖向只加 0.5，不再用整行高。
                rx -= 1.2;
                rw += 2.4;
                ry = bounds.Y + oy - 0.5;
                rh = bounds.Height + 1.0;
            }
            else
            {
                rx -= 0.6;
                rw += 1.2;
                ry = bounds.Y + oy;
                rh = bounds.Height;
            }

            // 夹进卡片内侧（滚动时首/末行底纹本就会被裁到卡边，这里一并挡住压在边框上的那一条）。
            double top = Math.Max(ry, minTop);
            double bottom = Math.Min(ry + rh, maxBottom);
            if (bottom - top <= 0.01) continue;
            var rect = new Rect(rx, top, rw, bottom - top);

            if (kind == CanvasMarkdownSpanKind.Code)
                ds.FillRoundedRectangle(rect, 2f, 2f, CanvasMarkdownCodeBackdrop);
            else
                ds.FillRectangle(rect, CanvasMarkdownHighlightBackdrop);
        }
    }

    // 编辑态用单独缓存：编辑中的卡片文字以【实时文本】(_canvasEditText) 排版，不能与节点文本(node.Text)的缓存混用
    // （否则提交前的实时文本会污染节点缓存）。每次编辑进入只可能有一张卡 ⇒ 单槽即可。
    private CanvasTextLayout? _canvasEditTextLayout;
    private int _canvasEditTextLayoutKey = -1;   // 缓存键 = 可见串版本号（随光标/选区/展开状态递增）
    private double _canvasEditTextLayoutW, _canvasEditTextLayoutH;
    private int _canvasEditTextLayoutLineCount = 1;
    // 编辑态的行内代码/高亮底纹（随编辑 layout 一起重建）。
    private readonly List<(Rect Bounds, CanvasMarkdownSpanKind Kind)> _canvasEditBackdrops = new();

    // ---- 实时渲染（C，Obsidian 式逐处展开）----
    // 可见串随光标变化：成对标记默认隐藏，光标落在哪一对里就展开哪一对；双击展开整行；全选展开全文。
    // ⇒ 布局/命中在【显示串】坐标，光标/选区在【源串】坐标，两边都要过 _canvasEditLive 的映射。
    private CanvasMarkdownLiveResult? _canvasEditLive;
    private string _canvasEditLiveSrc = "";         // 这次可见串对应的源文本（引用比较即可，每次编辑都换新串）
    private int _canvasEditLiveVersion;             // 每次重算 +1：布局缓存据此失效（不拼字符串 key，避免每帧分配）
    private int _canvasEditLiveA = -1, _canvasEditLiveB = -1;
    private int _canvasEditLiveE0 = -2, _canvasEditLiveE1 = -2;
    private bool _canvasEditLiveAll;
    // 双击展开整行：只记"行首偏移 + 是否生效"，展开范围每次按【当前光标行】现算 ——
    // 存死区间会在行内插入/删除后错位（区间漂到相邻行 ⇒ 展开错行的标记）。
    private bool _canvasEditExpandActive;
    private int _canvasEditExpandLineStart = -1;
    private int _canvasEditDragAnchor = -1;         // 编辑态拖选锚点（源串偏移）
    private int _canvasEditDragLast = -1;           // 上一次已落定的拖选活动端（去重，免得每次移动都写 TOM）
    private int _canvasEditPendingCaret = -1;       // 排队中的"单击落光标"请求（拖选/新点击会把它作废）
    private long _canvasEditClickTick;              // 编辑态"点击序列"识别（双击展开行）
    private Point _canvasEditClickVp;

    /// <summary>按当前光标/选区/展开状态算出这一帧的可见串（含双向映射）。参数全没变时直接返回缓存 ——
    /// 绘制每帧都会调用它，故这里刻意零字符串分配（不拼 key，改比"源串引用 + 四个下标 + 全选标志"）。</summary>
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
        if (display.Length == 0) return null;   // 空文本：调用方（光标/选区）各自走"无排版"分支
        if (_canvasEditTextLayout != null && _canvasEditTextLayoutKey == _canvasEditLiveVersion &&
            Math.Abs(_canvasEditTextLayoutW - maxW) < 0.5 && Math.Abs(_canvasEditTextLayoutH - maxH) < 0.5)
            return _canvasEditTextLayout;
        _canvasEditTextLayout?.Dispose();
        _canvasTextFormat ??= CreateCanvasTextFormat();
        _canvasEditTextLayout = new CanvasTextLayout(ScreenCanvas, display, _canvasTextFormat, (float)maxW, (float)maxH);
        // 样式区间由 ParseLive 直接给在【显示串】坐标上（含单边语法延到文末），这里不用再换算。
        ApplyCanvasMarkdownFormatting(_canvasEditTextLayout, display.Length, live.Spans);
        BuildCanvasEditBackdrops(_canvasEditTextLayout, display.Length, live.Spans);
        // 顺带缓存【真实行数】：用高度不限的 layout 量，与阅读态 GetCanvasTextLayout 同一口径（留白公式要用）。
        try
        {
            using var full = new CanvasTextLayout(ScreenCanvas, display, _canvasTextFormat, (float)maxW, 1e5f);
            _canvasEditTextLayoutLineCount = Math.Max(1, full.LineCount);
        }
        catch { _canvasEditTextLayoutLineCount = 1; }
        _canvasEditTextLayoutKey = _canvasEditLiveVersion;
        _canvasEditTextLayoutW = maxW;
        _canvasEditTextLayoutH = maxH;
        return _canvasEditTextLayout;
    }

    /// <summary>编辑态版本：底纹矩形按【编辑 layout】预算（文本与文档 1:1，不含剔除）。
    /// 与阅读态那份分开缓存 —— 两态的字符串很可能不同（标记在编辑态是显示的）。
    /// 算法与阅读态共用同一个实现 ⇒ 进/出编辑底纹不会跳变。</summary>
    private void BuildCanvasEditBackdrops(CanvasTextLayout layout, int textLength,
        IReadOnlyList<CanvasMarkdownSpan> spans)
    {
        BuildCanvasMarkdownBackdrops(layout, textLength, spans, _canvasEditBackdrops);
    }

    /// <summary>编辑态实时文本的【真实行数】（走编辑专用缓存；空文本按 1 行）。</summary>
    private int GetCanvasEditLineCount(double tw, double th)
    {
        if (string.IsNullOrEmpty(_canvasEditText)) return 1;
        _ = GetCanvasEditTextLayout(tw, th);   // 触发缓存重建/复用
        return _canvasEditTextLayoutLineCount;
    }

    /// <summary>编辑态"上方最多留半行"留白 —— 与阅读态完全同一公式/同一口径（真实行数 × 标称行高）。</summary>
    private double ComputeCanvasEditTopPad(double tw, double th)
    {
        double realContentH = GetCanvasEditLineCount(tw, th) * CanvasLineHeight;
        return Math.Min(0.5 * CanvasLineHeight, Math.Max(0, (th - Math.Min(th, realContentH)) / 2.0));
    }

    /// <summary>编辑态滚动口径（与宿主底部那圈"手柄命中带"padding 解耦）。
    /// PositionCanvasEditBox 给编辑宿主的【下 padding】额外留了 <see cref="CanvasHandleHitRadius"/> 一圈带
    /// （为承接下中点手柄的箭头光标，见那里的注释），它不是真实文本内容、却把宿主视口压低 ⇒ 直接拿宿主的
    /// Extent/Viewport/ScrollableHeight 画滚动条，会让【新建空卡（只有 1 行）】也判定为溢出而弹条（用户报告）。
    /// 这里统一换算成【真实可视高 visH / 真实内容高 contentH / 可视最大滚动 visMax】：
    ///   ⚠ 与宿主上报口径无关地成立：ScrollableHeight = 真实内容高 − 宿主可用高（算式中 padding 项两边相消）
    ///     ⇒ 真实内容高 = (卡片高 − topPad − 手柄带高) + ScrollableHeight。
    /// 显隐/长度/行程都用这套口径 ⇒ 与"Win2D 实际画出来的文字"一致。</summary>
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
        double hostH = Math.Max(1, th - topPad - CanvasHandleHitRadius);   // 宿主可用高（含人为留的手柄带）
        double scrollable = Math.Max(0, sv.ScrollableHeight);
        contentH = hostH + scrollable;                                     // 真实内容高
        visH = Math.Max(1, th - topPad);                                   // 真实可视高（= Win2D 文字可占高）
        visMax = Math.Max(0, contentH - visH);
        return true;
    }

    /// <summary>把编辑态"上方自适应留空"同步到编辑框 Padding.Top（⚠ 只改 top：左右变化会重排文字、可能形成布局回环）。
    /// 必须与 Win2D 绘制用同一 ComputeCanvasEditTopPad ⇒ "点击落光标"的命中映射与可见渲染一致。</summary>
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
        string? hovEdge = _hoverCanvasEdgeId;
        foreach (var edge in edges)
        {
            if (!ComputeEdgeScreenGeometry(edge, pages, destOf, index, z,
                    out var a1, out var c1, out var c2, out var a2))
                continue;

            // 可见段裁剪：跨页连线两端可能都在视口外（如从第 1 页连到第 20 页，用户在第 10 页阅读）。
            // 用 {a1,c1,c2,a2} 的包围盒做保守剔除 —— 曲线必落在该盒内，盒与视口不相交 ⇒ 整条不可见。
            // 只跳过多余的 PathBuilder/Geometry 构建与绘制，视觉结果不变。
            float bbMinX = MathF.Min(MathF.Min(a1.X, a2.X), MathF.Min(c1.X, c2.X));
            float bbMaxX = MathF.Max(MathF.Max(a1.X, a2.X), MathF.Max(c1.X, c2.X));
            float bbMinY = MathF.Min(MathF.Min(a1.Y, a2.Y), MathF.Min(c1.Y, c2.Y));
            float bbMaxY = MathF.Max(MathF.Max(a1.Y, a2.Y), MathF.Max(c1.Y, c2.Y));
            const float edgeCullMargin = 48f;   // 容纳线宽/箭头头部的少量外扩
            if (bbMaxX < -edgeCullMargin || bbMinX > _vpW + edgeCullMargin ||
                bbMaxY < -edgeCullMargin || bbMinY > _vpH + edgeCullMargin)
                continue;

            // 强调态（学 Obsidian）：线自身颜色全程不变；强调时主线变粗到箭头三角底边一半，
            // 外圈画一条同色但极低不透明度的更粗光晕（白底上呈极浅的晕），光晕同样包裹箭头；选中态 = 锁定悬浮态外观。
            // 出现/收回动画 = 以线为中心「径向长胖/变瘦」：几何始终是整条线，宽度与不透明度随 p 插值，
            // 不做沿线的生长收缩。进度 p(0..1) 由 _edgeEmph 逐帧推进。箭头在阅读/悬浮/选中态均实心同色绘制。
            var edgeColor = ParseCanvasColor(edge.Color) ?? CanvasEdgeColor;   // 线自己的颜色，全程用它
            _edgeEmph.TryGetValue(edge.Id, out float emph);                    // 0=原样, 1=完全强调
            // 箭头高随缩放：页局部点 × z = 屏幕 DIP（与卡片手柄同约定），缩小时箭头变小、放大时变大。
            float arrowLen = MathF.Max(CanvasArrowMinLen, CanvasArrowLen * (float)z);
            // 线宽随缩放（沿用上方箭头/卡片手柄的 ×z 约定）：卡片边框经页变换、箭头经 arrowLen·z 都已随 z 缩放，
            // 连线描边若用定值会保持原粗，小缩放时相对已缩小的卡片显得过粗。z=1 时即原 1.6DIP，零行为变化。
            float edgeW = MathF.Max(CanvasEdgeMinWidth, CanvasEdgeStrokeWidth * (float)z);
            bool toArrow = IsArrowEnd(edge.ToEnd);
            bool fromArrow = IsArrowEnd(edge.FromEnd);

            // 箭头三角半宽 = 高 / √3（等边）；主线强调宽 = 底边一半 = 此值，外圈光晕宽 = 两倍底边 = 4×此值。
            float halfBase = arrowLen / MathF.Sqrt(3f);
            float emphMainW = halfBase;                 // 主线强调宽
            float emphHaloW = 4f * halfBase;            // 外圈光晕宽（两倍箭头底边）

            // 端点收缩到箭头三角底边：箭头端时线止于底边，箭头（同色实心）从底边画到卡片边框，
            // 使尖点落在卡片、线不穿三角内部；非箭头端线照常伸到卡片。收缩只算一次、全状态共用。
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
            // 的同色图形会在衔接处因抗锯齿漏出底色（极细亮缝）；重叠后该段被上层实心箭头盖住、缝消失。
            // 代价：满强调时线宽=箭头半宽，伸入段两方角会在三角斜边外露出 ≈ 延伸量/√3（z=1 时 <1px），远不显眼。
            // p3/p0 本就落在末段控制腿 c2→a2 / c1→a1 上，延伸端点仍在该腿上，曲线形状几乎不变。
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
            // 动画 = 以线为中心「径向长胖/变瘦」：宽与不透明度随 p 插值，不做沿线生长。
            // 两层几何不同（关键，别再合并成一条）：
            //   主线止于箭头底边 p0/p3 —— 主线强调宽 = 箭头半宽，若伸到顶点，平头帽会在收尖的三角两侧露出成「柱状」尖点；
            //   光晕沿整条线 a1/a2 延伸到顶点（卡片边框）—— 光晕极淡且画在不透明箭头之下，三角内部被箭头盖住，
            //   两侧露出的部分正好「光晕包裹箭头」，平头端落在边框上与尖点齐平（即把描边延长到箭头顶点）。
            if (emph > 0.001f)
            {
                // 底层光晕：整条线（到卡片边框），同色极低不透明度（随 p 淡入）、宽随 p 从细线宽插值到 4×底边。
                using (var haloGeo = BuildBezier(a1, c1, c2, a2))
                {
                    if (haloGeo != null)
                        ds.DrawGeometry(haloGeo, WithAlpha(edgeColor, 0.3f * emph), Lerp(edgeW, emphHaloW, emph));
                }
                // 主线：收缩到箭头底边再向顶点延伸 seamOv（消缝），宽随 p 从细线宽插值到箭头底边一半，仍为线自身颜色。
                using (var mainGeo = BuildBezier(l0, c1, c2, l3))
                {
                    if (mainGeo != null)
                        ds.DrawGeometry(mainGeo, edgeColor, Lerp(edgeW, emphMainW, emph));
                }
            }

            // 箭头：阅读/悬浮/选中态均绘制，实心同色、尖点落在卡片边框、底与线收尾处衔接成连续尖点
            // （线已收缩到箭头底边，故强调时线「长胖」与箭头同态、且线不穿三角内部）。
            if (toArrow) DrawArrowHead(ds, p3, a2, edgeColor);
            if (fromArrow) DrawArrowHead(ds, p0, a1, edgeColor);
        }
    }

    /// <summary>计算一条连线在屏幕空间的几何（端点 + 控制点）。命中测试与渲染共用同一套算法，
    /// 保证"看得到就能点到"。边名/页变换与旧版 DrawCanvasEdges 完全一致。</summary>
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
        // （旋转只在绘制时绕页中心叠加），故 side 名在两坐标系间通用。
        double fcx = fPage.LayoutRect.X + fn.X + fn.Width / 2.0;
        double fcy = fPage.LayoutRect.Y + fn.Y + fn.Height / 2.0;
        double tcx = tPage.LayoutRect.X + tn.X + tn.Width / 2.0;
        double tcy = tPage.LayoutRect.Y + tn.Y + tn.Height / 2.0;
        string fSide = edge.FromSide ?? AutoSide(fcx, fcy, tcx, tcy, true);
        string tSide = edge.ToSide ?? AutoSide(fcx, fcy, tcx, tcy, false);

        var fxf = MakePageXform(destOf(fn.GlancePage), z);
        var txf = MakePageXform(destOf(tn.GlancePage), z);

        var fp = SideAnchor(fn, fSide);
        var tp = SideAnchor(tn, tSide);
        a1 = fxf.Map(fp.X, fp.Y);
        a2 = txf.Map(tp.X, tp.Y);

        // 控制点：沿各自出边法向（屏幕空间）外推，外推量随两端距离自适应。
        float dist = Vector2.Distance(a1, a2);
        float k = Math.Clamp(dist * 0.4f, 24f, 120f);
        var n1 = fxf.MapDirection(SideNormal(fSide).X, SideNormal(fSide).Y);
        var n2 = txf.MapDirection(SideNormal(tSide).X, SideNormal(tSide).Y);
        c1 = a1 + n1 * k;
        c2 = a2 + n2 * k;
        return true;
    }

    /// <summary>三次贝塞尔取点。</summary>
    private static Vector2 BezierPoint(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1 - t;
        float uu = u * u, tt = t * t;
        return uu * u * p0 + 3 * uu * t * p1 + 3 * u * tt * p2 + tt * t * p3;
    }

    /// <summary>浮点线性插值。</summary>
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>点到线段的最短距离。</summary>
    private static float DistancePointSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-6f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>点到三次贝塞尔曲线的最短距离（采样折线近似，足够做命中判定）。</summary>
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

    /// <summary>视口坐标 → 命中的连线 id（取屏幕距离最近且在阈值内者）。</summary>
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

        var p = new Vector2((float)vp.X, (float)vp.Y);
        // 命中阈值 = 悬浮态描边（光晕）区域的半宽：光晕满宽 4×halfBase、halfBase = CanvasArrowLen·z/√3，
        // 即"指针进入当前描边区域即触发悬浮"，且随页面缩放一致放大/缩小。
        // 下限保底 CanvasEdgeHitThreshold：深度缩小后光晕半宽趋近 0，不保底会几乎无法命中。
        float best = MathF.Max(CanvasEdgeHitThreshold, 2f * CanvasArrowLen * (float)z / MathF.Sqrt(3f));
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

    /// <summary>选中连线【边工具栏锚点】的屏幕坐标（仿 Obsidian Canvas）：
    /// X 取连线贝塞尔中点 X，Y 取靠上方的那张卡片连接点 Y（即两连接点中较小者）。
    /// 位置出厂在视口外时由调用方双向钳制到视口边缘。</summary>
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
        float upperY = MathF.Min(a1.Y, a2.Y);   // 靠上方的连接点 Y
        anchor = new Vector2(midX, upperY);
        return true;
    }

    /// <summary>指针是否落在"卡片本体或其边框控件命中带"上 —— 即按下会落到卡片/控件上的全部区域。
    /// ⚠ 判据必须与 <see cref="Viewer_PointerPressed"/> 的前四支、<see cref="ResolveCanvasHoverCursor"/>
    /// **完全同源**，不能只测卡片矩形：边框控件的命中带向卡外探出，且三者半径不同
    /// （悬停手柄=clamp(12z,10,26) DIP、四边中段带=12 页局部点、四角=8 页局部点，高缩放下中段带反而更宽）
    /// ⇒ 只测卡内会在卡外留一道"光标已是控件态、连线却仍高亮"的缝（用户报告）。</summary>
    private bool IsPointOnCanvasCardOrControl(Point vp) =>
        TryHitCanvasNode(vp, out _, out _, out _, out _) ||
        TryHitCanvasCorner(vp, out _, out _) ||
        TryHitCanvasHandle(vp, out _, out _) ||
        TryHitCanvasEdge(vp, out _, out _);

    /// <summary>指针是否落在白板【浮层控件】上（卡片悬浮胶囊 / 连线工具条）。
    /// 它们是盖在 Win2D 画布之上的 XAML 元素，指针在上面时属于"操作 chrome"而不是操作画布 —— 与卡片遮罩同一原则：
    /// 挡在指针下面的连线不许亮（否则鼠标停在胶囊上、后面的连线却在闪，用户报告）。
    /// 编辑框不在其列：它与卡片同矩形，已被 <see cref="IsPointOnCanvasCardOrControl"/> 的卡片矩形覆盖。
    /// 用元素实测矩形（TransformToVisual）而不是记住的 Margin：胶囊尺寸/居中/钳制都由布局决定，记常量必然对不上。</summary>
    private bool IsPointOverCanvasOverlayChrome(Point vp) =>
        IsPointInsideVisibleElement(CanvasCardToolbar, vp) ||
        IsPointInsideVisibleElement(CanvasEdgeToolbar, vp) ||
        // 编辑态文本菜单：也算浮层 chrome，否则指针停在菜单上时背后的卡片/连线会跟着高亮。
        IsPointInsideVisibleElement(CanvasEditContextMenu, vp);

    /// <summary>vp 是否落在某个【可见】元素的当前布局矩形内（坐标换算到 Viewer 系）。
    /// Collapsed / 尚未布局（ActualWidth=0）/ 抛异常 一律算"不在"，绝不外抛。</summary>
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

    /// <summary>无手势时的连线悬停高亮（指针靠近即变强调色）。
    /// 两重遮罩，原则都是"看得见挡着的东西优先"：
    /// ① 卡片/控件（<see cref="IsPointOnCanvasCardOrControl"/>，与光标和按下同源）；
    /// ② 浮层 chrome（<see cref="IsPointOverCanvasOverlayChrome"/>：卡片胶囊、连线工具条）。
    /// ⇒ 鼠标呈控件态或停在浮层上时，连线绝不高亮；反过来"还亮的范围"总是可以点选连线的范围。</summary>
    private void UpdateCanvasEdgeHover(Point vp)
    {
        if (IsPointOnCanvasCardOrControl(vp) || IsPointOverCanvasOverlayChrome(vp))
        {
            if (_hoverCanvasEdgeId == null) return;   // 已是空，避免每帧无效重绘
            _hoverCanvasEdgeId = null;
            InvalidateOverlayThrottled();
            return;
        }
        string? hover = TryHitCanvasEdge(vp, out var id) ? id : null;
        if (string.Equals(hover, _hoverCanvasEdgeId, StringComparison.Ordinal)) return;
        _hoverCanvasEdgeId = hover;
        InvalidateOverlayThrottled();
    }

    /// <summary>无手势时的卡片悬停判定：指针进入卡片（或其边缘手柄外扩区）即置 <see cref="_hoverCanvasNodeId"/>，
    /// 渲染侧据此显出四边连线圆点/四角缩放块与淡描边；指针移开即清空。仅在状态变化时重绘。</summary>
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

    /// <summary>命中"手柄可及"的卡片：卡片矩形在【页局部空间】外扩「手柄命中半径」（页局部点）后再测，
    /// 使指针靠近卡片边缘（手柄所在处）即视为悬停该卡 —— 手柄因此能"指针一来就出现"，
    /// 与 Obsidian 一致（悬停边缘即可直接拖连线/缩放，无需先单击）。取绘制顺序最上层者。</summary>
    private bool TryHitCanvasNodeForHandles(Point vp, out CanvasNode? node)
    {
        node = null;
        if (!CanvasViewportToPage(vp, out int pageIndex, out double px, out double py)) return false;
        // 悬停判定外扩半径：取"连线控件落屏半径 clamp 值"换算回页局部（页局部 = 屏幕DIP / 缩放），
        // 使四边拉线控件的悬浮显示区在任何缩放率下都合适（缩太小不消失、放太大不撑爆），与 TryHitCanvasHandle 同口径。
        // 四角/单轴缩放控件自身命中半径仍是页局部 8pt（用户要求不改）。
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

    /// <summary>卡片某条边的中点（页局部 PDF 点）。</summary>
    private static Vector2 SideAnchor(CanvasNode n, string side) => side switch
    {
        "top" => new Vector2((float)(n.X + n.Width / 2), (float)n.Y),
        "bottom" => new Vector2((float)(n.X + n.Width / 2), (float)(n.Y + n.Height)),
        "left" => new Vector2((float)n.X, (float)(n.Y + n.Height / 2)),
        _ => new Vector2((float)(n.X + n.Width), (float)(n.Y + n.Height / 2)),
    };

    /// <summary>卡片某个角（页局部 PDF 点）。</summary>
    private static Vector2 CornerAnchor(CanvasNode n, string corner)
    {
        double l = n.X, t = n.Y, r = n.X + n.Width, b = n.Y + n.Height;
        return corner switch
        {
            "tl" => new Vector2((float)l, (float)t),
            "tr" => new Vector2((float)r, (float)t),
            "bl" => new Vector2((float)l, (float)b),
            _ => new Vector2((float)r, (float)b),   // br
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

    /// <summary>按两卡片中心的相对位置自动选边（主轴支配）。from=true 取出边，false 取入边。</summary>
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

    /// <summary>画实心箭头：底边中心落在 baseC，尖端(tip)在 baseC 指向 tip 的方向上，屏幕坐标。
    /// 调用方负责让 baseC = 连线终点、tip = 卡片边框边中点，使尖端正好压在边框上、连线不穿过三角内部。
    /// 三角形为等边：高 = |tip - baseC|，半宽 = 高 / √3（Obsidian 风格，而非旧版窄等腰三角形）。</summary>
    private void DrawArrowHead(CanvasDrawingSession ds, Vector2 baseC, Vector2 tip)
        => DrawArrowHead(ds, baseC, tip, CanvasEdgeColor);

    /// <summary>同上显式颜色重载（连线预览用强调色）。</summary>
    private void DrawArrowHead(CanvasDrawingSession ds, Vector2 baseC, Vector2 tip, Windows.UI.Color color)
    {
        var dir = tip - baseC;
        float len = dir.Length();
        if (len < 1e-3f) return;
        dir /= len;
        var perp = new Vector2(-dir.Y, dir.X);
        float half = len / MathF.Sqrt(3f);   // 等边三角形：半宽 = 高 / √3
        using var pb = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(ScreenCanvas);
        pb.BeginFigure(tip);
        pb.AddLine(baseC + perp * half);
        pb.AddLine(baseC - perp * half);
        pb.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Closed);
        using var geo = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(pb);
        ds.FillGeometry(geo, color);
    }

    /// <summary>只绘制【指针最接近】的那一个手柄：靠近某条边中点 → 连线圆点；靠近某个角 → 缩放弧边。
    /// 其余位置一律不画，画面保持干净（对齐 Obsidian：选中不再全露 8 个点）。
    /// 全部在页局部空间比较与绘制，尺寸即页局部 PDF 点 → 随页面缩放，与卡片边框保持固定比例。
    /// 【手势进行中例外】：缩放/连线一旦开始，被操作的那一个控件【恒定显示】且与指针位置解耦
    /// （指针移开不消失、移到别处也不会点亮其他控件）。</summary>
    private void DrawNearestCanvasHandle(CanvasDrawingSession ds, CanvasNode node)
    {
        // 拖角缩放中：定格在开始时的那个角控件（单轴边缩放不画中点控件，卡片实时缩放即反馈）。
        if (_isCanvasResizing)
        {
            if (_canvasResizeMode != "edge" && _canvasResizeCorner.Length > 0)
                DrawCanvasCornerControl(ds, node, _canvasResizeCorner);
            return;
        }
        // 拖边连线中：定格在开始时的那个边中点圆点。
        if (_isCanvasConnecting)
        {
            if (_connFromSide.Length > 0) DrawCanvasSideHandle(ds, node, _connFromSide);
            return;
        }

        if (!_canvasPointerValid) return;
        if (!CanvasViewportToLockedPage(node.GlancePage, _canvasPointerPos, out double ppx, out double ppy))
            return;

        // 四边中点连线控件：落屏显形半径 = clamp(页局部8pt × 缩放, Min, Max)（中缩放随动、极缩不消失、极放不撑爆）；
        // 四角缩放控件：维持原页局部半径（用户要求不改）。各自独立取最近且落入自身半径者；优先级仍角 > 中点。
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

    /// <summary>四边中点手柄（连线起点）：强调色实心圆点 + 浅色描边。
    /// 落屏尺寸 = clamp(页局部半径 × 缩放, Min, Max)：中缩放随内容同比例、缩太小不消失、放太大不撑爆
    /// （Obsidian 式：先随动、再回弹到合适）。绘制空间是页局部，故页局部半径 = 落屏半径 / 缩放；描边按半径成比例。</summary>
    private void DrawCanvasSideHandle(CanvasDrawingSession ds, CanvasNode node, string side)
    {
        var a = SideAnchor(node, side);
        float screenR = Math.Clamp(CanvasHandleRadius * (float)CurrentZoom,
            CanvasConnHandleRadiusMinDip, CanvasConnHandleRadiusMaxDip);
        float r = screenR / (float)CurrentZoom;   // 页局部半径（绘制用）
        float stroke = r * 0.18f;                  // 描边按半径成比例（落屏恒定占圆点 ~18%）
        ds.FillCircle(a, r, _canvasAccentColor);
        ds.DrawCircle(a, r, CanvasHandleFill, stroke);
    }

    /// <summary>四角缩放控件：贴合卡片圆角、同心的【弧形边】（半径 = 卡片圆角 + 外扩量，覆盖该角 90° 象限），
    /// 线宽与选中边框一致 → 与"四边中点圆点"形成形状区分。
    /// 弧用【自算点的折线逼近】而非 CanvasPathBuilder.AddArc：后者角度单位在 Win2D 上与传入的度数不符
    /// （曾使 90° 的 sweep 绕成整圈圆环），自算点则与任何角度约定无关、结果确定。</summary>
    private void DrawCanvasCornerControl(CanvasDrawingSession ds, CanvasNode node, string corner)
    {
        double w = Math.Max(12, node.Width);
        double h = Math.Max(12, node.Height);
        float cr = CanvasCardRadius;                 // 卡片圆角半径（页局部）
        float arcR = cr + CanvasCornerGap;           // 弧半径：与卡片圆角同心、贴角外侧

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

    /// <summary>绘制连线拖拽的橡皮筋与目标吸附提示（屏幕坐标，会话变换须为恒等）。</summary>
    private void DrawCanvasConnecting(CanvasDrawingSession ds)
    {
        var a1 = _connFromAnchorScreen;
        Vector2 a2;
        Vector2 endNormal;
        bool hasTarget = _connTargetNodeId != null && _connTargetSide.Length > 0;
        if (hasTarget)
        {
            a2 = _connTargetAnchorScreen;
            endNormal = _connTargetNormalScreen;
        }
        else
        {
            a2 = new Vector2((float)_connCursorScreen.X, (float)_connCursorScreen.Y);
            var d = a2 - a1;
            float dl = d.Length();
            endNormal = dl > 1e-3f ? d / dl : new Vector2(1, 0);
        }

        float dist = Vector2.Distance(a1, a2);
        float k = Math.Clamp(dist * 0.4f, 24f, 120f);
        var c1 = a1 + _connFromNormalScreen * k;
        var c2 = hasTarget ? a2 + endNormal * k : a2 - endNormal * k;

        if (hasTarget)
        {
            // 箭头高随缩放（页局部点 × z），与已存连线一致；连线收缩到箭头底边，尖端落在卡片边框。
            // 线端向顶点再延伸一小段与三角重叠：消除平头线帽与三角底边之间的抗锯齿接缝（与 DrawCanvasEdges 同法）；
            // 预览线很细、重叠段完全藏在实心三角之下，无露角副作用。箭头底边仍用 arrowBase（尖端压边框）。
            float arrowLen = MathF.Max(CanvasArrowMinLen, CanvasArrowLen * (float)CurrentZoom);
            var d = a2 - c2;
            Vector2 arrowBase = d.Length() > 1e-3f ? a2 - (d / d.Length()) * arrowLen : a2;
            float seamOv = MathF.Max(1.5f, 0.15f * arrowLen);
            Vector2 lineEnd = arrowBase;
            if (d.Length() > 1e-3f) lineEnd = arrowBase + (d / d.Length()) * seamOv;
            using (var geo = BuildBezier(a1, c1, c2, lineEnd))
            {
                if (geo != null) ds.DrawGeometry(geo, _canvasAccentColor, MathF.Max(CanvasEdgeMinWidth, CanvasEdgeStrokeWidth * (float)CurrentZoom));
            }
            DrawArrowHead(ds, arrowBase, a2, _canvasAccentColor);
            // 目标吸附边的提示环，明确"将连到哪条边"。
            ds.DrawCircle(a2, 7f, _canvasAccentColor, 2f);
        }
        else
        {
            using (var geo = BuildBezier(a1, c1, c2, a2))
            {
                if (geo != null) ds.DrawGeometry(geo, _canvasAccentColor, MathF.Max(CanvasEdgeMinWidth, CanvasEdgeStrokeWidth * (float)CurrentZoom));
            }
            ds.FillCircle(a2, 3.5f, _canvasAccentColor);
        }
    }

    // =====================================================================
    // 白板交互（Canvas 模式，P2）：命中测试 / 选中 / 拖动移动 / 双击新建 / 浮层控件
    //   · 命中与拖动全部在【页局部 PDF 点】空间完成，渲染侧与存储侧同源；
    //   · 拖动只算位置，clamp 到页内；模型与历史留在 MainPage，本视图只发事件。
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
        }
    }
    private bool _canvasInputActive;

    /// <summary>当前选中的卡片 id（null=未选中）。渲染侧重画选中框并同步浮层控件位置。</summary>
    private string? _selectedCanvasNodeId;

    /// <summary>指针悬停的卡片 id（null=无）。与选中无关：指针靠近卡片（含边缘手柄外扩区）时置位，
    /// 渲染侧据此把该卡边框染成强调色；指针移开即清空 → 平时画面保持干净。</summary>
    private string? _hoverCanvasNodeId;

    /// <summary>指针视口坐标（白板模式下随 PointerMoved 更新）：手柄"按接近度显形"依赖它。</summary>
    private Point _canvasPointerPos;
    private bool _canvasPointerValid;

    // ---- 连线选中（P6）：点击选中 + 悬停高亮 + 边工具栏 ----
    private string? _selectedCanvasEdgeId;   // 与 _selectedCanvasNodeId 互斥
    private string? _hoverCanvasEdgeId;      // 指针靠近但未选中

    private bool _isCanvasDragging;
    private CanvasNode? _canvasDragNode;
    private int _canvasDragPage = -1;
    private double _canvasDragStartPx, _canvasDragStartPy; // 按下点（页局部 PDF 点）
    private double _canvasDragOrigX, _canvasDragOrigY;     // 节点原始位置

    // ---- 连线拖拽（P3）：从选中卡片四边中点手柄拖出，落到另一张卡片建立连线 ----
    private bool _isCanvasConnecting;
    private string? _connFromNodeId;
    private string _connFromSide = "";
    private Vector2 _connFromAnchorScreen;    // 源手柄屏幕坐标（视口 DIP）
    private Vector2 _connFromNormalScreen;    // 源边外法向（屏幕）
    private Point _connCursorScreen;          // 当前指针屏幕坐标
    private string? _connTargetNodeId;        // 悬停命中的目标卡片
    private string _connTargetSide = "";      // 目标吸附边
    private Vector2 _connTargetAnchorScreen;  // 目标吸附点屏幕坐标
    private Vector2 _connTargetNormalScreen;  // 目标边外法向（屏幕）

    // ---- 卡片缩放（P4）：拖四角手柄改尺寸（对角固定）或拖边中段单轴改尺寸（对侧固定） ----
    private bool _isCanvasResizing;
    private CanvasNode? _canvasResizeNode;
    private int _canvasResizePage = -1;
    private string _canvasResizeMode = "corner";   // "corner" = 四角对角缩放；"edge" = 单边单轴缩放
    private string _canvasResizeCorner = "";        // 角模式：tl/tr/bl/br
    private string _canvasResizeEdge = "";          // 边模式：top/bottom/left/right
    private double _canvasResizeOrigX, _canvasResizeOrigY, _canvasResizeOrigW, _canvasResizeOrigH;

    // 画布手势光标用的系统标准光标 IDC_*（与 CursorInterop.OverrideSystemCursorTo 配套）。
    private const int CanvasIdcArrow = 32512;       // IDC_ARROW
    private const int CanvasIdcSizeNwSe = 32642;    // IDC_SIZENWSE
    private const int CanvasIdcSizeNeSw = 32643;    // IDC_SIZENESW
    private const int CanvasIdcSizeNs = 32645;      // IDC_SIZENS
    private const int CanvasIdcSizeWe = 32644;      // IDC_SIZEWE
    // 编辑态"卡内=工字"作用域轮询间隔（对齐画笔圆环的 25ms _ringScopeTimer）。
    private const int IbeamScopeIntervalMs = 25;

    private string? _canvasEditNodeId;                     // 正在文本编辑的卡片 id
    // 编辑态辅助：编辑卡引用（滚动条/留白复用）+ TextBox 内部 ScrollViewer（取滚动偏移）+ 本次页变换（摆 XAML 滚动条）。
    private CanvasNode? _canvasEditNodeRef;
    private PageXform _canvasEditPageXf;
    private ScrollViewer? _canvasEditScrollViewer;

    // 每张卡片的滚动偏移（页局部 PDF 点，0 = 顶部）。**仅内存态**（与 Obsidian 一致，不写进 .canvas）。
    // 非编辑态卡片由 Win2D 绘制、没有内建滚动，必须靠这个值把文字整体上移来表现滚动。
    private readonly Dictionary<string, double> _canvasScrollY = new(StringComparer.Ordinal);

    /// <summary>取卡片滚动偏移（页局部点，非负）。</summary>
    private double GetCanvasScrollY(string nodeId)
        => _canvasScrollY.TryGetValue(nodeId, out double v) && v > 0 ? v : 0;

    /// <summary>设卡片滚动偏移（clamp 到 [0, maxScroll]）。返回是否真的变化。</summary>
    private bool SetCanvasScrollY(string nodeId, double value, double maxScroll)
    {
        double v = Math.Clamp(value, 0, Math.Max(0, maxScroll));
        double old = GetCanvasScrollY(nodeId);
        if (Math.Abs(v - old) < 0.01) return false;
        if (v <= 0) _canvasScrollY.Remove(nodeId);
        else _canvasScrollY[nodeId] = v;
        return true;
    }

    // ---- 滚动条拖动 ----
    private bool _isCanvasScrollDragging;
    private string? _canvasScrollDragNodeId;
    private double _canvasScrollDragStartPointerY;   // 按下时的页局部 Y
    private double _canvasScrollDragStartScrollY;    // 按下时的滚动偏移

    // ---- Obsidian 对齐：单击【已选中卡片】松手无拖动 → 进入编辑态（光标落点击点） ----
    // 按下已选中卡本体时挂起此标记；PointerMoved 超过阈值判定为拖动 → 撤销；PointerReleased 仍挂起 → 进编辑。
    // 双击路径：DoubleTapped 先行进入编辑，释放侧以 _canvasEditNodeId != null 守卫，不会二次进入。
    private bool _canvasClickEditPending;
    private Point _canvasClickEditStartVp;
    private const double CanvasClickEditMoveThreshold = 4.0;   // 视口 DIP：超过即视为拖动而非点击

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
    // MainPage 的顶层浮层盖在本视图【之上】并吞掉指针：顶栏宿主（透明底、整条顶边，约一条工具栏高）、
    // 底部缩放胶囊（右下角）、对比视图的 +/× 圆钮（上左右角）。被钳制到这些带里的悬浮胶囊会
    // "看得见点不到"（用户报告：钳到视口上边缘时 100% 按钮失效）⇒ 钳制必须避开它们。
    // 由 MainPage 把"四边被占掉多少 DIP"推下来（见 SetCanvasViewportInsets）。
    private double _canvasInsetL, _canvasInsetT, _canvasInsetR, _canvasInsetB;
    // 连线胶囊的淡入淡出去重（与 _cardToolbarShown 同源）：仅 Visible↔Collapsed 跳变才起停动画。
    private bool _edgeToolbarShown;
    // 连线胶囊内两个二级面板：默认折叠、互斥（同一时刻只开一个）；展开态真实宽度用于右缘钳制。
    private bool _canvasEdgeArrowPickerShown;
    private bool _canvasEdgeColorPickerShown;
    private double _canvasEdgeToolbarExpandedW;
    private Button? _canvasEdgeColorSelectedDot;   // 当前连线已应用颜色对应的圆点（展开时放大）
    // 箭头符号描边是否已挂 RasterizationScale 变化（Loaded 后只挂一次；跨不同 DPI 显示器时重算描边宽）。
    private bool _canvasEdgeGlyphScaleHooked;
    // 二级颜色胶囊（内联展开到卡片胶囊内）：是否展开（展开时隐藏颜色按钮、显示 7 色圆点、胶囊变宽）。
    private bool _canvasColorPickerShown;
    // 二级颜色胶囊圆点尺寸（与 PenOptionsCapsule 同源：悬浮/选中放大 1.6×，负 Margin 抵消布局位移）。
    private const double CanvasColorDotBaseSize = 16.0;
    private const double CanvasColorDotEnlargedSize = 16.0 * 1.6;
    private const double CanvasColorDotBaseRadius = CanvasColorDotBaseSize / 2.0;
    private const double CanvasColorDotEnlargedRadius = CanvasColorDotEnlargedSize / 2.0;
    private const double CanvasColorDotEnlargedInset = (CanvasColorDotEnlargedSize - CanvasColorDotBaseSize) / 2.0;
    // 卡片边框默认灰（DrawCanvasCard 的 CanvasCardStroke）：二级胶囊首位圆点的颜色。
    private const string CanvasCardDefaultGrayHex = "#8A8F98";
    private Button? _canvasColorSelectedDot;   // 当前卡片已应用颜色对应的圆点（展开时放大）

    /// <summary>卡片拖动中（携带 clamp 后的新位置，MainPage 直接写回模型）。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeDragged;
    /// <summary>卡片拖动结束（携带最终位置与拖动前原始位置，供 MainPage 组装撤销记录）。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeDragCompleted;
    /// <summary>双击空白请求新建卡片（携带已 clamp 到页内的左上角坐标）。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeCreateRequested;
    /// <summary>请求删除卡片（浮层控件"删除"）。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeDeleteRequested;
    /// <summary>文本编辑提交（携带新文本）。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeTextCommitted;
    /// <summary>连线建立请求：从 NodeId 的 FromSide 手柄拖到目标卡片 TargetNodeId（ToSide 为吸附边）。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeConnectionRequested;
    /// <summary>卡片缩放中（携带 clamp 后的新位置与尺寸，MainPage 直接写回模型）。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeResized;
    /// <summary>卡片缩放结束（携带最终矩形与缩放前矩形，供 MainPage 组装撤销记录）。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeResizeCompleted;
    /// <summary>请求删除连线（边工具栏"删除"或 Delete 键）。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasEdgeDeleteRequested;
    /// <summary>连线样式变更（箭头 ArrowStyle / 颜色 EdgeColor，仅填充变更项）。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasEdgeStyleChanged;
    /// <summary>卡片颜色变更（二级颜色胶囊：NodeId + NodeColor）。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeColorChanged;
    /// <summary>从连线手柄拖到空白处：请求在该处新建卡片并与源节点连成一条线
    /// （NodeId=源节点、FromSide=源出边、PageIndex/X/Y=落点页与已 clamp 的左上角）。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeCreateConnectedRequested;
    /// <summary>选中卡片 = 请求提升到叠放顶层（jsoncanvas 无独立 z 字段，nodes 数组顺序即 z 序）。
    /// MainPage 收到后重排模型数组并落盘 ⇒ 取消选中后层级保持，不再只是绘制期临时置顶。</summary>
    public event EventHandler<CanvasNodeEventArgs>? CanvasNodeBringToFrontRequested;
    /// <summary>编辑框内请求撤销（Ctrl+Z）：MainPage 统一走撤销栈，避免编辑框原生撤销与全局历史脱节。</summary>
    public event EventHandler? CanvasUndoRequested;
    /// <summary>编辑框内请求重做（Ctrl+Y）。</summary>
    public event EventHandler? CanvasRedoRequested;

    /// <summary>选中/取消选中卡片（null=取消）。命中测试只认页内坐标，跨页卡片各自独立。
    /// 与连线选中互斥：选中卡片即清掉连线选中。</summary>
    public void SelectCanvasNode(string? nodeId)
    {
        string? id = string.IsNullOrEmpty(nodeId) ? null : nodeId;
        if (_selectedCanvasNodeId == id && _selectedCanvasEdgeId == null) { SyncCanvasOverlayNow(); return; }
        // 选中目标变了（含取消选中）→ 收起二级颜色胶囊，避免它停在原卡片的旧位置。
        if (id != _selectedCanvasNodeId) HideCanvasColorPicker();
        _selectedCanvasNodeId = id;
        _selectedCanvasEdgeId = null;   // 与连线选中互斥
        HideCanvasEdgePickers();       // 选中卡片即收起连线胶囊的二级面板（胶囊本身随后由定位链路隐藏）
        // 选中即置顶，且必须是【永久】的：这里只发请求，真正的重排与落盘由 MainPage 改模型数组完成
        // （jsoncanvas 的 z 序 = nodes 数组顺序）。否则取消选中后 topId 失效，卡片会掉回出生层级。
        if (id != null)
            CanvasNodeBringToFrontRequested?.Invoke(this, new CanvasNodeEventArgs { NodeId = id });
        InvalidateOverlay();
        SyncCanvasOverlayNow();
    }

    // ---- 连线选中（P6）----

    /// <summary>当前是否有选中的连线（供 MainPage 路由 Delete 键）。</summary>
    public bool HasSelectedCanvasEdge => _selectedCanvasEdgeId != null;

    /// <summary>当前是否有选中的卡片（供 MainPage 路由 Delete 键）。</summary>
    public bool HasSelectedCanvasNode => _selectedCanvasNodeId != null;

    /// <summary>是否正在文本编辑（编辑框聚焦时 Delete 键应交给文本框，不当成删除卡片/连线）。</summary>
    public bool IsCanvasEditing => _canvasEditNodeId != null;

    /// <summary>选中/取消选中连线（null=取消）。与卡片选中互斥。</summary>
    public void SelectCanvasEdge(string? edgeId)
    {
        string? id = string.IsNullOrEmpty(edgeId) ? null : edgeId;
        if (_selectedCanvasEdgeId == id && _selectedCanvasNodeId == null) { SyncCanvasOverlayNow(); return; }
        // 换一条线（或取消选中）：上一个二级面板立即收起 —— 否则"上一条线展开着的颜色面板"会带过来。
        HideCanvasEdgePickers();
        _selectedCanvasEdgeId = id;
        _selectedCanvasNodeId = null;   // 与卡片选中互斥
        InvalidateOverlay();
        SyncCanvasOverlayNow();
    }

    /// <summary>清空连线选中（切换模式/隐藏白板层时调用）。</summary>
    public void ClearCanvasEdgeSelection()
    {
        if (_selectedCanvasEdgeId == null && _hoverCanvasEdgeId == null) return;
        _selectedCanvasEdgeId = null;
        _hoverCanvasEdgeId = null;
        HideCanvasEdgePickers();
        InvalidateOverlay();
    }

    /// <summary>删除当前选中的连线（边工具栏"删除"按钮 / Delete 键）。返回是否真的发出删除请求。</summary>
    public bool DeleteSelectedCanvasEdge()
    {
        string? id = _selectedCanvasEdgeId;
        if (string.IsNullOrEmpty(id)) return false;
        _selectedCanvasEdgeId = null;
        _hoverCanvasEdgeId = null;
        if (CanvasEdgeToolbar != null) CanvasEdgeToolbar.Visibility = Visibility.Collapsed;
        if (CanvasOverlay != null) CanvasOverlay.Visibility = Visibility.Collapsed;
        CanvasEdgeDeleteRequested?.Invoke(this, new CanvasNodeEventArgs { EdgeId = id });
        InvalidateOverlay();
        return true;
    }

    /// <summary>删除当前选中的卡片（Delete 键复用卡片工具栏删除路径）。返回是否发出了删除请求。</summary>
    public bool DeleteSelectedCanvasNode()
    {
        string? id = _selectedCanvasNodeId;
        if (string.IsNullOrEmpty(id)) return false;
        _selectedCanvasNodeId = null;
        HideCanvasCardToolbar();
        if (CanvasOverlay != null) CanvasOverlay.Visibility = Visibility.Collapsed;
        CanvasNodeDeleteRequested?.Invoke(this, new CanvasNodeEventArgs { NodeId = id });
        InvalidateOverlay();
        return true;
    }

    /// <summary>进入文本编辑：把【不可见】编辑框覆盖到卡片屏幕上并聚焦。可见文字/光标/选区/滚动条全部由 Win2D 绘制。</summary>
    public void BeginCanvasEdit(string nodeId, Point? clickVp = null)
    {
        CanvasNode? node;
        lock (_canvasLock) _canvasIndex.TryGetValue(nodeId, out node);
        if (node == null) return;
        _canvasEditNodeId = nodeId;
        // 双击补救基线：带落点时记下落点与时刻；不带落点（工具栏「编辑」/新建卡片）清零。
        _canvasEditEntryVp = clickVp;
        _canvasEditEntryTick = clickVp is null ? 0 : Stopwatch.GetTimestamp();
        // ⚠ 时序：_canvasEditNodeRef 必须在 SetCanvasEditText 之前赋值 —— SetCanvasEditText 内部会调
        //    EnsureCanvasEditLineSpacing，而那要用 _canvasEditNodeRef 去取"阅读态那份 layout 的实测行高"
        //    （仅用于把编辑框行距锁到 Win2D 同值，使"点击落光标"的命中映射与 Win2D 渲染对齐；不影响可见渲染）。
        _canvasEditNodeRef = node;
        SelectCanvasNode(nodeId);       // 进入编辑即置为选中 ⇒ 退出编辑后自然回到【选中态】（与 Obsidian 一致）
        // 记录"已入史的文本基线"：打字过程中按停顿分段提交，未变的段不再产生历史条目。
        _canvasLastCommittedText = node.Text ?? "";
        StopCanvasEditCommitTimer();
        int editDocEnd = SetCanvasEditText(node.Text);                 // 写入文档（内部顺带锁定行距）
        CanvasEditBox.LayoutUpdated -= OnCanvasEditBoxLayoutUpdated;   // 防重复订阅
        CanvasEditBox.LayoutUpdated += OnCanvasEditBoxLayoutUpdated;   // 每帧兜底：取内部 ScrollViewer + 刷新画面
        // 指针移动处理器已在构造时以 handledEventsToo 常驻订阅（见构造函数）——普通 `+=` 会被 RichEditBox
        // 的 Handled 吞掉，故不再在此处订阅。
        CanvasEditBox.IsHitTestVisible = true;   // 每次进编辑都从"整卡可命中"起（清除上一次被挖除的命中带状态）
        PositionCanvasEditBox();
        // 显式把编辑框根光标固定为系统工字：控件（滚动条/四角/四边）悬停时由 OnCanvasEditBoxPointerMoved
        // 的 OverrideCursor 覆盖为对应光标。资源级的"卡内恒工字"由工字作用域轮询（EnterIbeamScope）负责，
        // 二者叠加：元素级管卡内元素的即时求值，资源级兜住框架按下/捕获期间按箭头槽的求值。
        CanvasEditBox.OverrideCursor(CanvasIBeamCursor);
        _canvasSelLastStart = _canvasSelLastEnd = _canvasSelLastActive = -1;   // 选区活动端基线随会话重置
        // 实时渲染（C）的会话态：展开行 / 连击识别 / 拖选锚点 / 可见串缓存，每次进编辑都从零开始。
        _canvasEditExpandActive = false;
        _canvasEditExpandLineStart = -1;
        _canvasEditClickTick = 0;
        _canvasEditDragAnchor = _canvasEditDragLast = _canvasEditPendingCaret = -1;
        _canvasEditLive = null;
        _canvasEditTextLayoutKey = -1;
        StartCaretBlink();
        InvalidateOverlay();   // 编辑卡本体由 Win2D 绘制（与阅读/选中态同源）
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
            // 指针此刻是否在编辑卡上 —— 按【实际坐标】落在卡片屏幕矩形内判定，不再"有落点就假定在卡内"：
            //  · 带落点（双击卡内 / 单击已选中卡）：落点即指针位置；
            //  · 不带落点（工具栏「编辑」按钮 / MainPage 新建卡片后进编辑）：用实时指针位置镜像
            //    `_canvasPointerPos` —— 它由【根级】PointerMoved（构造函数 2745 行 `this.PointerMoved +=`）维护，
            //    工具栏按钮/编辑框上的移动会一路冒泡到根 ⇒ 指针不动时它就是真值，无需 GetCursorPos 轮询。
            // 两侧都要判的实证：① 工具栏按钮在卡外，装工字会闪一下（用户报告）；② 新建卡片以双击点为卡心、
            //    指针必在卡内，判真后当场装工字 ⇒ 不再依赖框架自己何时刷新元素光标（那正是"有时快有时慢"的来源）。
            // 用卡片【模型矩形】而不是编辑框几何：此刻刚 PositionCanvasEditBox，TransformToVisual 可能还是上一帧的。
            Point? entryPoint = clickVp;
            if (entryPoint == null && _canvasPointerValid) entryPoint = _canvasPointerPos;
            bool entryOnCard = entryPoint is { } ep &&
                TryGetCanvasNodeScreenRect(node.Id, out var entryRect) &&
                ep.X >= entryRect.X && ep.X <= entryRect.X + entryRect.Width &&
                ep.Y >= entryRect.Y && ep.Y <= entryRect.Y + entryRect.Height;
            // 启动"卡内=工字"光标作用域（对齐画笔）：轮询自愈，卡内（含按住/捕获/停下不动）恒工字，
            // 控件区/卡外自动恢复真箭头。放在 Focus 之后避开焦点求值竞态。
            EnterIbeamScope(entryOnCard);
            // 强制刷一次工字：只对"指针确实在卡内"的入口做。工具栏按钮在卡外，刷了就会闪一下再被轮询改回来（用户报告）。
            if (entryOnCard)
            { try { CursorInterop.SetCursor(CursorInterop.LoadSystemCursor(CursorInterop.IdcIbeam)); } catch { } }
        });
    }

    /// <summary>视口点击点 → 编辑框文档的光标偏移；-1 = 无法映射（落点在卡外/排版不可用，调用方回退文本末尾）。
    /// ⚠ 必须用【阅读态】layout 命中，再换算回源串（原文）下标：
    /// 双击那一刻用户看到的是【渲染后】的文字（Markdown 标记已剔除），而编辑态显示的是带标记的原文；
    /// 若直接拿编辑态 layout 做命中，同一个屏幕点会落到另一段文字上 —— 表现为"光标不在双击的那几个字上"。
    /// 几何口径与 DrawCanvasCard 的阅读态完全一致：内容坐标 = 点击点 − (node.X+PadX, node.Y+PadY+topPad−scrollY)，
    /// topPad / scrollY 都按阅读态公式（真实行数 × 标称行高，滚动量取卡片自身的滚动偏移并夹到可滚动范围）。
    /// 命中的是【显示串】下标，经 SourceIndexMap 换成源串下标；RichEdit 文档把 \n 换成 \r 且长度不变
    /// （SetCanvasEditText）⇒ 源串下标可直接当文档偏移。文字区域外对齐系统文本框语义：
    /// 上方 → 文本起点；下方 → 文本末尾；左右侧 → DWrite 最近字符沿（行首/行尾）。</summary>
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
        var layout = GetCanvasTextLayout(node, tw, th);   // 顺带刷新缓存（显示串 / 样式表 / 下标映射）
        if (layout == null) return -1;

        // 阅读态留白与滚动（与 DrawCanvasCard 阅读分支同一公式，改动须同步）
        double realContentH = GetCachedCanvasLineCount(node, tw, th) * CanvasLineHeight;
        double topPad = Math.Min(0.5 * CanvasLineHeight, Math.Max(0, (th - Math.Min(th, realContentH)) / 2.0));
        double maxScroll = Math.Max(0, realContentH - th);
        double scrollY = Math.Clamp(GetCanvasScrollY(node.Id), 0, maxScroll);

        float lx = (float)(px - (node.X + CanvasCardPadX));
        float ly = (float)(py - (node.Y + CanvasCardPadY + topPad - scrollY));
        try
        {
            if (ly < 0) return 0;   // 文字上方 → 起点（源串 0：光标落在标记之前）
            float textBottom = 0;
            var lms = layout.LineMetrics;
            if (lms != null) foreach (var lm in lms) textBottom += lm.Height;
            if (ly >= textBottom) return Math.Clamp(node.Text.Length, 0, _canvasEditText.Length);   // 文字下方 → 末尾
            // 界外（行首左/行尾右）也返回最近字符簇：region.CharacterIndex + trailingSide 即光标位。
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

    /// <summary>显示串下标 → 源串下标（无 Markdown 标记、未剔除任何字符时两者 1:1）。</summary>
    private int MapCanvasDisplayIndexToSource(CanvasNode node, int displayIndex)
    {
        if (displayIndex <= 0) return 0;
        if (!_canvasTextCache.TryGetValue(node, out var cache) || cache.SourceIndexMap == null)
            return displayIndex;
        var map = cache.SourceIndexMap;
        return displayIndex >= map.Count ? map[map.Count - 1] : map[displayIndex];
    }

    /// <summary>【编辑态】视口点击点 → 源串（文档）偏移；-1 = 取不到（卡外 / 排版未就绪）。
    /// 与 <see cref="MapCanvasClickToEditOffset"/> 的区别：那个用于"从阅读态点进来"（按阅读 layout 命中），
    /// 本方法用于**编辑态内部**的点击/拖选（按编辑 layout 命中）。
    /// 为什么必须自己算：可见串已按光标隐藏了部分标记 ⇒ 与原文档排版不同宽，
    /// 让 RichEditBox 用自己的排版去做命中，光标会落在错字上。几何口径与 DrawCanvasCard 编辑分支完全一致
    /// （topPad 用 ComputeCanvasEditTopPad，scrollY 取宿主 ScrollViewer 并夹到真实可滚动量）。</summary>
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
        try { layout = GetCanvasEditTextLayout(tw, th); }   // 顺带保证 _canvasEditLive 与可见串是最新的
        catch { return -1; }                                  // 设备丢失等：不接管，交回原生（宁可略偏也别崩）
        if (layout == null) return 0;
        double topPad = ComputeCanvasEditTopPad(tw, th);
        double scrollY = TryGetCanvasEditScrollMetrics(node, out _, out _, out double visMax)
            ? Math.Clamp(_canvasEditScrollViewer!.VerticalOffset, 0, visMax)
            : 0;

        float lx = (float)(px - (node.X + CanvasCardPadX));
        float ly = (float)(py - (node.Y + CanvasCardPadY + topPad - scrollY));
        try
        {
            if (ly < 0) return 0;   // 文字上方 → 起点
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

    /// <summary>把选区设成 [start,end]（源串偏移）。自绘光标的活动端推断基线一并清掉，下一帧直接按新选区推。</summary>
    private void SetCanvasEditSelection(int start, int end)
    {
        try
        {
            var sel = CanvasEditBox.Document.Selection;
            sel.SetRange(start, end);
            _canvasSelLastStart = _canvasSelLastEnd = _canvasSelLastActive = -1;
        }
        catch { }
        ResetCaretBlink();
        ScreenCanvas.Invalidate();
    }

    /// <summary>单击落光标：原生命中处理在类处理器里先跑（且按文档排版算）⇒ 排到队尾覆盖，才能赢。
    /// 与"双击补救"同一手法。</summary>
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

    /// <summary>这一下按下是不是"同一处的连击"（双击/三击）。是 → 不再接管光标（交给原生选词/选段），
    /// 只把该行标记全部展开。</summary>
    private bool IsCanvasEditClickRepeat(Point p)
    {
        long now = Stopwatch.GetTimestamp();
        double ms = _canvasEditClickTick == 0 ? double.MaxValue
            : (now - _canvasEditClickTick) * 1000.0 / Stopwatch.Frequency;
        bool repeat = ms <= CanvasDoubleClickWindowMs &&
            Math.Abs(p.X - _canvasEditClickVp.X) <= CanvasDoubleClickSlopDip &&
            Math.Abs(p.Y - _canvasEditClickVp.Y) <= CanvasDoubleClickSlopDip;
        _canvasEditClickTick = now;
        _canvasEditClickVp = p;
        return repeat;
    }

    /// <summary>退出（关闭）当前编辑；commit=true 时把文本提交给 MainPage。</summary>
    private void EndCanvasEdit(bool commit)
    {
        if (_canvasEditNodeId == null) return;
        if (commit) FlushCanvasEditText();   // 提交最后一段尚未入史的文字
        else StopCanvasEditCommitTimer();
        _canvasEditNodeId = null;
        _canvasEditControlGesture = false;
        _canvasEditMenuClickPending = false;
        _canvasEditEntryTick = 0;                                      // 双击补救基线失效
        _canvasEditEntryCaret = -1;
        _canvasEditMenuKeyTick = 0;
        _canvasEditLastPressWasRight = false;
        _canvasEditLastPressWasLeft = false;
        _canvasEditLastPressTick = 0;
        _canvasEditLastPressDevice = Microsoft.UI.Input.PointerDeviceType.Touch;   // 回默认值 = "未知来源"，与初值一致
        // 实时渲染（C）的会话态一并清掉：下次进编辑重新按当时的文本/光标算。
        _canvasEditExpandActive = false;
        _canvasEditExpandLineStart = -1;
        _canvasEditClickTick = 0;
        _canvasEditDragAnchor = _canvasEditDragLast = _canvasEditPendingCaret = -1;
        _canvasEditLive = null;
        _canvasEditTextLayoutKey = -1;
        StopCaretBlink();
        ExitIbeamScope();                                              // 退出编辑态：停工字作用域、恢复系统光标
        CanvasEditBox.OverrideCursor(null);                            // 复位光标覆盖（防残留箭头）
        this.ProtectedCursor = null;                                   // 同步清根光标，退出编辑后首帧不残留缩放箭头
        CanvasEditBox.LayoutUpdated -= OnCanvasEditBoxLayoutUpdated;   // 退出编辑即退订重试钩子
        CanvasEditBox.IsHitTestVisible = true;   // 复位命中区（可能停在"被挖除"态）⇒ 下次进编辑是干净的整卡可命中
        CanvasEditBox.Visibility = Visibility.Collapsed;
        // 折叠的 RichEditBox 仍会保持键盘焦点（WinUI 已知行为），会吞掉 F/G/Ctrl+Z 这类页面级按键：
        // 表现为白板模式下快捷键整体失效、字母键进入输入法。必须把焦点显式移回滚动宿主。
        RestoreViewerFocus();
        SyncCanvasOverlayNow();
        InvalidateOverlay();   // 编辑卡本体恢复由 Win2D 绘制
    }

    /// <summary>把键盘焦点从（已折叠的）编辑框移回视口宿主，恢复页面级快捷键。</summary>
    private void RestoreViewerFocus()
    {
        // 延迟到队列尾部：此刻仍在 KeyDown 分发过程中，立即转焦点可能被后续默认处理夺回。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_canvasEditNodeId != null) return; // 期间又进入编辑，不抢焦点
            // ⚠ 只有焦点【还滞留在】（已折叠的）编辑框上、或已经落空时才交还视口。
            // 用户已经点中别的控件（工具栏「设置」按钮 / 模式按钮 / 侧边栏…）时绝不抢焦点 ——
            // 否则会把刚被点开的 Flyout 顶掉，表现为"设置按钮第一次点没反应，第二次才展开"（用户报告）。
            object? focused = FocusManager.GetFocusedElement(XamlRoot);
            if (focused != null && !ReferenceEquals(focused, CanvasEditBox)) return;
            if (Viewer != null && Viewer.Focus(FocusState.Programmatic)) return;
            FocusManager.TryMoveFocus(FocusNavigationDirection.Next);
        });
    }

    /// <summary>把键盘焦点交给视口宿主（若尚未在它身上）。页面级按键里，Delete/Backspace 走的是
    /// MainPage 上依赖焦点的 KeyDown 路由，鼠标点击画布并不会让 ScrollViewer 自动获焦 ⇒ 需在画布左键
    /// 按下时显式补齐（否则首次打开带卡片的 PDF，选中卡片/连线按 Del 无效；F/G 等 KeyboardAccelerator
    /// 类按键与焦点无关，故不受影响，这也是问题只表现为 Del 失效的原因）。</summary>
    private void EnsureViewerFocus()
    {
        if (Viewer == null) return;
        if (ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), Viewer)) return;
        Viewer.Focus(FocusState.Programmatic);
    }

    /// <summary>G 键隐藏白板层时收起全部白板交互态与浮层（见 AnnotationsVisible setter）。</summary>
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
        _connFromNodeId = null;
        _connTargetNodeId = null;
        _connTargetSide = "";
        _isCanvasScrollDragging = false;
        _canvasScrollDragNodeId = null;
        CancelCanvasResize();
        if (CanvasOverlay != null) CanvasOverlay.Visibility = Visibility.Collapsed;
        HideCanvasCardToolbar();
        if (CanvasEdgeToolbar != null) CanvasEdgeToolbar.Visibility = Visibility.Collapsed;
        if (CanvasEditBox != null) CanvasEditBox.Visibility = Visibility.Collapsed;
    }

    /// <summary>退出白板交互（切回阅读/画笔模式）：清拖动/选中/编辑并隐藏浮层。</summary>
    private void ExitCanvasInteraction()
    {
        _isCanvasDragging = false;
        _canvasDragNode = null;
        _canvasDragPage = -1;
        _isCanvasConnecting = false;
        _connFromNodeId = null;
        _connTargetNodeId = null;
        _connTargetSide = "";
        _isCanvasScrollDragging = false;
        _canvasScrollDragNodeId = null;
        CancelCanvasResize();
        // 编辑态必须走 EndCanvasEdit 正式收尾：它会提交最后一段尚未入史的文字、收起文本菜单，
        // 并把焦点从（折叠后仍持焦的）编辑框交还视口。
        // ⚠ 此前是直接 `_canvasEditNodeId = null` —— 编辑框折叠了但**焦点还留在它身上**，切到阅读/画笔模式后
        //   键盘输入全被这个看不见的控件吃掉：字母键变成往卡片里打字，F/G 之类快捷键失灵（用户报告）。
        if (_canvasEditNodeId != null)
        {
            EndCanvasEdit(commit: true);
        }
        else
        {
            ExitIbeamScope();   // 退出白板模式即退出编辑态：停工字作用域、恢复系统光标
            CanvasEditBox.LayoutUpdated -= OnCanvasEditBoxLayoutUpdated;   // 直接清 id 的路径也要退订（否则钩子残留）
        }
        _selectedCanvasNodeId = null;
        _selectedCanvasEdgeId = null;
        _hoverCanvasNodeId = null;
        _hoverCanvasEdgeId = null;
        _canvasPointerValid = false;
        if (CanvasOverlay != null) CanvasOverlay.Visibility = Visibility.Collapsed;
        if (CanvasEditBox != null) CanvasEditBox.Visibility = Visibility.Collapsed;
        HideCanvasCardToolbar();
        if (CanvasEdgeToolbar != null) CanvasEdgeToolbar.Visibility = Visibility.Collapsed;
        InvalidateOverlay();
    }

    /// <summary>视口坐标 → 命中卡片（页内坐标测试，取绘制顺序最上层者）+ 该页局部坐标。
    /// 命中顺序必须与绘制顺序一致：选中的卡片被置顶绘制（见 DrawWhiteboardLayer），故先判它，
    /// 否则点在与下层卡片重叠的区域会选到被盖住的那张。</summary>
    private bool TryHitCanvasNode(Point vp, out CanvasNode? node, out int pageIndex,
        out double px, out double py)
    {
        node = null;
        // 白板层被 G 键隐藏时不做任何命中：卡片看不见就不该能选中/拖动。
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

    // ---- 连线（P3）：手柄命中 / 吸附 / 橡皮筋拖拽 ----

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

    /// <summary>视口坐标 → 命中选中卡片的连线手柄。命中则输出卡片 id 与边名。</summary>
    private bool TryHitCanvasHandle(Point vp, out string nodeId, out string side)
    {
        nodeId = ""; side = "";
        if (!_canvasInputActive || !_annotationsVisible) return false;
        // 手柄可及的目标卡：优先【指针悬停的卡】（Obsidian 式"悬停边缘即可直接拖"，无需先选中），
        // 其次回退到选中卡。命中在【页局部空间】比较 —— 与绘制同源，且随页面缩放。
        string? target = _hoverCanvasNodeId ?? _selectedCanvasNodeId;
        if (target == null) return false;
        CanvasNode? node = null;
        lock (_canvasLock) _canvasIndex.TryGetValue(target, out node);
        if (node == null || node.GlancePage < 0) return false;
        if (!CanvasViewportToLockedPage(node.GlancePage, vp, out double px, out double py)) return false;
        // 连线控件命中半径：落屏 = clamp(页局部8pt × 缩放, Min, Max)（中缩放随动、极缩不消失、极放不撑爆）。
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

    /// <summary>目标卡片中【离指针最近】的边中点 → 吸附边（屏幕距离比较）。
    /// 不用"卡片中心相对方位"决定：那样用户在卡片任意位置悬停都只吸到同一条边，
    /// 无法自由选边；按指针到四个中点手柄的屏幕距离取最小，手指在哪就吸哪条边，直观可控。</summary>
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

    /// <summary>开始从某卡片某边手柄拖连线：记录源锚点/法向并进入橡皮筋状态。</summary>
    private void BeginCanvasConnection(string nodeId, string side, Point vp)
    {
        _isCanvasConnecting = true;
        HideCanvasCardToolbar();   // 拉线开始：立即暂隐卡片悬浮胶囊，防止挡住连线过程
        LockCanvasCursor(CanvasIdcArrow, _canvasEditNodeId != null);   // 连线拖拽：锁定箭头光标（编辑态保留系统工字，含指针停下）
        _connFromNodeId = nodeId;
        _connFromSide = side;
        _connTargetNodeId = null;
        _connTargetSide = "";
        _connCursorScreen = vp;

        CanvasNode? node = null;
        lock (_canvasLock) _canvasIndex.TryGetValue(nodeId, out node);
        if (node != null && node.GlancePage >= 0 && node.GlancePage < _pages.Count)
        {
            GetViewState(out double z, out double hOff, out double vOff);
            var xf = MakePageXform(ComputePageDest(node.GlancePage, z, hOff, vOff), z);
            var a = SideAnchor(node, side);
            _connFromAnchorScreen = xf.Map(a.X, a.Y);
            var nrm = SideNormal(side);
            _connFromNormalScreen = xf.MapDirection(nrm.X, nrm.Y);
        }
        UpdateCanvasConnecting(vp);
        InvalidateOverlayThrottled();
    }

    /// <summary>连线拖拽中：更新指针位置与目标吸附（命中非源卡片时高亮其吸附边）。</summary>
    private void UpdateCanvasConnecting(Point vp)
    {
        _connCursorScreen = vp;
        _connTargetNodeId = null;
        _connTargetSide = "";
        var pages = _pages;
        if (TryHitCanvasNode(vp, out var hit, out _, out _, out _) && hit != null &&
            !string.Equals(hit.Id, _connFromNodeId, StringComparison.Ordinal))
        {
            _connTargetNodeId = hit.Id;
            _connTargetSide = ComputeTargetSide(hit.Id, vp);
            if (TryGetCanvasHandleScreen(hit.Id, _connTargetSide, out var anchor))
                _connTargetAnchorScreen = anchor;
            if (hit.GlancePage >= 0 && hit.GlancePage < pages.Count)
            {
                GetViewState(out double z, out double hOff, out double vOff);
                var xf = MakePageXform(ComputePageDest(hit.GlancePage, z, hOff, vOff), z);
                var nrm = SideNormal(_connTargetSide);
                _connTargetNormalScreen = xf.MapDirection(nrm.X, nrm.Y);
            }
        }
        InvalidateOverlayThrottled();
    }

    /// <summary>取消进行中的连线拖拽（Esc / 模式切换）。返回是否有待取消的连线。</summary>
    public bool CancelCanvasConnection()
    {
        if (!_isCanvasConnecting) return false;
        _isCanvasConnecting = false;
        SyncCanvasOverlayNow();   // 取消连线 ⇒ 恢复卡片悬浮胶囊（若仍有卡片选中）
        _connFromNodeId = null;
        _connTargetNodeId = null;
        _connTargetSide = "";
        UnlockCanvasCursor();   // 连线结束 ⇒ 解锁光标（恢复框架默认）
        InvalidateOverlay();
        return true;
    }

    // ---- 卡片缩放（P4） ----

    /// <summary>视口坐标 → 命中选中卡片的四角手柄。</summary>
    private bool TryHitCanvasCorner(Point vp, out string nodeId, out string corner)
    {
        nodeId = ""; corner = "";
        if (!_canvasInputActive || !_annotationsVisible) return false;
        // 与连线手柄同规则：优先指针悬停的卡（悬停四角即可直接缩放），其次回退选中卡；
        // 同样在【页局部空间】比较命中。
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

    /// <summary>视口坐标 → 命中选中卡片某条边【中段】（单轴缩放区）。
    /// 区域 = 卡片四条边线段上"垂直距离 ≤ 命中半径"，且落在【四角圆域】与【四边中点圆域】之间
    /// （即"边中点与四角之间的带状区"）。调用方按 角 &gt; 中点 &gt; 边 的顺序判定，故此处无需排除两端。</summary>
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
        // 上/下边为水平线段（y 固定），左/右边为竖直线段（x 固定）；clamp 投影到 [0,len] 保证落在线段内。
        // 上
        {
            double cx = Math.Clamp(px, l, rr), cy = t;
            double dx = px - cx, dy = py - cy, d2 = dx * dx + dy * dy;
            if (d2 <= r2 && d2 < bestD2) { bestD2 = d2; bestE = "top"; }
        }
        // 下
        {
            double cx = Math.Clamp(px, l, rr), cy = b;
            double dx = px - cx, dy = py - cy, d2 = dx * dx + dy * dy;
            if (d2 <= r2 && d2 < bestD2) { bestD2 = d2; bestE = "bottom"; }
        }
        // 左
        {
            double cx = l, cy = Math.Clamp(py, t, b);
            double dx = px - cx, dy = py - cy, d2 = dx * dx + dy * dy;
            if (d2 <= r2 && d2 < bestD2) { bestD2 = d2; bestE = "left"; }
        }
        // 右
        {
            double cx = rr, cy = Math.Clamp(py, t, b);
            double dx = px - cx, dy = py - cy, d2 = dx * dx + dy * dy;
            if (d2 <= r2 && d2 < bestD2) { bestD2 = d2; bestE = "right"; }
        }
        if (bestE.Length == 0) return false;
        nodeId = target; edge = bestE;
        return true;
    }

    /// <summary>开始【四角】缩放：记录原始矩形与模式（对角固定）。</summary>
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

    /// <summary>开始【单轴】缩放：只动一条边（edge = top/bottom/left/right），对侧固定。
    /// 用于卡片"边中点与四角之间"的带状区拖拽。</summary>
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

    /// <summary>锁定画布手势光标：把"系统箭头（编辑态保留系统 I 形）"资源替换为该标准光标（对齐画笔圆环的 SetSystemCursor 方案）。
    /// 框架每帧直接 SetCursor(箭头/I形)，资源被替换后画出的就是手势光标，拖动全程（含指针停下）稳定不回退。
    /// keepIBeam（编辑态传 true）：不动系统 I 形资源 ⇒ 手势期间编辑卡文本区工字不被牵连（用户报告"点一下跳一下"）。</summary>
    private void LockCanvasCursor(int idcId, bool keepIBeam = false)
    {
        _canvasCursorLocked = true;
        CursorInterop.OverrideSystemCursorTo(idcId, keepIBeam);
    }

    /// <summary>解锁画布手势光标：用 SPI_SETCURSORS 一次性恢复全部系统光标（箭头/I形还原为系统默认）。
    /// 之后由编辑态工字作用域轮询（UpdateIbeamScope）按指针位置自行决定是否重装工字。
    ///
    /// ⚠ 只在【确实锁过】时还原：编辑态的文本点击/拖选是编辑框通道驱动的，松手与编辑框丢捕获都会让
    ///   PointerReleased / PointerCaptureLost【冒泡】到 Viewer 通道 —— 那两处是无条件的"手势收尾/捕获丢失"，
    ///   在编辑态纯文本操作时并没有任何画布手势。若无条件 RestoreSystemCursors（SPI_SETCURSORS + SetCursor(真箭头)），
    ///   会把工字作用域刚装好的箭头槽替换当场抹掉并刷出真箭头：指针停下不动就一直停在真箭头（用户报告的根因）。</summary>
    private void UnlockCanvasCursor()
    {
        if (!_canvasCursorLocked) return;
        _canvasCursorLocked = false;
        CursorInterop.RestoreSystemCursors();
        // SPI_SETCURSORS 已把箭头槽还原成真箭头 ⇒ 安装标志同步复位，工字作用域轮询下一拍（≤25ms）
        // 据 should=true && installed=false 自动重装，编辑态卡内不残留真箭头（也覆盖"编辑态缩放手势结束后"）。
        _ibeamInstalled = false;
    }

    /// <summary>启动编辑态"卡内=工字"光标作用域（对齐画笔 EnterPenRingMode）：建 25ms 轮询并立刻评估一次。
    /// 轮询式（而非纯事件）：事件漏一条下一拍也会自愈 —— 这是画笔圆环稳定不回退的根本。</summary>
    /// <param name="pointerOnCard">进入编辑的那一刻，指针是否真的落在编辑卡上。
    /// ⚠ **绝不能无条件写 true**：进编辑有两条【不带落点】的入口 —— 卡片悬浮工具栏的「编辑」按钮（在卡片外面）
    /// 与 MainPage 的 <c>BeginCanvasEdit(nodeId)</c>。此时写 true 会让下面的首拍评估当场装上工字（箭头槽替换），
    /// 指针明明在卡外的按钮上却被刷成工字，下一拍/下一次指针事件才纠正 ⇒ 用户看到的"闪一下又变回来"。
    /// 由调用方按【落点优先、否则实时指针位置】在卡片屏幕矩形内实测后传入（见 BeginCanvasEdit）。</param>
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

    /// <summary>退出编辑态：停轮询、卸下工字替换、清标志（对齐画笔 ExitPenRingMode）。</summary>
    private void ExitIbeamScope()
    {
        try { _ibeamScopeTimer?.Stop(); } catch { }
        SetIbeamInstalled(false);
        _canvasPointerOnEditCard = false;
    }

    private void IbeamScopeTick(object? sender, object e) => UpdateIbeamScope();

    /// <summary>作用域判定（每 25ms 一拍，对齐画笔 UpdateRingScope）：编辑态 + 指针在编辑卡内 + 非边框控件命中带
    /// ⇒ 装工字；否则卸下。手势进行中不干预（手势光标由 LockCanvasCursor/UnlockCanvasCursor 负责）。</summary>
    private void UpdateIbeamScope()
    {
        if (_canvasEditNodeId == null) { SetIbeamInstalled(false); return; }
        if (AnyCanvasGestureActive) return;   // 缩放/拉线/滚动条拖动：让手势光标说话，不抢
        // 按住左键（拖选文本）时恒为工字；否则看指针是否落在文本区（落在控件命中带 → 卸下，让 resize/箭头正常）。
        bool pressed = CursorInterop.AnyMouseButtonDown();
        bool hoverNull = ResolveEditHoverCursor(_canvasPointerPos) == null;
        SetIbeamInstalled(_canvasPointerOnEditCard && (pressed || !_canvasPointerValid || hoverNull));
    }

    /// <summary>安装/卸下工字作用域（仅状态变化才动资源，幂等，对齐画笔 _ringInstalled）：
    /// 安装 = 把系统箭头槽替换为工字副本（keepIBeam 只动箭头槽、不动 I 形槽）。
    /// 文本区框架本就走 IDC_IBEAM（系统工字），此处再把箭头槽也换成工字 ⇒ 编辑框内无论框架按哪个槽求值都是工字。</summary>
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
                _canvasCursorLocked = false;   // Restore 一次性还原全部槽 ⇒ 锁定标志同步复位（不变量：Restore 后必为未锁）
                CursorInterop.RestoreSystemCursors();
            }
        }
        catch { }
    }

    /// <summary>视口点是否命中【当前编辑卡】的某个边框控件（滚动条 / 四角缩放 / 四边中点连线 / 四边中段缩放）。
    /// 判据与 <see cref="OnCanvasEditBoxPointerPressed"/> 的四条分支同源，故"编辑框内通道"与"编辑框外 Viewer 通道"
    /// 对同一控件的认定完全一致。</summary>
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

    /// <summary>视口点是否落在编辑框覆盖的卡片矩形内（用编辑框自身变换，含页缩放/旋转）。</summary>
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

    /// <summary>缩放拖拽中：对角固定，移动角跟随指针；按最小尺寸与页边界 clamp（页局部空间）。
    /// 页局部坐标系与卡片存储坐标同源，旋转态下指针换算已含旋转逆变换，故此处无需感知旋转。</summary>
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
                default:       eBottom = Math.Clamp(py, oy + CanvasMinNodeHeight, ph);   break;   // "bottom"
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

        // ── 四角缩放：对角固定，移动角跟随指针 ──
        string c = _canvasResizeCorner;
        bool moveLeft = c == "tl" || c == "bl";
        bool moveTop = c == "tl" || c == "tr";

        // 对角（固定）与移动角初始值
        double fixedX = moveLeft ? _canvasResizeOrigX + _canvasResizeOrigW : _canvasResizeOrigX;
        double fixedY = moveTop ? _canvasResizeOrigY + _canvasResizeOrigH : _canvasResizeOrigY;

        // 移动角坐标：clamp 到页内，且保证与固定角间距 >= 最小尺寸。
        double moveX = Math.Clamp(px, 0, pw);
        double moveY = Math.Clamp(py, 0, ph);
        if (moveLeft) moveX = Math.Clamp(moveX, 0, fixedX - CanvasMinNodeWidth);
        else moveX = Math.Clamp(moveX, fixedX + CanvasMinNodeWidth, pw);
        if (moveTop) moveY = Math.Clamp(moveY, 0, fixedY - CanvasMinNodeHeight);
        else moveY = Math.Clamp(moveY, fixedY + CanvasMinNodeHeight, ph);

        double left = Math.Min(fixedX, moveX), right = Math.Max(fixedX, moveX);
        double top = Math.Min(fixedY, moveY), bottom = Math.Max(fixedY, moveY);
        if (right - left < CanvasMinNodeWidth) return;   // 页太小等极端情形：放弃本次更新
        if (bottom - top < CanvasMinNodeHeight) return;

        CanvasNodeResized?.Invoke(this, new CanvasNodeEventArgs
        {
            NodeId = node.Id, PageIndex = _canvasResizePage,
            X = left, Y = top, Width = right - left, Height = bottom - top
        });
        SyncCanvasOverlayNow();
    }

    /// <summary>结束缩放（Esc/打断）：仅清状态，最终值已实时写回模型。</summary>
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

    /// <summary>视口坐标 → 命中页 + 页局部 PDF 点（未旋转）。与绘制同源读 _live* 视图镜像。</summary>
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

    /// <summary>视口坐标 → 指定页局部 PDF 点（拖动时可越出页边界，交给调用方 clamp）。</summary>
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

    /// <summary>双击：命中卡片 → 进入编辑（光标落在双击点，Obsidian 行为）；命中空白 → 请求新建（左上角落点，clamp 页内）。</summary>
    private void HandleCanvasDoubleTap(Point vp)
    {
        if (TryHitCanvasNode(vp, out var hit, out _, out _, out _))
        {
            // 已在编辑该卡 →【什么都不做】：RichEditBox 原生双击选词是权威（与系统文本框一致）。
            // 这里若再 SetRange 会与原生选词竞态 —— 映射把"行尾右侧"折算成 '\n' 簇 trailing ⇒ 光标跳到
            // 下一行行首，原生选词又从这个新位置扩词 ⇒ line1 末尾残留蓝底竖条（用户报告）。
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

    // ---- 浮层控件（浮动控件 + 编辑框）定位：跟随选中卡片/编辑卡片的屏幕矩形 ----

    private void CanvasEditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCanvasNodeId != null) BeginCanvasEdit(_selectedCanvasNodeId);
    }

    private void CanvasDeleteButton_Click(object sender, RoutedEventArgs e)
        => DeleteSelectedCanvasNode();

    // ---- 连线悬浮胶囊（P6）：[箭头样式] [颜色] [删除]，箭头/颜色各有一个默认折叠的二级面板 ----

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

    /// <summary>颜色按钮：切换二级颜色面板的展开 / 收起（与箭头面板互斥）。</summary>
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

    /// <summary>箭头三选之一：Tag = none / single / both，选完即收起面板（回到窄态）。</summary>
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
        InvalidateOverlay();
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
        InvalidateOverlay();
    }

    // ---- 连线胶囊的两个二级面板：内联展开 / 收起（照抄卡片胶囊的二级颜色胶囊）----

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
        RefreshEdgeArrowButtons();   // 展开时把"当前连线已应用的箭头样式"强调出来
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
        SyncCanvasEdgeColorDotSelection();   // 展开时把"当前连线已应用颜色"对应的圆点放大
        MeasureCanvasEdgeToolbarExpanded();
    }

    private void HideCanvasEdgeColorPicker()
    {
        if (CanvasEdgeColorDotPanel == null) return;
        CanvasEdgeColorDotPanel.Visibility = Visibility.Collapsed;
        _canvasEdgeColorPickerShown = false;
    }

    /// <summary>量一次【展开态真实宽度】缓存起来，供定位时按真实宽度做右缘钳制（只在越界时左移，正常不位移）。
    /// ⚠ DesiredSize 含 Margin：测前把 Margin 归零，否则上次定位的 left/top 会被算进宽度、测出畸大值。</summary>
    private void MeasureCanvasEdgeToolbarExpanded()
    {
        if (CanvasEdgeToolbar == null) return;
        var saved = CanvasEdgeToolbar.Margin;
        CanvasEdgeToolbar.Margin = new Thickness(0);
        CanvasEdgeToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _canvasEdgeToolbarExpandedW = CanvasEdgeToolbar.DesiredSize.Width;
        CanvasEdgeToolbar.Margin = saved;
    }

    /// <summary>展开时按当前选中连线的颜色高亮对应圆点。7 色一视同仁按 Tag 比较；
    /// 连线未着色（null/空）以默认灰呈现，故高亮首位灰圆点。</summary>
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

    // ---- 卡片二级颜色胶囊（步骤 4）：由卡片胶囊的"颜色"按钮触发 ----

    /// <summary>颜色按钮：切换二级颜色胶囊的展开 / 收起。</summary>
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

    /// <summary>颜色圆点：Tag = "#RRGGBB"，7 色一视同仁（首位即卡片边框默认灰 #8A8F98）。</summary>
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
        // 收起后指针若恰好停在其中一颗胶囊按钮上，悬浮光效照常立刻亮起（用户报告的问题）。
        ClearCanvasToolbarHoverIfMoved(oldLeft, sender as Button);
        RequestCapsuleHoverSyncAfterLayout();   // 收回后按实时指针权威补判一次（同上）
        InvalidateOverlay();
    }

    /// <summary>展开二级颜色胶囊：颜色按钮保留（作二次点击收起开关），7 色圆点在它右侧展开。
    /// 量一次【展开态真实宽度】缓存起来，供定位时按真实宽度做右缘钳制（只在越界时左移，正常不位移）。
    /// ⚠ DesiredSize 含 Margin：测前把 Margin 归零，否则上次定位的 left/top 会被算进宽度、测出畸大值。</summary>
    private void ShowCanvasColorPicker()
    {
        if (CanvasColorDotPanel == null || CanvasCardToolbar == null) return;
        CanvasColorDotPanel.Visibility = Visibility.Visible;
        _canvasColorPickerShown = true;
        SyncCanvasColorDotSelection();   // 展开时把"当前卡片已应用颜色"对应的圆点放大
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

    /// <summary>展开/收起（含钳制）导致胶囊实际位移后，对<b>刚才被按下的那颗按钮</b>开启 hover 冷处理 ——
    /// 挡住 ButtonBase 松手约 380ms 后补发的那次"圆谎" PointerEntered（带松手时的旧坐标），否则会把已滑出
    /// 光标下方的按钮重新点亮。窗口只由该按钮自己的真实 PointerMoved 提前解除（900ms 兜底）。
    /// <para>⚠ 作用域必须收窄到<b>被点的那一颗</b>，不能是整个胶囊子树：那两次假事件只可能发给被按的按钮。
    /// 早先按子树全冷处理，导致点圆点收起后（圆点不归接管、本无假事件），胶囊里 3 颗按钮也被禁亮 900ms
    /// ⇒ 指针若正好停在某颗上，悬浮光效要等一会才亮（用户报告）。</para>
    /// <para>pressed 不必管：它由 IsPressed 属性回调在松手时自行回落。</para></summary>
    private void ClearCanvasToolbarHoverIfMoved(double oldLeft, Button? clicked)
    {
        if (Math.Abs(_canvasToolbarLeft - oldLeft) > 0.5)
            ButtonVisualOverride.SuppressHoverFor(clicked);
    }

    /// <summary>连线胶囊版：判据是【连线胶囊】自己的 left（与卡片胶囊互不相干，别共用同一个字段）。</summary>
    private void ClearCanvasEdgeToolbarHoverIfMoved(double oldLeft, Button? clicked)
    {
        if (Math.Abs(_canvasEdgeToolbarLeft - oldLeft) > 0.5)
            ButtonVisualOverride.SuppressHoverFor(clicked);
    }

    // ---- 胶囊位移后按"最后一次指针位置"补判 hover ----
    // 背景：WinUI 不会因为布局变化而给"刚滑到静止指针下方"的元素补发 PointerEntered/Moved，
    // 几何驱动（只在真实移动时重算）也就没有机会跑 ⇒ 悬浮底色要等它某次迟到的重新命中测试才亮。
    // 故记下胶囊内的最后指针位置（CanvasOverlay 坐标，点击前必然已被 PointerMoved 刷新），
    // 在本次布局落定后（LayoutUpdated，arrange 已完成、TransformToVisual 已是新位置）补判一次。

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

    /// <summary>请求在【本次布局落定后】补判一次胶囊 3 颗按钮的 hover（一次性，触发即退订）。
    /// <para>双保险：① <c>LayoutUpdated</c>（布局落定即触发，同帧内）；② 下一帧 <c>CompositionTarget.Rendering</c>
    /// （渲染必然在布局之后）—— 展开路径里 <c>ShowCanvasColorPicker</c> 手动 <c>Measure</c> 过，会把"待测量"
    /// 标志清掉、框架可能认为无需再布局，单靠 ① 有扑空风险。两者都调用同一个<b>幂等</b>的补判，重复一次无副作用。</para></summary>
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

    /// <summary>收尾：退订两条通道并按记录的实时指针补判一次 hover。</summary>
    private void FinishCapsuleHoverSync()
    {
        if (!_capsuleHoverSyncArmed) return;
        _capsuleHoverSyncArmed = false;
        if (CanvasCardToolbar != null)
            CanvasCardToolbar.LayoutUpdated -= CanvasCardToolbar_LayoutUpdatedOnce;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= CompositionTarget_RenderingOnce;
        ApplyCapsuleHoverFromRecordedPointer();
    }

    /// <summary>按记录下的实时指针位置，判定胶囊 3 颗按钮各自是否被指针覆盖，并<b>权威指派</b> hover。</summary>
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
            catch { inside = false; }   // 元素已脱树（窗口关闭 / 容器回收）期间按"不在内"处理
        }
        ButtonVisualOverride.ForceHover(button, inside);
    }

    // ---- 二级颜色胶囊圆点反馈（照抄 PenOptionsCapsule）：悬浮放大、选中(当前卡片已应用色)放大 ----

    private void SetCanvasColorDotSize(Button b, bool enlarged)
    {
        b.Width = b.Height = enlarged ? CanvasColorDotEnlargedSize : CanvasColorDotBaseSize;
        b.CornerRadius = new CornerRadius(enlarged ? CanvasColorDotEnlargedRadius : CanvasColorDotBaseRadius);
        double inset = enlarged ? CanvasColorDotEnlargedInset : 0.0;
        b.Margin = new Thickness(-inset);
    }

    /// <summary>圆点属于哪一张面板（卡片 / 连线）⇒ 取它那张面板的"当前选中圆点"。
    /// 两副圆点共用同一套悬浮/离开处理器，靠父元素区分，避免为连线再抄一份。</summary>
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

    /// <summary>把"当前已应用颜色"对应的圆点置为选中(放大)态。7 色一视同仁：只放大、不改底色。
    /// <paramref name="edge"/> = true 时作用于连线胶囊的圆点面板，否则作用于卡片胶囊的圆点面板。</summary>
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

    /// <summary>展开时按当前选中卡片的颜色高亮对应圆点。7 色一视同仁按 Tag 比较；
    /// 未着色（空）等价于默认灰 #8A8F98，故默认卡片高亮首位灰圆点。</summary>
    private void SyncCanvasColorDotSelection()
    {
        if (CanvasColorDotPanel == null) return;
        string current = CanvasCardDefaultGrayHex;   // 空/未着色 = 默认灰
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

    /// <summary>给【两副】7 色圆点（卡片胶囊 / 连线胶囊）逐个挂悬浮/离开事件（悬浮预览放大用）。
    /// 绝不在此写 element.Resources（运行时写 Resources 在主题切换会闪退，2026-09-29 崩溃根因；
    /// 底色档已在 XAML 解析期逐按钮声明）。</summary>
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

    /// <summary>按选中连线的实际样式刷新箭头相关显示：① 胶囊主按钮的字形 = 当前样式（收起态也能看出当前值）；
    /// ② 展开面板里三项的强调态（当前 1.0 / 其余 0.4）。</summary>
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

    /// <summary>按当前箭头样式切换主按钮三态符号（none=直线 / single=右箭头 / both=双向箭头，均自绘 Path 描边，照抄 -/+ 方法）。
    /// 三态互斥：只显示当前样式对应的那个，避免叠加。</summary>
    private void SetCanvasEdgeArrowGlyph(string style)
    {
        if (CanvasEdgeArrowNoneGlyph != null)
            CanvasEdgeArrowNoneGlyph.Visibility = style == "none" ? Visibility.Visible : Visibility.Collapsed;
        if (CanvasEdgeArrowSingleGlyph != null)
            CanvasEdgeArrowSingleGlyph.Visibility = style == "single" ? Visibility.Visible : Visibility.Collapsed;
        if (CanvasEdgeArrowBothGlyph != null)
            CanvasEdgeArrowBothGlyph.Visibility = style == "both" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 连线箭头三态符号的描边宽：照抄缩放胶囊 -/+ 的「恒定 2 物理像素」规则
    /// （<c>StrokeThickness = 2 / RasterizationScale</c>，同一推导见 MainPage.ApplyGlyphStrokeThickness）。
    /// <para>为什么必须按 DPI 算：固定写 2 DIP 时，125% 设备宽 = 2.5px、175% = 3.5px（半像素），
    /// 直线上下两边各带 25%~50% 覆盖的灰边（截图取证：上下各 0.25 覆盖行）；三角形斜边可接受，直线不可。
    /// 配合 XAML 里「坐标取 4 的倍数、线心 y=16」，任意常用缩放（100/125/150/175/200%）下
    /// 中心与描边两边都落在整设备像素上 ⇒ 硬边、上下对称。</para>
    /// </summary>
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

    /// <summary>把一条连线的两端箭头归并成工具栏三态：none / single / both。</summary>
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
        // 菜单项点击同样算：菜单摆在卡片下方时，点它本身就在编辑框之外，焦点必然被挪走 —— 但那只是"执行菜单命令"，
        // 不能因此结束编辑（否则全选/剪切刚做完就退出编辑，选区看不见，等于动作没做）。
        if (AnyCanvasGestureActive || _canvasEditControlGesture || _canvasEditMenuClickPending)
        {
            // 焦点被夺走会让插入光标停止闪烁（DrawCanvasEditCaret 以 FocusState 为前提）。用法是"控件交互期间
            // 编辑态零变动、光标照常闪烁"，故这里把焦点重申回编辑框：焦点与指针捕获互不相干，交还焦点不影响
            // Viewer 侧正在进行的控件手势；队列尾部执行，避开本轮焦点切换事件的分发过程。
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

    /// <summary>
    /// 编辑期间拦截 Ctrl+Z / Ctrl+Y，交给 MainPage 的统一撤销栈（与画笔/白板操作共用同一历史）。
    /// 必须用 PreviewKeyDown（隧道事件）：RichEditBox 在 KeyDown 的类处理里就吃掉 Ctrl+Z（原生撤销）
    /// 并把事件标记为 Handled —— 普通 KeyDown 处理器根本收不到（日志证实无 editbox KeyDown key=Z）。
    /// </summary>
    private void CanvasEditBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // 键盘请求文本菜单（Shift+F10 / Application 键）：先记一笔放行 —— 紧随其后的 ContextMenuOpening
        // 没有指针按下可供判断来源，只能靠这个标记区分"键盘要菜单"与"左键手势的尾巴"。
        // 不标 Handled：让框架照常走它自己的菜单请求链路。
        if (IsCanvasEditMenuKey(e)) _canvasEditMenuKeyTick = System.Diagnostics.Stopwatch.GetTimestamp();
        bool ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                     & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (!ctrl) return;
        if (e.Key == VirtualKey.Z)
        {
            e.Handled = true;
            // 先把编辑框"最后一段尚未入史"的文字提交为一步，再撤销 —— 等价于"撤回到上一段"；
            // 撤销后 SetCanvasData 会把编辑框文本与光标一起同步到末尾。
            FlushCanvasEditText();
            CanvasUndoRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (e.Key == VirtualKey.Y)
        {
            e.Handled = true;
            CanvasRedoRequested?.Invoke(this, EventArgs.Empty);
        }
    }

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
        // Esc = 结束编辑（提交当前文本）；Enter 交给 RichEditBox 原生换行（AcceptsReturn=true）。
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            EndCanvasEdit(commit: true);
            return;
        }
        // 方向键/Home/End/PageUp/PageDown 移动光标 ⇒ Win2D 自绘光标需当帧刷新。
        ResetCaretBlink();
        ScreenCanvas.Invalidate();
    }

    /// <summary>文本停顿多久视为"一段输入结束"→ 提交为一步撤销记录。</summary>
    private const int CanvasEditCommitDebounceMs = 400;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _canvasEditCommitTimer;
    /// <summary>已提交入史的文本基线（与它相同则不再产生历史条目）。</summary>
    private string _canvasLastCommittedText = "";

    private void CanvasEditBox_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_canvasEditNodeId == null) return;
        _canvasEditText = GetCanvasEditText();  // 刷新缓存（绘制/提交都读它，避免每帧去读 TOM 文档）
        ApplyCanvasEditCharacterFormatting();   // 样式跟着文本走（只为让原生命中测试与可见排版同宽）
        // 行数变化 → 上方自适应留空随之变化（与阅读态同公式），同步到编辑框内边距（只改 top）。
        SyncCanvasEditTopPad();
        // 文字变化 → 立刻刷新 Win2D 绘制（文字/光标/滚动条皆由 Win2D 画，当帧反映，不再慢半拍）。
        ResetCaretBlink();                     // 打字时光标保持亮，停手后才开始闪烁
        ScreenCanvas.Invalidate();
        ScheduleCanvasEditCommit();
    }

    /// <summary>程序化改文档（剪切/粘贴这类走 TOM 的路径）之后必须调用：RichEditBox 对
    /// <c>Selection.SetText</c> 不保证触发 TextChanged，只 Invalidate 会拿旧快照重画 ⇒
    /// 表现为"删掉/粘上的文字要退出编辑才生效"（用户报告）。</summary>
    private void RefreshCanvasEditAfterProgrammaticEdit()
    {
        if (_canvasEditNodeId == null) return;
        _canvasEditText = GetCanvasEditText();
        ApplyCanvasEditCharacterFormatting();
        SyncCanvasEditTopPad();
        ResetCaretBlink();
        ScreenCanvas.Invalidate();
        ScheduleCanvasEditCommit();   // 程序化改动同样要能入撤销栈
    }

    /// <summary>打字过程中不逐字符入史（否则 Ctrl+Z 变成逐字符退格）：停顿 400ms 视为一段输入结束，
    /// 提交为一步；连续输入仍合并为一步。这样"多次输入"才能分成多步撤销。</summary>
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

    /// <summary>编辑框文本的缓存：RichEditBox 没有 Text 属性，每次读都要走 TOM 文档并分配字符串，
    /// 而留白/滚动条/提交都在高频路径上 ⇒ 统一缓存。所有写入路径（SetCanvasEditText / TextChanged）都会刷新它。</summary>
    private string _canvasEditText = "";
    /// <summary>已成功锁定的编辑态行距（磅）；-1 = 尚未锁定。与"当前目标值"不符时就重新锁一次
    /// —— 目标值依赖阅读态那份 layout 的实测自然行高，会随卡片尺寸/字体就绪而变。</summary>
    private float _canvasEditLineSpacingSet = -1f;

    // ---- 编辑态光标闪烁（Win2D 自绘光标，需自行实现闪烁）----
    private const int CanvasCaretBlinkMs = 530;                 // 亮/灭各 530ms（与系统文本框节奏一致）
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _canvasCaretBlinkTimer;
    private bool _canvasCaretVisible = true;
    // 编辑态指针落在卡片"控件"（滚动条 / 四角 / 四边中点）上时切换的光标（走编辑框 ProtectedCursor；null=默认文本 I 形光标）。
    private static readonly Microsoft.UI.Input.InputCursor? CanvasArrowCursor = MakeCanvasCursor(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
    private static readonly Microsoft.UI.Input.InputCursor? CanvasResizeNwSeCursor = MakeCanvasCursor(Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast);
    private static readonly Microsoft.UI.Input.InputCursor? CanvasResizeNeSwCursor = MakeCanvasCursor(Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest);
    private static readonly Microsoft.UI.Input.InputCursor? CanvasResizeNsCursor = MakeCanvasCursor(Microsoft.UI.Input.InputSystemCursorShape.SizeNorthSouth);   // 上下边单轴缩放
    private static readonly Microsoft.UI.Input.InputCursor? CanvasResizeWeCursor = MakeCanvasCursor(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);   // 左右边单轴缩放
    // 编辑态文本区显式工字：不能依赖 RichEditBox 内部文本表面的隐式 I 形 —— 按下时它捕获指针，
    // 捕获期间光标按【捕获元素(编辑框根)】求值，根上为 null ⇒ 跳成默认箭头、松手才回工字（"点一下跳一下"）。
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
        _canvasCaretVisible = true;   // 退出编辑后下次进入应立即可见
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

    /// <summary>读编辑框文本。RichEdit 用 \r 当段落符，且文档【末尾恒有一个】段落符 ⇒ 归一成 \n 后去掉末尾那一个。</summary>
    private string GetCanvasEditText()
    {
        if (CanvasEditBox == null) return "";
        try
        {
            CanvasEditBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.None, out string s);
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            if (s.Length > 0 && s[s.Length - 1] == '\n') s = s.Substring(0, s.Length - 1);
            return s;
        }
        catch { return ""; }
    }

    /// <summary>写编辑框文本（内部顺带锁定行距）；返回【文档全文长度】，供 SetRange 把光标落到末尾。
    /// RichEdit 的段落符是 \r，故把文本里的 \n 全部转成 \r；末尾那个段落符也计入长度。</summary>
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
        ApplyCanvasEditCharacterFormatting();
        return doc.Length + 1;
    }

    // 编辑态字符格式（只为"度量对齐"，不可见）：隐形 RichEditBox 负责点击落光标/拖选/双击选词的命中测试，
    // 它按自己的排版定位。若 Win2D 把某些字画粗/画成等宽、而文档里没这些格式，两者宽度就会分叉 ⇒
    // 点哪儿都不准（粗体越长偏得越多）。故把同一套样式也贴到文档的字符格式上。
    private bool _canvasEditApplyingFormat;
    private string? _canvasEditFormattedFor;

    /// <summary>把行内 Markdown 样式同步成编辑框的【字符格式】。只动字符级（Bold/Italic/Strikethrough/Name），
    /// 绝不碰段落级（行距由 <see cref="EnsureCanvasEditLineSpacing"/> 独占）。
    /// ⚠ 格式改动可能再次触发 TextChanged ⇒ 用 <see cref="_canvasEditApplyingFormat"/> 防重入。</summary>
    private void ApplyCanvasEditCharacterFormatting()
    {
        if (_canvasEditNodeId == null || CanvasEditBox?.Document == null) return;
        if (_canvasEditApplyingFormat) return;
        if (string.Equals(_canvasEditFormattedFor, _canvasEditText, StringComparison.Ordinal)) return;

        _canvasEditApplyingFormat = true;
        try
        {
            var doc = CanvasEditBox.Document;
            int len = _canvasEditText.Length;
            var all = doc.GetRange(0, len);
            all.CharacterFormat.Bold = Microsoft.UI.Text.FormatEffect.Off;
            all.CharacterFormat.Italic = Microsoft.UI.Text.FormatEffect.Off;
            all.CharacterFormat.Strikethrough = Microsoft.UI.Text.FormatEffect.Off;
            all.CharacterFormat.Name = "Segoe UI";

            foreach (var span in CanvasMarkdown.ParseStyling(_canvasEditText))
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
        }
        catch { }
        finally { _canvasEditApplyingFormat = false; }
    }

    /// <summary>编辑态目标行高（页局部单位 = DIP）：取【阅读态那份 layout 的实测自然行高】(LineMetrics[0].Height)
    /// —— 而不是 <see cref="CanvasLineHeight"/> 那个"标称常数"（标称 1.35×字号，实际约 1.23×字号，差约 9%）。
    /// 文字缺失 / 拿不到该节点时退回标称值兜底。</summary>
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

    /// <summary>把编辑态行距【显式锁】到阅读态的实测行高，使两态行距一致（进出编辑不再看到行距突变）。
    /// ⚠ 单位：SetLineSpacing 的线性值是【磅】(1/72 英寸)，而目标行高是页局部单位(≡DIP，1/96 英寸) ⇒ ×72/96。
    /// ⚠ 段前/段后必须一并清零：RichEdit 默认段间距会让"行距"看起来更大，而 Exactly 只管行高、管不了段间距。
    /// 写完【读回校验】并记录已锁值：未生效则后续布局帧会继续重试（上一版就是在文档未就绪时写失败又被
    /// catch 吞掉、且不重试，导致"设了等于没设"）。</summary>
    private void EnsureCanvasEditLineSpacing()
    {
        if (CanvasEditBox?.Document == null) return;
        float want = (float)(GetCanvasEditTargetLineHeight(_canvasEditNodeRef) * 72.0 / 96.0);
        if (Math.Abs(_canvasEditLineSpacingSet - want) < 0.05f) return;   // 已按当前目标锁好
        try
        {
            var range = CanvasEditBox.Document.GetRange(0, int.MaxValue);
            var pf = range.ParagraphFormat;
            pf.SpaceBefore = 0;
            pf.SpaceAfter = 0;
            pf.SetLineSpacing(Microsoft.UI.Text.LineSpacingRule.Exactly, want);
            range.ParagraphFormat = pf;   // 改完整体赋回 range（WinRT 投影下这样写入最稳）
            // 读回确认（同时作为"已按此值锁好"的依据，避免每帧重复写）
            var chk = CanvasEditBox.Document.GetRange(0, int.MaxValue).ParagraphFormat;
            if (chk.LineSpacingRule == Microsoft.UI.Text.LineSpacingRule.Exactly
                && Math.Abs(chk.LineSpacing - want) < 0.05f)
                _canvasEditLineSpacingSet = want;
        }
        catch { /* 文档尚未就绪：不记已锁值，由 OnCanvasEditBoxLayoutUpdated 在后续布局帧重试 */ }
    }

    /// <summary>粘贴一律按【纯文本】插入：RichEditBox 默认会把剪贴板里的 RTF/HTML 格式一起贴进来，
    /// 让卡片文字变成混排字号/字体（TextBox 时代没有这个问题）。这里拦掉默认粘贴，只取纯文本。</summary>
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
        CanvasEditBox.Document.Selection.Collapse(false);   // 光标落到插入内容之后
    }

    // ───────────────────── 编辑态文本右键菜单（自绘，非弹层）─────────────────────
    // 为什么不挂 ContextFlyout / MenuFlyout：任何 Flyout 都是独立弹层，一弹出就把焦点从编辑框拿走
    // → CanvasEditBox_LostFocus → EndCanvasEdit(commit)，表现为"右键后拖选消失、菜单项作用在已折叠的
    // 隐形宿主上"。所以菜单做在 CanvasOverlay 里（与编辑框同一棵可视树），且整份菜单没有一个可聚焦元素
    // （行是 Grid 不是 Button）⇒ 编辑框全程持有焦点，选区/光标/IME 状态零变动。

    /// <summary>菜单是否开着。只用于收尾判断（按下/按键/视图变化时收起），不参与焦点逻辑。</summary>
    private bool _canvasEditMenuOpen;

    /// <summary>正在执行一次菜单项点击。这段时间内的失焦【不算退出编辑】——
    /// 点击行为发生在编辑框之外（菜单可以摆在卡片下方），框架会把焦点从编辑框挪走，
    /// 若照常 EndCanvasEdit 就会"全选刚做完就退出编辑、选区看不见"，等于动作没做（用户报告）。
    /// 与 _canvasEditControlGesture 同一思路：交互期间编辑态零变动。</summary>
    private bool _canvasEditMenuClickPending;

    // ---- 菜单来源判定：左键只有选取语义（单击/双击/多击/拖选），菜单只由 右键 / 键盘 / 触控长按 触发。
    //      判据是【允许清单】，来源取自根级记录的"最近一次按下"（见 RecordCanvasEditPressOrigin）----
    private bool _canvasEditLastPressWasRight;                    // 最近一次按下是不是右键（含笔筒键）
    /// <summary>键盘（Shift+F10 / Application 菜单键）明确请求了文本菜单的时刻：这一次请求放行。
    /// 键盘触发没有指针按下，不能靠"最近一次按下"判来源，故显式记一笔（ContextMenuOpening 消费即清，
    /// 且只在 1s 内有效 —— 过期标记绝不能放行后面某次左键手势的请求）。</summary>
    private long _canvasEditMenuKeyTick;

    // ---- 双击补救：选中态下"第一次单击"就进编辑（与 Obsidian 一致），于是双击的第二下其实落在编辑态里。
    // 编辑态显示的是带标记的原文且 Markdown 会重排 ⇒ 原生按下会把光标放到原文的那个位置（错位）。
    // 记下进编辑的落点与时刻：若紧接着（双击窗口内、几乎同一点）再来一次按下，按【阅读态】重新映射光标。
    private Point? _canvasEditEntryVp;
    private long _canvasEditEntryTick;
    /// <summary>进编辑时最终落定的光标偏移。双击的第二下要"原地不动"，恢复的就是这个值。</summary>
    private int _canvasEditEntryCaret = -1;
    private const int CanvasDoubleClickWindowMs = 500;
    private const double CanvasDoubleClickSlopDip = 4;
    private Microsoft.UI.Input.PointerDeviceType _canvasEditLastPressDevice;
    /// <summary>最近一次按下是不是左键（与 <see cref="_canvasEditLastPressWasRight"/> 互斥；触控/笔两者皆假）。</summary>
    private bool _canvasEditLastPressWasLeft;
    /// <summary>最近一次按下的时刻。菜单请求只在"这次按下的窗口内"才可能被授权，避免用很久以前的一次右键放行。</summary>
    private long _canvasEditLastPressTick;
    /// <summary>键盘菜单请求标记的有效期（1s）：过期标记绝不能放行后面某次左键手势的请求。</summary>
    private static readonly long CanvasEditLeftMenuBlockTicks = System.Diagnostics.Stopwatch.Frequency;

    // ---- 菜单落点：右键/触控按下时就地记下（覆盖层坐标）----
    // 为什么不靠 ContextMenuEventArgs.CursorLeft/Top：WinUI 3 里它不是"相对控件"，
    // 直接当控件坐标用会把菜单钳到视口角落（用户报告）。按下点的坐标空间是确定的（GetCurrentPoint(CanvasOverlay)
    // 就是菜单 Margin 用的那套），拿它摆位一定准。
    private Point _canvasEditMenuAnchor;
    private bool _canvasEditMenuAnchorValid;
    private long _canvasEditMenuAnchorTick;      // 记下时刻：只认"刚刚那一下"的按下（触控长按要等 ~0.6s 才请求菜单，窗口不能太窄）
    private static readonly long CanvasEditMenuAnchorTicks = System.Diagnostics.Stopwatch.Frequency * 2;   // 2s

    /// <summary>这一次菜单请求有没有"该弹菜单"的来源。用**允许清单**判，不做排除法：
    /// ① 键盘（Shift+F10 / Application 键，KeyDown 时记的标记）；② 鼠标右键（含笔筒键）；③ 触控/笔（长按）。
    /// 其余一律不弹 —— 左键只有选取语义。排除法（"刚拖选过/刚双击过"）必然漏：框架对**任意**左键手势都会补发
    /// 本事件，且左键可能落在菜单上（按下点根本到不了编辑框，靠"编辑框最近按下"记的东西就全是过期值）。
    /// 判据取**根级**记录的"最近一次按下"，它在整个视图内都收得到（handledEventsToo）。</summary>
    private bool IsCanvasEditMenuRequestAuthorized()
    {
        bool keyboard = _canvasEditMenuKeyTick != 0 &&
            System.Diagnostics.Stopwatch.GetTimestamp() - _canvasEditMenuKeyTick < CanvasEditLeftMenuBlockTicks;
        _canvasEditMenuKeyTick = 0;   // 一次请求只消费一次
        if (keyboard) return true;
        // 触控 / 笔：没有左右键之分，长按就是合法的菜单入口（可能按住 >2s，不能套鼠标的时间窗）。
        if (_canvasEditLastPressDevice != Microsoft.UI.Input.PointerDeviceType.Mouse) return true;
        // 鼠标：只认"右键按下"，并且必须还在窗口内 —— 否则一次旧的右键会给后续请求长期授权。
        // 再加一条物理判据：左键此刻按着 ⇒ 绝不可能是右键请求（不依赖"本事件究竟在按下还是松手时发出"）。
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

    /// <summary>根级记下"最近一次按下"的来源（设备 / 左右键 / 时刻）。必须在**根**上记，不能只在编辑框上记：
    /// 点自绘菜单的行时按下点根本到不了编辑框，编辑框那条通道会一直持有上一次右键的旧值，
    /// 从而把随后"左键点菜单"补发的请求误判成右键（用户报告：点复制/全选不断弹出新菜单）。</summary>
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

    /// <summary>按键是不是"请求文本菜单"的组合：Shift+F10 / Application（菜单）键。</summary>
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

    /// <summary>根级按下监听（handledEventsToo 常驻）：菜单开着时，**菜单外的任何按下都立即收起**。
    /// 挂在根上而不是 Viewer 上，是因为卡片胶囊 / 连线工具条这类子控件会自己把 PointerPressed 标 Handled，
    /// 事件根本到不了 Viewer（用户报告"点工具栏不关菜单"）。</summary>
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 先记"这次按下的来源"，与菜单是否已开无关：菜单请求的授权判据完全依赖它（见 IsCanvasEditMenuRequestAuthorized）。
        RecordCanvasEditPressOrigin(e);
        // 再记落点：右键 / 触控长按随后请求菜单时，要靠它把菜单摆到"按下的那一点"。
        RecordCanvasEditMenuAnchor(e);
        if (!_canvasEditMenuOpen) return;
        var pt = e.GetCurrentPoint(Viewer);
        if (IsPointInsideVisibleElement(CanvasEditContextMenu, pt.Position)) return;   // 点在菜单内 → 交给菜单
        HideCanvasEditContextMenu();
    }

    /// <summary>记下用于摆菜单的按下点（覆盖层坐标）。只认右键 / 触控 / 笔：左键按下是"选文本"，
    /// 即便随后真把菜单调起来也该压在激活端或卡片内侧，不该拿它摆位。</summary>
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

    /// <summary>落点是否"刚刚那一下按下"留下的：右键/触控请求与按下几乎同拍，键盘请求（Shift+F10）
    /// 根本没有按下 ⇒ 不能用陈旧点，否则菜单会回到上一次右键的位置。</summary>
    private bool IsCanvasEditMenuAnchorFresh() =>
        _canvasEditMenuAnchorValid &&
        System.Diagnostics.Stopwatch.GetTimestamp() - _canvasEditMenuAnchorTick < CanvasEditMenuAnchorTicks;

    /// <summary>右键/键盘请求弹出文本菜单。**必须在 ContextMenuOpening 里拦**：RichEditBox 的内置菜单走的是
    /// 控件自己的这个事件；实测在 ContextRequested 里 Handled=true 完全无效（系统菜单照出）。
    /// 触发来源只有三种：**右键 / Shift+F10（含 Application 菜单键）/ 触控长按**，其余一律不弹
    /// （框架对任何左键手势都会补发本事件，左键只管选取）。判据集中在 <see cref="IsCanvasEditMenuRequestAuthorized"/>。</summary>
    private void CanvasEditBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_canvasEditNodeId == null) return;
        e.Handled = true;   // ← 系统菜单不再出现（框架据此根本不构建它）；本方法返回后不再有任何菜单工作
        if (!IsCanvasEditMenuRequestAuthorized()) return;
        Point pos;
        // ① 首选：刚刚那一下右键/触控的按下点（坐标空间确定 = 菜单 Margin 用的同一套）。
        if (IsCanvasEditMenuAnchorFresh())
        {
            pos = _canvasEditMenuAnchor;
        }
        // ② 其次：键盘触发（Shift+F10 / Application 键）时按 CursorLeft/Top 多原点自校验。
        else if (!TryGetCanvasEditMenuAnchor(e, out pos))
        {
            // ③ 拿不到可信落点：指针若仍在卡上就用它，否则退到卡片左上角内侧 ——
            //    绝不把菜单甩到视口角落（那是把窗口/屏幕坐标当成控件坐标算出来的典型症状）。
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

    /// <summary>菜单落点。`CursorLeft/CursorTop` 在 WinUI 3 里**不是**"相对控件"的坐标（UWP 文档那套写法
    /// 在这里会算飞，表现为菜单被钳到视口右下角），故用【多候选 + 必须落在编辑框内】自校验：
    /// 分别以编辑框 / 本控件 / 窗口根为原点换算一次，谁落在编辑框矩形（外扩 16 DIP）里谁可信。
    /// 都不在 ⇒ 返回 false，调用方退回实时指针位置。</summary>
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
        // 文案每次弹出时取（语言可在运行期变化；弹出不是高频路径，字典查找无感）。
        CanvasEditMenuCutText.Text = GlanceRX.Services.LocalizationService.Get("CanvasCut");
        CanvasEditMenuCopyText.Text = GlanceRX.Services.LocalizationService.Get("CanvasCopy");
        CanvasEditMenuPasteText.Text = GlanceRX.Services.LocalizationService.Get("CanvasPaste");
        CanvasEditMenuSelectAllText.Text = GlanceRX.Services.LocalizationService.Get("CanvasSelectAll");
        // 可用性：无选区 → 剪切/复制灰掉；剪贴板无文本 → 粘贴灰掉；空文档 → 全选灰掉。
        SetCanvasEditMenuItemEnabled(CanvasEditMenuCut, CanvasEditHasSelection());
        SetCanvasEditMenuItemEnabled(CanvasEditMenuCopy, CanvasEditHasSelection());
        SetCanvasEditMenuItemEnabled(CanvasEditMenuPaste, CanvasClipboardHasText());
        SetCanvasEditMenuItemEnabled(CanvasEditMenuSelectAll, _canvasEditText.Length > 0);
        CanvasEditContextMenu.Visibility = Visibility.Visible;
        // 先量出实际尺寸再钳制落点：菜单贴右/贴下会溢出视口。UpdateLayout 是同步的，
        // 这里只在"用户右键"这一下发生，不在绘制/滚动路径上。
        CanvasEditContextMenu.UpdateLayout();
        double w = CanvasEditContextMenu.ActualWidth > 0 ? CanvasEditContextMenu.ActualWidth : 192;
        double h = CanvasEditContextMenu.ActualHeight > 0 ? CanvasEditContextMenu.ActualHeight : 152;
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
        // 不收的话下次弹出会带着上一次的高亮。
        SetCanvasEditMenuRowHover(CanvasEditMenuCut, false);
        SetCanvasEditMenuRowHover(CanvasEditMenuCopy, false);
        SetCanvasEditMenuRowHover(CanvasEditMenuPaste, false);
        SetCanvasEditMenuRowHover(CanvasEditMenuSelectAll, false);
    }

    /// <summary>菜单项可用性用 Opacity 表达（1 = 可用 / 0.4 = 灰掉）：行是 Grid（不可聚焦），
    /// 灰掉只影响观感与是否执行，不引入额外的状态字段。</summary>
    private static void SetCanvasEditMenuItemEnabled(Grid row, bool enabled)
    {
        if (row == null) return;
        row.Opacity = enabled ? 1.0 : 0.4;
    }

    private static bool IsCanvasEditMenuItemEnabled(Grid row) => row != null && row.Opacity >= 0.99;

    /// <summary>悬停底色：每行第一个子元素就是那块置底的 Border（Opacity 0/1 切换；
    /// 颜色来自主题资源，深浅主题都不用另配）。</summary>
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

    /// <summary>菜单内任何按下都在此吞掉（含右键）：漏到画布会触发右键缩放、左键还会结束编辑。</summary>
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
        // IsPointOverCanvasOverlayChrome 还能把这次按下判成浮层、提前返回（多一道保险）。
        ExecuteCanvasEditMenuAction(action);
        HideCanvasEditContextMenu();
    }

    /// <summary>"菜单项点击进行中"窗口（一次点击的按下→抬起跨度）。用一次性定时器收尾而不是队列尾：
    /// 焦点可能在抬起的另一拍才被挪走，队列尾太早。</summary>
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
                case "selectall":
                {
                    int len = GetCanvasEditText().Length;
                    var sel = CanvasEditBox.Document.Selection;
                    // 先把【活动端】放到文末，再把另一端拉回文首 ⇒ 选中全文，且插入光标停在末尾。
                    // 直接 SetRange(0, len) 会让框架把活动端留在 start，全选后光标跑到文字开头（用户报告）。
                    sel.SetRange(len, len);
                    sel.StartPosition = 0;
                    // 自绘光标的活动端是"与上一帧比较"推出来的：清掉基线，下一帧直接取 End（见 DrawCanvasEditCaret）。
                    _canvasSelLastStart = _canvasSelLastEnd = _canvasSelLastActive = -1;
                    break;
                }
            }
        }
        catch { }
        // 选区/文字变化不经过 TextChanged（全选只动选区）⇒ 这里统一补一次重绘与光标重置。
        ResetCaretBlink();
        ScreenCanvas.Invalidate();
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

    /// <summary>复制选区为【纯文本】：TOM 的段落符是 \r，剪贴板要 \r\n（与粘贴端的反向归一同源）。</summary>
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
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();   // 关掉应用后剪贴板内容仍可用
        }
        catch { }
    }

    /// <summary>粘贴一律按纯文本（与 CanvasEditBox_Paste 同一口径，绝不把 RTF/HTML 带进卡片）。</summary>
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
        // await 期间编辑可能已结束（用户按 Esc / 点了别处）：此时绝不往文档里写
        if (_canvasEditNodeId == null || string.IsNullOrEmpty(text)) return;
        CanvasEditBox.Document.Selection.SetText(Microsoft.UI.Text.TextSetOptions.None,
            text.Replace("\r\n", "\r").Replace('\n', '\r'));
        CanvasEditBox.Document.Selection.Collapse(false);
        RefreshCanvasEditAfterProgrammaticEdit();   // 同上：TOM 改动不一定触发 TextChanged
    }

    /// <summary>把编辑框当前文本作为一步提交（不关闭编辑框）；与上次提交相同则忽略。
    /// 文本未变时 MainPage 侧也会自动忽略，这里提前判断避免无谓事件。</summary>
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

    /// <summary>设置视口四边被【上层浮层】占掉的量（DIP），悬浮胶囊/文本菜单的钳制会避开这些带。
    /// 由 MainPage 在布局变化（窗口尺寸 / 顶栏尺寸 / 侧栏开合）时推送。全 0 = 不避让（旧行为）。</summary>
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

    /// <summary>按当前视图状态把浮动控件/编辑框摆到目标卡片或连线上；不可见则整体隐藏。</summary>
    private void SyncCanvasOverlayNow()
    {
        if (CanvasOverlay == null) return;

        bool editing = _canvasEditNodeId != null;
        // 编辑态：先摆不可见编辑框（输入宿主）。⚠ 这里【不能 return】—— 编辑卡上方的卡片工具栏也要摆，
        // 否则编辑态缩放卡片时它会停在原地、不随卡片上沿移动（用户报告）。
        if (editing) PositionCanvasEditBox();

        // 连线选中（非编辑）：工具栏摆在连线中点附近，与卡片工具栏互斥。
        if (!editing && _selectedCanvasEdgeId != null)
        {
            PositionCanvasEdgeToolbar();
            return;
        }

        if (_selectedCanvasNodeId == null ||
            !TryGetCanvasNodeScreenRect(_selectedCanvasNodeId, out var rect))
        {
            if (editing) return;   // 编辑中：编辑框已摆好，别把整体藏掉
            CanvasOverlay.Visibility = Visibility.Collapsed;
            HideCanvasCardToolbar();
            HideCanvasEdgeToolbar();
            return;
        }

        CanvasOverlay.Visibility = Visibility.Visible;
        ShowCanvasCardToolbar();   // 编辑态也保持显示（摆位随卡片走；用户要求）
        HideCanvasEdgeToolbar();
        // 浮动控件尺寸固定（两颗按钮），测一次即缓存：拖动时每次指针事件都会走到这里，
        // 重复 Measure 会白白触发布局。位置未变（<0.5px）也不再写 Margin，进一步省掉无效布局。
        if (_canvasToolbarW <= 0)
        {
            // ⚠ Measure 的 DesiredSize【含 Margin】：胶囊上次定位写入的 (left,top) 会被算进宽高，
            //    展开/收起后重新 Measure 就会得到畸大尺寸 ⇒ left/top 被钳到视口左上、看起来"脱离卡片"。
            //    故测前先把 Margin 归零、并强制下方重新落位（_canvasToolbarPlaced=false）。
            CanvasCardToolbar.Margin = new Thickness(0);
            _canvasToolbarPlaced = false;
            CanvasCardToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var measured = CanvasCardToolbar.DesiredSize;
            _canvasToolbarW = measured.Width;
            _canvasToolbarH = measured.Height;
        }
        double szW = _canvasToolbarW, szH = _canvasToolbarH;   // 窄态宽度：用于"跟踪卡片中心"（展开时不重新居中）
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
        // 向下滚→钉上缘、向右滚→钉左缘，移出视口即停在对应边缘。
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

    // 卡片悬浮控件显隐：与缩放胶囊同源的淡入淡出（Opacity 0.15s）。
    // 显示：取消在途淡出再淡入；隐藏：淡出结束后才 Collapsed（放掉命中，避免透明态拦截点击）。
    // _cardToolbarShown 去重：SyncCanvasOverlayNow 每次调用都走到这里，仅 Collapsed→Visible / Visible→Collapsed 的
    // 跳变才触发动画，避免每帧重启动画导致闪烁 / 长时间停在半透明。
    private void ShowCanvasCardToolbar()
    {
        if (CanvasCardToolbar == null) return;
        if (_isCanvasConnecting) return;   // 拉线（连线）进行中：暂隐胶囊，避免遮挡连线交互
        CanvasCardToolbarHideAnim?.Stop();
        if (_cardToolbarShown) return;
        _cardToolbarShown = true;
        CanvasCardToolbar.Visibility = Visibility.Visible;
        CanvasCardToolbar.Opacity = 0;
        CanvasCardToolbarShowAnim?.Begin();
    }

    private void HideCanvasCardToolbar()
    {
        if (CanvasCardToolbar == null) return;
        HideCanvasColorPicker();   // 卡片工具栏收起（取消选中/拉线中）→ 二级颜色胶囊一并收起
        if (!_cardToolbarShown)
        {
            CanvasCardToolbar.Visibility = Visibility.Collapsed;
            return;
        }
        _cardToolbarShown = false;
        CanvasCardToolbarShowAnim?.Stop();
        CanvasCardToolbarHideAnim?.Begin();
    }

    // 连线胶囊显隐：与卡片胶囊同源的淡入淡出（0.15s），淡出结束才 Collapsed（放掉命中）。
    private void ShowCanvasEdgeToolbar()
    {
        if (CanvasEdgeToolbar == null) return;
        CanvasEdgeToolbarHideAnim?.Stop();
        if (_edgeToolbarShown) return;
        _edgeToolbarShown = true;
        CanvasEdgeToolbar.Visibility = Visibility.Visible;
        CanvasEdgeToolbar.Opacity = 0;
        CanvasEdgeToolbarShowAnim?.Begin();
    }

    private void HideCanvasEdgeToolbar()
    {
        if (CanvasEdgeToolbar == null) return;
        HideCanvasEdgePickers();   // 连线胶囊收起（取消选中/切卡片/退出模式）→ 两个二级面板一并收起
        if (!_edgeToolbarShown)
        {
            CanvasEdgeToolbar.Visibility = Visibility.Collapsed;
            return;
        }
        _edgeToolbarShown = false;
        CanvasEdgeToolbarShowAnim?.Stop();
        CanvasEdgeToolbarHideAnim?.Begin();
    }

    /// <summary>把边工具栏摆到选中连线锚点附近（仿 Obsidian Canvas）：
    /// X=连线贝塞尔中点 X，Y=靠上方卡片连接点 Y；出厂在视口外时【双向钳制】到视口边缘，
    /// 并尽可能满足原 XY（向下滚→钉视口上缘，向右滚→钉视口左缘，皆不够则停最近角）。</summary>
    private void PositionCanvasEdgeToolbar()
    {
        if (_selectedCanvasEdgeId == null ||
            !TryGetCanvasEdgeToolbarAnchorScreen(_selectedCanvasEdgeId, out var anchor))
        {
            CanvasOverlay.Visibility = Visibility.Collapsed;
            HideCanvasEdgeToolbar();
            return;
        }
        CanvasOverlay.Visibility = Visibility.Visible;
        HideCanvasCardToolbar();
        ShowCanvasEdgeToolbar();
        RefreshEdgeArrowButtons();   // 主按钮字形 = 当前箭头样式
        if (_canvasEdgeToolbarW <= 0)
        {
            // ⚠ Measure 的 DesiredSize【含 Margin】（卡片胶囊踩过同一个坑）：上次定位写入的 (left,top) 会被算进
            //    宽高 ⇒ 宽度畸大、left/top 被钳到视口左上，看起来"脱离连线"。故测前把 Margin 归零并强制重新落位。
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
        double left = anchor.X - _canvasEdgeToolbarW / 2.0;   // 窄态宽度跟踪连线中点（展开时不重新居中）
        double top = anchor.Y - _canvasEdgeToolbarH - CanvasToolbarGap;
        // 同上：钳到上层浮层底下就点不动了，一律走安全区。
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

    /// <summary>把【不可见输入宿主】编辑框贴合到卡片屏幕上：宽高/字号/内边距/变换矩阵用页局部值 + MatrixTransform，
    /// 内容起点 = 卡片内边距，与 Win2D 文字原点对齐。编辑框只负责接收键盘/指针/IME 输入、维护 TOM 文档与光标，
    /// 可见文字/光标/选区/滚动条全部由 Win2D 按其自身 CanvasTextLayout 绘制（与阅读/选中态同引擎 ⇒ 两态零偏移）。
    /// 旋转/缩放/布局切换共用同一套页变换，天然贴合。</summary>
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
            CanvasOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        GetViewState(out double z, out double hOff, out double vOff);
        if (z <= 0) return;
        var xf = MakePageXform(ComputePageDest(pageIdx, z, hOff, vOff), z);
        _canvasEditNodeRef = node;      // 行距锁定复用
        _canvasEditPageXf = xf;         // 备用（命中/绘制统一走 Win2D）

        double w = Math.Max(12, node.Width);
        double h = Math.Max(12, node.Height);
        // 编辑框是【不可见输入宿主】(Opacity=0)：仅接收输入并维护文档与光标，自身不绘制。
        // 内容起点 = 卡片内边距(PadX,PadY)，与 Win2D 文字原点对齐；行距锁到 Win2D 实测值（仅供"点击落光标"命中映射）。
        var p00 = xf.Map(node.X, node.Y);
        var p10 = xf.Map(node.X + w, node.Y);
        var p01 = xf.Map(node.X, node.Y + h);
        // 控件局部 (0,0)~(w,h) → 屏幕 p00、(p10-p00)、(p01-p00) 的仿射矩阵（含页缩放与旋转）。
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
        //   而卡片下内边距本为 0（CanvasCardPadY=0）⇒ 下中点手柄【侵入卡片内侧】那 12px 全落在文字面上，
        //   覆盖的 ProtectedCursor(箭头) 被它顶掉、光标变不回来（上/左/右因各有留白带而正常 —— 用户报告的非对称）。
        //   下padding 只在底部留空、不影响顶部对齐的正文原点，故安全。
        CanvasEditBox.Padding = new Thickness(CanvasCardPadX,
            CanvasCardPadY + ComputeCanvasEditTopPad(w - 2 * CanvasCardPadX, h - 2 * CanvasCardPadY),
            CanvasCardPadX, CanvasCardPadY + CanvasHandleHitRadius);
        CanvasEditBox.Margin = new Thickness(0);
        CanvasEditBox.RenderTransform = new MatrixTransform { Matrix = m };

        HideCanvasEdgeToolbar();
        CanvasOverlay.Visibility = Visibility.Visible;
        CanvasEditBox.Visibility = Visibility.Visible;   // Opacity=0（XAML）→ 不可见、但可命中/可输入
        EnsureCanvasEditLineSpacing();   // 行距锁到 Win2D 实测值（仅供点击落光标的命中映射对齐）
        EnsureCanvasEditScrollHost();
    }

    /// <summary>确保已拿到 RichEditBox 内部 ScrollViewer 并挂上 ViewChanged/SizeChanged（幂等）——
    /// 编辑态滚动由 RichEditBox 内部 ScrollViewer 驱动，偏移变化只需刷新 Win2D 绘制（文字/光标/滚动条随内容滚动）。
    /// ⚠ 绝不在这里排队重试：曾用 DispatcherQueue.TryEnqueue 自递归重试，模板未展开时会【无界排任务】→ UI 线程卡死。
    /// 现在查不到就先返回，由 LayoutUpdated（<see cref="OnCanvasEditBoxLayoutUpdated"/>）在后续布局帧重试，拿到即退订。</summary>
    private void EnsureCanvasEditScrollHost()
    {
        if (_canvasEditScrollViewer == null)
        {
            var sv = FindDescendant<ScrollViewer>(CanvasEditBox);
            if (sv == null) return;   // 模板还没展开 → 等下一次布局（有界：每帧至多一次）
            _canvasEditScrollViewer = sv;
            // 滚动/尺寸变化 → 刷新 Win2D 绘制（编辑态文字/光标/滚动条皆由 Win2D 画）。
            _canvasEditScrollViewer.ViewChanged += (_, __) => ScreenCanvas.Invalidate();
            _canvasEditScrollViewer.SizeChanged += (_, __) => ScreenCanvas.Invalidate();
            CanvasEditBox.LayoutUpdated -= OnCanvasEditBoxLayoutUpdated;   // 已拿到 → 退订
        }
    }

    /// <summary>构造卡片文本排版格式。字体与编辑框（XAML 侧显式 FontFamily="Segoe UI"）保持一致，
    /// 避免两侧默认字体不同带来的度量差异。
    /// ⚠ 行距**不设** LineSpacing：阅读/选中态就用 Win2D 按字体度量的自然行距，它是两态行距的"基准"
    /// （编辑态由 <see cref="EnsureCanvasEditLineSpacing"/> 用 SetLineSpacing(Exactly) 精确锁到同一个实测值）
    /// —— 而不是反过来去拉大 Win2D 的行距（那版实测失败已整体回退）。</summary>
    private CanvasTextFormat CreateCanvasTextFormat()
    {
        return new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = CanvasCardFontSize,
            WordWrapping = CanvasWordWrapping.Wrap,
        };
    }

    /// <summary>编辑框布局回调 —— 编辑期间持续订阅：① 模板展开前每帧重试取内部 ScrollViewer（幂等）；
    /// ② 行距自愈（进入编辑当帧文档可能未就绪，逐帧重试直到确认生效）；③ 之后每帧兜底刷新 Win2D 绘制
    /// （文字/光标/滚动条随布局变化当帧反映，避免滚动条出现/消失慢半拍）。</summary>
    private void OnCanvasEditBoxLayoutUpdated(object? sender, object e)
    {
        if (_canvasEditNodeId == null)
        {
            CanvasEditBox.LayoutUpdated -= OnCanvasEditBoxLayoutUpdated;   // 退出编辑 → 退订
            return;
        }
        EnsureCanvasEditLineSpacing();   // 行距自愈（仅供点击落光标的命中映射对齐）
        ApplyCanvasEditCharacterFormatting();   // 字符格式同样自愈：进入编辑当帧文档可能还没就绪（首贴会失败）
        if (_canvasEditScrollViewer == null) EnsureCanvasEditScrollHost();   // 幂等：拿到 sv 后由内部退订
        else ScreenCanvas.Invalidate();                                     // 每帧兜底刷新 Win2D 绘制
    }

    /// <summary>编辑框上的指针移动：编辑框是 XAML 元素、会吃掉 Viewer 的 PointerMoved ⇒ 编辑态下
    /// `_canvasPointerPos` 不更新，卡片边框手柄（四角/四边）就算不出"指针最近的那一个"、无法显示与命中。
    /// 这里补喂：把指针位置（统一转成 Viewer 坐标）同步给白板交互层，并重绘让手柄跟着指针走。</summary>
    private void OnCanvasEditBoxPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_canvasEditNodeId == null) return;
        var p = e.GetCurrentPoint(Viewer).Position;
        _canvasPointerOnEditCard = true;   // 编辑框通道收到移动 ⇒ 指针在编辑卡上
        // 文本拖选进行中（左键按住）：冻结边框控件交互 —— 不刷新手柄显形/悬停，
        // 指针滑过控件带也不切光标；元素级工字（箭头槽替换态由工字作用域轮询维持）。
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
        // 元素级光标：文本区工字、控件区对应光标（箭头槽替换态由工字作用域轮询按此同步）。
        var hoverCursor = ResolveEditHoverCursor(p);
        CanvasEditBox.OverrideCursor(hoverCursor ?? CanvasIBeamCursor);
        InvalidateOverlayThrottled();
    }

    /// <summary>编辑态指针悬停光标：委托给共享的 ResolveCanvasHoverCursor（编辑框覆盖整卡，走其 ProtectedCursor）。</summary>
    private Microsoft.UI.Input.InputCursor? ResolveEditHoverCursor(Point p) => ResolveCanvasHoverCursor(p);

    /// <summary>卡片"控件"悬停光标（编辑态与非编辑态共用）：在控件上给对应光标，文本区返回 null（= 控件默认 I 形）。
    /// 优先级：滚动条 &gt; 四角缩放(斜向) &gt; 四边中点连线(箭头) &gt; 四边中段单轴缩放(上下/左右)。
    /// 角域/中点域半径各自吃掉两端与中心，剩下的"边中点与四角之间"中段才归单轴缩放 —— 顺序必须为 角&gt;中点&gt;边。</summary>
    private Microsoft.UI.Input.InputCursor? ResolveCanvasHoverCursor(Point p)
    {
        // 胶囊叠层遮罩：悬停其上时不强加背后卡片的边框控件光标（卡片角/边缩放光标等），
        // 使胶囊成为真正隔离的控件面（用户实测：胶囊区光标会随背后卡片变）。
        if (IsPointOverCanvasOverlayChrome(p)) return null;
        if (TryHitCanvasScrollBar(p, out var sb) && sb != null) return CanvasArrowCursor;
        if (TryHitCanvasCorner(p, out _, out var corner))
            return (corner == "tl" || corner == "br") ? CanvasResizeNwSeCursor : CanvasResizeNeSwCursor;
        if (TryHitCanvasHandle(p, out _, out _)) return CanvasArrowCursor;   // 拉线控件：默认箭头（不用十字线）
        if (TryHitCanvasEdge(p, out _, out var edge))
            return (edge == "top" || edge == "bottom") ? CanvasResizeNsCursor : CanvasResizeWeCursor;
        return null;   // 文本区 → 控件默认（I 形）
    }

    /// <summary>编辑框上的指针抬起。编辑态下能落在编辑框上的只有【文本按下】—— 边框控件命中带已被
    /// <see cref="SyncCanvasEditHitCarve"/> 从编辑框命中区挖除，控件交互全程走 Viewer 通道（含其自有的
    /// PointerReleased 收尾）。故这里只需按松手点重判"指针是否仍在卡内"（松手前可能已滑出卡、拖动捕获期间
    /// 收不到 PointerExited），供工字作用域轮询下一拍决定装/卸工字。</summary>
    private void OnCanvasEditBoxPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _canvasEditDragAnchor = _canvasEditDragLast = _canvasEditPendingCaret = -1;   // 一次拖选收尾：下次按下重新取锚点
        _canvasPointerPos = e.GetCurrentPoint(Viewer).Position;
        _canvasPointerValid = true;
        _canvasPointerOnEditCard = IsPointOnCanvasEditCard(_canvasPointerPos);
        // 右键松手 = 菜单即将弹出（系统在松手时才请求）：把落点刷新到松手点，
        // 这样"按住右键停了很久再松手"也不会因为按下时刻太早而被判成陈旧落点。
        if (_canvasEditLastPressWasRight && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse)
            RefreshCanvasEditMenuAnchor(e);
    }

    /// <summary>指针离开编辑卡：清"在卡上"标志 ⇒ 工字作用域轮询下一拍卸下替换（卡外走正常悬停光标）。
    /// 按住（拖选滑出卡）时不清：按住全程恒工字，松手后 Released 按实际位置重判自愈。</summary>
    private void CanvasEditBox_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (CursorInterop.AnyMouseButtonDown()) return;
        _canvasPointerOnEditCard = false;
    }

    /// <summary>在可视化树里深度优先找第一个指定类型后代（用于取 TextBox 模板内的 ScrollViewer）。
    /// 对未接入视觉树的元素，VisualTreeHelper 可能抛异常 → 一律按"没找到"处理，绝不外抛。</summary>
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

    /// <summary>卡片在【视口坐标】的轴对齐外接矩形（含旋转页面下的四角包围盒）。</summary>
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
        // 旧实现把步进放在 CompositionTarget.Rendering、上屏放在本 Draw，两套 vsync 队列相位脱节，
        // 在 120Hz 屏上表现为"一半帧是上一帧的重复"→ 肉眼 judder。现在步进紧跟在 Draw 之首、
        // 下面 GetViewState 即读刚算出的位置，且末尾 Invalidate 维持的节奏就是 Win2D 交换链 vsync。
        {
            _lastFrameTicks = Stopwatch.GetTimestamp();
            double dt = ComputeFrameDt();
            bool animating = false;
            if (_isSmoothZooming) { StepZoomFrame(dt); animating = true; }
            if (_isWheelScrolling) { StepScrollFrame(dt); animating = true; }
            StepEdgeEmphFrame(dt); if (_edgeEmphAnimating) animating = true;  // 无条件步进：任何强调目标变化/中途都推进，内部维护 _edgeEmphAnimating
            StopFramePumpIfIdle();                         // 全部落定 → 停看门狗/循环标志
            if (animating) InvalidateBoth();              // 仍在进行 → 续帧（Win2D 交换链 vsync 节奏）
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

        // ---- 白板层：卡片 + 连线。与页位图/标注绘于同一交换链，随页面旋转/缩放。
        // 卡片常显（跟随 AnnotationsVisible）。
        if (AnnotationsVisible && HasCanvasContent())
            DrawWhiteboardLayer(ds, z, visible);

        // 视图在动 → 选中卡片 / 编辑框 / 选中连线的屏幕位置随之改变，每帧合并排一次浮层重定位。
        // 放在 Draw 末尾而非直接改布局：避免在绘制回调内触发布局重入。
        // ⚠ 三个条件缺一不可：漏掉 _selectedCanvasEdgeId 时，选中连线后滚动/缩放不会重排边工具栏，
        //    它会被钉死在首次选中时的视口位置（连线走了、工具栏不动）。
        if (_selectedCanvasNodeId != null || _canvasEditNodeId != null || _selectedCanvasEdgeId != null)
            RequestCanvasOverlaySync();

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
        // 逐页设置的是【绝对】会话变换，绘制后必须还原为恒等：否则最后的页变换会泄漏给
        // 紧随其后的白板层 —— 连线在屏幕坐标绘制，被泄漏的页变换二次映射后会"脱落"卡片，
        // 直到视图落定（走 _annoLayer 路径不再改变换）才恢复。这就是"缩放时连线先脱落再回去"的根因。
        var saved = ds.Transform;
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
        ds.Transform = saved;
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
        // 白板模式：双击卡片=进入编辑，双击空白=新建卡片；不触发缩放切换。
        if (CanvasInputActive)
        {
            HandleCanvasDoubleTap(e.GetPosition(Viewer));
            e.Handled = true;
            return;
        }
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
        _canvasClickEditPending = false;     // 新按下 ⇒ 清上一次的"单击已选中卡进编辑"待定（本次按下重新判定）

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

        // 编辑态：右键落在编辑框上 = 要弹【文本右键菜单】（自绘，见 CanvasEditContextMenu），不是右键拖拽缩放。
        // 直接 return 即可：既不开启 _isRightZooming，也不会走到下面的"落卡外就结束编辑"。
        // ⚠ 刻意【不】设 e.Handled：WinUI 的右键手势/ContextMenuOpening 与指针按下的 handled 状态有关，
        //   吞掉按下有连带把菜单请求也吞掉的风险（那菜单就再也不弹了）。
        if (_canvasEditNodeId != null && pt.Properties.IsRightButtonPressed &&
            IsPointInsideVisibleElement(CanvasEditBox, pt.Position))
            return;
        // 菜单开着时的其他按下（菜单内的按下被菜单自己吃掉，到不了这里）⇒ 先收起再继续常规处理。
        HideCanvasEditContextMenu();

        // ── 浮层 chrome 隔离：卡片胶囊 / 连线工具条是叠在画布上的独立控件，必须先把落在它们之上的
        //    按下隔离掉，否则【画布命中链会按"画布几何"】把胶囊背后的卡片边框控件 / 卡片本体 / 连线当成命中
        //    目标（用户实测：整颗胶囊区左键都会落到背后卡片的边框控件上、拖拽直接操作背后卡片）。
        //  · 左键：纯 no-op —— 不关胶囊、不拖背景、不触发背后任何元素（事件在此被吞，杜绝穿透）；
        //  · 右键：放行到下方右键缩放手势（物理自愈兜底，松手即收尾）。
        //    判据复用 IsPointOverCanvasOverlayChrome（与悬停侧浮层遮罩同一口径）；仅拦左键，右键不拦。
        if (IsPointOverCanvasOverlayChrome(pt.Position))
        {
            if (pt.Properties.IsLeftButtonPressed)
            {
                e.Handled = true;   // 吞掉，杜绝穿透到背后卡片
                return;
            }
            // 右键：跳过下面整条画布命中链（卡片/连线），直接走底部右键缩放手势。
        }

        // 白板编辑中：任何落在编辑框之外的按下都结束编辑（Obsidian 行为）。编辑框与浮动控件
        // 都在覆盖层内，落在其上的按下被它们吃掉、到不了本视图 —— 能到这里的一定是"卡片之外"。
        //
        // ⚠ 例外：命中【当前编辑卡】边框控件的按下（卡片滚动条 / 四角 / 四边中点 / 四边中段）。这些控件的命中带
        //   会探出编辑框覆盖区（卡片外缘与底部手柄带都留了命中半径一圈），属"控件交互"而非"点卡外退出编辑"。
        //   这里不结束编辑，交给下面的白板手势分派 —— 走的是与阅读态完全相同的 Viewer 通道（会 CapturePointer），
        //   编辑框全程收不到这次指针流 ⇒ 光标不挪、选区不变、编辑态不退出（用户报告：编辑态用边缘控件会退出编辑）。
        // 控件命中判定依赖 _hoverCanvasNodeId（回退 _selectedCanvasNodeId，编辑卡进编辑即置选中）；
        // 先在按下点就地刷新一次悬停，避免指针从别处快速移入时用的是上一次 PointerMoved 的残值。
        if (_canvasEditNodeId != null)
        {
            _canvasPointerPos = pt.Position;
            _canvasPointerValid = true;
            UpdateCanvasNodeHover(pt.Position);
        }
        _canvasEditControlGesture = _canvasEditNodeId != null && PressHitsEditedCardControl(pt.Position);
        if (_canvasEditNodeId != null && !_canvasEditControlGesture) EndCanvasEdit(commit: true);

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

        // ── 白板模式：左键优先命中卡片 → 选中并准备拖动；未命中卡片则取消选中，照常拖动平移 ──
        if (CanvasInputActive && pt.Properties.IsLeftButtonPressed)
        {
            // 手柄"显形/命中"都依据指针位置：按下瞬间先就地刷新一次（不依赖上次 PointerMoved 的残值）。
            // 编辑态已在上面"结束编辑判定"处就地刷新过同一坐标，此处不重复。
            if (_canvasEditNodeId == null)
            {
                _canvasPointerPos = pt.Position;
                _canvasPointerValid = true;
                UpdateCanvasNodeHover(pt.Position);
            }
            // 视口接管键盘焦点：鼠标点击画布不会让 ScrollViewer 自动获焦，而 MainPage 的 Delete/Backspace
            // 处理是【依赖焦点】的 KeyDown 路由（必须穿过 MainPage 视觉树才可达）⇒ 首次打开带卡片的 PDF 时
            // 选中卡片/连线按 Del 无效，非要在编辑态进出一次（退出编辑会 RestoreViewerFocus）才恢复（用户报告）。
            // ⚠ 编辑态【不】抢焦点：编辑框必须继续持有键盘焦点，插入光标才照常闪烁（DrawCanvasEditCaret 以
            //   FocusState 为绘制前提），方向键也才能继续移动光标。控件命中带的按下虽走本通道，但焦点保持不动
            //   ⇒ "用边框控件时编辑态零变动"。退出编辑后由 RestoreViewerFocus 恢复视口焦点。
            if (_canvasEditNodeId == null) EnsureViewerFocus();
            // 滚动条优先：它在卡片内部、意图更明确；命中区仅滚动条矩形（±小容差），
            // 只对真的画了滚动条的卡片生效 ⇒ 不会把贴着卡片边框的缩放/连线手柄整体抢走。
            if (TryHitCanvasScrollBar(pt.Position, out var sbNode) && sbNode != null)
            {
                StopSmoothZoom();
                StopWheelScroll();
                if (sbNode.Id != _selectedCanvasNodeId)
                {
                    // 阅读态：按住滚动条【拖动 = 移动卡片】并进入选中态；只是点一下（不移动）则仅选中。
                    SelectCanvasNode(sbNode.Id);
                    // 直接用滚动条所属卡片自身作拖动对象（不再绕经 TryHitCanvasNode：选中卡片已置顶绘制，
                    // 命中测试会优先返回它，可能返回一张并非滚动条所属的卡片 ⇒ 拖错对象）。
                    if (CanvasViewportToPage(pt.Position, out int dPage, out double dPx, out double dPy))
                    {
                        _isCanvasDragging = true;
                        _canvasDragNode = sbNode;
                        _canvasDragPage = dPage;
                        _canvasDragStartPx = dPx;
                        _canvasDragStartPy = dPy;
                        _canvasDragOrigX = sbNode.X;
                        _canvasDragOrigY = sbNode.Y;
                    }
                }
                else
                {
                    BeginCanvasScrollDrag(sbNode, pt.Position);   // 已选中 → 拖滚动条滚内容
                }
                this.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
            // 命中优先级 = 绘制层级（挡在上面的先命中），严格照抄 Obsidian Canvas：
            //   滚动条 > 四角（缩放）> 四边中点（拉线手柄）> 四边中段（单轴缩放）> 卡片本体（移动/点选）> 连线。
            // 手柄都落在卡片边界上，若先测本体必被"移动"抢走；四角与四边手柄在极小卡片上可能重叠，
            // 故四角最优先；中段单轴缩放带夹在角域与中点域之间，须排在拉线手柄之后、本体之前。
            // 连线【最后】：它画在所有卡片之下（见 DrawWhiteboardLayer），被卡片盖住的那一段根本看不见，
            // 让它抢命中只会变成"点卡片却选中了看不见的线"。⇒ 连线只在卡片之外的空白处可选中，
            // 卡片的一切控件与交互天然免于和连线打架（用户定案）。
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
            // 连线画在卡片之下（见 DrawWhiteboardLayer 的绘制顺序），看不见的部分就不该抢命中。
            if (TryHitCanvasNode(pt.Position, out var hitNode, out int hitPage, out double hitPx, out double hitPy)
                && hitNode != null)
            {
                StopSmoothZoom();
                StopWheelScroll();
                // Obsidian 行为：点击【已选中】卡片且松手前未拖动 → 进入编辑态（光标落点击点）。
                // 挂起待定标记：PointerMoved 超阈值即视为拖动并撤销；PointerReleased 仍挂起才进编辑。
                bool wasSelected = string.Equals(hitNode.Id, _selectedCanvasNodeId, StringComparison.Ordinal);
                _canvasClickEditPending = wasSelected && _canvasEditNodeId == null;
                _canvasClickEditStartVp = pt.Position;
                SelectCanvasNode(hitNode.Id);
                _isCanvasDragging = true;
                _canvasDragNode = hitNode;
                _canvasDragPage = hitPage;
                _canvasDragStartPx = hitPx;
                _canvasDragStartPy = hitPy;
                _canvasDragOrigX = hitNode.X;
                _canvasDragOrigY = hitNode.Y;
                this.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
            // 连线命中（最后）：只负责"卡片之外"的空白区。
            // 命中阈值 = max(CanvasEdgeHitThreshold, 光晕半宽)，与 UpdateCanvasEdgeHover 同一条；
            // 悬停侧另有按卡片矩形的遮罩 ⇒ "亮的范围 = 能点中的范围"依然成立，只是两者都排除卡内。
            if (TryHitCanvasEdge(pt.Position, out var hitEdgeId))
            {
                StopSmoothZoom();
                StopWheelScroll();
                SelectCanvasEdge(hitEdgeId);
                e.Handled = true;
                return;
            }
            // 命中链兜底：点中"画布空白"（卡片/连线/控件之外）⇒ 取消选中（随后左键继续拖动平移）。
            // 落在卡片胶囊 / 连线工具条上的左键已在上方 chrome 隔离处直接 return，不会到达这里。
            SelectCanvasNode(null); // 点空白 → 取消选中（随后左键继续拖动平移）
        }

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

        // 白板模式：记录指针视口坐标（手柄"按接近度显形"依赖它），随后各分支照常处理。
        // ⚠ 手势进行中【不更新】：拖动滚动条 / 缩放 / 连线期间指针位置若跟着变，
        //    "最近的那个手柄"就会随光标漂移、点亮别的控件 —— 与"手势期间控件定格"的要求冲突。
        if (_canvasInputActive && !_isCanvasScrollDragging && !_isCanvasResizing &&
            !_isCanvasConnecting && !_isCanvasDragging)
        {
            _canvasPointerPos = e.GetCurrentPoint(Viewer).Position;
            _canvasPointerValid = true;
        }

        // ── 白板滚动条拖动：把指针位移映射成滚动偏移 ──
        if (_isCanvasScrollDragging)
        {
            UpdateCanvasScrollDrag(e.GetCurrentPoint(Viewer).Position);
            e.Handled = true;
            return;
        }

        // ── 白板卡片缩放：对角固定，移动角跟随指针 ──
        if (_isCanvasResizing)
        {
            UpdateCanvasResize(e.GetCurrentPoint(Viewer).Position);
            e.Handled = true;
            return;
        }

        // ── 白板连线拖拽：更新橡皮筋与目标吸附 ──
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
            // 队列任务的执行时机在帧内会前后浮动，会让浮层相对卡片滞后/来回跳动。
            SyncCanvasOverlayNow();
            e.Handled = true;
            return;
        }

        // ── 白板悬停（无任何手势时）：连线靠近变强调色；卡片悬停把边框染色，手柄按指针接近度单独显形 ──
        // 编辑态文本拖选（左键按住）时【整体冻结】：不刷悬停/手柄显形、不写根光标（与编辑框通道同款守卫），
        // 否则拖选指针滑过控件带会触发边框控件悬浮显示与鼠标变化（用户报告）。
        bool canvasTextDragSelecting = _canvasEditNodeId != null &&
            e.GetCurrentPoint(Viewer).Properties.IsLeftButtonPressed;
        // 编辑态下 Viewer 通道收到移动 ⇒ 指针在编辑框之外（卡外或【已被挖除的控件命中带】）：
        // ① 清"在卡上"标志，工字作用域轮询下一拍卸下；② 同步最新的指针位置（工字作用域据此判定命中带）；
        // ③ 若已不在编辑卡的边框控件带上，把命中区【还回编辑框】（撤销 OnCanvasEditBoxPointerMoved 的挖除），
        //    鼠标移回文本区即可继续正常编辑。
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
            UpdateCanvasNodeHover(hoverPos);    // 先卡片悬停：下方连线悬浮的"拉线控件优先"判定依赖 _hoverCanvasNodeId
            UpdateCanvasEdgeHover(hoverPos);
            // 手柄（角/边中点/四边中段）悬停时改 Viewer 根光标，使"所有能使用边框控件的情境"
            // 都有鼠标变化；不在任何卡片上则复位为默认（平移）光标。
            // 编辑态同样生效：控件命中圈超出编辑框 bounds 的外半圈 moved 落到 Viewer 通道，根光标若清 null
            // 则这半圈的光标变化缺失 ⇒ 编辑态的光标变化范围比选中态小（用户报告）。编辑框 bounds 内由
            // 编辑框元素级 OverrideCursor 优先接管，两者按指针位置互不干扰。
            bool overCard = _selectedCanvasNodeId != null || _hoverCanvasNodeId != null;
            this.ProtectedCursor = overCard ? ResolveCanvasHoverCursor(hoverPos) : null;
            // 手柄"只显指针最接近的那一个"随指针连续变化：在卡片附近移动时需重绘（8ms 节流，
            // 代价可忽略），否则手柄会停在上一次显形的位置不动。
            if (overCard)
                InvalidateOverlayThrottled();
        }

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
            // 【自愈】物理按键已全部抬起，却仍处在右键缩放手势里 ⇒ 那次 PointerReleased 被别处吞掉了
            // （典型：胶囊按钮吃掉抬起，见 Viewer_PointerPressed 的浮层 chrome 守卫；或松手点在窗口外）。
            // 以【物理按键】为准立刻收尾：右键缩放不捕获指针，等不到"框架通知"，必须自己校验。
            if (!CursorInterop.AnyMouseButtonDown())
            {
                EndRightZoomGesture();
                e.Handled = true;
                return;
            }
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
        _canvasEditControlGesture = false;   // "控件交互"窗口随本次按下-抬起结束
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
            // 编辑态与插入光标闪烁继续（与滚动条分支同款收尾）。
            if (_canvasEditNodeId != null) CanvasEditBox.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        if (_isCanvasConnecting)
        {
            // 同拖动：必须先取状态再 ReleasePointerCapture（释放会同步触发 CaptureLost 清空字段）。
            var from = _connFromNodeId;
            var fromSide = _connFromSide;
            var dropPos = e.GetCurrentPoint(Viewer).Position;   // 释放捕获前取落点
            _isCanvasConnecting = false;
            _connFromNodeId = null;
            _connTargetNodeId = null;
            _connTargetSide = "";
            SyncCanvasOverlayNow();   // 连线结束 ⇒ 恢复卡片悬浮胶囊（若仍有卡片选中）
            this.ReleasePointerCapture(e.Pointer);
            InvalidateOverlay();
            // ⚠ 目标在【释放点当下重新判定】，不再依赖拖动期间累积的 _connTargetNodeId：
            //   拖动中若因失焦 / 指针捕获变化漏掉若干次 move，它会停在上一次的旧值 ⇒ 误判成"连到了别的卡片"而放弃建卡
            //   （用户报告：从卡片内侧拖连线到空白处不建卡、从外侧正常）。判据与拖动期一致：命中【非源】卡片才算目标。
            bool onAnyCard = TryHitCanvasNode(dropPos, out var dropHit, out _, out _, out _) && dropHit != null;
            bool onOtherCard = onAnyCard && !string.Equals(dropHit!.Id, from, StringComparison.Ordinal);
            int dropPage = -1; double dpx = 0, dpy = 0;
            bool dropOnEmptyPage = !onAnyCard &&
                CanvasViewportToPage(dropPos, out dropPage, out dpx, out dpy);
            // 落在另一张卡片 → 建立连线；落在空白页 → 就地新建卡片并连线；落空 / 落在自身 → 取消。
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
                // 落点在合法页且不在任何卡片上 → 以落点为中心放一张默认尺寸卡片（clamp 页内）。
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
            // 放在两个 Invoke【之后】：落在空白页时会由 MainPage 接管新卡片的编辑态（CanvasEditBox 被重新
            // 布局到新卡），这里再聚焦只是对同一个宿主的幂等重申，不会和 MainPage 的接管抢焦点。
            if (_canvasEditNodeId != null) CanvasEditBox.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        if (_isCanvasDragging)
        {
            // 必须先取状态再 ReleasePointerCapture：释放捕获会【同步】触发 PointerCaptureLost，
            // 而后者会把 _canvasDragNode/_canvasDragPage 清空 —— 先释放就会读到 null，
            // 导致拖动完成事件永远不发（表现为：卡片能拖动，但移动不入撤销历史）。
            var node = _canvasDragNode;
            int page = _canvasDragPage;
            double ox = _canvasDragOrigX, oy = _canvasDragOrigY;
            _isCanvasDragging = false;
            _canvasDragNode = null;
            _canvasDragPage = -1;
            // Obsidian 行为收尾：单击已选中卡且全程未拖动（待定仍挂起、且 DoubleTapped 尚未抢先进入编辑）
            // → 进入编辑态，光标落点击点。先发 DragCompleted（入史判定），再进编辑。
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
            // _isCanvasDragging 仍为 true ⇒ 自绘光标被 AnyCanvasGestureActive 抑制，松手前后的两次重绘
            // 都画不出光标；零位移拖动完成又不触发重绘 ⇒ 光标要等下一次闪烁 tick（≤530ms）才现身，
            // 表现为"真光标已落位、自绘光标慢半拍"。手势状态此刻已清，补绘即现。
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
        _canvasEditControlGesture = false;   // 捕获丢失 ⇒ "控件交互"窗口收尾（防标志残留导致后续失焦被误判为控件交互）
        // 滚动条拖动被系统打断：偏移已实时生效，直接清状态即可（无需回滚）。
        _isCanvasScrollDragging = false;
        _canvasScrollDragNodeId = null;
        UnlockCanvasCursor();                       // 捕获丢失 ⇒ 解锁画布手势光标（覆盖滚动/连线，恢复框架默认）
        // 缩放拖拽被系统打断：尺寸已实时写回模型 → 照常发完成事件收尾（MainPage 据此入历史）。
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
        // 连线拖拽被系统打断（窗口失焦等）：直接取消，不建立连线。
        if (_isCanvasConnecting)
        {
            _isCanvasConnecting = false;
            _connFromNodeId = null;
            _connTargetNodeId = null;
            _connTargetSide = "";
            InvalidateOverlay();
            SyncCanvasOverlayNow();   // 系统打断连线（失焦等）⇒ 恢复胶囊
        }
        // 白板卡片拖动被系统打断：位置已实时写回模型 → 照常发完成事件收尾（MainPage 据此入历史）。
        // 被打断的手势不再视为"单击"：撤销进编辑待定（不进入编辑态）。
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
        // 指针离开视图 → 清掉白板悬停与指针位置，避免手柄/染色边框残留在「指针已不在」的卡片上。
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
