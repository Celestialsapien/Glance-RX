using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using System;
using Microsoft.UI.Input;

namespace Glance;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool _isClosingConfirmed = false;

    public MainWindow()
    {
        InitializeComponent();


        // Set default window size and position BEFORE the window is shown,
        // to avoid the initial flash at default size/position.
        // Height = screen working area height (screen minus taskbar),
        // Width  = half of screen working area width,
        // Position = top-right corner of the screen.
        try
        {
            var displayArea = Microsoft.UI.Windowing.DisplayArea.Primary;
            if (displayArea != null)
            {
                var workArea = displayArea.WorkArea;
                var appWindow = this.AppWindow;

                int windowWidth = workArea.Width / 2;
                int windowHeight = workArea.Height;
                int windowX = workArea.X + (workArea.Width - windowWidth);
                int windowY = workArea.Y;

                appWindow.MoveAndResize(new Windows.Graphics.RectInt32
                {
                    X = windowX,
                    Y = windowY,
                    Width = windowWidth,
                    Height = windowHeight
                });
            }
        }
        catch { }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Set title bar colors to match the current system theme immediately
        // (prevents white/dark flash). When the user later picks a saved Light/Dark
        // theme, ApplySavedTheme() / ActualThemeChanged refresh these.
        bool systemDark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        UpdateTitleBarTheme(isDark: systemDark);

        // Initialize the window background to the system theme; ApplySavedTheme()
        // will re-apply the persisted preference right before activation.
        UpdateWindowBackground(ElementTheme.Default);

        // 冷启动白闪终极修复（DWM 遮蔽）：Activate 前把窗口对 DWM/shell 隐藏，
        // 首个 XAML 合成帧呈现后再揭开——用户只看到深色窗口直接出现（见
        // ArmColdStartCloak 注释）。必须在 Activate 之前武装，故放构造器末尾。
        ArmColdStartCloak();
        CompositionTarget.Rendering += OnFirstFrameReady;
        WindowRoot.Loaded += OnRootLoadedReveal;
        if (_uncloakSafetyTimer == null)
        {
            _uncloakSafetyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3000) };
            _uncloakSafetyTimer.Tick += (s, e) => CompleteColdStartReveal();
            _uncloakSafetyTimer.Start();
        }

        // Live-follow the OS theme while "跟随系统" (ElementTheme.Default) is active:
        // ActualTheme only changes when the effective theme changes (Default mode follows
        // the system; explicit Light/Dark keep ActualTheme pinned, so no spurious refresh).
        if (WindowRoot != null)
        {
            WindowRoot.ActualThemeChanged += (s, e) =>
            {
                try
                {
                    UpdateTitleBarTheme(WindowRoot.ActualTheme == ElementTheme.Dark);
                    UpdateWindowBackground(WindowRoot.ActualTheme);
                }
                catch { }
            };
        }

        // Set the window icon using absolute path lookup
        string iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
        else
        {
            AppWindow.SetIcon("Assets/AppIcon.ico");
        }

        // Check for file activation arguments
        Windows.Storage.StorageFile? fileToOpen = null;
        try
        {
            var activatedArgs = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activatedArgs != null && activatedArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File)
            {
                if (activatedArgs.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs && fileArgs.Files.Count > 0)
                {
                    fileToOpen = fileArgs.Files[0] as Windows.Storage.StorageFile;
                }
            }
        }
        catch { }

        // Fallback: check command-line arguments for a PDF file path.
        // This handles unpackaged mode where file association passes the file path as a command-line argument.
        if (fileToOpen == null)
        {
            try
            {
                var cmdArgs = Environment.GetCommandLineArgs();
                // args[0] is the executable path, args[1] is the first argument (the PDF file path)
                if (cmdArgs.Length > 1)
                {
                    string pdfPath = cmdArgs[1];
                    if (System.IO.File.Exists(pdfPath) &&
                        System.IO.Path.GetExtension(pdfPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        App.PendingFilePath = pdfPath;
                    }
                }
            }
            catch { }
        }

        // Navigate the root frame to the main page on startup.
        // --probe 参数：进入"矢量直绘"原型页（MuPDF display list → SVG → 每帧按矩阵
        // 矢量重绘），用于验证"任意倍率全矢量清晰 + 矢量重绘帧率"两个命题。
        var cmdLine = Environment.GetCommandLineArgs();
        bool probeMode = Array.IndexOf(cmdLine, "--probe") >= 0;
        RootFrame.Navigate(probeMode ? typeof(VectorProbePage) : typeof(MainPage), fileToOpen);

        // Subscribe to closing event to check for unsaved changes
        this.AppWindow.Closing += AppWindow_Closing;
        this.VisibilityChanged += MainWindow_VisibilityChanged;
        this.Activated += MainWindow_Activated;
        this.Closed += MainWindow_Closed;
    }

    /// <summary>
    /// When the pointer enters the window title bar, slide the MainPage top toolbar down.
    /// </summary>
    private void AppTitleBar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        (RootFrame.Content as MainPage)?.SetTitleBarHover(true);
    }

    /// <summary>
    /// Leaving the title bar arms the toolbar's auto-hide. If the pointer is heading into
    /// the toolbar itself, the toolbar's own PointerEntered cancels the pending hide, so
    /// crossing the seam never makes the bar flicker.
    /// </summary>
    private void AppTitleBar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        (RootFrame.Content as MainPage)?.SetTitleBarHover(false);
    }

    /// <summary>
    /// The pointer left the window's whole client area. This is the case the title bar's own
    /// PointerExited misses: moving the mouse straight up off the title bar and out of the
    /// window. Without it the toolbar would stay open with the pointer nowhere near it.
    /// </summary>
    private void WindowRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Debounced, not immediate: this also fires when the pointer moves into a Popup
        // (the pen-options capsule lives in the XamlRoot's popup layer, outside this Grid),
        // and the capsule's own PointerEntered must be able to cancel the pending hide.
        (RootFrame.Content as MainPage)?.OnWindowPointerExited();
    }

    /// <summary>
    /// Losing window activation (alt-tab, clicking another app, opening a system dialog) must
    /// retract the top toolbar: no further pointer events reach us in that state, so without
    /// this the bar could stay stuck open until the next pointer move.
    /// </summary>
    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        var mainPage = RootFrame.Content as MainPage;
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            mainPage?.OnAppDeactivated();
        else
            mainPage?.OnAppActivated();
    }

    /// <summary>窗口当前请求的主题（ApplySavedTheme 已按保存偏好设置；Default=跟随系统）。
    /// 子控件读取它可避免依赖 ActualTheme 的异步传播（首帧前可能仍是系统主题）。</summary>
    public ElementTheme WindowRequestedTheme => WindowRoot?.RequestedTheme ?? ElementTheme.Default;

    /// <summary>窗口当前有效的是否深色主题（保存偏好 + 系统主题综合判定）。</summary>
    public bool IsDarkTheme
    {
        get
        {
            var req = WindowRequestedTheme;
            return req == ElementTheme.Dark ||
                   (req == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        }
    }

    /// <summary>
    /// Updates the window background to match the current theme.
    /// </summary>
    public void UpdateWindowBackground(ElementTheme theme)
    {
        try
        {
            if (this.Content is not Grid rootGrid) return;

            // ElementTheme.Default (跟随系统) must resolve against the app-level theme,
            // which itself follows the OS now that App.xaml no longer pins RequestedTheme.
            bool isDark = theme == ElementTheme.Dark ||
                          (theme == ElementTheme.Default &&
                           Application.Current.RequestedTheme == ApplicationTheme.Dark);

            if (!isDark)
            {
                rootGrid.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA));
            }
            else
            {
                rootGrid.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1C));
            }

            // 原生窗口层也同步为主题实色，消除"窗口可见 → 首个 XAML 合成帧"间隙的白色（见
            // ApplyNativeWindowBackground 注释）。无论 XAML 主题资源何时就位，间隙都是深色。
            ApplyNativeWindowBackground(isDark);
        }
        catch { }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 冷启动白闪修复：
    // WinUI 3 在首个 XAML 合成帧呈现前，原生窗口表面默认以白色填充
    // （microsoft-ui-xaml#5148 确认该背景默认固定为白色）。冷启动时自包含
    // 运行时 + Windows App SDK 的 DLL 全部从磁盘冷加载，"窗口可见 → 首帧合成"
    // 的间隙被拉长到肉眼可察，深色模式下就是一次刺眼的全窗口白闪；热启动
    // 首帧几乎瞬间呈现，所以察觉不到。
    // 修复：把窗口类的背景画刷（GCLP_HBRBACKGROUND，DefWindowProc 处理
    // WM_ERASEBKGND 时用它填充）直接替换为主题实色，让这段间隙也是深色。
    // 窗口内容本身是 XAML 合成（不透明深色根 Grid），正常绘制期根本不会用到
    // 类画刷，因此只影响首帧前的空白间隙，无副作用。
    // ─────────────────────────────────────────────────────────────────────
    private const int GCLP_HBRBACKGROUND = -10;
    private IntPtr _nativeBgBrush = IntPtr.Zero;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetClassLongPtr")]
    private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetClassLong")]
    private static extern int SetClassLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    private void ApplyNativeWindowBackground(bool isDark)
    {
        try
        {
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
            if (hwnd == IntPtr.Zero) return;

            // COLORREF 是 0x00BBGGRR：#1C1C1C / #FAFAFA 的 RGB 三通道相同，故值不变。
            uint color = isDark ? 0x001C1C1Cu : 0x00FAFAFAu;
            var brush = CreateSolidBrush(color);
            if (brush == IntPtr.Zero) return;

            // 替换类画刷前先释放旧画刷，避免反复切主题时 GDI 对象泄漏（进程存活期持有当前画刷）。
            if (_nativeBgBrush != IntPtr.Zero)
                DeleteObject(_nativeBgBrush);
            _nativeBgBrush = brush;

            if (IntPtr.Size == 8)
                SetClassLongPtr64(hwnd, GCLP_HBRBACKGROUND, brush);
            else
                SetClassLong32(hwnd, GCLP_HBRBACKGROUND, brush.ToInt32());
        }
        catch
        {
            // 32 位下 SetClassLongPtr 入口不存在等任何失败：静默降级（XAML 根背景仍覆盖窗口）。
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 冷启动白闪终极修复（DWM 窗口遮蔽）：
    // 上一条 GCLP_HBRBACKGROUND 类画刷方案实测无效——WinUI 3 的窗口首帧前
    // 表面固定为白色（microsoft-ui-xaml#5148），且不走窗口类的 WM_ERASEBKGND
    // 画刷。改用 Chromium/Edge 同款手法：窗口在 Activate 前即被 DWM 遮蔽
    // （DWMWA_CLOAK=TRUE，对 shell/任务栏/屏幕完全不可见），等首个 XAML
    // 合成帧呈现后再解除遮蔽。用户只会看到深色窗口直接出现，任何中间颜色
    // 都不会被看到。三重揭盖信号保证永不出现"窗口永久不可见"：
    //   ① 首个 CompositionTarget.Rendering（= 首帧已合成，最理想）；
    //   ② 根元素 Loaded（内容已就绪，兜底）；
    //   ③ 3 秒安全定时器（绝对兜底）。
    // ─────────────────────────────────────────────────────────────────────
    private const int DWMWA_CLOAK = 13;
    private bool _coldStartCloaked = false;
    private bool _coldStartCloakArmed = false;
    private DispatcherTimer? _uncloakSafetyTimer;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>Activate 前调用：把窗口对 DWM 遮蔽，隐藏任何首帧前的白色表面。</summary>
    private void ArmColdStartCloak()
    {
        if (_coldStartCloakArmed) return;
        _coldStartCloakArmed = true;
        try
        {
            var hwnd = GetWindowHandleSafely();
            if (hwnd == IntPtr.Zero) return;
            int cloak = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
            _coldStartCloaked = true;
        }
        catch { }
    }

    private void DisarmColdStartCloak()
    {
        if (!_coldStartCloaked) return;
        _coldStartCloaked = false;
        try
        {
            var hwnd = GetWindowHandleSafely();
            if (hwnd == IntPtr.Zero) return;
            int cloak = 0;
            DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref cloak, sizeof(int));
        }
        catch { }
    }

    /// <summary>首个合成帧已呈现（内容已是深色）→ 解除遮蔽。</summary>
    private void OnFirstFrameReady(object? sender, object e) => CompleteColdStartReveal();

    /// <summary>根内容已加载（兜底，防首帧渲染信号未触发）→ 解除遮蔽。</summary>
    private void OnRootLoadedReveal(object sender, RoutedEventArgs e) => CompleteColdStartReveal();

    /// <summary>幂等揭盖：退订所有信号、停掉安全定时器、解除 DWM 遮蔽。</summary>
    private void CompleteColdStartReveal()
    {
        CompositionTarget.Rendering -= OnFirstFrameReady;
        if (WindowRoot != null) WindowRoot.Loaded -= OnRootLoadedReveal;
        if (_uncloakSafetyTimer != null)
        {
            _uncloakSafetyTimer.Stop();
            _uncloakSafetyTimer = null;
        }
        DisarmColdStartCloak();
    }

    /// <summary>取窗口 HWND：AppWindow 在构造期即已创建，Id 恒可用。</summary>
    private IntPtr GetWindowHandleSafely()
    {
        try { return Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id); }
        catch { return IntPtr.Zero; }
    }

    /// <summary>
    /// Updates the title bar button colors to match the current theme.
    /// Call this immediately when the window is created and whenever the theme changes.
    /// </summary>
    /// <param name="isDark">True for dark theme colors, false for light theme.</param>
    public void UpdateTitleBarTheme(bool isDark)
    {
        try
        {
            var titleBar = AppWindow.TitleBar;

            if (isDark)
            {
                // Dark theme: light buttons on dark background
                titleBar.ButtonForegroundColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF); // White
                titleBar.ButtonBackgroundColor = Color.FromArgb(0x00, 0x00, 0x00, 0x00); // Transparent
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
                titleBar.ButtonHoverForegroundColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF); // White
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);
                titleBar.ButtonPressedForegroundColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF); // White
                titleBar.ForegroundColor = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF); // White
                titleBar.BackgroundColor = Color.FromArgb(0x00, 0x00, 0x00, 0x00); // Transparent
                titleBar.InactiveForegroundColor = Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
                titleBar.InactiveBackgroundColor = Color.FromArgb(0x00, 0x00, 0x00, 0x00);
            }
            else
            {
                // Light theme: dark buttons on light background
                titleBar.ButtonForegroundColor = Color.FromArgb(0xFF, 0x00, 0x00, 0x00); // Black
                titleBar.ButtonBackgroundColor = Color.FromArgb(0x00, 0x00, 0x00, 0x00); // Transparent
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x19, 0x00, 0x00, 0x00);
                titleBar.ButtonHoverForegroundColor = Color.FromArgb(0xFF, 0x00, 0x00, 0x00); // Black
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x33, 0x00, 0x00, 0x00);
                titleBar.ButtonPressedForegroundColor = Color.FromArgb(0xFF, 0x00, 0x00, 0x00); // Black
                titleBar.ForegroundColor = Color.FromArgb(0xFF, 0x00, 0x00, 0x00); // Black
                titleBar.BackgroundColor = Color.FromArgb(0x00, 0x00, 0x00, 0x00); // Transparent
                titleBar.InactiveForegroundColor = Color.FromArgb(0x99, 0x00, 0x00, 0x00);
                titleBar.InactiveBackgroundColor = Color.FromArgb(0x00, 0x00, 0x00, 0x00);
            }
        }
        catch { }
    }

    /// <summary>
    /// Sets the text shown in the custom title bar (e.g. "Glance — filename.pdf").
    /// </summary>
    public void SetWindowTitle(string title)
    {
        try
        {
            AppTitleBar.Title = title;
        }
        catch { }
    }

    /// <summary>
    /// 暴露自定义标题栏控件，供 MainPage 计算其屏幕矩形，从而把画笔圆环光标排除在标题栏区域之外
    /// （标题栏恢复原生鼠标，圆环仅保留在窗口主体内容区）。
    /// </summary>
    public Microsoft.UI.Xaml.Controls.TitleBar? PenTitleBar => AppTitleBar;

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern int EmptyWorkingSet(IntPtr hwProcess);

    private void MainWindow_VisibilityChanged(object sender, WindowVisibilityChangedEventArgs args)
    {
        if (!args.Visible)
        {
            // Window is minimized or hidden. Perform garbage collection
            // and trim working set to drop memory usage to 7-9MB, matching UWP apps.
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                EmptyWorkingSet(currentProcess.Handle);
            }
            catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 关闭流程说明：
    //
    // AppWindow.Closing 事件在用户点击关闭按钮时触发。
    // 关键：绝不在 UI 线程同步上下文中调用 Environment.Exit(0)——
    // WinUI 3 的 AppWindow 在 Closing 事件期间持有内部锁，
    // Environment.Exit(0) 触发的托管清理会尝试获取该锁，导致死锁。
    //
    // 最终方案：使用 kernel32!TerminateProcess 直接在内核层终止进程，
    // 跳过 ExitProcess 的 DLL_PROCESS_DETACH 回调（Rust FFI 原生库的
    // DllMain 会在此阶段阻塞导致死锁），保证进程立即终止。
    // ─────────────────────────────────────────────────────────────────────

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    /// <summary>
    /// 强制终止进程。TerminateProcess 直接在内核层终止进程，
    /// 跳过 ExitProcess 会执行的 DLL_PROCESS_DETACH 回调，
    /// 避免 Rust FFI 原生库的 DllMain 阻塞导致死锁。
    /// </summary>
    private static void ForceTerminateProcess()
    {
        TerminateProcess(GetCurrentProcess(), 0);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        ForceTerminateProcess();
    }

    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_isClosingConfirmed) return;

        if (RootFrame.Content is MainPage mainPage)
        {
            if (mainPage.HasUnsavedChanges)
            {
                args.Cancel = true;
                _ = ShowCloseDialog(mainPage);
            }
            else
            {
                args.Cancel = true;
                _isClosingConfirmed = true;
                HideAndTerminate(mainPage);
            }
        }
    }

    /// <summary>
    /// 隐藏窗口后再执行清理与进程终止，避免关闭时出现白色闪烁。
    /// TerminateProcess 终止进程的瞬间，OS 销毁窗口时可能会短暂显示
    /// 默认白色背景；提前调用 AppWindow.Hide() 确保用户不会看到这一闪烁。
    /// </summary>
    private void HideAndTerminate(MainPage mainPage)
    {
        // 先保存视图状态【再】隐藏窗口：此时窗口仍可见、ScrollViewer 布局有效，_live* 是最后一次
        // 真实交互落点。若先 Hide，窗口隐藏可能触发布局失效/ViewChanged 把 _live* 同步成 0
        // （Viewer_ViewChanged 的 idle 同步没有窗口隐藏守卫）→ 保存的偏移被清零 → 重开位置丢失。
        // CleanupForExit 的引擎 Dispose 走异步 Task.Run 不阻塞 UI，紧接着 Hide+Terminate 用户无感。
        try { mainPage.CleanupForExit(); } catch { }
        // 同步隐藏窗口，避免在 DispatcherQueue 调度期间出现闪烁
        try { this.AppWindow.Hide(); } catch { }
        this.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
        {
            ForceTerminateProcess();
        });
    }

    private async System.Threading.Tasks.Task ShowCloseDialog(MainPage mainPage)
    {
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = Glance.Services.LocalizationService.Get("UnsavedChangesTitle"),
            Content = Glance.Services.LocalizationService.Get("UnsavedChangesContent"),
            PrimaryButtonText = Glance.Services.LocalizationService.Get("SaveAndExit"),
            SecondaryButtonText = Glance.Services.LocalizationService.Get("ExitWithoutSaving"),
            CloseButtonText = Glance.Services.LocalizationService.Get("Cancel"),
            XamlRoot = this.Content.XamlRoot,
            // ContentDialog 不自动跟随窗口根元素的 RequestedTheme（WinUI 3 已知问题），
            // 必须显式携带根元素解析后的 ActualTheme，否则与主界面主题不一致。
            RequestedTheme = (this.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default
        };

        // 对话框为前台模态层：弹出期间整个窗口恢复原生鼠标（避免画笔圆环覆盖在对话框上）。
        // 关闭后解除抑制；若用户取消则画笔模式圆环会自动重新出现。
        mainPage.SuppressPenRing();
        try
        {
            var result = await dialog.ShowAsync();
            if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                // 使用正常保存流程（与 Ctrl+S 相同），等待完成后再退出。
                // 关闭窗口触发的保存不弹结果小弹窗（窗口即将关闭，无需提示）。
                bool saved = await mainPage.SaveOriginalPdfAsync(notify: false);
                if (saved)
                {
                    _isClosingConfirmed = true;
                    HideAndTerminate(mainPage);
                }
                // 保存失败或用户取消另存为时，不退出，保持窗口打开
            }
            else if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Secondary)
            {
                // "不保存退出"统一处理：加密/只读文档清空 JSON 草稿；普通可写 PDF 把 JSON 叠加层
                // 回退到上次保存基线，使下次打开回到上一次保存的状态（见 MainPage.DiscardUnsavedOnExit）。
                mainPage.DiscardUnsavedOnExit();
                _isClosingConfirmed = true;
                HideAndTerminate(mainPage);
            }
            // If result is CloseButton (Cancel), do nothing (app stays open)
        }
        catch (Exception)
        {
            _isClosingConfirmed = true;
            HideAndTerminate(mainPage);
        }
        finally
        {
            mainPage.ResumePenRing();
        }
    }
}
