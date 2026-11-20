# Glance Native Backend (Rust)

`glance_native.dll` 是 Glance-RX 的原生后端，经 P/Invoke 被 C# / WinUI 3 外壳调用，负责 PDF 栅格化、批注处理和 JSON 存储。许可为 AGPL-3.0（与全项目一致）。

## 职责

- **渲染**：用 MuPDF 栅格化页面，输出 BGRA 位图由 C# 端 Win2D 合成；支持按区域渲染与页尺寸查询。
- **单进程多线程**：每个渲染线程持有独立的 `fz_context`，display list 解析一次后多线程回放。
- **批注 / 存储**：画布批注的序列化、处理与文件读写（`.gcanvas`）。
- **FFI 安全**：所有 `extern "C"` 入口经 `safe_ffi!` 用 `catch_unwind` 兜底， panic 不跨 FFI 边界。

## 源码结构

```
glance-native/
├── Cargo.toml / Cargo.lock
├── .cargo/config.toml.example   # 手动配置 mupdf-sys 构建环境的模板
└── src/
    ├── lib.rs                   # crate 根，重导出 FFI 函数
    ├── ffi/                     # extern "C" 入口、类型编组、panic 兜底
    ├── rendering/               # MuPDF 引擎封装、图像工具
    ├── annotations/             # 批注模型与处理
    ├── storage/                 # 文件与 JSON 读写
    └── utils/                   # 错误与日志
```

## 构建

**常规路径：不要直接跑 cargo。** 在仓库根执行 `dotnet build src/Glance.csproj`，C# 工程的 `BuildNativeLib` 目标会自动调用 `src/build/build-native.ps1` 编译 release 版原生库并拷到输出目录。脚本会读取环境变量 / `.cargo/config.toml`，两者都缺时用 `vswhere` 自动探测 MSVC 工具集与 libclang；详见仓库根 README 的「构建与运行」章节。

单独调试原生层时可手动 `cargo build --release`（需要自行保证 `MUPDF_MSVC_PLATFORM_TOOLSET` 与 `LIBCLANG_PATH` 可用，因为此时没有 PowerShell 探测）。产物为 `target/release/glance_native.dll`，**不提交入库**。

## FFI 导出

C# 侧通过 `src/Interop/GlanceRXNative.cs` 调用，主要入口（`src/ffi/bindings.rs`）：

- `mu_engine_open` / `mu_engine_reload` / `mu_engine_destroy`：文档生命周期
- `mu_engine_page_count` / `mu_engine_page_dimensions` / `mu_engine_page_dimensions_all` / `mu_engine_warmup_page`：页面查询与预热
- `mu_render_page_bgra` / `mu_render_page_region_bgra`：整页 / 区域栅格化
- `memory_free`：释放原生侧分配的内存

另有 `annotations_save` / `annotations_load` / `annotations_process` 三个批注入口在 Rust 侧导出，当前 C# 端尚未通过 P/Invoke 调用（批注落盘走上层画布管线）。

调用约定：所有导出函数带 `#[unsafe(no_mangle)]`，返回结构体的函数配套 `memory_free`。新增导出须与 C# 侧 `GlanceRXNative.cs` 的声明保持 ABI 一致。
