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

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace GlanceRX.Services;

/// <summary>
/// <see cref="ButtonVisualOverride"/> 与 <see cref="ToggleButtonVisualOverride"/> 的共享基座。
/// 两类的「接管视觉输出」机制 90% 相同（实例表 / 主题同步重绘 / 指针几何判据 / 模板根解析 / 主题资源取值），
/// 只有 (a) 是否关心 IsChecked、(b) 透明静止档、(c) 单按钮局部覆写 三处语义不同。
/// 把相同的 10 个成员提到此处，修一处即两类受益 —— 杜绝「姊妹类同款地雷」
/// （10-03 的 EnsureRegistered 缺陷就是两边各踩一次才暴露的）。
/// </summary>
internal abstract class VisualOverrideBase<TButton> where TButton : ButtonBase
{
    // 所有已接管的 Link 实例，供主题切换时同步强制重绘（见 RefreshAll），
    // 也供浮层项的清理 / 独占 hover 遍历。
    // ⚠ 成员资格会因 Flyout 卸载而丢失，Link.Loaded 必须用 EnsureRegistered() 补回，否则遍历会永久漏项。
    // 静态字段随 closed-generic 各持一份：ButtonVisualOverride 与 ToggleButtonVisualOverride 互不影响（与重构前等价）。
    protected static readonly List<BaseLink> _instances = new();

    // 弹模态对话框（文件选择器等）期间置 true：抑制所有按钮的 hover 更新（强制静止）。
    // 弹窗打开瞬间常有游离 PointerMoved/Entered 把按钮 hover 重新置 true、且弹窗期间整页几何驱动不触发
    // ⇒ 按钮停在悬浮态、关闭后随鼠标移动才回落（"凭空变色"）。置 true 时立即把所有已接管按钮清回静止；
    // 置 false 后由下一次真实指针移动按几何恢复。详见 ButtonVisualOverride / MainPage 的 OpenPdfAsync。
    protected static bool _hoverSuppressed;

    /// <summary>强制所有已接管按钮按当前主题立即重绘。应在主题被同步设值的时刻调用
    /// （如 MainPage 主题下拉），消除 ActualThemeChanged 异步传播造成的「旧白底闪一帧」。</summary>
    public static void RefreshAll()
    {
        foreach (var link in _instances) link.ForceRefresh();
    }

    /// <summary>
    /// 按<b>实时指针位置</b>重算本类所有已接管按钮的 hover —— 几何判据，只点亮指针真正落在里面的那颗（天然独占）。
    /// <para>这是「悬浮色残留」的根因修复：hover 的权威来源改成"每次指针移动时的实时几何重算"，
    /// 而<b>不再依赖每颗自己收到 <c>PointerExited</c></b> —— 该事件在 popup/flyout 输入上下文切换、
    /// 快速移动、LightDismiss 覆盖层移除时会被 WinUI 吞掉或误派发（10-03 浮层 8 颗、10-04 缩放胶囊 4 颗均因此残留）。
    /// 由页面/容器级 <c>PointerMoved</c> 驱动 ⇒ 任何残留都会在<b>下一次指针移动时自愈</b>。</para>
    /// <para>性能：每次移动遍历全部实例做 <c>GetCurrentPoint</c> 几何判定，但 <c>SetHover</c> 只在
    /// hover 真变化时才置脏重绘，未变化的走 <c>Apply</c> 早退 ⇒ 不产生额外重绘。</para>
    /// </summary>
    public static void UpdateHoverFromPointer(PointerRoutedEventArgs e)
    {
        foreach (var link in _instances)
        {
            // 活在另一棵 Xaml 树的（popup/flyout 内容）：跨树坐标不可靠，由所在容器的处理器驱动
            if (link.ExcludeFromPageHoverDrive) continue;
            link.SetHoverFromPointer(e);
        }
    }

    /// <summary>弹模态对话框期间调用：抑制全部已接管按钮的 hover 更新（强制静止）。
    /// 仅应在「点击按钮即弹模态对话框、鼠标随对话框离开按钮」的路径里 <c>true → await → false</c> 包裹，
    /// 如打开/保存文件选择器。置 true 时立即把所有已接管按钮清回静止；置 false 后由下一次真实
    /// 指针移动按几何恢复 hover / 静止，杜绝「凭空变色」。</summary>
    public static void SetHoverSuppressed(bool suppressed)
    {
        _hoverSuppressed = suppressed;
        if (suppressed)
        {
            foreach (var link in _instances) link.SetHover(false);
        }
    }

    /// <summary>接管逻辑的共享内核。具体按钮类型（Button / ToggleButton）由 <typeparamref name="TButton"/> 参数化；
    /// 类型相关的状态回调（如 ToggleButton.IsChecked）由 <see cref="AttachExtra"/> 补挂，状态表由 <see cref="Apply"/> 各自实现。
    /// 用 public 以便从各自独立的接管器类（非嵌套、无法直接触达 protected 嵌套类型）派生 Link。</summary>
    public abstract class BaseLink
    {
        protected readonly TButton _button;
        protected bool _hover;
        protected bool _lastPressed;
        protected bool _lastHover;
        protected bool _dirty;
        protected ContentPresenter? _root;

        protected BaseLink(TButton button) { _button = button; }

        internal void Attach()
        {
            // ① 决定视觉的真源挂依赖属性回调（按下）
            _button.RegisterPropertyChangedCallback(
                ButtonBase.IsPressedProperty, (s, e) => Sync());

            // ①' 类型相关的额外状态回调（ToggleButton 挂 IsChecked；Button 无事）
            AttachExtra();

            // ② 松手这一帧看一眼：此时状态已回写，而假 exited 与状态窜扰都还没发生
            _button.Click += (s, e) => Sync();

            // ③ 主题切换 / 反复挂载都可能让控件模板被重新应用（模板根换成新实例），
            //    并置 _dirty 强制重算 —— 否则状态都没变时会被 Apply 的早退挡掉，资源里的主题色换了也白换。
            //    ⚠ Loaded 必须重新登记到 _instances（见 EnsureRegistered 注释）：下面 ⑤ 挂了 Unloaded，
            //    Flyout 关闭会卸载内容并把本 Link 摘出实例表；Attach 又受业务守卫不会重跑 ⇒ 若 Loaded
            //    不补回去，任何"按实例表遍历"的操作都会永久漏掉这一项。
            _button.ActualThemeChanged += (s, e) => { _dirty = true; Sync(); };
            _button.Loaded += (s, e) =>
            {
                EnsureRegistered();                      // 卸载后重挂：补回实例表，否则再也清不掉
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

        /// <summary>类型相关的额外状态回调（如 ToggleButton 挂 IsCheckedProperty）。Button 无选中态 ⇒ 空实现。</summary>
        protected abstract void AttachExtra();

        private void OnUnloaded(object sender, RoutedEventArgs e) => _instances.Remove(this);

        /// <summary>
        /// 保证本 Link 仍在全局实例表 <see cref="_instances"/> 里（幂等）。
        /// ⚠ Flyout/Popup 关闭会卸载内容 ⇒ <see cref="OnUnloaded"/> 把本 Link 摘出实例表，而重开时外部
        /// <c>Attach</c> 受业务守卫不会重跑、只有 Loaded 回调会跑。若不在此补回去，所有按 <c>_instances</c>
        /// 遍历的操作都会**永久漏掉这一项**（10-03 实证：悬浮色关菜单后仍残留、下次展开还在）。
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

        private void Track(PointerRoutedEventArgs e) => SetHoverFromPointer(e);

        /// <summary>按指针相对本按钮的坐标几何判定 hover —— 同一可视树内、无跨树坐标系问题。
        /// 只在 hover 真变化时才置脏重绘（未变化不触发 Sync，省掉每次移动的无谓 Refresh/Apply）。
        /// <para>是否参与整页驱动由 <see cref="ExcludeFromPageHoverDrive"/> 决定（<b>不是</b>靠覆写本方法）。
        /// ⚠ 必须是 <c>internal</c> 而非 <c>protected</c>：包含类 <c>VisualOverrideBase</c> 不是
        /// <c>BaseLink</c> 的派生类，protected 成员对包含类不可访问（CS0122，实测）。</para></summary>
        internal void SetHoverFromPointer(PointerRoutedEventArgs e)
        {
            // 弹模态对话框期间直接按静止处理（见 SetHoverSuppressed），不读几何。
            if (_hoverSuppressed) { SetHover(false); return; }
            var pos = e.GetCurrentPoint(_button).Position;
            bool inside = pos.X >= 0 && pos.Y >= 0
                       && pos.X < _button.ActualWidth && pos.Y < _button.ActualHeight;
            SetHover(inside);
        }

        /// <summary>是否<b>退出</b>「整页 hover 几何驱动」。默认 false = 参与。
        /// <para>需要退出的情形：本 Link 的按钮活在<b>另一棵 Xaml 树</b>（popup / flyout 内容）——
        /// 跨树 <c>GetCurrentPoint</c> 的坐标不可靠（10-03 实证踩过），且它已由<b>所在容器</b>的处理器驱动
        /// ⇒ 整页驱动必须跳过，否则会误清 / 误亮。</para>
        /// <para>⚠ 这是<b>事件</b>语义，与「静止档是否透明」的<b>外观</b>语义正交，绝不能复用同一个标志：
        /// 缩放胶囊 4 颗静止档同样是透明的（要并入亚克力胶囊、看不见圆边界），但它在<b>主可视树</b>里、
        /// 必须参与整页驱动，否则悬浮色残留会立刻回归（10-04）。</para></summary>
        internal virtual bool ExcludeFromPageHoverDrive => false;

        /// <summary>直接设定 hover（供页面/容器级驱动「全灭」或外部指派）；
        /// 同样只在真变化时才置脏重绘。</summary>
        internal void SetHover(bool hover)
        {
            // 弹模态对话框期间抑制 hover（详见 SetHoverSuppressed）：任何 hover 请求都按静止处理，
            // 否则弹窗打开瞬间的游离 PointerMoved/Entered 会把按钮卡在悬浮态、且弹窗期间整页几何驱动不触发。
            if (_hoverSuppressed) hover = false;
            if (_hover == hover) return;
            _hover = hover;
            _dirty = true;
            Sync();
        }

        /// <summary>置脏后重解析模板根并应用当前状态。派生类经 <see cref="Apply"/> 实现状态→画刷映射。</summary>
        protected void Sync()
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

        /// <summary>把当前状态映射到对应主题画刷并写到模板根。状态集因按钮类型而异，由子类实现。</summary>
        protected abstract void Apply();

        // —— 浮层透明项专属（仅 ButtonVisualOverride 用到；ToggleButton 为空实现）——
        /// <summary>供浮层强清遍历调用：仅当本项是 transparentAtRest 时才清 hover。基类空实现。</summary>
        protected virtual void ClearTransparentHoverIfAny() { }
        /// <summary>供浮层独占 hover 遍历调用：仅当本项是 transparentAtRest 时才按 hovered 置 hover。基类空实现。</summary>
        protected virtual void SetTransparentHover(Button? hovered) { }

        internal void ClearTransparentHoverState() => ClearTransparentHoverIfAny();
        internal void SetTransparentHoverState(Button? hovered) => SetTransparentHover(hovered);

        /// <summary>
        /// 取主题资源：<b>主题字典优先，顶层字典降级为兜底</b>。取不到返回 null —— 写 null 等于清掉本地值，
        /// 会回落到模板的 TemplateBinding，不会留透明块。可被子类覆写（如 ToggleButton 的局部画笔色覆写）。
        /// </summary>
        /// <remarks>
        /// 【为什么必须反过来查（2026-09-27 实测，勿改回旧顺序）】
        /// 旧实现「先顶层字典，再主题字典」会命中 generic.xaml 提升上来的同名键（不随主题切换、Dark 返回 Light 值）
        /// ⇒ 主题字典里的配色永远轮不到、改了 App.xaml 画面纹丝不动。故顺序固定为 ① 主题字典 → ② Default 档 → ③ 顶层兜底。
        /// 且主题字典无 "Dark" 键（暗色值在 "Default" 里），用 <see cref="ResolvedThemeDictKey"/> 确定性映射、绝不依赖异步的 ActualTheme。
        /// </remarks>
        protected virtual object? Resource(string key)
        {
            var resources = Application.Current?.Resources;
            if (resources == null) return null;

            // ① 当前主题对应的主题字典（Dark 映射到 WinUI 的 "Default" 键）。
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
        /// 全链都是 Default(跟随系统)、或弹层独立树走到父链断裂时，回落【软件自己的主题设置】
        /// （<c>MainWindow.IsDarkTheme</c>，已含"Default 回落系统"；详见方法内注释），
        /// 只有连窗口都取不到才用 <c>Application.Current.RequestedTheme</c>（系统主题）。
        /// 绝不用 <c>_button.ActualTheme</c> —— 它在 RequestedTheme 设置后要经布局通道异步传播，
        /// 主题切换瞬间仍停在旧主题 ⇒ Resource() 读到旧字典、浅色下涂白底，在深色模式里闪一下白。
        /// </summary>
        protected string ResolvedThemeDictKey()
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
                // ⚠ 兜底必须用【软件自己的主题设置】，不能直接用 Application.Current.RequestedTheme（系统设置）：
                //    ① 弹层 / 浮层是【独立 XAML 树】，上面这段向上遍历到 popup 根就断了（父链为 null）⇒ 掉到这里；
                //    ② Application.Current.RequestedTheme 在本 app 里【从未被赋值】，它恒等于 Windows 的
                //       "默认应用模式"系统设置，与用户在软件里选的主题完全无关 ——
                //       用户系统应用模式=浅色、软件设为深色时，此处会判成"浅色"⇒ 取到浅色字典的画刷。
                //    ⇒ 后果：浅色画刷被应用到按深色渲染的按钮，浅色画刷内的
                //      {ThemeResource GlanceFillNeutral*} 在深色字典查不到 ⇒ COMException + 配色错乱。
                //    正解 = 取窗口的 WindowRequestedTheme（软件设置已写入），Default 时再回落系统
                //      —— 与 GpuPageView.xaml.cs:2452 的确定性主题源同源；MainWindow.IsDarkTheme 已封装该回落。
                if ((Application.Current as App)?.MainWindow is MainWindow mw)
                    return mw.IsDarkTheme ? "Default" : "Light";

                // 连窗口都取不到（极端情况）：才用系统主题判定
                return Application.Current?.RequestedTheme == ApplicationTheme.Dark ? "Default" : "Light";
            }
            catch
            {
                // 最坏回落 ActualTheme（可能仍带旧竞态，但不崩）
                return _button.ActualTheme == ElementTheme.Dark ? "Default" : "Light";
            }
        }

        /// <summary>按主题名取出主题字典；取不到返回 null。</summary>
        protected static ResourceDictionary? LookupThemeDict(ResourceDictionary resources, string themeName)
        {
            if (resources.ThemeDictionaries.TryGetValue(themeName, out var dict)) return dict as ResourceDictionary;
            return null;
        }
    }
}
