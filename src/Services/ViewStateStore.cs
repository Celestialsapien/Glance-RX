using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Glance.Services;

/// <summary>
/// 借鉴 SumatraPDF：把【所有 PDF】的阅读位置/缩放集中存进【单个 txt 文件】。
/// 路径在程序本体目录（AppContext.BaseDirectory，即 Glance.exe 同级），适配便携版——绝不写 C 盘 AppData。
/// 按 PDF 完整路径作键；重开同一 PDF 时精确还原视图（缩放/滚动偏移/旋转）。
/// </summary>
public static class ViewStateStore
{
    private static readonly string FilePath = Path.Combine(
        AppContext.BaseDirectory, "GlanceViewStates.txt");

    // 用 UnsafeRelaxedJsonEscaping 保留非 ASCII 字符（如中文文件名）原样写入，
    // 否则 System.Text.Json 默认会把它们转义成 \uXXXX，导致 GlanceViewStates.txt 里中文文件名不可读。
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static Dictionary<string, ViewStateData> LoadAll()
    {
        try
        {
            if (!File.Exists(FilePath)) return new Dictionary<string, ViewStateData>();
            string json = File.ReadAllText(FilePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, ViewStateData>>(json);
            return dict ?? new Dictionary<string, ViewStateData>();
        }
        catch (Exception)
        {
            return new Dictionary<string, ViewStateData>();
        }
    }

    private static void SaveAll(Dictionary<string, ViewStateData> dict)
    {
        try
        {
            string json = JsonSerializer.Serialize(dict, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch { }
    }

    public static void Save(string pdfPath, ViewStateData data)
    {
        if (string.IsNullOrEmpty(pdfPath)) return;
        var dict = LoadAll();
        dict[pdfPath] = data;
        SaveAll(dict);
    }

    public static ViewStateData? Load(string pdfPath)
    {
        if (string.IsNullOrEmpty(pdfPath)) return null;
        var dict = LoadAll();
        return dict.TryGetValue(pdfPath, out var v) ? v : null;
    }
}

/// <summary>单个 PDF 的阅读视图快照。</summary>
public class ViewStateData
{
    /// <summary>缩放百分比（= ZoomFactor * 100）。</summary>
    public double ZoomPercent { get; set; }

    /// <summary>水平滚动偏移（文档坐标，像素）。</summary>
    public double HorizontalOffset { get; set; }

    /// <summary>垂直滚动偏移（文档坐标，像素）。</summary>
    public double VerticalOffset { get; set; }

    /// <summary>当前页索引（0-based），仅供参考/缩略图高亮。</summary>
    public int PageIndex { get; set; }

    /// <summary>文档旋转角（0/90/180/270）。影响布局坐标系，必须与偏移一同还原。</summary>
    public int RotationDegrees { get; set; }

    /// <summary>连续布局方向：0=纵向单页连续，1=横向单页连续。须在偏移之前还原，
    /// 否则保存的偏移按错轴(横/纵)落定 → 还原后位置偏离。旧记录缺省 0=纵向，向后兼容。</summary>
    public int LayoutMode { get; set; }

    /// <summary>保存时刻（UTC Unix 秒），用于诊断与未来按时间排序。</summary>
    public long SavedAtUtc { get; set; }
}
