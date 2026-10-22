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

using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using System.Threading.Tasks;

// To learn more about WinUI, the WinUI project structure,

namespace GlanceRX;

/// <summary>Provides application-specific behavior to supplement the default Application class.</summary>
public partial class App : Application
{
    private Window? _window;
    public Window? MainWindow => _window;

    /// <summary>Stores a PDF file path passed via command-line arguments (file association fallback for unpackaged mode). This is used when AppInstance.</summary>
    public static string? PendingFilePath { get; set; }
    
    /// <summary>Initializes the singleton application object. This is the first line of authored code executed, and as such is the logical equivalent of mai…</summary>
    public App()
    {
        InitializeComponent();
        InstallCrashLogger();
#if DEBUG
#endif
    }

    /// <summary>全局未处理异常兜底：把任何会杀掉进程的异常原样写进 /crash_*.log， 便于复现后定位（WER 的 appcrash 归档在托管异常 fail-fast 下常缺失）。 写完后【不】吞异常——保留默认终止，确保崩溃可复现且日志已落盘。</summary>
    private void InstallCrashLogger()
    {
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Diagnostics.CrashLog.Capture("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Diagnostics.CrashLog.Capture("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
            // Microsoft.UI.Xaml.Application 级（UI 线程 XAML 回调等）。不设置 e.Handled，
            // 保留默认终止行为，保证崩溃可复现、日志已落盘。
            UnhandledException += (s, e) =>
                Diagnostics.CrashLog.Capture("Application.UnhandledException", e.Exception);
        }
        catch { }
    }

    /// <summary>Invoked when the application is launched.</summary>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 启动时后台回收便携版缓存目录（程序本体目录下的 Cache），防止无限增长。
        _ = Task.Run(() => Services.CacheManager.Cleanup());

        // 启动时异步清理 WinUI/.NET 依赖带来的多余卫星资源语言文件夹。
        _ = Services.LanguageFolderTrimmer.RunAsync();

        _window = new MainWindow();

        // Apply saved theme BEFORE the window is activated to prevent white flash
        ApplySavedTheme();

        _window.Activate();

        // 若本次新建窗口需从记录恢复"最大化"状态：真正的 Maximize 必须放在 Activate 之后。
        if (_window is MainWindow mw && mw.ShouldRestoreMaximized &&
            mw.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    /// <summary>Reads the saved theme preference from settings and applies it to the window content before activation, preventing white flash.</summary>
    private void ApplySavedTheme()
    {
        if (_window?.Content is not FrameworkElement rootElement) return;

        try
        {
            var settingsService = new Services.SettingsService();
            string themeStr = settingsService.AppTheme;

            if (Enum.TryParse(themeStr, out ElementTheme theme))
            {
                rootElement.RequestedTheme = theme;

                // Also set the window background and title bar to match the theme before activation
                if (_window is MainWindow glanceWindow)
                {
                    bool isDark = theme == ElementTheme.Dark ||
                                  (theme == ElementTheme.Default &&
                                   Application.Current.RequestedTheme == ApplicationTheme.Dark);
                    glanceWindow.UpdateTitleBarTheme(isDark);
                    glanceWindow.UpdateWindowBackground(theme);
                }
            }
        }
        catch (System.Exception)
        {
        }
    }
}