using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Glance.Services;

/// <summary>
/// 接管 ToggleButton 的视觉状态 —— 专治「鼠标松开后指针一动没动，按钮底色却先跳一档再跳回来」。
/// 与 <see cref="ButtonVisualOverride"/> 同源（共享 <see cref="VisualOverrideBase{TButton}"/>），仅多出 checked 状态与单按钮局部覆写。
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
/// ⇒ 取值顺序必须是「主题字典 → Default → 顶层兜底」，反了就永远拿不到配色（见基类 <c>Resource</c> 注释）。</item>
/// <item>主题字典<b>没有 "Dark" 键</b>（暗色值在 "Default" 里）
/// ⇒ <c>ActualTheme.Dark</c> 必须先映射成 "Default"，否则匹配不上。</item>
/// <item>Light 与 Dark 的状态表取值完全不同 ⇒ 颜色绝不能硬编码，只能按「档位名 → 资源后缀」同源映射。</item>
/// </list>
///
/// <para><b>不碰的东西</b>：模板、边框线宽、圆角、Padding、字体、布局、焦点 visuals 一律不动。</para>
/// </remarks>
internal class ToggleButtonVisualOverride : VisualOverrideBase<ToggleButton>
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

    private sealed class Link : VisualOverrideBase<ToggleButton>.BaseLink
    {
        private readonly Func<string, object?>? _colorOverride;
        private bool _lastChecked;

        internal Link(ToggleButton button, Func<string, object?>? colorOverride = null) : base(button)
        {
            _colorOverride = colorOverride;
        }

        // ToggleButton 多一个选中态，补挂 IsChecked 回调
        protected override void AttachExtra()
        {
            _button.RegisterPropertyChangedCallback(
                ToggleButton.IsCheckedProperty, (s, e) => Sync());
        }

        /// <summary>单按钮局部覆写（如画笔模式按钮的实时画笔色）：只有传了委托的按钮才走这一支；
        /// 其余按钮 _colorOverride == null ⇒ 短路、行为与之前逐字节一致。</summary>
        protected override object? Resource(string key)
        {
            if (_colorOverride != null && _colorOverride(key) is object overridden)
                return overridden;
            return base.Resource(key);
        }

        protected override void Apply()
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
    }
}
