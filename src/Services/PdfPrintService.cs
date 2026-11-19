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
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Foundation;
using Windows.Graphics.Printing;
using Windows.Graphics.Printing.OptionDetails;
using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;

namespace GlanceRX.Services;

/// WPS-style printing for GlanceRX.
/// a single physical sheet. The system NUp option cannot be read back by the app
/// GetResult() — this avoids the await-on-UI-thread deadlock, because the
public sealed class PdfPrintService : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly PdfRenderService _render;
    private readonly IReadOnlyList<PdfPageViewModel> _pages;
    private readonly CanvasDoc? _canvasDoc;
    private readonly IReadOnlyList<Rect> _pageLayouts;
    private readonly string _documentTitle;
    private readonly int _currentPage;
    private readonly bool _annotationsVisible;

    private PrintManager? _printManager;
    private PrintDocument? _printDocument;
    private TaskCompletionSource<bool>? _tcs;
    private bool _printTaskRequested;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

    // Resolved list of 0-based document page indices to print, cached from the
    private List<uint>? _pagesToPrint;

    private int _nUp = 1;
    private string _printScaleMode = "fit"; // "fit" | "fill" | "50".."200" | "custom"
    private int _customScalePercent = 100;
    private string _pageRangeMode = "all";
    private string _customPageRange = "1";
    private string _colorMode = "color";      // "color" | "grayscale" | "monochrome"
    private PrintPageDescription? _pageDescription;

    private const double DefaultDpi = 300.0;
    private const double PreviewDpi = 96.0;
    private const int MaxLongEdge = 4000;

    // A4 @ 96 DPI, used as the preview's physical page size when the print
    private const double DefaultPageWidthDip = 793.7;
    private const double DefaultPageHeightDip = 1122.5;

    private static readonly CanvasStrokeStyle PenStrokeStyle = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round,
    };

    public PdfPrintService(IntPtr hwnd, PdfRenderService render, IReadOnlyList<PdfPageViewModel> pages,
        CanvasDoc? canvas, IReadOnlyList<Rect> pageLayouts, string documentTitle, bool annotationsVisible,
        int currentPage = 0)
    {
        _hwnd = hwnd;
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        _canvasDoc = canvas;
        _pageLayouts = pageLayouts;
        _documentTitle = documentTitle;
        _annotationsVisible = annotationsVisible;
        _currentPage = currentPage;
    }

    /// <summary>是否已具备打印前提：系统支持打印 API 且注册表里至少有一个打印目标（虚拟打印机也算）。
    /// 供调用方在打开任何系统窗口之前做唯一一次前置判定。</summary>
    public static bool CanPrint() => PrintManager.IsSupported() && HasAnyPrinter();

    /// <summary>打开系统打印预览并等待打印任务结束。调用方须先用 CanPrint() 完成前置判定。
    /// 系统预览自身的加载与成败属于系统行为：这里不向调用方回传可被误读为"无打印机"的结果，
    /// 也不弹窗，避免出现"系统预览窗口 + 应用提示框"并存的双窗口。</summary>
    public async Task ShowPrintDialogAsync()
    {
        if (!CanPrint())
            return;

        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _printManager = PrintManagerInterop.GetForWindow(_hwnd);
        _tcs = new TaskCompletionSource<bool>();
        _printTaskRequested = false;

        try
        {
            _printManager.PrintTaskRequested += OnPrintTaskRequested;
            try
            {
                EnsurePrintDocument();
                await PrintManagerInterop.ShowPrintUIForWindowAsync(_hwnd);
                // 等打印任务结束再返回：调用方在 finally 释放渲染服务，提前返回会把打印页渲染截断。
                if (_printTaskRequested)
                    await _tcs.Task;
            }
            catch (Exception)
            {
                // 系统打印 UI 的加载与失败由其自身呈现；此处不弹窗，也不向上传播。
            }
        }
        finally
        {
            _printManager.PrintTaskRequested -= OnPrintTaskRequested;
            _printDocument = null;
        }
    }

    /// ⚠ PrintManager.IsSupported() 不表示已安装打印机。此方法读取后台打印程序的
    /// 走「未找到打印机」提示分支，避免进入 PrintTaskRequested 后在 deferral.Complete() 抛 COMException 崩溃。
    private static bool HasAnyPrinter()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Print\Printers");
            return key != null && key.SubKeyCount > 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Create the PrintDocument and wire its events on the UI thread. Must be called before the print subsystem requests the source.</summary>
    private void EnsurePrintDocument()
    {
        if (_printDocument == null)
        {
            _printDocument = new PrintDocument();
            _printDocument.Paginate += OnPaginate;
            _printDocument.GetPreviewPage += OnGetPreviewPage;
            _printDocument.AddPages += OnAddPages;
        }
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        // 仅当 PrintTask 真正创建成功才完成 deferral（见 finally 注释）。
        bool taskCreated = false;
        try
        {
            _printTaskRequested = true;
            string taskTitle = string.IsNullOrWhiteSpace(_documentTitle) ? "Glance-RX" : _documentTitle;
            var task = args.Request.CreatePrintTask(taskTitle, OnPrintTaskSourceRequested);
            taskCreated = true;
            task.Completed += OnPrintTaskCompleted;

            // Initial color mode (read from the system Color option so the first
            try { _colorMode = NormalizeColorMode(task.Options.ColorMode.ToString()); }
            catch { _colorMode = "color"; }

            _nUp = 1;
            _printScaleMode = "fit";
            _customScalePercent = 100;
            _pageRangeMode = "all";
            _customPageRange = "1";

            // Add Glance's own composed options to the print panel. These drive the
            var details = PrintTaskOptionDetails.GetFromPrintTaskOptions(task.Options);
            details.OptionChanged += OnPrintOptionChanged;

            var pages = details.CreateItemListOption("GlancePages", LocalizationService.Get("PrintPages"));
            pages.AddItem("all", LocalizationService.Get("PrintPagesAll"));
            pages.AddItem("current", LocalizationService.Get("PrintPagesCurrent"));
            pages.AddItem("custom", LocalizationService.Get("PrintPagesCustom"));
            pages.State = PrintOptionStates.Enabled;
            details.DisplayedOptions.Add("GlancePages");

            // Custom page range — not shown initially. It is dynamically inserted right
            var range = details.CreateTextOption("GlancePageRange", LocalizationService.Get("PrintPageRange"));
            range.TrySetValue("1");
            range.State = PrintOptionStates.Enabled;

            var nup = details.CreateItemListOption("GlanceNUp", LocalizationService.Get("PrintNUp"));
            nup.AddItem("1", "1");
            nup.AddItem("2", "2");
            nup.AddItem("4", "4");
            nup.AddItem("6", "6");
            nup.AddItem("9", "9");
            nup.AddItem("16", "16");
            nup.State = PrintOptionStates.Enabled;
            details.DisplayedOptions.Add("GlanceNUp");

            var scale = details.CreateItemListOption("GlancePrintScale", LocalizationService.Get("PrintScale"));
            scale.AddItem("fit", LocalizationService.Get("PrintScaleFit"));
            scale.AddItem("fill", LocalizationService.Get("PrintScaleFill"));
            scale.AddItem("100", LocalizationService.Get("PrintScaleActual"));
            scale.AddItem("50", "50%");
            scale.AddItem("75", "75%");
            scale.AddItem("125", "125%");
            scale.AddItem("150", "150%");
            scale.AddItem("200", "200%");
            scale.AddItem("custom", LocalizationService.Get("PrintScaleCustom"));
            // Keep the panel's selected value in sync with our internal default so the
            scale.TrySetValue("fit");
            scale.State = PrintOptionStates.Enabled;
            details.DisplayedOptions.Add("GlancePrintScale");

            // Custom scale percentage — not shown initially. It is dynamically inserted
            var custom = details.CreateTextOption("GlanceCustomScale", LocalizationService.Get("PrintScaleCustomLabel"));
            custom.TrySetValue("100");
            custom.State = PrintOptionStates.Enabled;

            // Duplex (single/double-sided) — driven by the system option, applied by
            details.DisplayedOptions.Add(StandardPrintTaskOptions.Duplex);
        }
        catch
        {
            // 仅当任务未创建成功时才把结果置 false（任务已创建则交由 OnPrintTaskCompleted 判定成败，
            if (!taskCreated)
                _tcs?.TrySetResult(false);
        }
        finally
        {
            // ⚠ 仅当 PrintTask 创建成功才完成 deferral。无打印机 / 创建失败时
            // Complete() 会抛 COMException 使进程崩溃。任务已创建则正常完成（即使后续配置抛错也不影响）。
            if (taskCreated)
                deferral.Complete();
        }
    }

    private void OnPrintOptionChanged(PrintTaskOptionDetails sender, PrintTaskOptionChangedEventArgs args)
    {
        // The handler may be raised off the UI thread; preview invalidation must run there.
        if (_dispatcherQueue == null || _dispatcherQueue.HasThreadAccess)
            HandleOptionChanged(sender, args);
        else
            _dispatcherQueue.TryEnqueue(() => HandleOptionChanged(sender, args));
    }

    private void HandleOptionChanged(PrintTaskOptionDetails sender, PrintTaskOptionChangedEventArgs args)
    {
        try
        {
            var id = args.OptionId as string;
            if (id == "GlancePages")
            {
                var raw = sender.Options["GlancePages"]?.Value as string;
                if (!string.IsNullOrEmpty(raw) && raw != _pageRangeMode)
                {
                    _pageRangeMode = raw;
                    if (_pageRangeMode == "custom")
                        ShowOptionAfter(sender, "GlancePageRange", "GlancePages");
                    else
                        sender.DisplayedOptions.Remove("GlancePageRange");
                    _printDocument?.InvalidatePreview();
                }
            }
            else if (id == "GlancePageRange")
            {
                if (_pageRangeMode == "custom" &&
                    sender.Options.TryGetValue("GlancePageRange", out var rangeObj) &&
                    rangeObj is PrintCustomTextOptionDetails range)
                {
                    _customPageRange = range.Value as string ?? "1";
                    _printDocument?.InvalidatePreview();
                }
            }
            else if (id == "GlanceNUp")
            {
                var raw = sender.Options["GlanceNUp"]?.Value as string;
                if (int.TryParse(raw, out int n) && n > 0 && n != _nUp)
                {
                    _nUp = n;
                    _printDocument?.InvalidatePreview();
                }
            }
            else if (id == "GlancePrintScale")
            {
                var raw = sender.Options["GlancePrintScale"]?.Value as string;
                if (!string.IsNullOrEmpty(raw) && raw != _printScaleMode)
                {
                    _printScaleMode = raw;
                    if (_printScaleMode == "custom")
                        ShowOptionAfter(sender, "GlanceCustomScale", "GlancePrintScale");
                    else
                        sender.DisplayedOptions.Remove("GlanceCustomScale");
                    _printDocument?.InvalidatePreview();
                }
            }
            else if (id == "GlanceCustomScale")
            {
                if (sender.Options.TryGetValue("GlanceCustomScale", out var customObj) &&
                    customObj is PrintCustomTextOptionDetails custom &&
                    int.TryParse(custom.Value as string, out int pct))
                {
                    pct = Math.Clamp(pct, 10, 500);
                    if (pct != _customScalePercent)
                    {
                        _customScalePercent = pct;
                        _printDocument?.InvalidatePreview();
                    }
                }
            }
            else if (id == StandardPrintTaskOptions.ColorMode)
            {
                // Color mode affects the rendered page, so the preview must follow.
                var value = sender.Options[StandardPrintTaskOptions.ColorMode]?.Value;
                var mode = NormalizeColorMode(value?.ToString() ?? "color");
                if (mode != _colorMode)
                {
                    _colorMode = mode;
                    _printDocument?.InvalidatePreview();
                }
            }
        }
        catch { }
    }

    private void OnPrintTaskSourceRequested(PrintTaskSourceRequestedArgs args)
    {
        var deferral = args.GetDeferral();

        void SetSource()
        {
            try
            {
                EnsurePrintDocument();
                if (_printDocument == null)
                    return;
                args.SetSource(_printDocument.DocumentSource);
            }
            catch { }
            finally
            {
                deferral.Complete();
            }
        }

        // The callback may run on a non-UI thread, but DocumentSource must be accessed
        if (_dispatcherQueue == null || _dispatcherQueue.HasThreadAccess)
            SetSource();
        else
            _dispatcherQueue.TryEnqueue(() => SetSource());
    }

    private void OnPaginate(object sender, PaginateEventArgs e)
    {
        try
        {
            var options = (PrintTaskOptions)e.PrintTaskOptions;
            _pagesToPrint = GetPagesToPrint();
            try { _pageDescription = options.GetPageDescription(1); }
            catch { _pageDescription = null; }

            int logical = _pagesToPrint.Count;
            int physical = (_nUp > 1) ? (int)Math.Ceiling((double)logical / _nUp) : logical;
            if (physical < 1) physical = 1;
            ((PrintDocument)sender).SetPreviewPageCount(physical, PreviewPageCountType.Final);
        }
        catch { }
    }

    private void OnGetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        var doc = (PrintDocument)sender;
        try
        {
            var pages = _pagesToPrint ?? GetPagesToPrint();
            int sheetIndex = (e.PageNumber >= 1) ? e.PageNumber - 1 : 0;
            doc.SetPreviewPage(e.PageNumber, CreateSheetVisual(sheetIndex, PreviewDpi));
        }
        catch (Exception)
        {
            doc.SetPreviewPage(e.PageNumber, CreateSheetVisual(0, PreviewDpi));
        }
    }

    private void OnAddPages(object sender, AddPagesEventArgs e)
    {
        var doc = (PrintDocument)sender;
        var options = (PrintTaskOptions)e.PrintTaskOptions;
        try
        {
            _pagesToPrint = GetPagesToPrint();
            try { _pageDescription = options.GetPageDescription(1); }
            catch { _pageDescription = null; }

            int logical = _pagesToPrint.Count;
            int physical = (_nUp > 1) ? (int)Math.Ceiling((double)logical / _nUp) : logical;
            if (physical < 1) physical = 1;

            for (int i = 0; i < physical; i++)
            {
                doc.AddPage(CreateSheetVisual(i, DefaultDpi));
            }
            doc.AddPagesComplete();
        }
        catch (Exception)
        {
            doc.AddPagesComplete();
        }
    }

    private void OnPrintTaskCompleted(PrintTask sender, PrintTaskCompletedEventArgs e)
    {
        _tcs?.TrySetResult(e.Completion != PrintTaskCompletion.Failed);
    }

    /// <summary>Resolve the Glance page-range selection into 0-based document page indices, clamped to the document. "All" → every page;</summary>
    private List<uint> GetPagesToPrint()
    {
        var result = new List<uint>();
        if (_pageRangeMode == "current")
        {
            if (_currentPage >= 0 && _currentPage < _pages.Count)
                result.Add((uint)_currentPage);
        }
        else if (_pageRangeMode == "custom")
        {
            result = ParsePageRange(_customPageRange);
        }
        else
        {
            for (uint i = 0; i < _pages.Count; i++) result.Add(i);
        }

        if (result.Count == 0)
        {
            for (uint i = 0; i < _pages.Count; i++) result.Add(i);
        }
        return result;
    }

    private List<uint> ParsePageRange(string text)
    {
        var result = new List<uint>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var parts = text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in parts)
        {
            var seg = raw.Trim();
            if (seg.Length == 0) continue;
            if (seg.Contains("-"))
            {
                var ends = seg.Split('-');
                if (ends.Length == 2 &&
                    int.TryParse(ends[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int a) &&
                    int.TryParse(ends[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int b))
                {
                    int lo = Math.Min(a, b), hi = Math.Max(a, b);
                    for (int p = lo; p <= hi; p++)
                    {
                        if (p < 1 || p > _pages.Count) continue;
                        uint idx = (uint)(p - 1);
                        if (!result.Contains(idx)) result.Add(idx);
                    }
                }
            }
            else if (int.TryParse(seg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
            {
                if (p < 1 || p > _pages.Count) continue;
                uint idx = (uint)(p - 1);
                if (!result.Contains(idx)) result.Add(idx);
            }
        }
        return result;
    }

    /// <summary>Compose one physical sheet. With N-Up &lt;= 1 the sheet holds a single logical page;</summary>
    private UIElement CreateSheetVisual(int sheetIndex, double dpi)
    {
        double paperW, paperH;
        if (_pageDescription.HasValue)
        {
            var ps = _pageDescription.Value.PageSize;
            paperW = ps.Width;
            paperH = ps.Height;
        }
        else
        {
            paperW = DefaultPageWidthDip;
            paperH = DefaultPageHeightDip;
        }

        var pages = _pagesToPrint ?? GetPagesToPrint();
        int logicalCount = pages.Count;
        int start = sheetIndex * _nUp;
        if (start >= logicalCount) start = Math.Max(0, logicalCount - 1);
        int count = (_nUp > 1) ? Math.Min(_nUp, logicalCount - start) : 1;
        if (count <= 0) count = 1;

        var sheet = new Grid
        {
            Width = paperW,
            Height = paperH,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
        };

        if (_nUp <= 1)
        {
            sheet.Children.Add(MakePageImage(pages[start], paperW, paperH, dpi));
            return sheet;
        }

        int cols = (int)Math.Ceiling(Math.Sqrt(_nUp));
        int rows = (int)Math.Ceiling((double)_nUp / cols);
        double cellW = paperW / cols;
        double cellH = paperH / rows;

        for (int k = 0; k < count; k++)
        {
            int pageIndex = start + k;
            int c = k % cols;
            int r = k / cols;
            var cell = new Grid
            {
                Width = cellW,
                Height = cellH,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(c * cellW, r * cellH, 0, 0),
            };
            cell.Children.Add(MakePageImage(pages[pageIndex], cellW, cellH, dpi));
            sheet.Children.Add(cell);
        }
        return sheet;
    }

    /// Render one logical page into a cell-sized element, honoring the active scaling mode (fit / fill / pe…
    private UIElement MakePageImage(uint pageIndex, double cellW, double cellH, double dpi)
    {
        var page = _pages[(int)pageIndex];
        double pageWpt = page.PageWidth > 0 ? page.PageWidth : 612.0;
        double pageHpt = page.PageHeight > 0 ? page.PageHeight : 792.0;
        double pageDipW = pageWpt * 96.0 / 72.0;
        double pageDipH = pageHpt * 96.0 / 72.0;

        // Compute the displayed size (DIP) of the page within the cell.
        double dispW, dispH;
        bool fill = _printScaleMode == "fill";
        double cellAspect = cellW / cellH;
        double pageAspect = pageDipW / pageDipH;
        if (fill)
        {
            if (pageAspect > cellAspect) { dispH = cellH; dispW = cellH * pageAspect; }
            else { dispW = cellW; dispH = cellW / pageAspect; }
        }
        else if (_printScaleMode == "fit")
        {
            if (pageAspect > cellAspect) { dispW = cellW; dispH = cellW / pageAspect; }
            else { dispH = cellH; dispW = cellH * pageAspect; }
        }
        else
        {
            double pct = 100.0;
            if (_printScaleMode == "custom") pct = _customScalePercent;
            else if (double.TryParse(_printScaleMode, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) pct = v;
            pct = Math.Clamp(pct, 1, 100000);
            dispW = pageDipW * pct / 100.0;
            dispH = pageDipH * pct / 100.0;
        }

        // Render at exactly the displayed DIP size (capped) so the Image can show it
        double scale = dpi / 96.0;
        uint pw = (uint)Math.Max(1, Math.Round(dispW * scale));
        uint ph = (uint)Math.Max(1, Math.Round(dispH * scale));
        double longest = Math.Max(pw, ph);
        if (longest > MaxLongEdge)
        {
            double s = MaxLongEdge / longest;
            pw = (uint)Math.Max(1, Math.Round(pw * s));
            ph = (uint)Math.Max(1, Math.Round(ph * s));
        }

        var wb = RenderPageBitmap(pageIndex, pw, ph);

        var img = new Image
        {
            Source = wb,
            Width = dispW,
            Height = dispH,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var host = new Grid
        {
            Width = cellW,
            Height = cellH,
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, cellW, cellH) },
        };
        host.Children.Add(img);
        return host;
    }

    /// <summary>Render a single page (with annotations baked in) to a WriteableBitmap at the requested pixel size, applying the active color mode (grayscale…</summary>
    private WriteableBitmap RenderPageBitmap(uint pageIndex, uint pw, uint ph)
    {
        var (pixels, w, h) = Task.Run(
            () => _render.RenderPageBgraAsync(pageIndex, pw, ph, CancellationToken.None))
            .GetAwaiter().GetResult();

        var page = _pages[(int)pageIndex];
        // 打印跟随 G 键的批注可见性：屏幕隐藏了笔迹与白板卡片，打印也必须只出纯 PDF 页。
        if (_annotationsVisible)
        {
            if (page.Annotations != null && page.Annotations.Count > 0)
            {
                try
                {
                    pixels = BakeAnnotations(pixels, w, h, page);
                }
                catch { }
            }
            try
            {
                pixels = CanvasPrintBaker.Bake(pixels, w, h, page, _pages,
                    _canvasDoc, _pageLayouts);
            }
            catch { }
        }

        ApplyColorMode(pixels, _colorMode);

        var wb = new WriteableBitmap(w, h);
        using (var stream = wb.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }
        wb.Invalidate(); // Force the bitmap to refresh; without this the print preview may hang.
        return wb;
    }

    /// <summary>Desaturate the BGRA page buffer in place for grayscale / monochrome printing.</summary>
    private static void ApplyColorMode(byte[] pixels, string mode)
    {
        if (mode != "grayscale" && mode != "monochrome") return;
        bool mono = mode == "monochrome";
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            int lum = (int)(0.114 * b + 0.587 * g + 0.299 * r);
            if (mono) lum = lum >= 128 ? 255 : 0;
            byte v = (byte)Math.Max(0, Math.Min(255, lum));
            pixels[i] = v;
            pixels[i + 1] = v;
            pixels[i + 2] = v;
        }
    }

    /// <summary>Composite the base BGRA page with the page's Pen annotations on an offscreen Win2D canvas, returning a fresh BGRA buffer.</summary>
    private static byte[] BakeAnnotations(byte[] pixels, int w, int h, PdfPageViewModel page)
    {
        var device = CanvasDevice.GetSharedDevice();
        float dpi = (float)(w / Math.Max(page.PageWidth, 1.0) * 72.0);
        using var target = new CanvasRenderTarget(device, w, h, dpi);
        // Load the rendered BGRA base page directly (CanvasRenderTarget defaults
        target.SetPixelBytes(pixels);
        using (var ds = target.CreateDrawingSession())
        {
            float s = dpi / 72.0f;
            int rot = ((int)Math.Round(page.RotationAngle)) % 360;
            float cx = w / 2f, cy = h / 2f;
            var old = ds.Transform;
            var m = Matrix3x2.CreateScale(s);
            if (rot != 0)
                m *= Matrix3x2.CreateRotation((float)(rot * Math.PI / 180.0), new Vector2(cx, cy));
            ds.Transform = m * old;

            foreach (var a in page.Annotations)
            {
                if (a.Type != AnnotationType.Pen) continue;
                var pts = a.PointsCollection;
                if (pts == null || pts.Count == 0) continue;
                var color = ParseAnnoColor(a.ColorHex);
                float thick = (float)Math.Max(a.Thickness, 0.5);
                if (pts.Count == 1)
                {
                    ds.FillCircle((float)pts[0].X, (float)pts[0].Y, thick / 2f, color);
                    continue;
                }
                using var pb = new CanvasPathBuilder(ds);
                pb.BeginFigure((float)pts[0].X, (float)pts[0].Y);
                for (int i = 1; i < pts.Count; i++)
                    pb.AddLine((float)pts[i].X, (float)pts[i].Y);
                pb.EndFigure(CanvasFigureLoop.Open);
                using var geo = CanvasGeometry.CreatePath(pb);
                ds.DrawGeometry(geo, Vector2.Zero, color, thick, PenStrokeStyle);
            }
            ds.Transform = old;
        }
        return target.GetPixelBytes();
    }

    private static Windows.UI.Color ParseAnnoColor(string colorHex)
    {
        try
        {
            string hex = (colorHex ?? "#F2C200").Replace("#", "");
            if (hex.Length == 6) hex = "FF" + hex;
            return Windows.UI.Color.FromArgb(
                Convert.ToByte(hex.Substring(0, 2), 16),
                Convert.ToByte(hex.Substring(2, 2), 16),
                Convert.ToByte(hex.Substring(4, 2), 16),
                Convert.ToByte(hex.Substring(6, 2), 16));
        }
        catch
        {
            return Windows.UI.Color.FromArgb(255, 255, 255, 0);
        }
    }

    /// <summary>Normalize a PrintColorMode value (enum name or string) to our internal tag: "monochrome" / "grayscale" / "color".</summary>
    private static string NormalizeColorMode(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "color";
        var s = raw.ToLowerInvariant();
        if (s.Contains("mono") || s.Contains("black")) return "monochrome";
        if (s.Contains("gray")) return "grayscale";
        return "color";
    }

    /// <summary>Insert a custom option into the print panel immediately after a parent option.</summary>
    private static void ShowOptionAfter(PrintTaskOptionDetails details, string childId, string parentId)
    {
        if (details.DisplayedOptions.Contains(childId))
            return;

        int idx = details.DisplayedOptions.IndexOf(parentId);
        if (idx >= 0 && idx + 1 < details.DisplayedOptions.Count)
            details.DisplayedOptions.Insert(idx + 1, childId);
        else
            details.DisplayedOptions.Add(childId);
    }

    public void Dispose()
    {
        try { _render.Dispose(); } catch { }
    }
}
