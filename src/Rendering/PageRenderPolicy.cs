// 1:1 port of SumatraPDF src/PageRenderPolicy.{h,cpp}
// (sumatrapdf-master/src/PageRenderPolicy.h / PageRenderPolicy.cpp).
// Pure policy, no state, no I/O — identical semantics, identical iteration order.
// Glance note: C++ Vec<T> -> System.Collections.Generic.List<T>; u32/u64/i64 -> uint/ulong/long.

using System;
using System.Collections.Generic;

namespace Glance.Rendering;

/// <summary>Lower values are more urgent (Sumatra: Visible &lt; Nearby &lt; Background).</summary>
internal enum PageRenderPriority
{
    Visible,
    Nearby,
    Background,
}

/// <summary>
/// Cache/render identity of a page bitmap.
/// Glance note: <see cref="Rotation"/> is kept for 1:1 parity with the Sumatra struct,
/// but the Glance shell applies rotation at draw time (GPU matrix) and always passes 0 —
/// bitmaps are rendered unrotated, so rotating never invalidates the kernel cache.
/// </summary>
internal readonly struct PageRenderKey : IEquatable<PageRenderKey>
{
    public readonly int PageNo;
    public readonly float Zoom;
    public readonly int Rotation;

    public PageRenderKey(int pageNo, float zoom, int rotation)
    {
        PageNo = pageNo;
        Zoom = zoom;
        Rotation = rotation;
    }

    public bool Equals(PageRenderKey other)
        => PageNo == other.PageNo && Zoom == other.Zoom && Rotation == other.Rotation;

    public override bool Equals(object? obj) => obj is PageRenderKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(PageNo, Zoom, Rotation);
}

/// <summary>One queued render request (Sumatra: PageRenderPolicyRequest).</summary>
internal struct PageRenderPolicyRequest
{
    public PageRenderKey Key;
    public PageRenderPriority Priority;
    public uint Generation;
    public ulong Serial;
}

/// <summary>Policy view of a cache entry used for eviction decisions (Sumatra: PageRenderPolicyCacheEntry).</summary>
internal struct PageRenderPolicyCacheEntry
{
    public long Bytes;
    public ulong LastUse;
}

/// <summary>
/// 1:1 port of the SumatraPDF page render policy functions. Pure functions over the
/// request/cache vectors; the service owns the vectors and the synchronization.
/// </summary>
internal static class PageRenderPolicy
{
    // Keep at most one queued request per page and generation. A newer viewport
    // request replaces obsolete render parameters and can raise its priority.
    public static void Upsert(List<PageRenderPolicyRequest> requests, in PageRenderPolicyRequest request)
    {
        for (int i = 0; i < requests.Count; i++)
        {
            PageRenderPolicyRequest existing = requests[i];
            if (existing.Generation != request.Generation || existing.Key.PageNo != request.Key.PageNo)
            {
                continue;
            }
            requests[i] = request;
            return;
        }
        requests.Add(request);
    }

    public static void DropStale(List<PageRenderPolicyRequest> requests, uint generation)
    {
        for (int i = requests.Count - 1; i >= 0; i--)
        {
            if (requests[i].Generation != generation)
            {
                requests.RemoveAt(i);
            }
        }
    }

    // Lower enum values are more urgent. Requests within the same priority remain
    // FIFO so fast scrolling does not permanently starve an older visible page.
    public static int PickRequest(List<PageRenderPolicyRequest> requests)
    {
        int best = -1;
        for (int i = 0; i < requests.Count; i++)
        {
            if (best < 0 || requests[i].Priority < requests[best].Priority ||
                (requests[i].Priority == requests[best].Priority && requests[i].Serial < requests[best].Serial))
            {
                best = i;
            }
        }
        return best;
    }

    public static int PickEviction(List<PageRenderPolicyCacheEntry> entries, int protectedIndex)
    {
        int oldest = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            if (i == protectedIndex)
            {
                continue;
            }
            if (oldest < 0 || entries[i].LastUse < entries[oldest].LastUse)
            {
                oldest = i;
            }
        }
        return oldest;
    }

#if DEBUG
    // 1:1 port of PageRenderPolicy_UnitTests() (Sumatra DEBUG self-test).
    public static void UnitTests()
    {
        var requests = new List<PageRenderPolicyRequest>();
        Upsert(requests, new PageRenderPolicyRequest { Key = new PageRenderKey(1, 1.0f, 0), Priority = PageRenderPriority.Background, Generation = 4, Serial = 1 });
        Upsert(requests, new PageRenderPolicyRequest { Key = new PageRenderKey(2, 1.0f, 0), Priority = PageRenderPriority.Nearby, Generation = 4, Serial = 2 });
        Upsert(requests, new PageRenderPolicyRequest { Key = new PageRenderKey(3, 1.0f, 0), Priority = PageRenderPriority.Visible, Generation = 4, Serial = 3 });
        System.Diagnostics.Debug.Assert(PickRequest(requests) == 2);

        Upsert(requests, new PageRenderPolicyRequest { Key = new PageRenderKey(1, 2.0f, 90), Priority = PageRenderPriority.Visible, Generation = 4, Serial = 4 });
        System.Diagnostics.Debug.Assert(requests.Count == 3);
        var replacement = new PageRenderKey(1, 2.0f, 90);
        System.Diagnostics.Debug.Assert(requests[0].Key.Equals(replacement));
        System.Diagnostics.Debug.Assert(requests[0].Priority == PageRenderPriority.Visible);

        Upsert(requests, new PageRenderPolicyRequest { Key = new PageRenderKey(4, 1.0f, 0), Priority = PageRenderPriority.Visible, Generation = 3, Serial = 5 });
        DropStale(requests, 4);
        System.Diagnostics.Debug.Assert(requests.Count == 3);
        foreach (PageRenderPolicyRequest request in requests)
        {
            System.Diagnostics.Debug.Assert(request.Generation == 4);
        }

        var cache = new List<PageRenderPolicyCacheEntry>();
        cache.Add(new PageRenderPolicyCacheEntry { Bytes = 10, LastUse = 20 });
        cache.Add(new PageRenderPolicyCacheEntry { Bytes = 10, LastUse = 5 });
        cache.Add(new PageRenderPolicyCacheEntry { Bytes = 10, LastUse = 12 });
        System.Diagnostics.Debug.Assert(PickEviction(cache, 0) == 1);
        System.Diagnostics.Debug.Assert(PickEviction(cache, 1) == 2);
    }
#endif
}
