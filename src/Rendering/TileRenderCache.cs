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
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace GlanceRX.Rendering;

// ============================================================================
// TileRenderCache — 1:1 port of sumatrapdf-master/src/RenderCache.{h,cpp}
// (the rendering + memory-management engine behind SumatraPDF's DisplayModel).
//
// 抄的是 Sumatra 的【真身】，不是简化件：
//   * 瓦片：页面按 2^res × 2^res 划分；GetTileRes 由 zoomReal ÷ maxTileSize
//     (屏幕尺寸) 的几何均值定层级 → 放大自动升 res 重渲染，缩小自动降。
//   * 按视口渲染：Paint 从 (0,0,0) 瓦片 BFS，只画与视口相交的瓦片；
//     renderMissing 只对目标层级瓦片发请求（viewport-driven）。
//   * 按视口控内存：gConserveMemory → 每次 Paint 后 FreePage(其他分辨率瓦片)
//     + FreeNotVisible()（不可见页/不可见瓦片即释放）；maxTileSize=屏幕尺寸
//     保证单张瓦片位图永不超约一屏；缓存条目 ≤ kMaxBitmapsCached=128。
//   * 渲染队列 kMaxPageRequests=8（LIFO，同参重排队到队尾优先）；
//     渲染线程惰性生成，上限 min(gMaxRenderThreads=8, cores)，硬顶 32。
//   * 预测链：kMaxPredictiveRequests=4，一次一页 chained（不淹没队列），
//     锚点页滚出视口即停。
//
// 对 C++ 源的必要对应（其余逐行对应，见各方法注释）：
//   * DisplayModel* dm → ITileRenderHost（视图自身实现）。
//   * RecursiveMutex → lock + "Locked" 后缀内部方法（C# lock 不可重入）。
//   * HDC/BlitPixmap → ITileRenderHost.DrawTileBitmap（Win2D DrawImage，
//     GPU 位图惰性挂在 entry.Gpu —— Sumatra 是 GDI 直 blit CPU pixmap，
//     Win2D 必须经 CanvasBitmap；CPU 位图仍是唯一缓存真身，与 Sumatra 一致）。
//   * AbortCookie：MuPDF 局部渲染是同步 FFI，无法中途打断 → 保留 req.abort
//     标志，渲染完成后丢弃结果（Sumatra 的 darkModeEpoch 过期丢弃同理）。
//   * 坐标：与 Sumatra 【完全同单位】——整个引擎（tile 矩形 / pageOnScreen /
//     viewPort / maxTileSize / source 索引）一律物理像素，zoomReal = 每 PDF 点
//     物理像素数。不得在本文件内出现 DIP。DIP 只存在于外壳的两个边界：
//     ITileRenderHost 的实现（取出时 ×DpiScale）与 DrawTileBitmap（target 传
//     画布前 ÷DpiScale；source 保持位图像素）。
//   * 旋转：Sumatra 把 rotation 烘进出图（RenderPage 用 viewctm(pageNo,zoom,rotation)），
//     故其 tile 矩形/pageOnScreen/pageRect 全在旋转后帧，且缓存按 rotation 分键。
//     Glance 的原生出图接口只接受【未旋转】device 空间 → 引擎内部统一用未旋转页帧，
//     旋转由外壳在 DrawingSession 上补一次（与标注层 ApplyPageTransform 同约定），
//     缓存恒按 rotation=0 分键。代价 = 所有 tile 几何都要把视口换算回页帧
//     （PageFrameViewPort）；收益 = 转屏不需要重渲染。
//     ⚠ 历史坑（本文件曾因此出现"右侧缺失 + 发糊 + 瓦片错位"三个症状）：
//       把 tileOnScreen 做成 DIP、却把 DIP 偏移直接当位图源索引，且缺整数对齐
//       → factor 恒为 0.9998 之类，走 Linear 重采样；源索引又少了 DpiScale 倍。
//       修复方式 = 回到 Sumatra 的单位体系，不做任何"就地换算"。
// ============================================================================

/// <summary>kRenderDelayUndefined / kRenderDelayFailed（RenderCache.h）。</summary>
internal static class TileRenderDelay
{
    public const int Undefined = int.MaxValue - 1;
    public const int Failed = int.MaxValue - 2;
}

/// <summary>RenderCache.h TilePosition：res/row/col 标识一颗瓦片。</summary>
internal readonly struct TilePosition : IEquatable<TilePosition>
{
    public const ushort InvalidRes = ushort.MaxValue;
    public TilePosition(ushort res, ushort row, ushort col) { Res = res; Row = row; Col = col; }
    public ushort Res { get; }
    public ushort Row { get; }
    public ushort Col { get; }
    public bool Equals(TilePosition other) => Res == other.Res && Row == other.Row && Col == other.Col;
    public override bool Equals(object? o) => o is TilePosition t && Equals(t);
    public override int GetHashCode() => (Res << 16) ^ (Row << 8) ^ Col;
}

/// <summary>RenderCache.h BitmapCacheEntry：一块已渲染瓦片（CPU 位图为真身；
/// Gpu 是 Glance 外壳的 Win2D 必需物，惰性创建、随条目释放）。</summary>
internal sealed class BitmapCacheEntry
{
    public object Dm = null!;
    public int PageNo;
    public int Rotation;
    public float Zoom;
    public TilePosition Tile;

    // ---- Pixmap* 对应物：native BGRA 缓冲（stride=W*4）----
    // ⚠ 与 Sumatra 1:1 的关键：Sumatra 的瓦片像素是 MuPDF 分配的 native Pixmap，
    //   由 FreePixmap() 确定性释放，GC 完全不参与。Glance 原先用托管 byte[]（MB 级 LOH）：
    //   每块瓦片一次 LOH 分配 → LOH 不可压缩 → GC 提交段只涨不落（实测 13MB 棘轮到 676MB）。
    //   现在同样持有 native 缓冲：分配在 native 堆（游离即被 malloc 复用），释放走 memory_free。
    public IntPtr Pixels;
    public int PixelsLen;                          // memory_free 所需长度（= BitmapW*BitmapH*4）
    public int BitmapW, BitmapH;
    public Microsoft.Graphics.Canvas.CanvasBitmap? Gpu;   // 外壳扩展：惰性 GPU 副本（Win2D 必需）
    public int GpuBytes;                                  // 该 GPU 副本的字节数（诊断记账用；UMA 上即系统内存占用）
    public bool OutOfDate;
    public int Refs = 1;
    public int CacheIdx = -1;

    /// <summary>条目是否还有可画的像素（native 缓冲 或 GPU 副本）。
    /// GPU 副本建好后会释放 native 侧（见 ReleaseCpuBitmap），故不能只看 Pixels。</summary>
    public bool HasBitmap => Pixels != IntPtr.Zero || Gpu != null;

    /// <summary>FreePixmap 对应物：释放 native 瓦片缓冲。幂等（置零后重复调用无害）。
    /// ⚠ 必须把 PixelsLen 一并置零 —— 否则「已释放」的瓦片会被误判为仍持有 native 缓冲。</summary>
    public void FreeCpuPixels(ITileRenderHost host)
    {
        var p = Pixels;
        if (p == IntPtr.Zero) return;
        int len = PixelsLen;
        Pixels = IntPtr.Zero;
        PixelsLen = 0;
        host.FreeTilePixels(p, len);
    }

    /// <summary>【外壳扩展】GPU 副本就位后丢弃 native 瓦片缓冲。
    /// Sumatra 是 GDI 直 blit CPU pixmap，所以它必须留着 Pixmap；Glance 走 Win2D，
    /// 画到屏上的是 CanvasBitmap，native 那份从此没有任何读取点 —— 留着就是整整一倍冗余。
    /// 释放后条目有效性以 Gpu 为准（HasBitmap）；若 Gpu 之后被设备重建作废（DropGpuResources），
    /// 该条目已无像素可画 → 由 DropGpuResources 直接淘汰，绝不留下"空条目卡住瓦片"。</summary>
    public void ReleaseCpuBitmap(ITileRenderHost host)
    {
        if (Gpu != null) FreeCpuPixels(host);
    }
}

/// <summary>RenderCache.h PageRenderRequest：渲染队列条目（复用结构）。</summary>
internal sealed class PageRenderRequest
{
    public object Dm = null!;
    public int PageNo;
    public int Rotation;
    public float Zoom;
    public TilePosition Tile;

    // GetTileRectDevice 对应物：瓦片在整页设备空间里的像素矩形（x0,y0,x1,y1）。
    public int DevW, DevH, X0, Y0, X1, Y1;

    public bool Abort;
    public ulong Timestamp;
    public int ErrorCode;   // 0 = success

    /// <summary>FitzAbortCookie 对应物（GlanceRXCookie 24B 共享块）。入队时 Zero（= Sumatra
    /// newRequest-&gt;abortCookie = nullptr）；worker 开渲前分配并存入 _curReqs[threadIdx]
    /// 同一对象 → AbortCurrentRequestLocked 可见并跨线程写 abort=1；渲染返回且
    /// _curReqs 置空后由 worker 持 _requestAccess 释放（与 aborter 互斥，无 UAF）。</summary>
    public IntPtr Cookie;

    // 预测链（kMaxPredictiveRequests）
    public int PredictiveOriginPageNo;
    public int NPredictiveRequests;
    public int[] PredictiveRequests = new int[TileRenderCache.KMaxPredictiveRequests];

    public Action<PageRenderRequest>? FinishedCb;
}

/// <summary>DisplayModel 的渲染侧对应物（GpuPageView 实现）。
/// ⚠ 单位铁律：本接口的矩形/尺寸一律【物理像素】，与 Sumatra 的 RenderCache 完全同单位
/// （Sumatra 的 pageOnScreen / viewPort / tile 矩形全是物理像素，没有 DIP 层）。
/// DIP↔像素 的换算只允许发生在外壳的两个边界上：本接口的实现里（取出时 ×DpiScale），
/// 以及 DrawTileBitmap 里（画布 target 传入前 ÷DpiScale）。缓存内部【不得】再做任何换算。</summary>
internal interface ITileRenderHost
{
    int PageCount { get; }
    float PageWidthPt(int pageNo);
    float PageHeightPt(int pageNo);
    /// <summary>dm->GetZoomReal()：每 PDF 点的物理像素数（= z×96/72×DpiScale）。</summary>
    float ZoomReal(int pageNo);
    /// <summary>dm->GetViewPort().Size()：物理像素。</summary>
    Size ViewPortSize { get; }
    /// <summary>pageInfo->pageOnScreen：视口坐标，物理像素。</summary>
    Rect PageOnScreen(int pageNo);
    bool PageVisible(int pageNo);
    bool PageVisibleNearby(int pageNo);
    /// <summary>外壳呈现层旋转角（0/90/180/270）。出图与全部 tile 几何恒在【未旋转页帧】，
    /// 外壳负责把这份几何绕页中心转 rot 度画到屏幕上；本引擎只用它换算视口外接矩形
    /// （PageFrameViewPort）与 GetTileRes 的 fit 朝向。因为位图与旋转无关，
    /// 转屏【不作废缓存】（Sumatra 的位图已烘入 rotation，必须按 rotation 分键）。</summary>
    int Rotation { get; }
    bool PauseRendering { get; }
    /// <summary>DisplayModel::RenderFinished → Repaint（已在 UI 线程）。</summary>
    void Repaint();
    void PostToUi(Action action);
    /// <summary>render thread 的渲染入口（= Sumatra `engine->RenderPage(RenderPageArgs)`）。
    /// 入参 devW/devH/x0..y1 均为物理像素（与 mu_render_page_region_bgra 一致）。
    /// cookie = FitzAbortCookie 对应物（GlanceRXCookie 分配的 24B fz_cookie 兼容块），
    /// MuPDF 在 display-list 操作间隙自查中止（EngineMupdf.cpp:6542）。
    /// 【同步】渲染一块瓦片，返回【拥有所有权的 native BGRA 缓冲】——不拷贝、也不释放；
    /// 调用方负责把所有权交给缓存，或在丢弃时用 <see cref="FreeTilePixels"/> 释放。
    /// Sumatra 的 render thread 就是同步调用引擎的，这里刻意不做任何 Task/线程池包装
    /// （包一层 Task.Run + 信号量 = 每块瓦片多一个 Task、多一次线程池跳转、多一组阻塞等待）。</summary>
    (IntPtr Data, int Length, int W, int H) RenderTile(
        IntPtr cookie, int pageNo, int devW, int devH, int x0, int y0, int x1, int y1);

    /// <summary>FreePixmap 对应物：释放 RenderTile 返回的 native 缓冲。</summary>
    void FreeTilePixels(IntPtr data, int length);

    /// <summary>BlitPixmapRegion 对应物（仅 UI 线程 Draw 帧内调用）。
    /// target = 视口物理像素矩形；source = 位图内像素矩形（源索引必须用位图像素，
    /// 不能是 DIP —— 这是 Win2D DrawImage 的源/目标异单位陷阱）。</summary>
    void DrawTileBitmap(BitmapCacheEntry entry, Rect target, Rect source);
}


internal sealed class TileRenderCache : IDisposable
{
    public const int KMaxPageRequests = 8;
    public const int KMaxBitmapsCached = 128;
    public const int KMaxRenderThreads = 32;
    public const int KMaxPredictiveRequests = 4;
    private const int KInvalidZoom = -1;

    // RenderCache.cpp：static bool gConserveMemory = true（CONSERVE_MEMORY 定义）。
    private const bool GConserveMemory = true;
    // RenderCache.cpp：int gMaxRenderThreads = 8。
    private const int GMaxRenderThreads = 8;

    private readonly object _cacheAccess = new();
    private readonly object _requestAccess = new();

    private readonly BitmapCacheEntry?[] _cache = new BitmapCacheEntry?[KMaxBitmapsCached];
    private int _cacheCount;
    private readonly PageRenderRequest[] _requests = new PageRenderRequest[KMaxPageRequests];
    private int _requestCount;

    private readonly ITileRenderHost _host;

    // RenderCache 构造：maxTileSize = {SM_CXSCREEN, SM_CYSCREEN}（÷DpiScale → DIP）。
    public Size MaxTileSize;
    public int NTileSizeReductions;

    // 渲染线程（惰性生成）：startRendering 信号量 + shouldExit。
    private readonly SemaphoreSlim _startRendering = new(0, int.MaxValue);
    private volatile bool _shouldExit;
    private readonly Thread?[] _renderThreads = new Thread?[KMaxRenderThreads];
    private readonly PageRenderRequest?[] _curReqs = new PageRenderRequest?[KMaxRenderThreads];
    private int _nRenderThreads;
    private int _maxRenderThreads;
    private int _idleThreads;

    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    public TileRenderCache(ITileRenderHost host, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        // RenderCache 构造：maxTileSize = {SM_CXSCREEN, SM_CYSCREEN}（物理像素，原样）。
        MaxTileSize = new Size(Win32Metrics.ScreenWidth, Win32Metrics.ScreenHeight);

        int numCores = Environment.ProcessorCount;
        _maxRenderThreads = Math.Max(GMaxRenderThreads, numCores);
        _maxRenderThreads = Math.Min(_maxRenderThreads, KMaxRenderThreads);
    }

    /// <summary>GetSystemMetrics 对应物（构造期一次性）。</summary>
    private static class Win32Metrics
    {
        public static readonly int ScreenWidth = GetSystemMetrics(0);   // SM_CXSCREEN
        public static readonly int ScreenHeight = GetSystemMetrics(1);  // SM_CYSCREEN

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
    }

    // ---------------- Find / Exists / Drop（RenderCache.cpp 161-236） ----------------

    /// <summary>RenderCache::Find。zoom=KInvalidZoom → 任意 zoom；tile=null → 任意瓦片。</summary>
    private BitmapCacheEntry? FindLocked(object dm, int pageNo, int rotation, float zoom, TilePosition? tile)
    {
        rotation = NormalizeRotation(rotation);
        for (int i = 0; i < _cacheCount; i++)
        {
            var e = _cache[i]!;
            if (dm == e.Dm && pageNo == e.PageNo && rotation == e.Rotation &&
                (KInvalidZoom == zoom || zoom == e.Zoom) &&
                (tile is null || e.Tile.Equals(tile.Value)))
            {
                e.Refs++;
                return e;
            }
        }
        return null;
    }

    public bool Exists(object dm, int pageNo, int rotation, float zoom, TilePosition? tile)
    {
        lock (_cacheAccess)
        {
            var entry = FindLocked(dm, pageNo, rotation, zoom, tile);
            if (entry != null) DropCacheEntryLocked(entry);
            return entry != null;
        }
    }

    /// <summary>RenderCache::DropCacheEntry（refs 归零才真删；swap-remove 保持 cacheIdx 一致）。</summary>
    private bool DropCacheEntryLocked(BitmapCacheEntry entry)
    {
        int idx = entry.CacheIdx;
        if (idx < 0 || idx >= _cacheCount) return false;
        entry.Refs--;
        if (entry.Refs > 0) return false;

        ReleaseEntryPayload(entry);

        _cache[idx] = null;
        int lastIdx = _cacheCount - 1;
        if (lastIdx >= 0 && idx != lastIdx)
        {
            var moved = _cache[lastIdx]!;
            moved.CacheIdx = idx;
            _cache[idx] = moved;
            _cache[lastIdx] = null;
        }
        _cacheCount--;
        return true;
    }

    private bool DropCacheEntryIfNotUsedLocked(BitmapCacheEntry entry)
    {
        if (entry.Refs > 1) return false;
        return DropCacheEntryLocked(entry);
    }

    /// <summary>GPU 位图释放必须回 UI 线程（Draw 帧可能仍引用）。
    /// 诊断记账放在真正 Dispose() 之后 —— 这一列的意义就是量"已 Drop、还在等 Dispose"的纹理。</summary>
    private void DisposeGpuLocked(BitmapCacheEntry entry)
    {
        var gpu = entry.Gpu;
        int bytes = entry.GpuBytes;
        entry.Gpu = null;
        entry.GpuBytes = 0;
        if (gpu != null)
            _host.PostToUi(() =>
            {
                try { gpu.Dispose(); } catch { }
            });
    }

    /// <summary>条目彻底作废时释放两侧像素：GPU 副本 + native 瓦片缓冲（= FreePixmap）。
    /// ⚠ 与 <see cref="DisposeGpuLocked"/> 的区别：它【只】释放 GPU 侧，用于设备重建
    /// （那时 native 侧要留下当回退，不能一起丢）。</summary>
    private void ReleaseEntryPayload(BitmapCacheEntry entry)
    {
        DisposeGpuLocked(entry);
        entry.FreeCpuPixels(_host);
    }

    /// <summary>外壳扩展：设备重建时作废全部 GPU 位图。仍持 CPU 真身的条目与 Sumatra 一致地保留；
    /// 已被 ReleaseCpuBitmap 掏空的条目此时已无像素可画 → 淘汰（否则会留下"有键无图"的空条目）。</summary>
    public void DropGpuResources()
    {
        lock (_cacheAccess)
        {
            for (int i = _cacheCount - 1; i >= 0; i--)
            {
                var e = _cache[i]!;
                DisposeGpuLocked(e);
                if (e.Pixels == IntPtr.Zero) DropCacheEntryLocked(e);
            }
        }
    }

    // ---------------- Add / FreeIfFull（RenderCache.cpp 238-301） ----------------

    /// <summary>RenderCache.cpp FreeIfFull：缓存满时先淘汰同 dm 的不可见页瓦片，再淘汰其他 dm。</summary>
    private bool FreeIfFullLocked(PageRenderRequest req)
    {
        int n = _cacheCount;
        if (n < KMaxBitmapsCached) return true;

        object dm = req.Dm;
        for (int i = 0; i < n; i++)
        {
            var entry = _cache[i]!;
            if (entry.Dm == dm && !_host.PageVisibleNearby(entry.PageNo))
            {
                if (DropCacheEntryIfNotUsedLocked(entry)) return true;
            }
        }
        for (int i = 0; i < n; i++)
        {
            var entry = _cache[i]!;
            if (entry.Dm == dm) continue;
            if (DropCacheEntryIfNotUsedLocked(entry)) return true;
        }
        return false;
    }

    /// <summary>RenderCache::Add：先 FreePage 同瓦片（不同 zoom/rotation），再腾坑，再入缓存。
    /// 返回 true = 缓存已接管 native 缓冲所有权；false = 腾不出坑，调用方须自行 FreeTilePixels。</summary>
    private bool AddLocked(PageRenderRequest req, IntPtr pixels, int len, int w, int h)
    {
        req.Rotation = NormalizeRotation(req.Rotation);

        // 同一瓦片可能已有不同 zoom/rotation 的旧图 → 先释放。
        FreePageLocked(req.Dm, req.PageNo, req.Tile);

        bool hasSpace = FreeIfFullLocked(req);
        if (!hasSpace || _cacheCount >= KMaxBitmapsCached)
        {
            return false;   // 腾不出坑：丢弃本图（Sumatra 同路径 → 调用方 FreePixmap）。
        }

        var entry = new BitmapCacheEntry
        {
            Dm = req.Dm,
            PageNo = req.PageNo,
            Rotation = req.Rotation,
            Zoom = req.Zoom,
            Tile = req.Tile,
            Pixels = pixels,
            PixelsLen = len,
            BitmapW = w,
            BitmapH = h,
            CacheIdx = _cacheCount
        };
        _cache[_cacheCount] = entry;
        _cacheCount++;
        return true;
    }

    // ---------------- 瓦片几何（RenderCache.cpp 303-355） ----------------

    /// <summary>GetTileRect：mediabox(pt) 内的瓦片矩形；row 以 PDF y-up 语义（源码原样翻转）。</summary>
    private static void GetTileRectPt(double mbX, double mbY, double mbW, double mbH, TilePosition tile,
        out double x, out double y, out double w, out double h)
    {
        double div = 1UL << tile.Res;
        w = mbW / div;
        h = mbH / div;
        x = mbX + tile.Col * w;
        y = mbY + ((div - tile.Row - 1) * h);
    }

    /// <summary>GetTileRectDevice 对应物（RenderCache.cpp:314）：瓦片设备像素矩形。
    /// zoom 已是「每 PDF 点物理像素数」，故这里就是 Sumatra 的
    /// `engine->Transform(mediabox, pageNo, zoom, rotation=0)`（Glance 出图恒未旋转，
    /// 与 mu_render_page_region_bgra 的 unrotated device space 对齐）。</summary>
    private void GetTileRectDevice(int pageNo, float zoom, TilePosition tile,
        out int devW, out int devH, out int x0, out int y0, out int x1, out int y1)
    {
        double ptW = _host.PageWidthPt(pageNo), ptH = _host.PageHeightPt(pageNo);
        double dens = zoom;   // px/point
        devW = Math.Max(1, (int)Math.Round(ptW * dens));
        devH = Math.Max(1, (int)Math.Round(ptH * dens));
        if (tile.Res == 0 || tile.Res == TilePosition.InvalidRes)
        {
            x0 = 0; y0 = 0; x1 = devW; y1 = devH;
        }
        else
        {
            GetTileRectPt(0, 0, ptW, ptH, tile, out double tx, out double ty, out double tw, out double th);
            x0 = (int)Math.Round(tx * dens);
            y0 = (int)Math.Round(ty * dens);
            x1 = (int)Math.Round((tx + tw) * dens);
            y1 = (int)Math.Round((ty + th) * dens);
            x0 = Math.Clamp(x0, 0, devW); x1 = Math.Clamp(x1, 0, devW);
            y0 = Math.Clamp(y0, 0, devH); y1 = Math.Clamp(y1, 0, devH);
        }
    }

    /// <summary>GetTileOnScreen（RenderCache.cpp:328）1:1：tileOnScreen = 瓦片设备矩形 + pageOnScreen。
    /// Sumatra 两端都是【整数】Rect（GetTileRectDevice 里有 .Round()，pageOnScreen 是 int Rect），
    /// 于是目标层瓦片的 factor 恰好 == 1.0 → BlitPixmapRegion 走 1:1 直拷。
    /// ⚠ 这条整数对齐必须保住：一旦 tileOnScreen 带小数，factor 就变成 0.9998 之类，
    /// 每次上屏都会被 Linear 重采样一次 → 目标分辨率明明够了画面仍然发糊（实测症状）。
    ///
    /// 旋转（Sumatra 不需要这一步的原因）：Sumatra 把 rotation 烘进出图 ——
    /// `EngineMupdf::RenderPage` 用 `viewctm(pageNo, zoom, rotation)` 光栅化，产出的位图
    /// 【本身已旋转】，所以它的 tile 矩形 / pageOnScreen / pageRect 全在旋转后的帧里，
    /// 本函数只需相加。Glance 出图恒未旋转（`mu_render_page_region_bgra` 只接受未旋转
    /// device 空间），若照抄就会出现「位图内容不随旋转 + 缩放时瓦片错乱」。
    /// → 解决办法：引擎内部统一改用【未旋转页帧】，pageOnScreen 传未旋转页矩形，
    ///   旋转由外壳在 DrawingSession 上补一次（与标注层 ApplyPageTransform 同一约定）。
    ///   于是本函数与 Sumatra 逐字等价（rotation 恒为 0），且旋转页【无需重渲染】。</summary>
    private Rect GetTileOnScreen(int pageNo, float zoom, TilePosition tile, Rect pageOnScreen)
    {
        GetTileRectDevice(pageNo, zoom, tile, out _, out _, out int dx0, out int dy0, out int dx1, out int dy1);
        return new Rect(dx0 + pageOnScreen.X, dy0 + pageOnScreen.Y,
            Math.Max(0, dx1 - dx0), Math.Max(0, dy1 - dy0));
    }

    /// <summary>
    /// 视口在【未旋转页帧】里的最小外接矩形（物理像素）。
    /// Sumatra 没有对应物：它的 tile / pageOnScreen 本来就在旋转后帧，直接与
    /// `dm->GetViewPort()` 相交即可（IsTileVisible / Paint 的 bounds 都是那么写的）。
    /// Glance 的 tile 在未旋转页帧、视口在屏幕帧，两者差一个「绕页中心转 +rot」
    /// → 这里把视口按 -rot 转回页帧取 AABB。
    /// AABB ⊇ 旋转后的视口，故用它做可见性判定【只会多画、绝不漏画】；
    /// rot==0 时即视口本身（与 Sumatra 完全一致，零行为变化）。
    /// ⚠ 它的原点不是 (0,0)，rot!=0 时可能落在页面中部 —— 只能当可见性判据，
    ///    不能无条件当绘制裁剪区（Sumatra 的 bounds 是屏幕脏矩形，两者不是一回事）。
    /// </summary>
    private Rect PageFrameViewPort(Rect pageOnScreen)
    {
        int rot = _host.Rotation % 360;
        var vp = _host.ViewPortSize;
        double vw = vp.Width, vh = vp.Height;
        if (rot == 0 || vw <= 0 || vh <= 0) return new Rect(0, 0, vw, vh);

        // 旋转枢轴 = 页中心（与外壳 Draw 里压在 DrawingSession 上的那个枢轴同一个点）。
        double cx = pageOnScreen.X + pageOnScreen.Width / 2.0;
        double cy = pageOnScreen.Y + pageOnScreen.Height / 2.0;
        double rad = -rot * Math.PI / 180.0;
        double cs = Math.Cos(rad), sn = Math.Sin(rad);
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        void Accum(double x, double y)
        {
            double ox = x - cx, oy = y - cy;
            double rx = cx + ox * cs - oy * sn;
            double ry = cy + ox * sn + oy * cs;
            if (rx < minX) minX = rx;
            if (rx > maxX) maxX = rx;
            if (ry < minY) minY = ry;
            if (ry > maxY) maxY = ry;
        }
        Accum(0, 0); Accum(vw, 0); Accum(vw, vh); Accum(0, vh);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>IsTileVisible（fuzz 语义原样：0.5/2.0 扩边）。</summary>
    private bool IsTileVisible(int pageNo, TilePosition tile, double fuzz)
    {
        float zoom = _host.ZoomReal(pageNo);
        Rect r = _host.PageOnScreen(pageNo);
        if (r.IsEmpty) return false;
        // Sumatra 的 pi->pageOnScreen 恒为整数 Rect；取整后与 Paint / 外壳旋转枢轴同帧同值。
        r = new Rect(Math.Round(r.X), Math.Round(r.Y), Math.Round(r.Width), Math.Round(r.Height));
        Rect tileOnScreen = GetTileOnScreen(pageNo, zoom, tile, r);
        double dx = tileOnScreen.Width * fuzz * 0.5;
        double dy = tileOnScreen.Height * fuzz * 0.5;
        tileOnScreen = new Rect(tileOnScreen.X - dx, tileOnScreen.Y - dy,
            tileOnScreen.Width * (fuzz + 1), tileOnScreen.Height * (fuzz + 1));
        tileOnScreen.Intersect(PageFrameViewPort(r));
        return !tileOnScreen.IsEmpty;
    }

    // ---------------- Free*（RenderCache.cpp 357-411） ----------------

    /// <summary>RenderCache::FreePage：释放某页全部瓦片，或按 tile 语义（row=-1 → 其他分辨率）。</summary>
    private void FreePageLocked(object dm, int pageNo, TilePosition tile)
    {
        for (int i = _cacheCount - 1; i >= 0; i--)
        {
            var entry = _cache[i]!;
            bool shouldFree = entry.Dm == dm && entry.PageNo == pageNo;
            if (shouldFree)
            {
                shouldFree = entry.Tile.Equals(tile) ||
                             tile.Row == ushort.MaxValue && entry.Tile.Res > 0 && entry.Tile.Res != tile.Res ||
                             tile.Row == ushort.MaxValue && entry.Tile.Res == 0 && entry.OutOfDate;
            }
            if (shouldFree) DropCacheEntryIfNotUsedLocked(entry);
        }
    }

    private void FreeForDisplayModelLocked(object dm)
    {
        for (int i = _cacheCount - 1; i >= 0; i--)
        {
            var entry = _cache[i]!;
            if (entry.Dm == dm) DropCacheEntryIfNotUsedLocked(entry);
        }
    }

    /// <summary>RenderCache::FreeForDisplayModel（换文档/关视图）。</summary>
    public void FreeForDisplayModel(object dm)
    {
        lock (_cacheAccess) FreeForDisplayModelLocked(dm);
    }

    /// <summary>
    /// RenderCache::KeepForDisplayModel（RenderCache.cpp:415）1:1：重载文档时保留可见页的
    /// 缓存位图以免闪烁，同时把 zoom 置 kInvalidZoom、outOfDate 置 true，保证这些页最终会
    /// 重新渲染。⚠ Glance 目前是「换文档即 ShutdownRenderCache + CreateRenderCache」，
    /// 本方法暂无调用点（保留 = 1:1 完整性 + 是 OutOfDate 在 Sumatra 里的唯一写入口）。
    /// </summary>
    public void KeepForDisplayModel(object oldDm, object newDm)
    {
        lock (_cacheAccess)
        {
            for (int i = 0; i < _cacheCount; i++)
            {
                var entry = _cache[i]!;
                if (entry.Dm != oldDm) continue;
                if (_host.PageVisible(entry.PageNo)) entry.Dm = newDm;
                entry.Zoom = KInvalidZoom;
                entry.OutOfDate = true;
            }
        }
    }

    /// <summary>RenderCache::FreeNotVisible：不可见页 + res&gt;1 且不可见（fuzz 2.0）的瓦片。</summary>
    public void FreeNotVisible()
    {
        lock (_cacheAccess)
        {
            for (int i = _cacheCount - 1; i >= 0; i--)
            {
                var entry = _cache[i]!;
                bool shouldFree = !_host.PageVisibleNearby(entry.PageNo);
                if (!shouldFree && entry.Tile.Res > 1)
                    shouldFree = !IsTileVisible(entry.PageNo, entry.Tile, 2.0);
                if (shouldFree) DropCacheEntryIfNotUsedLocked(entry);
            }
        }
    }

    // ---------------- GetTileRes / GetMaxTileRes / ReduceTileSize（455-530） ----------------

    /// <summary>
    /// RenderCache::GetTileRes 1:1：factorAvg = sqrt(fw×fh)（pixelbox/maxTileSize 几何均值），
    /// fit-width/fit-page 或整页小于视口时减半（Glance 无 zoomVirtual 概念，fit 态被几何条件
    /// 覆盖：fit-width 时 pixelbox.dx == viewPort.dx）；res = ceil(log2(factorAvg))，上限 30。
    ///
    /// ⚠ 本函数【不得】依赖旋转角。Sumatra 比的是旋转后的 pixelbox 与 viewport，那是因为它的
    /// 位图把 rotation 烘进出图（pixelbox 与 pageOnScreen 本就同帧）；Glance 的位图/瓦片恒在
    /// 未旋转页帧，必须用未旋转 pixelbox 比未旋转 viewport。一旦让 res 随角度变化，转到一个
    /// 角度就会把整个【目标层】判废（targetRes 变 → 该角度全部瓦片 miss → 替身发糊 → 重出），
    /// 表现为"每个角度首次进入都要重渲染一遍、且与 0° 各自独立"。
    /// （几何均值 sqrt(fw×fh) 对 W/H 交换本来就不变，唯一在意朝向的是下面那条 fit 条件。）
    /// </summary>
    public ushort GetTileRes(int pageNo, float? zoom = null)
    {
        double ptW = _host.PageWidthPt(pageNo), ptH = _host.PageHeightPt(pageNo);
        float zv = zoom ?? _host.ZoomReal(pageNo);
        var viewPort = _host.ViewPortSize;

        double pixelW = ptW * zv, pixelH = ptH * zv;   // 物理像素（与 maxTileSize 同单位）

        double factorW = pixelW / (MaxTileSize.Width + 1);
        double factorH = pixelH / (MaxTileSize.Height + 1);
        double factorAvg = Math.Sqrt(factorW * factorH);

        if (pixelW <= viewPort.Width || pixelH < viewPort.Height)
            factorAvg /= 2.0;

        ushort res = 0;
        if (factorAvg > 1.5)
            res = (ushort)Math.Ceiling(Math.Log(factorAvg) / Math.Log(2.0));
        return Math.Min(res, (ushort)30);
    }

    public ushort GetMaxTileRes(int pageNo, int rotation)
    {
        lock (_cacheAccess)
        {
            ushort maxRes = 0;
            for (int i = 0; i < _cacheCount; i++)
            {
                var e = _cache[i]!;
                if (e.Dm == _host && e.PageNo == pageNo && e.Rotation == NormalizeRotation(rotation))
                    maxRes = Math.Max(e.Tile.Res, maxRes);
            }
            return maxRes;
        }
    }

    /// <summary>RenderCache::ReduceTileSize：maxTileSize 减半并清空全部缓存/请求（内存压力兜底）。</summary>
    public bool ReduceTileSize()
    {
        if (MaxTileSize.Width < 200 || MaxTileSize.Height < 200) return false;

        lock (_requestAccess)
        {
            lock (_cacheAccess)
            {
                if (MaxTileSize.Width > MaxTileSize.Height)
                    MaxTileSize = new Size(MaxTileSize.Width / 2, MaxTileSize.Height);
                else
                    MaxTileSize = new Size(MaxTileSize.Width, MaxTileSize.Height / 2);
                NTileSizeReductions++;

                for (int i = _cacheCount - 1; i >= 0; i--) ReleaseEntryPayload(_cache[i]!);
                Array.Clear(_cache);
                _cacheCount = 0;
                _requestCount = 0;
                for (int i = 0; i < _nRenderThreads; i++) AbortCurrentRequestLocked(i);
            }
        }
        return true;
    }

    // ---------------- RequestRendering（532-623） ----------------

    /// <summary>RenderCache::RequestRendering(dm,pageNo)：res≤1 才整页请求（res==1 连带第一行第二块）。</summary>
    public void RequestRendering(object dm, int pageNo)
    {
        if (_host.ZoomReal(pageNo) <= 0) return;
        var tile = new TilePosition(GetTileRes(pageNo), 0, 0);
        if (tile.Res > 1) return;

        RequestRendering(dm, pageNo, tile);
        if (tile.Res == 1 && !IsRenderQueueFull)
        {
            tile = new TilePosition(tile.Res, 0, 1);
            RequestRendering(dm, pageNo, tile, clearQueueForPage: false);
        }
    }

    /// <summary>RenderCache::RequestRendering(dm,pageNo,tile,clearQueueForPage) 1:1。</summary>
    public void RequestRendering(object dm, int pageNo, TilePosition tile, bool clearQueueForPage = true,
        int[]? predictivePages = null, int predictiveOriginPageNo = 0)
    {
        if (_host.PauseRendering) return;

        lock (_requestAccess)
        {
            // 不可见的在途渲染 → 打断（abort 后结果被丢弃）。
            for (int i = 0; i < _nRenderThreads; i++)
            {
                var cr = _curReqs[i];
                if (cr != null && cr.Dm == dm && !_host.PageVisibleNearby(cr.PageNo))
                    AbortCurrentRequestLocked(i);
            }

            // Sumatra 在这里用 dm->GetRotation()（它的位图已烘入 rotation，必须分键）。
            // Glance 的位图来自未旋转 device 空间、与 rotation 无关 → 恒用 0 作键：
            // 转屏既不作废缓存，也不会出现"同一瓦片两组不同旋转的图"。
            int rotation = NormalizeRotation(0);
            float zoom = _host.ZoomReal(pageNo);

            for (int i = 0; i < _nRenderThreads; i++)
            {
                var cr = _curReqs[i];
                bool isRenderingTile = cr != null && !cr.Abort && cr.PageNo == pageNo && cr.Dm == dm && cr.Tile.Equals(tile);
                if (isRenderingTile)
                {
                    if (cr!.Zoom == zoom && cr.Rotation == rotation)
                        return;   // 完全同参在途
                    AbortCurrentRequestLocked(i);   // 同页不同 zoom/rotation → 打断重排
                }
            }

            if (clearQueueForPage)
                ClearQueueForDisplayModelLocked(dm, pageNo, tile);

            for (int i = 0; i < _requestCount; i++)
            {
                var req = _requests[i]!;
                if (req.PageNo == pageNo && req.Dm == dm && req.Tile.Equals(tile))
                {
                    if (req.Zoom == zoom && req.Rotation == rotation)
                    {
                        // 同参已在队列 → 挪到队尾（LIFO 下一个渲染）。
                        var tmp = _requests[_requestCount - 1]!;
                        _requests[_requestCount - 1] = req;
                        _requests[i] = tmp;
                    }
                    else
                    {
                        // 同瓦片不同 zoom → 原位改参（Sumatra 原样）。
                        // ⚠ Sumatra 的 req 只存 zoom/rotation，设备几何在渲染线程里按 zoom
                        //   现算，故"只改 zoom"是自洽的。Glance 把几何物化成了
                        //   DevW/DevH/X0/Y0/X1/Y1（入队时由 GetTileRectDevice 算好），
                        //   若此处只改 Zoom 不改几何，就会出现「req.Zoom 是新值、DevW 是旧值」
                        //   的撕裂 —— 引擎按旧 DevW 出图，绘制侧按新 Zoom 反算期望尺寸，
                        //   factor = 旧zoom/新zoom ≠ 1 → 该页【整页被重采样】= 整页均匀糊。
                        //   故必须与 Render() 入队路径做同一套几何重算。
                        req.Zoom = zoom;
                        req.Rotation = rotation;
                        GetTileRectDevice(pageNo, zoom, tile,
                            out int devW2, out int devH2, out int x02, out int y02, out int x12, out int y12);
                        req.DevW = devW2; req.DevH = devH2;
                        req.X0 = x02; req.Y0 = y02; req.X1 = x12; req.Y1 = y12;
                    }
                    return;
                }
            }

            if (Exists(dm, pageNo, rotation, zoom, tile))
            {
                return;   // 已按正确参数渲染过
            }

            Render(dm, pageNo, rotation, zoom, tile, predictivePages, predictiveOriginPageNo);
        }
    }

    /// <summary>RenderCache::RequestPredictiveRendering 1:1：找第一个需要渲染的页，其余顺延成链。</summary>
    public void RequestPredictiveRendering(object dm, int originPageNo, int[] pages, int nPages)
    {
        if (_host.PauseRendering) return;
        if (!_host.PageVisible(originPageNo)) return;   // 锚点页已滚出 → 停链

        int rotation = 0;
        int i = 0;
        for (; i < nPages; i++)
        {
            int pageNo = pages[i];
            if (pageNo < 0 || pageNo >= _host.PageCount) continue;
            float zoom = _host.ZoomReal(pageNo);
            if (zoom <= 0) continue;
            var tile = new TilePosition(GetTileRes(pageNo), 0, 0);
            if (tile.Res > 1) continue;
            if (!Exists(dm, pageNo, rotation, zoom, tile)) break;
        }
        if (i >= nPages) return;

        int[] chain = new int[KMaxPredictiveRequests];
        int nChain = 0;
        for (int j = i + 1; j < nPages; j++) chain[nChain++] = pages[j];
        var tile2 = new TilePosition(GetTileRes(pages[i]), 0, 0);
        RequestRendering(dm, pages[i], tile2, true, chain, originPageNo);
    }

    // ---------------- Render（690-774）：入队（LIFO，cap 8）+ 惰性线程 ----------------

    private bool Render(object dm, int pageNo, int rotation, float zoom, TilePosition tile,
        int[]? predictivePages, int predictiveOriginPageNo)
    {
        if (_host.PauseRendering) return false;

        lock (_requestAccess)
        {
            PageRenderRequest newRequest;
            if (_requestCount == KMaxPageRequests)
            {
                // 队满 → 弹最老请求（以 abort 语义通知其回调）。
                var oldest = _requests[0]!;
                if (oldest.FinishedCb != null)
                {
                    oldest.Abort = true;
                    oldest.ErrorCode = 0;
                    oldest.FinishedCb.Invoke(oldest);
                }
                for (int i = 0; i < KMaxPageRequests - 1; i++)
                    _requests[i] = _requests[i + 1];
                newRequest = new PageRenderRequest();
                _requests[KMaxPageRequests - 1] = newRequest;
            }
            else
            {
                newRequest = new PageRenderRequest();
                _requests[_requestCount] = newRequest;
                _requestCount++;
            }

            newRequest.Dm = dm;
            newRequest.PageNo = pageNo;
            newRequest.Rotation = rotation;
            newRequest.Zoom = zoom;
            newRequest.Tile = tile;
            GetTileRectDevice(pageNo, zoom, tile, out int devW, out int devH, out int x0, out int y0, out int x1, out int y1);
            newRequest.DevW = devW; newRequest.DevH = devH;
            newRequest.X0 = x0; newRequest.Y0 = y0; newRequest.X1 = x1; newRequest.Y1 = y1;
            newRequest.Abort = false;
            newRequest.Timestamp = (ulong)Environment.TickCount64;
            newRequest.ErrorCode = 0;
            newRequest.PredictiveOriginPageNo = predictiveOriginPageNo;
            newRequest.NPredictiveRequests = 0;
            if (predictivePages != null)
            {
                newRequest.NPredictiveRequests = Math.Min(predictivePages.Length, KMaxPredictiveRequests);
                for (int i = 0; i < newRequest.NPredictiveRequests; i++)
                    newRequest.PredictiveRequests[i] = predictivePages[i];
            }
            newRequest.FinishedCb = OnRenderFinished;

            _startRendering.Release();

            // 惰性线程生成（Render() 原样）：无空闲线程且未达上限 → 起新线程。
            if (_idleThreads == 0 && _nRenderThreads < _maxRenderThreads)
            {
                int idx = _nRenderThreads;
                var t = new Thread(RenderCacheThread)
                {
                    IsBackground = true,
                    Name = "RenderCacheThread",
                    Priority = ThreadPriority.BelowNormal
                };
                _renderThreads[idx] = t;
                t.Start(idx);
                _nRenderThreads++;
            }
            return true;
        }
    }

    /// <summary>DisplayModel::RenderFinishedAsync 对应物：回 UI 线程后由 shell 判定重绘/续链。</summary>
    private void OnRenderFinished(PageRenderRequest req)
    {
        if (req.Abort) return;
        _host.PostToUi(() => RenderFinishedOnUi(req));
    }

    private void RenderFinishedOnUi(PageRenderRequest req)
    {
        if (req.Abort) return;
        if (req.ErrorCode != 0)
        {
            _host.Repaint();
        }
        else if (_host.PageVisibleNearby(req.PageNo))
        {
            _host.Repaint();
        }
        // 继续预测链（失败也继续——一条坏页不打断后续预测，Sumatra 原样）。
        if (req.NPredictiveRequests > 0)
        {
            int[] pred = new int[req.NPredictiveRequests];
            Array.Copy(req.PredictiveRequests, pred, req.NPredictiveRequests);
            RequestPredictiveRendering(req.Dm, req.PredictiveOriginPageNo, pred, req.NPredictiveRequests);
        }
    }

    // ---------------- 队列消费（776-855） ----------------

    public int GetRenderDelay(object dm, int pageNo, TilePosition tile)
    {
        lock (_requestAccess)
        {
            ulong now = (ulong)Environment.TickCount64;
            for (int i = 0; i < _nRenderThreads; i++)
            {
                var cr = _curReqs[i];
                bool isRenderingTile = cr != null && !cr.Abort && cr.PageNo == pageNo && cr.Dm == dm && cr.Tile.Equals(tile);
                if (isRenderingTile) return (int)(now - cr!.Timestamp);
            }
            for (int i = 0; i < _requestCount; i++)
            {
                var req = _requests[i]!;
                if (req.PageNo == pageNo && req.Dm == dm && req.Tile.Equals(tile))
                    return (int)(now - req.Timestamp);
            }
            return TileRenderDelay.Undefined;
        }
    }

    /// <summary>RenderCache::GetNextRequest：LIFO 取队尾。</summary>
    private bool GetNextRequestLocked(PageRenderRequest req, int threadIdx)
    {
        if (_requestCount <= 0 || _requestCount > KMaxPageRequests) return false;
        int idx = _requestCount - 1;
        _requestCount = idx;
        // 浅拷贝字段（req 是线程栈上的复用对象）。
        var src = _requests[idx]!;
        req.Dm = src.Dm; req.PageNo = src.PageNo; req.Rotation = src.Rotation; req.Zoom = src.Zoom;
        req.Tile = src.Tile;
        req.DevW = src.DevW; req.DevH = src.DevH;
        req.X0 = src.X0; req.Y0 = src.Y0; req.X1 = src.X1; req.Y1 = src.Y1;
        req.Abort = src.Abort;
        req.Timestamp = src.Timestamp;
        req.ErrorCode = 0;
        req.Cookie = IntPtr.Zero;   // 队列条目尚未开渲（= Sumatra newRequest->abortCookie = nullptr）
        req.PredictiveOriginPageNo = src.PredictiveOriginPageNo;
        req.NPredictiveRequests = src.NPredictiveRequests;
        Array.Copy(src.PredictiveRequests, req.PredictiveRequests, KMaxPredictiveRequests);
        req.FinishedCb = src.FinishedCb;
        _curReqs[threadIdx] = req;
        return true;
    }

    private bool ClearCurrentRequestLocked(int threadIdx)
    {
        _curReqs[threadIdx] = null;
        return _requestCount == 0;
    }

    /// <summary>RenderCache::CancelRenderingBlocking：清队列 + 轮询打断在途（收尾用）。</summary>
    public void CancelRenderingBlocking(object dm)
    {
        ClearQueueForDisplayModel(dm);
        for (;;)
        {
            bool found = false;
            lock (_requestAccess)
            {
                for (int i = 0; i < _nRenderThreads; i++)
                {
                    if (_curReqs[i] != null && _curReqs[i]!.Dm == dm)
                    {
                        AbortCurrentRequestLocked(i);
                        found = true;
                    }
                }
                if (!found) { ClearQueueForDisplayModelLocked(dm, null, null); return; }
            }
            Thread.Sleep(50);
        }
    }

    public bool IsRenderingFor(object dm)
    {
        lock (_requestAccess)
        {
            for (int i = 0; i < _nRenderThreads; i++)
                if (_curReqs[i] != null && _curReqs[i]!.Dm == dm) return true;
            return false;
        }
    }

    /// <summary>外壳扩展：渲染线程忙或队列非空（EnsureWorkerCacheFlush 判定用）。</summary>
    public bool IsBusy()
    {
        lock (_requestAccess)
        {
            if (_requestCount > 0) return true;
            for (int i = 0; i < _nRenderThreads; i++)
                if (_curReqs[i] != null) return true;
            return false;
        }
    }

    /// <summary>RenderCache::AbortRendering。</summary>
    public void AbortRendering(object dm)
    {
        lock (_requestAccess)
        {
            ClearQueueForDisplayModelLocked(dm, null, null);
            for (int i = 0; i < _nRenderThreads; i++)
            {
                if (_curReqs[i] != null && _curReqs[i]!.Dm == dm)
                    AbortCurrentRequestLocked(i);
            }
        }
    }

    /// <summary>ClearQueueForDisplayModel：按页/瓦片清队列（不同 res 或不可见瓦片移除）。</summary>
    private void ClearQueueForDisplayModelLocked(object? dm, int? pageNo, TilePosition? tile)
    {
        int reqCount = _requestCount;
        int curPos = 0;
        for (int i = 0; i < reqCount; i++)
        {
            var req = _requests[i]!;
            bool shouldRemove = (dm == null || req.Dm == dm) &&
                                (pageNo is null || req.PageNo == pageNo.Value) &&
                                (tile is null || req.Tile.Res != tile.Value.Res ||
                                 !IsTileVisible(req.PageNo, tile.Value, 0.5));
            if (i != curPos) _requests[curPos] = req;
            if (shouldRemove)
            {
                _requestCount--;
            }
            else curPos++;
        }
    }

    public void ClearQueueForDisplayModel(object dm)
    {
        lock (_requestAccess) ClearQueueForDisplayModelLocked(dm, null, null);
    }

    private void AbortCurrentRequestLocked(int threadIdx)
    {
        var cr = _curReqs[threadIdx];
        if (cr == null) return;
        // AbortCookie 对应物（Sumatra RenderCache.cpp:1043-1052）：置标志 +
        // 跨线程写 cookie.abort=1 —— MuPDF 在 display-list 操作间隙自查，
        // 在途渲染毫秒级终止，而不是跑完全程再丢弃（400% 快滚 CPU 风暴根因）。
        cr.Abort = true;
        if (cr.Cookie != IntPtr.Zero)
        {
            try { GlanceRX.Interop.GlanceRXCookie.Abort(cr.Cookie); } catch { }
        }
    }

    // ---------------- RenderCacheThread（1056-1180） ----------------

    private void RenderCacheThread(object? data)
    {
        int threadIdx = (int)data!;
        var req = new PageRenderRequest();
        IntPtr pendingCookie = IntPtr.Zero;   // 本线程当前在渲请求的 abort block（C# 所有）

        for (;;)
        {
            if (_shouldExit) break;
            bool queueEmpty;
            lock (_requestAccess)
            {
                // 清 _curReqs[threadIdx]（= Sumatra ClearCurrentRequest）。
                // 此刻起 aborter 再也拿不到上一请求的 cookie → 同锁内释放，无 UAF。
                queueEmpty = ClearCurrentRequestLocked(threadIdx);
                if (pendingCookie != IntPtr.Zero)
                {
                    try { GlanceRX.Interop.GlanceRXCookie.Free(pendingCookie); } catch { }
                    pendingCookie = IntPtr.Zero;
                }
            }
            if (queueEmpty)
            {
                lock (_requestAccess) _idleThreads++;
                _startRendering.Wait();
                lock (_requestAccess) _idleThreads--;
                if (_shouldExit) break;
            }

            IntPtr cookie = IntPtr.Zero;
            lock (_requestAccess)
            {
                if (!GetNextRequestLocked(req, threadIdx)) { continue; }
                // FitzAbortCookie 分配（Sumatra: RenderPageArgs{&req.abortCookie} 由引擎
                // 出口分配；我们同样在开渲前创建）→ 存入 _curReqs 同一对象，aborter 可见。
                try
                {
                    cookie = GlanceRX.Interop.GlanceRXCookie.New();
                    req.Cookie = cookie;
                    pendingCookie = cookie;
                }
                catch { cookie = IntPtr.Zero; req.Cookie = IntPtr.Zero; }
            }

            if (!_host.PageVisibleNearby(req.PageNo))
            {
                // Sumatra：无回调的 legacy 请求跳过不可见页；带回调的照样渲染
                // （cb 内部决定是否重绘）。我们的 cb 恒有效 → 照样渲染。
            }

            if (_host.PauseRendering) continue;

            // Sumatra render thread：同步调引擎拿 Pixmap*，没有 Task / 信号量 / 线程池跳转。
            IntPtr tilePtr = IntPtr.Zero;
            int len = 0, w = 0, h = 0;
            try
            {
                (tilePtr, len, w, h) = _host.RenderTile(
                    cookie, req.PageNo, req.DevW, req.DevH, req.X0, req.Y0, req.X1, req.Y1);
            }
            catch
            {
                tilePtr = IntPtr.Zero;
                req.ErrorCode = 1;
            }

            if (tilePtr == IntPtr.Zero || len <= 0)
            {
                req.ErrorCode = 1;
                req.FinishedCb?.Invoke(req);
                continue;
            }

            if (req.Abort)
            {
                _host.FreeTilePixels(tilePtr, len);   // 已打断 → 丢弃结果（AbortCookie 语义）
                req.FinishedCb?.Invoke(req);
                continue;
            }

            bool owned = false;
            try
            {
                // AddLocked 腾不出坑时返回 false（Sumatra 同路径），由下面 FreePixmap 兜底。
                lock (_cacheAccess) owned = AddLocked(req, tilePtr, len, w, h);
                req.ErrorCode = owned ? 0 : 1;
            }
            catch
            {
                req.ErrorCode = 1;
            }
            if (!owned) _host.FreeTilePixels(tilePtr, len);

            req.FinishedCb?.Invoke(req);
        }
    }

    // ---------------- PaintTile / Paint（1184-1360，HDC → Win2D sink） ----------------


    /// <summary>RenderCache::PaintTile：画一颗瓦片；未命中按需请求。返回 renderDelay。
    /// isTargetRes 期用了替身（缩放过渡）→ neededScaling=true（conservation 不清其他分辨率）。</summary>
    private int PaintTile(Rect isect, int pageNo, TilePosition tile, Rect tileOnScreenFull,
        float zoom, bool renderMissing, bool isTargetRes, ref bool neededScaling)
    {

        BitmapCacheEntry? entry;
        lock (_cacheAccess) entry = FindLocked(_host, pageNo, 0, zoom, tile);

        int renderDelay = 0;
        if (entry == null)
        {
            // 未命中 → 任意 zoom 的同位瓦片当替身（缩放过渡帧的来源）。
            if (isTargetRes) neededScaling = true;
            lock (_cacheAccess) entry = FindLocked(_host, pageNo, 0, KInvalidZoom, tile);
            renderDelay = GetRenderDelay(_host, pageNo, tile);
            bool undef = TileRenderDelay.Undefined == renderDelay;
            if (renderMissing && undef && !IsRenderQueueFull)
            {
                RequestRendering(_host, pageNo, tile);
                renderDelay = 1;
            }
        }

        // HasBitmap = CPU 真身或 GPU 副本有一个可画（GPU 就位后 CPU 份会被释放，见 ReleaseCpuBitmap）。
        if (entry == null || !entry.HasBitmap)
        {
            bool didReduce = entry != null && ReduceTileSize();
            if (entry != null && !didReduce) renderDelay = TileRenderDelay.Failed;
            else if (0 == renderDelay) renderDelay = 1;
            if (entry != null && !didReduce)
            {
                lock (_cacheAccess) DropCacheEntryLocked(entry);
            }
            return renderDelay;
        }

        // ---- BlitPixmapRegion（RenderCache.cpp:1220-1234）1:1 ----
        // target = isect（视口物理像素）；source = 位图内像素矩形。
        // ⚠ source 的坐标单位是【位图像素】，绝不能拿 DIP 偏移直接当源索引 ——
        // 那是 Win2D DrawImage 的源/目标异单位陷阱（DIP 与 px 混用会让每个瓦片只显示
        // 左侧 1/DpiScale 的内容并把其余拉伸，表现为页面右侧缺一块 + 发糊）。
        double bmpW = entry.BitmapW, bmpH = entry.BitmapH;
        double factor = Math.Min(bmpW / tileOnScreenFull.Width, bmpH / tileOnScreenFull.Height);

        // 源起点 = 目标起点相对瓦片原点的偏移 × factor。
        // Sumatra 写作 xSrc = -min(tileOnScreen.x, 0)；因 isect = bounds∩tileOnScreen 且 bounds
        // 原点在 (0,0)，恒有 xSrc == isect.x - tileOnScreen.x，故两式完全等价（这里用通式，
        // 对 bounds 原点不为 0 也成立）。
        double sx = (isect.X - tileOnScreenFull.X) * factor;
        double sy = (isect.Y - tileOnScreenFull.Y) * factor;
        double sw = isect.Width * factor;
        double sh = isect.Height * factor;

        // 取整（Sumatra 用 int 截断）+ 夹到原位图内：夹掉多少就在 target 同侧裁掉多少
        // （保持像素对齐，绝不用拉伸去凑满 target）。
        double x0 = Math.Floor(sx), y0 = Math.Floor(sy);
        double x1 = Math.Round(sx + sw), y1 = Math.Round(sy + sh);
        Rect target = isect;
        if (x0 < 0) { if (factor > 0) { target.X += -x0 / factor; target.Width -= -x0 / factor; } x0 = 0; }
        if (y0 < 0) { if (factor > 0) { target.Y += -y0 / factor; target.Height -= -y0 / factor; } y0 = 0; }
        if (x1 > bmpW) { if (factor > 0) target.Width -= (x1 - bmpW) / factor; x1 = bmpW; }
        if (y1 > bmpH) { if (factor > 0) target.Height -= (y1 - bmpH) / factor; y1 = bmpH; }

        Rect source = new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        if (source.Width < 0.5 || source.Height < 0.5 || target.Width < 0.5 || target.Height < 0.5)
        {
            lock (_cacheAccess) DropCacheEntryLocked(entry);
            return 0;
        }

        _host.DrawTileBitmap(entry, target, source);

        lock (_cacheAccess) DropCacheEntryLocked(entry);
        return 0;
    }

    /// <summary>RenderCache::Paint 1:1：目标层瓦片 BFS，只画视口相交瓦片，未命中请求；
    /// gConserveMemory 收尾（FreePage 其他分辨率 + FreeNotVisible）。
    /// `pageOnScreen` 传【未旋转页帧】的页矩形（物理像素），旋转由外壳在 DrawingSession 上补；
    /// Sumatra 的 `bounds`（更新区）在这里由 `PageFrameViewPort` 内部导出（= 视口在页帧的外接矩形），
    /// 故不再作为参数传入 —— Glance 每帧画整个视口，没有「局部更新区」这个概念。</summary>
    public int Paint(int pageNo, Rect pageOnScreen, bool renderMissing, float zoom)
    {
        // zoom 由外壳 Draw 显式传入（与 dest 矩形同一来源/同一时刻）——显示端几何与
        // 请求端（_liveZoom）在 ChangeView 异步落地窗口内可能短暂分叉，显示必须自洽。
        ushort targetRes = GetTileRes(pageNo, zoom);
        ushort maxRes = GetMaxTileRes(pageNo, 0);
        maxRes = Math.Max(maxRes, targetRes);

        // Sumatra 的 pi->pageOnScreen 是整数 Rect（layout 产出）。
        // 这里同样取整，好让 target/source 全部落在整物理像素上 → factor 精确 == 1.0，
        // 且 Win2D 设备像素 == DIP×RasterizationScale == 这里的像素 → 真正 1:1 直拷（不重采样）。
        // ⚠ 外壳压在 DrawingSession 上的旋转枢轴必须取【这份取整后矩形】的中心，否则旋转页
        //   会出现半像素偏移 → 又被重采样。
        pageOnScreen = new Rect(
            Math.Round(pageOnScreen.X), Math.Round(pageOnScreen.Y),
            Math.Round(pageOnScreen.Width), Math.Round(pageOnScreen.Height));
        Rect bounds = PageFrameViewPort(pageOnScreen);

        var queue = new System.Collections.Generic.List<TilePosition> { new TilePosition(0, 0, 0) };
        int renderDelayMin = TileRenderDelay.Undefined;
        bool neededScaling = false;
        int qi = 0;

        while (qi < queue.Count)
        {
            TilePosition tile = queue[qi++];
            Rect tileOnScreen = GetTileOnScreen(pageNo, zoom, tile, pageOnScreen);
            if (tileOnScreen.IsEmpty)
            {
                renderDelayMin = Math.Min(TileRenderDelay.Failed, renderDelayMin);
                continue;
            }
            tileOnScreen.Intersect(pageOnScreen);
            Rect isect = bounds;
            isect.Intersect(tileOnScreen);
            if (isect.IsEmpty) continue;

            bool isTargetRes = tile.Res == targetRes;
            int renderDelay = PaintTile(isect, pageNo, tile, tileOnScreen, zoom, isTargetRes && renderMissing, isTargetRes, ref neededScaling);
            if (!(isTargetRes && 0 == renderDelay) && tile.Res < maxRes)
            {
                queue.Add(new TilePosition((ushort)(tile.Res + 1), (ushort)(tile.Row * 2), (ushort)(tile.Col * 2)));
                queue.Add(new TilePosition((ushort)(tile.Res + 1), (ushort)(tile.Row * 2), (ushort)(tile.Col * 2 + 1)));
                queue.Add(new TilePosition((ushort)(tile.Res + 1), (ushort)(tile.Row * 2 + 1), (ushort)(tile.Col * 2)));
                queue.Add(new TilePosition((ushort)(tile.Res + 1), (ushort)(tile.Row * 2 + 1), (ushort)(tile.Col * 2 + 1)));
            }
            if (isTargetRes && renderDelay != 0) neededScaling = true;
            if (renderDelay == TileRenderDelay.Failed || renderDelayMin == TileRenderDelay.Failed)
                renderDelayMin = TileRenderDelay.Failed;
            else
                renderDelayMin = Math.Min(renderDelay, renderDelayMin);
            // 从左到右、从上到下绘制（res 跨层时排序，Sumatra 原样）。
            if (tile.Res > 0 && qi < queue.Count && tile.Res < queue[qi].Res)
                queue.Sort(qi, queue.Count - qi, TileComparer.Instance);
        }

        if (GConserveMemory)
        {
            if (!neededScaling)
            {
                // 目标层全覆盖 → 释放该页其他分辨率的瓦片。
                var tile = new TilePosition(targetRes, ushort.MaxValue, 0);
                lock (_cacheAccess) FreePageLocked(_host, pageNo, tile);
            }
            FreeNotVisible();
        }

        return renderDelayMin;
    }

    private sealed class TileComparer : System.Collections.Generic.IComparer<TilePosition>
    {
        public static readonly TileComparer Instance = new();
        public int Compare(TilePosition a, TilePosition b)
        {
            if (a.Res != b.Res) return a.Res - b.Res;
            if (a.Row != b.Row) return a.Row - b.Row;
            return a.Col - b.Col;
        }
    }

    public bool IsRenderQueueFull => _requestCount == KMaxPageRequests;

    private static int NormalizeRotation(int rotation)
    {
        rotation %= 360;
        if (rotation < 0) rotation += 360;
        if (rotation >= 270) return 270;
        if (rotation >= 180) return 180;
        if (rotation >= 90) return 90;
        return rotation;
    }

    // ---------------- Dispose（~RenderCache） ----------------

    public void Dispose()
    {
        _shouldExit = true;
        int n;
        lock (_requestAccess) n = _nRenderThreads;
        if (n > 0)
        {
            try { _startRendering.Release(n); } catch { }
            for (int i = 0; i < n; i++)
            {
                var t = _renderThreads[i];
                if (t != null && t.IsAlive) { try { t.Join(5000); } catch { } }
                _renderThreads[i] = null;
            }
        }
        _startRendering.Dispose();
        lock (_cacheAccess)
        {
            for (int i = _cacheCount - 1; i >= 0; i--) ReleaseEntryPayload(_cache[i]!);
            Array.Clear(_cache);
            _cacheCount = 0;
        }
    }
}
