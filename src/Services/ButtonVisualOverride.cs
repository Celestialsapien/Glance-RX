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

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace GlanceRX.Services;

/// <summary>
/// 接管普通功能按钮（<see cref="Button"/>，非 <see cref="ToggleButton"/>）的视觉输出 —— 与
/// <c>ToggleButtonVisualOverride</c> 同源（共享 <see cref="VisualOverrideBase{TButton}"/>），
/// 专治「鼠标松开后指针一动没动，按钮底色却先跳一档再跳回来」。
/// </summary>
/// <remarks>
/// <para><b>取色定案（09-29，用户拍板）：功能按钮 = 「永不勾选的 ToggleButton」。</b></para>
/// <para>本类只写 <c>ToggleButtonBackground/Foreground/BorderBrush</c> 的<b>未选中三档</b>
/// （空后缀 / PointerOver / Pressed），绝不触碰 <c>*Checked*</c> 档 —— 那三档在 App.xaml 主题字典里
/// 是 Obsidian 紫（强调色），功能按钮没有选中态、不需要强调色，在紫色处分支即「永不进入 Checked 档」。
/// 未选中三档与选中态按钮静止时逐键同源（深色 = ControlFill 半透明三档、随亚克力；浅色 = 纯白三档，
/// 用户已验收），主题切换、RefreshAll 防闪白全部复用既有通路。</para>
///
/// <para><b>为什么绝不写 <c>Button*</c> 键（09-29 截图实测翻车，勿改回）</b>：<c>ButtonBackground*</c>
/// 在 generic.xaml:355-366 是 <c>StaticResource</c> 别名；App 顶层字典里躺着 generic 提升上来的同名实例，
/// 深色下取到的是错误主题档 ⇒ 9 颗按钮「样式改变 + 常驻颜色判断不了」。这与
/// <c>ToggleButtonVisualOverride.Resource()</c> 注释里记过的「顶层 = generic 提升的 Light 档」
/// 是<b>同一个坑</b>——当年 ToggleButton* 靠搬进 App.xaml 主题字典解决，Button* 键不归我们定义，
/// 正解就是根本不用它、改用已验收的 ToggleButton* 未选中档。</para>
///
/// <para><b>机制（与 ToggleButtonVisualOverride 相同，皆由 VisualOverrideBase 实现）</b>：先以
/// <c>ButtonNoStateMachineStyle</c>（复刻默认模板、去掉 VisualStateGroups 与 BackgroundTransition）干掉原生状态机，
/// 再独占写 Background/Foreground/BorderBrush。<b>普通按钮的 hover 必须用坐标几何判据</b>：ButtonBase 松手时
/// <c>ReleasePointerCaptureInternal()</c> 补发一次假 PointerExited（约 380ms 后圆谎），框架
/// <c>IsPointerOver</c> 短暂为 false ⇒ 原生状态机把 Pressed→Normal→PointerOver 跳一帧。
/// 本类不信任何框架悬停标记，全部用指针落点几何判断。</para>
///
/// <para><b>例外：浮层 8 颗的 hover 走「面板独占」而非各颗 PointerExited</b>（10-02 定案）。
/// 各颗的 <c>PointerExited</c> 在 popup/flyout 输入上下文切换时常被 WinUI 吞掉 ⇒ 旧项 <c>_hover</c> 卡 true、
/// 与新项同时亮（悬浮色残留）。故改由菜单面板（<c>ZoomPresetPanel</c>，这片区域唯一稳定收到指针事件的
/// 元素）的 <c>PointerMoved</c> 实时算「指针落在哪颗内」，经 <see cref="SetFlyoutHover"/> 只点亮那一颗；
/// 指针离开整片面板经 <see cref="ClearFlyoutHoverStates"/> 全灭。只用<b>元素相对坐标</b>
/// （<c>e.GetCurrentPoint(item)</c>，同一可视树内），不碰跨树的 <c>TransformToVisual(null)</c>。</para>
///
/// <para><b>取值坑（改这里必看）</b>：取值顺序必须「主题字典 → Default → 顶层兜底」，且主题字典无
/// "Dark" 键（暗色值在 "Default" 里），<c>ActualTheme</c> 异步传播会闪旧白底 ⇒ 用
/// <c>ResolvedThemeDictKey()</c> 确定性主题源（沿视觉树取 MainPage 的 RequestedTheme）。
/// <c>ToggleButton*</c> 键定义在 App.xaml 主题字典里，走第一优先级即命中。</para>
///
/// <para><b>不碰的东西</b>：模板、边框线宽、圆角、Padding、字体、布局、焦点 visuals 一律不动。</para>
///
/// <para><b>适用范围</b>：标准外观功能按钮 + 对比视图开/关 PDF 的两颗 pill（09-30 用户否决透明描边变体，
/// 改走「整颗变色」同源逻辑）。<b>10-02 补齐</b>缩放胶囊 4 颗与缩放预设浮层 8 颗：
/// 缩放胶囊 4 颗 10-02 起与对比视图两颗统一（静止同样带底），<b>10-04 再改为静止完全透明</b> ——
/// 胶囊是亚克力（透 PDF），静止档要与胶囊<b>完全一致、看不到圆边界</b>（用户定案）；
/// 于是 <c>transparentAtRest</c> 覆盖两类：<b>缩放预设浮层 8 颗</b>（菜单项语义：静止不该有独立底色）
/// 与 <b>缩放胶囊 4 颗</b>（并入亚克力胶囊）。二者 hover 的事件来源不同，由 <c>inPopupTree</c> 区分。
    /// 透明静止档 —— 静止档底色写缓存的 <c>TransparentBrush</c>
    /// （不可见但仍可命中；<b>绝不是 null</b> —— null 背景的 ContentPresenter 整片不参与命中测试，
    /// 只有文字能被点中，10-02 实测），只给 hover / pressed 上色（菜单项语义：静止不该有独立底色）。
    /// 仍排除：背景本身是内容（颜色胶囊 / 线宽胶囊）、书签模板内按钮
    /// （DataTemplate 无法 x:Name 引用）—— 这两类用户已确认现状满意，接管会破坏其设计或取不到字段。
    /// ⚠ 欢迎页 OpenPdfButton 自 10-05 起已纳入 <c>accent</c> 模式接管（见 <see cref="Apply"/> 的 <c>_accent</c>
    /// 分支 + App.xaml 的 <c>GlanceAccentButtonStyle</c> 已去 VSM），不再是排除项。</para>
/// </remarks>
internal class ButtonVisualOverride : VisualOverrideBase<Button>
{
    /// <summary>接管指定按钮的视觉输出。应在控件模板已应用之后调用（例如 Page.Loaded）。</summary>
    /// <param name="transparentAtRest">true = 静止必须保持透明的按钮（缩放预设浮层 8 颗 + 缩放胶囊 4 颗）：
    /// 浮层 8 颗是菜单项语义（静止不该有独立底色）；缩放胶囊 4 颗是要<b>完全并入亚克力胶囊</b>、静止时
    /// 看不到按钮与胶囊的圆边界（10-04 用户定案）。两者静止档写法一致，但 hover 的事件来源不同
    /// （见 <paramref name="inPopupTree"/>）。
    /// 静止（既非悬停也非按下）时底色写缓存的 <c>TransparentBrush</c>、边框写 null，
    /// 前景写静止档前景刷（<c>ToggleButtonForeground</c>）；只有 hover / pressed 两档才写已验收的
    /// <c>ToggleButton*PointerOver</c> / <c>ToggleButton*Pressed</c>（整颗变色，与功能按钮同源）。
    /// ⚠ 底色绝不能写 null：null 背景的 ContentPresenter 整片不参与命中测试，只有文字能被命中
    ///   ⇒ hover / click 只在文字附近生效、扫过文字后才整片可点（10-02 实测）。
    ///   <c>TransparentBrush</c> 不可见但可命中，才是这里的正确写法。
    /// ⚠ 前景也绝不能写 null —— null 是「本地值 = 无画刷」，文字会整段消失（10-02 实测浮层静止全不可见）。
    /// 09-30 已否决这类按钮的「描边变体」（丑 + 点击无反馈），故不走描边。</param>
    /// <param name="inPopupTree">true = 该按钮活在<b>另一棵 Xaml 树</b>（popup / flyout 内容，目前只有缩放预设浮层 8 颗）。
    /// 只影响 hover 的<b>事件来源</b>：这类按钮会被整页几何驱动跳过（跨树 <c>GetCurrentPoint</c> 坐标不可靠，10-03 实证），
    /// 改由它所在容器的 PointerMoved 驱动。
    /// ⚠ 与「静止档是否透明」<b>正交，不要混用</b>：缩放胶囊 4 颗静止档同样是透明的，但它在<b>主可视树</b>
    /// ⇒ 必须传 <b>false</b>，否则整页驱动跳过它、悬浮色残留立刻回归。</param>
    public static void Attach(Button button, bool transparentAtRest = false, bool inPopupTree = false, bool accent = false)
    {
        if (button == null) return;
        new Link(button, transparentAtRest, inPopupTree, accent).Attach();
    }

    // 透明刷：静止档拿它（而非 null）填 ContentPresenter.Background。
    // ⚠ WinUI 命中测试：null 背景的 ContentPresenter 整片不参与命中，只有文字（TextBlock）能被命中
    //   ⇒ hover / click 只在文字附近生效，扫过文字把整片涂上色后才可点（10-02 实测）。
    //   Transparent 刷「不可见但仍可命中」⇒ 整片矩形响应，且不会涂成不透明底。
    private static readonly Brush TransparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    /// <summary>浮层关闭/展开时强清预设项的 hover：WinUI 在 Flyout/light-dismiss 关闭瞬间吞掉
    /// PointerExited ⇒ <c>_hover</c> 卡 true ⇒ 悬浮色残留、下次展开仍亮。
    /// ⚠ 只清<b>活在 popup 独立树</b>的项（<see cref="VisualOverrideBase{TButton}.BaseLink.ExcludeFromPageHoverDrive"/> = true，
    /// 即缩放预设浮层 8 颗），<b>不清缩放胶囊的 ZoomValue</b>：ZoomValue 也是 <c>transparentAtRest</c>（10-04 并入亚克力胶囊），
    /// 但它在<b>主树</b>且菜单展开期间指针就停在它上面，强清会让它「按下→悬浮→静止→(移动后)悬浮」地闪一跳
    /// （10-05 实测 ZoomValue bug）。胶囊项由整页几何驱动自维护，强清反而破坏它。其余功能按钮 hover 由常态指针事件维护，更不在此列。</summary>
        public static void ClearFlyoutHoverStates()
        {
            foreach (var link in _instances)
                if (link.ExcludeFromPageHoverDrive) link.ClearTransparentHoverState();
        }

    /// <summary>某颗按钮因钳制 / 布局改变发生位移后调用：把它立即清回静止，并开启 hover 冷处理窗口
    /// （默认 900ms，仅由<b>它自己的</b>真实 PointerMoved 提前解除）。
    /// <para>⚠ 必须用"窗口"而不是"清一次"：<c>ButtonBase</c> 松手时补发假 PointerExited，
    /// <b>约 380ms 后</b>再补一次 <c>PointerEntered</c>（带松手时的旧坐标）"圆谎"。按钮已滑出光标下方，
    /// 那个迟到事件仍按旧坐标判定"在按钮里"⇒ 重新点亮，且几何驱动只在真实移动时重算 ⇒ 残留到用户动鼠标。
    /// 零时长的 <c>true→false</c> 挡不住它（首次修复因此无效）。</para>
    /// <para>⚠ 传的必须是<b>被按下的那一颗</b>：假事件只发给它自己，否则邻居按钮会被无谓禁亮一个窗口期。
    /// 不传 / 传未接管的按钮（如颜色圆点）则为空操作。</para></summary>
    public static void SuppressHoverFor(ButtonBase? clicked) => BeginHoverHoldOffFor(clicked);

    /// <summary><b>权威指派</b>某颗按钮的 hover 状态（几何由调用方按<b>实时指针位置</b>算好）。
    /// <para>用途：元素因布局变化<b>滑到静止指针下方</b>时，WinUI 不会主动派发 PointerEntered/Moved
    /// ⇒ 几何驱动没机会重算，光效要等它某次迟到的重新命中测试才亮。此入口让调用方（已知指针未动）
    /// 在布局落定后补判一次。<b>不受冷处理窗口约束</b>（窗口只针对可能带旧坐标的迟到事件）。</para>
    /// <para>不属于本类接管的按钮（如实色圆点）为空操作。</para></summary>
    public static void ForceHover(Button? button, bool hover)
    {
        if (button == null) return;
        foreach (var link in _instances)
        {
            if (!ReferenceEquals(link.Button, button)) continue;
            link.ForceHover(hover);
            return;
        }
    }

    /// <summary>浮层预设项「独占 hover」：只让 <paramref name="hovered"/> 那一颗亮，其余
    /// <c>transparentAtRest</c>（= 浮层 8 颗）一律灭。<b>由菜单面板自身的 PointerMoved 实时驱动</b>
    /// （面板是这片区域里唯一稳定收到指针事件的元素），从根本上不依赖会被 WinUI 在 popup/flyout
    /// 输入上下文切换时吞掉的 <c>PointerExited</c> —— 杜绝「旧项残留亮着」。
    /// 传 <c>null</c> 表示指针不在任何一颗上（落在凹缝 / 菜单外）⇒ 全灭。</summary>
    public static void SetFlyoutHover(Button? hovered)
    {
        foreach (var link in _instances) link.SetTransparentHoverState(hovered);
    }

    private sealed class Link : VisualOverrideBase<Button>.BaseLink
    {
        private readonly bool _transparentAtRest;
        private readonly bool _inPopupTree;
        private readonly bool _accent;

        internal Link(Button button, bool transparentAtRest, bool inPopupTree, bool accent) : base(button)
        {
            _transparentAtRest = transparentAtRest;
            _inPopupTree = inPopupTree;
            _accent = accent;
        }

        // Button 无选中态，无需额外状态回调
        protected override void AttachExtra() { }

        /// <summary>整页驱动退出：只有 <c>inPopupTree</c>（缩放预设浮层 8 颗）退出 —— 它们活在 popup 的
        /// <b>独立 Xaml 树</b>，跨树 <c>GetCurrentPoint</c> 坐标不可靠（10-03 实证），且已由
        /// <c>ZoomPresetPanel</c> 面板驱动 ⇒ 整页驱动必须跳过，以免误清 / 误亮。
        /// ⚠ 缩放胶囊 4 颗虽同为透明静止档，但在<b>主树</b> ⇒ 参与整页驱动
        /// （判据是 <c>inPopupTree</c>，不是静止档透明与否 —— 两者正交）。</summary>
        internal override bool ExcludeFromPageHoverDrive => _inPopupTree;

        /// <summary>popup 树按钮（缩放预设浮层 8 颗、操作指南按钮）跳过视觉树遍历、直接用软件主题源：
        /// 该树的 <c>FlyoutPresenter</c> 挂着 WinUI 自己镜像的 <c>RequestedTheme</c>，它<b>滞后于</b> app 主题
        /// （10-09 日志实证：app 已深色、<c>FlyoutPresenter</c> 仍报 Light）⇒ 遍历会取到浅色档 #FFFFFFFF，
        /// 深色下按钮整体变白、鼠标掠过才恢复。判据与 <see cref="ExcludeFromPageHoverDrive"/> 同源（皆 popup 树）。</summary>
        protected override bool UseAppThemeSourceOnly => _inPopupTree;

        /// <summary>浮层强清：仅 transparentAtRest 项才清 hover，避免误清正常功能按钮。</summary>
        protected override void ClearTransparentHoverIfAny()
        {
            if (!_transparentAtRest) return;
            if (!_hover) return;
            _hover = false;
            _dirty = true;
            Sync();
        }

        /// <summary>由菜单面板的 PointerMoved 驱动：本项是否恰好是 <paramref name="hovered"/>。</summary>
        protected override void SetTransparentHover(Button? hovered)
        {
            if (!_transparentAtRest) return;
            bool hover = ReferenceEquals(_button, hovered);
            if (_hover == hover) return;
            _hover = hover;
            _dirty = true;
            Sync();
        }

        protected override void Apply()
        {
            if (_root == null) return;

            bool pressed = _button.IsPressed;
            if (!_dirty
                && pressed == _lastPressed
                && _hover == _lastHover)
            {
                return;
            }
            _dirty = false;
            _lastPressed = pressed;
            _lastHover = _hover;

            // 欢迎页「打开 PDF」按钮（accent 模式，10-05 接管）：原来套 GlanceAccentButtonStyle（自带原生
            // VSM + BrushTransition），点击弹文件选择器时输入上下文切换、且无法判定鼠标是否仍在按钮上
            // ⇒ VSM 的 Pressed→Normal→PointerOver 序列在快速点击下闪烁（用户实测 flicker）。改为几何驱动后，
            // hover 由整页 PageRoot_PointerMoved 实时几何判定、pressed 由 IsPressed 属性回调，模板已无 VSM
            // ⇒ flicker 根因消除。配色沿用用户已验收的 Obsidian 紫三档（GlanceAccentBrush 0.9 / 0.8 梯度），
            // 边框与原 GlanceAccentButtonStyle 完全一致：normal / hover 用 AccentControlElevationBorderBrush、
            // pressed 边框透明（ControlFillColorTransparentBrush）。前景【不再】用 WinUI 自适应的
            // TextOnAccentFillColor*（它会随系统强调色 / 主题错位，曾导致深色调悬浮态文字错变白且 hover 卡 true
            // 后无法回落），改为 App.xaml 两主题字典显式写死的 GlanceAccentForeground（深黑 / 浅白）+
            // GlanceAccentForegroundPressed（深 #595959 / 浅 #E6E6E6）：normal / hover 同源（文字恒定，只靠
            // 背景 1.0/0.9 紫梯度表状态），pressed 略压一档。两主题键一一对应（见 App.xaml 注释）。
            if (_accent)
            {
                if (pressed)
                {
                    _root.Background = Resource("GlanceAccentBrushPressed") as Brush;
                    _root.Foreground = Resource("GlanceAccentForegroundPressed") as Brush;
                    _root.BorderBrush = Resource("ControlFillColorTransparentBrush") as Brush;
                }
                else if (_hover)
                {
                    _root.Background = Resource("GlanceAccentBrushPointerOver") as Brush;
                    _root.Foreground = Resource("GlanceAccentForeground") as Brush;
                    _root.BorderBrush = Resource("AccentControlElevationBorderBrush") as Brush;
                }
                else
                {
                    _root.Background = Resource("GlanceAccentBrush") as Brush;
                    _root.Foreground = Resource("GlanceAccentForeground") as Brush;
                    _root.BorderBrush = Resource("AccentControlElevationBorderBrush") as Brush;
                }
                return;
            }

            // 与 generic.xaml 的 CommonStates 状态表逐行对应：按下优先于悬停（无 IsChecked 档）。
            // ⚠ 键用 ToggleButton* 的「未选中三档」（App.xaml 主题字典，与亚克力同源、深浅已验收），
            //   绝不用 Button*（顶层 generic 提升实例，深浅错乱，见类注释）；也绝不带 Checked 后缀
            //   （那是 Obsidian 紫强调档，功能按钮没有选中态）。
            string suffix = pressed ? "Pressed" : _hover ? "PointerOver" : string.Empty;

            if (_transparentAtRest && suffix.Length == 0)
            {
                // 透明项静止档：背景写「透明刷」而非 null —— 目的有二：
                //   ① 不涂成 App.xaml 的 ToggleButtonBackground（不透明中性底，会破坏菜单项透明设计）；
                //   ② ⚠ 保住命中测试：null 的 ContentPresenter 整片不可命中，只有文字能被命中 ⇒ hover / click
                //      只在文字附近生效、扫过文字后才整片可点（10-02 实测）。透明刷不可见但仍可命中。
                // ⚠ Foreground 也绝不能写 null：null 是「本地值 = 无画刷」而非「清值回退 TemplateBinding」，
                //   文字会整段消失（10-02 实测：缩放预设浮层静止完全不可见、hover 才显形）。必须写回静止档前景刷。
                _root.Background = TransparentBrush;
                _root.BorderBrush = null;
                _root.Foreground = Resource("ToggleButtonForeground") as Brush;
                return;
            }

            if (_transparentAtRest)
            {
                // 缩放胶囊 4 颗 + 浮层预设 8 颗（均 transparentAtRest）的悬浮/点击态（10-05 推广到浮层）：
                // 同源反色/近胶囊【淡叠加】档。静止完全透明（胶囊=并入亚克力、浮层=菜单项语义）；
                // 悬浮=胶囊反色的≈16% 半透明叠加、按下=靠近胶囊色的≈5% 极淡叠加（亚克力照透、对比很小）。
                // 两者 hover 事件源不同（浮层在 popup 独立树 → ZoomPresetPanel 驱动；胶囊在主树 → 整页驱动），
                // 但颜色完全一致 —— 都由本分支按 ZoomPillButtonHover/PressedBackground 上色（仅背景键，
                // 前景复用静止档 ToggleButtonForeground ⇒ 文字不随悬浮跳动）。
                // ⚠ 边框留 null：淡叠加已自带形状，加描边反而显脏。
                string key = pressed ? "ZoomPillButtonPressed" : "ZoomPillButtonHover";
                _root.Background = Resource(key + "Background") as Brush;
                _root.Foreground = Resource("ToggleButtonForeground") as Brush;
                _root.BorderBrush = null;
                return;
            }

            _root.Background = Resource("ToggleButtonBackground" + suffix) as Brush;
            _root.Foreground = Resource("ToggleButtonForeground" + suffix) as Brush;
            _root.BorderBrush = Resource("ToggleButtonBorderBrush" + suffix) as Brush;
        }
    }
}
