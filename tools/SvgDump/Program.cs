// 临时验证工具：调用 Rust FFI 导出页面 SVG，检查其内容有效性
// （验证 mu_export_page_svg 输出的命令流是真实含路径/图形的矢量文档）。
// 用法: dotnet run --project src/SvgDump -- <pdf路径> [页码]
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

class Program
{
    [StructLayout(LayoutKind.Sequential)]
    struct EngineCreateResult
    {
        public IntPtr Engine;
        public IntPtr ErrorMsg;
        public byte NeedsPassword;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Response
    {
        public bool Success;
        public IntPtr ErrorMsg;
        public IntPtr DataPtr;
        public ulong DataLen;
    }

    [DllImport("glance_native.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern EngineCreateResult mu_engine_open(IntPtr path, IntPtr password, uint hotCapacity);

    [DllImport("glance_native.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern Response mu_export_page_svg(IntPtr engine, uint page, out IntPtr outData, out ulong outLen);

    [DllImport("glance_native.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern Response mu_export_page_commands(IntPtr engine, uint page, out IntPtr outData, out ulong outLen);

    [DllImport("glance_native.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern Response mu_render_page_region_bgra(IntPtr engine, uint page, uint devW, uint devH, uint x0, uint y0, uint x1, uint y1, out IntPtr outData, out ulong outLen, out uint outW, out uint outH);

    [DllImport("glance_native.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern uint mu_engine_page_count(IntPtr engine);

    [DllImport("glance_native.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void mu_engine_destroy(IntPtr engine);

    [DllImport("glance_native.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void memory_free(IntPtr ptr, ulong len);

    static IntPtr Utf8(string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s + "\0");
        IntPtr p = Marshal.AllocHGlobal(b.Length);
        Marshal.Copy(b, 0, p, b.Length);
        return p;
    }

    static string? ExportSvg(IntPtr engine, uint page)
    {
        var resp = mu_export_page_svg(engine, page, out _, out _);
        if (!resp.Success) return null;
        byte[] bytes = new byte[resp.DataLen];
        Marshal.Copy(resp.DataPtr, bytes, 0, (int)resp.DataLen);
        var s = Encoding.UTF8.GetString(bytes);
        memory_free(resp.DataPtr, resp.DataLen);
        return s;
    }

    static byte[]? ExportCommands(IntPtr engine, uint page)
    {
        var resp = mu_export_page_commands(engine, page, out _, out _);
        if (!resp.Success) return null;
        try
        {
            byte[] bytes = new byte[resp.DataLen];
            Marshal.Copy(resp.DataPtr, bytes, 0, (int)resp.DataLen);
            return bytes;
        }
        finally { memory_free(resp.DataPtr, resp.DataLen); }
    }

    static void Main(string[] args)
    {
        if (args.Length < 1 || !System.IO.File.Exists(args[0]))
        {
            Console.WriteLine("usage: SvgDump <pdf> [page]");
            return;
        }
        uint page = args.Length > 1 ? uint.Parse(args[1]) : 0;

        var pathPtr = Utf8(args[0]);
        try
        {
            var res = mu_engine_open(pathPtr, IntPtr.Zero, 8);
            if (res.Engine == IntPtr.Zero)
            {
                Console.WriteLine($"open failed, needsPassword={res.NeedsPassword}");
                return;
            }
            uint count = mu_engine_page_count(res.Engine);
            Console.WriteLine($"pages={count}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var svg = ExportSvg(res.Engine, page);
            sw.Stop();
            if (svg == null) { Console.WriteLine("export failed"); return; }
            Console.WriteLine($"export ms={sw.Elapsed.TotalMilliseconds:0}  len={svg.Length} ({svg.Length / 1024.0:0.0}KB)");

            // 统计关键元素
            int paths = Count(svg, "<path");
            int rects = Count(svg, "<rect");
            int imgs = Count(svg, "<image");
            int texts = Count(svg, "<text");
            int groups = Count(svg, "<g");
            int defs = Count(svg, "<defs");
            Console.WriteLine($"paths={paths} rects={rects} images={imgs} texts={texts} groups={groups} defs={defs}");

            // 检查 viewBox
            int vb = svg.IndexOf("viewBox", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine("viewBox=" + (vb >= 0 ? svg.Substring(vb, Math.Min(60, svg.Length - vb)).Replace('\n', ' ') : "MISSING"));

            // flatten 验证：<use> 引用展开为 <path>（Win2D 的 D2D SVG 不支持 <use>）
            var flattened = FlattenSvgText(svg);
            int usesAfter = Count(flattened, "<use");
            Console.WriteLine($"flatten: use_remaining={usesAfter} path_total={Count(flattened, "<path")}");

            // 命令流导出验证（v2 几何缓存路径）
            Console.WriteLine("\n--- command stream ---");
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            var cmds = ExportCommands(res.Engine, page);
            sw2.Stop();
            Console.WriteLine($"export ms={sw2.Elapsed.TotalMilliseconds:0}  len={cmds?.Length ?? 0} bytes");
            if (cmds != null && cmds.Length >= 16)
            {
                int o = 0;
                uint magic = BitConverter.ToUInt32(cmds, o); o += 4;
                uint ver = BitConverter.ToUInt32(cmds, o); o += 4;
                float pw = BitConverter.ToSingle(cmds, o); o += 4;
                float ph = BitConverter.ToSingle(cmds, o); o += 4;
                uint nCmds = BitConverter.ToUInt32(cmds, o); o += 4;
                uint nImgs = BitConverter.ToUInt32(cmds, o); o += 4;
                uint nSkip = BitConverter.ToUInt32(cmds, o); o += 4;
                Console.WriteLine($"magic=0x{magic:X8} ver={ver} page={pw:0.##}x{ph:0.##} cmds={nCmds} images={nImgs} skipped={nSkip}");
                var skipParts = new List<string>();
                for (uint s = 0; s < nSkip && o + 5 <= cmds.Length; s++)
                {
                    byte tag = cmds[o++];
                    uint cnt = BitConverter.ToUInt32(cmds, o); o += 4;
                    skipParts.Add($"tag{tag}:{cnt}");
                }
                if (skipParts.Count > 0) Console.WriteLine("skipped: " + string.Join(" ", skipParts));
                // 前 6 条命令的 tag 预览
                var tagCounts = new Dictionary<byte, int>();
                for (uint c = 0; c < nCmds && o < cmds.Length; c++)
                {
                    byte tag = cmds[o++];
                    if (tagCounts.ContainsKey(tag)) tagCounts[tag]++; else tagCounts[tag] = 1;
                    // 跳过 payload（简单跳过：不再精细解析，仅统计 tag 分布）
                    // 用 SkipCommand 逻辑
                    o = SkipCommandOffset(cmds, o, tag);
                }
                Console.WriteLine("tag histogram: " + string.Join(" ", tagCounts.OrderBy(kv => kv.Key).Select(kv => $"tag{kv.Key}:{kv.Value}")));

                // 打印前 260 条命令的完整 tag 序列（诊断图章/软蒙版结构）
                int imgStart = o; // tagCounts 循环后停在命令区末尾 = 图像区起点
                int cmdStart = 28 + (int)nSkip * 5;
                int o2 = cmdStart;
                List<int> seq = new();
                for (int c = 0; c < nCmds && o2 < imgStart && c < 260; c++)
                {
                    byte tag = cmds[o2++];
                    seq.Add(tag);
                    switch (tag)
                    {
                        case 1: o2 += 1 + 16; o2 = SkipPathOffset(cmds, o2); break;
                        case 2:
                        {
                            o2 += 2 + 8;
                            uint ndash = BitConverter.ToUInt32(cmds, o2); o2 += 4;
                            o2 += (int)ndash * 4 + 16;
                            o2 = SkipPathOffset(cmds, o2);
                            break;
                        }
                        case 3: o2 += 1; o2 = SkipPathOffset(cmds, o2); break;
                        case 5: o2 += 32; break;
                        case 6: o2 += 24 + 4 + 16 + 4; break;
                        default: o2 = imgStart; break;
                    }
                }
                Console.WriteLine("cmd seq (first 260):");
                for (int grp = 0; grp < seq.Count; grp += 60)
                    Console.WriteLine("  " + string.Join(" ", seq.Skip(grp).Take(60)));

                // 命令流路径顶点 y 范围统计：判断内容上下方向（fitz 页面空间 y 向下）
                Console.WriteLine("\n--- path y-extent ---");
                int yp = cmdStart;
                float minY = float.MaxValue, maxY = float.MinValue, minX = float.MaxValue, maxX = float.MinValue;
                long npts = 0, np = 0;
                for (int c = 0; c < nCmds && yp < imgStart; c++)
                {
                    byte tag = cmds[yp++];
                    if (tag == 3) { yp += 1; yp = SkipPathOffset(cmds, yp); continue; } // skip clip
                    if (tag == 1 || tag == 2)
                    {
                        if (tag == 1) yp += 1 + 16; else { yp += 2 + 8; uint nd = BitConverter.ToUInt32(cmds, yp); yp += 4 + (int)nd * 4 + 16; }
                        np++;
                        uint pcnt = BitConverter.ToUInt32(cmds, yp); yp += 4;
                        for (uint kk = 0; kk < pcnt && yp < imgStart; kk++)
                        {
                            byte t = cmds[yp++];
                            float x0f = 0, y0f = 0, x1f = 0;
                            if (t == 0 || t == 1) { x0f = BitConverter.ToSingle(cmds, yp); y0f = BitConverter.ToSingle(cmds, yp + 4); yp += 8; npts++; }
                            else if (t == 2) { x0f = BitConverter.ToSingle(cmds, yp); y0f = BitConverter.ToSingle(cmds, yp + 4); yp += 24; npts++; }
                            else if (t == 3) { }
                            if (t == 0 || t == 1 || t == 2)
                            {
                                if (x0f < minX) minX = x0f; if (x0f > maxX) maxX = x0f;
                                if (y0f < minY) minY = y0f; if (y0f > maxY) maxY = y0f;
                            }
                        }
                    }
                    else if (tag == 5) yp += 32;
                    else if (tag == 6) yp += 24 + 4 + 16 + 4;
                    else if (tag == 4) { }
                    else break;
                }
                Console.WriteLine($"paths analyzed={np} pts={npts}");
                Console.WriteLine($"x: [{minX:0.#}, {maxX:0.#}]  y: [{minY:0.#}, {maxY:0.#}]  (page {pw}x{ph})");
                // 保存命令流供外部（python）独立可视化核对坐标方向
                try
                {
                    string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cmdstream_p{page}.bin");
                    System.IO.File.WriteAllBytes(outPath, cmds);
                    Console.WriteLine("saved stream: " + outPath);
                }
                catch (Exception ex) { Console.WriteLine("save stream failed: " + ex.Message); }

                // 前 6 条非空 path 的起始顶点（move 点），判断坐标在页面的位置
                {
                    Console.WriteLine("first path starts:");
                    int sp = cmdStart;
                    for (int c = 0; c < nCmds && sp < imgStart && c < 400; c++)
                    {
                        byte tag = cmds[sp++];
                        if (tag == 1 || tag == 2)
                        {
                            if (tag == 1) sp += 1 + 16; else { sp += 2 + 8; uint nd = BitConverter.ToUInt32(cmds, sp); sp += 4 + (int)nd * 4 + 16; }
                            uint pn = BitConverter.ToUInt32(cmds, sp); sp += 4;
                            float fx = float.NaN, fy = float.NaN;
                            for (uint kk = 0; kk < pn && sp < imgStart; kk++)
                            {
                                byte tt = cmds[sp++];
                                if (tt == 0) { fx = BitConverter.ToSingle(cmds, sp); fy = BitConverter.ToSingle(cmds, sp + 4); sp += 8; break; }
                                else if (tt == 1) sp += 8;
                                else if (tt == 2) sp += 24;
                                else if (tt == 3) { }
                            }
                            Console.WriteLine($"  path#{c} tag={tag} firstMove=({fx:0.#},{fy:0.#})");
                            if (c >= 6) break;
                        }
                        else if (tag == 3) { sp += 1; sp = SkipPathOffset(cmds, sp); }
                        else if (tag == 5) sp += 32;
                        else if (tag == 6) sp += 24 + 4 + 16 + 4;
                        else if (tag == 4) { }
                        else break;
                    }
                }

                // 图像数据统计（dump 前几个像素 + 均值 + 通道分布）
                Console.WriteLine("\n--- image data ---");
                for (uint k = 0; k < nImgs && imgStart + 16 <= cmds.Length; k++)
                {
                    uint id = BitConverter.ToUInt32(cmds, imgStart); imgStart += 4;
                    uint w = BitConverter.ToUInt32(cmds, imgStart); imgStart += 4;
                    uint h = BitConverter.ToUInt32(cmds, imgStart); imgStart += 4;
                    uint len = BitConverter.ToUInt32(cmds, imgStart); imgStart += 4;
                    Console.WriteLine($"img id={id} {w}x{h} dataLen={len}");
                    if (len >= 64 && imgStart + 64 <= cmds.Length)
                    {
                        // 采样关键像素：四角 + 中心
                        int[] pts = { 0, (int)(w / 2), (int)w - 1, 0, (int)(h * 0.3), (int)(h - 1) };
                        int[] px = { (int)(w / 4), (int)(w / 2), (int)(w - 1 - w / 4), (int)(h / 2), (int)(w / 2), (int)(w / 2) };
                        for (int s = 0; s < 6; s++)
                        {
                            int o4 = imgStart + (pts[s + 0] * 0 + 0) * 0; // placeholder
                        }
                        // 简化：中心 + 1/4 处 + 边缘中点
                        int[] sx = { (int)(w / 2), (int)(w / 4), (int)w - 1, (int)(w / 2), (int)(w / 2) };
                        int[] sy = { (int)(h / 2), (int)(h / 2), (int)(h / 2), (int)0, (int)(h - 1) };
                        for (int s = 0; s < 5; s++)
                        {
                            if (sx[s] < 0 || sx[s] >= w || sy[s] < 0 || sy[s] >= h) continue;
                            int o4 = imgStart + (sy[s] * (int)w + sx[s]) * 4;
                            byte B = cmds[o4], G = cmds[o4 + 1], R = cmds[o4 + 2], A = cmds[o4 + 3];
                            Console.WriteLine($"  px({sx[s]},{sy[s]}) RGBA=({R},{G},{B},{A})");
                        }
                    }
                    if (len >= 64 && imgStart + 64 <= cmds.Length)
                    {
                        long sum = 0; long min = 999, max = 0; int nz = 0;
                        int nPix = (int)Math.Min(len / 4, 100000);
                        for (int p = 0; p < nPix; p++)
                        {
                            int o4 = imgStart + p * 4;
                            byte b0 = cmds[o4], g0 = cmds[o4 + 1], r0 = cmds[o4 + 2], a0 = cmds[o4 + 3];
                            byte v = (byte)Math.Max(r0, Math.Max(g0, b0));
                            sum += v; if (v < min) min = v; if (v > max) max = v; if (v > 32) nz++;
                        }
                        Console.WriteLine($"first16: {string.Join(" ", cmds.Skip(imgStart).Take(16))}");
                    Console.WriteLine($"sample {nPix}px: avg={(sum / (double)nPix):0.0} min={min} max={max} nonBlack={nz} ({(nz * 100.0 / nPix):0}%)");
                    // 分散采样：整幅分 5×5 网格，每格中心取 16×16 块统计平均亮度
                    Console.Write("grid avg (5x5): ");
                    int gw = (int)w / 5, gh2 = (int)h / 5;
                    for (int gx = 0; gx < 5; gx++)
                    {
                        for (int gy = 0; gy < 5; gy++)
                        {
                            long s = 0; int cnt = 0;
                            for (int dy = -8; dy < 9; dy++)
                                for (int dx = -8; dx < 9; dx++)
                                {
                                    int px = Math.Min((int)w - 1, Math.Max(0, gx * gw + gw / 2 + dx));
                                    int py = Math.Min((int)h - 1, Math.Max(0, gy * gh2 + gh2 / 2 + dy));
                                    int o4 = imgStart + (py * (int)w + px) * 4;
                                    byte v = Math.Max(cmds[o4 + 2], Math.Max(cmds[o4 + 1], cmds[o4]));
                                    s += v; cnt++;
                                }
                            Console.Write($"{s / cnt,3} ");
                        }
                        Console.WriteLine();
                    }
                    }
                    imgStart += (int)len;
                }
                // 对照：用现有 region 渲染路径渲染同一页中心 64×64，验证引擎输出是否正常
                Console.WriteLine("\n--- region render control (page center 64x64) ---");
                uint cxp = 1760, cyp = 2346; // 3520x4693 中心
                IntPtr rdataPtr = IntPtr.Zero; ulong rdataLen = 0; uint rw = 0, rh = 0;
                var rr = mu_render_page_region_bgra(res.Engine, page, 3520, 4693, cxp - 32, cyp - 32, cxp + 32, cyp + 32, out rdataPtr, out rdataLen, out rw, out rh);
                Console.WriteLine($"region success={rr.Success} len={rdataLen} {rw}x{rh}");
                if (rr.Success && rdataLen > 0)
                {
                    byte[] rdata = new byte[rdataLen];
                    Marshal.Copy(rdataPtr, rdata, 0, (int)rdataLen);
                    memory_free(rdataPtr, rdataLen);
                    Console.WriteLine($"region first16: {string.Join(" ", rdata.Take(16))}");
                    long rsum = 0; int rmin = 999, rmax = 0, rnz = 0;
                    int rnPix = (int)Math.Min(rdata.Length / 4, 4000);
                    for (int p = 0; p < rnPix; p++)
                    {
                        int o4 = p * 4;
                        byte v = Math.Max(rdata[o4 + 2], Math.Max(rdata[o4 + 1], rdata[o4]));
                        rsum += v; if (v < rmin) rmin = v; if (v > rmax) rmax = v; if (v > 32) rnz++;
                    }
                    Console.WriteLine($"region sample {rnPix}px: avg={(rsum / (double)rnPix):0.0} min={rmin} max={rmax} nonBlack={rnz} ({(rnz * 100.0 / rnPix):0}%)");
                }
                else Console.WriteLine("region render FAILED");

                // 方向对照：渲染整页为 BMP，直接查看方向
                Console.WriteLine("\n--- full-page BMP (tile path) ---");
                try
                {
                    uint fw = 502, fh = 733;
                    IntPtr fP = IntPtr.Zero; ulong fL = 0; uint fW = 0, fH = 0;
                    var rf = mu_render_page_region_bgra(res.Engine, page, fw, fh, 0, 0, fw, fh, out fP, out fL, out fW, out fH);
                    Console.WriteLine($"full-page success={rf.Success} len={fL} {fW}x{fH}");
                    if (rf.Success && fL == (ulong)fw * fh * 4)
                    {
                        byte[] fD = new byte[fL]; Marshal.Copy(fP, fD, 0, (int)fL); memory_free(fP, fL);
                        // 写 BMP（DIB bottom-up 但 Win 显示会翻正；加文件头）
                        int bmpRowSize = (int)(fw * 4 + 3) / 4 * 4;
                        int bmpDataSize = bmpRowSize * (int)fh;
                        int bmpFileSize = 54 + bmpDataSize;
                        byte[] bmp = new byte[bmpFileSize];
                        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
                        BitConverter.GetBytes(bmpFileSize).CopyTo(bmp, 2);
                        BitConverter.GetBytes(54).CopyTo(bmp, 10);
                        BitConverter.GetBytes(40).CopyTo(bmp, 14);
                        BitConverter.GetBytes((int)fw).CopyTo(bmp, 18);
                        BitConverter.GetBytes((int)fh).CopyTo(bmp, 22);
                        BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
                        BitConverter.GetBytes((short)32).CopyTo(bmp, 28);
                        // 像素行（bottom-up：BMP 第一行是图像最后一行；BGRA 原样）
                        for (int y = 0; y < (int)fh; y++)
                        {
                            int srcRow = (int)fh - 1 - y;
                            Buffer.BlockCopy(fD, srcRow * (int)fw * 4, bmp, 54 + y * bmpRowSize, (int)fw * 4);
                        }
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpam_tile.bmp"), bmp);
                        Console.WriteLine("saved gpam_tile.bmp (bottom-up 已翻正，看文字方向)");
                    }
                }
                catch (Exception ex) { Console.WriteLine("full-page BMP failed: " + ex.Message); }

                // 方向对照：渲染整页上下两半，统计每半页的文字/暗像素占比（瓦片路径 = 与现有渲染相同的 dl）
                Console.WriteLine("\n--- orientation pixel stats (tile path) ---");
                try
                {
                    uint fw = 502, fh = 733;
                    IntPtr aP = IntPtr.Zero; ulong aL = 0; uint aW = 0, aH = 0;
                    var ra = mu_render_page_region_bgra(res.Engine, page, fw, fh, 0, 0, fw, fh / 2, out aP, out aL, out aW, out aH);
                    IntPtr bP = IntPtr.Zero; ulong bL = 0; uint bW = 0, bH = 0;
                    var rb = mu_render_page_region_bgra(res.Engine, page, fw, fh, 0, fh / 2, fw, fh, out bP, out bL, out bW, out bH);
                    Console.WriteLine($"top-half success={ra.Success} len={aL} {aW}x{aH}");
                    Console.WriteLine($"bot-half success={rb.Success} len={bL} {bW}x{bH}");
                    if (ra.Success && rb.Success && aL > 0 && bL > 0)
                    {
                        byte[] aD = new byte[aL]; Marshal.Copy(aP, aD, 0, (int)aL); memory_free(aP, aL);
                        byte[] bD = new byte[bL]; Marshal.Copy(bP, bD, 0, (int)bL); memory_free(bP, bL);
                        long ad = 0; foreach (var v in aD) ad += (v < 120) ? 1 : 0;
                        long bd = 0; foreach (var v in bD) bd += (v < 120) ? 1 : 0;
                        long anD = 0; foreach (var v in aD) if (Math.Abs(v - 255) > 30) anD++;
                        long bnD = 0; foreach (var v in bD) if (Math.Abs(v - 255) > 30) bnD++;
                        Console.WriteLine($"top:  dark={ad} nonWhite={anD}");
                        Console.WriteLine($"bot:  dark={bd} nonWhite={bnD}");
                        Console.WriteLine("=> top-vs-bot dark: " + ad + " vs " + bd + " (少者=内容向该半页聚集)");
                    }
                }
                catch (Exception ex) { Console.WriteLine("orientation stats failed: " + ex.Message); }
                Console.WriteLine("\n--- orientation probes (corner 32x32 @ 400dpi-ish) ---");
                uint dw2 = 502, dh2 = 733; // 页面 pt，1:1 渲染
                IntPtr tlp = IntPtr.Zero; ulong tll = 0; uint tlw = 0, tlh = 0;
                var rtl = mu_render_page_region_bgra(res.Engine, page, dw2, dh2, 0, 0, 100, 100, out tlp, out tll, out tlw, out tlh);
                IntPtr brp = IntPtr.Zero; ulong brl = 0; uint brw = 0, brh = 0;
                var rbr = mu_render_page_region_bgra(res.Engine, page, dw2, dh2, dw2 - 100, dh2 - 100, dw2, dh2, out brp, out brl, out brw, out brh);
                if (rtl.Success && rbr.Success && tll > 0 && brl > 0)
                {
                    byte[] tld = new byte[tll]; Marshal.Copy(tlp, tld, 0, (int)tll); memory_free(tlp, tll);
                    byte[] brd = new byte[brl]; Marshal.Copy(brp, brd, 0, (int)brl); memory_free(brp, brl);
                    long ts = 0; DateTimeOffset now = DateTimeOffset.UtcNow;
                    int tnz = 0; for (int p = 0; p < (int)Math.Min(tld.Length / 4, 2000); p++) { int o4 = p * 4; if (Math.Max(tld[o4 + 2], Math.Max(tld[o4 + 1], tld[o4])) > 32) tnz++; }
                    var tnza = tnz;
                    int bnz = 0; for (int p = 0; p < (int)Math.Min(brd.Length / 4, 2000); p++) { int o4 = p * 4; if (Math.Max(brd[o4 + 2], Math.Max(brd[o4 + 1], brd[o4])) > 32) bnz++; }
                    int tlColorful = 0, brColorful = 0;
                    for (int p = 0; p < (int)Math.Min(brd.Length / 4, 2000); p++) { int o4 = p * 4; if (Math.Abs(brd[o4 + 2] - brd[o4]) > 20 || Math.Abs(brd[o4 + 2] - brd[o4 + 1]) > 20) brColorful++; }
                    for (int p = 0; p < (int)Math.Min(tld.Length / 4, 2000); p++) { int o4 = p * 4; if (Math.Abs(tld[o4 + 2] - tld[o4]) > 20 || Math.Abs(tld[o4 + 2] - tld[o4 + 1]) > 20) tlColorful++; }
                    Console.WriteLine($"topLeft: nonBlack={tnza}/2000 colorful={tlColorful}/2000");
                    Console.WriteLine($"bottomRight: nonBlack={bnz}/2000 colorful={brColorful}/2000");
                }
                else Console.WriteLine("orientation probes FAILED");

            }

            // 写文件供检查
            string outFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "glance_probe_page.svg");
            System.IO.File.WriteAllText(outFile, flattened);
            Console.WriteLine("saved: " + outFile);

            mu_engine_destroy(res.Engine);
        }
        finally { Marshal.FreeHGlobal(pathPtr); }
    }

    static int Count(string s, string needle)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    static int SkipCommandOffset(byte[] b, int i, byte tag)
    {
        if (i >= b.Length) return i;
        switch (tag)
        {
            case 1: i += 1 + 16; return SkipPathOffset(b, i);
            case 2:
            {
                i += 2 + 8;
                uint ndash = BitConverter.ToUInt32(b, i); i += 4;
                i += (int)ndash * 4 + 16;
                return SkipPathOffset(b, i);
            }
            case 3: i += 1; return SkipPathOffset(b, i);
            case 4: return i;
            case 5: return i + 32;
            case 6: return i + 24 + 4 + 16 + 4;
            default: return i;
        }
    }

    static int SkipPathOffset(byte[] b, int i)
    {
        if (i + 4 > b.Length) return i;
        uint n = BitConverter.ToUInt32(b, i); i += 4;
        for (uint k = 0; k < n && i < b.Length; k++)
        {
            byte t = b[i++];
            if (t == 0 || t == 1) i += 8;
            else if (t == 2) i += 24;
            else if (t == 3) { }
        }
        return i;
    }

    // 与 VectorProbePage.FlattenSvgText 相同的展开逻辑，验证 D2D 渲染可行性
    static string FlattenSvgText(string svg)
    {
        var defs = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(svg, "<path\\s+id=\"([^\"]+)\"[^>]*\\sd=\"([^\"]*)\"\\s*/>"))
            defs[m.Groups[1].Value] = m.Groups[2].Value;
        if (defs.Count == 0) return svg;

        return Regex.Replace(svg,
            "<use\\b([^>]*?)xlink:href=\"#([^\"]+)\"([^/>]*?)/>",
            m =>
            {
                if (!defs.TryGetValue(m.Groups[2].Value, out string? d)) return m.Value;
                return $"<path d=\"{d}\"{m.Groups[1].Value}{m.Groups[3].Value}/>";
            });
    }
}