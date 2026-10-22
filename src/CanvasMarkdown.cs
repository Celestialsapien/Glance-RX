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

internal enum CanvasMarkdownSpanKind
{
    Bold,
    Italic,
    Strike,
    Code,
    Highlight,
}

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

    /// <summary>显示下标 → 源串下标（长度 = DisplayText.Length + 1，末项 = 源串长度）。 null = 两者 1:1（没有剔除任何字符），调用方直接用下标即可。 用途：双击落点是在【渲染后】的文字上量的，换算成编辑文档（原文）偏移时必经此映射。</summary>
    public IReadOnlyList<int>? SourceIndexMap { get; }
}

/// 【编辑态实时渲染】结果：可见串（按"展开/隐藏"决定后去掉了部分定界符）+ 显示串样式区间 + 源串↔显示串的**双向**下标映射。
/// 同一份文本在不同光标位置会得到不同的可见串，故两份映射都必须有：光标/选区是源串坐标，
internal sealed class CanvasMarkdownLiveResult
{
    internal CanvasMarkdownLiveResult(string displayText, IReadOnlyList<CanvasMarkdownSpan> spans,
        int[] docToDisplay, int[] displayToDoc, bool changed)
    {
        DisplayText = displayText;
        Spans = spans;
        DocToDisplay = docToDisplay;
        DisplayToDoc = displayToDoc;
        Changed = changed;
    }

    public string DisplayText { get; }
    public IReadOnlyList<CanvasMarkdownSpan> Spans { get; }

    public int[] DocToDisplay { get; }

    public int[] DisplayToDoc { get; }

    public bool Changed { get; }

    public static CanvasMarkdownLiveResult Identity(string text)
    {
        int n = text.Length;
        var map = new int[n + 1];
        for (int i = 0; i <= n; i++) map[i] = i;
        return new CanvasMarkdownLiveResult(text, Array.Empty<CanvasMarkdownSpan>(), map, map, false);
    }

    public int ToDisplay(int docIndex) =>
        docIndex <= 0 ? 0 : docIndex >= DocToDisplay.Length ? DisplayText.Length : DocToDisplay[docIndex];

    /// <summary>显示下标 → 源下标。下标 0 不能被硬钳成 0：开头若有被隐藏的定界符， 显示串第 0 个字符对应的源位置其实是"标记之后"（如 **粗体** 的 2）， 钳成 0 会让点在最左侧的落点跑到标记前面去。</summary>
    public int ToDoc(int displayIndex) =>
        displayIndex <= 0 ? DisplayToDoc[0]
        : displayIndex >= DisplayToDoc.Length ? DisplayToDoc[DisplayToDoc.Length - 1]
        : DisplayToDoc[displayIndex];
}

/// 卡片文字的行内 Markdown 子集解析（纯函数，无状态、无依赖）。
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

    private readonly struct DelimiterPair
    {
        public DelimiterPair(int openPos, int openLen, int closePos, int closeLen)
        {
            OpenPos = openPos;
            OpenLen = openLen;
            ClosePos = closePos;
            CloseLen = closeLen;
        }

        public int OpenPos { get; }
        public int OpenLen { get; }
        public int ClosePos { get; }
        public int CloseLen { get; }
        public int OpenEnd => OpenPos + OpenLen;
        public int CloseEnd => ClosePos + CloseLen;
    }

    public static CanvasMarkdownResult Parse(string? source)
    {
        string text = source ?? "";
        if (text.Length == 0) return new CanvasMarkdownResult("", Array.Empty<CanvasMarkdownSpan>());
        if (!MayContainMarkup(text)) return new CanvasMarkdownResult(text, Array.Empty<CanvasMarkdownSpan>());

        int n = text.Length;
        var skip = new bool[n];
        var literal = new bool[n];
        var inCode = new bool[n];

        MarkEscapes(text, skip, literal);

        var spans = new List<(int Start, int End, TokenKind Kind)>();
        MarkCodeSpans(text, skip, literal, inCode, spans);

        var tokens = CollectDelimiters(text, skip, literal, inCode);
        MatchDelimiters(text, tokens, skip, spans, out _);

        return Build(text, skip, spans);
    }

    /// <summary>【编辑态专用】只算样式区间，不改动文本（标记照常显示，光标/选区的字符下标与文档 1:1）。 与阅读态的关键差别：**未闭合的标记也生效，效果一直带到文末**（Obsidian 的"单边语法立刻看得见"）， 后续补上闭合标记时自然收回成成对区间。</summary>
    public static IReadOnlyList<CanvasMarkdownSpan> ParseStyling(string? source)
    {
        string text = source ?? "";
        if (text.Length == 0 || !MayContainMarkup(text)) return Array.Empty<CanvasMarkdownSpan>();

        int n = text.Length;
        var skip = new bool[n];
        var literal = new bool[n];
        var inCode = new bool[n];

        var spans = new List<(int Start, int End, TokenKind Kind)>();
        MarkEscapes(text, skip, literal);
        MarkCodeSpans(text, skip, literal, inCode, spans);

        var tokens = CollectDelimiters(text, skip, literal, inCode);
        MatchDelimiters(text, tokens, skip, spans, out var unclosed);

        foreach (var o in unclosed)
        {
            int start = o.Pos + o.Len;
            if (start < n) spans.Add((start, n, o.Kind));
        }

        var list = new List<CanvasMarkdownSpan>(spans.Count);
        foreach (var s in spans) AddStyled(list, s.Start, s.End - s.Start, s.Kind);
        return list;
    }

    /// 【编辑态实时渲染（Obsidian 式）】按"光标在哪"决定这一帧**显示哪些定界符**： · 默认隐藏所有**成对**定界符（文字看起来就是渲染后的样子）；
    public static CanvasMarkdownLiveResult ParseLive(string? source,
        int activeStart, int activeEnd, int expandStart, int expandEnd, bool expandAll)
    {
        string text = source ?? "";
        int n = text.Length;
        if (n == 0) return CanvasMarkdownLiveResult.Identity("");
        if (!MayContainMarkup(text)) return CanvasMarkdownLiveResult.Identity(text);

        var skip = new bool[n];
        var literal = new bool[n];
        var inCode = new bool[n];
        var hide = new bool[n];

        // 转义：编辑态【不删反斜杠】（skip 只影响匹配，不参与隐藏）⇒ 与阅读态共用同一次扫描。
        MarkEscapes(text, skip, literal);

        var spans = new List<(int Start, int End, TokenKind Kind)>();
        var pairs = new List<DelimiterPair>();
        MarkCodeSpans(text, skip, literal, inCode, spans, pairs);

        var tokens = CollectDelimiters(text, skip, literal, inCode);
        MatchDelimiters(text, tokens, skip, spans, out var unclosed, pairs);

        foreach (var o in unclosed)
        {
            int start = o.Pos + o.Len;
            if (start < n) spans.Add((start, n, o.Kind));
        }

        foreach (var p in pairs)
        {
            if (IsRevealed(p, activeStart, activeEnd, expandStart, expandEnd, expandAll)) continue;
            for (int i = p.OpenPos; i < p.OpenEnd; i++) hide[i] = true;
            for (int i = p.ClosePos; i < p.CloseEnd; i++) hide[i] = true;
        }

        return BuildLive(text, hide, spans);
    }

    /// <summary>这一对定界符这一帧要不要展开显示。选区可能是折叠光标（start == end）⇒ 用闭区间判。</summary>
    private static bool IsRevealed(DelimiterPair p, int activeStart, int activeEnd,
        int expandStart, int expandEnd, bool expandAll)
    {
        if (expandAll) return true;
        if (Touches(p.OpenPos, p.CloseEnd, activeStart, activeEnd)) return true;
        return Touches(p.OpenPos, p.CloseEnd, expandStart, expandEnd);
    }

    private static bool Touches(int s, int e, int a, int b)
    {
        if (a == b) return a >= s && a <= e;
        return a <= e && b >= s;
    }

    private static CanvasMarkdownLiveResult BuildLive(string text, bool[] hide,
        List<(int Start, int End, TokenKind Kind)> spans)
    {
        int n = text.Length;
        var docToDisplay = new int[n + 1];
        bool anyHidden = false;
        for (int i = 0; i < n; i++) if (hide[i]) { anyHidden = true; break; }

        if (!anyHidden)
        {
            var identity = new int[n + 1];
            for (int i = 0; i <= n; i++) identity[i] = i;
            var same = new List<CanvasMarkdownSpan>(spans.Count);
            foreach (var s in spans) AddStyled(same, s.Start, s.End - s.Start, s.Kind);
            return new CanvasMarkdownLiveResult(text, same, identity, identity, false);
        }

        var sb = new StringBuilder(n);
        // 反查表按【显示串长度 + 1】建，不能按源串长度：逻辑长度必须与可见串一致，
        // 否则 ToDoc 会把越界下标当成合法（末尾那几个空位全是 0，光标会飞回行首）。
        var displayToDoc = new List<int>(n + 1);
        for (int i = 0; i < n; i++)
        {
            docToDisplay[i] = sb.Length;
            if (hide[i]) continue;
            displayToDoc.Add(i);
            sb.Append(text[i]);
        }
        docToDisplay[n] = sb.Length;
        displayToDoc.Add(n);

        var list = new List<CanvasMarkdownSpan>(spans.Count);
        foreach (var s in spans)
        {
            int start = docToDisplay[s.Start];
            AddStyled(list, start, docToDisplay[s.End] - start, s.Kind);
        }
        return new CanvasMarkdownLiveResult(sb.ToString(), list, docToDisplay, displayToDoc.ToArray(), true);
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
        List<(int Start, int End, TokenKind Kind)> spans, List<DelimiterPair>? pairs = null)
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
            if (close < 0) break;

            skip[i] = true;
            skip[close] = true;
            pairs?.Add(new DelimiterPair(i, 1, close, 1));
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

            i += len;
        }
        return tokens;
    }

    /// 定界符配对：先按"同类已开则闭合、否则开启"的栈规则匹配。
    /// ⚠ 不能把它们"顺手收尾"，否则 `**==测试**` 的 `==` 被当成已闭合（标记被吃掉还上高亮）；
    private static void MatchDelimiters(string text, List<(int Pos, int Len, TokenKind Kind)> tokens,
        bool[] skip, List<(int Start, int End, TokenKind Kind)> spans,
        out List<(int Pos, int Len, TokenKind Kind)> unclosed, List<DelimiterPair>? pairs = null)
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
                // 中间的异类开标记一律丢弃（不 AddSpan、不记 pair、也不再入栈）⇒ 三个入口
                AddSpan(skip, spans, open[hit], t.Pos, t, pairs);
                open.RemoveRange(hit, open.Count - hit);
            }
            else if (canOpen)
            {
                open.Add(t);
            }
        }
        unclosed = open;
    }

    /// <summary>收下一对定界符：内容非空才剔掉两侧标记并记样式；内容为空（如 ****） 则整对保留字面，避免把卡片文字吞成空白。</summary>
    private static void AddSpan(bool[] skip, List<(int Start, int End, TokenKind Kind)> spans,
        (int Pos, int Len, TokenKind Kind) opener, int contentEnd, (int Pos, int Len, TokenKind Kind) closer,
        List<DelimiterPair>? pairs = null)
    {
        int start = opener.Pos + opener.Len;
        if (contentEnd <= start) return;
        pairs?.Add(new DelimiterPair(opener.Pos, opener.Len, closer.Pos, closer.Len));
        for (int i = opener.Pos; i < opener.Pos + opener.Len; i++) skip[i] = true;
        for (int i = closer.Pos; i < closer.Pos + closer.Len; i++) skip[i] = true;
        spans.Add((start, contentEnd, opener.Kind));
    }

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
