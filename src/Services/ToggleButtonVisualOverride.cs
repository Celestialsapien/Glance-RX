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
/// 于是本版不再跟它抢，直接改模板（见 MainPage.xaml 的 ReadModeButtonStyle）：
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
/// <item><c>ToggleButtonBackground*</c> 只定义在 <c>Application.Resources.ThemeDictionaries</c> 里，
/// 顶层字典取不到 ⇒ 必须查主题字典。</item>
/// <item>顶层 ThemeDictionaries <b>没有 "Dark" 键</b>（暗色值在 "Default" 里）
/// ⇒ 按主题名取不到时必须退回 Default，否则写进去的是 null。</item>
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

    private sealed class Link
    {
        private readonly ToggleButton _button;
        private ContentPresenter? _root;

        private bool _hover;
        private bool _lastChecked;
        private bool _lastPressed;
        private bool _lastHover;
        private bool _dirty;

        internal Link(ToggleButton button) => _button = button;

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
            //    资源里的主题色换了也白换。Unloaded 不挂：重挂时 Loaded 自然会重算一次。
            _button.ActualThemeChanged += (s, e) => { _dirty = true; Sync(); };
            _button.Loaded += (s, e) => { _dirty = true; Sync(); };

            // ④ 指针落点只用来维护「鼠标到底在不在按钮上」，绝不采信 IsPointerOver / PointerExited
            _button.PointerEntered += (s, e) => Track(e);
            _button.PointerMoved += (s, e) => Track(e);
            _button.PointerExited += (s, e) => Track(e);
            _button.PointerPressed += (s, e) => Track(e);

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
        /// 取主题资源：先顶层字典，再主题字典，最后退回 Default（Dark 档在顶层没有 "Dark" 键）。
        /// 取不到返回 null —— 写 null 等于清掉本地值，会回落到模板的 TemplateBinding，不会留透明块。
        /// </summary>
        private object? Resource(string key)
        {
            var resources = Application.Current?.Resources;
            if (resources == null) return null;

            if (resources.TryGetValue(key, out var direct)) return direct;

            if (LookupThemeDict(resources, _button.ActualTheme.ToString()) is ResourceDictionary themeDict
                && themeDict.TryGetValue(key, out var themed))
            {
                return themed;
            }

            if (LookupThemeDict(resources, "Default") is ResourceDictionary fallback
                && fallback.TryGetValue(key, out var defaulted))
            {
                return defaulted;
            }

            return null;
        }

        /// <summary>按主题名取出主题字典；取不到返回 null。</summary>
        private static ResourceDictionary? LookupThemeDict(ResourceDictionary resources, string themeName)
        {
            if (resources.ThemeDictionaries.TryGetValue(themeName, out var dict)) return dict as ResourceDictionary;
            return null;
        }
    }
}
