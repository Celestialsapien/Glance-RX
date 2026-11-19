# Glance 前端（C# / WinUI 3）

Glance-RX 的 WinUI 3 外壳与应用逻辑：页面渲染、批注手感、白板、设置与全部用户交互。
后端渲染、数据落盘约定与构建门槛见仓库根目录的 [README.md](../README.md)，这里只说明前端模块布局。

## 模块布局

```
src/
├── MainPage.xaml(.cs)           # 主界面：工具栏 / 侧栏 / 欢迎页 / 设置 / 指南 / 打印
├── MainWindow.xaml(.cs)         # 窗口壳：标题栏、窗口几何、图标、拖放入口、关闭流程
├── App.xaml(.cs)                # 应用入口、主题、语言
├── GpuPageView.xaml(.cs)        # GPU 阅读视图：瓦片合成、滚动/缩放、指针手势、笔迹与白板交互
├── CanvasMarkdown.cs            # 卡片文字的 Markdown 行内样式解析
├── SelfDrawnTextBox / SelfDrawnContextMenu.cs
│                                # 白板卡片编辑框与自绘文本菜单
├── Tuning.cs / config.txt       # 运行时手感参数（默认值 / 可编辑模板）
├── Interop/
│   ├── GlanceRXNative.cs        # P/Invoke FFI 声明（glance_native.dll）
│   ├── CursorInterop.cs         # 系统光标
│   └── ShellFileOps.cs          # 文件管理器操作
├── Services/
│   ├── PdfRenderService.cs      # PDF 文档加载与页面渲染封装
│   ├── PdfOutlineLoader.cs      # 大纲（目录）加载
│   ├── PdfPrintService.cs       # 打印（把批注合成进打印结果）
│   ├── DocumentStore.cs         # 阅读位置 / 缩放 / 面板状态持久化
│   ├── CanvasStore.cs           # 单份画布读写（.gcanvas）
│   ├── CanvasLibrary.cs         # 画布库（多份画布管理）
│   ├── CacheManager.cs          # 便携缓存目录与清理
│   ├── LocalizationService.cs   # 中 / 英 / 西 三语文案
│   ├── SettingsService.cs       # 设置持久化
│   ├── AppPaths.cs              # 便携目录解析（全部基于 AppContext.BaseDirectory）
│   ├── Motion.cs                # 弹簧运动引擎
│   ├── ButtonVisualOverride.cs / ToggleButtonVisualOverride.cs / VisualOverrideBase.cs
│                                # 按钮视觉状态接管（绕开原生 VSM）
│   └── LanguageFolderTrimmer.cs # 语言资源目录裁剪
├── ViewModels/
│   ├── PdfPageViewModel.cs
│   └── AnnotationViewModel.cs
├── Assets/                      # 图标与品牌图（AppIcon.ico / WelcomeBrand.png）
└── build/                       # 构建辅助脚本
```

## 关键约定

* **PDF 全程只读**：笔迹与白板卡片存 `.gcanvas`，不写回 PDF。保存走 `MainPage.SaveChangesAsync()`，不是 PDF 导出。
* **视觉状态接管**：自定义按钮不用原生 `VisualStateManager`（山区状态机在弹窗/点击时会闪档），统一由 `ButtonVisualOverride` / `ToggleButtonVisualOverride` 按坐标与按下状态写属性。
* **坐标同源**：指针 → 页面换算走 `GpuPageView` 的 `GetViewState()` + 视图尺寸，不读 `Viewer.ZoomFactor/Offset` 的中间态。
* **便携数据**：任何写盘路径都从 `AppPaths` / `AppContext.BaseDirectory` 推导，禁止写系统 AppData / Temp。
* **文案集中**：新增界面文本一律进 `LocalizationService` 三语字典，不在 XAML / C# 里硬编码。

## 构建

```bash
# 从仓库根目录执行
dotnet build src/Glance.csproj -c Debug
dotnet build src/Glance.csproj -c Release
```

Rust 后端由 `src/Glance.csproj` 的 `BuildNativeLib` 目标按需自动编译；改过 `native/**` 时才需关注 `cargo`。
本工程为非打包应用，无证书 / MSIX / 签名步骤。
