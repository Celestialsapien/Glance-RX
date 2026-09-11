using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Glance.Interop;

namespace Glance.Services;

/// <summary>
/// Service for rendering PDF pages to BGRA bitmaps.
///
/// Backend: a single-process MuPDF engine (<c>glance_native.dll</c>, the MuPDF
/// refactor replacing the old out-of-process <c>glance_renderer_worker.exe</c>
/// pool). MuPDF parses each page once into a display list that is
/// reference-counted and safe to replay from many threads (the crate keeps one
/// fz_context per thread), so tile renders run CONCURRENTLY on the .NET thread
/// pool with no global lock and no cross-process marshalling — the viewer's
/// "指哪打哪" path: drag/scroll and every visible tile is rasterized in-process
/// at full parallelism.
///
/// This class preserves the historical public surface used by
/// <c>GpuPageView</c> / <c>MainPage</c> / <c>PdfPrintService</c> so the rest of
/// the app is unaware the rendering backend changed.
/// </summary>
public class PdfRenderService : IDisposable
{
    /// <summary>Max in-flight byte[] pool, mirrors the old worker pool's budget.</summary>
    private static readonly ArrayPool<byte> PixelPool =
        ArrayPool<byte>.Create(64 * 1024 * 1024, 4);

    private readonly int _renderCap;
    private IntPtr _engine;
    private SemaphoreSlim? _slot;
    private bool _initialized;
    private bool _disposed;

    public PdfRenderService(int? workerCount = null)
    {
        // 并发上限：min(物理核 - 留档余量, cap)。【不再可配置】——始终按物理核自适应选择：
        // 单进程多线程下并发数 = 同时光栅化的线程数；超过物理核会反向打架，故默认留 1 档给
        // UI/Win2D，并按【物理核】（而非逻辑核/超线程）定并发，抑制超线程争抢内存带宽。
        // 每个线程惰性持有自己的 fz_context（有克隆开销 + 常驻，但远低于旧的多进程）。硬上限防吃爆内存。
        // workerCount 仅作为 API 层显式覆盖（默认全调用点都不传）；config.txt 已不再提供该键。
        int auto = Math.Clamp(CpuCoreInfo.PhysicalCoreCount() - 1, 1, Tuning.RenderWorkerCap);
        int wc = workerCount ?? auto;
        _renderCap = Math.Clamp(wc, 1, Tuning.RenderWorkerHardCap);
    }

    /// <summary>Max concurrent in-process render threads backing this service.</summary>
    public int WorkerCount => _renderCap;

    /// <summary>
    /// Initialize the MuPDF engine for a given PDF file path. Throws
    /// <see cref="PdfPasswordRequiredException"/> if the PDF is encrypted and
    /// <paramref name="password"/> is missing/wrong.
    /// </summary>
    public async Task InitializeAsync(
        string pdfFilePath,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(pdfFilePath))
        {
            throw new ArgumentException("PDF file path cannot be null or empty", nameof(pdfFilePath));
        }
        if (_initialized)
        {
            throw new InvalidOperationException("PDF engine already initialized. Dispose first to initialize a new file.");
        }

        // MuPDF 打开文档需解析 xref / 页树，大 PDF 可能耗时数百 ms——移到线程池执行，
        // 避免同步卡住 UI 线程（冷启动时窗口/加载状态才能立即响应）。
        var result = await Task.Run(() =>
        {
            IntPtr pathPtr = GlanceNative.MarshalUtf8(pdfFilePath);
            IntPtr pwPtr = password == null ? IntPtr.Zero : GlanceNative.MarshalUtf8(password);
            try
            {
                // hot capacity = 滑动窗口页数，与 C# 端缓存窗口对齐 → 窗口内翻页/回看零 reload。
                return GlanceNative.mu_engine_open(pathPtr, pwPtr, (uint)Tuning.MaxCachedPages);
            }
            finally
            {
                GlanceNative.FreeUtf8(pwPtr);
                GlanceNative.FreeUtf8(pathPtr);
            }
        }, cancellationToken).ConfigureAwait(false);

        if (result.NeedsPassword != 0)
        {
            throw new PdfPasswordRequiredException();
        }
        if (result.Engine == IntPtr.Zero)
        {
            ThrowCreateError(result, "Open PDF");
            throw new InvalidOperationException("Open PDF failed: unknown error");
        }
        _engine = result.Engine;
        _slot = new SemaphoreSlim(_renderCap, _renderCap);
        _initialized = true;
    }

    /// <summary>Render a page directly to raw BGRA pixels (tightly packed BGRA8).</summary>
    public async Task<(byte[] Pixels, int Width, int Height)> RenderPageBgraAsync(
        uint pageIndex,
        uint width,
        uint height,
        CancellationToken cancellationToken = default)
    {
        var (buf, len, w, h) = await RenderPageBgraPooledAsync(pageIndex, width, height, cancellationToken).ConfigureAwait(false);
        byte[] owned = new byte[len];
        Buffer.BlockCopy(buf, 0, owned, 0, len);
        ReturnPixelBuffer(buf);
        return (owned, w, h);
    }

    /// <summary>
    /// Render a page to raw BGRA pixels using a POOLED buffer (zero LOH churn).
    /// The returned array may be LARGER than Length — only the first Length bytes
    /// are valid. Caller MUST return it via <see cref="ReturnPixelBuffer"/>.
    /// </summary>
    public async Task<(byte[] Buffer, int Length, int Width, int Height)> RenderPageBgraPooledAsync(
        uint pageIndex,
        uint width,
        uint height,
        CancellationToken cancellationToken = default)
    {
        if (width == 0 || height == 0)
        {
            throw new ArgumentException("Width and height must be greater than zero");
        }
        EnsureReady();
        await AcquireSlotAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => RenderPageCore(pageIndex, width, height),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _slot!.Release();
        }
    }

    /// <summary>Return a buffer obtained from <see cref="RenderPageBgraPooledAsync"/> back to the pool.</summary>
    public static void ReturnPixelBuffer(byte[] buffer)
    {
        PixelPool.Return(buffer);
    }

    /// <summary>
    /// Reload the MuPDF document to flush its parsed-page/object cache, keeping
    /// scanned-PDF memory from climbing to the whole file size. Page count /
    /// indices / dimensions are unchanged.
    /// </summary>
    public async Task<bool> ReloadDocumentAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized) return false;
        // 单文档 reload 快速（仅重解析 xref）；并行渲染线程不会卡在 reload 上，因为
        // get_parsed 的锁只保护 cache 查询/重建，reload 持有同一把锁刷新后立即释放。
        return await Task.Run(
            () => GlanceNative.mu_engine_reload(_engine),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Total number of pages in the loaded PDF.</summary>
    public async Task<uint> GetPageCountAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        return await Task.Run(() => GlanceNative.mu_engine_page_count(_engine), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Page dimensions in PDF points (1/72 inch).</summary>
    public async Task<(float Width, float Height)> GetPageDimensionsAsync(
        uint pageIndex,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        return await Task.Run(() =>
        {
            if (!GlanceNative.mu_engine_page_dimensions(_engine, pageIndex, out float w, out float h))
            {
                throw new InvalidOperationException($"Get page dimensions failed for page {pageIndex}");
            }
            return (w, h);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Get dimensions for ALL pages 0..count-1 in one native call (single lock
    /// hold + single FFI round trip). Returns count*2 floats: w0,h0,w1,h1,...
    /// Load-time page enumeration on large PDFs is much faster than N sequential
    /// per-page round trips. Throws if any page dimension is invalid (0 / negative).
    /// </summary>
    public async Task<float[]> GetAllPageDimensionsAsync(
        uint count,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if (count == 0)
        {
            return Array.Empty<float>();
        }
        return await Task.Run(() =>
        {
            float[] dims = new float[count * 2];
            var handle = GCHandle.Alloc(dims, GCHandleType.Pinned);
            try
            {
                if (!GlanceNative.mu_engine_page_dimensions_all(_engine, count, handle.AddrOfPinnedObject()))
                {
                    throw new InvalidOperationException("Get page dimensions (all) failed");
                }
            }
            finally
            {
                handle.Free();
            }
            for (int i = 0; i < dims.Length; i += 2)
            {
                if (dims[i] <= 0f || dims[i + 1] <= 0f)
                {
                    throw new InvalidOperationException($"Get page dimensions failed for page {i / 2}");
                }
            }
            return dims;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 冷启动热页：预解析某页为 DisplayList 并缓存，使该页首块瓦片渲染直接命中缓存、
    /// 跳过内容流解析（按还原位置热身；第 0 页已在 <see cref="GetAllPageDimensionsAsync"/>
    /// 阶段由 native 顺带预解析）。与渲染一样占用并发槽位（Dispose 排空槽位后才销毁引擎，
    /// 可避免与引擎销毁竞态）；热页是纯优化，任何失败（含 dispose 竞态、越界页）都静默
    /// 吞掉并返回 false，不影响加载。
    /// </summary>
    public async Task<bool> WarmupPageAsync(uint pageIndex, CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureReady();
            await AcquireSlotAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(
                    () => GlanceNative.mu_engine_warmup_page(_engine, pageIndex),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _slot!.Release();
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 导出某页的矢量命令流（display list → 与分辨率无关的二进制命令流，见
    /// VectorSceneBuilder）。与渲染一样占用并发槽位（Dispose 排空槽位后才销毁
    /// 引擎，避免导出与引擎销毁竞态——必须走槽，不能绕过）。返回 null = 失败
    /// 或命令流超限（调用方应回退瓦片管线）。
    /// </summary>
    public async Task<byte[]?> ExportPageCommandsAsync(uint pageIndex, CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureReady();
            await AcquireSlotAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    var resp = GlanceNative.mu_export_page_commands(_engine, pageIndex, out _, out _);
                    if (!resp.Success) return null;
                    try
                    {
                        if (resp.DataLen > (ulong)Tuning.VectorMaxCommandBytes) return null;
                        byte[] bytes = new byte[resp.DataLen];
                        Marshal.Copy(resp.DataPtr, bytes, 0, (int)resp.DataLen);
                        return bytes;
                    }
                    finally { GlanceNative.memory_free(resp.DataPtr, resp.DataLen); }
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _slot!.Release();
            }
        }
        catch
        {
            return null;
        }
    }

    // ---- private helpers --------------------------------------------------

    private async Task AcquireSlotAsync(CancellationToken ct)
    {
        var slot = _slot!;
        await slot.WaitAsync(ct).ConfigureAwait(false);
        if (_disposed)
        {
            slot.Release();
            throw new ObjectDisposedException(nameof(PdfRenderService));
        }
    }

    /// <summary>
    /// 渲染线程降优先级到 BelowNormal：让所有光栅线程满载也不与 UI 线程(动画)抢核，
    /// 从系统调度层面保证动画期 UI 能插队，减少"偶尔卡一帧"。线程池线程每线程首帧设一次。
    /// </summary>
    [ThreadStatic] private static bool _renderPriorityDowngraded;

    private static void EnsureRenderPriority()
    {
        if (_renderPriorityDowngraded) return;
        _renderPriorityDowngraded = true;
        try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
    }

    private (byte[], int, int, int) RenderPageCore(uint pageIndex, uint width, uint height)
    {
        EnsureRenderPriority();
        var resp = GlanceNative.mu_render_page_bgra(
            _engine, pageIndex, width, height,
            out IntPtr data, out ulong len, out uint w, out uint h);
        GlanceNative.ThrowIfFailed(resp, "Render page");
        return CopyOut(data, len, w, h);
    }

    private static (byte[], int, int, int) CopyOut(IntPtr data, ulong len, uint w, uint h)
    {
        try
        {
            byte[] buf = PixelPool.Rent((int)len);
            Marshal.Copy(data, buf, 0, (int)len);
            return (buf, (int)len, (int)w, (int)h);
        }
        finally
        {
            GlanceNative.memory_free(data, len);
        }
    }

    private static void ThrowCreateError(GlanceNative.EngineCreateResult result, string operation)
    {
        if (result.ErrorMsg == IntPtr.Zero) return;
        string msg = Marshal.PtrToStringAnsi(result.ErrorMsg) ?? "Unknown error";
        GlanceNative.memory_free(result.ErrorMsg);
        throw new InvalidOperationException($"{operation}: {msg}");
    }

    private void EnsureReady()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("PDF engine not initialized. Call InitializeAsync first.");
        }
    }

    public void Dispose() => Dispose(false);

    private void Dispose(bool synchronous)
    {
        if (_disposed) return;
        _disposed = true;
        // 先排空并发渲染（若有在途任务），再销毁引擎，避免引擎指针与在途 fz_context 竞态。
        // 需连续获取全部 _renderCap 个许可才说明没有渲染线程持有缝位；获取不足则超时放弃，
        // 之后销毁引擎。在途 Arc<DisplayList> 引用可保护 display list 不被提前释放。
        if (_slot != null)
        {
            int acquired = 0;
            var margin = TimeSpan.FromMilliseconds(500);
            var deadline = Environment.TickCount64 + (long)margin.TotalMilliseconds;
            try
            {
                for (int i = 0; i < _renderCap; i++)
                {
                    long remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0) break;
                    bool got = synchronous
                        ? _slot.Wait(TimeSpan.FromMilliseconds(remaining))
                        : _slot.WaitAsync(TimeSpan.FromMilliseconds(remaining)).GetAwaiter().GetResult();
                    if (!got) break;
                    acquired++;
                }
            }
            catch { }
            if (acquired == _renderCap)
            {
                try { _slot.Release(_renderCap); } catch { }
            }
            try { _slot.Dispose(); } catch { }
        }
        if (_engine != IntPtr.Zero)
        {
            try { GlanceNative.mu_engine_destroy(_engine); } catch { }
            _engine = IntPtr.Zero;
        }
        _initialized = false;
        _slot = null;
    }

    /// <summary>
    /// 物理核数自适应：区分超线程，给出"真实物理并行上限"。
    /// 只有 Windows（WinUI 目标）才有 GetLogicalProcessorInformation；失败时回退到逻辑核数。
    /// </summary>
    private static class CpuCoreInfo
    {
        private const int RelationProcessorCore = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION
        {
            public UIntPtr ProcessorMask; // +0
            public int Relationship;      // +8
            public int Reserved;          // +12
            public ulong Info;            // +16 union 前 8 字节（ProcessorCore.Flags / NumaNode 等）
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformation(
            IntPtr buffer, ref int returnLength);

        public static int PhysicalCoreCount()
        {
            try
            {
                int coreCount = 0;
                int length = 0;
                if (!GetLogicalProcessorInformation(IntPtr.Zero, ref length) || length <= 0)
                    return Environment.ProcessorCount;

                int stride = Marshal.SizeOf<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>();
                if (stride <= 0) stride = 24; // 理论上 Marshal.SizeOf 不会失败；保留兜底
                int entries = length / stride;
                if (entries <= 0) return Environment.ProcessorCount;
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetLogicalProcessorInformation(buffer, ref length))
                        return Environment.ProcessorCount;
                    for (int i = 0; i < entries; i++)
                    {
                        var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION>(
                            buffer + (i * stride));
                        if (info.Relationship == RelationProcessorCore && info.ProcessorMask != UIntPtr.Zero)
                            coreCount++;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
                return coreCount > 0 ? coreCount : Environment.ProcessorCount;
            }
            catch
            {
                return Environment.ProcessorCount;
            }
        }
    }
}