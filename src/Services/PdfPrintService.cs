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

/// <summary>
/// WPS-style printing for GlanceRX.
///
/// Shows the system print dialog (printer / copies / orientation / duplex / paper /
/// color) via the WinUI3 PrintManager, then prints the requested PDF pages at high
/// resolution. Freehand (Pen) annotations are baked into each page using the exact
/// same transform the on-screen GPU viewer uses (Scale(DPI/72) · Rotation(page
/// rotation, center)), so the printed ink matches what the user sees.
///
/// WinUI3 printing model notes (different from UWP):
///  - PrintDocument lives in Microsoft.UI.Xaml.Printing.
///  - The AddPages event fires ONCE; inside it we loop over the pages we want
///    and call printDocument.AddPage(visual) for each, then AddPagesComplete().
///
/// Glance-specific print options (composed by the app, not by the system):
///  - "GlancePages" (Pages): All / Current page / Custom range. The system "Current
///    page" option resolves to page 1 because the framework does not know which page
///    the user is viewing inside Glance, so we track it ourselves and expose our own
///    selector. The custom range is a free-text "1-3, 5" box.
///  - "GlanceNUp" (Pages per sheet): 1 / 2 / 4 / 6 / 9 / 16 logical pages laid out on
///    a single physical sheet. The system NUp option cannot be read back by the app
///    (it is not exposed in PrintTaskOptionDetails and does not raise OptionChanged),
///    so we provide our own list option and composite the physical sheet ourselves —
///    this also makes the preview reflect the layout live.
///  - "GlancePrintScale" (Print scaling): fit / fill / 100% / 50/75/125/150/200% /
///    custom %, controlling how each logical page is sized on the sheet.
///  - Duplex and Color mode are presented by the system print panel; we read Color
///    mode back and apply it to the preview (and to the printed bitmap) so the
///    preview matches the output. Duplex is applied by the driver automatically.
///
/// Concurrency: the print events run on the UI thread. The native page render is
/// kicked off inside Task.Run (no UI SynchronizationContext) and awaited with
/// GetResult() — this avoids the await-on-UI-thread deadlock, because the
/// native FFI call is offloaded to the thread pool via Task.Run.
/// The bitmap is written into a WriteableBitmap.PixelBuffer (synchronous), so no
/// async SetBitmapAsync is needed either.
/// </summary>
public sealed class PdfPrintService : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly PdfRenderService _render;
    private readonly IReadOnlyList<PdfPageViewModel> _pages;
    private readonly string _documentTitle;
    private readonly int _currentPage;

    private PrintManager? _printManager;
    private PrintDocument? _printDocument;
    private TaskCompletionSource<bool>? _tcs;
    private bool _printTaskRequested;
    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

    // Resolved list of 0-based document page indices to print, cached from the
    // page-range selection so the preview and the final print stay in sync.
    private List<uint>? _pagesToPrint;

    // Glance-self-composed print options.
    private int _nUp = 1;
    private string _printScaleMode = "fit"; // "fit" | "fill" | "50".."200" | "custom"
    private int _customScalePercent = 100;
    private string _pageRangeMode = "all";   // "all" | "current" | "custom"
    private string _customPageRange = "1";
    private string _colorMode = "color";      // "color" | "grayscale" | "monochrome"
    private PrintPageDescription? _pageDescription;

    private const double DefaultDpi = 300.0;
    private const double PreviewDpi = 96.0;
    private const int MaxLongEdge = 4000;

    // A4 @ 96 DPI, used as the preview's physical page size when the print
    // subsystem has not yet reported a page description.
    private const double DefaultPageWidthDip = 793.7;
    private const double DefaultPageHeightDip = 1122.5;

    private static readonly CanvasStrokeStyle PenStrokeStyle = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round,
    };

    public PdfPrintService(IntPtr hwnd, PdfRenderService render, IReadOnlyList<PdfPageViewModel> pages, string documentTitle, int currentPage = 0)
    {
        _hwnd = hwnd;
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        _documentTitle = documentTitle;
        _currentPage = currentPage;
    }

    /// <summary>
    /// Show the system print dialog and print. Returns false only when the print
    /// UI could not be shown (e.g. no printer installed); a user cancel is treated
    /// as success.
    /// </summary>
    public async Task<bool> ShowPrintDialogAsync()
    {
        if (!PrintManager.IsSupported())
            return false;

        // 根因修复（打印崩溃）：PrintManager.IsSupported() 仅表示系统支持打印 API，不表示已安装打印机。
        // 无打印机时点打印仍会触发 PrintTaskRequested，并在 deferral.Complete() 抛 COMException 使进程崩溃。
        // 故在打开打印 UI 前主动检测已安装打印机；一台都没有则直接返回 false，由 Print_Click 弹出
        // 「未找到打印机」提示（NoPrinterTitle/NoPrinterContent），根本不进入会崩的路径。
        if (!HasAnyPrinter())
            return false;

        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        _printManager = PrintManagerInterop.GetForWindow(_hwnd);
        _printManager.PrintTaskRequested += OnPrintTaskRequested;
        _tcs = new TaskCompletionSource<bool>();
        _printTaskRequested = false;

        // Create the PrintDocument on the UI thread up front. The PrintTaskSourceRequested
        // callback may run off the UI thread, and WinUI3's PrintDocument / its DocumentSource
        // must be created and accessed on the UI thread — otherwise the source never gets
        // set and the print subsystem abandons the job (the dialog hangs on "loading preview").
        try
        {
            EnsurePrintDocument();
        }
        catch (Exception)
        {
            return false;
        }

        try
        {
            await PrintManagerInterop.ShowPrintUIForWindowAsync(_hwnd);

            // If the user closed the dialog without printing, PrintTaskRequested
            // never fires — treat as a benign cancel.
            if (!_printTaskRequested)
                return true;

            return await _tcs.Task;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (_printManager != null)
                _printManager.PrintTaskRequested -= OnPrintTaskRequested;
            _printDocument = null;
        }
    }

    /// <summary>
    /// 根因修复（打印崩溃）：PrintManager.IsSupported() 不表示已安装打印机。此方法读取后台打印程序的
    /// 打印机注册表项（HKLM\SYSTEM\CurrentControlSet\Control\Print\Printers），统计已安装打印机数量
    /// （含虚拟打印机如 Microsoft Print to PDF / OneNote / WPS PDF）。一台都没有则返回 false，使调用方
    /// 走「未找到打印机」提示分支，避免进入 PrintTaskRequested 后在 deferral.Complete() 抛 COMException 崩溃。
    ///
    /// 不用 Windows.Devices.Enumeration.DeviceInformation 枚举：FindAllAsync 默认只返回「当前在位(present)」
    /// 的设备，而打印机是后台打印程序注册的软件设备，不在 PnP 在位集合里，按安装类 GUID 查询会得到 0
    /// （曾因此在有打印机的机器上误报「未找到打印机」）。注册表项才是"已安装打印机"的权威来源。
    ///
    /// 读取失败（如权限受限）时失败开放，返回 true 仍尝试打开打印 UI，避免误伤正常机器。
    /// </summary>
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

    /// <summary>
    /// Create the PrintDocument and wire its events on the UI thread. Must be
    /// called before the print subsystem requests the source.
    /// </summary>
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
            // preview already matches the selected mode).
            try { _colorMode = NormalizeColorMode(task.Options.ColorMode.ToString()); }
            catch { _colorMode = "color"; }

            // Initialize the composed-option state.
            _nUp = 1;
            _printScaleMode = "fit";
            _customScalePercent = 100;
            _pageRangeMode = "all";
            _customPageRange = "1";

            // Add Glance's own composed options to the print panel. These drive the
            // live preview (N-Up layout + scaling + page range) and are read back in
            // OptionChanged.
            var details = PrintTaskOptionDetails.GetFromPrintTaskOptions(task.Options);
            details.OptionChanged += OnPrintOptionChanged;

            // Pages: All / Current page / Custom range.
            var pages = details.CreateItemListOption("GlancePages", LocalizationService.Get("PrintPages"));
            pages.AddItem("all", LocalizationService.Get("PrintPagesAll"));
            pages.AddItem("current", LocalizationService.Get("PrintPagesCurrent"));
            pages.AddItem("custom", LocalizationService.Get("PrintPagesCustom"));
            pages.State = PrintOptionStates.Enabled;
            details.DisplayedOptions.Add("GlancePages");

            // Custom page range — not shown initially. It is dynamically inserted right
            // after "GlancePages" only when the user selects "custom" range.
            var range = details.CreateTextOption("GlancePageRange", LocalizationService.Get("PrintPageRange"));
            range.TrySetValue("1");
            range.State = PrintOptionStates.Enabled;

            // Pages-per-sheet (N-Up).
            var nup = details.CreateItemListOption("GlanceNUp", LocalizationService.Get("PrintNUp"));
            nup.AddItem("1", "1");
            nup.AddItem("2", "2");
            nup.AddItem("4", "4");
            nup.AddItem("6", "6");
            nup.AddItem("9", "9");
            nup.AddItem("16", "16");
            nup.State = PrintOptionStates.Enabled;
            details.DisplayedOptions.Add("GlanceNUp");

            // Print scaling.
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
            // preview matches what the dropdown shows ("fit" → scaled to the paper).
            scale.TrySetValue("fit");
            scale.State = PrintOptionStates.Enabled;
            details.DisplayedOptions.Add("GlancePrintScale");

            // Custom scale percentage — not shown initially. It is dynamically inserted
            // right after "GlancePrintScale" only when the user selects "custom" scale.
            var custom = details.CreateTextOption("GlanceCustomScale", LocalizationService.Get("PrintScaleCustomLabel"));
            custom.TrySetValue("100");
            custom.State = PrintOptionStates.Enabled;

            // Duplex (single/double-sided) — driven by the system option, applied by
            // the driver automatically; we only surface it in the panel.
            details.DisplayedOptions.Add(StandardPrintTaskOptions.Duplex);
        }
        catch
        {
            // 仅当任务未创建成功时才把结果置 false（任务已创建则交由 OnPrintTaskCompleted 判定成败，
            // 避免配置阶段的偶发异常误报失败）。同时也避免调用方 await _tcs.Task 永久挂起。
            if (!taskCreated)
                _tcs?.TrySetResult(false);
        }
        finally
        {
            // 根因修复（打印崩溃）：仅当 PrintTask 创建成功才完成 deferral。无打印机 / 创建失败时
            // CreatePrintTask 会抛异常（被上方 catch 吞掉），此时不存在可完成的任务；若仍调用
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
        // on the UI thread. Keep the deferral open until we have posted back and set it.
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

            // Physical sheet count = logical pages / N-Up, rounded up.
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
        // Failed = real error; Succeeded / Canceled = not an error for our purposes.
        _tcs?.TrySetResult(e.Completion != PrintTaskCompletion.Failed);
    }

    /// <summary>
    /// Resolve the Glance page-range selection into 0-based document page indices,
    /// clamped to the document. "All" → every page; "Current" → the page currently
    /// shown in the viewer; "Custom" → parsed from free text ("1-3, 5").
    /// </summary>
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

    /// <summary>
    /// Compose one physical sheet. With N-Up &lt;= 1 the sheet holds a single logical
    /// page; otherwise logical pages are tiled into a cols×rows grid that fills the
    /// physical paper. Preview uses 96 DPI, the final print uses 300 DPI.
    /// </summary>
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

    /// <summary>
    /// Render one logical page into a cell-sized element, honoring the active
    /// scaling mode (fit / fill / percentage / custom %). The page is rendered at
    /// exactly its displayed DIP size and shown 1:1 (centered), so "fit"/percentage
    /// leave white borders and "fill"/enlarged pages overflow (clipped by the cell).
    /// </summary>
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
            // Cover the cell, preserving aspect, overflow clipped by the host.
            if (pageAspect > cellAspect) { dispH = cellH; dispW = cellH * pageAspect; }
            else { dispW = cellW; dispH = cellW / pageAspect; }
        }
        else if (_printScaleMode == "fit")
        {
            // Fit inside the cell, preserving aspect, white borders around.
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
        // 1:1 — no second stretch, so borders and overflow are truthful.
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

    /// <summary>
    /// Render a single page (with annotations baked in) to a WriteableBitmap at the
    /// requested pixel size, applying the active color mode (grayscale / monochrome).
    /// Runs the native render on a pool thread to avoid a UI-thread deadlock.
    /// </summary>
    private WriteableBitmap RenderPageBitmap(uint pageIndex, uint pw, uint ph)
    {
        var (pixels, w, h) = Task.Run(
            () => _render.RenderPageBgraAsync(pageIndex, pw, ph, CancellationToken.None))
            .GetAwaiter().GetResult();

        // Bake freehand annotations onto the rendered page (same transform as the
        // on-screen viewer). Falls back to the base page if Win2D baking fails.
        var page = _pages[(int)pageIndex];
        if (page.Annotations != null && page.Annotations.Count > 0)
        {
            try
            {
                pixels = BakeAnnotations(pixels, w, h, page);
            }
            catch { }
        }

        // Apply grayscale / monochrome so the preview matches the printed output.
        ApplyColorMode(pixels, _colorMode);

        var wb = new WriteableBitmap(w, h);
        using (var stream = wb.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }
        wb.Invalidate(); // Force the bitmap to refresh; without this the print preview may hang.
        return wb;
    }

    /// <summary>
    /// Desaturate the BGRA page buffer in place for grayscale / monochrome printing.
    /// </summary>
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

    /// <summary>
    /// Composite the base BGRA page with the page's Pen annotations on an offscreen
    /// Win2D canvas, returning a fresh BGRA buffer. The transform mirrors
    /// GpuPageView.DrawAnnotations exactly (page-point space → pixels).
    /// </summary>
    private static byte[] BakeAnnotations(byte[] pixels, int w, int h, PdfPageViewModel page)
    {
        var device = CanvasDevice.GetSharedDevice();
        float dpi = (float)(w / Math.Max(page.PageWidth, 1.0) * 72.0);
        using var target = new CanvasRenderTarget(device, w, h, dpi);
        // Load the rendered BGRA base page directly (CanvasRenderTarget defaults
        // to B8G8R8A8UIntNormalized, matching our buffer). This avoids the
        // CreateFromBytes overload pitfalls.
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

    /// <summary>
    /// Normalize a PrintColorMode value (enum name or string) to our internal tag:
    /// "monochrome" / "grayscale" / "color". Tolerant of naming variations.
    /// </summary>
    private static string NormalizeColorMode(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "color";
        var s = raw.ToLowerInvariant();
        if (s.Contains("mono") || s.Contains("black")) return "monochrome";
        if (s.Contains("gray")) return "grayscale";
        return "color";
    }

    /// <summary>
    /// Insert a custom option into the print panel immediately after a parent option.
    /// Used to show dependent text boxes (custom page range / custom scale) only when
    /// the parent ItemList is set to "custom".
    /// </summary>
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
