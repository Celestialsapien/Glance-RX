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
using System.Collections.ObjectModel;
using System.IO;

namespace GlanceRX.Services;

/// <summary>资源管理器树节点：一个文件夹，或一份画布。
/// 只带列表要显示的信息，不含卡片数据 —— 库里有几百份画布时，解析全部 JSON 会把"打开面板"变成一次全库读取。</summary>
public sealed class CanvasTreeNode : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>文件夹的目录路径 / 画布的文件路径。两者都唯一，也是右键菜单与打开的目标。</summary>
    public string FullPath { get; init; } = "";

    /// <summary>显示名：文件夹名，或画布文件名（不含扩展名）。子层级由树表达，名字里不再拼路径。</summary>
    public string Name { get; init; } = "";

    public bool IsFolder { get; init; }

    /// <summary>层级深度（库根下的第一层 = 0）。列表是拍平的，缩进由这一层算出来。</summary>
    public int Depth { get; init; }

    /// <summary>左侧缩进宽度（每级 12px）。</summary>
    public double IndentWidth => Depth * 12;

    private Microsoft.UI.Xaml.Media.Brush? _rowBackground;

    /// <summary>整行底色：空白 / 悬浮 / 按下 / 多选四种状态由界面写这一支 —— 不用框架的选中态：
    /// 框架的选中语义只认一个"当前项"，文件夹行与画布行分不开，也改不动。
    /// 注意这里【不含激活态】：激活改由行首竖条表达（见 IsActive），两种状态因此可以同时可见。</summary>
    public Microsoft.UI.Xaml.Media.Brush? RowBackground
    {
        get => _rowBackground;
        set
        {
            if (ReferenceEquals(_rowBackground, value)) return;
            _rowBackground = value;
            Raise(nameof(RowBackground));
        }
    }

    private bool _isActive;

    /// <summary>该行是不是"盖在 PDF 上的那份画布"（激活态）。由界面写 —— 与 RowBackground 同源，
    /// 两者表达两件不同的事：激活 = 行首那条强调色竖条，选中 = 整行淡染底色。刻意用两种视觉，
    /// 因为"开着哪份"和"勾中了哪几份"会同时存在，压成一种颜色就分不清了。</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            Raise(nameof(IsActive));
            Raise(nameof(ActiveBarMark));
            Raise(nameof(FileGlyph));
        }
    }

    /// <summary>行首竖条的显隐（只有激活行显示）。避开转换器：WinUI 没有内置的 bool→Visibility。</summary>
    public Microsoft.UI.Xaml.Visibility ActiveBarMark =>
        _isActive ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>文件图标：激活行用实心页（E729），其余用空心页（E7C3）。两者同一字形族、同一包围盒
    /// （都是 1.00em 高、宽高比 1.33），所以切换时图标的尺寸与位置都不动，差别只有"填满"这一件事
    /// —— 填充面积约 0.86 vs 0.29，比只改亮度多一个"形状级"的线索，缩进再深也看得见。</summary>
    public string FileGlyph => _isActive ? "\uE729" : "\uE7C3";

    private Microsoft.UI.Xaml.Media.Brush? _nameBrush;

    /// <summary>名字文字色，由界面按"是否激活"写两档。列满时整屏都是同一亮度的文字，
    /// 单靠行首竖条认不出正开着的是哪一份（层级深时竖条离名字还有几十像素），
    /// 所以再用一档亮度差把激活行拎出来 —— 与底色（选中）、竖条（激活标记）互不抢通道。</summary>
    public Microsoft.UI.Xaml.Media.Brush? NameBrush
    {
        get => _nameBrush;
        set
        {
            if (ReferenceEquals(_nameBrush, value)) return;
            _nameBrush = value;
            Raise(nameof(NameBrush));
        }
    }

    /// <summary>展开箭头（只有文件夹行有）。</summary>
    public Microsoft.UI.Xaml.Visibility ChevronMark =>
        IsFolder ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>展开箭头符号：展开时向下（E70D），收起时向右（E76C）。</summary>
    public string ChevronGlyph => _isExpanded ? "\uE70D" : "\uE76C";

    /// <summary>子节点（文件夹在前、再按名称，与 vscode 默认排序一致）。
    /// 用 ObservableCollection 而不是 List：磁盘变动只对"受影响的那一层"做增删补丁，
    /// 换成整表重建会把展开态、选中行和滚动位置全部打回原点。</summary>
    public ObservableCollection<CanvasTreeNode> Children { get; } = new();

    private bool _isExpanded;

    /// <summary>展开态。点整行与点箭头都改这里，子行的显隐、箭头方向、_expandedCanvasFolders 都跟着它走 ——
    /// 只有这一份真相，重建时按它恢复。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Raise(nameof(IsExpanded));
            Raise(nameof(ChevronGlyph));
        }
    }

    /// <summary>已提交删除、正在等磁盘结果。行集合跳过它（连同它的子树）⇒ 点下删除的那一刻这一行就消失，
    /// shell 的回收站操作在后台线程慢慢做。删除失败时界面把它清回 false，行会重新出现。
    /// 不用 INotifyPropertyChanged：它只被拍平列表读一次，界面从不绑定它。</summary>
    public bool IsPendingDelete { get; set; }

    private bool _isRenaming;

    /// <summary>该行正在行内改名：名字文本与输入框二选一显示（与书签同一套做法）。</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (_isRenaming == value) return;
            _isRenaming = value;
            Raise(nameof(IsRenaming));
            Raise(nameof(NameTextMark));
        }
    }

    /// <summary>名字文本的可见性（不在改名时显示）。避开 bool→Visibility 转换器：WinUI 没有内置的。</summary>
    public Microsoft.UI.Xaml.Visibility NameTextMark =>
        _isRenaming ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    private string _renameText = "";

    /// <summary>行内输入框里的临时文本。回车/失焦才落盘，Esc 丢弃。</summary>
    public string RenameText
    {
        get => _renameText;
        set
        {
            string text = value ?? "";
            if (string.Equals(_renameText, text, StringComparison.Ordinal)) return;
            _renameText = text;
            Raise(nameof(RenameText));
        }
    }

    /// <summary>文件夹图标与文件图标二选一（同样避开转换器）。</summary>
    public Microsoft.UI.Xaml.Visibility FolderIconMark =>
        IsFolder ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Visibility FileIconMark =>
        IsFolder ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    private void Raise(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>画布库：<see cref="AppPaths.CanvasDirectory"/> 下的全部 .gcanvas。
/// 这里没有任何"画布属于谁"的概念 —— 画布只是库里的文件，用哪份由用户当场决定，
/// 因此不存在改名/移动/复制导致对应关系失效的问题。</summary>
internal static class CanvasLibrary
{
    public static string Root => AppPaths.CanvasDirectory;

    /// <summary>建整棵树（供打开面板 / 换文档时用）。同层：先按手动顺序，未列出的按"文件夹在前、再按名称"。</summary>
    public static ObservableCollection<CanvasTreeNode> BuildTree(
        ICollection<string>? expanded = null,
        IReadOnlyDictionary<string, List<string>>? order = null)
    {
        var roots = new ObservableCollection<CanvasTreeNode>();
        SyncChildren(roots, Root, expanded, order);
        return roots;
    }

    /// <summary>把 <paramref name="dirPath"/> 的直接子项同步进 <paramref name="target"/>：删掉已不存在的、
    /// 补上新出现的、按顺序就位，已经存在的节点【原样保留】（连同它的子节点与展开态）。
    /// 这样外部改一个文件只让那一行变化，其余行与滚动位置都不动。
    /// <paramref name="order"/> = 手动顺序表（目录 → 子项名）；没排过的目录传 null / 缺键即可。
    /// <paramref name="depth"/> = 新节点的层级深度（缩进用），调用方按目录在树里的位置给。</summary>
    public static void SyncChildren(ObservableCollection<CanvasTreeNode> target, string dirPath,
                                    ICollection<string>? expanded,
                                    IReadOnlyDictionary<string, List<string>>? order = null,
                                    int depth = 0)
    {
        List<DirEntry> desired;
        try { desired = ScanChildren(dirPath, OrderOf(order, dirPath)); }
        catch (Exception) { return; }   // 目录读不到：保留现状，比清空列表更接近真相

        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (IndexOf(desired, target[i].FullPath) < 0) target.RemoveAt(i);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && SamePath(target[i].FullPath, desired[i].Path)) continue;

            int found = -1;
            for (int j = i + 1; j < target.Count; j++)
            {
                if (SamePath(target[j].FullPath, desired[i].Path)) { found = j; break; }
            }
            if (found >= 0)
            {
                var existing = target[found];
                target.RemoveAt(found);
                target.Insert(i, existing);
            }
            else
            {
                target.Insert(i, MakeNode(desired[i], expanded, order, depth));
            }
        }
    }

    private static IReadOnlyList<string>? OrderOf(
        IReadOnlyDictionary<string, List<string>>? order, string dirPath)
        => order != null && order.TryGetValue(dirPath, out List<string>? names) && names.Count > 0 ? names : null;

    /// <summary>某个目录的直接子项（文件夹在前、再按名称）。**空文件夹也会列出来** —— 建了却看不见等于没建。
    /// 传了手动顺序：列表里出现过的按它的名次排在前，其余（新建/没排过的）按默认规则接在后面。</summary>
    private static List<DirEntry> ScanChildren(string dirPath, IReadOnlyList<string>? order)
    {
        var list = new List<DirEntry>();
        foreach (string dir in Directory.EnumerateDirectories(dirPath))
            list.Add(new DirEntry(dir, Path.GetFileName(dir), true));
        foreach (string file in Directory.EnumerateFiles(dirPath, "*" + CanvasStore.Extension))
            list.Add(new DirEntry(file, Path.GetFileNameWithoutExtension(file), false));

        // 名次表用"文件/目录名（含扩展名）"，与落盘时的键一致；显示名会撞车（同名文件夹 + 同名画布），不能用。
        Dictionary<string, int>? rank = null;
        if (order is { Count: > 0 })
        {
            rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < order.Count; i++) rank[order[i]] = i;
        }

        list.Sort((a, b) =>
        {
            int ra = -1, rb = -1;
            bool hasA = rank != null && rank.TryGetValue(a.Key, out ra);
            bool hasB = rank != null && rank.TryGetValue(b.Key, out rb);
            if (hasA != hasB) return hasA ? -1 : 1;             // 排过的在前
            if (hasA && ra != rb) return ra.CompareTo(rb);
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            return CompareNatural(a.Name, b.Name);
        });
        return list;
    }

    /// <summary>默认排序的比较：先比"去掉重名序号后的基名"（保持中文按当前区域设置排），
    /// 基名相同再按序号的【数值】比。名字形如 报告 / 报告 (2) / 报告 (10) —— 逐字符比较会把
    /// "(10)" 排到 "(9)" 前面（'1' &lt; '9'），连续新建几份画布就会看到顺序"从 (9) 跳到 (10) 又乱回去"。</summary>
    private static int CompareNatural(string a, string b)
    {
        SplitOrdinalSuffix(a, out string stemA, out int ordinalA);
        SplitOrdinalSuffix(b, out string stemB, out int ordinalB);

        int cmp = string.Compare(stemA, stemB, StringComparison.CurrentCultureIgnoreCase);
        if (cmp != 0) return cmp;

        if (ordinalA < 0 && ordinalB < 0) return 0;   // 两个都是裸名：基名相同 ⇒ 同名
        if (ordinalA < 0) return -1;                  // 裸名排在 "基名 (2)" 之前
        if (ordinalB < 0) return 1;
        return ordinalA.CompareTo(ordinalB);
    }

    /// <summary>拆出 "基名 (序号)" 这种重名后缀。不是这个形状就原样返回基名、序号 = -1。
    /// 只认"末尾一对括号、括号里全是数字"，所以 "报告 (2024) 修订" 这类名字不会被误拆。</summary>
    private static void SplitOrdinalSuffix(string name, out string stem, out int ordinal)
    {
        stem = name;
        ordinal = -1;
        if (name.Length < 4 || name[name.Length - 1] != ')') return;

        int open = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (open <= 0) return;

        int digits = name.Length - open - 3;          // " (n)" 里 n 的位数
        if (digits < 1) return;
        for (int i = open + 2; i < name.Length - 1; i++)
        {
            if (name[i] < '0' || name[i] > '9') return;
        }
        if (!int.TryParse(name.AsSpan(open + 2, digits), out int n)) return;   // 位数多到溢出 int：当归一档更安全
        stem = name[..open];
        ordinal = n;
    }

    private static CanvasTreeNode MakeNode(DirEntry entry, ICollection<string>? expanded,
                                           IReadOnlyDictionary<string, List<string>>? order, int depth)
    {
        var node = new CanvasTreeNode
        {
            FullPath = entry.Path,
            Name = entry.Name,
            IsFolder = entry.IsFolder,
            Depth = depth,
        };
        if (!entry.IsFolder) return node;
        if (expanded != null && expanded.Contains(entry.Path)) node.IsExpanded = true;
        try
        {
            foreach (DirEntry child in ScanChildren(entry.Path, OrderOf(order, entry.Path)))
                node.Children.Add(MakeNode(child, expanded, order, depth + 1));
        }
        catch (Exception) { }
        return node;
    }

    private static int IndexOf(List<DirEntry> list, string path)
    {
        for (int i = 0; i < list.Count; i++)
            if (SamePath(list[i].Path, path)) return i;
        return -1;
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>一次扫描里的一行（只描述，不构造节点 —— 已存在的行不再重建）。</summary>
    private readonly struct DirEntry
    {
        public DirEntry(string path, string name, bool isFolder)
        {
            Path = path;
            Name = name;
            IsFolder = isFolder;
            Key = System.IO.Path.GetFileName(path);
        }

        public string Path { get; }

        /// <summary>显示名（画布去掉 .gcanvas）。</summary>
        public string Name { get; }

        /// <summary>磁盘上的真名（含扩展名）—— 手动顺序表以它为键：显示名里文件夹与画布可能重名。</summary>
        public string Key { get; }

        public bool IsFolder { get; }
    }

    /// <summary>在库里挑一个还没被占用的画布路径：名字.gcanvas / 名字 (2).gcanvas / …
    /// <paramref name="parentPath"/> 为 null = 库根。只挑路径不建文件 —— 用户可能只是想看一眼，不落空文件。</summary>
    private static string NewCanvasPath(string baseName, string? parentPath)
    {
        string dir = string.IsNullOrEmpty(parentPath) ? Root : parentPath!;
        try { Directory.CreateDirectory(dir); } catch (Exception) { }
        string candidate = Path.Combine(dir, baseName + CanvasStore.Extension);
        for (int i = 2; File.Exists(candidate) && i < 10000; i++)
            candidate = Path.Combine(dir, $"{baseName} ({i}){CanvasStore.Extension}");
        return candidate;
    }

    /// <summary>建一份空白画布并落盘，返回文件路径；写失败返回 null。
    /// 先落盘再交给界面：这样列表刷新能立刻看到它，也不会出现"内存里有一份、盘上没有"的悬空画布。
    /// 名字由调用方给（当前文档名或本地化的"未命名"）—— 库不猜名字，重名在这里加序号。</summary>
    public static string? CreateEmpty(string baseName, string? parentPath = null)
    {
        if (!IsNameValid(baseName)) return null;
        string path = NewCanvasPath(baseName.Trim(), parentPath);
        try
        {
            if (CanvasStore.SaveFile(path, new CanvasDoc())) return path;
        }
        catch (Exception) { }
        return null;
    }

    // ---------------- 资源管理器操作（新建文件夹 / 重命名 / 删除） ----------------

    /// <summary>在库里新建子文件夹，<paramref name="parentPath"/> 为 null = 库根。
    /// 重名时自动加序号（新建文件夹 / 新建文件夹 (2)，与资源管理器一致）：新建不进入行内改名，
    /// 名字当场就是最终的，所以更不能因为重名就失败一次。</summary>
    public static string? CreateFolder(string? parentPath, string baseName)
    {
        string parent = string.IsNullOrEmpty(parentPath) ? Root : parentPath!;
        if (!IsInsideRoot(parent) || !IsNameValid(baseName)) return null;
        string name = baseName.Trim();

        for (int i = 1; i < 10000; i++)
        {
            string path;
            try { path = Path.Combine(parent, i == 1 ? name : $"{name} ({i})"); }
            catch (Exception) { return null; }
            if (Directory.Exists(path) || File.Exists(path)) continue;
            try
            {
                Directory.CreateDirectory(path);
                return path;
            }
            catch (Exception) { return null; }
        }
        return null;
    }

    /// <summary>改名（文件或文件夹）。返回新路径；名字非法 / 目标已存在 / 容器目录本身不可改名时返回 null。
    /// 画布的扩展名由调用方补：用户敲 "笔记" 得到 笔记.gcanvas，敲 "笔记.gcanvas" 不会变成 笔记.gcanvas.gcanvas。</summary>
    public static string? Rename(string path, bool isFolder, string? newName)
    {
        if (string.IsNullOrEmpty(path) || !IsInsideRoot(path)) return null;
        if (string.Equals(path, Root, StringComparison.OrdinalIgnoreCase)) return null;   // 库根不可改名
        if (!IsNameValid(newName)) return null;

        string name = newName!.Trim();
        if (!isFolder && !name.EndsWith(CanvasStore.Extension, StringComparison.OrdinalIgnoreCase))
            name += CanvasStore.Extension;

        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent)) return null;
        string target = Path.Combine(parent, name);
        if (string.Equals(target, path, StringComparison.OrdinalIgnoreCase)) return path;    // 名字没变
        try
        {
            // 同层重名：文件和文件夹都算冲突（Windows 下本来就不允许同名共存）。
            if (Directory.Exists(target) || File.Exists(target)) return null;
            if (isFolder) Directory.Move(path, target);
            else File.Move(path, target);
            return target;
        }
        catch (Exception) { return null; }
    }

    /// <summary>删除（送回收站）。返回 false = 被拒绝/占用，调用方保留原状。</summary>
    public static bool Delete(string path)
    {
        if (string.IsNullOrEmpty(path) || !IsInsideRoot(path)) return false;
        if (string.Equals(path, Root, StringComparison.OrdinalIgnoreCase)) return false;
        return Interop.ShellFileOps.MoveToRecycleBin(path);
    }

    /// <summary>把一项搬到另一个目录（拖拽的"进文件夹"只在这一步动磁盘）。返回新路径；失败返回 null。
    /// 三种拒绝：目标目录不存在、目标已有同名项、把文件夹搬进它自己的子孙（否则整棵子树会消失）。</summary>
    public static string? MoveNode(string path, bool isFolder, string targetDir)
    {
        if (string.IsNullOrEmpty(path) || !IsInsideRoot(path)) return null;
        if (string.IsNullOrEmpty(targetDir) || !IsInsideRoot(targetDir)) return null;
        if (string.Equals(path, Root, StringComparison.OrdinalIgnoreCase)) return null;   // 库根不可搬
        if (!Directory.Exists(targetDir)) return null;
        if (isFolder && IsUnder(targetDir, path)) return null;                            // 不能进自己的子孙

        string name = Path.GetFileName(path);
        string dest; 
        try { dest = Path.Combine(targetDir, name); }
        catch (Exception) { return null; }
        if (SamePath(dest, path)) return null;                                            // 已经在那儿
        if (Directory.Exists(dest) || File.Exists(dest)) return null;                     // 同名占位
        try
        {
            if (isFolder) Directory.Move(path, dest);
            else File.Move(path, dest);
            return dest;
        }
        catch (Exception) { return null; }
    }

    /// <summary><paramref name="path"/> 是否落在 <paramref name="ancestor"/> 之下（不含相等）。</summary>
    private static bool IsUnder(string path, string ancestor)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(ancestor)) return false;
        string a = ancestor.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>名字合法性：非空、不含路径分隔符与 Windows 非法字符、不是 . / ..、长度不超过 100。
    /// 只校验名字本身 —— 路径由调用方给定，这里不做"从文本解析路径"的推断。</summary>
    public static bool IsNameValid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string n = name.Trim();
        if (n.Length > 100) return false;
        if (n is "." or "..") return false;
        foreach (char c in n)
        {
            if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar) return false;
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0) return false;
        }
        return true;
    }

    /// <summary>路径必须落在库根之内（含根自身）。用"根 + 分隔符"前缀比较，
    /// 否则 D:\...\Canvases2 会被误判成 Canvases 里面。</summary>
    public static bool IsInsideRoot(string path)
    {
        try
        {
            string root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true;
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }
}
