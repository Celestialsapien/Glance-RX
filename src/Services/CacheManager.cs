using System;
using System.IO;
using System.Linq;

namespace Glance.Services;

/// <summary>
/// 便携版缓存目录管理。
/// 所有"安全可删"的临时/诊断文件（缓存、save_error.txt 等）都放进程序本体目录下的
/// GlanceData\Cache 子目录，绝不写 C 盘 AppData（遵循便携版硬约束）。
/// 该目录内的任何文件被删除都不会影响软件运行；启动时按"保留天数 + 总大小上限"自动清理，
/// 防止目录无限增长。
/// </summary>
public static class CacheManager
{
    /// <summary>缓存子目录名（位于数据根目录 GlanceData 下）。</summary>
    public const string CacheFolderName = "Cache";

    /// <summary>缓存位于数据根目录 GlanceData 之下（GlanceData\Cache），与设置/记忆/日志同样集中收纳。</summary>
    public static readonly string CacheRootDirectory = Path.Combine(AppPaths.DataDirectory, CacheFolderName);

    /// <summary>缓存文件最长保留天数，超过则删除。</summary>
    private const int MaxFileAgeDays = 7;

    /// <summary>缓存目录总大小上限（字节）。超过时按最旧优先删除，直到降到此值以下。</summary>
    private const long MaxTotalBytes = 200L * 1024 * 1024; // 200 MB

    /// <summary>缓存目录完整路径（数据目录下的 Cache 子目录）。</summary>
    public static readonly string CacheDirectory = CacheRootDirectory;

    /// <summary>确保缓存目录存在，返回其路径。失败静默。</summary>
    public static string EnsureCacheDirectory()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory))
                Directory.CreateDirectory(CacheDirectory);
        }
        catch { }
        return CacheDirectory;
    }

    /// <summary>
    /// 生成缓存目录内一个唯一临时文件路径（含扩展名，如 "pdf"）。
    /// 调用方负责在使用后删除；即使漏删，<see cref="Cleanup"/> 也会在下次启动时回收。
    /// </summary>
    public static string GetTempFilePath(string extension)
    {
        EnsureCacheDirectory();
        string ext = string.IsNullOrWhiteSpace(extension) ? "tmp" : extension.TrimStart('.');
        return Path.Combine(CacheDirectory, $"{Guid.NewGuid():N}.{ext}");
    }

    /// <summary>
    /// 返回缓存目录内某个具名文件的完整路径（不自动创建文件）。
    /// 适合放诊断日志等"安全可删"的持久化缓存文件。
    /// </summary>
    public static string GetCacheFilePath(string fileName)
    {
        EnsureCacheDirectory();
        return Path.Combine(CacheDirectory, fileName);
    }

    /// <summary>
    /// 启动时调用：清理过期/超额缓存，防止目录无限增长。
    /// 删除整个 Cache 目录都不影响软件运行。失败静默。
    /// </summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(CacheDirectory)) return;

            var dir = new DirectoryInfo(CacheDirectory);

            // 一次性清理：旧版"未烘焙原始副本"机制已彻底移除（批注改为真实 PDF 注解对象、
            // 文件即唯一真相源），遗留的 anno_base_*.pdf 纯属死数据、无任何引用方，无条件删除。
            foreach (var f in dir.GetFiles("anno_base_*.pdf"))
                TryDelete(f);

            var files = dir.GetFiles()
                .Where(f => !IsLocked(f))
                .ToList();
            if (files.Count == 0) return;

            // 1) 删除超过保留天数的文件
            var cutoff = DateTime.UtcNow.AddDays(-MaxFileAgeDays);
            foreach (var f in files.Where(f => f.LastWriteTimeUtc < cutoff))
            {
                TryDelete(f);
            }
            files = files
                .Where(f => f.Exists && f.LastWriteTimeUtc >= cutoff)
                .ToList();

            // 2) 若仍超过总大小上限，按最旧优先删除
            long total = files.Sum(f => f.Length);
            if (total > MaxTotalBytes)
            {
                foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (total <= MaxTotalBytes) break;
                    total -= f.Length;
                    TryDelete(f);
                }
            }
        }
        catch { }
    }

    private static bool IsLocked(FileInfo f)
    {
        try
        {
            using var s = f.Open(FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(FileInfo f)
    {
        try { f.Delete(); } catch { }
    }
}
