using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace Glance;

/// <summary>
/// 矢量直绘（Vector Direct Render）共享构建器。
///
/// 把 MuPDF display list 导出的二进制命令流（页面 pt 空间：路径 / 描边 / 剪裁 /
/// 位图引用）解析并录制成一个 CanvasCommandList —— 几何与位图全部缓存在 GPU
/// 命令列表里，之后每帧只做 DrawImage(cmdList, 视图矩阵) 重放。任何缩放倍率
/// 都是按当前矩阵现算的矢量，全程清晰、无缓存密度、无重建等待。
///
/// 线程模型：可在【后台线程】用 device 快照调用 Build()（与 RenderTileAsync
/// 在后台线程 new CanvasRenderTarget 同构）；CreateDrawingSession 录制与
/// CanvasBitmap.CreateFromBytes 上传是设备绑定操作，调用方通过 uploadGateAcquire
/// 以 GpuUploadConcurrency 门限流上传，避免多路并发锤击共享 D2D 设备。
/// 构建完成后由调用方把 cmdList 交给 UI 线程绘制；dispose 也须在 UI 线程。
///
/// 命令流二进制布局（Rust 侧 vector_export.rs 写入，一致约定）：
///   u32 magic "GVDC" | u32 version=1 | f32 page_w | f32 page_h
///   u32 n_cmds | u32 n_images | u32 n_skipped
///   n_skipped × [u8 tag, u32 count]
///   commands...（见 Tag* 说明）
///   images...  （u32 id, u32 w, u32 h, u32 data_len, BGRA bytes）
/// </summary>
internal static class VectorSceneBuilder
{
    // 命令流 tag（与 Rust 侧 vector_export.rs 一致）
    private const byte TagFillPath = 1;
    private const byte TagStrokePath = 2;
    private const byte TagClipPath = 3;
    private const byte TagPopClip = 4;
    private const byte TagFillImage = 5;
    private const byte TagFillImageMask = 6;

    private const uint Magic = 0x4344_5647; // "GVDC"
    private const uint Version = 1;

    /// <summary>
    /// 解析命令流并录制成 CanvasCommandList。
    /// </summary>
    /// <param name="creator">CanvasDevice 或画布（ICanvasResourceCreator）。后台线程可传 device 快照。</param>
    /// <param name="stream">mu_export_page_commands 返回的原始字节。</param>
    /// <param name="uploadGateAcquire">图像上传前等待的并发门（返回 Task，调用方负责 Release）。</param>
    /// <param name="pageW">页面宽度（pt）。</param>
    /// <param name="pageH">页面高度（pt）。</param>
    /// <param name="imageCount">图像张数统计（诊断用）。</param>
    /// <param name="pathCount">路径命令数统计（诊断用）。</param>
    /// <param name="skipSummary">被跳过的命令统计字符串（诊断用）。</param>
    /// <returns>null = 流无效/版本不支持（调用方应回退瓦片）。</returns>
    public static CanvasCommandList? Build(
        ICanvasResourceCreator creator,
        byte[] stream,
        Func<long, Task>? uploadGateAcquire,
        out float pageW,
        out float pageH,
        out int imageCount,
        out int pathCount,
        out string skipSummary)
    {
        pageW = 0f;
        pageH = 0f;
        imageCount = 0;
        pathCount = 0;
        skipSummary = "";

        if (stream.Length < 16) return null;
        var cmdList = new CanvasCommandList(creator);
        var images = new Dictionary<uint, CanvasBitmap>();

        int i = 0;
        uint magic = R32(stream, ref i);
        uint version = R32(stream, ref i);
        pageW = RF(stream, ref i);
        pageH = RF(stream, ref i);
        if (magic != Magic || version != Version) return null;
        uint nCmds = R32(stream, ref i);
        uint nImages = R32(stream, ref i);
        uint nSkipped = R32(stream, ref i);

        var skipParts = new List<string>();
        for (uint k = 0; k < nSkipped && i < stream.Length; k++)
        {
            byte tag = stream[i++];
            uint count = R32(stream, ref i);
            skipParts.Add($"{tag}:{count}");
        }
        skipSummary = string.Join(",", skipParts);
        int cmdStart = i;

        // 第一遍：精确跳过 nCmds 条命令（payload 长度已知），定位图像区起点。
        for (uint c = 0; c < nCmds && i < stream.Length; c++) SkipCommand(stream, ref i);
        int imageStart = i;

        // 读图像数据块 → 上传 CanvasBitmap（构建期一次，之后每帧 GPU 引用）。
        for (uint k = 0; k < nImages && i + 16 <= stream.Length; k++)
        {
            uint id = R32(stream, ref i);
            uint w = R32(stream, ref i);
            uint h = R32(stream, ref i);
            uint dataLen = R32(stream, ref i);
            if (i + dataLen > stream.Length) break;
            byte[] bgra = new byte[dataLen];
            Array.Copy(stream, i, bgra, 0, (int)dataLen);
            i += (int)dataLen;
            try
            {
                if (uploadGateAcquire != null) uploadGateAcquire(dataLen).GetAwaiter().GetResult();
                try
                {
                    images[id] = CanvasBitmap.CreateFromBytes(creator, bgra, (int)w, (int)h, DirectXPixelFormat.B8G8R8A8UIntNormalized);
                }
                finally { }
            }
            catch
            {
                // 解码失败：降级 1×1 透明占位，保持命令内引用 id 有效。
                try { images[id] = CanvasBitmap.CreateFromBytes(creator, new byte[4], 1, 1, DirectXPixelFormat.B8G8R8A8UIntNormalized); } catch { }
            }
        }
        imageCount = (int)nImages;

        // 第二遍：重新从命令起点解析并录制到 CanvasCommandList（图像已可用）。
        i = cmdStart;
        pathCount = 0;
        var clipStack = new Stack<CanvasActiveLayer>();
        using var ds = cmdList.CreateDrawingSession();
        for (uint c = 0; c < nCmds && i < imageStart; c++)
        {
            byte tag = stream[i++];
            switch (tag)
            {
                case TagFillPath: // u8 evenOdd, rgba(4×f32), path
                {
                    bool evenOdd = stream[i++] != 0;
                    float rr = RF(stream, ref i), gg = RF(stream, ref i), bb = RF(stream, ref i), aa = RF(stream, ref i);
                    var geo = ReadGeometry(creator, stream, ref i, out _);
                    if (geo != null)
                    {
                        try { ds.FillGeometry(geo, new Vector2(0, 0), ColorF(rr, gg, bb, aa)); } catch { }
                    }
                    pathCount++;
                    break;
                }
                case TagStrokePath:
                {
                    byte cap = stream[i++];
                    byte join = stream[i++];
                    float width = RF(stream, ref i);
                    float miter = RF(stream, ref i);
                    uint ndash = R32(stream, ref i);
                    var dashes = new float[ndash];
                    for (uint k = 0; k < ndash; k++) dashes[k] = RF(stream, ref i);
                    float rr = RF(stream, ref i), gg = RF(stream, ref i), bb = RF(stream, ref i), aa = RF(stream, ref i);
                    var geo = ReadGeometry(creator, stream, ref i, out _);
                    if (geo != null)
                    {
                        var style = new CanvasStrokeStyle
                        {
                            StartCap = (CanvasCapStyle)cap,
                            EndCap = (CanvasCapStyle)cap,
                            DashCap = (CanvasCapStyle)cap,
                            LineJoin = (CanvasLineJoin)join,
                            MiterLimit = miter,
                            CustomDashStyle = dashes.Length > 0 ? dashes : null,
                        };
                        try { ds.DrawGeometry(geo, new Vector2(0, 0), ColorF(rr, gg, bb, aa), width, style); } catch { }
                    }
                    pathCount++;
                    break;
                }
                case TagClipPath: // u8 evenOdd, path → push clip layer（保持到 POP_CLIP）
                {
                    bool evenOdd = stream[i++] != 0;
                    var geo = ReadGeometry(creator, stream, ref i, out _);
                    if (geo != null)
                    {
                        try
                        {
                            var layer = ds.CreateLayer(1.0f, geo);
                            clipStack.Push(layer);
                        }
                        catch { }
                    }
                    break;
                }
                case TagPopClip:
                {
                    if (clipStack.Count > 0)
                    {
                        try { clipStack.Pop().Dispose(); } catch { }
                    }
                    break;
                }
                case TagFillImage: // mat(6×f32), u32 id, f32 alpha
                {
                    var m = ReadMatrix(stream, ref i);
                    uint id = R32(stream, ref i);
                    float alpha = RF(stream, ref i);
                    if (images.TryGetValue(id, out var bmp))
                    {
                        var save = ds.Transform;
                        // ctm 把"单位正方形"映射到页面目标区域。位图先缩到 1×1 再按 ctm
                        // 铺放 —— 必须走 Transform 而非绝对值 Rect：ctm 的负 d（页面 y
                        // 翻转）由变换矩阵天然携带，绝对值矩形会丢掉符号造成上下镜像。
                        // NearestNeighbor：位图放大保持原始像素粒（扫描件/照片与 Sumatra
                        // 的"像素清晰"一致），不做插值平滑；矢量文字（FillGeometry）不受
                        // 本插值影响、仍旧矢量 AA。位图缩小改由外层录制矩阵在显示时处理。
                        var bmpSz = bmp.SizeInPixels;
                        var s = Matrix3x2.CreateScale(1.0f / bmpSz.Width, 1.0f / bmpSz.Height);
                        ds.Transform = s * m;
                        try
                        {
                            ds.DrawImage(bmp,
                                new Rect(0, 0, bmpSz.Width, bmpSz.Height),
                                new Rect(0, 0, bmpSz.Width, bmpSz.Height),
                                1f,
                                CanvasImageInterpolation.NearestNeighbor);
                        }
                        catch { }
                        ds.Transform = save;
                    }
                    break;
                }
                case TagFillImageMask: // mat, u32 id, rgba(4×f32 incl alpha), f32 alpha
                {
                    var m = ReadMatrix(stream, ref i);
                    uint id = R32(stream, ref i);
                    float rr = RF(stream, ref i), gg = RF(stream, ref i), bb = RF(stream, ref i), _dummy = RF(stream, ref i);
                    float alpha = RF(stream, ref i);
                    if (images.TryGetValue(id, out var bmp))
                    {
                        var save = ds.Transform;
                        var bmpSz = bmp.SizeInPixels;
                        var s = Matrix3x2.CreateScale(1.0f / bmpSz.Width, 1.0f / bmpSz.Height);
                        ds.Transform = s * m;
                        try
                        {
                            ds.DrawImage(bmp,
                                new Rect(0, 0, bmpSz.Width, bmpSz.Height),
                                new Rect(0, 0, bmpSz.Width, bmpSz.Height),
                                1f,
                                CanvasImageInterpolation.NearestNeighbor);
                        }
                        catch { }
                        ds.Transform = save;
                    }
                    break;
                }
                default:
                    i = imageStart; // 未知 tag：终止（跳到图像区结束解析）
                    break;
            }
        }
        return cmdList;
    }

    private static uint R32(byte[] b, ref int i) { uint v = BitConverter.ToUInt32(b, i); i += 4; return v; }
    private static float RF(byte[] b, ref int i) { float v = BitConverter.ToSingle(b, i); i += 4; return v; }

    /// <summary>跳过一条命令的 payload（不解析），用于第一遍定位图像区。</summary>
    private static void SkipCommand(byte[] b, ref int i)
    {
        if (i >= b.Length) return;
        byte tag = b[i++];
        switch (tag)
        {
            case TagFillPath: // evenOdd + rgba(16B) + path
                i += 1 + 16;
                SkipPath(b, ref i);
                break;
            case TagStrokePath: // cap join + width miter + ndash + dashes + rgba(16B) + path
            {
                i += 2 + 8;
                uint ndash = R32(b, ref i);
                i += (int)ndash * 4 + 16;
                SkipPath(b, ref i);
                break;
            }
            case TagClipPath: // evenOdd + path
                i += 1;
                SkipPath(b, ref i);
                break;
            case TagPopClip:
                break;
            case TagFillImage: // mat(24B) + id(4B) + alpha(4B)
                i += 32;
                break;
            case TagFillImageMask: // mat + id + rgba(16B) + alpha(4B)
                i += 24 + 4 + 16 + 4;
                break;
            default:
                break;
        }
    }

    /// <summary>跳过 path 子流（u32 n，然后每条命令变长）。</summary>
    private static void SkipPath(byte[] b, ref int i)
    {
        if (i + 4 > b.Length) return;
        uint n = R32(b, ref i);
        for (uint k = 0; k < n && i < b.Length; k++)
        {
            byte t = b[i++];
            if (t == 0 || t == 1) i += 8;      // move/line: 2×f32
            else if (t == 2) i += 24;          // curve: 6×f32
            else if (t == 3) { /* close: 无数据 */ }
        }
    }

    private static Matrix3x2 ReadMatrix(byte[] b, ref int i)
    {
        float a = RF(b, ref i), b0 = RF(b, ref i), c = RF(b, ref i), d = RF(b, ref i), e = RF(b, ref i), f = RF(b, ref i);
        // Win2D Matrix3x2 行主序 (M11 M12; M21 M22; M31 M32)，平移在 M31/M32；
        // MuPDF Matrix (a b; c d; e f) 平移在 e/f。两者均为列向量约定 point*M，直接映射。
        return new Matrix3x2(a, b0, c, d, e, f);
    }

    /// <summary>读取 path 子流并构建 CanvasGeometry（页面用户空间坐标）。</summary>
    private static CanvasGeometry? ReadGeometry(ICanvasResourceCreator creator, byte[] b, ref int i, out bool anyClosed)
    {
        anyClosed = false;
        if (i + 4 > b.Length) return null;
        uint n = R32(b, ref i);
        if (n == 0) return null;
        try
        {
            var pb = new CanvasPathBuilder(creator);
            bool figureOpen = false;
            for (uint k = 0; k < n && i < b.Length; k++)
            {
                byte t = b[i++];
                switch (t)
                {
                    case 0: // move
                    {
                        if (i + 8 > b.Length) return null;
                        float x = RF(b, ref i), y = RF(b, ref i);
                        if (figureOpen) { pb.EndFigure(CanvasFigureLoop.Open); figureOpen = false; }
                        pb.BeginFigure(x, y);
                        figureOpen = true;
                        break;
                    }
                    case 1: // line
                    {
                        if (i + 8 > b.Length) return null;
                        float x = RF(b, ref i), y = RF(b, ref i);
                        pb.AddLine(x, y);
                        break;
                    }
                    case 2: // curve
                    {
                        if (i + 24 > b.Length) return null;
                        float c1x = RF(b, ref i), c1y = RF(b, ref i);
                        float c2x = RF(b, ref i), c2y = RF(b, ref i);
                        float ex = RF(b, ref i), ey = RF(b, ref i);
                        pb.AddCubicBezier(new Vector2(c1x, c1y), new Vector2(c2x, c2y), new Vector2(ex, ey));
                        break;
                    }
                    case 3: // close
                    {
                        if (figureOpen) { pb.EndFigure(CanvasFigureLoop.Closed); figureOpen = false; anyClosed = true; }
                        break;
                    }
                }
            }
            if (figureOpen) { pb.EndFigure(CanvasFigureLoop.Open); figureOpen = false; }
            return CanvasGeometry.CreatePath(pb);
        }
        catch { return null; }
    }

    public static Windows.UI.Color ColorF(float r, float g, float b, float a)
    {
        var c = new Windows.UI.Color();
        c.R = (byte)Math.Clamp(Math.Round(r * 255), 0, 255);
        c.G = (byte)Math.Clamp(Math.Round(g * 255), 0, 255);
        c.B = (byte)Math.Clamp(Math.Round(b * 255), 0, 255);
        c.A = (byte)Math.Clamp(Math.Round(a * 255), 0, 255);
        return c;
    }
}