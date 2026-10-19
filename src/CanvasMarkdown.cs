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
using System.Text;

namespace GlanceRX;

/// <summary>行内 Markdown 的样式类别（P0 子集）。</summary>
internal enum CanvasMarkdownSpanKind
{
    Bold,
    Italic,
    Strike,
    Code,
    Highlight,
}

/// <summary>应用在【显示串】上的一个区间样式（Start/Length 均以显示串为坐标）。</summary>
internal readonly struct CanvasMarkdownSpan
{
    public CanvasMarkdownSpan(int start, int length, CanvasMarkdownSpanKind kind)
    {
        Start = start;
        Length = length;
        Kind = kind;
    }

    public int Start { get; }
    public int Length { get; }
    public CanvasMarkdownSpanKind Kind { get; }
}

/// <summary>解析结果：显示串（已去掉成对的标记符号）+ 区间样式表 + 显示下标→源串下标映射。</summary>
internal sealed class CanvasMarkdownResult
{
    public CanvasMarkdownResult(string displayText, IReadOnlyList<CanvasMarkdownSpan> spans,
        IReadOnlyList<int>? sourceIndexMap = null)
    {
        DisplayText = displayText;
        Spans = spans;
        SourceIndexMap = sourceIndexMap;
    }

    public string DisplayText { get; }
    public IReadOnlyList<CanvasMarkdownSpan> Spans { get; }

    /// <summary>显示下标 → 源串下标（长度 = DisplayText.Length + 1，末项 = 源串长度）。
    /// null = 两者 1:1（没有剔除任何字符），调用方直接用下标即可。
    /// 用途：双击落点是在【渲染后】的文字上量的，换算成编辑文档（原文）偏移时必经此映射。</summary>
    public IReadOnlyList<int>? SourceIndexMap { get; }
}

/// <summary>
/// 卡片文字的行内 Markdown 子集解析（纯函数，无状态、无依赖）。
///
/// 支持的语法（刻意的最小集，卡片语境够用）：
///   **粗体**   *斜体*   ***粗斜***   ~~删除线~~   `行内代码`   ==高亮==
///   \* 转义；标记可嵌套（**a *b* c**）；未配对的标记【原样保留】，不吞字符。
///
/// 刻意【不】支持：
///   · 下划线 _斜体_ —— snake_case / 文件名会被误判成强调，得不偿失；
///   · 块级语法（标题 / 列表 / 任务框）—— 它们会改变行高，属 P2，另行处理；
///   · 链接 —— 一个 CanvasTextLayout 只有一种前景色，链接无法单独着色。
///
/// 核心不变式：**不含标记时 DisplayText 与原文逐字相同** ⇒ 排版结果与未启用本功能时完全一致，
/// 因此"没写 Markdown 的卡片进/出编辑依旧零重排"这条要求由本文件保证。
/// </summary>
internal static class CanvasMarkdown
{
    private const char EscapeChar = '\\';

    private enum TokenKind
    {
        Bold,
        Italic,
        BoldItalic,
        Strike,
        Highlight,
        Code,
    }

    public static CanvasMarkdownResult Parse(string? source)
    {
        string text = source ?? "";
        if (text.Length == 0) return new CanvasMarkdownResult("", Array.Empty<CanvasMarkdownSpan>());
        if (!MayContainMarkup(text)) return new CanvasMarkdownResult(text, Array.Empty<CanvasMarkdownSpan>());

        int n = text.Length;
        var skip = new bool[n];      // 不进入显示串（成对定界符、被消费的转义反斜杠）
        var literal = new bool[n];   // 被转义的字符：照常显示，但不参与匹配
        var inCode = new bool[n];    // 行内代码正文：不参与强调匹配

        MarkEscapes(text, skip, literal);

        var spans = new List<(int Start, int End, TokenKind Kind)>();
        MarkCodeSpans(text, skip, literal, inCode, spans);

        var tokens = CollectDelimiters(text, skip, literal, inCode);
        MatchDelimiters(text, tokens, skip, spans, out _);

        return Build(text, skip, spans);
    }

    /// <summary>
    /// 【编辑态专用】只算样式区间，<b>不改动文本</b>（标记照常显示，光标/选区的字符下标与文档 1:1）。
    /// 与阅读态的关键差别：**未闭合的标记也生效，效果一直带到文末**（Obsidian 的"单边语法立刻看得见"），
    /// 后续补上闭合标记时自然收回成成对区间。
    /// </summary>
    public static IReadOnlyList<CanvasMarkdownSpan> ParseStyling(string? source)
    {
        string text = source ?? "";
        if (text.Length == 0 || !MayContainMarkup(text)) return Array.Empty<CanvasMarkdownSpan>();

        int n = text.Length;
        var skip = new bool[n];
        var literal = new bool[n];
        var inCode = new bool[n];

        MarkEscapes(text, skip, literal);
        var spans = new List<(int Start, int End, TokenKind Kind)>();
        MarkCodeSpans(text, skip, literal, inCode, spans);

        var tokens = CollectDelimiters(text, skip, literal, inCode);
        MatchDelimiters(text, tokens, skip, spans, out var unclosed);

        // 单边语法：还没闭合的开标记，内容从"标记之后"一直延伸到文末。
        foreach (var o in unclosed)
        {
            int start = o.Pos + o.Len;
            if (start < n) spans.Add((start, n, o.Kind));
        }

        var list = new List<CanvasMarkdownSpan>(spans.Count);
        foreach (var s in spans) AddStyled(list, s.Start, s.End - s.Start, s.Kind);
        return list;
    }

    private static bool MayContainMarkup(string text)
    {
        foreach (char c in text)
        {
            if (c == '*' || c == '~' || c == '=' || c == '`' || c == EscapeChar) return true;
        }
        return false;
    }

    private static bool IsMarker(char c) => c is '*' or '~' or '=' or '`' or EscapeChar;

    private static void MarkEscapes(string text, bool[] skip, bool[] literal)
    {
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != EscapeChar || !IsMarker(text[i + 1])) continue;
            skip[i] = true;
            literal[i + 1] = true;
            i++;   // 被转义的字符不再当标记看
        }
    }

    /// <summary>行内代码优先识别：反引号成对、正文逐字保留（内部不再解析任何标记）。</summary>
    private static void MarkCodeSpans(string text, bool[] skip, bool[] literal, bool[] inCode,
        List<(int Start, int End, TokenKind Kind)> spans)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '`' || skip[i] || literal[i]) continue;

            int close = -1;
            for (int j = i + 1; j < text.Length; j++)
            {
                if (text[j] == '`' && !skip[j] && !literal[j])
                {
                    close = j;
                    break;
                }
            }
            if (close < 0) break;   // 未闭合：整段保留字面

            skip[i] = true;
            skip[close] = true;
            for (int k = i + 1; k < close; k++) inCode[k] = true;
            if (close > i + 1) spans.Add((i + 1, close, TokenKind.Code));
            i = close;
        }
    }

    private static List<(int Pos, int Len, TokenKind Kind)> CollectDelimiters(
        string text, bool[] skip, bool[] literal, bool[] inCode)
    {
        var tokens = new List<(int Pos, int Len, TokenKind Kind)>();
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if (skip[i] || literal[i] || inCode[i] || (c != '*' && c != '~' && c != '='))
            {
                i++;
                continue;
            }

            int len = 1;
            while (i + len < text.Length && text[i + len] == c
                   && !skip[i + len] && !literal[i + len] && !inCode[i + len])
            {
                len++;
            }

            if (c == '*')
            {
                if (len == 1) { tokens.Add((i, 1, TokenKind.Italic)); i += 1; }
                else if (len == 2) { tokens.Add((i, 2, TokenKind.Bold)); i += 2; }
                else { tokens.Add((i, 3, TokenKind.BoldItalic)); i += 3; }
                continue;
            }
            if (c == '~' && len >= 2) { tokens.Add((i, 2, TokenKind.Strike)); i += 2; continue; }
            if (c == '=' && len >= 2) { tokens.Add((i, 2, TokenKind.Highlight)); i += 2; continue; }

            i += len;   // 单个 ~ / = 不是标记，跳过整段
        }
        return tokens;
    }

    /// <summary>
    /// 定界符配对：先按"同类已开则闭合、否则开启"的栈规则匹配。
    /// 两侧空白不算有效标记（`a * b * c` 不会变成斜体），未配对的开标记原样留在文本里。
    /// 嵌套顺序不合法时（如 **a *b** c*）在冲突点一并收尾，保证不产生越界区间。
    /// </summary>
    private static void MatchDelimiters(string text, List<(int Pos, int Len, TokenKind Kind)> tokens,
        bool[] skip, List<(int Start, int End, TokenKind Kind)> spans,
        out List<(int Pos, int Len, TokenKind Kind)> unclosed)
    {
        var open = new List<(int Pos, int Len, TokenKind Kind)>();
        foreach (var t in tokens)
        {
            int after = t.Pos + t.Len;
            bool canOpen = after < text.Length && !char.IsWhiteSpace(text[after]);
            bool canClose = t.Pos > 0 && !char.IsWhiteSpace(text[t.Pos - 1]);

            int hit = -1;
            for (int k = open.Count - 1; k >= 0; k--)
            {
                if (open[k].Kind == t.Kind)
                {
                    hit = k;
                    break;
                }
            }

            if (hit >= 0 && canClose)
            {
                for (int k = open.Count - 1; k > hit; k--) AddSpan(skip, spans, open[k], t.Pos, t);
                AddSpan(skip, spans, open[hit], t.Pos, t);
                open.RemoveRange(hit, open.Count - hit);
            }
            else if (canOpen)
            {
                open.Add(t);
            }
        }
        unclosed = open;
    }

    /// <summary>收下一对定界符：内容非空才剔掉两侧标记并记样式；内容为空（如 <c>****</c>）
    /// 则整对保留字面，避免把卡片文字吞成空白。</summary>
    private static void AddSpan(bool[] skip, List<(int Start, int End, TokenKind Kind)> spans,
        (int Pos, int Len, TokenKind Kind) opener, int contentEnd, (int Pos, int Len, TokenKind Kind) closer)
    {
        int start = opener.Pos + opener.Len;
        if (contentEnd <= start) return;
        for (int i = opener.Pos; i < opener.Pos + opener.Len; i++) skip[i] = true;
        for (int i = closer.Pos; i < closer.Pos + closer.Len; i++) skip[i] = true;
        spans.Add((start, contentEnd, opener.Kind));
    }

    /// <summary>构造显示串，同时把"源串索引 → 显示串索引"的映射记下来，供区间换算与双击落点反查。</summary>
    private static CanvasMarkdownResult Build(string text, bool[] skip,
        List<(int Start, int End, TokenKind Kind)> spans)
    {
        var map = new int[text.Length + 1];
        var sb = new StringBuilder(text.Length);
        bool anySkip = false;
        for (int i = 0; i < text.Length; i++)
        {
            map[i] = sb.Length;
            if (skip[i]) { anySkip = true; continue; }
            sb.Append(text[i]);
        }
        map[text.Length] = sb.Length;

        // 反查表：显示下标 → 源串下标。只有"真的剔掉了字符"才需要（否则 1:1，省一次分配）。
        IReadOnlyList<int>? inverse = null;
        if (anySkip)
        {
            var inv = new int[sb.Length + 1];
            for (int i = 0; i < text.Length; i++)
                if (!skip[i]) inv[map[i]] = i;
            inv[sb.Length] = text.Length;
            inverse = inv;
        }

        if (spans.Count == 0) return new CanvasMarkdownResult(sb.ToString(), Array.Empty<CanvasMarkdownSpan>(), inverse);

        var list = new List<CanvasMarkdownSpan>(spans.Count);
        foreach (var s in spans)
        {
            int start = map[s.Start];
            AddStyled(list, start, map[s.End] - start, s.Kind);
        }
        return new CanvasMarkdownResult(sb.ToString(), list, inverse);
    }

    /// <summary>把一对区间（起始 + 长度）按样式种类展开成 0~2 条 span。</summary>
    private static void AddStyled(List<CanvasMarkdownSpan> list, int start, int len, TokenKind kind)
    {
        if (len <= 0) return;
        switch (kind)
        {
            case TokenKind.Bold:
                list.Add(new CanvasMarkdownSpan(start, len, CanvasMarkdownSpanKind.Bold));
                break;
            case TokenKind.Italic:
                list.Add(new CanvasMarkdownSpan(start, len, CanvasMarkdownSpanKind.Italic));
                break;
            case TokenKind.BoldItalic:
                list.Add(new CanvasMarkdownSpan(start, len, CanvasMarkdownSpanKind.Bold));
                list.Add(new CanvasMarkdownSpan(start, len, CanvasMarkdownSpanKind.Italic));
                break;
            case TokenKind.Strike:
                list.Add(new CanvasMarkdownSpan(start, len, CanvasMarkdownSpanKind.Strike));
                break;
            case TokenKind.Highlight:
                list.Add(new CanvasMarkdownSpan(start, len, CanvasMarkdownSpanKind.Highlight));
                break;
            case TokenKind.Code:
                list.Add(new CanvasMarkdownSpan(start, len, CanvasMarkdownSpanKind.Code));
                break;
        }
    }
}
