using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Glance
{
    /// <summary>
    /// 设计手感调参中心 / Design-feel tuning hub.
    ///
    /// 原本这些值是散落在 GpuPageView / MainPage 里的编译期 const。
    /// 现已集中到这里，并在程序启动时从同目录（或 统计 / Stats 子目录）下的
    /// config.txt 读取覆盖 —— 改完重启 Glance 即可生效，无需重新编译。
    ///
    /// These used to be compile-time consts scattered across GpuPageView / MainPage.
    /// They now live here and are overridden at startup from config.txt (same dir, or a
    /// 统计 / Stats subfolder). Edit the file and restart Glance to apply — no recompile.
    ///
    /// 每个字段的「默认值」即下方初始化值，与改造前逐字一致，方便回退。
    /// Each field's initializer IS the default value, byte-for-byte identical to before, for easy revert.
    /// </summary>
    public static class Tuning
    {

        // 一份【全默认值】的 config.txt 文本，用于"删文件=恢复出厂"时自动重新生成。
        // A full-default config.txt text, used to auto-regenerate when the file is deleted.
        // 此文本与 src/config.txt 保持一致（由工具从 config.txt 生成）。
        // Kept identical to src/config.txt (generated from it by tooling).
        private const string DefaultConfigText = @"# ============================================================================
# Glance 设计手感调参文件 / Glance Design-Feel Tuning
# ----------------------------------------------------------------------------
# 改完本文件后【重启 Glance】即可生效，无需重新编译。
# Edit this file and RESTART Glance to apply — no recompile needed.
#
# 每行格式 / Format:   键名 = 值   # 注释
#   - 以 # 开头的行是注释，忽略。/ Lines starting with # are ignored.
#   - 只写你想要的键即可；没写的键自动保留默认值（=下方初始值）。
#     Only list the keys you want to change; omitted keys keep their default.
#   - 想回退某项：直接删掉那一行（或改回""默认""列的值）。
#     To revert a key: delete its line (or set it back to the default value).
#
# 一键回退（恢复出厂）/ One-click factory reset:
#   直接删除本文件即可。下次打开 Glance 会以【全部默认值】运行，并在本目录
#   （或默认位置）自动重新生成一份【全默认值】的 config.txt —— 等价于""恢复出厂设置""。
#   Simply delete this file. Next launch runs with ALL defaults and auto-regenerates a
#   full-default config.txt in this (or the default) location — i.e. a factory reset.
# ============================================================================


# ---------------- 缩放范围 / Zoom range ----------------
# 最小缩放比例（0.1 = 10%）。调小可整页预览超大跨页杂志。
# Min zoom ratio (0.1 = 10%). Lower = fit huge spreads on screen.
MinZoom = 0.1                     # 默认 0.1 | default

# 最大缩放比例（4.0 = 400%）。
# Max zoom ratio (4.0 = 400%).
MaxZoom = 4.0                     # 默认 4.0 | default


# ---------------- 布局留白 / Layout margins (DIP) ----------------
# 文档四周留白（DIP）。/ Margin around the document (DIP).
DocMargin = 16.0                  # 默认 16.0 | default

# 纵向连续布局页间距（DIP）。/ Vertical continuous layout page gap (DIP).
PageGap = 16.0                    # 默认 16.0 | default

# 横向连续布局页间距（DIP）。书本/杂志横向串页时更""贴""，破坏感更小。
# Horizontal continuous layout page gap (DIP). Tighter for cross-page reading.
PageGapHorizontal = 6.0           # 默认 6.0 | default


# ---------------- 光栅并发 / Raster concurrency ----------------
# 光栅线程自动模式上限：auto = min(逻辑核数, 本值)。
# Auto-mode ceiling: auto = min(logical cores, this).
RenderWorkerCap = 16              # 默认 16 | default

# 绝对硬上限（防止极端配置让线程抢占 CPU/内存带宽）。
# Absolute hard ceiling (prevents thread over-subscription on extreme configs).
RenderWorkerHardCap = 24          # 默认 24 | default


# ---------------- 滚动 / Scrolling ----------------
# 判定滚动方向的死区(DIP)。远小于一次滚轮位移(~100)，但吃掉触控板抖动。
# Deadzone for scroll direction (DIP). Smaller than a wheel notch (~100) but kills jitter.
ScrollDirDeadzone = 8.0           # 默认 8.0 | default


# ---------------- 统一运动引擎弹簧 ω (rad/s) / Motion springs ----------------
# 99% 收敛时长 ≈ 6.6/ω。想更快只调 ω，不要动下方收敛阈值。
# ~99% settle time ≈ 6.6/ω. Want faster? Raise ω only; leave settle thresholds alone.

# 滚轮滚动弹簧硬度：≈315ms 收敛，跟手且顺滑。
# Wheel scroll spring: ~315ms settle, responsive & smooth.
ScrollSpringOmega = 21.0          # 默认 21.0 | default

# 双击/按钮/Ctrl+滚轮缩放弹簧：≈370ms，有""推进感""。
# Double-click / button / Ctrl+wheel zoom spring: ~370ms, with a sense of push.
ZoomSpringOmega = 18.0            # 默认 18.0 | default

# 右键拖拽缩放弹簧：≈165ms，以跟手为主、只滤输入抖动。
# Right-drag zoom spring: ~165ms, follows hand, only filters jitter.
RightZoomSpringOmega = 40.0       # 默认 40.0 | default


# ---------------- 滚轮与缩放步进 / Wheel & zoom step ----------------
# 滚轮一格位移系数。负值=前推往回看。幅度 3.0 → 一档≈360px（视口约1/3）。
# Wheel notch factor (negative = push forward = scroll back). 3.0 → ~360px/notch.
# 2.0≈240px 偏稳，4.0≈480px 更奔放。/ 2.0 steady, 4.0 snappier.
WheelScrollFactor = -3.0          # 默认 -3.0 | default

# Ctrl+滚轮缩放强度（每档 120 delta 约缩放 3%）。越大滚轮缩放越猛。
# Ctrl+wheel zoom strength (~3% per notch). Higher = more aggressive.
CtrlWheelZoomStrength = 0.25      # 默认 0.25 | default

# 缩放按钮(+/-)每按一次步进（百分比点：100%→110%）。
# Zoom in/out button step per click (percentage points: 100%→110%).
ZoomButtonStep = 10.0             # 默认 10.0 | default


# ---------------- 收敛阈值（按""屏幕看得见""判定） / Settle thresholds ----------------
# 残差 ≤ 约 1~2 屏幕像素即落定，消除""看着停了其实还在跑""的拖尾。
# Settle when residual ≤ ~1-2 on-screen px; kills the ""looks stopped but still running"" tail.

# 滚动收敛残差(px)。/ Scroll settle residual (px).
ScrollSettlePx = 1.5              # 默认 1.5 | default

# 滚动收敛速度(px/帧)。/ Scroll settle velocity.
ScrollSettleVel = 40.0            # 默认 40.0 | default

# 缩放收敛残差(log 空间)。/ Zoom settle residual (log space).
ZoomSettleLog = 0.002             # 默认 0.002 | default

# 缩放收敛速度。/ Zoom settle velocity.
ZoomSettleVel = 0.08              # 默认 0.08 | default


# ---------------- 提交落地（ChangeView 异步窗口） / Commit window ----------------
# 缩放/偏移经 ScrollViewer.ChangeView 异步落地，下面是容差与兜底。
# Zoom/offset land asynchronously via ChangeView; tolerances & safety net below.

# 缩放提交容差。/ Zoom commit epsilon.
CommitZoomEps = 0.001             # 默认 0.001 | default

# 偏移提交容差(px)。夹断造成的偏差几十~几百px，1.5px 足以区分。
# Offset commit epsilon (px). Clip-induced error is tens~hundreds px; 1.5px tells apart.
CommitOffsetEps = 1.5             # 默认 1.5 | default

# 最大补发偏移次数。/ Max commit nudges.
MaxCommitNudges = 4               # 默认 4 | default

# 提交落地超时(墙钟兜底 ms)。zoom 通常 <20ms 落地；远超此值才视为真死锁强制关窗。
# Commit wait deadline (ms wall-clock). Usually <20ms; only past this = real deadlock.
CommitWaitDeadlineMs = 250.0      # 默认 250.0 | default


# ---------------- 锚点 / 双击 / Anchor & double-tap ----------------
# 落定后仍复用锚点的宽限(ms)。/ Anchor reuse grace after settle (ms).
ZoomReuseAnchorGraceMs = 200.0    # 默认 200.0 | default

# 双击去重窗口(ms)，吃掉手势识别器的重复派发。
# Double-tap dedupe window (ms) to swallow recognizer double-fire.
DoubleTapIgnoreMs = 150.0         # 默认 150.0 | default


# ---------------- UI / 工具栏 / UI & toolbar ----------------
# 工具栏滑入滑出时长(ms)。/ Toolbar slide-in/out duration (ms).
UiTransitionMs = 150              # 默认 150 | default

# 鼠标离开工具栏区域后，自动隐藏的延迟(ms)。/ Auto-hide delay after pointer leaves (ms).
ToolbarHideDelayMs = 180          # 默认 180 | default
";
        // ⚠ 上面末尾那个换行是【刻意】的，别删：让本字面量与 src/config.txt 逐字一致（含末尾换行）。
        //   删掉它 => 本字面量会比 src/config.txt 少一个换行，"删文件=恢复出厂"生成的那份就与仓库那份不同。
        // The trailing newline above is deliberate: keeps this literal byte-identical to src/config.txt.

        // ---------------- 缩放范围 / Zoom range ----------------
        public static double MinZoom = 0.1;                 // 最小缩放比例 / min zoom ratio (10%)
        public static double MaxZoom = 4.0;                 // 最大缩放比例 / max zoom ratio (400%)
        public static double MinZoomPercent => MinZoom * 100.0;
        public static double MaxZoomPercent => MaxZoom * 100.0;

        // ---------------- 布局留白 / Layout margins (DIP) ----------------
        public static double DocMargin = 16.0;              // 文档四周留白 / doc margin around content
        public static double PageGap = 16.0;                // 纵向连续布局页间距 / vertical page gap
        public static double PageGapHorizontal = 6.0;       // 横向连续布局页间距 / horizontal page gap (cross-page feel)

        // ---------------- 缓存与光栅 / Cache & raster ----------------
        // 并发光栅线程数【不再可配置】：始终由 PdfRenderService 按物理核自适应选择
        //（物理核 - 1 留档给 UI/Win2D，受 RenderWorkerCap 封顶）。——
        // RenderWorkerCount：见 PdfRenderService.CpuCoreInfo.PhysicalCoreCount()
        public static int RenderWorkerCap = 16;             // 自动模式上限：auto = min(逻辑核数, 本值)
        public static int RenderWorkerHardCap = 24;         // 绝对硬上限（防止极端配置让线程抢占 CPU/内存带宽）

        // ---------------- 预渲染滑动窗口 / Prefetch window ----------------
        public static double ScrollDirDeadzone = 8.0;       // 滚动方向死区(DIP) / scroll direction deadzone (DIP)

        // ---------------- 统一运动引擎弹簧 ω (rad/s) / Motion springs ----------------
        // 99% 收敛时长 ≈ 6.6/ω。想更快只调 ω，不要动收敛阈值。
        // ~99% settle time ≈ 6.6/ω. Want faster? Only raise ω; leave settle thresholds alone.
        public static double ScrollSpringOmega = 21.0;     // 滚轮滚动 ≈315ms / wheel scroll (~315ms)
        public static double ZoomSpringOmega = 18.0;       // 双击/按钮/Ctrl+滚轮 ≈370ms / dblclick/button/ctrl-wheel (~370ms)
        public static double RightZoomSpringOmega = 40.0;  // 右键拖拽缩放 ≈165ms / right-drag zoom (~165ms)

        // ---------------- 滚轮与缩放步进 / Wheel & zoom step ----------------
        public static double WheelScrollFactor = -3.0;     // 滚轮一格位移系数(≈360px/档；2.0≈240px偏稳,4.0≈480px更奔放)
                                                            // wheel notch factor (~360px/notch; 2.0≈240px steady, 4.0≈480px snappy)
        public static double CtrlWheelZoomStrength = 0.25; // Ctrl+滚轮缩放强度(每档120delta约3%) / ctrl+wheel zoom strength (~3%/notch)
        public static double ZoomButtonStep = 10.0;        // 缩放按钮(+/-)每按一次步进(百分比点) / zoom button step per click (%pts)

        // ---------------- 收敛阈值（按"屏幕看得见"判定） / Settle thresholds (visible-on-screen) ----------------
        public static double ScrollSettlePx = 1.5;         // 滚动收敛残差(px) / scroll settle residual (px)
        public static double ScrollSettleVel = 40.0;       // 滚动收敛速度(px/帧) / scroll settle velocity
        public static double ZoomSettleLog = 2e-3;        // 缩放收敛残差(log) / zoom settle residual (log)
        public static double ZoomSettleVel = 8e-2;         // 缩放收敛速度 / zoom settle velocity

        // ---------------- 提交落地（ChangeView 异步窗口） / Commit window ----------------
        public static double CommitZoomEps = 1e-3;         // 缩放提交容差 / commit zoom epsilon
        public static double CommitOffsetEps = 1.5;        // 偏移提交容差(px) / commit offset epsilon (px)
        public static int MaxCommitNudges = 4;            // 最大补发偏移次数 / max commit nudges
        public static double CommitWaitDeadlineMs = 250.0; // 落地超时(墙钟兜底, ms) / commit wait deadline (ms)

        // ---------------- 锚点 / 双击 / Anchor & double-tap ----------------
        public static double ZoomReuseAnchorGraceMs = 200.0; // 落定后仍复用锚点宽限(ms) / anchor reuse grace (ms)
        public static double DoubleTapIgnoreMs = 150.0;     // 双击去重窗口(ms) / double-tap dedupe window (ms)

        // ---------------- UI / 工具栏 / UI & toolbar ----------------
        public static double UiTransitionMs = 150;         // 工具栏滑入滑出时长(ms) / toolbar slide duration (ms)
        public static double ToolbarHideDelayMs = 180;     // 鼠标离开后自动隐藏延迟(ms) / toolbar auto-hide delay (ms)

        // ============================ 加载 / Loading ============================

        /// <summary>启动时自动加载一次（首次访问 Tuning 字段时触发）。</summary>
        static Tuning() { Load(); }

        /// <summary>
        /// 从 config.txt 读取覆盖。只覆盖文件里出现且能解析的键；缺失键保留默认值。
        /// Loads overrides from config.txt. Only keys present & parseable are applied; missing keys keep defaults.
        /// 搜索顺序 / Search order: 程序目录/config.txt → 程序目录/统计/config.txt → 程序目录/Stats/config.txt → 当前目录/config.txt。
        /// </summary>
        public static void Load()
        {
            string? dir = null;
            try { dir = AppContext.BaseDirectory; } catch { /* ignore */ }

            var candidates = new List<string>(4);
            if (!string.IsNullOrEmpty(dir))
            {
                candidates.Add(Path.Combine(dir, "config.txt"));
                candidates.Add(Path.Combine(dir, "统计", "config.txt"));
                candidates.Add(Path.Combine(dir, "Stats", "config.txt"));
            }
            candidates.Add("config.txt");

            string? path = null;
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) { path = c; break; } } catch { /* ignore */ }
            }
            if (path == null) { RegenerateConfigFile(); return; }

            try
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                    // 允许尾注：Key = Value   # 注释
                    var hash = line.IndexOf('#');
                    if (hash >= 0) line = line.Substring(0, hash).Trim();
                    if (line.Length == 0) continue;

                    var eq = line.IndexOf('=');
                    if (eq < 0) continue;

                    var key = line.Substring(0, eq).Trim();
                    var val = line.Substring(eq + 1).Trim();
                    if (key.Length == 0 || val.Length == 0) continue;

                    TrySet(key, val);
                }
            }
            catch
            {
                // 解析失败不影响启动，保留默认值。
                // Parse failure must not break startup; defaults remain.
            }
        }

        /// <summary>
        /// config.txt 不存在（被删除）时调用。默认值已由字段初始化就位，
        /// 这里把一份【全默认值】的 config.txt 重新写回，实现"删文件=恢复出厂"。
        /// Called when config.txt is missing. Defaults are already active (field initializers);
        /// we (re)write a full-default config.txt to realize "delete file = factory reset".
        /// 写入位置优先级 / Write target priority:
        ///   已存在的 统计/ 或 Stats/ 子目录 -> 其下 config.txt；否则 -> 程序目录/config.txt。
        ///   existing 统计/Stats subfolder -> its config.txt; else -> program-dir/config.txt.
        /// </summary>
        private static void RegenerateConfigFile()
        {
            try
            {
                string target = GetRegenPath();
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(target, DefaultConfigText);
            }
            catch
            {
                // 写入失败（如程序目录只读）不影响运行：默认值仍生效，只是本次未生成文件。
                // Write failure (e.g. read-only program dir) is non-fatal; defaults still apply.
            }
        }

        private static string GetRegenPath()
        {
            string? baseDir = null;
            try { baseDir = AppContext.BaseDirectory; } catch { baseDir = null; }
            if (!string.IsNullOrEmpty(baseDir))
            {
                string statsCn = Path.Combine(baseDir, "统计", "config.txt");
                if (Directory.Exists(Path.GetDirectoryName(statsCn))) return statsCn;
                string statsEn = Path.Combine(baseDir, "Stats", "config.txt");
                if (Directory.Exists(Path.GetDirectoryName(statsEn))) return statsEn;
                return Path.Combine(baseDir, "config.txt");
            }
            return "config.txt";
        }

        private static void TrySet(string key, string raw)
        {
            // 容错布尔/数值解析；解析失败则忽略该键（保留默认）。
            // Tolerant parse; on failure the key is ignored (default kept).
            switch (key)
            {
                case "MinZoom": MinZoom = ParseDouble(raw, MinZoom); break;
                case "MaxZoom": MaxZoom = ParseDouble(raw, MaxZoom); break;
                case "DocMargin": DocMargin = ParseDouble(raw, DocMargin); break;
                case "PageGap": PageGap = ParseDouble(raw, PageGap); break;
                case "PageGapHorizontal": PageGapHorizontal = ParseDouble(raw, PageGapHorizontal); break;
                case "RenderWorkerCap": RenderWorkerCap = ParseInt(raw, RenderWorkerCap); break;
                case "RenderWorkerHardCap": RenderWorkerHardCap = ParseInt(raw, RenderWorkerHardCap); break;
                case "ScrollDirDeadzone": ScrollDirDeadzone = ParseDouble(raw, ScrollDirDeadzone); break;
                case "ScrollSpringOmega": ScrollSpringOmega = ParseDouble(raw, ScrollSpringOmega); break;
                case "ZoomSpringOmega": ZoomSpringOmega = ParseDouble(raw, ZoomSpringOmega); break;
                case "RightZoomSpringOmega": RightZoomSpringOmega = ParseDouble(raw, RightZoomSpringOmega); break;
                case "WheelScrollFactor": WheelScrollFactor = ParseDouble(raw, WheelScrollFactor); break;
                case "CtrlWheelZoomStrength": CtrlWheelZoomStrength = ParseDouble(raw, CtrlWheelZoomStrength); break;
                case "ZoomButtonStep": ZoomButtonStep = ParseDouble(raw, ZoomButtonStep); break;
                case "ScrollSettlePx": ScrollSettlePx = ParseDouble(raw, ScrollSettlePx); break;
                case "ScrollSettleVel": ScrollSettleVel = ParseDouble(raw, ScrollSettleVel); break;
                case "ZoomSettleLog": ZoomSettleLog = ParseDouble(raw, ZoomSettleLog); break;
                case "ZoomSettleVel": ZoomSettleVel = ParseDouble(raw, ZoomSettleVel); break;
                case "CommitZoomEps": CommitZoomEps = ParseDouble(raw, CommitZoomEps); break;
                case "CommitOffsetEps": CommitOffsetEps = ParseDouble(raw, CommitOffsetEps); break;
                case "MaxCommitNudges": MaxCommitNudges = ParseInt(raw, MaxCommitNudges); break;
                case "CommitWaitDeadlineMs": CommitWaitDeadlineMs = ParseDouble(raw, CommitWaitDeadlineMs); break;
                case "ZoomReuseAnchorGraceMs": ZoomReuseAnchorGraceMs = ParseDouble(raw, ZoomReuseAnchorGraceMs); break;
                case "DoubleTapIgnoreMs": DoubleTapIgnoreMs = ParseDouble(raw, DoubleTapIgnoreMs); break;
                case "UiTransitionMs": UiTransitionMs = ParseDouble(raw, UiTransitionMs); break;
                case "ToolbarHideDelayMs": ToolbarHideDelayMs = ParseDouble(raw, ToolbarHideDelayMs); break;
                default: break; // 未知键忽略 / unknown key ignored
            }
        }

        private static double ParseDouble(string raw, double fallback)
            => double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        private static int ParseInt(string raw, int fallback)
            => int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
