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
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GlanceRX.Interop;

namespace GlanceRX.Services;

/// Service for rendering PDF pages to BGRA bitmaps.
public class PdfRenderService : IDisposable
{
    private readonly int _renderCap;
    private IntPtr _engine;
    private SemaphoreSlim? _slot;
    private bool _initialized;
    private bool _disposed;

    public PdfRenderService(int? workerCount = null)
    {
        // 并发上限：min(物理核 - 留档余量, cap)。【不再可配置】——始终按物理核自适应选择。
        int auto = Math.Clamp(CpuCoreInfo.PhysicalCoreCount() - 1, 1, Tuning.RenderWorkerCap);
        int wc = workerCount ?? auto;
        _renderCap = Math.Clamp(wc, 1, Tuning.RenderWorkerHardCap);
    }

    public int WorkerCount => _renderCap;

    /// <summary>Initialize the MuPDF engine for a given PDF file path. Throws if the PDF is encrypted and is missing/wrong.</summary>
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
        var result = await Task.Run(() =>
        {
            IntPtr pathPtr = GlanceRXNative.MarshalUtf8(pdfFilePath);
            IntPtr pwPtr = password == null ? IntPtr.Zero : GlanceRXNative.MarshalUtf8(password);
            try
            {
                // hot capacity = 0 → 原生侧不逐出解析页缓存（1:1 Sumatra pageInfos。
                // → store 已解码图变孤儿 → 每块瓦片重新解码整页扫描图（CPU 风暴根因）。
                return GlanceRXNative.mu_engine_open(pathPtr, pwPtr, 0);
            }
            finally
            {
                GlanceRXNative.FreeUtf8(pwPtr);
                GlanceRXNative.FreeUtf8(pathPtr);
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

    /// <summary>Render a page directly to raw BGRA pixels (tightly packed BGRA8). 返回值是【自有】缓冲（一次分配、一次拷贝），调用方可长期持有，无需归还。</summary>
    public async Task<(byte[] Pixels, int Width, int Height)> RenderPageBgraAsync(
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
            return await Task.Run(() =>
            {
                var (pixels, _, w, h) = RenderPageCore(pageIndex, width, height);
                return (pixels, w, h);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _slot!.Release();
        }
    }

    /// Render a tile (sub-rectangle of a page's device space) and hand back the OWNING native BGRA buffer —…
    public (IntPtr Data, int Length, int W, int H) RenderPageRegionOwningNative(
        uint pageIndex, uint devW, uint devH,
        uint x0, uint y0, uint x1, uint y1,
        IntPtr cookie = default)
    {
        EnsureReady();
        EnsureRenderPriority();
        var resp = GlanceRXNative.mu_render_page_region_bgra(
            _engine, pageIndex, devW, devH, x0, y0, x1, y1,
            cookie,
            out IntPtr data, out ulong len, out uint w, out uint h);
        GlanceRXNative.ThrowIfFailed(resp, "Render page region");
        if (data == IntPtr.Zero || len == 0)
        {
            if (data != IntPtr.Zero) GlanceRXNative.memory_free(data, len);
            return (IntPtr.Zero, 0, (int)w, (int)h);
        }
        return (data, (int)len, (int)w, (int)h);
    }

    /// <summary>Reload the MuPDF document to flush its parsed-page/object cache, keeping scanned-PDF memory from climbing to the whole file size.</summary>
    public async Task<bool> ReloadDocumentAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized) return false;
        // 单文档 reload 快速（仅重解析 xref）；并行渲染线程不会卡在 reload 上，因为
        return await Task.Run(
            () => GlanceRXNative.mu_engine_reload(_engine),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Total number of pages in the loaded PDF.</summary>
    public async Task<uint> GetPageCountAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        return await Task.Run(() => GlanceRXNative.mu_engine_page_count(_engine), cancellationToken).ConfigureAwait(false);
    }

    public async Task<(float Width, float Height)> GetPageDimensionsAsync(
        uint pageIndex,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        return await Task.Run(() =>
        {
            if (!GlanceRXNative.mu_engine_page_dimensions(_engine, pageIndex, out float w, out float h))
            {
                throw new InvalidOperationException($"Get page dimensions failed for page {pageIndex}");
            }
            return (w, h);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// Get dimensions for ALL pages 0..count-1 in one native call (single lock hold + single FFI round trip…
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
                if (!GlanceRXNative.mu_engine_page_dimensions_all(_engine, count, handle.AddrOfPinnedObject()))
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

    /// 冷启动热页：预解析某页为 DisplayList 并缓存，使该页首块瓦片渲染直接命中缓存、 跳过内容流解析（按还原位置热身；第 0 页已在 阶段由 native 顺带预解析）。
    public async Task<bool> WarmupPageAsync(uint pageIndex, CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureReady();
            await AcquireSlotAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(
                    () => GlanceRXNative.mu_engine_warmup_page(_engine, pageIndex),
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

    /// <summary>渲染线程降优先级到 BelowNormal：让所有光栅线程满载也不与 UI 线程(动画)抢核， 从系统调度层面保证动画期 UI 能插队，减少"偶尔卡一帧"。线程池线程每线程首帧设一次。</summary>
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
        var resp = GlanceRXNative.mu_render_page_bgra(
            _engine, pageIndex, width, height,
            IntPtr.Zero,
            out IntPtr data, out ulong len, out uint w, out uint h);
        GlanceRXNative.ThrowIfFailed(resp, "Render page");
        return CopyOut(data, len, w, h);
    }

    /// <summary>把 native BGRA 结果拷成一张【自有的、可被缓存长期持有的】托管缓冲，并释放 native 侧。 用 跳过零初始化：紧接着的 Marshal.Copy 会写满整段，零初始化只是白白跑一遍多 MB 的 memset（每个瓦片都跑）。</summary>
    private static (byte[], int, int, int) CopyOut(IntPtr data, ulong len, uint w, uint h)
    {
        try
        {
            int n = (int)len;
            byte[] owned = GC.AllocateUninitializedArray<byte>(n);
            Marshal.Copy(data, owned, 0, n);
            return (owned, n, (int)w, (int)h);
        }
        finally
        {
            GlanceRXNative.memory_free(data, len);
        }
    }

    private static void ThrowCreateError(GlanceRXNative.EngineCreateResult result, string operation)
    {
        if (result.ErrorMsg == IntPtr.Zero) return;
        string msg = Marshal.PtrToStringUTF8(result.ErrorMsg) ?? "Unknown error";
        GlanceRXNative.memory_free(result.ErrorMsg);
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
            try { GlanceRXNative.mu_engine_destroy(_engine); } catch { }
            _engine = IntPtr.Zero;
        }
        _initialized = false;
        _slot = null;
    }

    /// <summary>物理核数自适应：区分超线程，给出"真实物理并行上限"。 只有 Windows（WinUI 目标）才有 GetLogicalProcessorInformation；失败时回退到逻辑核数。</summary>
    private static class CpuCoreInfo
    {
        private const int RelationProcessorCore = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION
        {
            public UIntPtr ProcessorMask;
            public int Relationship;
            public int Reserved;
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