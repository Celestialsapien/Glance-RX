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
/// 【编辑态实时渲染】结果：可见串（按"展开/隐藏"决定后去掉了部分定界符）+ 显示串样式区间 +
/// 源串↔显示串的**双向**下标映射。
///
/// 与 <see cref="CanvasMarkdownResult"/> 的区别：那份是"一次性渲染"（标记全去），这份是"随光标演变的"——
/// 同一份文本在不同光标位置会得到不同的可见串，故两份映射都必须有：光标/选区是源串坐标，
/// 排版/命中是显示串坐标。
/// </summary>
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

    /// <summary>源串下标 → 显示串下标（长度 = 源串长度 + 1）。被隐藏的字符映射到"其后的第一个可见位置"。</summary>
    public int[] DocToDisplay { get; }

    /// <summary>显示串下标 → 源串下标（长度 = 显示串长度 + 1，末项 = 源串长度）。</summary>
    public int[] DisplayToDoc { get; }

    /// <summary>可见串与源串是否不同（= 真的隐藏了字符）。为 false 时两份映射都是恒等。</summary>
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

    /// <summary>显示下标 → 源下标。下标 0 不能被硬钳成 0：开头若有被隐藏的定界符，
    /// 显示串第 0 个字符对应的源位置其实是"标记之后"（如 <c>**粗体**</c> 的 2），
    /// 钳成 0 会让点在最左侧的落点跑到标记前面去。</summary>
    public int ToDoc(int displayIndex) =>
        displayIndex <= 0 ? DisplayToDoc[0]
        : displayIndex >= DisplayToDoc.Length ? DisplayToDoc[DisplayToDoc.Length - 1]
        : DisplayToDoc[displayIndex];
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

    /// <summary>一对已配平的定界符位置（源串坐标）。实时渲染靠它决定"这一对标记要不要显示"。</summary>
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

        var spans = new List<(int Start, int End, TokenKind Kind)>();
        MarkEscapes(text, skip, literal);
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

    /// <summary>
    /// 【编辑态实时渲染（Obsidian 式）】按"光标在哪"决定这一帧**显示哪些定界符**：
    ///   · 默认隐藏所有**成对**定界符（文字看起来就是渲染后的样子）；
    ///   · 光标/选区落在某一对内部 ⇒ 只展开那一对（前后两个标记都显示），便于就地修改；
    ///   · <paramref name="expandStart"/>..<paramref name="expandEnd"/> 覆盖到的对 ⇒ 整行展开（双击该行）；
    ///   · <paramref name="expandAll"/> ⇒ 全部展开（全选，与"全选才看得到全文标记"一致）。
    /// <b>未闭合的开标记永不隐藏</b>：它还不是"语法"，隐藏掉会让用户打了 <c>**</c> 就当场消失、没法继续输入；
    /// 但它的样式照旧延到文末（单边语法立刻看得见，与 <see cref="ParseStyling"/> 同一口径）。
    /// 反斜杠也保留显示（编辑态是"原文"，不该把转义符吃掉）。
    /// 不含任何成对标记时可见串与原文逐字相同 ⇒ 映射为恒等，"没写 Markdown 的卡片依旧零重排"。
    /// </summary>
    public static CanvasMarkdownLiveResult ParseLive(string? source,
        int activeStart, int activeEnd, int expandStart, int expandEnd, bool expandAll)
    {
        string text = source ?? "";
        int n = text.Length;
        if (n == 0) return CanvasMarkdownLiveResult.Identity("");
        if (!MayContainMarkup(text)) return CanvasMarkdownLiveResult.Identity(text);

        var skip = new bool[n];        // 与阅读态共用匹配器的入参（本方法不用它造显示串）
        var literal = new bool[n];     // 被转义的字符：照常显示、不参与匹配
        var inCode = new bool[n];      // 行内代码正文：不参与强调匹配
        var hide = new bool[n];        // 真正要从显示串里剔除的定界符字符

        // 转义：编辑态【不删反斜杠】（skip 只影响匹配，不参与隐藏）⇒ 与阅读态共用同一次扫描。
        MarkEscapes(text, skip, literal);

        var spans = new List<(int Start, int End, TokenKind Kind)>();
        var pairs = new List<DelimiterPair>();
        MarkCodeSpans(text, skip, literal, inCode, spans, pairs);

        var tokens = CollectDelimiters(text, skip, literal, inCode);
        MatchDelimiters(text, tokens, skip, spans, out var unclosed, pairs);

        // 单边语法：未闭合的开标记字面显示，但样式从标记之后一直带到文末。
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

    /// <summary>[s,e] 与 [a,b] 是否相交；a == b（折叠光标）按点判（点落在开闭标记之间也算"在里面"）。</summary>
    private static bool Touches(int s, int e, int a, int b)
    {
        if (a == b) return a >= s && a <= e;
        return a <= e && b >= s;
    }

    /// <summary>按 hide[] 造可见串，并同时造出【源→显示】【显示→源】两张映射与显示串坐标下的样式表。</summary>
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
            if (hide[i]) continue;               // 隐藏字符 → 映射到"其后的第一个可见位置"
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
            if (close < 0) break;   // 未闭合：整段保留字面

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

            i += len;   // 单个 ~ / = 不是标记，跳过整段
        }
        return tokens;
    }

    /// <summary>
    /// 定界符配对：先按"同类已开则闭合、否则开启"的栈规则匹配。
    /// 两侧空白不算有效标记（`a * b * c` 不会变成斜体），未配对的开标记原样留在文本里。
    /// <b>一个 closer 只闭合它自己那种类型的最近开标记</b>；夹在它里面、类型不同的开标记**直接作废**
    /// （当普通字符：不剔标记、不上样式、也不再参与后续配对）。这样处理的原因是交叉写法本身没有合法解释：
    /// `**测试*测试**` 里的 `*` 被 `**` 跨过去，标准实现（CommonMark）也只是把它当字面字符。
    /// ⚠ 旧实现会把它们"顺手收尾"，导致 `**==测试**` 的 `==` 被当成已闭合（标记被吃掉还上高亮）；
    /// 若改成"给它们算到打断点的单边样式"，又会造成编辑态多出一段阅读态没有的斜体（用户报告）——两者都不对。
    /// </summary>
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
                // （Parse / ParseStyling / ParseLive）看到的是同一份判定，绝不会一边有斜体一边没有。
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

    /// <summary>收下一对定界符：内容非空才剔掉两侧标记并记样式；内容为空（如 <c>****</c>）
    /// 则整对保留字面，避免把卡片文字吞成空白。</summary>
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
