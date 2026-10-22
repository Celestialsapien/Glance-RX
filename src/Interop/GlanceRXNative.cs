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
using System.IO;
using System.Runtime.InteropServices;

namespace GlanceRX.Interop;

/// <summary>P/Invoke wrapper for Rust FFI functions in glance_native.dll</summary>
public static class GlanceRXNative
{
    private const string DllName = "glance_native";

    static GlanceRXNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(GlanceRXNative).Assembly, DllImportResolver);
    }

    /// <summary>Custom DLL import resolver to locate glance_native.dll Searches in multiple locations for the DLL</summary>
    private static IntPtr DllImportResolver(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != DllName)
            return IntPtr.Zero;

        string?[] searchPaths = new string?[]
        {
            AppContext.BaseDirectory,

            Path.Combine(AppContext.BaseDirectory, ".."),

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
                }
            }
        }

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

    [StructLayout(LayoutKind.Sequential)]
    public struct Response
    {
        public bool Success;
        public IntPtr ErrorMsg;      // UTF-8 C string, must be freed with memory_free()
        public IntPtr DataPtr;       // PNG bytes or JSON string, must be freed with memory_free()
        public ulong DataLen;
    }

    /// <summary>Throws if the FFI failed. Frees the Rust-allocated error message on failure.</summary>
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

    [StructLayout(LayoutKind.Sequential)]
    public struct EngineCreateResult
    {
        public IntPtr Engine;        // Opaque MuPDF engine pointer, or Zero on failure
        public IntPtr ErrorMsg;      // UTF-8 C string (non-zero only on non-password failure); free with memory_free()
        public byte NeedsPassword;   // 1 = the PDF is encrypted and needs a password
    }

    // ========================================================================
    // MuPDF Single-Process Engine (single-process multi-thread)

    /// Open a PDF into a single-process MuPDF engine.
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern EngineCreateResult mu_engine_open(IntPtr pdfPath, IntPtr pdfPassword, uint hotCapacity);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool mu_engine_reload(IntPtr engine);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mu_engine_destroy(IntPtr engine);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U4)]
    public static extern uint mu_engine_page_count(IntPtr engine);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool mu_engine_page_dimensions(
        IntPtr engine, uint pageIndex, out float outWidth, out float outHeight);

    /// Get page dimensions for pages 0..count-1 in ONE call into a caller-provided buffer of 2*count floats…
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool mu_engine_page_dimensions_all(IntPtr engine, uint count, IntPtr outDims);

    /// <summary>Pre-parse a page into its DisplayList and cache it (cold-start warmup for the restore-target page;</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool mu_engine_warmup_page(IntPtr engine, uint page);

    /// Render one tile (an axis-aligned sub-rectangle of a page's device space) into tightly-packed BGRA8 (…
    /// must point to a 24-byte fz_cookie-compatible abort block created by
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Response mu_render_page_region_bgra(
        IntPtr engine, uint page, uint devW, uint devH,
        uint x0, uint y0, uint x1, uint y1,
        IntPtr cookie,
        out IntPtr outData, out ulong outLen, out uint outWidth, out uint outHeight);

    /// Render a whole page into tightly-packed BGRA8.
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Response mu_render_page_bgra(
        IntPtr engine, uint page, uint width, uint height,
        IntPtr cookie,
        out IntPtr outData, out ulong outLen, out uint outWidth, out uint outHeight);

    // ========================================================================

    /// <summary>Free memory allocated by Rust FFI functions. MUST be called for all non-null pointers returned in Response.error_msg or Response.data_ptr.</summary>
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void memory_free(IntPtr ptr, ulong len = 0);

    // ========================================================================

    /// <summary>Marshal a .NET string to a UTF-8 encoded C string pointer. The returned pointer must be freed with FreeUtf8() or Marshal.FreeHGlobal().</summary>
    public static IntPtr MarshalUtf8(string s)
    {
        if (s == null) return IntPtr.Zero;
        byte[] utf8Bytes = System.Text.Encoding.UTF8.GetBytes(s);
        int bytesLen = utf8Bytes.Length + 1;
        IntPtr ptr = Marshal.AllocHGlobal(bytesLen);
        Marshal.Copy(utf8Bytes, 0, ptr, utf8Bytes.Length);
        Marshal.WriteByte(ptr, bytesLen - 1, 0);
        return ptr;
    }

    /// <summary>Free a pointer allocated by MarshalUtf8().</summary>
    public static void FreeUtf8(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

}

/// fz_cookie-compatible abort block（= Sumatra FitzAbortCookie 的对应物）。
public static class GlanceRXCookie
{
    private const int Size = 24;

    public static IntPtr New()
    {
        IntPtr p = System.Runtime.InteropServices.Marshal.AllocHGlobal(Size);
        try
        {
            for (int off = 0; off < Size; off += 8) System.Runtime.InteropServices.Marshal.WriteInt64(p, off, 0);
            System.Runtime.InteropServices.Marshal.WriteInt64(p, 8, -1);
        }
        catch
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(p);
            throw;
        }
        return p;
    }

    public static void Abort(IntPtr cookie)
    {
        if (cookie == IntPtr.Zero) return;
        System.Runtime.InteropServices.Marshal.WriteInt32(cookie, 0, 1);
    }

    public static void Free(IntPtr cookie)
    {
        if (cookie == IntPtr.Zero) return;
        System.Runtime.InteropServices.Marshal.FreeHGlobal(cookie);
    }
}