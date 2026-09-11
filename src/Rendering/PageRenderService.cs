// 1:1 port of SumatraPDF src/PageRenderService.{h,cpp}
// (sumatrapdf-master/src/PageRenderService.h / PageRenderService.cpp).
//
// Architecture parity notes (Glance <-> Sumatra):
//   Mutex + ConditionVariable      -> lock(object) + Monitor.Wait/PulseAll
//   ThreadHandle worker            -> dedicated Thread (named "page-render")
//   EngineBase* engine (Clone())   -> shared PdfRenderService. Sumatra clones the engine to
//                                     give the worker an isolated engine; Glance's MuPDF engine
//                                     parses each page once into a ref-counted display list and
//                                     replays it on per-thread fz_contexts, so sharing one
//                                     engine across threads is the equivalent isolation. The
//                                     service still participates in the engine's render slots.
//   EngineBase::RenderPage(args)   -> PdfRenderService.RenderPageBgraAsync (blocking wait on the
//                                     worker thread, same synchronous call shape)
//   AbortCookie                    -> not available in the MuPDF path; the identical
//                                     generation check before AddToCache covers staleness
//                                     (an aborted/late render result is simply not accepted).
//   Pixmap* + FreePixmap           -> owned byte[] BGRA pixels + (Width, Height); GC frees
//   ClonePixmap (CopyPage)         -> immutable cached buffer handed out by reference
//                                     (entry bytes are never mutated in place, only evicted)
//   Gfx::DrawPixmap (DrawPage)     -> the shell draws the bitmap itself via Win2D; this port
//                                     exposes TryGetPage (cache hit + lastUse touch) so the
//                                     shell's Draw loop can consume it like DocumentView does.
//   PlatformPostTask (PostNotify)  -> DispatcherQueue.TryEnqueue (UI thread callback)

using System;
using System.Collections.Generic;
using System.Threading;
using Glance.Services;
using Microsoft.UI.Dispatching;

namespace Glance.Rendering;

/// <summary>A cached full-resolution page bitmap (Sumatra: PageRenderCacheEntry holding a Pixmap*).</summary>
internal readonly struct CachedPageBitmap
{
    public readonly byte[] Pixels; // tightly packed BGRA8, exactly Length bytes
    public readonly int Width;
    public readonly int Height;

    public CachedPageBitmap(byte[] pixels, int width, int height)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
    }

    public long ByteSize => (long)Width * Height * 4;
}

/// <summary>
/// Sumatra's page render service: a single background worker thread fed by a
/// priority policy (Visible &gt; Nearby &gt; Background, FIFO within a priority) and a
/// fixed byte-budget LRU cache that protects the newest entry while evicting.
/// </summary>
internal sealed class PageRenderService : IDisposable
{
    /// <summary>Sumatra default: PageRenderService::Create(..., maxBytes = 96 * 1024 * 1024).</summary>
    public const long DefaultMaxBytes = 96L * 1024 * 1024;

    private const string WorkerName = "page-render";

    private readonly object _mutex = new();
    private Thread? _worker;
    private readonly PdfRenderService _engine;
    private readonly float[] _pageDims;               // pt w0,h0,w1,h1,... (immutable snapshot from the shell)
    private readonly double _dpiFactor;               // pt -> device px multiplier (shell _dpiScale)
    private readonly List<PageRenderPolicyRequest> _requests = new();
    private readonly List<PageRenderCacheEntry> _cache = new();
    private readonly DispatcherQueue _dispatcher;     // PlatformPostTask target (UI thread)
    private readonly Action<PageRenderKey> _onPageReady;
    private long _cacheBytes;
    private readonly long _maxBytes;
    private ulong _serial;
    private ulong _useSerial;
    private uint _generation = 1;
    private PageRenderKey _activeKey;
    private bool _hasActive;
    private bool _stopping;
    private bool _workerStopped;

    private PageRenderService(PdfRenderService engine, float[] pageDims, double dpiFactor,
        DispatcherQueue dispatcher, Action<PageRenderKey> onPageReady, long maxBytes)
    {
        _engine = engine;
        _pageDims = pageDims;
        _dpiFactor = dpiFactor;
        _dispatcher = dispatcher;
        _onPageReady = onPageReady;
        _maxBytes = maxBytes;
    }

    // Sumatra: PageRenderService::Create(engine, onPageReady, maxBytes) — returns null on
    // invalid args. Glance adds pageDims/dpiFactor (the engine derives pixel sizes from them,
    // like EngineBase::RenderPage derives a bitmap size from zoom).
    public static PageRenderService? Create(PdfRenderService engine, float[] pageDimsPt, double dpiFactor,
        DispatcherQueue dispatcher, Action<PageRenderKey> onPageReady, long maxBytes = DefaultMaxBytes)
    {
        if (engine == null || pageDimsPt == null || pageDimsPt.Length < 2 ||
            dispatcher == null || onPageReady == null || maxBytes <= 0)
        {
            return null;
        }

        var service = new PageRenderService(engine, pageDimsPt, dpiFactor, dispatcher, onPageReady, maxBytes);
        var worker = new Thread(service.RenderWorker)
        {
            IsBackground = true,
            Name = WorkerName,
            Priority = ThreadPriority.BelowNormal, // match PdfRenderService's render-thread priority policy
        };
        worker.Start();
        service._worker = worker;
        return service;
    }

    /// <summary>Bytes currently held by the cache (Sumatra: CacheBytes()).</summary>
    public long CacheBytes
    {
        get
        {
            lock (_mutex)
            {
                return _cacheBytes;
            }
        }
    }

    /// <summary>
    /// True while the worker is rendering or requests are queued. Glance shell addition used
    /// by the idle cache-flush guard (the old _previewInflight check) so an idle GC/engine
    /// reload never lands in the middle of a kernel render.
    /// </summary>
    public bool HasInflight
    {
        get
        {
            lock (_mutex)
            {
                return _hasActive || _requests.Count > 0;
            }
        }
    }

    // Sumatra: NewGeneration() — bump generation, drop queued requests, clear cache, abort active.
    public void NewGeneration()
    {
        lock (_mutex)
        {
            _generation++;
            _requests.Clear();
            ClearCacheLocked();
            // AbortCookie equivalent: the in-flight render (if any) completes but its result is
            // rejected below because request.generation != _generation.
        }
    }

    // Sumatra: Request(key, priority) — dedup against cache and the active render, upsert into
    // the policy queue, wake the worker.
    public void Request(PageRenderKey key, PageRenderPriority priority)
    {
        lock (_mutex)
        {
            if (_stopping || FindCachedLocked(key) >= 0 ||
                (_hasActive && _activeKey.Equals(key)))
            {
                return;
            }
            PageRenderPolicyRequest request;
            request.Key = key;
            request.Priority = priority;
            request.Generation = _generation;
            request.Serial = ++_serial;
            PageRenderPolicy.Upsert(_requests, request);
            Monitor.Pulse(_mutex);
        }
    }

    // Sumatra: CopyPage(key) — cache hit + lastUse touch. Returns the immutable cached buffer
    // by reference (ClonePixmap equivalent; the entry bytes are never mutated in place).
    public bool TryGetPage(PageRenderKey key, out CachedPageBitmap page)
    {
        lock (_mutex)
        {
            int idx = FindCachedLocked(key);
            if (idx < 0)
            {
                page = default;
                return false;
            }
            PageRenderCacheEntry entry = _cache[idx];
            entry.Policy.LastUse = ++_useSerial;
            _cache[idx] = entry;
            page = new CachedPageBitmap(_cache[idx].Pixels, _cache[idx].Width, _cache[idx].Height);
            return true;
        }
    }

    // ---- worker ------------------------------------------------------------

    // Sumatra: RenderWorker — pick by policy, render off-lock, accept only if the generation
    // still matches, cache under budget, notify the UI thread.
    private void RenderWorker()
    {
        while (true)
        {
            PageRenderPolicyRequest request;
            lock (_mutex)
            {
                while (!_stopping && _requests.Count == 0)
                {
                    Monitor.Wait(_mutex);
                }
                if (_stopping)
                {
                    _workerStopped = true;
                    Monitor.PulseAll(_mutex);
                    return;
                }

                int requestIdx = PageRenderPolicy.PickRequest(_requests);
                request = _requests[requestIdx];
                _requests.RemoveAt(requestIdx);
                _activeKey = request.Key;
                _hasActive = true;
            }

            // engine->RenderPage(RenderPageArgs(..., &activeCookie)) equivalent: synchronous
            // render on the worker thread, off the lock.
            byte[]? pixels = null;
            int w = 0, h = 0;
            try
            {
                PageDims(request.Key.PageNo, out float ptW, out float ptH);
                int rw = (int)Math.Clamp(Math.Ceiling(ptW * request.Key.Zoom * _dpiFactor), 16L, 16384L);
                int rh = (int)Math.Clamp(Math.Ceiling(ptH * request.Key.Zoom * _dpiFactor), 16L, 16384L);
                var (buf, bw, bh) = _engine.RenderPageBgraAsync((uint)request.Key.PageNo, (uint)rw, (uint)rh)
                    .GetAwaiter().GetResult();
                if (buf != null && bw > 0 && bh > 0)
                {
                    pixels = buf;
                    w = bw;
                    h = bh;
                }
            }
            catch
            {
                pixels = null;
            }

            bool accepted = false;
            lock (_mutex)
            {
                _hasActive = false;
                if (!_stopping && request.Generation == _generation && pixels != null)
                {
                    accepted = AddToCacheLocked(request.Key, pixels, w, h);
                }
            }
            if (accepted)
            {
                PostNotify(request.Key);
            }
        }
    }

    // Sumatra: AddToCache — add only if it fits the fixed budget; protect the new entry while
    // evicting older LRU entries so a useful visible result survives.
    private bool AddToCacheLocked(PageRenderKey key, byte[] pixels, int width, int height)
    {
        long bytes = (long)width * height * 4;
        if (bytes <= 0 || bytes > _maxBytes)
        {
            return false; // Sumatra frees the pixmap here; GC frees ours
        }
        int old = FindCachedLocked(key);
        if (old >= 0)
        {
            RemoveCacheEntryLocked(old);
        }

        var entry = new PageRenderCacheEntry { Key = key, Pixels = pixels, Width = width, Height = height };
        entry.Policy.Bytes = bytes;
        entry.Policy.LastUse = ++_useSerial;
        _cache.Add(entry);
        _cacheBytes += bytes;

        int protectedIndex = _cache.Count - 1;
        while (_cacheBytes > _maxBytes)
        {
            var policyEntries = new List<PageRenderPolicyCacheEntry>(_cache.Count);
            foreach (PageRenderCacheEntry cached in _cache)
            {
                policyEntries.Add(cached.Policy);
            }
            int evict = PageRenderPolicy.PickEviction(policyEntries, protectedIndex);
            if (evict < 0)
            {
                break;
            }
            RemoveCacheEntryLocked(evict);
            if (evict < protectedIndex)
            {
                protectedIndex--;
            }
        }
        return true;
    }

    private int FindCachedLocked(PageRenderKey key)
    {
        for (int i = 0; i < _cache.Count; i++)
        {
            if (_cache[i].Key.Equals(key))
            {
                return i;
            }
        }
        return -1;
    }

    private void RemoveCacheEntryLocked(int idx)
    {
        _cacheBytes -= _cache[idx].Policy.Bytes;
        _cache.RemoveAt(idx);
    }

    private void ClearCacheLocked()
    {
        _cache.Clear();
        _cacheBytes = 0;
    }

    private void PageDims(int pageNo, out float w, out float h)
    {
        int i = pageNo * 2;
        if (pageNo >= 0 && i + 1 < _pageDims.Length)
        {
            w = _pageDims[i];
            h = _pageDims[i + 1];
        }
        else
        {
            w = 612f; // US Letter fallback (engine render will still clamp to sane sizes)
            h = 792f;
        }
    }

    // Sumatra: PostNotify — ref-counted notify posted to the main thread. Glance's notify is
    // fire-and-forget (one callback per accepted render); the shell re-checks the cache.
    private void PostNotify(PageRenderKey key)
    {
        _dispatcher.TryEnqueue(() => _onPageReady(key));
    }

    // Sumatra: ~PageRenderService — stop, wake, join, clear cache, release engine reference.
    public void Dispose()
    {
        lock (_mutex)
        {
            if (_stopping)
            {
                return;
            }
            _stopping = true;
            _requests.Clear();
            Monitor.PulseAll(_mutex);
            while (!_workerStopped)
            {
                Monitor.Wait(_mutex);
            }
            ClearCacheLocked();
        }
    }

    /// <summary>Cache entry: policy view + the bitmap (Sumatra: PageRenderCacheEntry).</summary>
    private struct PageRenderCacheEntry
    {
        public PageRenderKey Key;
        public PageRenderPolicyCacheEntry Policy;
        public byte[] Pixels;
        public int Width;
        public int Height;
    }
}
