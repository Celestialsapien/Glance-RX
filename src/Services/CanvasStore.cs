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
}

/// <summary>格式标记内容。format 用来拒绝"未来版本写的"文件：老程序宁可当它不存在，
/// 也不要用旧模型去解析新格式、再把结果写回去毁掉它。</summary>
public sealed class CanvasFormatMark
{
    /// <summary>当前写出的格式号。改字段语义时递增，并在读取侧按需接受旧号。</summary>
    public const int CurrentFormat = 1;

    [JsonPropertyName("format")] public int Format { get; set; } = CurrentFormat;
}

/// <summary>白板数据的磁盘存取。主路径与 PDF 同目录同名、扩展名 .gcanvas；目录只读/无权限时回退到
/// GlanceData/Canvases 下（便携版约束：绝不写 C 盘 AppData）。
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

    /// <summary>白板文件扩展名。刻意不用 .canvas —— 那是 Obsidian 的扩展名，会让两家的文件在同目录互相踩。</summary>
    public const string Extension = ".gcanvas";

    public static string GetPrimaryPath(string pdfPath) => Path.ChangeExtension(pdfPath, Extension);

    /// <summary>回退路径：GlanceData/Canvases 下按完整路径散列命名，避免同名 PDF 相撞。</summary>
    public static string GetFallbackPath(string pdfPath)
    {
        string dir = Path.Combine(AppPaths.DataDirectory, "Canvases");
        string name = Path.GetFileNameWithoutExtension(pdfPath) ?? "canvas";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(pdfPath);
        return Path.Combine(dir, $"{name}.{hash:X8}{Extension}");
    }

    /// <summary>读取白板。主路径优先，其次回退路径；文件不存在、解析失败、或没有 glance 格式标记
    /// （= 不是本程序写的）都返回 null。</summary>
    public static CanvasDoc? Load(string pdfPath)
    {
        if (string.IsNullOrEmpty(pdfPath)) return null;
        var doc = TryLoadFile(GetPrimaryPath(pdfPath));
        return doc ?? TryLoadFile(GetFallbackPath(pdfPath));
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

    /// <summary>保存白板。先写主路径，失败（只读目录/无权限/目标被别人的文件占着）再落回退路径。
    /// 返回实际写入成功。</summary>
    public static bool Save(string pdfPath, CanvasDoc doc)
    {
        if (string.IsNullOrEmpty(pdfPath) || doc == null) return false;
        doc.Glance ??= new CanvasFormatMark();   // 本程序写的文件必须带标记
        if (TrySaveFile(GetPrimaryPath(pdfPath), doc)) return true;
        return TrySaveFile(GetFallbackPath(pdfPath), doc);
    }

    private static bool TrySaveFile(string path, CanvasDoc doc)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 目标已存在却没有我们的格式标记 ⇒ 不是本程序的文件，绝不覆盖（改由调用方落回退路径）。
            if (File.Exists(path) && !HasFormatMarkOnDisk(path)) return false;
            // 先写临时文件再原子替换，避免写入中途崩溃留下半截 JSON。
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOptions));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
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