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
using System.IO;
using System.Linq;
using System.Reflection;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Drawing;
using GlanceRX;

namespace GlanceRX.Services;

/// <summary>
/// 把批注写成 PDF 原生的 /Ink 注解对象（非破坏性、可移除、可显隐），而非把笔迹烘焙进
/// 页面内容流变成像素。文件即唯一真相源：每次保存先移除旧的 GlanceAnno 标记注解再重加，
/// 保证幂等、绝不堆叠重复笔迹。批注是独立对象，原始页面内容永不被改动。
/// </summary>
public class PdfExportService
{
    // PdfGenericAnnotation 是 internal 类，但其 (PdfDictionary) 构造为 public，可跨程序集反射创建。
    // 用 /Subtype /Ink 把它变成真实墨迹注解对象。
    private static readonly Type? GenericAnnotType =
        typeof(PdfAnnotation).Assembly.GetType("PdfSharp.Pdf.Annotations.PdfGenericAnnotation");
    private static readonly ConstructorInfo? GenericAnnotCtor =
        GenericAnnotType?
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 1 &&
                                 c.GetParameters()[0].ParameterType == typeof(PdfDictionary));

    public async Task ExportPdfWithAnnotationsAsync(
        string inputPdfPath,
        string outputPdfPath,
        List<SavedAnnotation> annotations,
        Dictionary<int, double>? pageRotations = null,
        string? password = null)
    {
        if (GenericAnnotCtor == null)
            throw new InvalidOperationException("PdfGenericAnnotation 反射构造不可用，无法写入真实批注对象。");

        await Task.Run(() =>
        {
            // 旧版 PDF 解析可能用到代码页编码提供器
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            }
            catch { }

            using (PdfDocument document = string.IsNullOrEmpty(password)
                ? PdfReader.Open(inputPdfPath, PdfDocumentOpenMode.Modify)
                : PdfReader.Open(inputPdfPath, password, PdfDocumentOpenMode.Modify))
            {
                // 1) 用户旋转烘焙进 /Rotate（整页旋转，含批注；与旧方案视觉一致）
                for (int i = 0; i < document.Pages.Count; i++)
                {
                    PdfPage p = document.Pages[i];
                    if (pageRotations != null && pageRotations.TryGetValue(i, out double userRot))
                    {
                        p.Rotate = (p.Rotate + (int)userRot) % 360;
                    }
                }

                // 2) 幂等：移除旧版(/GlanceAnno)和修正版(/GlanceAnnoV2)真实批注对象，避免重复堆叠
                for (int i = 0; i < document.Pages.Count; i++)
                {
                    var page = document.Pages[i];
                    for (int j = page.Annotations.Count - 1; j >= 0; j--)
                    {
                        var els = page.Annotations[j].Elements;
                        if (els.ContainsKey("/GlanceAnno") || els.ContainsKey("/GlanceAnnoV2"))
                            page.Annotations.Remove(page.Annotations[j]);
                    }
                }

                // 3) 把当前批注写成真实 /Ink 对象（按页分组）
                var byPage = annotations
                    .Where(a => a != null && a.Type == AnnotationType.Pen && a.Points != null && a.Points.Count > 1)
                    .GroupBy(a => a.PageIndex)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var kv in byPage)
                {
                    int pageIndex = kv.Key;
                    if (pageIndex < 0 || pageIndex >= document.Pages.Count) continue;

                    PdfPage page = document.Pages[pageIndex];
                    int rot = page.Rotate;

                    // ⚠ 关键：注释坐标必须在 MediaBox（未旋转）坐标系下计算。
                    //    PdfSharp 的 page.Width/Height.Point 在刚设置 Rotate 后（Modify 模式）
                    //    返回【旋转后显示尺寸】（如 Rotate=90 时 Width=MediaBox.Height），
                    //    而读回（Import 模式）返回 MediaBox 未旋转尺寸 → 写读参数不一致，
                    //    坐标全部错位（视觉 180° 旋转）。CropBox 维度始终未旋转，安全。
                    double cropX = 0, cropY = 0, Wc, Hc;
                    if (page.Elements.ContainsKey("/CropBox") && page.CropBox != null)
                    {
                        var cb = page.CropBox;
                        cropX = cb.X1;
                        cropY = page.MediaBox.Height - cb.Y2;
                        Wc = cb.X2 - cb.X1;
                        Hc = cb.Y2 - cb.Y1;
                    }
                    else
                    {
                        Wc = page.MediaBox.Width;
                        Hc = page.MediaBox.Height;
                    }

                    foreach (var anno in kv.Value)
                    {
                        // 单条笔迹失败绝不能拖垮整页（否则会连累同页其它正常笔迹一起丢失）。
                        // 只有 NaN/Infinity 这类非法点才过滤，避免生成非法 PDF 使整份文档保存失败。
                        try
                        {
                            double minX = double.MaxValue, minY = double.MaxValue;
                            double maxX = double.MinValue, maxY = double.MinValue;
                            var phys = new List<(double x, double y)>(anno.Points.Count);
                            foreach (var pt in anno.Points)
                            {
                                if (!double.IsFinite(pt.X) || !double.IsFinite(pt.Y)) continue;
                                var ph = MapPointToPhysical(pt.X, pt.Y, rot, Wc, Hc, cropX, cropY);
                                phys.Add(ph);
                                if (ph.x < minX) minX = ph.x;
                                if (ph.y < minY) minY = ph.y;
                                if (ph.x > maxX) maxX = ph.x;
                                if (ph.y > maxY) maxY = ph.y;
                            }
                            if (phys.Count < 2) continue;

                            double thick = (anno.Thickness > 0 && double.IsFinite(anno.Thickness)) ? anno.Thickness : 3.5;
                            double margin = thick / 2.0 + 1.0; // /Rect 须包住整条笔迹（含半粗）
                            minX -= margin; minY -= margin; maxX += margin; maxY += margin;

                            var dict = new PdfDictionary(document);
                            var annot = (PdfAnnotation)GenericAnnotCtor.Invoke(new object[] { dict });
                            annot.Elements.SetName("/Subtype", "/Ink");
                            annot.Elements.SetRectangle("/Rect",
                                new PdfRectangle(new XPoint(minX, minY), new XPoint(maxX, maxY)));

                            // /InkList：一条笔画 = 一个坐标数组（x,y 交替）
                            var inkList = new PdfArray();
                            var stroke = new PdfArray();
                            foreach (var ph in phys)
                            {
                                stroke.Elements.Add(new PdfReal(ph.x));
                                stroke.Elements.Add(new PdfReal(ph.y));
                            }
                            inkList.Elements.Add(stroke);
                            annot.Elements["/InkList"] = inkList;

                            // /C：笔迹颜色（0..1）
                            var col = new PdfArray();
                            (double r, double g, double b) = ParseColorRgb(anno.ColorHex);
                            col.Elements.Add(new PdfReal(r));
                            col.Elements.Add(new PdfReal(g));
                            col.Elements.Add(new PdfReal(b));
                            annot.Elements["/C"] = col;

                            // /BS：边框样式（线宽 = 笔粗）
                            var bs = new PdfDictionary();
                            bs.Elements.SetName("/S", "/S");
                            bs.Elements.SetReal("/W", thick);
                            annot.Elements["/BS"] = bs;

                            // GlanceAnno 幂等标记（保留）+ GlanceAnnoV2（新版坐标系 = MediaBox，区分遗留）
                            annot.Elements.SetString("/GlanceAnno", "1");
                            annot.Elements.SetString("/GlanceAnnoV2", "1");
                            page.Annotations.Add(annot);
                        }
                        catch { /* 跳过这一条，保住其余笔迹 */ }
                    }
                }

                document.Save(outputPdfPath);
            }
        });
    }

    /// <summary>
    /// 快速预检：文件里是否含 Glance 批注标记 "/GlanceAnno"（旧版 /GlanceAnno 与新版
    /// /GlanceAnnoV2 是子串关系，一次命中全覆盖）。Glance 保存的注解字典是 PdfSharp 落盘的
    /// 直写对象（非流压缩），键名必然以明文出现在文件字节中 → 未命中即可确定本文件没有任何
    /// Glance 批注，从而跳过整份 PdfSharp 解析。
    ///
    /// 为什么必须跳过：PdfReader.Open(Import) 会把整份 PDF（全部对象 + 解压后的流）载入托管堆，
    /// 一本杂志 PDF 可达数百 MB —— 这正是"首开不操作冲到 1.2GB、约 5s 回落"的主因。普通打开
    /// 无批注文档时先做一次 1MB 分块滑动扫描（.NET SIMD IndexOf，一本杂志 &lt;1s、额外内存 ~1MB），
    /// 未命中直接返回空列表，首开尖峰彻底消除。
    ///
    /// 加密文档无法预检（内容已加密、键名不可见）→ 保守走完整解析（加密文档通常即用户批注过的文件）。
    /// </summary>
    private static bool FileContainsGlanceMarker(string pdfPath, string? password)
    {
        if (!string.IsNullOrEmpty(password)) return true; // 加密：内容不可见，保守走完整解析
        try
        {
            byte[] needle = System.Text.Encoding.ASCII.GetBytes("/GlanceAnno");
            int chunk = 1 << 20;              // 1MB/块
            int overlap = needle.Length - 1;  // 跨块重叠长度，防标记被切成两半漏检
            byte[] buf = new byte[chunk + overlap];
            using var fs = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int carry = 0;
            while (true)
            {
                int n = fs.Read(buf, carry, chunk);
                if (n <= 0) break;
                int total = carry + n;
                if (buf.AsSpan(0, total).IndexOf(needle) >= 0) return true;
                carry = Math.Min(overlap, total);
                if (carry > 0) Buffer.BlockCopy(buf, total - carry, buf, 0, carry);
            }
            return false;
        }
        catch { return true; } // 读失败：保守走完整解析，不吞批注
    }

    /// <summary>
    /// 从 PDF 文件读回 Glance 写入的真实 /Ink 注解对象（带 /GlanceAnno 标记），
    /// 还原为 SavedAnnotation 列表（坐标经 MapPointFromPhysical 逆变换回叠加层逻辑页坐标，
    /// 与写入严格互逆 → 显示的笔迹逐点一致）。文件即唯一真相源，无需任何 JSON/副本。
    /// </summary>
    public static List<SavedAnnotation> ReadGlanceInkAnnotations(string pdfPath, string? password)
    {
        var result = new List<SavedAnnotation>();
        // 快速预检：无 Glance 批注标记 → 跳过整份 PdfSharp 解析（消除首开数百 MB 托管尖峰）。
        if (!FileContainsGlanceMarker(pdfPath, password)) return result;
        try
        {
            using (var fs = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (PdfDocument doc = string.IsNullOrEmpty(password)
                ? PdfReader.Open(fs, PdfDocumentOpenMode.Import)
                : PdfReader.Open(fs, password, PdfDocumentOpenMode.Import))
            {
                for (int i = 0; i < doc.Pages.Count; i++)
                {
                    PdfPage page = doc.Pages[i];
                    int rot = page.Rotate;
                    // ⚠ 必须用 MediaBox 原始宽高（与写入路径严格一致），否则 R≠0 页面坐标全部错位
                    double Wc, Hc;
                    double cropX = 0, cropY = 0;
                    if (page.Elements.ContainsKey("/CropBox") && page.CropBox != null)
                    {
                        var cb = page.CropBox;
                        cropX = cb.X1;
                        cropY = page.MediaBox.Height - cb.Y2;
                        Wc = cb.X2 - cb.X1;
                        Hc = cb.Y2 - cb.Y1;
                    }
                    else
                    {
                        Wc = page.MediaBox.Width;
                        Hc = page.MediaBox.Height;
                    }

                    // 注解集合的枚举器返回 PdfItem，须用索引器取 PdfAnnotation（其 Elements 才可用）
                    for (int ai = 0; ai < page.Annotations.Count; ai++)
                    {
                        PdfAnnotation a = page.Annotations[ai];
                        // 单条注解解析失败不拖垮整页
                        try
                        {
                            bool isLegacy = a.Elements.ContainsKey("/GlanceAnno") && !a.Elements.ContainsKey("/GlanceAnnoV2");
                            // V2（新写入，坐标基于 MediaBox 计算） 或 /GlanceAnno（兼容旧）
                            if (!isLegacy && !a.Elements.ContainsKey("/GlanceAnnoV2")) continue;
                            if (a.Elements.GetName("/Subtype") != "/Ink") continue;
                            if (a.Elements["/InkList"] is not PdfArray inkList || inkList.Elements.Count == 0) continue;
                            if (inkList.Elements[0] is not PdfArray stroke || stroke.Elements.Count < 2) continue;

                            // 遗留笔迹迁移：pre-fix 版本写入时 page.Width/Height.Point 返回【MediaBox 未旋转尺寸】
                            // (mbW, mbH)，与读回时不一致 → 坐标错位。遗留逆变换：用 MediaBox 原尺寸
                            // (mbW, mbH) 直接还原（不交换），与旧写时 ToPhys 的参数严格互逆。
                            double useWc = Wc, useHc = Hc;
                            if (isLegacy)
                            {
                                double mbW = page.MediaBox.Width, mbH = page.MediaBox.Height;
                                useWc = mbW;
                                useHc = mbH;
                            }

                            var anno = new SavedAnnotation
                            {
                                PageIndex = i,
                                Type = AnnotationType.Pen,
                                ColorHex = ReadColorHex(a),
                                Thickness = ReadThickness(a)
                            };
                            for (int k = 0; k + 1 < stroke.Elements.Count; k += 2)
                            {
                                double vx = AsDouble(stroke.Elements[k]);
                                double vy = AsDouble(stroke.Elements[k + 1]);
                                if (!double.IsFinite(vx) || !double.IsFinite(vy)) continue;
                                var (lx, ly) = MapPointFromPhysical(vx, vy, rot, useWc, useHc, cropX, cropY);
                                anno.Points.Add(new SavedPoint { X = lx, Y = ly });
                            }
                            if (anno.Points.Count >= 2) result.Add(anno);
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
        return result;
    }

    private static string ReadColorHex(PdfAnnotation a)
    {
        try
        {
            if (a.Elements["/C"] is PdfArray c && c.Elements.Count >= 3)
            {
                double r = AsDouble(c.Elements[0]);
                double g = AsDouble(c.Elements[1]);
                double b = AsDouble(c.Elements[2]);
                if (double.IsFinite(r) && double.IsFinite(g) && double.IsFinite(b))
                {
                    byte rr = (byte)Math.Clamp((int)Math.Round(r * 255), 0, 255);
                    byte gg = (byte)Math.Clamp((int)Math.Round(g * 255), 0, 255);
                    byte bb = (byte)Math.Clamp((int)Math.Round(b * 255), 0, 255);
                    return $"#{rr:X2}{gg:X2}{bb:X2}";
                }
            }
        }
        catch { }
        return "#F2C200";
    }

    private static double ReadThickness(PdfAnnotation a)
    {
        try
        {
            if (a.Elements["/BS"] is PdfDictionary bs)
            {
                double w = AsDouble(bs.Elements["/W"]);
                if (double.IsFinite(w) && w > 0) return w;
            }
        }
        catch { }
        return 3.5;
    }

    /// <summary>
    /// PdfSharp 读回数值时：整数形态为 PdfInteger(Value:Int32)、小数形态为 PdfReal(Value:double)。
    /// 统一取 double；元素缺失/非法返回 NaN 由调用方过滤。
    /// </summary>
    private static double AsDouble(PdfItem? item)
    {
        switch (item)
        {
            case PdfReal r: return r.Value;
            case PdfInteger i: return i.Value;
            default: return double.NaN;
        }
    }

    /// <summary>
    /// 把覆盖层"逻辑页坐标"(未旋转显示帧)映射到 PDF 物理页坐标(未旋转原生空间)。
    /// 与旧 XGraphics 烘焙方案完全一致，保证新旧保存的笔迹在页面上的位置逐点相同。
    /// </summary>
    private static (double x, double y) MapPointToPhysical(
        double x_pt_ui, double y_pt_ui,
        int rotation,
        double Wc, double Hc,
        double cropXOffset, double cropYOffset)
    {
        // ⚠ PDF 物理坐标：原点 = 页左下角，Y 轴向上；
        //   UI 逻辑坐标：原点 = 显示帧左上角，Y 轴向下。
        //   除了旋转映射，必须额外做一次 Y 轴翻转。以下每个分支均已合并翻转，
        //   推导参考 dev-docs。
        double x_cropped, y_cropped;
        switch (rotation)
        {
            case 90:
                x_cropped = y_pt_ui;
                y_cropped = x_pt_ui;
                break;
            case 180:
                x_cropped = Wc - x_pt_ui;
                y_cropped = y_pt_ui;
                break;
            case 270:
                x_cropped = Wc - y_pt_ui;
                y_cropped = Hc - x_pt_ui;
                break;
            default: // 0
                x_cropped = x_pt_ui;
                y_cropped = Hc - y_pt_ui;
                break;
        }
        return (x_cropped + cropXOffset, y_cropped + cropYOffset);
    }

    /// <summary>MapPointToPhysical 的逆变换：物理页坐标 → 覆盖层逻辑页坐标。</summary>
    private static (double x, double y) MapPointFromPhysical(
        double px, double py,
        int rotation,
        double Wc, double Hc,
        double cropXOffset, double cropYOffset)
    {
        double x_c = px - cropXOffset;
        double y_c = py - cropYOffset;
        double x, y;
        switch (rotation)
        {
            case 90:
                x = y_c;
                y = x_c;
                break;
            case 180:
                x = Wc - x_c;
                y = y_c;
                break;
            case 270:
                x = Hc - y_c;
                y = Wc - x_c;
                break;
            default: // 0
                x = x_c;
                y = Hc - y_c;
                break;
        }
        return (x, y);
    }

    /// <summary>把 #RRGGBB / #AARRGGBB 解析为 0..1 的 (r,g,b)。</summary>
    private static (double r, double g, double b) ParseColorRgb(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return (1.0, 1.0, 0.0);
        try
        {
            string h = hex.Replace("#", "");
            if (h.Length == 8) h = h.Substring(2); // 丢弃 alpha
            if (h.Length == 6)
            {
                byte r = Convert.ToByte(h.Substring(0, 2), 16);
                byte g = Convert.ToByte(h.Substring(2, 2), 16);
                byte b = Convert.ToByte(h.Substring(4, 2), 16);
                return (r / 255.0, g / 255.0, b / 255.0);
            }
        }
        catch { }
        return (1.0, 1.0, 0.0); // 默认黄
    }
}
