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

/// <summary>把批注写成 PDF 原生的 /Ink 注解对象（非破坏性、可移除、可显隐），而非把笔迹烘焙进 页面内容流变成像素。文件即唯一真相源：每次保存先移除旧的 GlanceAnno 标记注解再重加， 保证幂等、绝不堆叠重复笔迹。批注是独立对象，原始页面内容永不被改动。</summary>
public class PdfExportService
{
    // PdfGenericAnnotation 是 internal 类，但其 (PdfDictionary) 构造为 public，可跨程序集反射创建。
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
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            }
            catch { }

            using (PdfDocument document = string.IsNullOrEmpty(password)
                ? PdfReader.Open(inputPdfPath, PdfDocumentOpenMode.Modify)
                : PdfReader.Open(inputPdfPath, password, PdfDocumentOpenMode.Modify))
            {
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
                            double margin = thick / 2.0 + 1.0;
                            minX -= margin; minY -= margin; maxX += margin; maxY += margin;

                            var dict = new PdfDictionary(document);
                            var annot = (PdfAnnotation)GenericAnnotCtor.Invoke(new object[] { dict });
                            annot.Elements.SetName("/Subtype", "/Ink");
                            annot.Elements.SetRectangle("/Rect",
                                new PdfRectangle(new XPoint(minX, minY), new XPoint(maxX, maxY)));

                            var inkList = new PdfArray();
                            var stroke = new PdfArray();
                            foreach (var ph in phys)
                            {
                                stroke.Elements.Add(new PdfReal(ph.x));
                                stroke.Elements.Add(new PdfReal(ph.y));
                            }
                            inkList.Elements.Add(stroke);
                            annot.Elements["/InkList"] = inkList;

                            var col = new PdfArray();
                            (double r, double g, double b) = ParseColorRgb(anno.ColorHex);
                            col.Elements.Add(new PdfReal(r));
                            col.Elements.Add(new PdfReal(g));
                            col.Elements.Add(new PdfReal(b));
                            annot.Elements["/C"] = col;

                            var bs = new PdfDictionary();
                            bs.Elements.SetName("/S", "/S");
                            bs.Elements.SetReal("/W", thick);
                            annot.Elements["/BS"] = bs;

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

    /// 快速预检：文件里是否含 Glance 批注标记 "/GlanceAnno"（旧版 /GlanceAnno 与新版 /GlanceAnnoV2 是子串关系，一次命中全覆盖）。
    /// 为什么必须跳过：PdfReader.Open(Import) 会把整份 PDF（全部对象 + 解压后的流）载入托管堆，
    private static bool FileContainsGlanceMarker(string pdfPath, string? password)
    {
        if (!string.IsNullOrEmpty(password)) return true;
        try
        {
            byte[] needle = System.Text.Encoding.ASCII.GetBytes("/GlanceAnno");
            int chunk = 1 << 20;              // 1MB/块
            int overlap = needle.Length - 1;
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

    /// <summary>从 PDF 文件读回 Glance 写入的真实 /Ink 注解对象（带 /GlanceAnno 标记）， 还原为 SavedAnnotation 列表（坐标经 MapPointFromPhysical 逆变换回叠加层逻辑页坐标， 与写入严格互逆 → 显示的笔迹逐点一致）。</summary>
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
                            if (!isLegacy && !a.Elements.ContainsKey("/GlanceAnnoV2")) continue;
                            if (a.Elements.GetName("/Subtype") != "/Ink") continue;
                            if (a.Elements["/InkList"] is not PdfArray inkList || inkList.Elements.Count == 0) continue;
                            if (inkList.Elements[0] is not PdfArray stroke || stroke.Elements.Count < 2) continue;

                            // 兼容旧文件：早期版本写入时 page.Width/Height.Point 返回【MediaBox 未旋转尺寸】
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

    /// <summary>PdfSharp 读回数值时：整数形态为 PdfInteger(Value:Int32)、小数形态为 PdfReal(Value:double)。 统一取 double；元素缺失/非法返回 NaN 由调用方过滤。</summary>
    private static double AsDouble(PdfItem? item)
    {
        switch (item)
        {
            case PdfReal r: return r.Value;
            case PdfInteger i: return i.Value;
            default: return double.NaN;
        }
    }

    /// <summary>把覆盖层"逻辑页坐标"(未旋转显示帧)映射到 PDF 物理页坐标(未旋转原生空间)。 与旧 XGraphics 烘焙方案完全一致，保证新旧保存的笔迹在页面上的位置逐点相同。</summary>
    private static (double x, double y) MapPointToPhysical(
        double x_pt_ui, double y_pt_ui,
        int rotation,
        double Wc, double Hc,
        double cropXOffset, double cropYOffset)
    {
        // ⚠ PDF 物理坐标：原点 = 页左下角，Y 轴向上；
        //   除了旋转映射，必须额外做一次 Y 轴翻转。以下每个分支均已合并翻转，
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
            default:
                x_cropped = x_pt_ui;
                y_cropped = Hc - y_pt_ui;
                break;
        }
        return (x_cropped + cropXOffset, y_cropped + cropYOffset);
    }

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
            default:
                x = x_c;
                y = Hc - y_c;
                break;
        }
        return (x, y);
    }

    private static (double r, double g, double b) ParseColorRgb(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return (1.0, 1.0, 0.0);
        try
        {
            string h = hex.Replace("#", "");
            if (h.Length == 8) h = h.Substring(2);
            if (h.Length == 6)
            {
                byte r = Convert.ToByte(h.Substring(0, 2), 16);
                byte g = Convert.ToByte(h.Substring(2, 2), 16);
                byte b = Convert.ToByte(h.Substring(4, 2), 16);
                return (r / 255.0, g / 255.0, b / 255.0);
            }
        }
        catch { }
        return (1.0, 1.0, 0.0);
    }
}
