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

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Glance;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    public Window? MainWindow => _window;

    /// <summary>
    /// Stores a PDF file path passed via command-line arguments (file association fallback for unpackaged mode).
    /// This is used when AppInstance.GetActivatedEventArgs() does not return file activation args.
    /// </summary>
    public static string? PendingFilePath { get; set; }
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // 启动时后台回收便携版缓存目录（程序本体目录下的 Cache），防止无限增长。
        // 不在 OnLaunched 同步执行：Cleanup 需遍历/试探打开文件，可能拖慢窗口显示；
        // 它只删 Cache 目录里"安全可删"的临时文件，后台执行安全。
        _ = Task.Run(() => Services.CacheManager.Cleanup());

        // 启动时异步清理 WinUI/.NET 依赖带来的多余卫星资源语言文件夹。
        // 此操作在后台线程执行，不阻塞窗口显示；仅扫描 glance.exe 同级目录。
        _ = Services.LanguageFolderTrimmer.RunAsync();

        _window = new MainWindow();

        // Apply saved theme BEFORE the window is activated to prevent white flash
        ApplySavedTheme();

        _window.Activate();
    }

    /// <summary>
    /// Reads the saved theme preference from settings and applies it
    /// to the window content before activation, preventing white flash.
    /// </summary>
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