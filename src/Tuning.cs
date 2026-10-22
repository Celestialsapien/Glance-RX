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
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace GlanceRX
{
    /// 设计手感调参中心 / Design-feel tuning hub. 原本这些值是散落在 GpuPageView / MainPage 里的编译期 const。
    public static class Tuning
    {

        // 一份【全默认值】的 config.txt 文本，用于"删文件=恢复出厂"时自动重新生成。
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
# ⚠ 安全边界 / Safety: 每个键的【有效区间】已写在对应行尾（如 [0, 256]、(0, 5]）。
#    越界或非法（非数字 / Infinity / NaN）的值会在启动时自动钳到安全范围，不会让程序崩溃；
#    但钳制会悄悄改变手感，所以请按区间填写。
#    Out-of-range / invalid values are auto-clamped to a safe range at launch (no crash),
#    but clamping silently changes feel — so please stay within the stated range.
#
# 一键回退（恢复出厂）/ One-click factory reset:
#   直接删除本文件即可。下次打开 Glance 会以【全部默认值】运行，并在本目录
#   （或默认位置）自动重新生成一份【全默认值】的 config.txt —— 等价于""恢复出厂设置""。
#   Simply delete this file. Next launch runs with ALL defaults and auto-regenerates a
#   full-default config.txt in this (or the default) location — i.e. a factory reset.
# ============================================================================

# ---------------- 缩放范围 / Zoom range ----------------
# 最小缩放比例。有效区间 (0, MaxZoom)，建议 0.02 ~ 5.0。
# Min zoom ratio. Valid (0, MaxZoom); recommended 0.02 ~ 5.0.
# 若 >= MaxZoom 会被自动钳为 MaxZoom×0.5，否则缩放夹取逻辑会抛异常。
# If >= MaxZoom it is auto-clamped to MaxZoom×0.5, else zoom clamping throws.
MinZoom = 0.1                     # 默认 0.1 | default | 区间 (0, MaxZoom)

# 最大缩放比例。有效区间 (MinZoom, 16]，建议 0.05 ~ 16.0。
# Max zoom ratio. Valid (MinZoom, 16]; recommended 0.05 ~ 16.0.
MaxZoom = 4.0                     # 默认 4.0 | default | 区间 (MinZoom, 16]

# ---------------- 布局留白 / Layout margins (DIP) ----------------
# 文档四周留白（DIP）。有效区间 [0, 256]，非负（负值会把内容拉出屏幕）。
# Doc margin (DIP). Valid [0, 256], non-negative.
DocMargin = 16.0                  # 默认 16.0 | default | 区间 [0, 256]

# 纵向连续布局页间距（DIP）。有效区间 [0, 256]，非负。
# Vertical page gap (DIP). Valid [0, 256], non-negative.
PageGap = 16.0                    # 默认 16.0 | default | 区间 [0, 256]

# 横向连续布局页间距（DIP）。有效区间 [0, 256]，非负。
# Horizontal page gap (DIP). Valid [0, 256], non-negative.
PageGapHorizontal = 6.0           # 默认 6.0 | default | 区间 [0, 256]

# ---------------- 光栅并发 / Raster concurrency ----------------
# 自动模式并发上限：auto = min(物理核-1, 本值)。有效区间 [1, 1024]，且必须 <= RenderWorkerHardCap。
# Auto ceiling: auto = min(physical cores - 1, this). Valid [1, 1024]; must be <= HardCap.
# 低于 1 会被钳为 1，否则 Math.Clamp(...,1,Cap) 抛异常。
# Below 1 clamps to 1, else Math.Clamp(...,1,Cap) throws.
RenderWorkerCap = 16              # 默认 16 | default | 区间 [1, 1024]

# 绝对硬上限（并发不可能超过此值）。有效区间 [1, 1024]，且必须 >= RenderWorkerCap。
# Absolute hard ceiling. Valid [1, 1024]; must be >= Cap (else auto-raised to Cap).
# 低于 1 会被钳为 1，否则 Math.Clamp 抛异常。/ Below 1 clamps to 1, else throws.
RenderWorkerHardCap = 24          # 默认 24 | default | 区间 [1, 1024]

# ---------------- 统一运动引擎弹簧 ω (rad/s) / Motion springs ----------------
# 99% 收敛时长 ≈ 6.6/ω。想更快只调 ω，不要动下方收敛阈值。
# ~99% settle time ≈ 6.6/ω. Want faster? Raise ω only; leave settle thresholds alone.
# 以下三个 ω 的有效区间均为 (0, 200] rad/s，必须 > 0：
# All three omegas valid (0, 200] rad/s, must be > 0:
#   ω <= 0 时弹簧退化为瞬切（负值本应指数发散、画面乱跳）。
#   ω <= 0 makes the spring snap instantly (negative would diverge).

# 滚轮滚动弹簧硬度：≈315ms 收敛，跟手且顺滑。
# Wheel scroll spring: ~315ms settle, responsive & smooth.
ScrollSpringOmega = 21.0          # 默认 21.0 | default | 区间 (0, 200]

# 双击/按钮/Ctrl+滚轮缩放弹簧：≈370ms，有""推进感""。
# Double-click / button / ctrl+wheel zoom spring: ~370ms, with a sense of push.
ZoomSpringOmega = 18.0            # 默认 18.0 | default | 区间 (0, 200]

# 右键拖拽缩放弹簧：≈165ms，以跟手为主、只滤输入抖动。
# Right-drag zoom spring: ~165ms, follows hand, only filters jitter.
RightZoomSpringOmega = 40.0       # 默认 40.0 | default | 区间 (0, 200]

# ---------------- 滚轮与缩放步进 / Wheel & zoom step ----------------
# 滚轮一格位移系数。有效区间 [-50, 50]；负值=前推往回看（符号有意保留，只限幅）。
# Wheel notch factor. Valid [-50, 50]; negative = push forward = scroll back (sign kept, magnitude clamped).
WheelScrollFactor = -3.0          # 默认 -3.0 | default | 区间 [-50, 50]

# Ctrl+滚轮缩放强度（每档 120 delta 约缩放 3%）。有效区间 [0, 5]，非负（负值会反向缩放）。
# Ctrl+wheel zoom strength (~3% per notch). Valid [0, 5], non-negative (negative inverts).
CtrlWheelZoomStrength = 0.25      # 默认 0.25 | default | 区间 [0, 5]

# 缩放按钮(+/-)每按一次步进（百分比点：100%→110%）。有效区间 [0.5, 100]，> 0。
# Zoom button step per click (%pts). Valid [0.5, 100], > 0.
ZoomButtonStep = 10.0             # 默认 10.0 | default | 区间 [0.5, 100]

# ---------------- 收敛阈值（按""屏幕看得见""判定） / Settle thresholds ----------------
# 残差 ≤ 约 1~2 屏幕像素即落定，消除""看着停了其实还在跑""的拖尾。
# Settle when residual ≤ ~1-2 on-screen px; kills the ""looks stopped but still running"" tail.
# 以下四个阈值的有效区间均为 (0, 上限]，必须 > 0：阈值 = 0 时判定要求精确相等，
# 动画/滚动永远到不了，帧泵空转（吃电池）。/ All four valid (0, cap], must be > 0; =0 never settles.

# 滚动收敛残差(px)。有效区间 (0, 50]。
# Scroll settle residual (px). Valid (0, 50].
ScrollSettlePx = 1.5              # 默认 1.5 | default | 区间 (0, 50]

# 滚动收敛速度(px/帧)。有效区间 (0, 1000]。
# Scroll settle velocity (px/frame). Valid (0, 1000].
ScrollSettleVel = 40.0            # 默认 40.0 | default | 区间 (0, 1000]

# 缩放收敛残差(log 空间)。有效区间 (0, 0.5]。
# Zoom settle residual (log space). Valid (0, 0.5].
ZoomSettleLog = 0.002             # 默认 0.002 | default | 区间 (0, 0.5]

# 缩放收敛速度。有效区间 (0, 5]。
# Zoom settle velocity. Valid (0, 5].
ZoomSettleVel = 0.08              # 默认 0.08 | default | 区间 (0, 5]

# ---------------- 提交落地（ChangeView 异步窗口） / Commit window ----------------
# 缩放/偏移经 ScrollViewer.ChangeView 异步落地，下面是容差与兜底。
# Zoom/offset land asynchronously via ChangeView; tolerances & safety net below.

# 缩放提交容差。有效区间 (0, 0.1]。
# Zoom commit epsilon. Valid (0, 0.1].
CommitZoomEps = 0.001             # 默认 0.001 | default | 区间 (0, 0.1]

# 偏移提交容差(px)。有效区间 (0, 50]。
# Offset commit epsilon (px). Valid (0, 50].
CommitOffsetEps = 1.5             # 默认 1.5 | default | 区间 (0, 50]

# 最大补发偏移次数（整数）。有效区间 [0, 64]，非负。
# Max commit nudges (int). Valid [0, 64], non-negative.
MaxCommitNudges = 4               # 默认 4 | default | 区间 [0, 64]

# 提交落地超时(墙钟兜底 ms)。有效区间 (0, 10000]，> 0。
# Commit wait deadline (ms). Valid (0, 10000], > 0.
CommitWaitDeadlineMs = 250.0      # 默认 250.0 | default | 区间 (0, 10000]

# ---------------- 锚点 / 双击 / Anchor & double-tap ----------------
# 落定后仍复用锚点的宽限(ms)。有效区间 [0, 5000]，非负。
# Anchor reuse grace after settle (ms). Valid [0, 5000], non-negative.
ZoomReuseAnchorGraceMs = 200.0    # 默认 200.0 | default | 区间 [0, 5000]

# 双击去重窗口(ms)，吃掉手势识别器的重复派发。有效区间 [0, 2000]，非负。
# Double-tap dedupe window (ms). Valid [0, 2000], non-negative.
DoubleTapIgnoreMs = 150.0         # 默认 150.0 | default | 区间 [0, 2000]

# ---------------- UI / 工具栏 / UI & toolbar ----------------
# 工具栏滑入滑出时长(ms)。有效区间 [0, 2000]，非负。
# Toolbar slide-in/out duration (ms). Valid [0, 2000], non-negative.
UiTransitionMs = 150              # 默认 150 | default | 区间 [0, 2000]

# 鼠标离开工具栏区域后，自动隐藏的延迟(ms)。有效区间 [0, 10000]，非负。
# Auto-hide delay after pointer leaves (ms). Valid [0, 10000], non-negative.
ToolbarHideDelayMs = 180          # 默认 180 | default | 区间 [0, 10000]
";
        // ⚠ 上面末尾那个换行是【刻意】的，别删：让本字面量与 src/config.txt 逐字一致（含末尾换行）。

        // ---------------- 缩放范围 / Zoom range ----------------
        public static double MinZoom = 0.1;
        public static double MaxZoom = 4.0;
        public static double MinZoomPercent => MinZoom * 100.0;
        public static double MaxZoomPercent => MaxZoom * 100.0;

        // ---------------- 布局留白 / Layout margins (DIP) ----------------
        public static double DocMargin = 16.0;              // 文档四周留白 / doc margin around content
        public static double PageGap = 16.0;
        public static double PageGapHorizontal = 6.0;       // 横向连续布局页间距 / horizontal page gap (cross-page feel)

        // ---------------- 缓存与光栅 / Cache & raster ----------------
        public static int RenderWorkerCap = 16;             // 自动模式上限：auto = min(逻辑核数, 本值)
        public static int RenderWorkerHardCap = 24;         // 绝对硬上限（防止极端配置让线程抢占 CPU/内存带宽）

        // ---------------- 统一运动引擎弹簧 ω (rad/s) / Motion springs ----------------
        public static double ScrollSpringOmega = 21.0;     // 滚轮滚动 ≈315ms / wheel scroll (~315ms)
        public static double ZoomSpringOmega = 18.0;       // 双击/按钮/Ctrl+滚轮 ≈370ms / dblclick/button/ctrl-wheel (~370ms)
        public static double RightZoomSpringOmega = 40.0;  // 右键拖拽缩放 ≈165ms / right-drag zoom (~165ms)

        public static double WheelScrollFactor = -3.0;     // 滚轮一格位移系数(≈360px/档；2.0≈240px偏稳,4.0≈480px更奔放)
                                                            // wheel notch factor (~360px/notch; 2.0≈240px steady, 4.0≈480px snappy)
        public static double CtrlWheelZoomStrength = 0.25; // Ctrl+滚轮缩放强度(每档120delta约3%) / ctrl+wheel zoom strength (~3%/notch)
        public static double ZoomButtonStep = 10.0;        // 缩放按钮(+/-)每按一次步进(百分比点) / zoom button step per click (%pts)

        public static double ScrollSettlePx = 1.5;         // 滚动收敛残差(px) / scroll settle residual (px)
        public static double ScrollSettleVel = 40.0;       // 滚动收敛速度(px/帧) / scroll settle velocity
        public static double ZoomSettleLog = 2e-3;        // 缩放收敛残差(log) / zoom settle residual (log)
        public static double ZoomSettleVel = 8e-2;

        public static double CommitZoomEps = 1e-3;
        public static double CommitOffsetEps = 1.5;        // 偏移提交容差(px) / commit offset epsilon (px)
        public static int MaxCommitNudges = 4;
        public static double CommitWaitDeadlineMs = 250.0; // 落地超时(墙钟兜底, ms) / commit wait deadline (ms)

        public static double ZoomReuseAnchorGraceMs = 200.0; // 落定后仍复用锚点宽限(ms) / anchor reuse grace (ms)
        public static double DoubleTapIgnoreMs = 150.0;     // 双击去重窗口(ms) / double-tap dedupe window (ms)

        public static double UiTransitionMs = 150;         // 工具栏滑入滑出时长(ms) / toolbar slide duration (ms)
        public static double ToolbarHideDelayMs = 180;     // 鼠标离开后自动隐藏延迟(ms) / toolbar auto-hide delay (ms)

        static Tuning() { Load(); }

        /// <summary>从 config.txt 读取覆盖。只覆盖文件里出现且能解析的键；缺失键保留默认值。 Loads overrides from config.txt. Only keys present & parseable are applied;</summary>
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
            if (path == null) { RegenerateConfigFile(); Validate(); return; }

            try
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

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

            // 解析无论成功与否，最后都把所有值钳进安全区间并修好跨字段不变量，
            // 保证后续 Math.Clamp / 弹簧 / 收敛判定不会因配置异常而崩溃或空转。
            Validate();
        }

        /// config.txt 不存在（被删除）时调用。
        /// 已存在的 统计/ 或 Stats/ 子目录 -> 其下 config.txt；否则 -> 程序目录/config.txt。
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
            switch (key)
            {
                case "MinZoom": MinZoom = ParseDouble(raw, MinZoom); break;
                case "MaxZoom": MaxZoom = ParseDouble(raw, MaxZoom); break;
                case "DocMargin": DocMargin = ParseDouble(raw, DocMargin); break;
                case "PageGap": PageGap = ParseDouble(raw, PageGap); break;
                case "PageGapHorizontal": PageGapHorizontal = ParseDouble(raw, PageGapHorizontal); break;
                case "RenderWorkerCap": RenderWorkerCap = ParseInt(raw, RenderWorkerCap); break;
                case "RenderWorkerHardCap": RenderWorkerHardCap = ParseInt(raw, RenderWorkerHardCap); break;
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
                default: break;
            }
        }

        // ============================ 校验 / 钳制 / Validation ============================
        // 配置是给"手调手感"的人用的，任何离谱值都不该让程序崩。

        private static double ClampD(double v, double lo, double hi)
            => double.IsFinite(v) ? Math.Max(lo, Math.Min(v, hi)) : lo;

        private static int ClampI(int v, int lo, int hi)
            => v < lo ? lo : (v > hi ? hi : v);

        private static void Validate()
        {
            // ---- 缩放范围 / Zoom range ----
            // 硬性不变量 MinZoom < MaxZoom：否则 GpuPageView 里十几处 Math.Clamp(zoom, MinZoom, MaxZoom)
            MinZoom = ClampD(MinZoom, 0.02, 5.0);
            MaxZoom = ClampD(MaxZoom, 0.05, 16.0);
            if (MinZoom >= MaxZoom) MinZoom = Math.Max(0.02, MaxZoom * 0.5);

            // ---- 布局留白 / Layout margins (DIP) ----  负留白无意义（内容被拉出屏幕）。
            DocMargin = ClampD(DocMargin, 0.0, 256.0);
            PageGap = ClampD(PageGap, 0.0, 256.0);
            PageGapHorizontal = ClampD(PageGapHorizontal, 0.0, 256.0);

            // ---- 并发光栅 / Raster concurrency ----
            // HardCap 必须是绝对上限 => 不低于 Cap，否则 Cap 才是真正上限、注释语义失效。
            RenderWorkerCap = ClampI(RenderWorkerCap, 1, 1024);
            RenderWorkerHardCap = ClampI(RenderWorkerHardCap, 1, 1024);
            if (RenderWorkerHardCap < RenderWorkerCap) RenderWorkerHardCap = RenderWorkerCap;

            // ---- 弹簧角频率 / Spring ω ----  必须 > 0：<=0 在 Motion 里退化为瞬切，负值本应指数发散。
            ScrollSpringOmega = ClampD(ScrollSpringOmega, 1.0, 200.0);
            ZoomSpringOmega = ClampD(ZoomSpringOmega, 1.0, 200.0);
            RightZoomSpringOmega = ClampD(RightZoomSpringOmega, 1.0, 200.0);

            // ---- 滚轮与缩放步进 / Wheel & zoom step ----
            WheelScrollFactor = ClampD(WheelScrollFactor, -50.0, 50.0);
            CtrlWheelZoomStrength = ClampD(CtrlWheelZoomStrength, 0.0, 5.0);
            ZoomButtonStep = ClampD(ZoomButtonStep, 0.5, 100.0);

            // ---- 收敛阈值 / Settle thresholds ----  必须 > 0：=0 时 IsSettled 要求精确相等，
            ScrollSettlePx = ClampD(ScrollSettlePx, 0.1, 50.0);
            ScrollSettleVel = ClampD(ScrollSettleVel, 0.1, 1000.0);
            ZoomSettleLog = ClampD(ZoomSettleLog, 1e-4, 0.5);
            ZoomSettleVel = ClampD(ZoomSettleVel, 1e-3, 5.0);

            CommitZoomEps = ClampD(CommitZoomEps, 1e-5, 0.1);
            CommitOffsetEps = ClampD(CommitOffsetEps, 0.1, 50.0);
            MaxCommitNudges = ClampI(MaxCommitNudges, 0, 64);
            CommitWaitDeadlineMs = ClampD(CommitWaitDeadlineMs, 1.0, 10000.0);

            ZoomReuseAnchorGraceMs = ClampD(ZoomReuseAnchorGraceMs, 0.0, 5000.0);
            DoubleTapIgnoreMs = ClampD(DoubleTapIgnoreMs, 0.0, 2000.0);
            UiTransitionMs = ClampD(UiTransitionMs, 0.0, 2000.0);
            ToolbarHideDelayMs = ClampD(ToolbarHideDelayMs, 0.0, 10000.0);
        }

        private static double ParseDouble(string raw, double fallback)
            => double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        private static int ParseInt(string raw, int fallback)
            => int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
