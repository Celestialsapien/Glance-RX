using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace Glance.Rendering;

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
//   * 坐标：外壳一律 DIP（Win2D 画布坐标），zoomReal = z×96/72（DIP 比例）；
//     maxTileSize = 物理屏幕像素 ÷ DpiScale；出图密度 = DIP 比例 × DpiScale
//     （物理 px），与 Sumatra 的 zoomReal×dpi 单位语义一致。
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

    public byte[] Bitmap = Array.Empty<byte>();   // Pixmap* 对应物（BGRA8，stride=W*4）
    public int BitmapW, BitmapH;
    public Microsoft.Graphics.Canvas.CanvasBitmap? Gpu;   // 外壳扩展：惰性 GPU 副本
    public bool OutOfDate;
    public int Refs = 1;
    public int CacheIdx = -1;
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

    // 预测链（kMaxPredictiveRequests）
    public int PredictiveOriginPageNo;
    public int NPredictiveRequests;
    public int[] PredictiveRequests = new int[TileRenderCache.KMaxPredictiveRequests];

    public Action<PageRenderRequest>? FinishedCb;
}

/// <summary>DisplayModel 的渲染侧对应物（GpuPageView 实现）。</summary>
internal interface ITileRenderHost
{
    int PageCount { get; }
    float PageWidthPt(int pageNo);
    float PageHeightPt(int pageNo);
    /// <summary>page->zoomReal 对应物：DIP 渲染比例（z×96/72）。</summary>
    float ZoomReal(int pageNo);
    double DpiScale { get; }
    Size ViewPortSizeDip { get; }
    /// <summary>pageInfo->pageOnScreen 对应物：视口坐标 DIP。</summary>
    Rect PageOnScreen(int pageNo);
    bool PageVisible(int pageNo);
    bool PageVisibleNearby(int pageNo);
    /// <summary>外壳呈现层旋转角（0/90/180/270；出图恒按未旋转，显示端映射）。</summary>
    int Rotation { get; }
    bool PauseRendering { get; }
    /// <summary>DisplayModel::RenderFinished → Repaint（已在 UI 线程）。</summary>
    void Repaint();
    void PostToUi(Action action);
    /// <summary>engine->RenderPage(RenderPageArgs{pageRect}) 对应物：局部渲染。
    /// 返回 (BGRA pixels, w, h)；失败抛异常（→ errorCode=1 路径）。</summary>
    Task<(byte[] Pixels, int W, int H)> RenderTileAsync(
        int pageNo, int devW, int devH, int x0, int y0, int x1, int y1);
    /// <summary>BlitPixmapRegion 对应物（仅 UI 线程 Draw 帧内调用）。</summary>
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
        double dpi = host.DpiScale;
        // GetSystemMetrics(SM_CXSCREEN/SM_CYSCREEN)：主屏物理像素（→ DIP）。
        MaxTileSize = new Size(
            Win32Metrics.ScreenWidth / dpi,
            Win32Metrics.ScreenHeight / dpi);

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

        DisposeGpuLocked(entry);

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

    /// <summary>GPU 位图释放必须回 UI 线程（Draw 帧可能仍引用）。</summary>
    private void DisposeGpuLocked(BitmapCacheEntry entry)
    {
        var gpu = entry.Gpu;
        entry.Gpu = null;
        if (gpu != null)
            _host.PostToUi(() => { try { gpu.Dispose(); } catch { } });
    }

    /// <summary>外壳扩展：设备重建时作废全部 GPU 位图（CPU 缓存与 Sumatra 一致地保留）。</summary>
    public void DropGpuResources()
    {
        lock (_cacheAccess)
        {
            for (int i = 0; i < _cacheCount; i++) DisposeGpuLocked(_cache[i]!);
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

    /// <summary>RenderCache::Add：先 FreePage 同瓦片（不同 zoom/rotation），再腾坑，再入缓存。</summary>
    private void AddLocked(PageRenderRequest req, byte[] bmp, int w, int h)
    {
        req.Rotation = NormalizeRotation(req.Rotation);

        // 同一瓦片可能已有不同 zoom/rotation 的旧图 → 先释放。
        FreePageLocked(req.Dm, req.PageNo, req.Tile);

        bool hasSpace = FreeIfFullLocked(req);
        if (!hasSpace || _cacheCount >= KMaxBitmapsCached)
        {
            return;   // 腾不出坑：丢弃本图（Sumatra 同路径）。
        }

        var entry = new BitmapCacheEntry
        {
            Dm = req.Dm,
            PageNo = req.PageNo,
            Rotation = req.Rotation,
            Zoom = req.Zoom,
            Tile = req.Tile,
            Bitmap = bmp,
            BitmapW = w,
            BitmapH = h,
            CacheIdx = _cacheCount
        };
        _cache[_cacheCount] = entry;
        _cacheCount++;
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

    /// <summary>GetTileRectDevice 对应物：瓦片设备像素矩形（密度 = zoom×DpiScale，物理 px）。</summary>
    private void GetTileRectDevice(int pageNo, float zoom, TilePosition tile,
        out int devW, out int devH, out int x0, out int y0, out int x1, out int y1)
    {
        double ptW = _host.PageWidthPt(pageNo), ptH = _host.PageHeightPt(pageNo);
        double dens = zoom * _host.DpiScale;
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

    /// <summary>GetTileOnScreen 对应物：瓦片在视口坐标系的 DIP 矩形。瓦片几何按【未旋转】页算
    /// （与出图设备空间一致），再按外壳旋转角映射到屏幕（与 Glance 标注 ApplyPageTransform 的
    /// GPU 旋转约定同式：CreateRotation(rot·π/180, 页中心)，y-down 屏幕上为顺时针）。</summary>
    private Rect GetTileOnScreen(int pageNo, float zoom, TilePosition tile, Rect pageOnScreen)
    {
        int rot = _host.Rotation % 360;
        double ptW = _host.PageWidthPt(pageNo), ptH = _host.PageHeightPt(pageNo);
        GetTileRectPt(0, 0, ptW, ptH, tile, out double tx, out double ty, out double tw, out double th);
        double lx = tx * zoom, ly = ty * zoom, lw = tw * zoom, lh = th * zoom;

        if (rot == 0)
            return new Rect(pageOnScreen.X + lx, pageOnScreen.Y + ly, lw, lh);

        // 未旋转页矩形（PageUnrotatedRect 对应物）：90/270 交换 dest 宽高、绕中心放置。
        bool swap = rot == 90 || rot == 270;
        double w = swap ? pageOnScreen.Height : pageOnScreen.Width;
        double h = swap ? pageOnScreen.Width : pageOnScreen.Height;
        double cx = pageOnScreen.X + pageOnScreen.Width / 2.0;
        double cy = pageOnScreen.Y + pageOnScreen.Height / 2.0;
        double uX = cx - w / 2.0, uY = cy - h / 2.0;

        return rot switch
        {
            90 => new Rect(cx + cy - uY - ly - lh, cy - w / 2.0 + lx, lh, lw),
            180 => new Rect(cx + w / 2.0 - lx - lw, cy + h / 2.0 - ly - lh, lw, lh),
            270 => new Rect(cx - cy + uY + ly, cy + w / 2.0 - lx - lw, lh, lw),
            _ => new Rect(pageOnScreen.X + lx, pageOnScreen.Y + ly, lw, lh),
        };
    }

    /// <summary>IsTileVisible（fuzz 语义原样：0.5/2.0 扩边）。</summary>
    private bool IsTileVisible(int pageNo, TilePosition tile, double fuzz)
    {
        float zoom = _host.ZoomReal(pageNo);
        Rect r = _host.PageOnScreen(pageNo);
        if (r.IsEmpty) return false;
        Rect tileOnScreen = GetTileOnScreen(pageNo, zoom, tile, r);
        double dx = tileOnScreen.Width * fuzz * 0.5;
        double dy = tileOnScreen.Height * fuzz * 0.5;
        tileOnScreen = new Rect(tileOnScreen.X - dx, tileOnScreen.Y - dy,
            tileOnScreen.Width * (fuzz + 1), tileOnScreen.Height * (fuzz + 1));
        var screen = new Rect(0, 0, _host.ViewPortSizeDip.Width, _host.ViewPortSizeDip.Height);
        tileOnScreen.Intersect(screen);
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
    /// </summary>
    public ushort GetTileRes(int pageNo, float? zoom = null)
    {
        double ptW = _host.PageWidthPt(pageNo), ptH = _host.PageHeightPt(pageNo);
        float zv = zoom ?? _host.ZoomReal(pageNo);
        var viewPort = _host.ViewPortSizeDip;

        double pixelW = ptW * zv, pixelH = ptH * zv;

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

                for (int i = _cacheCount - 1; i >= 0; i--) DisposeGpuLocked(_cache[i]!);
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
                        req.Zoom = zoom;
                        req.Rotation = rotation;
                    }
                    return;
                }
            }

            if (Exists(dm, pageNo, rotation, zoom, tile))
                return;   // 已按正确参数渲染过

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
            if (shouldRemove) _requestCount--;
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
        // AbortCookie 无对应物（同步 FFI）——置 abort 标志，渲染完成后丢弃结果。
        cr.Abort = true;
    }

    // ---------------- RenderCacheThread（1056-1180） ----------------

    private void RenderCacheThread(object? data)
    {
        int threadIdx = (int)data!;
        var req = new PageRenderRequest();

        for (;;)
        {
            if (_shouldExit) break;
            bool queueEmpty;
            lock (_requestAccess) queueEmpty = ClearCurrentRequestLocked(threadIdx);
            if (queueEmpty)
            {
                lock (_requestAccess) _idleThreads++;
                _startRendering.Wait();
                lock (_requestAccess) _idleThreads--;
                if (_shouldExit) break;
            }

            lock (_requestAccess)
            {
                if (!GetNextRequestLocked(req, threadIdx)) { continue; }
            }

            if (!_host.PageVisibleNearby(req.PageNo))
            {
                // Sumatra：无回调的 legacy 请求跳过不可见页；带回调的照样渲染
                // （cb 内部决定是否重绘）。我们的 cb 恒有效 → 照样渲染。
            }

            if (_host.PauseRendering) continue;

            try
            {
                var (pixels, w, h) = _host.RenderTileAsync(
                    req.PageNo, req.DevW, req.DevH, req.X0, req.Y0, req.X1, req.Y1)
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                if (req.Abort)
                {
                    continue;   // 已打断 → 丢弃结果（AbortCookie 语义）
                }

                if (pixels != null && pixels.Length > 0)
                {
                    lock (_cacheAccess) AddLocked(req, pixels, w, h);
                    req.ErrorCode = 0;
                }
                else
                {
                    req.ErrorCode = 1;
                }
            }
            catch
            {
                req.ErrorCode = 1;
            }

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
            if (renderMissing && TileRenderDelay.Undefined == renderDelay && !IsRenderQueueFull)
            {
                RequestRendering(_host, pageNo, tile);
                renderDelay = 1;
            }
        }

        if (entry == null || entry.Bitmap.Length == 0)
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

        // BlitPixmapRegion 对应物：target=isect（视口 DIP），source=位图内对应区域。
        double dens = _host.DpiScale;
        double bmpW = entry.BitmapW, bmpH = entry.BitmapH;
        int xSrc = (int)-Math.Min(tileOnScreenFull.X, 0);
        int ySrc = (int)-Math.Min(tileOnScreenFull.Y, 0);
        double factor = Math.Min(
            bmpW / (tileOnScreenFull.Width * dens),
            bmpH / (tileOnScreenFull.Height * dens));

        Rect target = isect;
        Rect source;
        if (factor != 1.0)
        {
            double sx = (isect.X - tileOnScreenFull.X) * factor;
            double sy = (isect.Y - tileOnScreenFull.Y) * factor;
            source = new Rect(
                Math.Max(0, Math.Min(sx, bmpW - 1)),
                Math.Max(0, Math.Min(sy, bmpH - 1)),
                Math.Max(1, Math.Min(isect.Width * factor, bmpW)),
                Math.Max(1, Math.Min(isect.Height * factor, bmpH)));
        }
        else
        {
            source = new Rect(
                xSrc + (isect.X - tileOnScreenFull.X),
                ySrc + (isect.Y - tileOnScreenFull.Y),
                isect.Width, isect.Height);
        }

        _host.DrawTileBitmap(entry, target, source);

        lock (_cacheAccess) DropCacheEntryLocked(entry);
        return 0;
    }

    /// <summary>RenderCache::Paint 1:1：目标层瓦片 BFS，只画视口相交瓦片，未命中请求；
    /// gConserveMemory 收尾（FreePage 其他分辨率 + FreeNotVisible）。</summary>
    public int Paint(int pageNo, Rect pageOnScreen, Rect bounds, bool renderMissing, float zoom)
    {
        // zoom 由外壳 Draw 显式传入（与 dest 矩形同一来源/同一时刻）——显示端几何与
        // 请求端（_liveZoom）在 ChangeView 异步落地窗口内可能短暂分叉，显示必须自洽。
        ushort targetRes = GetTileRes(pageNo, zoom);
        ushort maxRes = GetMaxTileRes(pageNo, 0);
        maxRes = Math.Max(maxRes, targetRes);

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

    public string BusyInfo()
    {
        lock (_requestAccess)
        {
            string s = $"tile={MaxTileSize.Width:F0}x{MaxTileSize.Height:F0} cache={_cacheCount} queued={_requestCount} threads={_nRenderThreads} reduced={NTileSizeReductions}";
            for (int i = 0; i < _nRenderThreads; i++)
            {
                var r = _curReqs[i];
                if (r != null)
                    s += $" t{i}=p{r.PageNo},res{r.Tile.Res},r{r.Tile.Row}c{r.Tile.Col}{(r.Abort ? ",abort" : "")}";
            }
            return s;
        }
    }

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
            for (int i = _cacheCount - 1; i >= 0; i--) DisposeGpuLocked(_cache[i]!);
            Array.Clear(_cache);
            _cacheCount = 0;
        }
    }
}
