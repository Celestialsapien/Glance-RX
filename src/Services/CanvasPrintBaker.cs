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
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace GlanceRX.Services;

/// <summary>把一页的画布内容烘焙进页位图。绘制只用页局部 PDF 点，位图边界即 PDF 页框：
/// 越出页框的卡片与连线由位图边界裁掉，不参与任何缩放，PDF 主体比例保持不变。</summary>
internal static class CanvasPrintBaker
{
    private const float CardRadius = 8f;
    private const float CardPadX = 10f;
    private const float CardPadY = 0f;
    private const float CardFontSize = 12f;
    private const float LineHeight = CardFontSize * 1.35f;
    private const float CardStrokeWidth = 1.0f;
    private const float EdgeStrokeWidth = 1.6f;
    private const float ArrowLen = 9f;
    private const float BackdropAscent = 0.92f;
    private const float BackdropDescent = 0.25f;
    private const byte CardTintAlpha = 26;
    private const string CardFontFamily = "Segoe UI";
    private const string CodeFontFamily = "Consolas";
    private const string DefaultGrayHex = "#8A8F98";

    private static readonly Color CardFill = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color CardStroke = Color.FromArgb(255, 138, 143, 152);
    private static readonly Color CardTextColor = Color.FromArgb(255, 32, 32, 34);
    private static readonly Color CodeBackdrop = Color.FromArgb(255, 236, 236, 241);
    private static readonly Color HighlightBackdrop = Color.FromArgb(255, 255, 236, 140);
    private static readonly Color EdgeColor = Color.FromArgb(255, 96, 102, 112);
    private static readonly Color InkDefault = Color.FromArgb(255, 242, 194, 0);
    private static readonly FontWeight BoldWeight = new() { Weight = 700 };

    public static byte[] Bake(byte[] pixels, int w, int h, PdfPageViewModel page,
        IReadOnlyList<PdfPageViewModel> pages, CanvasDoc? canvas, IReadOnlyList<Rect>? layouts)
    {
        if (w <= 0 || h <= 0) return pixels;
        int pageIndex = page.PageIndex;

        var device = CanvasDevice.GetSharedDevice();
        float dpi = (float)(w / Math.Max(page.PageWidth, 1.0) * 72.0);
        using var target = new CanvasRenderTarget(device, w, h, dpi);
        target.SetPixelBytes(pixels);

        using (var ds = target.CreateDrawingSession())
        {
            float s = dpi / 72f;
            var old = ds.Transform;
            var m = Matrix3x2.CreateScale(s);
            int rot = ((int)Math.Round(page.RotationAngle)) % 360;
            if (rot != 0)
                m *= Matrix3x2.CreateRotation((float)(rot * Math.PI / 180.0),
                    new Vector2(w / 2f, h / 2f));
            ds.Transform = m * old;

            DrawInk(ds, target, pages, layouts, page, pageIndex);
            if (canvas != null)
            {
                DrawEdges(ds, target, canvas, layouts, pageIndex);
                DrawCards(ds, target, canvas, pageIndex);
            }

            ds.Transform = old;
        }
        return target.GetPixelBytes();
    }

    private static void DrawInk(CanvasDrawingSession ds, ICanvasResourceCreator creator,
        IReadOnlyList<PdfPageViewModel> pages, IReadOnlyList<Rect>? layouts,
        PdfPageViewModel targetPage, int pageIndex)
    {
        var style = new CanvasStrokeStyle
        {
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
            LineJoin = CanvasLineJoin.Round,
        };
        double pageWpt = Math.Max(targetPage.PageWidth, 1.0);
        double pageHpt = Math.Max(targetPage.PageHeight, 1.0);

        for (int p = 0; p < pages.Count; p++)
        {
            // 发起页的笔迹由 BakeAnnotations 画；这里只补从别的页溢出到本页的部分。
            if (p == pageIndex) continue;
            var annos = pages[p].Annotations;
            if (annos == null || annos.Count == 0) continue;
            if (layouts == null || p >= layouts.Count || pageIndex >= layouts.Count) continue;

            double shiftX = (layouts[p].X - layouts[pageIndex].X) * 72.0 / 96.0;
            double shiftY = (layouts[p].Y - layouts[pageIndex].Y) * 72.0 / 96.0;

            foreach (var a in annos)
            {
                if (a.Type != AnnotationType.Pen) continue;
                var pts = a.PointsCollection;
                if (pts == null || pts.Count == 0) continue;

                double minX = double.MaxValue, maxX = double.MinValue;
                double minY = double.MaxValue, maxY = double.MinValue;
                for (int i = 0; i < pts.Count; i++)
                {
                    double x = pts[i].X + shiftX, y = pts[i].Y + shiftY;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
                if (maxX < 0 || minX > pageWpt || maxY < 0 || minY > pageHpt) continue;

                var color = ParseColor(a.ColorHex) ?? InkDefault;
                float thick = (float)Math.Max(a.Thickness, 0.5);
                if (pts.Count == 1)
                {
                    ds.FillCircle((float)(pts[0].X + shiftX), (float)(pts[0].Y + shiftY),
                        thick / 2f, color);
                    continue;
                }
                using var path = new CanvasPathBuilder(creator);
                path.BeginFigure((float)(pts[0].X + shiftX), (float)(pts[0].Y + shiftY));
                for (int i = 1; i < pts.Count; i++)
                    path.AddLine((float)(pts[i].X + shiftX), (float)(pts[i].Y + shiftY));
                path.EndFigure(CanvasFigureLoop.Open);
                using var geometry = CanvasGeometry.CreatePath(path);
                ds.DrawGeometry(geometry, color, thick, style);
            }
        }
    }

    private static void DrawEdges(CanvasDrawingSession ds, ICanvasResourceCreator creator,
        CanvasDoc canvas, IReadOnlyList<Rect>? layouts, int pageIndex)
    {
        var index = new Dictionary<string, CanvasNode>(StringComparer.Ordinal);
        foreach (var node in canvas.Nodes)
            if (!string.IsNullOrEmpty(node.Id)) index[node.Id] = node;

        int layoutCount = layouts?.Count ?? 0;
        bool useDoc = layouts != null && pageIndex >= 0 && pageIndex < layoutCount;
        double scale = useDoc ? 96.0 / 72.0 : 1.0;
        double inv = useDoc ? 72.0 / 96.0 : 1.0;
        double kx = useDoc ? layouts![pageIndex].X : 0;
        double ky = useDoc ? layouts![pageIndex].Y : 0;

        foreach (var edge in canvas.Edges)
        {
            if (!index.TryGetValue(edge.FromNode, out var from)) continue;
            if (!index.TryGetValue(edge.ToNode, out var to)) continue;
            if (ReferenceEquals(from, to)) continue;
            bool fromOk = from.GlancePage >= 0 && from.GlancePage < layoutCount;
            bool toOk = to.GlancePage >= 0 && to.GlancePage < layoutCount;
            if (useDoc && (!fromOk || !toOk)) continue;
            if (!useDoc && (from.GlancePage != pageIndex || to.GlancePage != pageIndex)) continue;

            double fx = useDoc ? layouts![from.GlancePage].X : 0;
            double fy = useDoc ? layouts![from.GlancePage].Y : 0;
            double tx = useDoc ? layouts![to.GlancePage].X : 0;
            double ty = useDoc ? layouts![to.GlancePage].Y : 0;

            double fcx = fx + (from.X + from.Width / 2.0) * scale;
            double fcy = fy + (from.Y + from.Height / 2.0) * scale;
            double tcx = tx + (to.X + to.Width / 2.0) * scale;
            double tcy = ty + (to.Y + to.Height / 2.0) * scale;
            string fSide = edge.FromSide ?? AutoSide(fcx, fcy, tcx, tcy, true);
            string tSide = edge.ToSide ?? AutoSide(fcx, fcy, tcx, tcy, false);

            Vector2 sa = new Vector2((float)(fx + SideAnchor(from, fSide).X * scale),
                (float)(fy + SideAnchor(from, fSide).Y * scale));
            Vector2 sb = new Vector2((float)(tx + SideAnchor(to, tSide).X * scale),
                (float)(ty + SideAnchor(to, tSide).Y * scale));
            Vector2 n1 = SideNormal(fSide), n2 = SideNormal(tSide);
            float k = Math.Clamp(Vector2.Distance(sa, sb) * 0.4f, 24f, 120f);
            Vector2 sc1 = sa + n1 * k, sc2 = sb + n2 * k;

            bool toArrow = IsArrowEnd(edge.ToEnd);
            bool fromArrow = IsArrowEnd(edge.FromEnd);
            Vector2 sp0 = sa, sp3 = sb;
            if (toArrow)
            {
                var d = sb - sc2;
                if (d.Length() > 1e-3f) sp3 = sb - d / d.Length() * ArrowLen;
            }
            if (fromArrow)
            {
                var d = sa - sc1;
                if (d.Length() > 1e-3f) sp0 = sa - d / d.Length() * ArrowLen;
            }

            float seam = MathF.Max(1.5f, 0.15f * ArrowLen);
            Vector2 sl0 = sp0, sl3 = sp3;
            if (fromArrow)
            {
                var d = sa - sc1;
                if (d.Length() > 1e-3f) sl0 = sp0 + d / d.Length() * seam;
            }
            if (toArrow)
            {
                var d = sb - sc2;
                if (d.Length() > 1e-3f) sl3 = sp3 + d / d.Length() * seam;
            }

            Color color = ParseColor(edge.Color) ?? EdgeColor;
            using var path = new CanvasPathBuilder(creator);
            path.BeginFigure(ToPageLocal(sl0, kx, ky, inv));
            path.AddCubicBezier(ToPageLocal(sc1, kx, ky, inv), ToPageLocal(sc2, kx, ky, inv),
                ToPageLocal(sl3, kx, ky, inv));
            path.EndFigure(CanvasFigureLoop.Open);
            using var geometry = CanvasGeometry.CreatePath(path);
            ds.DrawGeometry(geometry, color, (float)(EdgeStrokeWidth * inv));

            if (toArrow) DrawArrow(ds, creator, ToPageLocal(sp3, kx, ky, inv),
                ToPageLocal(sb, kx, ky, inv), color);
            if (fromArrow) DrawArrow(ds, creator, ToPageLocal(sp0, kx, ky, inv),
                ToPageLocal(sa, kx, ky, inv), color);
        }
    }

    private static Vector2 ToPageLocal(Vector2 p, double originX, double originY, double inv)
        => new((float)((p.X - originX) * inv), (float)((p.Y - originY) * inv));

    private static void DrawCards(CanvasDrawingSession ds, ICanvasResourceCreator creator,
        CanvasDoc canvas, int pageIndex)
    {
        foreach (var node in canvas.Nodes)
        {
            if (node.GlancePage != pageIndex) continue;

            double w = Math.Max(12, node.Width);
            double h = Math.Max(12, node.Height);
            var rect = new Rect(node.X, node.Y, w, h);
            ds.FillRoundedRectangle(rect, CardRadius, CardRadius, CardFill);

            bool customColor = !string.IsNullOrEmpty(node.Color)
                && !string.Equals(node.Color, DefaultGrayHex, StringComparison.OrdinalIgnoreCase);
            var stroke = CardStroke;
            if (customColor)
            {
                stroke = ParseColor(node.Color) ?? CardStroke;
                ds.FillRoundedRectangle(rect, CardRadius, CardRadius,
                    Color.FromArgb(CardTintAlpha, stroke.R, stroke.G, stroke.B));
            }

            double tw = w - 2 * CardPadX;
            double th = h - 2 * CardPadY;
            if (!string.IsNullOrEmpty(node.Text) && tw > 0 && th > 0)
            {
                var md = CanvasMarkdown.Parse(node.Text);
                var format = CreateTextFormat(creator);
                using var layout = new CanvasTextLayout(creator, md.DisplayText, format,
                    (float)tw, (float)th);
                ApplyFormatting(layout, md);

                double realContentH = RealLineCount(creator, format, md.DisplayText, tw) * LineHeight;
                double topPad = Math.Min(0.5 * LineHeight,
                    Math.Max(0, (th - Math.Min(th, realContentH)) / 2.0));
                var textRect = new Rect(node.X + CardPadX, node.Y + CardPadY, tw, th);
                using (ds.CreateLayer(1f, textRect))
                {
                    DrawBackdrops(ds, layout, md, textRect, topPad);
                    ds.DrawTextLayout(layout,
                        new Vector2((float)textRect.X, (float)(textRect.Y + topPad)), CardTextColor);
                }
            }

            ds.DrawRoundedRectangle(rect, CardRadius, CardRadius, stroke, CardStrokeWidth);
        }
    }

    private static CanvasTextFormat CreateTextFormat(ICanvasResourceCreator creator)
    {
        var probeFormat = new CanvasTextFormat
        {
            FontFamily = CardFontFamily,
            FontSize = CardFontSize,
            WordWrapping = CanvasWordWrapping.Wrap,
        };
        float baseline = 0;
        try
        {
            using var probe = new CanvasTextLayout(creator, "Ag", probeFormat, 64f, 64f);
            var lm = probe.LineMetrics;
            if (lm is { Length: > 0 }) baseline = lm[0].Baseline;
        }
        catch { }
        if (!(baseline > 0)) baseline = CardFontSize * 1.079f;
        if (baseline >= LineHeight) baseline = LineHeight * 0.9f;

        var format = new CanvasTextFormat
        {
            FontFamily = CardFontFamily,
            FontSize = CardFontSize,
            WordWrapping = CanvasWordWrapping.Wrap,
        };
        format.LineSpacing = LineHeight;
        format.LineSpacingBaseline = baseline;
        format.LineSpacingMode = CanvasLineSpacingMode.Uniform;
        return format;
    }

    private static int RealLineCount(ICanvasResourceCreator creator, CanvasTextFormat format,
        string text, double maxW)
    {
        try
        {
            using var full = new CanvasTextLayout(creator, text, format, (float)maxW, 1e5f);
            return Math.Max(1, full.LineCount);
        }
        catch
        {
            return 1;
        }
    }

    private static void ApplyFormatting(CanvasTextLayout layout, CanvasMarkdownResult md)
    {
        int length = md.DisplayText.Length;
        foreach (var span in md.Spans)
        {
            int start = Math.Clamp(span.Start, 0, length);
            int count = Math.Clamp(span.Length, 0, length - start);
            if (count <= 0) continue;
            switch (span.Kind)
            {
                case CanvasMarkdownSpanKind.Bold:
                    layout.SetFontWeight(start, count, BoldWeight);
                    break;
                case CanvasMarkdownSpanKind.Italic:
                    layout.SetFontStyle(start, count, FontStyle.Italic);
                    break;
                case CanvasMarkdownSpanKind.Strike:
                    layout.SetStrikethrough(start, count, true);
                    break;
                case CanvasMarkdownSpanKind.Code:
                    layout.SetFontFamily(start, count, CodeFontFamily);
                    break;
            }
        }
    }

    private static void DrawBackdrops(CanvasDrawingSession ds, CanvasTextLayout layout,
        CanvasMarkdownResult md, Rect textRect, double topPad)
    {
        var lms = layout.LineMetrics;
        float[]? tops = null, baselines = null;
        if (lms is { Length: > 0 })
        {
            tops = new float[lms.Length];
            baselines = new float[lms.Length];
            float top = 0;
            for (int i = 0; i < lms.Length; i++)
            {
                tops[i] = top;
                baselines[i] = lms[i].Baseline;
                top += lms[i].Height > 0 ? lms[i].Height : LineHeight;
            }
        }

        int length = md.DisplayText.Length;
        foreach (var span in md.Spans)
        {
            if (span.Kind != CanvasMarkdownSpanKind.Code
                && span.Kind != CanvasMarkdownSpanKind.Highlight) continue;
            int start = Math.Clamp(span.Start, 0, length);
            int count = Math.Clamp(span.Length, 0, length - start);
            if (count <= 0) continue;

            foreach (var region in layout.GetCharacterRegions(start, count))
            {
                var bounds = region.LayoutBounds;
                if (bounds.Width <= 0 || bounds.Height <= 0) continue;

                double y = bounds.Y;
                double h = bounds.Height;
                if (tops != null && baselines != null)
                {
                    float mid = (float)(bounds.Y + bounds.Height * 0.5);
                    int line = -1;
                    for (int i = 0; i < tops.Length; i++)
                    {
                        float bottom = i + 1 < tops.Length ? tops[i + 1] : float.MaxValue;
                        if (mid >= tops[i] && mid < bottom) { line = i; break; }
                    }
                    if (line < 0) line = tops.Length - 1;
                    float baseline = tops[line] + baselines[line];
                    if (baseline > tops[line])
                    {
                        y = baseline - BackdropAscent * CardFontSize;
                        h = (BackdropAscent + BackdropDescent) * CardFontSize;
                    }
                }

                var rect = new Rect(bounds.X + textRect.X, y + textRect.Y + topPad,
                    bounds.Width, h);
                if (span.Kind == CanvasMarkdownSpanKind.Code)
                {
                    rect = new Rect(rect.X, rect.Y - 0.5, rect.Width, rect.Height + 1.0);
                    ds.FillRoundedRectangle(rect, 2f, 2f, CodeBackdrop);
                }
                else
                {
                    ds.FillRectangle(rect, HighlightBackdrop);
                }
            }
        }
    }

    private static Vector2 SideAnchor(CanvasNode n, string side) => side switch
    {
        "top" => new Vector2((float)(n.X + n.Width / 2), (float)n.Y),
        "bottom" => new Vector2((float)(n.X + n.Width / 2), (float)(n.Y + n.Height)),
        "left" => new Vector2((float)n.X, (float)(n.Y + n.Height / 2)),
        _ => new Vector2((float)(n.X + n.Width), (float)(n.Y + n.Height / 2)),
    };

    private static Vector2 SideNormal(string side) => side switch
    {
        "top" => new Vector2(0, -1),
        "bottom" => new Vector2(0, 1),
        "left" => new Vector2(-1, 0),
        _ => new Vector2(1, 0),
    };

    private static string AutoSide(double fx, double fy, double tx, double ty, bool from)
    {
        double dx = tx - fx, dy = ty - fy;
        bool horizontal = Math.Abs(dx) >= Math.Abs(dy);
        if (from)
            return horizontal ? (dx >= 0 ? "right" : "left") : (dy >= 0 ? "bottom" : "top");
        return horizontal ? (dx >= 0 ? "left" : "right") : (dy >= 0 ? "top" : "bottom");
    }

    private static void DrawArrow(CanvasDrawingSession ds, ICanvasResourceCreator creator,
        Vector2 baseC, Vector2 tip, Color color)
    {
        var dir = tip - baseC;
        float len = dir.Length();
        if (len < 1e-3f) return;
        dir /= len;
        var perp = new Vector2(-dir.Y, dir.X);
        float half = len / MathF.Sqrt(3f);
        using var path = new CanvasPathBuilder(creator);
        path.BeginFigure(tip);
        path.AddLine(baseC + perp * half);
        path.AddLine(baseC - perp * half);
        path.EndFigure(CanvasFigureLoop.Closed);
        using var geometry = CanvasGeometry.CreatePath(path);
        ds.FillGeometry(geometry, color);
    }

    private static bool IsArrowEnd(string? end) =>
        !string.IsNullOrEmpty(end) && !string.Equals(end, "none", StringComparison.OrdinalIgnoreCase);

    private static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        string s = hex.TrimStart('#');
        try
        {
            if (s.Length == 6)
                return Color.FromArgb(255,
                    Convert.ToByte(s.Substring(0, 2), 16),
                    Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16));
            if (s.Length == 8)
                return Color.FromArgb(
                    Convert.ToByte(s.Substring(0, 2), 16),
                    Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16),
                    Convert.ToByte(s.Substring(6, 2), 16));
        }
        catch { }
        return null;
    }
}
