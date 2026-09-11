// Diagnostic log for the Sumatra kernel wiring (render→accept→notify→draw chain).
// DEBUG-only: Log is [Conditional("DEBUG")] — Release 编译直接剔除调用（无文件写入、可移植性安全）。
// Written to AppContext.BaseDirectory (portable-safe).

using System;
using System.IO;
using System.Text;

namespace Glance.Rendering;

internal static class KernelDebugLog
{
    private static readonly object _lock = new();
    private static string? _path;

    [System.Diagnostics.Conditional("DEBUG")]
    internal static void Log(string message)
    {
        try
        {
            if (_path == null)
            {
                _path = Path.Combine(AppContext.BaseDirectory, "kernel_debug.log");
                File.WriteAllText(_path, $"==== kernel debug {DateTime.Now:HH:mm:ss.fff} ====\n");
            }
            lock (_lock)
            {
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {message}\n", Encoding.UTF8);
            }
        }
        catch { }
    }
}
