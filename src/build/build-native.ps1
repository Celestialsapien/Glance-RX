# Build Native (Rust) Library for Glance
#
# Compiles the Rust backend (glance_native.dll) in release mode.
# The C# side always consumes target/release/*, so this script builds with
# "cargo build --release" (host target = x64 windows) and never passes --target.

param(
    [string]$Configuration = "Release",
    [string[]]$Platforms = @("x64"),
    # Native directory. Supplied by Glance.csproj (authoritative).
    [string]$NativeDir = ""
)

$ErrorActionPreference = "Stop"

if (-not $NativeDir) {
    $dir = $PSScriptRoot
    while ($dir -and -not (Test-Path (Join-Path $dir "native\glance-native"))) {
        $parent = Split-Path $dir -Parent
        if ($parent -eq $dir) { break }
        $dir = $parent
    }
    $NativeDir = Join-Path $dir "native\glance-native"
}

Write-Host ""
Write-Host "Building Glance Native Library (Rust, release)"
Write-Host "  Native dir : $NativeDir"
Write-Host "  Note       : C# always ships the release build, so cargo runs in --release."
Write-Host ""

if (-not (Test-Path $NativeDir)) {
    Write-Host "ERROR: native/glance-native/ directory not found at $NativeDir"
    exit 1
}

# MuPDF (mupdf-sys) requires a specific MSVC platform toolset and libclang for
# bindgen. v145 matches MSVC 14.5x (VS2025/18).
if (-not $env:MUPDF_MSVC_PLATFORM_TOOLSET) {
    $env:MUPDF_MSVC_PLATFORM_TOOLSET = "v145"
}
if (-not $env:LIBCLANG_PATH) {
    $vsBase = Join-Path $env:ProgramFiles "Microsoft Visual Studio"
    $clangCandidates = @(
        "D:\new\Microsoft Visual Studio\18\Community\VC\Tools\Llvm\x64\bin"
    )
    if (Test-Path $vsBase) {
        $clangCandidates += Get-ChildItem -Path $vsBase -Recurse -Filter "libclang.dll" -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty DirectoryName
    }
    foreach ($c in $clangCandidates) {
        if ($c -and (Test-Path (Join-Path $c "libclang.dll"))) {
            $env:LIBCLANG_PATH = $c
            break
        }
    }
}

Write-Host "  MUPDF_MSVC_PLATFORM_TOOLSET = $env:MUPDF_MSVC_PLATFORM_TOOLSET"
Write-Host "  LIBCLANG_PATH              = $env:LIBCLANG_PATH"
Write-Host "  Running: cargo build --release"

# 用 .NET Process 直接跑 cargo，异步排空 stdout/stderr 后再等待退出。
# 不能再用 Start-Process -Wait -Redirect*：PS 5.1 下 cargo 派生的 rustc/link
# 会继承被重定向的句柄，cargo 退出后 -Wait 仍死等"流 EOF"，导致 VS 构建
# "既不完成、不报错、也不停止"。异步读取 + 超时看门狗可彻底避免死锁。
$outLog = Join-Path $NativeDir "target\glance_cargo_stdout.log"
$errLog = Join-Path $NativeDir "target\glance_cargo_stderr.log"
Remove-Item $outLog, $errLog -ErrorAction SilentlyContinue

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "cargo.exe"
$psi.Arguments = "build --release"
$psi.WorkingDirectory = $NativeDir
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true

$timeoutSec = 3600   # 看门狗：超过 1 小时视为异常，强制终止并报错

try {
    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    if (-not $proc.Start()) {
        throw "Failed to start cargo.exe"
    }
    # 先异步排空两个流，防止管道缓冲区写满反过来阻塞 cargo
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()

    if (-not $proc.WaitForExit($timeoutSec * 1000)) {
        try { $proc.Kill() } catch { }
        $proc.WaitForExit()
        throw "cargo build --release 超时（>${timeoutSec}s），已强制终止"
    }

    # 排空完成后取结果不会阻塞；被强制终止时流已关闭，取到空串
    $stdout = try { $outTask.Result } catch { "" }
    $stderr = try { $errTask.Result } catch { "" }
    Set-Content -Path $outLog -Value $stdout -Encoding UTF8
    Set-Content -Path $errLog -Value $stderr -Encoding UTF8

    $exitCode = $proc.ExitCode
    $proc.Dispose()
} catch {
    Write-Host "  Exception while launching cargo: $_"
    exit 1
}

if ($exitCode -ne 0) {
    Write-Host "  cargo build failed with exit code $exitCode"
    Write-Host "  See log: $errLog"
    exit 1
} else {
    Write-Host "  cargo build succeeded."
}

Write-Host ""
Write-Host "Native build complete. Artifacts are in target\release; copied to bin by Glance.csproj."
exit 0