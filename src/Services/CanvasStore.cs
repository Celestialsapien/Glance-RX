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
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlanceRX.Services;

/// <summary>白板节点。字段名对齐 jsoncanvas 1.0（camelCase），坐标是【页局部未旋转 PDF 点】， 即与画笔标注同一坐标空间。所属页由扩展字段 标记。</summary>
public sealed class CanvasNode
{
    /// <summary>新建卡片的默认尺寸（页局部 PDF 点）：高约「2 行文字」，对齐 Obsidian 新建卡片的观感 （行高见 GpuPageView.CanvasLineHeight = 字号 × 1.35 ≈ 16.2，故 2 行标称 ≈ 32.4）。</summary>
    public const double DefaultWidth = 180;
    public const double DefaultHeight = 36;

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; } = DefaultWidth;
    [JsonPropertyName("height")] public double Height { get; set; } = DefaultHeight;
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }

    /// <summary>扩展字段：节点所属页（0-based）。-1 = 无归属，任何页都不绘制（防御字段丢失的数据）。不参与 jsoncanvas 语义。</summary>
    [JsonPropertyName("glancePage")]
    public int GlancePage { get; set; } = -1;
}

/// <summary>白板连线。端点是节点的【四边中点】，fromSide/toSide 由渲染侧自动吸附最近边后写回。</summary>
public sealed class CanvasEdge
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("fromNode")] public string FromNode { get; set; } = "";
    [JsonPropertyName("fromSide")] public string? FromSide { get; set; }
    [JsonPropertyName("fromEnd")] public string? FromEnd { get; set; }
    [JsonPropertyName("toNode")] public string ToNode { get; set; } = "";
    [JsonPropertyName("toSide")] public string? ToSide { get; set; }
    [JsonPropertyName("toEnd")] public string? ToEnd { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
}

public sealed class CanvasDoc
{
    /// <summary>格式标记：本程序写的文件一定带它。读取时没有就不认（防止把别人的同名文件当白板），
    /// 写入时目标存在却没有它就不覆盖（防止毁掉别人的文件）。</summary>
    [JsonPropertyName("glance")] public CanvasFormatMark? Glance { get; set; }

    [JsonPropertyName("nodes")] public List<CanvasNode> Nodes { get; set; } = new();
    [JsonPropertyName("edges")] public List<CanvasEdge> Edges { get; set; } = new();

    /// <summary>页面上的笔迹（画笔模式的墨）。与 nodes/edges 并列：同一份画布承载"白板卡片"与"页上墨迹"
    /// 两类内容，两者都是"页局部坐标 + 页号"，只是绘制视图不同（阅读视图画墨，白板视图画卡片）。
    /// 一笔跨页时按页切成多段，段之间默认相连（渲染时用一条直线跨过页间空隙）。</summary>
    [JsonPropertyName("ink")] public List<CanvasInk> Ink { get; set; } = new();
}

/// <summary>一条笔画。整条挂在【发起页】上：全部点用那一页的局部坐标，允许越出页框
/// （负数、超页高都是正常轨迹）—— 跨页、穿过页间空隙、越过左右页边，都只是同一条线的自然延伸。
/// 于是不需要按页切开，也不需要任何事后补线；整条共用同一套页变换，
/// 旋转页面、切换单页/双页/网格时都不会被拉出假线。</summary>
public sealed class CanvasInk
{
    [JsonPropertyName("color")] public string Color { get; set; } = "#F2C200";
    [JsonPropertyName("width")] public double Width { get; set; } = 3.5;

    /// <summary>发起页（0-based）。-1 = 无归属（数据损坏的防御值，任何页都不画）。</summary>
    [JsonPropertyName("page")] public int Page { get; set; } = -1;

    /// <summary>扁平点串 [x0,y0,x1,y1,…]，页局部未旋转 PDF 点，可越出页框。</summary>
    [JsonPropertyName("pts")] public List<double> Pts { get; set; } = new();

    /// <summary>旧格式残留（曾是"按页切段"，一条笔有 N 段）。**只在读回旧文件时出现、写入永不产出**；
    /// 读到就每段各还原成一笔（不跨页连接），旧内容因此不会凭空消失。</summary>
    [JsonPropertyName("segments")] public List<CanvasInkSegment>? LegacySegments { get; set; }
}

/// <summary>旧格式里的一段（见 <see cref="CanvasInk.LegacySegments"/>）：段自带页号与点串。
/// 点同样是【页局部未旋转 PDF 点】的扁平数组 —— 不存旋转量，页面旋转是文档级视图状态（随 ViewState 保存），
/// 换算进页局部坐标时已把旋转逆算回去，于是同一组点在 0/90/180/270 度下都落在同一处内容上，
/// 存了反而会与当时的视图状态打架。</summary>
public sealed class CanvasInkSegment
{
    /// <summary>本段所属页（0-based）。-1 = 无归属（数据损坏的防御值，任何页都不画）。</summary>
    [JsonPropertyName("page")] public int Page { get; set; } = -1;

    [JsonPropertyName("pts")] public List<double> Pts { get; set; } = new();
}

/// <summary>格式标记内容。format 用来拒绝"未来版本写的"文件：老程序宁可当它不存在，
/// 也不要用旧模型去解析新格式、再把结果写回去毁掉它。</summary>
public sealed class CanvasFormatMark
{
    /// <summary>当前写出的格式号。改字段语义时递增，并在读取侧按需接受旧号。
    /// 2 = 增加 ink（页面笔迹，整条挂发起页）；1 = 只有 nodes/edges。旧文件照常读，ink 为空。</summary>
    public const int CurrentFormat = 2;

    [JsonPropertyName("format")] public int Format { get; set; } = CurrentFormat;
}

/// <summary>画布写入失败的原因。提示里只分两句：<see cref="TargetReadOnly"/> 是用户自己能立刻解决的
/// （去掉只读属性就能继续写原件），其余一律说成"无法写入"—— 被占用 / 权限不足 / 磁盘满 / 不是我们的文件，
/// 对用户是同一件事：这次写不进去，改用副本。
/// 值分得比提示细，是为了让每个判定点各自归因清楚，不必在调用处反推"为什么失败"。</summary>
public enum CanvasSaveError
{
    None,
    /// <summary>路径或文档为空（调用方没准备好，属于程序内部错误）。</summary>
    Invalid,
    /// <summary>目标文件存在但不带本程序的格式标记：拒绝覆盖。</summary>
    ForeignFile,
    /// <summary>目标文件带只读属性：去掉属性即可继续写，是用户自己能立刻解决的一种。</summary>
    TargetReadOnly,
    /// <summary>写入抛异常：被占用、权限不足、磁盘已满等。</summary>
    WriteFailed,
}

/// <summary>画布文件的磁盘存取。每份画布都是库里的独立文件（<see cref="AppPaths.CanvasDirectory"/> 之下，
/// 允许任意层子目录），与任何 PDF 都不绑定：同一份画布可以盖到任意文档上，
/// 所以读写都要显式给出【画布自己的路径】——这里没有任何"由 PDF 路径推出画布路径"的推导。
/// 为何不用 .canvas：那是 Obsidian Canvas 的扩展名，同名 PDF 旁的笔记会被误当白板加载并整份重写，
/// 而 Obsidian 反过来也会丢掉本程序的 glancePage 字段（卡片全变无归属 ⇒ 白板看起来整块消失）。</summary>
public static class CanvasStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>画布文件扩展名。刻意不用 .canvas —— 那是 Obsidian 的扩展名，会让两家的文件在同目录互相踩。</summary>
    public const string Extension = ".gcanvas";

    /// <summary>读取一份画布。文件不存在、解析失败、或没有 glance 格式标记
    /// （= 不是本程序写的）都返回 null。</summary>
    public static CanvasDoc? LoadFile(string? canvasPath)
    {
        if (string.IsNullOrEmpty(canvasPath)) return null;
        return TryLoadFile(canvasPath!);
    }

    private static CanvasDoc? TryLoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path);
            using var json = JsonDocument.Parse(text);
            if (!HasFormatMark(json.RootElement)) return null;
            return json.RootElement.Deserialize<CanvasDoc>(JsonOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>顶层是否有本程序的格式标记。判断用 JsonDocument 而不是反序列化结果：反序列化丢掉了
    /// "这个键到底存不存在"的信息，无法区分"没有标记"与"标记被解析成默认值"。
    /// 标记里没有 format 的（手工加过标记的文件）按旧号放行；format 高于当前号的不认，
    /// 读写两侧共用这一份判断 —— 写侧也要拦住"未来版本的文件"，否则会拿旧模型覆盖掉它。</summary>
    private static bool HasFormatMark(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("glance", out var mark) || mark.ValueKind != JsonValueKind.Object) return false;
        if (!mark.TryGetProperty("format", out _)) return true;
        CanvasFormatMark? parsed;
        try { parsed = mark.Deserialize<CanvasFormatMark>(JsonOptions); }
        catch (Exception) { return false; }
        return parsed != null && parsed.Format <= CanvasFormatMark.CurrentFormat;
    }

    /// <summary>写回一份画布。先写临时文件再原子替换；目标文件已存在但不是本程序写的
    /// （没有格式标记）就拒绝覆盖，返回 false —— 调用方据此保留脏标记，绝不毁掉别人的文件。</summary>
    public static bool SaveFile(string? canvasPath, CanvasDoc doc)
        => SaveFile(canvasPath, doc, out _);

    /// <summary>同上，额外给出失败原因：调用方要拿它决定是"换个文件名再写一次"（只读 / 不是我们的文件）
    /// 还是"这个位置根本写不进去"（占用 / 权限 / 磁盘满）。</summary>
    public static bool SaveFile(string? canvasPath, CanvasDoc doc, out CanvasSaveError error)
    {
        error = CanvasSaveError.None;
        if (string.IsNullOrEmpty(canvasPath) || doc == null) { error = CanvasSaveError.Invalid; return false; }
        doc.Glance ??= new CanvasFormatMark();   // 本程序写的文件必须带标记
        return TrySaveFile(canvasPath!, doc, out error);
    }

    private static bool TrySaveFile(string path, CanvasDoc doc, out CanvasSaveError error)
    {
        error = CanvasSaveError.None;
        // 临时文件先声明在外面：写成功、替换失败时它必须被删掉，否则库里会攒下一堆 .tmp。
        string tmp = path + ".tmp";
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(path))
            {
                // 0 字节一律放行：FileSavePicker 在用户点"保存"时就先把占位文件建出来了，
                // 里面没有任何内容，"覆盖"也就谈不上。不放过这一种，另存为会因为自己刚建的空壳
                // 被判成"不是本程序的文件"而永远写不进去 —— 文件出现了，界面却说无法写入。
                // 目标已存在且确实有内容、却没有我们的格式标记 ⇒ 不是本程序的文件，绝不覆盖。
                if (new FileInfo(path).Length > 0 && !HasFormatMarkOnDisk(path))
                {
                    error = CanvasSaveError.ForeignFile;
                    return false;
                }
                // 只读单独归因、且在建临时文件【之前】判掉：这是用户自己能立刻解决的一种
                // （右键→属性→取消只读），混进"被占用/磁盘满"里他就不知道该干什么了。
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                {
                    error = CanvasSaveError.TargetReadOnly;
                    return false;
                }
            }
            // 先写临时文件再原子替换，避免写入中途崩溃留下半截 JSON。
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOptions));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            error = CanvasSaveError.WriteFailed;
            try { File.Delete(tmp); } catch { }
            return false;
        }
    }

    private static bool HasFormatMarkOnDisk(string path)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return HasFormatMark(json.RootElement);
        }
        catch (Exception)
        {
            // 读不了/不是 JSON：同样按"不是我们的文件"处理，宁可换路径也不覆盖。
            return false;
        }
    }
}