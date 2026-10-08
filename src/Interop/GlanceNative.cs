// Glance - a high-performance PDF reader
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
using System.IO;
using System.Runtime.InteropServices;

namespace Glance.Interop;

/// <summary>
/// P/Invoke wrapper for Rust FFI functions in glance_native.dll
/// </summary>
public static class GlanceNative
{
    private const string DllName = "glance_native";

    // Static constructor to register DLL import resolver
    static GlanceNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(GlanceNative).Assembly, DllImportResolver);
    }

    /// <summary>
    /// Custom DLL import resolver to locate glance_native.dll
    /// Searches in multiple locations for the DLL
    /// </summary>
    private static IntPtr DllImportResolver(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != DllName)
            return IntPtr.Zero;


        // Try multiple locations
        string?[] searchPaths = new string?[]
        {
            // 1. Current application directory
            AppContext.BaseDirectory,

            // 2. Application directory + one level up (for packaged apps)
            Path.Combine(AppContext.BaseDirectory, ".."),

            // 3. System PATH (let OS handle it)
            null
        };

        foreach (var searchPath_item in searchPaths)
        {
            if (searchPath_item == null)
                break; // Skip system PATH for now, use it only as last resort

            string dllPath = Path.Combine(searchPath_item, "glance_native.dll");

            if (File.Exists(dllPath))
            {
                try
                {
                    IntPtr handle = NativeLibrary.Load(dllPath);
                    return handle;
                }
                catch (Exception)
                {
                    // Continue searching
                }
            }
        }

        // Let the system try to find it in PATH
        try
        {
            return NativeLibrary.Load(DllName);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    // ========================================================================
    // Response Structure (must match Rust #[repr(C)])
    // ========================================================================

    [StructLayout(LayoutKind.Sequential)]
    public struct Response
    {
        public bool Success;
        public IntPtr ErrorMsg;      // UTF-8 C string, must be freed with memory_free()
        public IntPtr DataPtr;       // PNG bytes or JSON string, must be freed with memory_free()
        public ulong DataLen;        // Length of data_ptr
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if the FFI <paramref name="response"/>
    /// failed. Frees the Rust-allocated error message on failure. On success, returns
    /// normally so the caller can continue reading <c>DataPtr</c>.
    /// </summary>
    public static void ThrowIfFailed(Response response, string operation)
    {
        if (response.Success) return;

        string errorMsg;
        if (response.ErrorMsg != IntPtr.Zero)
        {
            errorMsg = Marshal.PtrToStringUTF8(response.ErrorMsg) ?? "Unknown error";
            memory_free(response.ErrorMsg);
        }
        else
        {
            errorMsg = "Unknown error (null error message)";
        }

        throw new InvalidOperationException($"{operation}: {errorMsg}");
    }

    // ========================================================================
    // Engine Create Result Structure (must match Rust #[repr(C)])
    // Field order matches the Rust side: two 8-byte pointers then a 1-byte
    // bool, so there is no inter-field padding to misalign the structs.
    // ========================================================================

    [StructLayout(LayoutKind.Sequential)]
    public struct EngineCreateResult
    {
        public IntPtr Engine;        // Opaque MuPDF engine pointer, or Zero on failure
        public IntPtr ErrorMsg;      // UTF-8 C string (non-zero only on non-password failure); free with memory_free()
        public byte NeedsPassword;   // 1 = the PDF is encrypted and needs a password
    }

    // ========================================================================
    // MuPDF Single-Process Engine (single-process multi-thread)
    //
    // Replaces the out-of-process worker pool. A single MupdfEngineImpl lives in
    // this process; its methods are thread-safe, so tile renders can be fired
    // concurrently from the .NET thread pool with NO global lock. `engine` is the
    // opaque handle from mu_engine_open, passed to every call, and must be freed
    // with mu_engine_destroy.
    // ========================================================================

    /// <summary>
    /// Open a PDF into a single-process MuPDF engine.
    /// Receives path (marshalled UTF-8) + optional password + parsed-page cache
    /// capacity. Returns an <see cref="EngineCreateResult"/>: on success
    /// <c>Engine</c> is the opaque handle; if the document is encrypted
    /// <c>NeedsPassword</c> is 1; otherwise <c>ErrorMsg</c> holds a UTF-8 message
    /// (free with memory_free()).
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern EngineCreateResult mu_engine_open(IntPtr pdfPath, IntPtr pdfPassword, uint hotCapacity);

    /// <summary>Reload the MuPDF document to flush its object/display-list cache.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool mu_engine_reload(IntPtr engine);

    /// <summary>Destroy a MuPDF engine and free its document + display lists.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mu_engine_destroy(IntPtr engine);

    /// <summary>Get total page count from the MuPDF engine.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U4)]
    public static extern uint mu_engine_page_count(IntPtr engine);

    /// <summary>Get page dimensions (in points) from the MuPDF engine.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool mu_engine_page_dimensions(
        IntPtr engine, uint pageIndex, out float outWidth, out float outHeight);

    /// <summary>
    /// Get page dimensions for pages 0..count-1 in ONE call into a caller-provided
    /// buffer of 2*count floats (w0,h0,w1,h1,...). Failed pages are written as
    /// (0,0). Single lock hold + single FFI round trip replaces N per-page calls,
    /// much faster on large PDFs during load.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool mu_engine_page_dimensions_all(IntPtr engine, uint count, IntPtr outDims);

    /// <summary>
    /// Pre-parse a page into its DisplayList and cache it (cold-start warmup for
    /// the restore-target page; the first tile render then hits the cache).
    /// Returns true on success.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool mu_engine_warmup_page(IntPtr engine, uint page);

    /// <summary>
    /// Render one tile (an axis-aligned sub-rectangle of a page's device space)
    /// into tightly-packed BGRA8 (stride = width*4). THREAD-SAFE. On success
    /// <paramref name="outData"/> is a Rust-allocated buffer (free with
    /// memory_free(ptr, len)), outLen/outWidth/outHeight describe it.
    /// <paramref name="cookie"/> may be <see cref="IntPtr.Zero"/>; otherwise it
    /// must point to a 24-byte fz_cookie-compatible abort block created by
    /// <see cref="GlanceCookie.New"/> — MuPDF checks it between display-list ops
    /// and cancels this render mid-flight when the aborter writes 1.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Response mu_render_page_region_bgra(
        IntPtr engine, uint page, uint devW, uint devH,
        uint x0, uint y0, uint x1, uint y1,
        IntPtr cookie,
        out IntPtr outData, out ulong outLen, out uint outWidth, out uint outHeight);

    /// <summary>
    /// Render a whole page into tightly-packed BGRA8. THREAD-SAFE. On success
    /// <paramref name="outData"/> is a Rust-allocated buffer (free with
    /// memory_free(ptr, len)). <paramref name="cookie"/> semantics as in
    /// <see cref="mu_render_page_region_bgra"/>.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Response mu_render_page_bgra(
        IntPtr engine, uint page, uint width, uint height,
        IntPtr cookie,
        out IntPtr outData, out ulong outLen, out uint outWidth, out uint outHeight);

    // ========================================================================
    // Memory Management
    // ========================================================================

    /// <summary>
    /// Free memory allocated by Rust FFI functions.
    /// MUST be called for all non-null pointers returned in Response.error_msg or Response.data_ptr.
    /// </summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void memory_free(IntPtr ptr, ulong len = 0);

    // ========================================================================
    // UTF-8 String Marshaling Helpers
    // ========================================================================

    /// <summary>
    /// Marshal a .NET string to a UTF-8 encoded C string pointer.
    /// The returned pointer must be freed with FreeUtf8() or Marshal.FreeHGlobal().
    /// </summary>
    public static IntPtr MarshalUtf8(string s)
    {
        if (s == null) return IntPtr.Zero;
        byte[] utf8Bytes = System.Text.Encoding.UTF8.GetBytes(s);
        int bytesLen = utf8Bytes.Length + 1; // +1 for null terminator
        IntPtr ptr = Marshal.AllocHGlobal(bytesLen);
        Marshal.Copy(utf8Bytes, 0, ptr, utf8Bytes.Length);
        Marshal.WriteByte(ptr, bytesLen - 1, 0); // null terminator
        return ptr;
    }

    /// <summary>
    /// Free a pointer allocated by MarshalUtf8().
    /// </summary>
    public static void FreeUtf8(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

}

/// <summary>
/// fz_cookie-compatible abort block（= Sumatra <c>FitzAbortCookie</c> 的对应物）。
/// 24 字节 C# 侧分配的共享内存，布局与 MuPDF <c>fz_cookie</c>（fitz/device.h）逐字段一致：
/// <code>
/// struct fz_cookie {
///     int abort;            // offset 0  —— aborter 跨线程写 1
///     int progress;         // offset 4
///     size_t progress_max;  // offset 8  —— 置 (size_t)-1 防 MuPDF 预计数（同 Sumatra）
///     int errors;           // offset 16
///     int incomplete;       // offset 20
/// };
/// </code>
/// 生命周期（1:1 RenderCache.cpp:1107/820）：渲染 worker 出请求前 New → 存入
/// _curReqs[threadIdx]（aborter 经同一对象可见）→ 传 FFI → 渲染返回且
/// _curReqs 置空后（持 _requestAccess）Free。aborter 写 abort 与 worker Free
/// 都在 _requestAccess 临界区内 → 无 use-after-free。
/// </summary>
public static class GlanceCookie
{
    private const int Size = 24; // sizeof(fz_cookie) on x64

    /// <summary>[渲染 worker] 分配零初始化的 abort block，progress_max=(size_t)-1（同 Sumatra 构造）。</summary>
    public static IntPtr New()
    {
        IntPtr p = System.Runtime.InteropServices.Marshal.AllocHGlobal(Size);
        try
        {
            // 零初始化（abort=0, progress=0, errors=0, incomplete=0）
            for (int off = 0; off < Size; off += 8) System.Runtime.InteropServices.Marshal.WriteInt64(p, off, 0);
            System.Runtime.InteropServices.Marshal.WriteInt64(p, 8, -1); // progress_max = (size_t)-1
        }
        catch
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(p);
            throw;
        }
        return p;
    }

    /// <summary>[aborter, 须持 TileRenderCache._requestAccess] AbortCookie::Abort() 对应物。</summary>
    public static void Abort(IntPtr cookie)
    {
        if (cookie == IntPtr.Zero) return;
        System.Runtime.InteropServices.Marshal.WriteInt32(cookie, 0, 1);
    }

    /// <summary>[渲染 worker, 须持 _requestAccess] 释放（保证 aborter 已离开临界区）。</summary>
    public static void Free(IntPtr cookie)
    {
        if (cookie == IntPtr.Zero) return;
        System.Runtime.InteropServices.Marshal.FreeHGlobal(cookie);
    }
}