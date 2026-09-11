using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage.Pickers;
using Windows.UI;
using Glance.Interop;

namespace Glance;

/// <summary>
/// 矢量直绘（Vector Direct Render）原型 v2：几何缓存版。
///
/// 命题：MuPDF display list → 分辨率无关的命令流 → C# 侧【构建期一次】解析成
/// CanvasCommandList（Geometry/位图全部缓存在 GPU 命令列表里），【每帧】只做
/// DrawImage(cmdList, 视图矩阵) —— 不再每帧重解析场景树（v1 的 40ms 根因）。
///
/// v1 结论回顾：文字 PDF 5ms 达标、200× 文字清晰；大图案 PDF 40ms（SVG DOM
/// 每帧重解析 + 内嵌位图重解码）。本版专门验证这 40ms 能否归位。
/// </summary>
public sealed partial class VectorProbePage : Page
{
    private static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "glance_probe.log");

    private static void Log(string msg)
    {
        try { System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n"); } catch { }
    }

    private IntPtr _engine;
    private uint _pageCount;
    private bool _selfTest;
    private bool _deviceReady;
    private uint _probePage;

    // 几何缓存（构建期一次）
    private CanvasCommandList? _cmdList;
    private float _pageW = 1f, _pageH = 1f;
    private int _pathCount, _imageCount;
    private string _skipSummary = "";

    // 视图状态
    private float _zoom = 1f;
    private Vector2 _pan;
    private Vector2 _lastPtr;
    private bool _dragging;
    private int _retryLoad;

    // 探针
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _frames;
    private double _drawMs;
    private long _exportMs, _buildMs;

    public VectorProbePage()
    {
        InitializeComponent();
        ProbeCanvas.PointerWheelChanged += ProbeCanvas_PointerWheelChanged;
        ProbeCanvas.PointerPressed += ProbeCanvas_PointerPressed;
        ProbeCanvas.PointerMoved += ProbeCanvas_PointerMoved;
        ProbeCanvas.PointerReleased += ProbeCanvas_PointerReleased;
        ProbeCanvas.DoubleTapped += ProbeCanvas_DoubleTapped;

        var args = Environment.GetCommandLineArgs();
        _selfTest = Array.IndexOf(args, "--probe-selftest") >= 0;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--probe-page" && uint.TryParse(args[i + 1], out uint pp)) { _probePage = pp; }
        }
        for (int i = 1; i < args.Length; i++)
        {
            if (System.IO.File.Exists(args[i]) &&
                System.IO.Path.GetExtension(args[i]).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                _ = LoadPdfAsync(args[i]);
                break;
            }
        }
    }

    private void ProbeCanvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _deviceReady = true;
    }

    // ================= 文件加载 =================

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var app = Application.Current as App;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(app?.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add(".pdf");
        var file = await picker.PickSingleFileAsync();
        if (file != null) await LoadPdfAsync(file.Path);
    }

    private async Task LoadPdfAsync(string path)
    {
        try
        {
            if (_engine != IntPtr.Zero) { GlanceNative.mu_engine_destroy(_engine); _engine = IntPtr.Zero; }
            StatusText.Text = $"打开: {System.IO.Path.GetFileName(path)}";

            var pathPtr = GlanceNative.MarshalUtf8(path);
            try
            {
                var res = GlanceNative.mu_engine_open(pathPtr, IntPtr.Zero, 8);
                if (res.NeedsPassword != 0) { StatusText.Text = "需要密码"; return; }
                if (res.Engine == IntPtr.Zero) { StatusText.Text = "打开失败"; return; }
                _engine = res.Engine;
            }
            finally { GlanceNative.FreeUtf8(pathPtr); }

            _pageCount = GlanceNative.mu_engine_page_count(_engine);

            while (!_deviceReady && _retryLoad < 200)
            {
                await Task.Delay(20);
                _retryLoad++;
            }
            if (!_deviceReady) { StatusText.Text = "Canvas device 未就绪"; Log("device NOT ready"); return; }

            var sw = Stopwatch.StartNew();
            byte[]? stream = ExportPageCommands(_probePage);
            sw.Stop();
            _exportMs = sw.ElapsedMilliseconds;
            Log($"export ok: {_exportMs}ms len={stream?.Length ?? 0}");
            if (stream == null || stream.Length == 0) { StatusText.Text = "命令流导出失败"; Log("export FAILED"); return; }

            sw.Restart();
            _cmdList?.Dispose();
            _cmdList = VectorSceneBuilder.Build(ProbeCanvas, stream, null,
                out _pageW, out _pageH, out _imageCount, out _pathCount, out _skipSummary);
            sw.Stop();
            _buildMs = sw.ElapsedMilliseconds;
            Log($"build ok: {_buildMs}ms paths={_pathCount} images={_imageCount}");

            StatusText.Text = $"第 {_probePage}/{_pageCount} 页 · 导出 {_exportMs}ms · 构建 {_buildMs}ms · 路径 {_pathCount} · 图像 {_imageCount} · 跳过[{(_skipSummary.Length > 1 ? _skipSummary : "无")}] · 页面 {_pageW:0.##}×{_pageH:0.##}pt";
            _zoom = (float)(ProbeCanvas.ActualWidth > 0 ? ProbeCanvas.ActualWidth / _pageW : 1.0);
            _pan = Vector2.Zero;
            ProbeCanvas.Invalidate();

            if (_selfTest) await RunSelfTestAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"错误: {ex.Message}";
            Log($"LoadPdfAsync ERROR: {ex}");
        }
    }

    // ================= 命令流 → CanvasCommandList =================

    private byte[]? ExportPageCommands(uint page)
    {
        var resp = GlanceNative.mu_export_page_commands(_engine, page, out _, out _);
        if (!resp.Success) return null;
        try
        {
            byte[] bytes = new byte[resp.DataLen];
            Marshal.Copy(resp.DataPtr, bytes, 0, (int)resp.DataLen);
            return bytes;
        }
        finally { GlanceNative.memory_free(resp.DataPtr, resp.DataLen); }
    }

    // ================= 交互 =================

    private void ProbeCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(ProbeCanvas).Position;
        float delta = (float)e.GetCurrentPoint(ProbeCanvas).Properties.MouseWheelDelta;
        float factor = (float)Math.Pow(1.1, delta / 120.0);
        ZoomAt(new Vector2((float)pos.X, (float)pos.Y), factor);
        e.Handled = true;
    }

    private void ZoomAt(Vector2 screenPt, float factor)
    {
        var before = ScreenToPage(screenPt);
        _zoom = Math.Clamp(_zoom * factor, 0.01f, 500f);
        var after = ScreenToPage(screenPt);
        _pan += (after - before) * _zoom;
    }

    private void ProbeCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        var pos = e.GetCurrentPoint(ProbeCanvas).Position;
        _lastPtr = new Vector2((float)pos.X, (float)pos.Y);
        ProbeCanvas.CapturePointer(e.Pointer);
    }

    private void ProbeCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var pos = e.GetCurrentPoint(ProbeCanvas).Position;
        var pt = new Vector2((float)pos.X, (float)pos.Y);
        _pan += pt - _lastPtr;
        _lastPtr = pt;
        ProbeCanvas.Invalidate();
    }

    private void ProbeCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
    }

    private void ProbeCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _zoom = Math.Max(1f, (float)(ProbeCanvas.ActualWidth / _pageW));
        _pan = Vector2.Zero;
        ProbeCanvas.Invalidate();
    }

    private Vector2 ScreenToPage(Vector2 screen) => (screen - _pan) / _zoom;

    // ================= 绘制（每帧只重放 + 换矩阵） =================

    private void ProbeCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var sw = Stopwatch.StartNew();
        var ds = args.DrawingSession;
        ds.Clear(Microsoft.UI.Colors.Gray);

        if (_cmdList != null)
        {
            var m = Matrix3x2.CreateScale(_zoom, _zoom) * Matrix3x2.CreateTranslation(_pan);
            var save = ds.Transform;
            ds.Transform = m;
            try { ds.DrawImage(_cmdList); } catch (Exception ex) { ds.DrawText("render err: " + ex.Message, new Vector2(8, 8), Microsoft.UI.Colors.Red); }
            ds.Transform = save;
        }

        ds.DrawText($"zoom={_zoom:0.###}×  draw={sw.Elapsed.TotalMilliseconds:0.000}ms",
            new Vector2(8, 8), Microsoft.UI.Colors.White);
        sw.Stop();
        UpdateFps(sw.Elapsed.TotalMilliseconds);
        ProbeCanvas.Invalidate();
    }

    private void UpdateFps(double lastMs)
    {
        _frames++;
        _drawMs += lastMs;
        if (_clock.ElapsedMilliseconds >= 500)
        {
            double fps = _frames * 1000.0 / _clock.ElapsedMilliseconds;
            double avgMs = _drawMs / _frames;
            FpsText.Text = $"FPS={fps:0.0}  avg_draw={avgMs:0.000}ms";
            _frames = 0; _drawMs = 0;
            _clock.Restart();
        }
    }

    // ================= 自测：离屏渲染多档位 =================

    private async Task RunSelfTestAsync()
    {
        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "glance_vector_probe");
            System.IO.Directory.CreateDirectory(dir);
            float[] zooms = { 1.0f, 4.0f };
            foreach (float z in zooms)
            {
                int w = (int)(_pageW * z);
                int h = (int)(_pageH * z);
                if (w <= 0 || h <= 0 || w > 16384 || h > 16384) continue;
                var sw = Stopwatch.StartNew();
                // 方向对照：同一命令流分别以【不翻转】与【y 翻转】画两份 PNG，
                // 标题在上、字朝上那版即正确方向 → 据此判定 cmdList 坐标轴（y-up / y-down）。
                string baseName = $"zoom{(_pageW * z):0}px_z{z}";
                using (var rt = new CanvasRenderTarget(ProbeCanvas, w, h))
                {
                    using (var ds = rt.CreateDrawingSession())
                    {
                        ds.Clear(Microsoft.UI.Colors.White);
                        ds.DrawImage(_cmdList); // 不翻转
                    }
                    await rt.SaveAsync(System.IO.Path.Combine(dir, baseName + "_noraw.png"), CanvasBitmapFileFormat.Png);
                }
                using (var rt = new CanvasRenderTarget(ProbeCanvas, w, h))
                {
                    using (var ds = rt.CreateDrawingSession())
                    {
                        ds.Clear(Microsoft.UI.Colors.White);
                        // 翻转：y' = h - y（把 y-up 页面翻到 y-down 画布）
                        ds.Transform = Matrix3x2.CreateScale(z, -z) * Matrix3x2.CreateTranslation(0, h);
                        ds.DrawImage(_cmdList);
                        ds.Transform = Matrix3x2.Identity;
                    }
                    sw.Stop();
                    Log($"selftest zoom{z}: {w}x{h} draw={sw.Elapsed.TotalMilliseconds:0}ms");
                    await rt.SaveAsync(System.IO.Path.Combine(dir, baseName + "_flip.png"), CanvasBitmapFileFormat.Png);
                    Log($"selftest saved: {baseName}_*.png");
                }
            }
            StatusText.Text = $"自测完成 → {dir}";
            FpsText.Text = "";
            Log("selftest DONE");
            System.Environment.Exit(0);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"自测失败: {ex.Message}";
            Log($"selftest ERROR: {ex}");
        }
    }
}