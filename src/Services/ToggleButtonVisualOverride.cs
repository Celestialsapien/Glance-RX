using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Glance.Services;

/// <summary>
/// 接管 ToggleButton 的视觉状态 —— 专治「鼠标松开后指针一动没动，按钮底色却先跳一档再跳回来」。
/// </summary>
/// <remarks>
/// <para><b>做法（v4，最终版）：自定义模板 + 独占写属性，把原生状态机整个停掉。</b></para>
/// <para>WinUI 的 <c>DefaultToggleButtonStyle</c>（generic.xaml:27811-28013，本机 WinAppSDK 2.2.1）把
/// 「未选中」与「选中」混在同一个 CommonStates 组里；模板根是个 <see cref="ContentPresenter"/>
/// （generic.xaml:27829），状态组挂在它身上（27834-28007），而<b>每个状态都有一个
/// ObjectAnimationUsingKeyFrames Storyboard 在写这四个属性</b>（除了空的 Normal）。
/// 也就是说：只要原生状态机还在，颜色就由 Storyboard 说了算，本地属性值永远被压在下面。</para>
///
/// <para>前面几版都试图跟 Storyboard 抢（先 Clear 状态组、再写本地属性 + GoToState），实测<b>都没用</b>：
/// 状态组 Clear 不掉；Storyboard 动画值优先级高于本地值；<c>GoToState</c> 的签名只收
/// <see cref="Control"/>，而模板根是 ContentPresenter，根本够不着。
/// 于是本版不再跟它抢，直接改模板（见 MainPage.xaml 的 ToggleButtonNoStateMachineStyle）：
/// <b>复刻同一个 ContentPresenter 模板，但去掉 VisualStateGroups 与 BackgroundTransition</b>
/// ⇒ 原生 CommonStates 那套 Storyboard 根本不存在，颜色只有本类一个来源，写什么就是什么。</para>
///
/// <para><b>于是本类只剩一件事</b>：在 (IsChecked, IsPressed, 指针是否真的悬停) 变化时，
/// 把该档位对应的四个视觉属性（Background / Foreground / BorderBrush / BackgroundSizing）
/// 写到模板根的 ContentPresenter 上。取值与 generic.xaml 的状态表<b>逐行同源</b>，所以外观与之前一模一样。</para>
///
/// <para><b>为什么 hover 必须用坐标几何判据</b>：ButtonBase 在松手时会调
/// <c>ReleasePointerCaptureInternal()</c>，它重新命中测试后对原捕获元素<b>补发一次 PointerExited</b>
/// —— 指针坐标一个像素都没动、期间也没有任何移动消息 —— 把框架内部的 <c>IsPointerOver</c> 打成 false，
/// 约 380ms 后框架再自发补发一次 PointerEntered 把谎圆回来。这就是「鼠标没动，颜色先跳一档」的根源，
/// 既然本类不再采信框架的任何悬停标记，这个谎也就影响不到画面了。</para>
///
/// <para><b>三个取值坑（改这里必看）</b>：</para>
/// <list type="bullet">
/// <item><c>ToggleButtonBackground*</c> 只定义在 <c>Application.Resources.ThemeDictionaries</c> 里。
/// 顶层字典<b>也能取到同名键</b>（generic 提升上来的），但那份不随主题切换、值还是旧的
/// ⇒ 取值顺序必须是「主题字典 → Default → 顶层兜底」，反了就永远拿不到配色。</item>
/// <item>主题字典<b>没有 "Dark" 键</b>（暗色值在 "Default" 里）
/// ⇒ <c>ActualTheme.Dark</c> 必须先映射成 "Default"，否则匹配不上。</item>
/// <item>Light 与 Dark 的状态表取值完全不同 ⇒ 颜色绝不能硬编码，只能按「档位名 → 资源后缀」同源映射。</item>
/// </list>
///
/// <para><b>不碰的东西</b>：模板、边框线宽、圆角、Padding、字体、布局、焦点 visuals 一律不动。</para>
/// </remarks>
internal static class ToggleButtonVisualOverride
{
    /// <summary>接管指定按钮的视觉输出。应在控件模板已应用之后调用（例如 Page.Loaded）。</summary>
    public static void Attach(ToggleButton button)
    {
        if (button == null) return;
        new Link(button).Attach();
    }

    /// <summary>接管指定按钮的视觉输出，并用 <paramref name="colorOverride"/> 对该按钮单独覆写视觉键。
    /// 只有传了委托的按钮才走覆写分支，其余按钮 <paramref name="colorOverride"/> 为 null 会短路、行为零变化。</summary>
    /// <param name="colorOverride">按资源键返回覆写值；返回 null 表示「不覆写、回落主题」。仅对需要独立配色的按钮（如画笔色）使用。</param>
    public static void Attach(ToggleButton button, Func<string, object?>? colorOverride)
    {
        if (button == null) return;
        new Link(button, colorOverride).Attach();
    }

    // 所有已接管的 Link 实例，供主题切换时同步强制重绘（见 RefreshAll）。
    // 接管器原本只在 ActualThemeChanged（异步、下一帧才触发）里重绘；主题切换瞬间按钮仍留着旧主题画刷，
    // 浅→深时旧画刷是白底 ⇒ 闪一下白。故在主题被「同步设值」的那一刻调用 RefreshAll() 立即重绘，消除该帧。
    private static readonly List<Link> _instances = new();

    /// <summary>强制所有已接管按钮按当前主题立即重绘。应在主题被同步设值的时刻调用
    /// （如 MainPage 主题下拉），消除 ActualThemeChanged 异步传播造成的「旧白底闪一帧」。</summary>
    public static void RefreshAll()
    {
        foreach (var link in _instances)
        {
            link.ForceRefresh();
        }
    }

    private sealed class Link
    {
        private readonly ToggleButton _button;
        private readonly Func<string, object?>? _colorOverride;
        private ContentPresenter? _root;

        private bool _hover;
        private bool _lastChecked;
        private bool _lastPressed;
        private bool _lastHover;
        private bool _dirty;

        internal Link(ToggleButton button, Func<string, object?>? colorOverride = null)
        {
            _button = button;
            _colorOverride = colorOverride;
        }

        internal void Attach()
        {
            // ① 两个决定视觉的真源挂依赖属性回调
            _button.RegisterPropertyChangedCallback(
                ToggleButton.IsCheckedProperty, (s, e) => Sync());
            _button.RegisterPropertyChangedCallback(
                ButtonBase.IsPressedProperty, (s, e) => Sync());

            // ② 松手这一帧看一眼：此时 IsChecked 已回写、IsPressed 已清，
            //    而假 exited 与三重状态窜扰都还没发生
            _button.Click += (s, e) => Sync();

            // ③ 主题切换 / 反复挂载都可能让控件模板被重新应用（模板根换成新实例），
            //    并置 _dirty 强制重算 —— 否则 (checked, pressed, hover) 一个都没变时会被下面的早退挡掉，
            //    资源里的主题色换了也白换。
            //    ⚠ Loaded 必须重新登记到 _instances（见 EnsureRegistered 注释）：下面 ⑤ 挂了 Unloaded，
            //    一旦该控件活在某个 Popup/Flyout 里，关闭时内容被卸载就会把本 Link 摘出实例表；
            //    Attach 又受业务守卫不会重跑 ⇒ 若 Loaded 不补回去，RefreshAll() 会永久漏掉这一项
            //    ⇒ 主题切换时它仍持旧主题画刷、该闪的白照闪。姊妹类 ButtonVisualOverride 已在
            //    10-03 实证踩过（缩放预设浮层的悬浮色残留），此处同步堵上。
            _button.ActualThemeChanged += (s, e) => { _dirty = true; Sync(); };
            _button.Loaded += (s, e) => { EnsureRegistered(); _dirty = true; Sync(); };

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
        /// ⚠ Unloaded 会把本 Link 摘出实例表（控件被移出可视树，例如所在 Popup/Flyout 关闭）；
        /// 重新载入时 Attach 通常不会再跑，只有 Loaded 回调会跑 ⇒ 必须在此补回，
        /// 否则 <see cref="RefreshAll"/> 会永久漏掉这一项（主题切换不再更新它）。
        /// 与姊妹类 <c>ButtonVisualOverride.Link.EnsureRegistered</c> 同一处缺陷、同一套修法。
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

            // 三态按钮的 Indeterminate 档位本类不接管，交给模板
            if (_button.IsChecked == null) return;

            bool checkedValue = _button.IsChecked == true;
            bool pressed = _button.IsPressed;
            if (!_dirty
                && checkedValue == _lastChecked
                && pressed == _lastPressed
                && _hover == _lastHover)
            {
                return;
            }
            _dirty = false;
            _lastChecked = checkedValue;
            _lastPressed = pressed;
            _lastHover = _hover;

            // 与 generic.xaml 的 CommonStates 状态表逐行对应：按下优先于悬停
            string suffix = checkedValue
                ? (pressed ? "CheckedPressed" : _hover ? "CheckedPointerOver" : "Checked")
                : (pressed ? "Pressed" : _hover ? "PointerOver" : string.Empty);

            _root.Background = Resource("ToggleButtonBackground" + suffix) as Brush;
            _root.Foreground = Resource("ToggleButtonForeground" + suffix) as Brush;
            _root.BorderBrush = Resource("ToggleButtonBorderBrush" + suffix) as Brush;

            // checked 系列才切到 OuterBorderEdge（generic.xaml:2344/5141/7956 起的资源）。
            // 取不到就别动它 —— 保持模板自己给的那一档。
            if (suffix.StartsWith("Checked", StringComparison.Ordinal)
                && Resource("ToggleButtonCheckedStateBackgroundSizing") is BackgroundSizing sizing)
            {
                _root.BackgroundSizing = sizing;
            }
        }

        /// <summary>
        /// 取主题资源：<b>主题字典优先，顶层字典降级为兜底</b>。取不到返回 null ——
        /// 写 null 等于清掉本地值，会回落到模板的 TemplateBinding，不会留透明块。
        /// </summary>
        /// <remarks>
        /// 【为什么必须反过来查（2026-09-27 实测，勿改回旧顺序）】
        /// 旧实现是「先顶层字典，再主题字典」。但顶层 <c>Application.Resources</c> 里<b>已经有一份
        /// 框架从 generic.xaml 提升上来的同名键</b>（<c>XamlControlsResources</c> 合并时带进来的）。
        /// 实测探针（Dark 主题、ReadModeButton）：
        ///     themeDictKeys                        = Default,Light   ← 我们写的两个字典都在
        ///     ToggleButtonBackgroundChecked        -> APP-ROOT | #FF0067C0  （generic 强调蓝）
        ///     ToggleButtonBackground               -> APP-ROOT | #B3FFFFFF  （generic Light 档的白）
        /// 顶层那份先命中 ⇒ 主题字典里的 Obsidian 紫<b>永远轮不到</b> ⇒
        /// 改了 App.xaml 画面纹丝不动。这就是「配色没生效」的真因，与字典遮挡是两回事。
        /// 而且顶层那份还不随主题切换（Dark 下返回的是 Light 档的值），拿它当默认值本身就是错的。
        /// </remarks>
        private object? Resource(string key)
        {
            // ① 单按钮局部覆写（如画笔模式按钮的实时画笔色）：只有传了委托的按钮才走这一支；
            //    其余按钮 _colorOverride == null ⇒ 短路、行为与之前逐字节一致。
            if (_colorOverride != null && _colorOverride(key) is object overridden)
                return overridden;

            var resources = Application.Current?.Resources;
            if (resources == null) return null;

            // ① 当前主题对应的主题字典（Dark 映射到 WinUI 的 "Default" 键）。
            //    ⚠ 用确定性主题源 ResolvedThemeDictKey()（沿视觉树取 MainPage 等显式 RequestedTheme），
            //    绝不用 _button.ActualTheme —— 它在 RequestedTheme 设置后要经布局通道异步传播，
            //    主题切换瞬间仍停在旧主题 ⇒ 读到旧字典、浅色下涂白底，在深色模式里闪一下白。
            //    与 GpuPageView.UpdateThemeColors 同因（见其 2452-2476 注释）。
            if (LookupThemeDict(resources, ResolvedThemeDictKey()) is ResourceDictionary themeDict
                && themeDict.TryGetValue(key, out var themed))
            {
                return themed;
            }

            // ③ 退回 Default 档（WinUI 的顶层主题字典没有 "Dark" 键，暗色值就在 Default 里）
            if (LookupThemeDict(resources, "Default") is ResourceDictionary fallback
                && fallback.TryGetValue(key, out var defaulted))
            {
                return defaulted;
            }

            // ④ 最后才考虑顶层字典，纯粹是为了「主题字典里压根没定义」时不写 null
            if (resources.TryGetValue(key, out var direct)) return direct;

            return null;
        }

        /// <summary>
        /// 确定性主题源：把"当前应是深色还是浅色"映射成 WinUI 主题字典的键名（Default=暗 / Light=浅）。
        /// 从 <c>_button</c> 沿视觉树向上找第一个显式设置 <see cref="FrameworkElement.RequestedTheme"/>
        /// 为 Dark/Light 的祖先（本 app 主题正是同步写到 MainPage 上的那个值），
        /// 用它判定深浅；全链都是 Default(跟随系统) 才回落 <c>Application.Current.RequestedTheme</c>（系统主题）。
        /// 绝不用 <c>_button.ActualTheme</c> —— 它在 RequestedTheme 设置后要经布局通道异步传播，
        /// 主题切换瞬间仍停在旧主题 ⇒ Resource() 读到旧字典、浅色下涂白底，在深色模式里闪一下白。
        /// 与 GpuPageView.UpdateThemeColors 同因（见其 2452-2476 注释），但本实现不依赖 MainWindow.IsDarkTheme
        /// （WindowRoot 是 Grid、主题实际写在 MainPage 上，IsDarkTheme 对显式深/浅选择会回落系统主题而失准）。
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
