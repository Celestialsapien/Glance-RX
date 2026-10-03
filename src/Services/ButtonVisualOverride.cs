using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Glance.Services;

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
/// 缩放胶囊 4 颗已与对比视图两颗统一（静止同样带中性底，用户拍板）；
/// <b>仅缩放预设浮层 8 颗</b>用 <c>transparentAtRest: true</c> —— 静止档底色写缓存的 <c>TransparentBrush</c>
/// （不可见但仍可命中；<b>绝不是 null</b> —— null 背景的 ContentPresenter 整片不参与命中测试，
/// 只有文字能被点中，10-02 实测），只给 hover / pressed 上色（菜单项语义：静止不该有独立底色）。
/// 仍排除：背景本身是内容（颜色胶囊 / 线宽胶囊）、欢迎页 OpenPdfButton、书签模板内按钮
/// （DataTemplate 无法 x:Name 引用）—— 这三类用户已确认现状满意，接管会破坏其设计或取不到字段。</para>
/// </remarks>
internal class ButtonVisualOverride : VisualOverrideBase<Button>
{
    /// <summary>接管指定按钮的视觉输出。应在控件模板已应用之后调用（例如 Page.Loaded）。</summary>
    /// <param name="transparentAtRest">true = 静止必须保持透明的按钮（目前只有缩放预设浮层 8 颗）：
    /// 静止（既非悬停也非按下）时底色写缓存的 <c>TransparentBrush</c>、边框写 null，
    /// 前景写静止档前景刷（<c>ToggleButtonForeground</c>）；只有 hover / pressed 两档才写已验收的
    /// <c>ToggleButton*PointerOver</c> / <c>ToggleButton*Pressed</c>（整颗变色，与功能按钮同源）。
    /// ⚠ 底色绝不能写 null：null 背景的 ContentPresenter 整片不参与命中测试，只有文字能被命中
    ///   ⇒ hover / click 只在文字附近生效、扫过文字后才整片可点（10-02 实测）。
    ///   <c>TransparentBrush</c> 不可见但可命中，才是这里的正确写法。
    /// ⚠ 前景也绝不能写 null —— null 是「本地值 = 无画刷」，文字会整段消失（10-02 实测浮层静止全不可见）。
    /// 09-30 已否决这类按钮的「描边变体」（丑 + 点击无反馈），故不走描边。</param>
    public static void Attach(Button button, bool transparentAtRest = false)
    {
        if (button == null) return;
        new Link(button, transparentAtRest).Attach();
    }

    // 透明刷：静止档拿它（而非 null）填 ContentPresenter.Background。
    // ⚠ WinUI 命中测试：null 背景的 ContentPresenter 整片不参与命中，只有文字（TextBlock）能被命中
    //   ⇒ hover / click 只在文字附近生效，扫过文字把整片涂上色后才可点（10-02 实测）。
    //   Transparent 刷「不可见但仍可命中」⇒ 整片矩形响应，且不会涂成不透明底。
    private static readonly Brush TransparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    /// <summary>浮层关闭时强清 8 颗预设项的 hover：WinUI 在 Flyout/light-dismiss 关闭瞬间吞掉
    /// PointerExited ⇒ <c>_hover</c> 卡 true ⇒ 悬浮色残留、下次展开仍亮。仅清
    /// <c>transparentAtRest</c>（= 浮层项，全 app 唯一一类），不影响其他功能按钮（它们的 hover 由常态指针事件维护）。</summary>
    public static void ClearFlyoutHoverStates()
    {
        foreach (var link in _instances) link.ClearTransparentHoverState();
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

        internal Link(Button button, bool transparentAtRest) : base(button)
        {
            _transparentAtRest = transparentAtRest;
        }

        // Button 无选中态，无需额外状态回调
        protected override void AttachExtra() { }

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

            _root.Background = Resource("ToggleButtonBackground" + suffix) as Brush;
            _root.Foreground = Resource("ToggleButtonForeground" + suffix) as Brush;
            _root.BorderBrush = Resource("ToggleButtonBorderBrush" + suffix) as Brush;
        }
    }
}
