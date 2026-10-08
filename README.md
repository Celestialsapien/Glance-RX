# Glance

![Glance](docs/img/glance_hero_1920x1080.png)

[![License: AGPL-3.0](https://img.shields.io/badge/License-AGPL--3.0-blue.svg)](LICENSE)
[![Framework: WinUI 3](https://img.shields.io/badge/Framework-WinUI_3-purple.svg)](https://learn.microsoft.com/zh-cn/windows/apps/winui/winui3/)
[![Platform: Windows 10+](https://img.shields.io/badge/Platform-Windows_10%2B-blue.svg)](https://www.microsoft.com/windows/)
[![Language: C# / Rust](https://img.shields.io/badge/Language-C%23%20%2F%20Rust-green.svg)](https://learn.microsoft.com/dotnet/csharp/)

Glance-me 是一款改良自jonas1ara的Glance的高性能 PDF 阅读器，基于 Windows 11 的 WinUI 3 外壳，搭配 Rust 原生渲染后端（MuPDF）与 Win2D GPU 合成。

> 完全免安装、可移植（self-contained，非打包形态）：用户数据只写入 exe 同级目录，**绝不写入 C 盘、AppData 或系统 Temp**。

---

## 核心特性

* **渲染内核（Sumatra 内核 + MuPDF 后端）：**
  * 将 Sumatra PDF 的 `RenderCache` 渲染/内存管理引擎 **1:1 移植**为 C# 端的 `TileRenderCache`（物理像素瓦片、按需预取、近环全分辨率 / 远环快照），后端由 Rust 封装的 MuPDF 经 FFI 提供页位图，Win2D (`CanvasVirtualControl`) 在 GPU 上做合成。
  * 统一运动引擎（弹簧模型）驱动所有缩放与平移。
* **布局与缩放：**
  * 纵向 / 横向连续滚动；单页、双页、书籍对开三种版面。
  * 缩放范围 **0.1×–4.0×**；双击在「适应宽度 ↔ 实际大小 100%」之间切换。
  * 滚轮、Ctrl+滚轮、右键拖拽缩放、方向键 / 空格翻页等丰富交互。
* **批注（本地持久化）：**
  * 手写画笔，七色标注（黄 / 绿 / 青 / 洋红 / 红 / 蓝 / 黑）。
  * 撤销 / 重做（支持多步）；批注经 Rust 端序列化，落盘为本地 JSON，退出提示保存。
* **支持密码 PDF：** 支持打开受密码保护的 PDF（登录对话框 + FFI 向原生后端传递解锁凭据，覆盖阅读 / 对比视图）。
* **分屏对比：** 并排打开两个 PDF 同步对照，中间附带红色同步滚动条。
* **导航：** 侧边栏缩略图、书签、大纲（目录）。
* **打印与导出：** 直接打印；用 PDFsharp 把批注烧录回 PDF，导出带签注的副本。打印功能实机测试较少，可能有未知问题。
* **多语言：** 中文 / 英语 / 西班牙语，按系统区域自动选择；所有界面文案统一经 `LocalizationService.Get(...)` 读取，无硬编码字符串。
* **主题：** 现代主题配色，浅色 / 深色 / 跟随系统三档。部分区域有亚克力效果。
* **运行时手感调参：** 同级目录下的 `config.txt` 可调节缩放范围、弹簧刚度、滚轮步进、留白等参数，**删文件即恢复出厂**，无需重新编译。

---

## 架构

Glance 采用 **C# 前端 + Rust 原生后端** 的混合架构：

* **前端（外壳）：** WinUI 3（C# / .NET 10，`net10.0-windows10.0.26100.0`）—— 原生 Windows 11 外观、交互与排版。
* **后端（渲染）：** Rust（`glance-native`，编译为 `cdylib`）—— 基于 MuPDF 的单进程多线程渲染引擎（每个线程持有独立的 `fz_context`），负责栅格化、批注处理与本地 JSON 持久化。
* **桥接：** P/Invoke FFI（`GlanceNative.cs`）—— 类型安全的 C# ↔ Rust 互操作；所有 `extern "C"` 入口以 `catch_unwind` 兜底，杜绝跨 FFI 边界 panic 导致的静默崩溃。
* **合成：** Win2D / `CanvasVirtualControl` —— 在 GPU 上把原生后端产出的物理像素瓦片合成为最终画面。

### 渲染管线（要点）

1. 缩放 / 滚动落定后，`TileRenderCache`（`GpuPageView` 宿主）按物理像素计算可见页的瓦片需求（单位体系与 Sumatra 一致，引擎内部不再出现 DIP）。
2. 渲染请求经 FFI 提交给 Rust 后端，由 MuPDF 在原生线程池内栅格化，零拷贝地把位图所有权交给 C# 缓存。
3. Win2D 在 GPU 上做 GPU src/dest-rect 变换与合成；缩放 / 平移在弹簧收敛期内不触发重渲染，仅在落定后补齐缺失瓦片。

---

## 技术栈与依赖

### 前端（.NET / C#）
* **[Windows App SDK (WinUI 3) 2.2.0](https://github.com/microsoft/microsoft-ui-xaml)：** 现代 Fluent 界面框架，原生 Windows 桌面开发。
* **[Win2D 1.3.1](https://github.com/microsoft/Win2D)：** GPU 呈现层（`CanvasVirtualControl`），用于瓦片合成。
* **[PDFsharp 6.1.1](https://github.com/empira/PDFsharp)：** 将用户批注烧录回原始 PDF 并导出。

### 后端与 FFI（Rust）
* **[MuPDF（`mupdf` crate 0.8）](https://mupdf.com/)：** 单进程多线程 PDF 渲染引擎，是 Glance 原生后端的渲染内核。
* **[windows-rs 0.51](https://github.com/microsoft/windows-rs)：** 微软官方 Rust 投影，提供对 Windows API 的原生访问。
* **[serde / serde_json](https://github.com/serde-rs/serde)：** 本地 JSON 批注 / 书签数据库的序列化框架。

---

## 系统要求

* **操作系统：** Windows 10 17763（1809）或更高版本。
* **运行时：** 以 self-contained 方式分发，**无需在目标机器另行安装 .NET 运行时**；支持 x86 / x64 / ARM64。
* **开发环境（仅构建时需要）：** .NET 10 SDK、Rust 工具链（MSVC 目标）、Visual Studio 2022（可选）。

---

<details>
<summary><strong>📚 构建与运行（面向开发者）</strong></summary>

### 前置条件

* **.NET 10 SDK** —— [dotnet.microsoft.com](https://dotnet.microsoft.com)
* **Rust 1.70+**（需 MSVC 目标工具集）—— [rustup.rs](https://rustup.rs)
* **Visual Studio 2022**（可选）

### 构建步骤

1. 克隆本仓库：
   ```bash
   git clone <本仓库地址>
   cd Glance
   ```

2. 构建 Rust 后端（**可省略**）：
   C# 工程已在 `BeforeTargets="Build"` 通过 `BuildNativeLib` 目标**自动**以 release 模式拉起 `cargo build`，并把产物 `glance_native.dll` 自动复制到输出目录。如需手动构建：
   ```bash
   cd native/glance-native
   cargo build --release
   ```

   > **Windows / Visual Studio 提示：** 部分 VS 安装下 `mupdf-sys` 的构建脚本无法自动探测 MSVC 平台工具集或 `libclang`（bindgen 所需）。若 `cargo build` 报 `MSB8020`（找不到 VS 生成工具）或 `libclang` 相关错误，请将 `native/glance-native/.cargo/config.toml.example` 复制为 `native/glance-native/.cargo/config.toml`，并把 `MUPDF_MSVC_PLATFORM_TOOLSET` 与 `LIBCLANG_PATH` 指向本机实际路径。真正的 `config.toml` 已被 git 忽略，**切勿提交**。

3. 构建并运行 C# 前端：
   ```bash
   cd ../../src
   dotnet run --project Glance.csproj
   ```
   （MSBuild 会在构建期自动编译并复制 `glance_native.dll`，无需手动拷贝。）

### 构建变体

* **Debug：** `dotnet build`
* **Release：** `dotnet build -c Release`
* **清理：** `dotnet clean` 与 `cargo clean`

### 质量门槛

* 交付前需通过 **Debug + Release 双构建，0 错误 0 警告** 的标准。
* 便携版约束：禁止向 C 盘 / AppData / 系统 Temp 写入任何数据；用户数据只写 `AppContext.BaseDirectory`。

</details>

---

## 致谢与来源

Glance-me 建立在多个优秀的开源项目之上，特此致谢：

* **[Glance](https://github.com/jonas1ara/Glance)：** jonas1ara的原型
* **[Sumatra PDF](https://www.sumatrapdfreader.org/)：** 渲染内核（`RenderCache` 瓦片引擎与 `DocumentLayout` 版面逻辑）被 1:1 移植为 C# 端的 `TileRenderCache`，是 Glance-me 高速、稳定渲染的基础。
* **[MuPDF](https://mupdf.com/)（Artifex）：** 经 Rust 绑定的底层 PDF 渲染引擎，提供栅格化能力。
* **[WinUI 3 / Windows App SDK](https://github.com/microsoft/microsoft-ui-xaml) 与 [Win2D](https://github.com/microsoft/Win2D)：** 微软提供的现代 Fluent 界面框架与 GPU 呈现层。
* **[PDFsharp](https://github.com/empira/PDFsharp)：** 批注烧录与 PDF 导出。
* **[Rust](https://www.rust-lang.org/)、[windows-rs](https://github.com/microsoft/windows-rs)、[serde](https://github.com/serde-rs/serde)：** 原生后端与序列化基座。

---

## 许可证

Glance 基于 **GNU Affero General Public License v3 (AGPL-3.0)** 发布，详见 [LICENSE](LICENSE)。

* **MuPDF**（Artifex）作为原生渲染后端，本身以 AGPL-3.0 发布（`mupdf` crate 0.8，精确版本见 `native/glance-native/Cargo.toml` 与 `Cargo.lock`）；其对应完整源码随本仓库公开。
* 渲染内核 `TileRenderCache` 是对 **Sumatra PDF**（GPL-3.0）`RenderCache` 的 1:1 移植，依 GPL-3.0 → AGPL-3.0 的单向兼容性，在本项目下整体以 AGPL-3.0 发布。
* 原生后端静态链接了多个第三方库（FreeType、Little CMS、OpenJPEG、jbig2dec、zlib、Brotli、Zint、ZXing-C++、Tesseract 等），它们的许可文本汇总于 [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md)。
