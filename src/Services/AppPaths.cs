using System;
using System.IO;

namespace Glance.Services;

/// <summary>
/// Glance 便携版的「数据根目录」：程序本体目录下的 <c>GlanceData</c> 子目录。
/// </summary>
/// <remarks>
/// 背景：此前 软件设置（settings.json）、文件退出记忆（GlanceViewStates.txt）、书签（bookmarks.json）
/// 与崩溃日志（crash_*.log / crash_native_*.log）全部直接落在 Glance.exe 同级目录里，
/// 和编译产物混在一起——既难找，也无法整体清理。
/// 现在 GlanceData 内再分两层：**数据文件（本文件上述各 *FilePath）** 与
/// **崩溃日志（Crash 子目录）**，诊断不和用户数据同处一个目录。
///
/// ⚠ 硬约束不变：便携版禁止写 C 盘 / AppData / 系统 Temp。这里【不是】把数据搬到别处，
/// 而是在 <see cref="AppContext.BaseDirectory"/> 之下收拢进一个专建子目录：
/// 程序本体目录依然只有 Glance.exe、本机库、Assets 等随包产物，GlanceData 是唯一的数据区。
///
/// 目录不存在时自动创建（首次运行 / 首次拷贝后即落地）。
/// ⚠ 这里【不做】「把 exe 同级的旧数据文件搬进来的迁移」：便携版的标准用法是整目录拷贝，
/// 数据目录随包一起复制，压根不存在"就地升级遗留旧文件"的场景；而重建 bin 时旧数据本来就会丢。
/// 为一个不会发生的情况加一步启动期 IO，只会让根目录多一些无谓的文件操作。
/// </remarks>
internal static class AppPaths
{
    /// <summary>数据子目录名（位于 AppContext.BaseDirectory 之下）。</summary>
    public const string DataFolderName = "GlanceData";

    /// <summary>崩溃日志子目录名，嵌在 <see cref="DataFolderName"/> 之下。
    /// 诊断（crash_*.log / crash_all.log / crash_native_*.log）与设置、退出记忆、书签
    /// **分开两个目录**：清诊断不会误删用户数据，查日志也不会在设置文件里翻。</summary>
    public const string CrashFolderName = "Crash";

    /// <summary>数据根目录完整路径。</summary>
    public static readonly string DataDirectory = SafeRoot();

    /// <summary>崩溃日志目录完整路径 = &lt;BaseDirectory&gt;/GlanceData/Crash。</summary>
    public static readonly string CrashDirectory = Path.Combine(DataDirectory, CrashFolderName);

    /// <summary>软件设置（主题等）。</summary>
    public static readonly string SettingsFilePath = Path.Combine(DataDirectory, "settings.json");

    /// <summary>退出记忆 / 窗口几何（Sumatra 风格：所有 PDF 集中在一个 txt 文件）。</summary>
    public static readonly string ViewStateFilePath = Path.Combine(DataDirectory, "GlanceViewStates.txt");

    /// <summary>书签（以 PDF 完整路径为键）。</summary>
    public static readonly string BookmarksFilePath = Path.Combine(DataDirectory, "bookmarks.json");

    /// <summary>静态构造里立即建目录：数据文件的写点分散在各 Store 里，不能等到第一次写入时才建；
    /// 崩溃日志同理（进程最早阶段就可能落盘）。</summary>
    static AppPaths()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(CrashDirectory);
        }
        catch { /* 可移植版下目录不可建时静默沿用旧行为 */ }
    }

    private static string SafeRoot()
    {
        try
        {
            string dir = AppContext.BaseDirectory;
            return string.IsNullOrEmpty(dir) ? DataFolderName : Path.Combine(dir, DataFolderName);
        }
        catch
        {
            return DataFolderName;
        }
    }

    /// <summary>确保崩溃日志目录存在（含父级数据目录），返回其路径。创建失败静默。</summary>
    public static string EnsureCrashDirectory()
    {
        try
        {
            if (!Directory.Exists(CrashDirectory))
                Directory.CreateDirectory(CrashDirectory);
        }
        catch { }
        return CrashDirectory;
    }
}
