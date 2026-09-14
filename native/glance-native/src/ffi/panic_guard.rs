// panic_guard.rs
//
// FFI panic boundary for the Glance native library.
//
// Why this exists
// ---------------
// Every `extern "C"` function in `bindings.rs` is reachable from C# (the WinUI3
// frontend) across the FFI boundary. If any of them `panic!` — an index-out-of-
// bounds in tile arithmetic, a MuPDF-internal panic, a poisoned-lock unwrap,
// etc. — the panic would *unwind across the `extern "C"` boundary*. Unwinding
// across an FFI boundary is undefined behaviour and on the MSVC target it
// manifests as a **silent native crash**: the process dies inside the native
// frame, before C#'s `try/catch` in `RenderTile` or the `UnhandledException`
// handler ever get a chance to run. The observable symptom is exactly what was
// reported — "app crashed, appcrash has no record" — because no managed handler
// fired and (depending on WER settings) no dump was produced.
//
// Fix
// ---
// `catch_unwind` converts an unwinding panic into a recoverable `Err`, which the
// wrapper turns into the function's normal error return (a `Response{success:
// false, error_msg: ...}` for the render/export/annotation entry points, or a
// safe sentinel for the `void`/`bool` ones). With `panic = "unwind"` (the
// default; we deliberately do NOT set `panic = "abort"` in Cargo.toml) this
// catches every Rust panic.
//
// A panic hook (installed once, idempotently) additionally writes the panic
// message and a full backtrace to `<BaseDirectory>/crash_native_*.log` so the
// failure is reproducible and locatable next time instead of vanishing.
//
// Note: the engine already recovers from a poisoned Mutex on the render path
// (`render_lock.lock().unwrap_or_else(|e| e.into_inner())`), so a panic that
// happens while holding `render_lock` does not permanently wedge subsequent
// renders — the next tile simply succeeds.

use std::io::Write;
use std::panic;
use std::os::windows::ffi::OsStrExt;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Once, OnceLock};

use windows::Win32::System::Diagnostics::Debug::{
    AddVectoredExceptionHandler, EXCEPTION_POINTERS, IMAGEHLP_LINEW64,
    RtlCaptureStackBackTrace, SymCleanup, SymFromAddrW, SymGetLineFromAddrW64,
    SymInitializeW, SYMBOL_INFOW,
};
use windows::Win32::Foundation::{CloseHandle, HANDLE};
use windows::Win32::Storage::FileSystem::{
    CreateFileW, WriteFile, FILE_ATTRIBUTE_NORMAL, FILE_SHARE_READ, OPEN_ALWAYS,
};
use windows::core::PCWSTR;
use core::fmt::Write as _;

static INIT_HOOK: Once = Once::new();

/// Directory next to the host executable — mirrors C# `AppContext.BaseDirectory`.
/// For a cdylib loaded into the .NET process, `current_exe()` is `Glance.exe`,
/// whose parent is the base directory where the C# `CrashLog` also writes.
fn base_dir() -> std::path::PathBuf {
    std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.to_path_buf()))
        .unwrap_or_else(|| std::env::temp_dir())
}

fn timestamp() -> String {
    let secs = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    format!("{}", secs)
}

fn write_panic_log(msg: &str) {
    let dir = base_dir();
    let ts = timestamp();
    // Per-incident log.
    if let Ok(mut f) = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(dir.join(format!("crash_native_{}.log", ts)))
    {
        let _ = writeln!(f, "[NATIVE PANIC] {}", msg);
        let _ = f.flush();
    }
    // Cumulative log for cross-incident search.
    if let Ok(mut f) = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(dir.join("crash_native_all.log"))
    {
        let _ = writeln!(f, "[NATIVE PANIC] {}  (ts={})", msg, ts);
        let _ = f.flush();
    }
}

/// Extract a human-readable description (location + message + backtrace) from a
/// panic payload.
fn describe_panic(info: &std::panic::PanicHookInfo) -> String {
    let mut s = String::new();
    if let Some(loc) = info.location() {
        s.push_str(&format!("at {}:{}:{} ", loc.file(), loc.line(), loc.column()));
    }
    let payload = if let Some(p) = info.payload().downcast_ref::<&str>() {
        Some(p.to_string())
    } else if let Some(p) = info.payload().downcast_ref::<String>() {
        Some(p.clone())
    } else {
        None
    };
    match payload {
        Some(p) => s.push_str(&format!("panic: {}", p)),
        None => s.push_str("panic: <non-string payload>"),
    }
    // Append a backtrace for post-mortem. Capturing is cheap relative to a crash.
    let bt = std::backtrace::Backtrace::force_capture();
    s.push_str(&format!("\n{}", bt));
    s
}

/// Install a panic hook (idempotent via `Once`) that persists every panic to
/// disk. The default hook is preserved so stderr output is unchanged. Also arms
/// the native access-violation logger so a C-level AV is recorded too.
pub fn install_panic_hook() {
    INIT_HOOK.call_once(|| {
        install_av_logger();
        let default_hook = panic::take_hook();
        panic::set_hook(Box::new(move |info| {
            // Default behaviour first (stderr).
            default_hook(info);
            // Persist for post-mortem, co-located with the C# crash logs.
            let msg = describe_panic(info);
            write_panic_log(&msg);
        }));
    });
}

/// Belt-and-suspenders marker written when a panic is caught at the FFI boundary.
/// The detailed message + backtrace were already persisted by the panic hook.
pub fn log_panic_occurred() {
    write_panic_log("[NATIVE PANIC] caught at FFI boundary (see above for detail)");
}

// ---------------------------------------------------------------------------
// Native access-violation logger.
//
// `catch_unwind` above handles Rust panics, but it CANNOT catch a hardware
// access violation raised inside MuPDF's C rasterizer — a C-level AV is an OS
// exception, not a Rust unwind. Without an interceptor the process simply dies
// with no diagnostic, which is exactly the "appcrash with no record" symptom.
// A vectored exception handler (VEH) runs first-chance on any AV, writes a
// one-line record (exception code / operation / faulting virtual address /
// program counter) to `crash_native_av.log`, then returns CONTINUE_SEARCH so the
// OS terminates the process normally. We cannot safely *recover* from a C AV
// (the stack is corrupted), but we can guarantee a post-mortem record instead
// of silent death.
// ---------------------------------------------------------------------------

static AV_LOG_PATH: OnceLock<Vec<u16>> = OnceLock::new();
static AV_RECURSE: AtomicBool = AtomicBool::new(false);

/// Build `<BaseDirectory>/crash_native_av.log` as a NUL-terminated wide string.
fn av_log_path() -> Vec<u16> {
    base_dir()
        .join("crash_native_av.log")
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect()
}

/// Minimal `core::fmt::Write` over a fixed stack buffer (no heap in the handler).
struct BufWriter<'a> {
    buf: &'a mut [u8],
    pos: usize,
}

impl<'a> core::fmt::Write for BufWriter<'a> {
    fn write_str(&mut self, s: &str) -> core::fmt::Result {
        for &b in s.as_bytes() {
            if self.pos < self.buf.len() {
                self.buf[self.pos] = b;
                self.pos += 1;
            }
        }
        Ok(())
    }
}

/// Exception codes that are **first-chance by design** in a managed + WinRT
/// + MuPDF process. Every one of these is raised (and swallowed by the runtime
/// or the OS) during normal operation; recording them as "native crashes" would
/// flood the record and bury the real one.
fn is_benign_exception(code: u32) -> bool {
    match code {
        // OutputDebugStringA / OutputDebugStringW (raised by debuggers, and by
        // injected APM/DLP shims — observed live in this environment).
        0x4001_0006 | 0x4001_000A => true,
        0x406D_1388 => true, // MS_VC_EXCEPTION (SetThreadName)
        0x8000_0003 | 0x8000_0004 => true, // BREAKPOINT / SINGLE_STEP
        0xE06D_7363 => true, // MSVC C++ `throw`
        0xE043_4352 => true, // .NET CLR exception
        0xE043_4F4D => true, // .NET CLR exception (legacy code)
        0xC000_013A => true, // STATUS_CONTROL_C_EXIT
        _ => false,
    }
}

extern "system" fn av_handler(info: *mut EXCEPTION_POINTERS) -> i32 {
    // CONTINUE_SEARCH = 0. Re-entrancy latch: never recurse into the recorder
    // (dump_crash itself may fault, and DbgHelp allocates).
    if AV_RECURSE.swap(true, Ordering::AcqRel) {
        return 0;
    }
    record_exception(info);
    // ⚠ Release the latch. Every exception that reaches a VEH is **first
    // chance**, and SEH / the CRT / the .NET runtime swallow plenty of them.
    // Latching here used to silently disable the recorder for the rest of the
    // process's life — i.e. the *next*, genuine crash went unrecorded.
    AV_RECURSE.store(false, Ordering::Release);
    0
}

fn record_exception(info: *mut EXCEPTION_POINTERS) {
    unsafe {
        if info.is_null() {
            return;
        }
        let rec = (*info).ExceptionRecord;
        if rec.is_null() {
            return;
        }
        let code: u32 = (*rec).ExceptionCode.0 as u32;
        if is_benign_exception(code) {
            return;
        }
        // 0xC000_0005 = ACCESS_VIOLATION carries fault details; other codes
        // (stack overflow 0xC00000FD etc.) don't, but we still want every native
        // crash recorded with a stack.
        let op = if code == 0xC000_0005 {
            match (*rec).ExceptionInformation[0] {
                1 => "WRITE",
                8 => "DEP",
                _ => "READ",
            }
        } else {
            "-"
        };
        let fault_va = (*rec).ExceptionInformation[1] as u64;
        let ctx = (*info).ContextRecord;
        let pc: u64 = if ctx.is_null() {
            0
        } else {
            #[cfg(target_arch = "x86_64")]
            {
                (*ctx).Rip
            }
            #[cfg(target_arch = "x86")]
            {
                (*ctx).Eip as u64
            }
            #[cfg(not(any(target_arch = "x86_64", target_arch = "x86")))]
            {
                0
            }
        };
        let kind = if code == 0xC000_0005 {
            "ACCESS VIOLATION"
        } else {
            "CRASH"
        };
        dump_crash(kind, code, op, fault_va, pc);
    }
}

/// CRT SIGABRT trap.
///
/// UCRT `abort()` — which a failed MuPDF `assert`/`fz_bug` calls — raises
/// `SIGABRT` *before* fast-failing, and a fast-fail (`0xC0000409`) **bypasses
/// vectored exception handlers**. That is exactly why the VEH alone recorded
/// nothing for this crash. Installing a SIGABRT handler lets us persist the
/// record + stack first, then re-raise so the process still terminates.
extern "C" fn abort_handler(_sig: i32) {
    if AV_RECURSE.swap(true, Ordering::AcqRel) {
        return;
    }
    dump_crash("ABORT (SIGABRT)", 0, "-", 0, 0);
    unsafe {
        signal(SIGABRT, SIG_DFL);
        raise(SIGABRT);
    }
}

/// Capture the crashing thread's stack, symbolize it via DbgHelp, and append the
/// record to `crash_native_av.log`. Shared by the VEH and the SIGABRT trap.
fn dump_crash(kind: &str, code: u32, op: &str, fault_va: u64, pc: u64) {
    // Skip the 2 innermost frames (the capture thunk + this fn) for a cleaner
    // trace; the faulting/abort call chain sits directly below.
    let mut trace: [*mut core::ffi::c_void; 62] = [core::ptr::null_mut(); 62];
    let n = unsafe { RtlCaptureStackBackTrace(2, &mut trace, None) as usize };

    let proc = HANDLE(-1isize); // -1 = current process pseudo-handle
    // Give DbgHelp the binary's own directory as the symbol search path so it
    // loads the build PDB (holds both Rust and MuPDF C symbols).
    let sym_path: Vec<u16> = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.to_path_buf()))
        .map(|d| d.as_os_str().encode_wide().chain(std::iter::once(0)).collect())
        .unwrap_or_else(|| vec![0u16]);
    let sym_ok = unsafe { SymInitializeW(proc, PCWSTR(sym_path.as_ptr()), true).is_ok() };

    let mut buf = [0u8; 8192];
    let len = {
        let mut bw = BufWriter {
            buf: &mut buf,
            pos: 0,
        };
        let _ = writeln!(
            bw,
            "NATIVE {} code={:#x} op={} va={:#x} pc={:#x}",
            kind, code, op, fault_va, pc
        );
        sym_frame(&mut bw, proc, sym_ok, pc, 0);
        for i in 0..n.min(40) {
            let addr = trace[i] as u64;
            if addr == 0 {
                break;
            }
            sym_frame(&mut bw, proc, sym_ok, addr, i as u32 + 1);
        }
        bw.pos
    };
    unsafe {
        if let Some(path) = AV_LOG_PATH.get() {
            if let Ok(h) = CreateFileW(
                PCWSTR(path.as_ptr()),
                0x4000_0000u32, // GENERIC_WRITE
                FILE_SHARE_READ,
                None,
                OPEN_ALWAYS,
                FILE_ATTRIBUTE_NORMAL,
                None,
            ) {
                if !h.is_invalid() {
                    let mut written = 0u32;
                    let _ = WriteFile(h, Some(&buf[..len]), Some(&mut written), None);
                    let _ = CloseHandle(h);
                }
            }
        }
    }
    if sym_ok {
        unsafe {
            let _ = SymCleanup(proc);
        }
    }
}

/// Fatal-exit tracer.
///
/// MuPDF's internal `throw()` (error.c) calls `exit(EXIT_FAILURE)` when an error
/// is thrown with NO active `fz_try` on the context ("aborting process from
/// uncaught error!"). That is a *clean* `exit(1)` — no exception, no signal — so
/// neither the VEH nor the SIGABRT trap can see it, and with `ctx->error.print`
/// unset it is completely silent. `exit()` runs atexit handlers first, so this
/// captures the throw chain at that instant. Enabled only when `GLANCE_TRACE_EXIT`
/// is set (diagnostic; it would otherwise fire on every normal exit too).
extern "C" fn exit_handler() {
    if AV_RECURSE.swap(true, Ordering::AcqRel) {
        return;
    }
    // ⚠ atexit() handlers receive no status, so this cannot tell `exit(0)` from
    // `exit(EXIT_FAILURE)` — the label must not pretend otherwise. Read the
    // *stack*: a dump whose trace still contains `throw` / `fz_rethrow` /
    // `fz_close_device` is the MuPDF silent-kill signature
    // (`throw() -> exit(EXIT_FAILURE)`, error.c:223); a dump rooted at
    // `__scrt_common_main_seh` is just the normal CRT exit.
    dump_crash("EXIT via atexit()", 0, "-", 0, 0);
}

/// CRT signal trampoline (linked from the C runtime that Rust's std already
/// pulls in). `SIGABRT == 22` on the MSVC/UCRT ABI.
const SIGABRT: i32 = 22;
const SIG_DFL: usize = 0;
unsafe extern "C" {
    fn signal(sig: i32, handler: usize) -> usize;
    fn raise(sig: i32) -> i32;
    fn atexit(cb: extern "C" fn()) -> i32;
}

/// Aligned buffer holding a `SYMBOL_INFOW` plus an over-allocated name field
/// (the struct's trailing `Name: [u16;1]` is backed by `name` here).
#[repr(C, align(8))]
struct SymBuf {
    sym: SYMBOL_INFOW,
    name: [u16; 512],
}

/// Resolve `addr` to `module!function` (+ file:line) and append `#<idx> <text>`
/// to the buffer. Falls back to the raw address when symbols are unavailable.
fn sym_frame(bw: &mut BufWriter<'_>, proc: HANDLE, sym_ok: bool, addr: u64, idx: u32) {
    if !sym_ok {
        let _ = writeln!(bw, "#{} {:#x}", idx, addr);
        return;
    }
    unsafe {
        let mut sb: SymBuf = std::mem::zeroed();
        sb.sym.SizeOfStruct = core::mem::size_of::<SYMBOL_INFOW>() as u32;
        sb.sym.MaxNameLen = 512;
        let mut disp = 0u64;
        if SymFromAddrW(proc, addr, Some(&mut disp), &mut sb.sym as *mut SYMBOL_INFOW).is_ok() {
            let name_slice = std::slice::from_raw_parts(sb.sym.Name.as_ptr(), sb.sym.NameLen as usize);
            let name = String::from_utf16_lossy(name_slice);
            let mut loc = String::new();
            let mut line: IMAGEHLP_LINEW64 = std::mem::zeroed();
            line.SizeOfStruct = core::mem::size_of::<IMAGEHLP_LINEW64>() as u32;
            let mut line_disp = 0u32;
            if SymGetLineFromAddrW64(proc, addr, &mut line_disp, &mut line).is_ok()
                && !line.FileName.0.is_null()
            {
                let fp = line.FileName.0;
                let mut n = 0usize;
                while *fp.add(n) != 0 {
                    n += 1;
                }
                let fslice = std::slice::from_raw_parts(fp, n);
                loc = format!(" ({}:{})", String::from_utf16_lossy(fslice), line.LineNumber);
            }
            let _ = writeln!(bw, "#{} {} +{:#x}{}", idx, name, disp, loc);
        } else {
            let _ = writeln!(bw, "#{} {:#x}", idx, addr);
        }
    }
}

/// Install the vectored exception handler once. The log path is precomputed and
/// stored outside the handler so the faulting context performs no allocation.
fn install_av_logger() {
    let _ = AV_LOG_PATH.get_or_init(av_log_path);
    unsafe {
        // First = 1 inserts at the front so we always get first-chance logging.
        AddVectoredExceptionHandler(1, Some(av_handler));
        // Also trap CRT abort() (a fast-fail bypasses the VEH).
        signal(SIGABRT, abort_handler as *const () as usize);
        // Optional fatal-exit tracer (MuPDF `throw()` -> exit(1) path).
        if std::env::var("GLANCE_TRACE_EXIT").is_ok() {
            atexit(exit_handler);
        }
    }
}

/// Run an FFI body, converting any unwinding panic into the function's normal
/// error return.
///
/// `$fallback` is the value returned when a panic is caught (must be the same
/// type the body would otherwise return). `install_panic_hook()` is re-asserted
/// on every call so whichever FFI entry point the host invokes *first* always
/// arms the hook.
#[macro_export]
macro_rules! safe_ffi {
    ($fallback:expr => $body:block) => {{
        $crate::ffi::panic_guard::install_panic_hook();
        match ::std::panic::catch_unwind(::std::panic::AssertUnwindSafe(|| $body)) {
            Ok(__v) => __v,
            Err(_) => {
                $crate::ffi::panic_guard::log_panic_occurred();
                $fallback
            }
        }
    }};
}

#[cfg(test)]
mod tests {
    /// Prove the FFI boundary contains a panic instead of letting it unwind
    /// across `extern "C"` (which would be UB / silent native crash with no log).
    #[test]
    fn safe_ffi_converts_panic_to_fallback_and_logs() {
        // A panicking FFI body must yield the fallback, not abort the process.
        let result: u32 = safe_ffi!(42 => {
            panic!("simulated ffi panic");
        });
        assert_eq!(result, 42, "panic must be caught and fallback returned");

        // The panic hook must have persisted the panic to disk next to the exe.
        let dir = super::base_dir();
        let mut newest: Option<(std::time::SystemTime, std::path::PathBuf)> = None;
        let mut found_any = false;
        if let Ok(entries) = std::fs::read_dir(&dir) {
            for e in entries.flatten() {
                if let Some(name) = e.file_name().to_str() {
                    if name.starts_with("crash_native_") && name.ends_with(".log") {
                        found_any = true;
                        if let Ok(meta) = e.metadata() {
                            if let Ok(t) = meta.modified() {
                                if newest.as_ref().map_or(true, |(nt, _)| t > *nt) {
                                    newest = Some((t, e.path()));
                                }
                            }
                        }
                    }
                }
            }
        }
        assert!(found_any, "no crash_native_*.log written to {:?}", dir);
        if let Some((_, path)) = newest {
            let contents = std::fs::read_to_string(&path).unwrap_or_default();
            assert!(
                contents.contains("simulated ffi panic"),
                "crash log {:?} should contain the panic message, got:\n{}",
                path, contents
            );
        }
    }

    /// Prove the vectored-exception handler records a native access violation to
    /// `crash_native_av.log` (the gap `catch_unwind` cannot cover: a C-level AV
    /// inside MuPDF's rasterizer would otherwise die silently with no record).
    #[test]
    fn av_handler_writes_log_on_access_violation() {
        // Arm the VEH + precompute the log path (idempotent).
        super::install_av_logger();

        // Synthesize an ACCESS_VIOLATION (write) at a fake virtual address.
        let mut rec: windows::Win32::System::Diagnostics::Debug::EXCEPTION_RECORD =
            unsafe { std::mem::zeroed() };
        rec.ExceptionCode.0 = 0xC000_0005u32 as i32; // STATUS_ACCESS_VIOLATION
        rec.ExceptionInformation[0] = 1; // write
        rec.ExceptionInformation[1] = 0xdead_beef_u32 as usize;
        let mut ctx: windows::Win32::System::Diagnostics::Debug::CONTEXT = unsafe { std::mem::zeroed() };
        ctx.Rip = 0x4020_1337u64;
        let mut info = windows::Win32::System::Diagnostics::Debug::EXCEPTION_POINTERS {
            ExceptionRecord: &mut rec as *mut _,
            ContextRecord: &mut ctx as *mut _,
        };

        // Clear any stale log so the asserted content is exactly what we wrote.
        let dir = super::base_dir();
        let path = dir.join("crash_native_av.log");
        let _ = std::fs::remove_file(&path);

        // Invoke the handler directly, as the OS would on a first-chance AV.
        let r = super::av_handler(&mut info as *mut _);
        assert_eq!(r, 0, "handler must return EXCEPTION_CONTINUE_SEARCH");

        let contents = std::fs::read_to_string(&path)
            .unwrap_or_else(|_| panic!("crash_native_av.log should exist at {:?}", path));
        assert!(
            contents.contains("NATIVE ACCESS VIOLATION"),
            "log should report the AV, got:\n{}",
            contents
        );
        assert!(
            contents.contains("0xdeadbeef"),
            "log should contain the faulting VA, got:\n{}",
            contents
        );
        assert!(
            contents.contains("0x40201337"),
            "log should contain the program counter, got:\n{}",
            contents
        );
    }
}
