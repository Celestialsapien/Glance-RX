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
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace GlanceRX.Services;

/// <summary>一份 PDF 的文档级数据：阅读位置（含缩放/布局/旋转/侧边栏/窗口几何）+ 书签。
/// 两者都是"按 PDF 索引"的数据，所以住在同一个条目里 —— 一个文件、一次写入、不会互相覆盖。</summary>
public sealed class PdfDocumentState
{
    public ViewStateData? ViewState { get; set; }

    public List<PdfBookmark>? Bookmarks { get; set; }
}

/// <summary>所有 PDF 的文档级数据集中存进 `GlanceData/documents.json`，键 = PDF 完整路径。
///
/// 为什么合成一个文件：阅读位置和书签是同一份文档的两种数据，分开存就要在两个地方各自
/// 「读盘 → 改自己那份 → 整份写回」，两个写入者写同一个文件时后写的那次会把先写的改动抹掉。
/// 合成一个内存字典 + 一把锁之后，任何一次修改都只动自己那条字段。
///
/// ⚠ 键是路径字符串：PDF 改名/移动后就对不上了（这是当前实现已知的边界，不是 bug 修完的状态）。
/// ⚠ 解析失败一律返回空，不做"损坏就重建"以外的补救 —— 别为了容错把用户的旧数据写坏。</summary>
public static class DocumentStore
{
    private static readonly string FilePath = AppPaths.DocumentsFilePath;

    /// <summary>所有读写共用的唯一锁。文件小（几十条 PDF 也就几十 KB），整段持锁最简单可靠。</summary>
    private static readonly object Gate = new();

    /// <summary>内存镜像，null = 还没从磁盘读过。读入一次后所有读写都走它，避免每次操作都解析整份文件。</summary>
    private static Dictionary<string, PdfDocumentState>? _data;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 中文路径原样写进文件，否则 System.Text.Json 默认会转义成 \uXXXX，用户打开文件看不懂。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- 阅读位置 ----

    public static ViewStateData? LoadViewState(string pdfPath)
    {
        if (string.IsNullOrEmpty(pdfPath)) return null;
        lock (Gate)
        {
            return Data().TryGetValue(pdfPath, out var state) ? state.ViewState : null;
        }
    }

    public static void SaveViewState(string pdfPath, ViewStateData data)
    {
        if (string.IsNullOrEmpty(pdfPath) || data == null) return;
        lock (Gate)
        {
            Entry(Data(), pdfPath).ViewState = data;
            Flush(Data());
        }
    }

    // ---- 书签 ----

    /// <summary>读回书签。返回深拷贝：调用方（侧边栏列表）会就地增删改，直接给缓存对象的话，
    /// 用户取消编辑也会"改掉"盘上的内容。</summary>
    public static Task<List<PdfBookmark>> LoadBookmarksAsync(string pdfPath)
    {
        if (string.IsNullOrEmpty(pdfPath)) return Task.FromResult(new List<PdfBookmark>());
        lock (Gate)
        {
            var list = Data().TryGetValue(pdfPath, out var state) ? state.Bookmarks : null;
            var copy = list == null
                ? new List<PdfBookmark>()
                : list.Select(b => new PdfBookmark { PageIndex = b.PageIndex, DisplayName = b.DisplayName }).ToList();
            return Task.FromResult(copy);
        }
    }

    public static Task SaveBookmarksAsync(string pdfPath, List<PdfBookmark> bookmarks)
    {
        if (string.IsNullOrEmpty(pdfPath) || bookmarks == null) return Task.CompletedTask;
        lock (Gate)
        {
            Entry(Data(), pdfPath).Bookmarks = bookmarks
                .Where(b => b != null && b.PageIndex >= 0)
                .Select(b => new PdfBookmark { PageIndex = b.PageIndex, DisplayName = (b.DisplayName ?? "").Trim() })
                .ToList();
            Flush(Data());
        }
        return Task.CompletedTask;
    }

    // ---- 内部 ----

    /// <summary>取（必要时建）某个 PDF 的条目。⚠ 必须在锁内调用。</summary>
    private static PdfDocumentState Entry(Dictionary<string, PdfDocumentState> data, string pdfPath)
    {
        if (data.TryGetValue(pdfPath, out var state) && state != null) return state;
        state = new PdfDocumentState();
        data[pdfPath] = state;
        return state;
    }

    /// <summary>内存镜像（首次访问时从磁盘读入）。⚠ 必须在锁内调用。</summary>
    private static Dictionary<string, PdfDocumentState> Data()
    {
        return _data ??= LoadFromDisk();
    }

    private static Dictionary<string, PdfDocumentState> LoadFromDisk()
    {
        // 路径比较不区分大小写：Windows 上 d:\a.pdf 与 D:\A.PDF 是同一份文档，不该各记一条。
        var data = new Dictionary<string, PdfDocumentState>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(FilePath)) return data;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, PdfDocumentState>>(
                File.ReadAllText(FilePath), JsonOptions);
            if (loaded == null) return data;
            foreach (var kv in loaded)
            {
                if (kv.Value == null) continue;
                data[kv.Key] = kv.Value;
            }
        }
        catch (Exception)
        {
            // 读不出来/解析失败：当作空表继续（后续写入会重建），不抛给调用方 ——
            // 阅读位置和书签读不到最多是体验退回默认值，绝不能因此让打开文档失败。
        }
        return data;
    }

    /// <summary>整份写盘：先写临时文件再原子替换。写到一半崩掉若留下半截 JSON，
    /// 这个文件里**所有** PDF 的阅读位置和书签会一起读不出来。返回是否写成功。</summary>
    private static bool Flush(Dictionary<string, PdfDocumentState> data)
    {
        try
        {
            string json = JsonSerializer.Serialize(data, JsonOptions);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            try { File.Delete(FilePath + ".tmp"); } catch (Exception) { }
            return false;
        }
    }
}

/// <summary>阅读位置/缩放/窗口几何。存在该 PDF 自己的条目里（`documents.json` 的 ViewState 字段）。
/// 还原顺序见各字段注释。</summary>
public class ViewStateData
{
    public double ZoomPercent { get; set; }

    public double HorizontalOffset { get; set; }

    public double VerticalOffset { get; set; }

    public int PageIndex { get; set; }

    /// <summary>文档旋转角（0/90/180/270）。影响布局坐标系，必须与偏移一同还原。</summary>
    public int RotationDegrees { get; set; }

    /// <summary>连续布局方向：0=纵向单页连续，1=横向单页连续。须在偏移之前还原， 否则保存的偏移按错轴(横/纵)落定 → 还原后位置偏离。缺字段时按 0=纵向（向后兼容）。</summary>
    public int LayoutMode { get; set; }

    /// <summary>纵向布局下的列数/排版：0=单页，1=双页对开，2=书籍对开。仅纵向布局生效； 横向胶片条忽略。须在偏移之前还原（与 LayoutMode 同理）。缺字段时按 0=单页（向后兼容）。</summary>
    public int Arrangement { get; set; }

    /// <summary>退出时侧边栏（缩略图/书签/大纲）是否展开。重开同一 PDF 时按此状态打开。 缺字段时按 false（收起），与 SplitView 的 XAML 默认值一致。</summary>
    public bool SidebarOpen { get; set; }

    /// <summary>上次盖在这个文档上的画布文件绝对路径（空 = 没盖）。这只是"上次用了哪份"的便利记忆，
    /// 不是绑定关系：随时可换；画布被删/改名后这里指向的文件不存在 ⇒ 退回空画布，画布数据本身不受影响。</summary>
    public string CanvasPath { get; set; } = "";

    /// <summary>退出时右侧画布面板是否展开。与 SidebarOpen 同样语义：重开同一 PDF 时按此状态打开。缺字段按 false。</summary>
    public bool CanvasPanelOpen { get; set; }

    /// <summary>保存时刻（UTC Unix 秒），用于诊断与未来按时间排序。</summary>
    public long SavedAtUtc { get; set; }

    /// <summary>窗口状态：0=未记录（走默认右半屏），1=Normal，2=Maximized。 取值对齐 Sumatra 的 WIN_STATE_*（sumatrapdf-master/src/AppSettings.h:5-10）；</summary>
    public int WindowState { get; set; }

    /// <summary>窗口矩形（屏幕物理像素，与 AppWindow.Position/Size、MoveAndResize 同一坐标系）。 恒为【非最大化】矩形：最大化时存 GetWindowPlacement 的 rcNormalPosition，下次先按正常 尺寸摆好再最大化，</summary>
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
}

public sealed class PdfBookmark
{
    public int PageIndex { get; set; }
    public string DisplayName { get; set; } = "";
}
