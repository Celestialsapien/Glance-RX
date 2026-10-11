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

/// <summary>
/// 白板节点。字段名对齐 jsoncanvas 1.0（camelCase），坐标是【页局部未旋转 PDF 点】，
/// 即与画笔标注同一坐标空间。所属页由扩展字段 <see cref="GlancePage"/> 标记。
/// </summary>
public sealed class CanvasNode
{
    /// <summary>新建卡片的默认尺寸（页局部 PDF 点）：高约「2 行文字」，对齐 Obsidian 新建卡片的观感
    /// （行高见 GpuPageView.CanvasLineHeight = 字号 × 1.35 ≈ 16.2，故 2 行 ≈ 32）。
    /// 视图侧（双击落点按中心对齐）与模型侧（建节点）共用同一常量，避免两处硬编码漂移。</summary>
    public const double DefaultWidth = 180;
    public const double DefaultHeight = 32;

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("width")] public double Width { get; set; } = DefaultWidth;
    [JsonPropertyName("height")] public double Height { get; set; } = DefaultHeight;
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }

    /// <summary>扩展字段：节点所属页（0-based）。-1 = 未指定（读取外部 .canvas 时）。不参与 jsoncanvas 语义。</summary>
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

/// <summary>一份白板文档（对应单个 &lt;pdf&gt;.canvas 文件）。</summary>
public sealed class CanvasDoc
{
    [JsonPropertyName("nodes")] public List<CanvasNode> Nodes { get; set; } = new();
    [JsonPropertyName("edges")] public List<CanvasEdge> Edges { get; set; } = new();
}

/// <summary>
/// 白板数据的磁盘存取。主路径与 PDF 同目录（&lt;pdf&gt;.canvas，便于与 Obsidian 互操作），
/// 目录只读/无权限时回退到 GlanceData/Canvases 下（便携版约束：绝不写 C 盘 AppData）。
/// </summary>
public static class CanvasStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>主路径：与 PDF 同目录、同名的 .canvas。</summary>
    public static string GetPrimaryPath(string pdfPath) => Path.ChangeExtension(pdfPath, ".canvas");

    /// <summary>回退路径：GlanceData/Canvases 下按完整路径散列命名，避免同名 PDF 相撞。</summary>
    public static string GetFallbackPath(string pdfPath)
    {
        string dir = Path.Combine(AppPaths.DataDirectory, "Canvases");
        string name = Path.GetFileNameWithoutExtension(pdfPath) ?? "canvas";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(pdfPath);
        return Path.Combine(dir, $"{name}.{hash:X8}.canvas");
    }

    /// <summary>读取白板。主路径优先，其次回退路径；都不存在或解析失败返回 null。</summary>
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
            var doc = JsonSerializer.Deserialize<CanvasDoc>(File.ReadAllText(path), JsonOptions);
            return doc ?? null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>保存白板。先写主路径，失败（只读目录/无权限）再落回退路径。返回实际写入成功。</summary>
    public static bool Save(string pdfPath, CanvasDoc doc)
    {
        if (string.IsNullOrEmpty(pdfPath) || doc == null) return false;
        if (TrySaveFile(GetPrimaryPath(pdfPath), doc)) return true;
        return TrySaveFile(GetFallbackPath(pdfPath), doc);
    }

    private static bool TrySaveFile(string path, CanvasDoc doc)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
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
}