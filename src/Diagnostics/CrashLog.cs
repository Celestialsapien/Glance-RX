// 全局未处理异常兜底：把任何会杀掉进程的异常原样写进
// <BaseDirectory>/GlanceData/Crash/crash_*.log，便于复现后定位。
// 崩溃日志与用户数据（settings/view-state/bookmarks）分处两个目录，清诊断不会误删数据。
// 背景：本项目曾出现「闪退但 appcrash 归档里没有记录」的现象——
// 那是托管异常经 WinUI3 默认 fail-fast 终止进程，WER 不总为这类终止生成 appcrash AV
// 归档，肉眼/事件查看器都难追。此日志器把异常类型/消息/完整堆栈落盘，使崩溃可复盘。
//
// 设计：写完后【不】吞异常——保留进程默认终止行为，保证崩溃可复现且日志已落盘。
// Release 同样生效，因为崩溃与配置无关。
// BaseDirectory 不可写时回退到临时目录，绝不影响进程退出行为。

using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Glance.Diagnostics;

internal static class CrashLog
{
    private static readonly object _lock = new();

    internal static void Capture(string source, Exception? ex)
    {
        try
        {
            string dir = PickDir();
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string path = Path.Combine(dir, $"crash_{stamp}.log");
            string body = Build(source, ex);
            lock (_lock)
            {
                File.WriteAllText(path, body, Encoding.UTF8);
                File.AppendAllText(Path.Combine(dir, "crash_all.log"), body, Encoding.UTF8);
            }
        }
        catch { /* 日志失败绝不能影响进程退出行为 */ }
    }

    /// <summary>调用方已自行吞掉的『本应致命』的异常（如单瓦片渲染失败），记录供定位但不终止进程。</summary>
    internal static void CaptureHandled(string context, Exception ex) => Capture("Handled:" + context, ex);

    private static string Build(string source, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.Now:o}] source={source}");
        sb.AppendLine($"exe={Environment.ProcessPath}");
        sb.AppendLine($"base={AppContext.BaseDirectory}");
        var t = Thread.CurrentThread;
        sb.AppendLine($"thread=#{t.ManagedThreadId} IsBackground={t.IsBackground} Name={t.Name ?? ""}");
        Exception? cur = ex;
        int depth = 0;
        while (cur != null && depth < 8)
        {
            depth++;
            sb.AppendLine($"--- exception[{depth}] {cur.GetType().FullName}: {cur.Message}");
            sb.AppendLine(cur.StackTrace ?? "(no stack trace)");
            cur = cur.InnerException;
        }
        sb.AppendLine();
        return sb.ToString();
    }

    private static string PickDir()
    {
        try
        {
            // 与 native 侧 panic_guard::crash_dir() 相同的落点：
            // 程序本体目录下 GlanceData/Crash 子目录，不写 exe 同级根目录
            // （那里是编译产物，日志混在里面既难找也难清），也不和数据文件同处一个目录。
            string dir = Services.AppPaths.EnsureCrashDirectory();
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            try { return Path.GetTempPath(); } catch { return "."; }
        }
    }
}
