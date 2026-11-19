# Glance-RX

[![License: AGPL-3.0](https://img.shields.io/badge/License-AGPL--3.0-blue.svg)](LICENSE)
[![Framework: WinUI 3](https://img.shields.io/badge/Framework-WinUI_3-purple.svg)](https://learn.microsoft.com/zh-cn/windows/apps/winui/winui3/)
[![Platform: Windows 10+](https://img.shields.io/badge/Platform-Windows_10%2B-blue.svg)](https://www.microsoft.com/windows/)
[![Language: C# / Rust](<https://img.shields.io/badge/Language-C%23%20%2F%20Rust-green.svg>)](https://learn.microsoft.com/dotnet/csharp/)

Glance-RX 是一款改良自 jonas1ara 的 Glance 的高性能 PDF 阅读器，基于 WinUI 3 外壳，搭配 Rust 原生渲染后端（MuPDF）与 Win2D GPU 合成。

> **完全免安装、可移植**：self-contained、非打包形态（永不制作 MSIX）。用户数据只写入 exe 同级目录的 `GlanceData` 与画布文件，**不写入系统 AppData 或系统 Temp**（仅当便携目录不可写时，崩溃日志会降级写到系统 Temp）。

---

## 核心特性

**阅读与渲染**

* **渲染内核（Sumatra 内核 + MuPDF 后端）：** 将 Sumatra PDF 的 `RenderCache` 渲染/内存管理引擎 1:1 移植为 C# 端的 `TileRenderCache`（物理像素瓦片、按需预取、单级缓存），后端由 Rust 封装的 MuPDF 经 FFI 提供页位图，Win2D (`CanvasVirtualControl`) 在 GPU 上做合成。
* **统一运动引擎：** 弹簧模型驱动所有缩放与平移；滚轮滚动、双击/按钮/Ctrl+滚轮缩放、右键拖拽缩放各有独立手感参数。
* **布局：** 单页、双页、书籍对开三种版面；纵向 / 横向 布局；90°旋转；连续平滑滚动；方向键 / 空格 / PageUp·Down / Home·End 翻页定位。
* **缩放：** 默认缩放范围 **0.1×–4.0×**；`双击`在「适应宽度 ↔ 实际大小 100%」之间切换；`F 键`→适应宽度；`Ctrl + 数字0` →实际大小 100%；`Ctrl + 符号+/-` 缩放；点击右下角悬浮`缩放胶囊`的按钮、快速菜单缩放；`按住右键拖拽`缩放，上拖放大，下拖缩小。
* **上下固定余量：** 文档容器上下各留一段固定滚动余量，任何缩放下首页上缘与末页下缘都可滚出余量，余量不随缩放变形、不参与 PDF 布局，防止文档被遮挡。

**批注与画布（Glance 永不修改 PDF）**

* **重要：** Glance-RX **从不将批注写入原始 PDF**。手绘笔迹与白板卡片都保存在独立的画布文件（`.gcanvas`，jsoncanvas 1.0 语义）中，PDF 全程只读。打开文档时按路径自动恢复上次盖在该文档上的画布。
* **画布：** 承载画笔笔迹和白板卡片的`.gcanvas`独立文件，可在右侧边栏和文件管理器中调整命名和位置。可在右侧边栏中自由选择挂载与卸下，与 PDF 文件没有固定对应关系，各 PDF 下创建的画布可以互相挂载，但由于 PDF 尺寸不一，通常会导致批注错位。按`G 键`切换画布的 显示 / 隐藏。
* **手绘笔迹：** 画笔模式在页面上自由书写；七色标注（紫 / 蓝 / 青 / 绿 / 黄 / 红 / 黑）。按住 `E 键` 临时切换为整笔橡皮，按住 `H 键` 临时切换为阅读平移，松键即恢复画笔。
* **白板模式：** 仿 Obsidian Canvas 的无限画布——可新建/拖动/缩放文字卡片、卡片间连线、按住 `~ 键` （Tab上方）点击两张卡片可快速连线、双击编辑（支持部分 Markdown 行内样式）、块与连线统一撤销栈。
* **撤销 / 重做：** 笔迹、卡片、连线共用一条历史（`Ctrl+Z` / `Ctrl+Y`），多步可回退。
* **Ctrl+S：** 保存的是**画布**（笔迹与卡片），与 PDF 本体无关；未落盘改动会在退出软件时提示。

**导航与文档**

* **欢迎页：** 点击「打开 PDF 文档」或把 PDF **直接拖进窗口**即可打开。阅读中也可以把 PDF **直接拖进窗口**，在本窗口打开。
* **左侧边栏：** 缩略图 / 书签 / 大纲 / 白板 四个模式，各自可跳转阅读位置。`点击黄星按钮`、`点击缩略图黄星标记`、`Ctrl+D` 为当前页加书签。
* **右侧边栏：** 画布文件的管理器，可调整画布命名与位置，可选择挂载与卸下画布文件。
* **密码 PDF：** 支持打开受密码保护的文档（登录对话框 + FFI 传递解锁凭据），阅读 / 对比视图均覆盖。
* **分屏对比：** 并排打开两个 PDF 同步对照，中间位置有红色同步滚动条。**打开**和**关闭**右侧 PDF 的按钮分别位于**右侧视图的左右上角**，右侧 PDF 仅支持阅读对比，没有特殊功能。
* **打印：** 直接打印当前 PDF；打印输出会把当前页的批注合成进去（仅作用于打印结果，不改动原文件）。打印功能缺少测试，效果不能保证。

**其它**

* **多语言：** 中文 / 英语 / 西班牙语，按系统区域自动选择，也支持自行设置。
* **多窗口：** 双击 PDF 文件全部使用新窗口打开。拖拽 PDF 文件进入已打开的窗口则在当前窗口打开。没有标签页形态。
* **主题：** 浅色 / 深色 / 跟随系统三档，跟随系统时实时响应系统外观变化。
* **运行时手感调参：** Glance-RX.exe 同级目录下的 `config.txt` 可调节缩放范围、弹簧刚度、滚轮步进等参数，**删文件即恢复出厂**，无需重新编译。

---

## 架构

Glance-RX 采用 **C# 前端 + Rust 原生后端** 的混合架构：

* **前端（外壳）：** WinUI 3（C# / .NET 10，`net10.0-windows10.0.26100.0`）—— 原生 Windows 外观、交互与排版。
* **后端（渲染）：** Rust（`glance-native`，编译为 `cdylib`）—— 基于 MuPDF 的单进程多线程渲染引擎（每线程持有独立 `fz_context`），负责栅格化与页位图生成。
* **桥接：** P/Invoke FFI（`Interop/GlanceRXNative.cs`）—— C# ↔ Rust 互操作；`extern "C"` 入口以 `catch_unwind` 兜底，杜绝跨 FFI 边界 panic。
* **合成：** Win2D / `CanvasVirtualControl` —— GPU 上把原生后端产出的物理像素瓦片合成为最终画面。

### 渲染管线

1. 缩放 / 滚动落定后，`TileRenderCache`（`GpuPageView` 宿主）按物理像素计算可见页的瓦片需求（引擎内部不使用 DIP）。
2. 渲染请求经 FFI 提交给 Rust 后端，由 MuPDF 在原生线程池内栅格化，把位图所有权交给 C# 缓存。
3. Win2D 在 GPU 上做 src/dest-rect 变换与合成；弹簧收敛期内不触发重渲染，落定后补齐缺失瓦片。

### 数据落盘

用户数据只写进Glance-RX.exe 同级目录下的 `GlanceData` 文件夹内：

* **窗口大小 / 窗口位置 / 阅读位置 / 缩放 / 面板开关 / 上次画布** → `GlanceData`文件夹内`documents.json`，按 PDF 路径记忆。
* **设置 / 语言** → `GlanceData`文件夹内`settings.json`。
* **笔迹与白板卡片** → `GlanceData`文件夹内`Canvases`文件夹，每份独立 `.gcanvas` 画布文件；PDF 本体只读。

---

## 技术栈与依赖

### 前端（.NET / C#）

* **[Windows App SDK (WinUI 3) 2.2.0](https://github.com/microsoft/microsoft-ui-xaml)：** Fluent 界面框架。
* **[Win2D 1.3.1](https://github.com/microsoft/Win2D)：** GPU 呈现层（`CanvasVirtualControl`）。
* **[PDFsharp 6.1.1](https://github.com/empira/PDFsharp)：** 打印路径的辅助处理（不用于修改原 PDF）。

### 后端与 FFI（Rust）

* **[MuPDF（`mupdf` crate 0.8）](https://mupdf.com/)：** 底层 PDF 渲染引擎。
* **[windows-rs 0.51](https://github.com/microsoft/windows-rs)：** Windows API 的 Rust 投影。
* **[serde / serde_json](https://github.com/serde-rs/serde)：** Rust 侧数据序列化；C# 侧用户数据由 `System.Text.Json` 落盘。

---

## 系统要求

* **操作系统：** Windows 10 17763（1809）或更高版本。
* **运行时：** self-contained 分发，**无需另装 .NET 运行时**；仅支持 64 位（x64）。
* **开发环境（仅构建时需要）：** .NET 10 SDK、Rust 工具链（MSVC 目标）、Visual Studio 2022（可选）。

---

<details>
<summary><strong>📚 构建与运行（面向开发者）</strong></summary>

### 前置条件

* **.NET 10 SDK** —— [dotnet.microsoft.com](https://dotnet.microsoft.com)
* **Rust 1.70+**（需 MSVC 目标工具集，`mupdf-sys` 还需 `libclang`）—— [rustup.rs](https://rustup.rs)
* **Visual Studio 2022**（可选）

### 构建

```bash
# 后端可省略：C# 工程在 BeforeTargets="Build" 通过 BuildNativeLib 目标
# 自动以 release 模式拉起 cargo build，并复制 glance_native.dll 到输出目录。
dotnet build src/Glance.csproj -c Debug
dotnet build src/Glance.csproj -c Release

# 运行
dotnet run --project src/Glance.csproj
```

> 若 `cargo build` 因 MSVC 工具集或 `libclang` 报错，参考 `native/glance-native/.cargo/config.toml.example`，
> 将 `MUPDF_MSVC_PLATFORM_TOOLSET` 与 `LIBCLANG_PATH` 指向本机实际路径。真正的 `config.toml` 已被 git 忽略，**切勿提交**。

> 本工程为**非打包应用**（`WindowsPackageType=None`），不需要证书或 MSIX 打包步骤。

### 质量门槛

* 便携版约束：禁止向系统 AppData / 系统 Temp 写用户数据；用户数据只写进Glance-RX.exe 同级目录下的 `GlanceData` 文件夹内。

</details>

---

## 致谢与来源

* **[Glance](https://github.com/jonas1ara/Glance)（jonas1ara 的原型）：** 在此基础上大幅改造，依其 MIT 许可以 AGPL-3.0 再许可，保留原始 MIT 版权与许可声明（见 [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md)）。
* **[Sumatra PDF](https://www.sumatrapdfreader.org/)：** `RenderCache` 瓦片引擎与 `DocumentLayout` 版面逻辑被 1:1 移植为 C# 端实现。
* **[MuPDF](https://mupdf.com/)（Artifex）：** 经 Rust 绑定的底层渲染引擎。
* **[WinUI 3 / Windows App SDK](https://github.com/microsoft/microsoft-ui-xaml)、[Win2D](https://github.com/microsoft/Win2D)、[PDFsharp](https://github.com/empira/PDFsharp)、[Rust](https://www.rust-lang.org/)、[windows-rs](https://github.com/microsoft/windows-rs)、[serde](https://github.com/serde-rs/serde)：** 各自领域的基座。

---

## 许可证

Glance-RX 基于 **GNU Affero General Public License v3 (AGPL-3.0)** 发布，详见 [LICENSE](LICENSE)。

* **MuPDF**（Artifex）以 AGPL-3.0 发布（`mupdf` crate 0.8，精确版本见 `native/glance-native/Cargo.toml` 与 `Cargo.lock`）；其完整源码随本仓库公开。
* 渲染内核是对 **Sumatra PDF**（GPL-3.0）相关模块的 1:1 移植，依 GPL-3.0 → AGPL-3.0 单向兼容，整体以 AGPL-3.0 发布。
* 派生自 **[jonas1ara/Glance](https://github.com/jonas1ara/Glance)**（MIT），依 MIT → AGPL-3.0 兼容发布，并保留原始 MIT 声明。
* 原生后端静态链接的第三方库（FreeType、Little CMS、OpenJPEG、jbig2dec、zlib、Brotli、Zint、ZXing-C++、Tesseract 等）许可文本汇总于 [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md)。
