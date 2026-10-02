using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Glance.Services;

/// <summary>
/// 接管普通功能按钮（<see cref="Button"/>，非 <see cref="ToggleButton"/>）的视觉输出 —— 与
/// <c>ToggleButtonVisualOverride</c> 同源，专治「鼠标松开后指针一动没动，按钮底色却先跳一档再跳回来」。
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
/// <para><b>机制（与 ToggleButtonVisualOverride 相同）</b>：先以 <c>ButtonNoStateMachineStyle</c>
/// （复刻默认模板、去掉 VisualStateGroups 与 BackgroundTransition）干掉原生状态机，再独占写
/// Background/Foreground/BorderBrush。<b>普通按钮的 hover 必须用坐标几何判据</b>：ButtonBase 松手时
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
    /// 改走「整颗变色」同源逻辑）。<b>10-02 补齐</b>：缩放胶囊 4 颗与缩放预设浮层 8 颗以
    /// 缩放胶囊 4 颗 10-02 已与对比视图两颗统一（静止同样带中性底，用户拍板）；
    /// <b>仅缩放预设浮层 8 颗</b>用 <c>transparentAtRest: true</c> —— 静止档写 null 保持透明、
    /// 只给 hover / pressed 上色（菜单项语义：静止不该有独立底色）。
    /// 仍排除：背景本身是内容（颜色胶囊 / 线宽胶囊）、欢迎页 OpenPdfButton、书签模板内按钮
    /// （DataTemplate 无法 x:Name 引用）—— 这三类用户已确认现状满意，接管会破坏其设计或取不到字段。</para>
/// </remarks>
internal static class ButtonVisualOverride
{
    /// <summary>接管指定按钮的视觉输出。应在控件模板已应用之后调用（例如 Page.Loaded）。</summary>
    /// <param name="transparentAtRest">true = 静止必须保持透明的按钮（目前只有缩放预设浮层 8 颗）：
    /// 静止（既非悬停也非按下）时底色 / 边框写 null（= 无画刷、不涂任何底），前景写静止档前景刷
    /// （<c>ToggleButtonForeground</c>）；只有 hover / pressed 两档才写已验收的
    /// <c>ToggleButton*PointerOver</c> / <c>ToggleButton*Pressed</c>（整颗变色，与功能按钮同源）。
    /// ⚠ 前景绝不能写 null —— null 是「本地值 = 无画刷」，文字会整段消失（10-02 实测浮层静止全不可见）。
    /// 09-30 已否决这类按钮的「描边变体」（丑 + 点击无反馈），故不走描边。</param>
    public static void Attach(Button button, bool transparentAtRest = false)
    {
        if (button == null) return;
        new Link(button, transparentAtRest).Attach();
    }

    // 所有已接管的 Link 实例，供主题切换时同步强制重绘（见 RefreshAll），
    // 也供浮层项的清理 / 独占 hover 遍历（SetFlyoutHover / ClearFlyoutHoverStates）。
    // ⚠ 成员资格会因 Flyout 卸载而丢失，Loaded 必须用 EnsureRegistered() 补回，否则遍历会永久漏项。
    // ActualThemeChanged 异步传播会造成「旧白底闪一帧」，故在主题被「同步设值」的那一刻
    // 调用 RefreshAll() 立即重绘（与 ToggleButtonVisualOverride.RefreshAll 同一时间点调用）。
    private static readonly List<Link> _instances = new();

    // 透明刷：静止档拿它（而非 null）填 ContentPresenter.Background。
    // ⚠ WinUI 命中测试：null 背景的 ContentPresenter 整片不参与命中，只有文字（TextBlock）能被命中
    //   ⇒ hover / click 只在文字附近生效，扫过文字把整片涂上色后才可点（10-02 实测）。
    //   Transparent 刷「不可见但仍可命中」⇒ 整片矩形响应，且不会涂成不透明底。
    private static readonly Brush TransparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    /// <summary>强制所有已接管按钮按当前主题立即重绘。应在主题被同步设值的时刻调用
    /// （如 MainPage 主题下拉），消除 ActualThemeChanged 异步传播造成的「旧白底闪一帧」。</summary>
    public static void RefreshAll()
    {
        foreach (var link in _instances)
        {
            link.ForceRefresh();
        }
    }

    /// <summary>浮层关闭时强清 8 颗预设项的 hover：WinUI 在 Flyout/light-dismiss 关闭瞬间吞掉
    /// PointerExited ⇒ <c>_hover</c> 卡 true ⇒ 悬浮色残留、下次展开仍亮。仅清
    /// <c>transparentAtRest</c>（= 浮层项，全 app 唯一一类），不影响其他功能按钮（它们的 hover 由常态指针事件维护）。</summary>
    public static void ClearFlyoutHoverStates()
    {
        foreach (var link in _instances)
        {
            link.ClearHoverIfTransparent();
        }
    }

    /// <summary>浮层预设项「独占 hover」：只让 <paramref name="hovered"/> 那一颗亮，其余
    /// <c>transparentAtRest</c>（= 浮层 8 颗）一律灭。<b>由菜单面板自身的 PointerMoved 实时驱动</b>
    /// （面板是这片区域里唯一稳定收到指针事件的元素），从根本上不依赖会被 WinUI 在 popup/flyout
    /// 输入上下文切换时吞掉的 <c>PointerExited</c> —— 杜绝「旧项残留亮着」。
    /// 传 <c>null</c> 表示指针不在任何一颗上（落在凹缝 / 菜单外）⇒ 全灭。</summary>
    public static void SetFlyoutHover(Button? hovered)
    {
        foreach (var link in _instances)
        {
            link.SetTransparentHover(hovered);
        }
    }

    private sealed class Link
    {
        private readonly Button _button;
        private readonly bool _transparentAtRest;
        private ContentPresenter? _root;

        private bool _hover;
        private bool _lastPressed;
        private bool _lastHover;
        private bool _dirty;

        internal Link(Button button, bool transparentAtRest)
        {
            _button = button;
            _transparentAtRest = transparentAtRest;
        }

        internal void Attach()
        {
            // ① 决定视觉的真源挂依赖属性回调（Button 无 IsChecked，只关心 IsPressed）
            _button.RegisterPropertyChangedCallback(
                ButtonBase.IsPressedProperty, (s, e) => Sync());

            // ② 松手这一帧看一眼：此时 IsPressed 已清，而假 exited 与状态窜扰都还没发生
            _button.Click += (s, e) => Sync();

            // ③ 主题切换 / 反复挂载都可能让控件模板被重新应用（模板根换成新实例），
            //    并置 _dirty 强制重算 —— 否则 (pressed, hover) 一个都没变时会被下面的早退挡掉，
            //    资源里的主题色换了也白换。
            //    ⚠ Loaded 必须重新登记到 _instances（见 EnsureRegistered 注释）：下面 ⑤ 挂了 Unloaded，
            //    Flyout 关闭会卸载内容并把本 Link 摘出实例表；Attach 又受业务守卫不会重跑 ⇒ 若 Loaded
            //    不补回去，任何"按实例表遍历"的清理 / 重绘（SetFlyoutHover / ClearFlyoutHoverStates /
            //    RefreshAll）都会永久漏掉这一项 —— 这正是"悬浮色关菜单后仍残留、下次展开还在"的根因（10-03 实证）。
            _button.ActualThemeChanged += (s, e) => { _dirty = true; Sync(); };
            _button.Loaded += (s, e) =>
            {
                EnsureRegistered();                      // 卸载后重挂：补回实例表，否则再也清不掉
                if (_transparentAtRest) _hover = false;  // 内容重新载入 ⇒ 此刻尚无任何项被悬停，彻底断掉残留
                _dirty = true;
                Sync();
            };

            // ④ 指针落点只用来维护「鼠标到底在不在按钮上」，绝不采信 IsPointerOver / PointerExited
            _button.PointerEntered += (s, e) => Track(e);
            _button.PointerMoved += (s, e) => Track(e);
            _button.PointerExited += (s, e) => Track(e);
            _button.PointerPressed += (s, e) => Track(e);

            Sync();

            // ⑤ 注册到全局实例表，供主题切换时 RefreshAll() 同步重绘（见类级注释）。
            _instances.Add(this);
            _button.Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _instances.Remove(this);
        }

        /// <summary>
        /// 保证本 Link 仍在全局实例表 <see cref="_instances"/> 里（幂等）。
        /// ⚠ Flyout/Popup 关闭会卸载内容 ⇒ <see cref="OnUnloaded"/> 把本 Link 摘出实例表，而重开时外部
        /// <c>Attach</c> 受业务守卫（<c>_zoomPresetOverrideAttached</c>）不会重跑、只有 Loaded 回调会跑。
        /// 若不在此补回去，所有按 <c>_instances</c> 遍历的操作都会**永久漏掉这一项**：
        /// SetFlyoutHover / ClearFlyoutHoverStates / RefreshAll 全部失效 ⇒ 该项 hover 一旦卡 true 就再
        /// 也清不掉，跨关菜单 / 重开都残留（10-03 实证）。
        /// </summary>
        private void EnsureRegistered()
        {
            for (int i = 0; i < _instances.Count; i++)
            {
                if (ReferenceEquals(_instances[i], this)) return;
            }
            _instances.Add(this);
        }

        /// <summary>置脏并立即重绘（供外部 RefreshAll 调用）。</summary>
        internal void ForceRefresh()
        {
            _dirty = true;
            Sync();
        }

        internal void ClearHoverIfTransparent()
        {
            if (!_transparentAtRest) return;
            if (!_hover) return;
            _hover = false;
            _dirty = true;
            Sync();
        }

        /// <summary>由菜单面板的 PointerMoved 驱动：本项是否恰好是 <paramref name="hovered"/>。</summary>
        internal void SetTransparentHover(Button? hovered)
        {
            if (!_transparentAtRest) return;
            bool hover = ReferenceEquals(_button, hovered);
            if (_hover == hover) return;
            _hover = hover;
            _dirty = true;
            Sync();
        }

        private void Track(PointerRoutedEventArgs e)
        {
            var pos = e.GetCurrentPoint(_button).Position;
            _hover = pos.X >= 0 && pos.Y >= 0
                  && pos.X < _button.ActualWidth && pos.Y < _button.ActualHeight;
            Sync();
        }

        private void Sync()
        {
            Refresh();
            Apply();
        }

        /// <summary>重新解析模板根（每次刷新都做，模板可能被重新应用过）。</summary>
        private void Refresh()
        {
            ContentPresenter? root = null;
            if (VisualTreeHelper.GetChildrenCount(_button) > 0)
                root = VisualTreeHelper.GetChild(_button, 0) as ContentPresenter;
            _root = root;
        }

        private void Apply()
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

        /// <summary>
        /// 取主题资源：<b>主题字典优先，顶层字典降级为兜底</b>。取不到返回 null ——
        /// 写 null 等于清掉本地值，会回落到模板的 TemplateBinding，不会留透明块。
        /// </summary>
        /// <remarks>
        /// <c>ToggleButton*</c> 未选中三档定义在 App.xaml 的 ThemeDictionaries 里，第一优先级即命中；
        /// 顶层兜底只是防「主题字典漏键」——而顶层躺着 generic 提升的 Light 档值，漏键时深色会拿到
        /// 浅色值（App.xaml 顶部注释记录过的 09-27 教训），所以两个主题字典的键集合必须一一对应。
        /// </remarks>
        private object? Resource(string key)
        {
            var resources = Application.Current?.Resources;
            if (resources == null) return null;

            // ① 当前主题对应的主题字典（Dark 映射到 WinUI 的 "Default" 键）。
            //    ⚠ 用确定性主题源 ResolvedThemeDictKey()（沿视觉树取 MainPage 等显式 RequestedTheme），
            //    绝不用 _button.ActualTheme —— 它在 RequestedTheme 设置后要经布局通道异步传播，
            //    主题切换瞬间仍停在旧主题 ⇒ 读到旧字典、浅色下涂白底，在深色模式里闪一下白。
            if (LookupThemeDict(resources, ResolvedThemeDictKey()) is ResourceDictionary themeDict
                && themeDict.TryGetValue(key, out var themed))
            {
                return themed;
            }

            // ② 退回 Default 档（WinUI 的顶层主题字典没有 "Dark" 键，暗色值就在 Default 里）
            if (LookupThemeDict(resources, "Default") is ResourceDictionary fallback
                && fallback.TryGetValue(key, out var defaulted))
            {
                return defaulted;
            }

            // ③ 最后才考虑顶层字典，纯粹是为了「主题字典里压根没定义」时不写 null
            if (resources.TryGetValue(key, out var direct)) return direct;

            return null;
        }

        /// <summary>
        /// 确定性主题源：把"当前应是深色还是浅色"映射成 WinUI 主题字典的键名（Default=暗 / Light=浅）。
        /// 从 <c>_button</c> 沿视觉树向上找第一个显式设置 <see cref="FrameworkElement.RequestedTheme"/>
        /// 为 Dark/Light 的祖先（本 app 主题正是同步写到 MainPage 上的那个值），用它判定深浅；
        /// 全链都是 Default(跟随系统) 才回落 <c>Application.Current.RequestedTheme</c>（系统主题）。
        /// 绝不用 <c>_button.ActualTheme</c> —— 它在 RequestedTheme 设置后要经布局通道异步传播，
        /// 主题切换瞬间仍停在旧主题 ⇒ Resource() 读到旧字典、浅色下涂白底，在深色模式里闪一下白。
        /// </summary>
        private string ResolvedThemeDictKey()
        {
            try
            {
                FrameworkElement? e = _button;
                while (e != null)
                {
                    if (e.RequestedTheme == ElementTheme.Dark) return "Default";
                    if (e.RequestedTheme == ElementTheme.Light) return "Light";
                    e = VisualTreeHelper.GetParent(e) as FrameworkElement;
                }
                // 全 Default（跟随系统）：用系统主题判定
                return Application.Current?.RequestedTheme == ApplicationTheme.Dark ? "Default" : "Light";
            }
            catch
            {
                // 最坏回落 ActualTheme（可能仍带旧竞态，但不崩）
                return _button.ActualTheme == ElementTheme.Dark ? "Default" : "Light";
            }
        }

        /// <summary>按主题名取出主题字典；取不到返回 null。</summary>
        private static ResourceDictionary? LookupThemeDict(ResourceDictionary resources, string themeName)
        {
            if (resources.ThemeDictionaries.TryGetValue(themeName, out var dict)) return dict as ResourceDictionary;
            return null;
        }
    }
}
