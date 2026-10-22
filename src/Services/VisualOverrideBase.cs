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

/// 与 的共享基座。 两类的「接管视觉输出」机制 90% 相同（实例表 / 主题同步重绘 / 指针几何判据 / 模板根解析 / 主题资源取值），
internal abstract class VisualOverrideBase<TButton> where TButton : ButtonBase
{
    // 所有已接管的 Link 实例，供主题切换时同步强制重绘（见 RefreshAll），
    // ⚠ 成员资格会因 Flyout 卸载而丢失，Link.Loaded 必须用 EnsureRegistered() 补回，否则遍历会永久漏项。
    protected static readonly List<BaseLink> _instances = new();

    // 弹模态对话框（文件选择器等）期间置 true：抑制所有按钮的 hover 更新（强制静止）。
    protected static bool _hoverSuppressed;

    /// <summary>强制所有已接管按钮按当前主题立即重绘。应在主题被同步设值的时刻调用 （如 MainPage 主题下拉），消除 ActualThemeChanged 异步传播造成的「旧白底闪一帧」。</summary>
    public static void RefreshAll()
    {
        foreach (var link in _instances) link.ForceRefresh();
    }

    /// 按实时指针位置重算本类所有已接管按钮的 hover —— 几何判据，只点亮指针真正落在里面的那颗（天然独占）。
    public static void UpdateHoverFromPointer(PointerRoutedEventArgs e)
    {
        foreach (var link in _instances)
        {
            if (link.ExcludeFromPageHoverDrive) continue;
            link.SetHoverFromPointer(e);
        }
    }

    /// <summary>弹模态对话框期间调用：抑制全部已接管按钮的 hover 更新（强制静止）。 仅应在「点击按钮即弹模态对话框、鼠标随对话框离开按钮」的路径里 true → await → false 包裹， 如打开/保存文件选择器。置 true 时立即把所有已接管按钮清回静止；</summary>
    public static void SetHoverSuppressed(bool suppressed)
    {
        _hoverSuppressed = suppressed;
        if (suppressed)
        {
            foreach (var link in _instances) link.SetHover(false);
        }
    }

    /// 对指定的那一颗已接管按钮开启 hover「冷处理」：先立即把它清回静止， 再在窗口期内拒绝一切"点亮"请求（见 ）。
    protected static void BeginHoverHoldOffFor(ButtonBase? target, int windowMs = 900)
    {
        if (target == null) return;
        long until = Environment.TickCount64 + windowMs;
        foreach (var link in _instances)
        {
            if (!ReferenceEquals(link.Button, target)) continue;
            link.BeginHoverHoldOff(until);
        }
    }

    /// <summary>接管逻辑的共享内核。具体按钮类型（Button / ToggleButton）由 参数化； 类型相关的状态回调（如 ToggleButton.IsChecked）由 补挂，状态表由 各自实现。</summary>
    public abstract class BaseLink
    {
        protected readonly TButton _button;
        protected bool _hover;
        protected bool _lastPressed;
        protected bool _lastHover;
        protected bool _dirty;
        protected ContentPresenter? _root;

        // hover「冷处理」到期时间戳（TickCount64，0 = 未启用；见 BeginHoverHoldOffFor）。
        private long _holdOffUntilTicks;

        protected BaseLink(TButton button) { _button = button; }

        internal TButton Button => _button;

        internal void BeginHoverHoldOff(long untilTicks)
        {
            _holdOffUntilTicks = untilTicks;
            SetHover(false);
        }

        internal void Attach()
        {
            _button.RegisterPropertyChangedCallback(
                ButtonBase.IsPressedProperty, (s, e) => Sync());

            AttachExtra();

            // ② 松手这一帧看一眼：此时状态已回写，而假 exited 与状态窜扰都还没发生
            _button.Click += (s, e) => Sync();

            // ③ 主题切换 / 反复挂载都可能让控件模板被重新应用（模板根换成新实例），
            //    并置 _dirty 强制重算 —— 否则状态都没变时会被 Apply 的早退挡掉，资源里的主题色换了也白换。
            _button.ActualThemeChanged += (s, e) => { _dirty = true; Sync(); };
            _button.Loaded += (s, e) =>
            {
                EnsureRegistered();                      // 卸载后重挂：补回实例表，否则再也清不掉
                _dirty = true;
                Sync();
            };

            // ④ 指针落点只用来维护「鼠标到底在不在按钮上」，绝不采信 IsPointerOver / PointerExited
            //    ⚠ PointerMoved 只可能是<b>真实</b>移动 —— ButtonBase 那两次"假退出 / 约 380ms 后圆谎"
            _button.PointerEntered += (s, e) => Track(e);
            _button.PointerMoved += (s, e) => { _holdOffUntilTicks = 0; Track(e); };
            _button.PointerExited += (s, e) => Track(e);
            _button.PointerPressed += (s, e) => Track(e);

            Sync();

            _instances.Add(this);
            _button.Unloaded += OnUnloaded;
        }

        /// <summary>类型相关的额外状态回调（如 ToggleButton 挂 IsCheckedProperty）。Button 无选中态 ⇒ 空实现。</summary>
        protected abstract void AttachExtra();

        private void OnUnloaded(object sender, RoutedEventArgs e) => _instances.Remove(this);

        /// 保证本 Link 仍在全局实例表 里（幂等）。
        private void EnsureRegistered()
        {
            for (int i = 0; i < _instances.Count; i++)
            {
                if (ReferenceEquals(_instances[i], this)) return;
            }
            _instances.Add(this);
        }

        internal void ForceRefresh()
        {
            _dirty = true;
            Sync();
        }

        private void Track(PointerRoutedEventArgs e) => SetHoverFromPointer(e);

        /// <summary>按指针相对本按钮的坐标几何判定 hover —— 同一可视树内、无跨树坐标系问题。 只在 hover 真变化时才置脏重绘（未变化不触发 Sync，省掉每次移动的无谓 Refresh/Apply）。 是否参与整页驱动由 决定（不是靠覆写本方法）。</summary>
        internal void SetHoverFromPointer(PointerRoutedEventArgs e)
        {
            if (_hoverSuppressed) { SetHover(false); return; }
            var pos = e.GetCurrentPoint(_button).Position;
            bool inside = pos.X >= 0 && pos.Y >= 0
                       && pos.X < _button.ActualWidth && pos.Y < _button.ActualHeight;
            SetHover(inside);
        }

        /// 是否退出「整页 hover 几何驱动」。
        internal virtual bool ExcludeFromPageHoverDrive => false;

        /// <summary>是否只信软件自身主题源、跳过视觉树遍历（默认 false = 走遍历）。 仅 popup / flyout 独立树需要置 true：该树的 FlyoutPresenter 上挂着 WinUI 自己 镜像的 ，</summary>
        protected virtual bool UseAppThemeSourceOnly => false;

        /// <summary>直接设定 hover（供页面/容器级驱动「全灭」或外部指派）； 同样只在真变化时才置脏重绘。</summary>
        internal void SetHover(bool hover)
        {
            // 弹模态对话框期间抑制 hover：任何 hover 请求都按静止处理，
            // 否则弹窗打开瞬间的游离 PointerMoved/Entered 会把按钮卡在悬浮态、且弹窗期间整页几何驱动不触发。
            if (_hoverSuppressed) hover = false;
            // 冷处理窗口内拒绝"点亮"（见 BeginHoverHoldOffFor）：挡的正是 ButtonBase 松手约 380ms 后
            if (hover && Environment.TickCount64 < _holdOffUntilTicks) hover = false;
            if (_hover == hover) return;
            _hover = hover;
            _dirty = true;
            Sync();
        }

        /// <summary>权威指派：调用方已按实时指针位置算好几何（不经过任何指针事件）， 直接设定 hover 状态，不受冷处理窗口约束（那窗口是给「可能带旧坐标的迟到事件」设的， 与本地实时几何无关）。</summary>
        internal void ForceHover(bool hover)
        {
            if (_hoverSuppressed) hover = false;
            if (_hover == hover) return;
            _hover = hover;
            _dirty = true;
            Sync();
        }

        protected void Sync()
        {
            Refresh();
            Apply();
        }

        private void Refresh()
        {
            ContentPresenter? root = null;
            if (VisualTreeHelper.GetChildrenCount(_button) > 0)
                root = VisualTreeHelper.GetChild(_button, 0) as ContentPresenter;
            _root = root;
        }

        protected abstract void Apply();

        /// <summary>供浮层强清遍历调用：仅当本项是 transparentAtRest 时才清 hover。基类空实现。</summary>
        protected virtual void ClearTransparentHoverIfAny() { }
        /// <summary>供浮层独占 hover 遍历调用：仅当本项是 transparentAtRest 时才按 hovered 置 hover。基类空实现。</summary>
        protected virtual void SetTransparentHover(Button? hovered) { }

        internal void ClearTransparentHoverState() => ClearTransparentHoverIfAny();
        internal void SetTransparentHoverState(Button? hovered) => SetTransparentHover(hovered);

        /// 取主题资源：主题字典优先，顶层字典降级为兜底。
        /// 【为什么必须反过来查】
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

        /// 确定性主题源：把"当前应是深色还是浅色"映射成 WinUI 主题字典的键名（Default=暗 / Light=浅）。
        protected string ResolvedThemeDictKey()
        {
            try
            {
                // popup / flyout 独立树：跳过遍历，直接用确定性的软件主题源（见 UseAppThemeSourceOnly 注释）。
                if (UseAppThemeSourceOnly && (Application.Current as App)?.MainWindow is MainWindow popupMw)
                    return popupMw.IsDarkTheme ? "Default" : "Light";

                FrameworkElement? e = _button;
                while (e != null)
                {
                    if (e.RequestedTheme == ElementTheme.Dark) return "Default";
                    if (e.RequestedTheme == ElementTheme.Light) return "Light";
                    e = VisualTreeHelper.GetParent(e) as FrameworkElement;
                }
                // ⚠ 兜底必须用【软件自己的主题设置】，不能直接用 Application.Current.RequestedTheme（系统设置）。
                //    ① 全链都是 Default（跟随系统）时掉到这里（popup 树已在上方 UseAppThemeSourceOnly 分支短路，
                if ((Application.Current as App)?.MainWindow is MainWindow mw)
                    return mw.IsDarkTheme ? "Default" : "Light";

                // 连窗口都取不到（极端情况）：才用系统主题判定
                return Application.Current?.RequestedTheme == ApplicationTheme.Dark ? "Default" : "Light";
            }
            catch
            {
                return _button.ActualTheme == ElementTheme.Dark ? "Default" : "Light";
            }
        }

        protected static ResourceDictionary? LookupThemeDict(ResourceDictionary resources, string themeName)
        {
            if (resources.ThemeDictionaries.TryGetValue(themeName, out var dict)) return dict as ResourceDictionary;
            return null;
        }
    }
}
