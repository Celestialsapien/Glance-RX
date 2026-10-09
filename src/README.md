# Glance Frontend (C# WinUI 3)

User interface and application logic for Glance-RX PDF viewer. Handles rendering, annotations, and all user interactions.

## What C# Does Here

Glance's frontend is built in C# with WinUI 3 to provide:

- **Native Windows 11 UI:** acrylic flyouts, Fluent Design, native aesthetics
- **Rapid Development:** High-level C# with .NET 10.0 ecosystem
- **Modern XAML:** Declarative UI with data binding and MVVM
- **FFI Bridge:** P/Invoke interop with Rust backend (`glance_native.dll`)

## Architecture

```
src/
├── MainPage.xaml(.cs)           # Main UI + page rendering
├── App.xaml(.cs)                # Application entry point
├── Interop/
│   └── GlanceRXNative.cs          # P/Invoke FFI declarations
├── Services/
│   ├── PdfRenderService.cs      # PDF rendering wrapper
│   ├── PdfExportService.cs      # 真实 PDF /Ink 注解对象读写（文件即唯一真相源）
│   ├── CacheManager.cs          # 便携缓存目录（Temp/诊断/清理）
│   └── ViewStateStore.cs        # 阅读位置/缩放状态持久化
├── ViewModels/
│   ├── PdfPageViewModel.cs      # Page data + UI state
│   └── AnnotationViewModel.cs   # Annotation data
├── Assets/                      # Images, icons, resources
├── Properties/                  # App configuration
└── build/                       # Build automation scripts
```

## Key Components

### MainPage.xaml / MainPage.xaml.cs
- **PDF Display:** ScrollViewer with virtualized page list
- **Annotations:** Canvas overlay for drawing, highlighting, notes
- **Toolbar:** Edit modes (Pen, Highlight, Note), zoom, rotation
- **Sidebar:** Page thumbnails for navigation
- **Lazy Loading:** First 10 pages instant, remainder in background

### Services

#### PdfRenderService.cs
- `InitializeAsync(filePath)` - Load PDF document
- `RenderPageAsync(pageIndex)` - Render single page to bitmap

#### PdfExportService.cs
- 批注以 PDF 原生 /Ink 注解对象写入（非破坏性、可移除、可显隐）
- `/GlanceAnno` 标记幂等：保存先移除旧注解再重加，绝不堆叠
- `ReadGlanceInkAnnotations` 打开时读回真实注解重建叠加层（文件即唯一真相源，无 JSON/副本）

### Interop Layer

#### GlanceRXNative.cs
- P/Invoke declarations for `glance_native.dll`
- Struct definitions: `EngineCreateResult`, `Response`
- DllImportResolver for dynamic DLL discovery
- Memory management (`memory_free()`)

## Building

```bash
# Prerequisites
dotnet tool install --global Microsoft.Windows.CsWin32

# Build (Pass the certificate password if using a local PFX to avoid signature warnings)
dotnet build -p:PackageCertificatePassword=YourPassword

# Build Release / Publish (Required for generating a signed MSIX package)
dotnet publish -c Release -r win-x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageDir="C:\Users\adria\GitHub\Glance\src\AppPackages\" -p:SelfContained=true -p:PackageCertificatePassword=YourPassword

# Run (Pass the certificate password to register the development package)
dotnet run -p:PackageCertificatePassword=YourPassword
```

## Requirements

- **.NET 10.0 SDK** - [dotnet.microsoft.com](https://dotnet.microsoft.com)
- **Windows App SDK 1.5+** - For WinUI 3
- **Visual Studio 2022** (optional) - For IDE development

## Testing

Tests located in `/tests/`:

- `TestDllLoad/` - Verify `glance_native.dll` loads
- `CHECK_DLL_DEPENDENCIES.ps1` - Check missing dependencies
- `test-phase2.cs` - Persistence layer validation

Run from project root:
```bash
dotnet script src/tests/test-phase2.cs
.\src\tests\CHECK_DLL_DEPENDENCIES.ps1
cd src/tests/TestDllLoad && dotnet run
```

## Development Workflow

1. **Edit XAML** (`MainPage.xaml`) - Layout, bindings
2. **Update C#** (`MainPage.xaml.cs`) - Event handlers, logic
3. **Call Rust** (via GlanceRXNative FFI) - PDF operations
4. **Test locally** (`dotnet run`) - Launch app
5. **Commit** - Git push to GitHub

## Dependencies

- **Microsoft.WindowsAppSDK** 2.2.0 - WinUI 3 framework
- **System.Text.Json** - Built-in (annotation serialization)
- **Windows.Data.Pdf** - Built-in (PDF fallback rendering)

See `Glance.csproj` for full dependency list.

## Philosophy

- **Fluent Design First:** Respect Windows 11 native aesthetics
- **Performance Conscious:** Lazy loading, virtualization, async/await
- **User-Focused:** Keyboard shortcuts, smooth transitions, responsive
- **Minimal Dependencies:** Use built-in .NET libraries when possible
