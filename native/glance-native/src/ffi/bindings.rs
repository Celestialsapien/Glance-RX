// FFI Bindings - extern "C" functions callable from C#
//
// ⚠ Every entry point below is wrapped in the `safe_ffi!` macro (defined in
// `panic_guard.rs`). That macro installs a one-time panic hook (which persists
// any panic to `<BaseDirectory>/crash_native_*.log`) and catches unwinding
// panics at the FFI boundary, converting them into the function's normal error
// return instead of letting them unwind across `extern "C"` (which is UB and
// kills the process before any managed handler can run). See panic_guard.rs for
// the full rationale.
use crate::safe_ffi;

/// Response structure for FFI calls
#[repr(C)]
pub struct Response {
    pub success: bool,
    pub error_msg: *const u8,    // NULL if success=true
    pub data_ptr: *mut u8,       // PNG bytes or JSON string
    pub data_len: u64,
}

/// Result of `mu_engine_open`.
///
/// Field order puts the two pointers first and the single `bool` last so the
/// `#[repr(C)]` layout matches the C# `EngineCreateResult` struct exactly
/// (two 8-byte pointers, then a 1-byte bool — no inter-field padding to
/// misalign). `error_msg` is non-NULL only on a non-password failure and must
/// be freed with `memory_free()`.
#[repr(C)]
pub struct EngineCreateResult {
    pub engine: *mut MuEngine,    // NULL if creation failed
    pub error_msg: *const u8,     // NULL if success or password-required; free with memory_free() if non-NULL
    pub needs_password: bool,     // true if the PDF is encrypted and no/wrong password was supplied
}

// ============================================================================
// MuPDF Single-Process Engine (single-process multi-thread architecture)
// ============================================================================
//
// These functions replace the out-of-process worker-pool path. A single
// MupdfEngineImpl lives in this process and exposes `&self` (thread-safe)
// methods: parsed DisplayLists are shared lock-free, and rasterization runs on
// the caller's own fz_context (the crate keeps one per thread). Therefore the
// C# side can fire many tile renders from its thread pool CONCURRENTLY with NO
// global lock — this is the whole point of the MuPDF refactor.
//
// `engine` is a `*const MuEngine`; pass the same pointer to every call. The
// handle must be destroyed via `mu_engine_destroy`.
// ============================================================================

pub type MuEngine = crate::rendering::mupdf_engine::MupdfEngineImpl;

/// Open a PDF into a single-process MuPDF engine.
///
/// `hot_capacity` bounds the parsed-page LRU cache (mirrors the C# sliding
/// window). `password` may be NULL/empty for "no password". Returns the same
/// layout as `EngineCreateResult`: on success `engine` is the opaque handle;
/// on an encrypted doc with no/wrong password `needs_password` is set and
/// `engine` is NULL; any other failure fills `error_msg` (free with memory_free).
#[unsafe(no_mangle)]
pub extern "C" fn mu_engine_open(
    pdf_path: *const u8,
    password: *const u8,
    hot_capacity: u32,
) -> EngineCreateResult {
    safe_ffi!(EngineCreateResult {
        engine: std::ptr::null_mut(),
        error_msg: crate::utils::error::make_error_string("native panic in mu_engine_open"),
        needs_password: false,
    } => {
        let null_result = EngineCreateResult {
            engine: std::ptr::null_mut(),
            error_msg: std::ptr::null(),
            needs_password: false,
        };
        if pdf_path.is_null() {
            return null_result;
        }

        let path_str = match crate::ffi::marshalling::c_string_to_rust(pdf_path) {
            Ok(s) => s,
            Err(_) => return null_result,
        };
        let password_opt: Option<String> = if password.is_null() {
            None
        } else {
            match crate::ffi::marshalling::c_string_to_rust(password) {
                Ok(s) => Some(s),
                Err(_) => None,
            }
        };

        match MuEngine::new(&path_str, password_opt.as_deref(), hot_capacity as usize) {
            Ok(engine) => {
                let ptr = Box::into_raw(Box::new(engine));
                EngineCreateResult {
                    engine: ptr,
                    error_msg: std::ptr::null(),
                    needs_password: false,
                }
            }
            Err(crate::rendering::mupdf_engine::EngineOpenError::PasswordRequired) => EngineCreateResult {
                engine: std::ptr::null_mut(),
                error_msg: std::ptr::null(),
                needs_password: true,
            },
            Err(crate::rendering::mupdf_engine::EngineOpenError::Other(msg)) => EngineCreateResult {
                engine: std::ptr::null_mut(),
                error_msg: crate::utils::error::make_error_string(&msg),
                needs_password: false,
            },
        }
    })
}

/// Reload the document from disk, dropping every parsed display list so the
/// old document's resources (image streams etc.) return to the OS. This is the
/// in-process analogue of the worker `reload`, used by the idle cache-flush.
/// Returns true on success.
#[unsafe(no_mangle)]
pub extern "C" fn mu_engine_reload(engine: *const MuEngine) -> bool {
    safe_ffi!(false => {
        if engine.is_null() {
            return false;
        }
        let eng = unsafe { &*engine };
        eng.reload().is_ok()
    })
}

/// Destroy a MuPDF engine and free its document + display lists.
#[unsafe(no_mangle)]
pub extern "C" fn mu_engine_destroy(engine: *const MuEngine) {
    safe_ffi!(() => {
        if !engine.is_null() {
            unsafe {
                drop(Box::from_raw(engine as *mut MuEngine));
            }
        }
    })
}

/// Get total page count. Cheap; no global lock needed.
#[unsafe(no_mangle)]
pub extern "C" fn mu_engine_page_count(engine: *const MuEngine) -> u32 {
    safe_ffi!(0 => {
        if engine.is_null() {
            return 0;
        }
        let eng = unsafe { &*engine };
        eng.get_page_count()
    })
}

/// Get page dimensions (in points) for a page index. Returns true + writes
/// width/height on success; false on failure.
#[unsafe(no_mangle)]
pub extern "C" fn mu_engine_page_dimensions(
    engine: *const MuEngine,
    page_index: u32,
    out_width: *mut f32,
    out_height: *mut f32,
) -> bool {
    safe_ffi!(false => {
        if engine.is_null() || out_width.is_null() || out_height.is_null() {
            return false;
        }
        let eng = unsafe { &*engine };
        match eng.get_page_dimensions(page_index) {
            Ok((w, h)) => {
                unsafe {
                    *out_width = w;
                    *out_height = h;
                }
                true
            }
            Err(_) => false,
        }
    })
}

/// Get page dimensions for pages 0..count in ONE call, writing 2*count floats
/// (w0,h0,w1,h1,...) into `out_dims` (caller-allocated, must hold 2*count f32).
/// Failed pages are written as (0.0, 0.0). Single engine-lock hold + single FFI
/// round trip, so load-time enumeration of all page sizes is much faster than N
/// per-page calls on large PDFs. Returns false only on invalid parameters.
#[unsafe(no_mangle)]
pub extern "C" fn mu_engine_page_dimensions_all(
    engine: *const MuEngine,
    count: u32,
    out_dims: *mut f32,
) -> bool {
    safe_ffi!(false => {
        if engine.is_null() || out_dims.is_null() || count == 0 {
            return false;
        }
        let eng = unsafe { &*engine };
        let out = unsafe { std::slice::from_raw_parts_mut(out_dims, (count as usize) * 2) };
        eng.get_all_page_dimensions(count, out);
        true
    })
}

/// Pre-parse a page into its DisplayList and cache it (cold-start warmup: the
/// first tile render of that page then hits the cache instead of paying the
/// content-stream parse). No-op if already cached. Returns true on success;
/// false on null engine / invalid page.
#[unsafe(no_mangle)]
pub extern "C" fn mu_engine_warmup_page(engine: *const MuEngine, page: u32) -> bool {
    safe_ffi!(false => {
        if engine.is_null() {
            return false;
        }
        let eng = unsafe { &*engine };
        eng.warmup_page(page)
    })
}

/// Render one tile (an axis-aligned sub-rectangle of a page's device space) to
/// tightly-packed BGRA (stride = width*4), matching WinUI WriteableBitmap byte
/// order. THREAD-SAFE: call concurrently from the C# thread pool with the same
/// engine handle — each thread renders on its own fz_context, no global lock.
///
/// `dev_w`/`dev_h` define the full-page raster density; `x0,y0,x1,y1` are pixel
/// coords of the tile within that page. `cookie` (may be NULL) is a caller-owned
/// 24-byte fz_cookie-compatible abort block (`int abort` at offset 0): the C#
/// aborter writes 1 cross-thread to cancel this render mid-flight (MuPDF checks
/// between display-list ops; stage-boundary checks in mupdf_engine). On success
/// writes out_data (free with memory_free(ptr, len)), out_len, out_w, out_h and
/// returns success=true.
#[unsafe(no_mangle)]
pub extern "C" fn mu_render_page_region_bgra(
    engine: *const MuEngine,
    page: u32,
    dev_w: u32,
    dev_h: u32,
    x0: u32,
    y0: u32,
    x1: u32,
    y1: u32,
    cookie: *mut u8,
    out_data: *mut *mut u8,
    out_len: *mut u64,
    out_w: *mut u32,
    out_h: *mut u32,
) -> Response {
    safe_ffi!(Response {
        success: false,
        error_msg: crate::utils::error::make_error_string("native panic in mu_render_page_region_bgra"),
        data_ptr: std::ptr::null_mut(),
        data_len: 0,
    } => {
        if engine.is_null() || out_data.is_null() || out_len.is_null() || out_w.is_null() || out_h.is_null() {
            return Response {
                success: false,
                error_msg: crate::utils::error::make_error_string("Invalid parameters"),
                data_ptr: std::ptr::null_mut(),
                data_len: 0,
            };
        }
        let eng = unsafe { &*engine };
        match eng.render_page_region_bgra_cookie(page, dev_w, dev_h, x0, y0, x1, y1, cookie) {
            Ok((bgra, w, h)) => {
                let boxed_slice = bgra.into_boxed_slice();
                let len = boxed_slice.len() as u64;
                let ptr = Box::into_raw(boxed_slice) as *mut u8;
                unsafe {
                    *out_data = ptr;
                    *out_len = len;
                    *out_w = w;
                    *out_h = h;
                }
                Response {
                    success: true,
                    error_msg: std::ptr::null(),
                    data_ptr: std::ptr::null_mut(),
                    data_len: 0,
                }
            }
            Err(e) => Response {
                success: false,
                error_msg: crate::utils::error::make_error_string(&e),
                data_ptr: std::ptr::null_mut(),
                data_len: 0,
            },
        }
    })
}

/// Render a whole page to tightly-packed BGRA. Thread-safe. Convenience for
/// single page render (page jumps) without tile arithmetic. `cookie` semantics
/// as in mu_render_page_region_bgra (may be NULL).
#[unsafe(no_mangle)]
pub extern "C" fn mu_render_page_bgra(
    engine: *const MuEngine,
    page: u32,
    width: u32,
    height: u32,
    cookie: *mut u8,
    out_data: *mut *mut u8,
    out_len: *mut u64,
    out_w: *mut u32,
    out_h: *mut u32,
) -> Response {
    safe_ffi!(Response {
        success: false,
        error_msg: crate::utils::error::make_error_string("native panic in mu_render_page_bgra"),
        data_ptr: std::ptr::null_mut(),
        data_len: 0,
    } => {
        if engine.is_null() || out_data.is_null() || out_len.is_null() || out_w.is_null() || out_h.is_null() {
            return Response {
                success: false,
                error_msg: crate::utils::error::make_error_string("Invalid parameters"),
                data_ptr: std::ptr::null_mut(),
                data_len: 0,
            };
        }
        let eng = unsafe { &*engine };
        match eng.render_page_to_bgra_cookie(page, width, height, cookie) {
            Ok((bgra, w, h)) => {
                let boxed_slice = bgra.into_boxed_slice();
                let len = boxed_slice.len() as u64;
                let ptr = Box::into_raw(boxed_slice) as *mut u8;
                unsafe {
                    *out_data = ptr;
                    *out_len = len;
                    *out_w = w;
                    *out_h = h;
                }
                Response {
                    success: true,
                    error_msg: std::ptr::null(),
                    data_ptr: std::ptr::null_mut(),
                    data_len: 0,
                }
            }
            Err(e) => Response {
                success: false,
                error_msg: crate::utils::error::make_error_string(&e),
                data_ptr: std::ptr::null_mut(),
                data_len: 0,
            },
        }
    })
}

// ============================================================================
// Annotation Functions (Phase 2-3)
// ============================================================================

/// Save annotations to JSON file
#[unsafe(no_mangle)]
pub extern "C" fn annotations_save(
    json: *const u8,
    path: *const u8,
) -> Response {
    safe_ffi!(Response {
        success: false,
        error_msg: crate::utils::error::make_error_string("native panic in annotations_save"),
        data_ptr: std::ptr::null_mut(),
        data_len: 0,
    } => {
        if json.is_null() || path.is_null() {
            return Response {
                success: false,
                error_msg: crate::utils::error::make_error_string("Null parameters"),
                data_ptr: std::ptr::null_mut(),
                data_len: 0,
            };
        }

        // Convert C strings to Rust
        let json_str = match crate::ffi::marshalling::c_string_to_rust(json) {
            Ok(s) => s,
            Err(e) => {
                return Response {
                    success: false,
                    error_msg: crate::utils::error::make_error_string(&e),
                    data_ptr: std::ptr::null_mut(),
                    data_len: 0,
                };
            }
        };

        let path_str = match crate::ffi::marshalling::c_string_to_rust(path) {
            Ok(s) => s,
            Err(e) => {
                return Response {
                    success: false,
                    error_msg: crate::utils::error::make_error_string(&e),
                    data_ptr: std::ptr::null_mut(),
                    data_len: 0,
                };
            }
        };

        // Validate JSON
        match crate::storage::json_adapter::deserialize_annotations(&json_str) {
            Ok(_) => {
                // JSON is valid, write to file
                match crate::storage::file_handler::write_json_file(&json_str, &path_str) {
                    Ok(_) => Response {
                        success: true,
                        error_msg: std::ptr::null(),
                        data_ptr: std::ptr::null_mut(),
                        data_len: 0,
                    },
                    Err(e) => Response {
                        success: false,
                        error_msg: crate::utils::error::make_error_string(&e),
                        data_ptr: std::ptr::null_mut(),
                        data_len: 0,
                    },
                }
            }
            Err(e) => Response {
                success: false,
                error_msg: crate::utils::error::make_error_string(&e),
                data_ptr: std::ptr::null_mut(),
                data_len: 0,
            },
        }
    })
}

/// Load annotations from JSON file
#[unsafe(no_mangle)]
pub extern "C" fn annotations_load(
    path: *const u8,
) -> Response {
    safe_ffi!(Response {
        success: false,
        error_msg: crate::utils::error::make_error_string("native panic in annotations_load"),
        data_ptr: std::ptr::null_mut(),
        data_len: 0,
    } => {
        if path.is_null() {
            return Response {
                success: false,
                error_msg: crate::utils::error::make_error_string("Null path"),
                data_ptr: std::ptr::null_mut(),
                data_len: 0,
            };
        }

        // Convert C string to Rust
        let path_str = match crate::ffi::marshalling::c_string_to_rust(path) {
            Ok(s) => s,
            Err(e) => {
                return Response {
                    success: false,
                    error_msg: crate::utils::error::make_error_string(&e),
                    data_ptr: std::ptr::null_mut(),
                    data_len: 0,
                };
            }
        };

        // Read file
        match crate::storage::file_handler::read_json_file(&path_str) {
            Ok(json_str) => {
                // Validate JSON
                match crate::storage::json_adapter::deserialize_annotations(&json_str) {
                    Ok(_) => {
                        // Return JSON data
                        let data_ptr = crate::ffi::marshalling::rust_string_to_c(&json_str);
                        let data_len = json_str.len() as u64;
                        Response {
                            success: true,
                            error_msg: std::ptr::null(),
                            data_ptr,
                            data_len,
                        }
                    }
                    Err(e) => Response {
                        success: false,
                        error_msg: crate::utils::error::make_error_string(&e),
                        data_ptr: std::ptr::null_mut(),
                        data_len: 0,
                    },
                }
            }
            Err(e) => Response {
                success: false,
                error_msg: crate::utils::error::make_error_string(&e),
                data_ptr: std::ptr::null_mut(),
                data_len: 0,
            },
        }
    })
}

/// Process and validate annotations
#[unsafe(no_mangle)]
pub extern "C" fn annotations_process(
    json: *const u8,
) -> Response {
    safe_ffi!(Response {
        success: false,
        error_msg: crate::utils::error::make_error_string("native panic in annotations_process"),
        data_ptr: std::ptr::null_mut(),
        data_len: 0,
    } => {
        if json.is_null() {
            return Response {
                success: false,
                error_msg: crate::utils::error::make_error_string("Null JSON"),
                data_ptr: std::ptr::null_mut(),
                data_len: 0,
            };
        }

        // Convert C string to Rust
        let json_str = match crate::ffi::marshalling::c_string_to_rust(json) {
            Ok(s) => s,
            Err(e) => {
                return Response {
                    success: false,
                    error_msg: crate::utils::error::make_error_string(&e),
                    data_ptr: std::ptr::null_mut(),
                    data_len: 0,
                };
            }
        };

        // Deserialize JSON to annotations
        match crate::storage::json_adapter::deserialize_annotations(&json_str) {
            Ok(annotations) => {
                // Validate all annotations
                match crate::annotations::processor::validate_all(&annotations) {
                    Ok(_) => Response {
                        success: true,
                        error_msg: std::ptr::null(),
                        data_ptr: std::ptr::null_mut(),
                        data_len: 0,
                    },
                    Err(e) => Response {
                        success: false,
                        error_msg: crate::utils::error::make_error_string(&e),
                        data_ptr: std::ptr::null_mut(),
                        data_len: 0,
                    },
                }
            }
            Err(e) => Response {
                success: false,
                error_msg: crate::utils::error::make_error_string(&e),
                data_ptr: std::ptr::null_mut(),
                data_len: 0,
            },
        }
    })
}

// ============================================================================
// Memory Management
// ============================================================================

/// Free memory allocated by Rust and returned to C#
/// If len is 0, ptr is treated as a C-style null-terminated string and freed using CString.
/// If len > 0, ptr is treated as a boxed slice of bytes (Box<[u8]>) and freed using Box.
#[unsafe(no_mangle)]
pub extern "C" fn memory_free(ptr: *mut u8, len: u64) {
    safe_ffi!(() => {
        if !ptr.is_null() {
            unsafe {
                if len == 0 {
                    // Free as null-terminated CString
                    let _ = std::ffi::CString::from_raw(ptr as *mut std::os::raw::c_char);
                } else {
                    // Free as boxed slice
                    let slice = std::slice::from_raw_parts_mut(ptr, len as usize);
                    let _ = Box::from_raw(slice);
                }
            }
        }
    })
}

// ============================================================================
// Headless crash reproduction (right-click drag-zoom crash investigation)
// ============================================================================
//
// Drives the real `mu_render_page_region_bgra` FFI path for the exact PDF that
// crashed during right-click drag-zoom, across a sweep of zooms and tiles
// (including partial/edge tiles at high zoom, a known panic/AV candidate).
//
// Outcome interpretation:
//   * A Rust panic inside the render is caught by `safe_ffi!` and surfaces as
//     `success=false` with "native panic in ..." — recorded below, not a crash.
//   * A C-level access violation inside MuPDF kills this process, but the VEH
//     writes `crash_native_av.log` (VA + PC) — re-run and read it.
//   * No failures => the crash depends on GUI-specific state (concurrent
//     thread-pool rendering, abort-cookie churn, or a zoom value between the
//     sampled points) and is not reproducible by a static single-threaded sweep.
//
// Skips silently if the fixture PDF is absent (so it never breaks CI/ship).
#[cfg(test)]
mod repro_tests {
    use super::*;
    use std::ffi::{CStr, CString};
    use std::path::Path;

    const FIXTURE: &str = "D:\\测试\\加强员工着装管理规定.pdf";
    const ZOOMS: &[f32] = &[0.1, 0.5, 1.0, 2.0, 4.0];

    #[test]
    fn repro_render_across_zooms() {
        if !Path::new(FIXTURE).exists() {
            eprintln!("repro: fixture not found at {FIXTURE}; skipping");
            return;
        }
        let cpath = match CString::new(FIXTURE) {
            Ok(p) => p,
            Err(_) => return,
        };
        let res = mu_engine_open(cpath.as_ptr() as *const u8, std::ptr::null(), 0);
        if res.engine.is_null() {
            eprintln!(
                "repro: engine open failed: {:?}",
                unsafe { res.error_msg.as_ref().map(|p| CStr::from_ptr(p as *const u8 as *const i8).to_string_lossy().into_owned()) }
            );
            return;
        }
        let engine = res.engine;
        let count = mu_engine_page_count(engine);
        eprintln!("repro: opened, pages={count}");

        let mut errors: Vec<String> = Vec::new();
        for page in 0..count.min(3) {
            let mut w: f32 = 0.0;
            let mut h: f32 = 0.0;
            mu_engine_page_dimensions(engine, page, &mut w, &mut h);
            for &z in ZOOMS {
                let dev_w = ((w * z * 96.0).round() as u32).max(1);
                let dev_h = ((h * z * 96.0).round() as u32).max(1);
                let tile = 512u32;
                let nx = (dev_w + tile - 1) / tile;
                let ny = (dev_h + tile - 1) / tile;
                // Sample first / middle / last tiles (edge + partial tiles count).
                let xs: Vec<u32> = sample_indices(nx);
                let ys: Vec<u32> = sample_indices(ny);
                for &ty in &ys {
                    for &tx in &xs {
                        let x0 = tx * tile;
                        let y0 = ty * tile;
                        let x1 = ((tx + 1) * tile).min(dev_w);
                        let y1 = ((ty + 1) * tile).min(dev_h);
                        let mut out_data = std::ptr::null_mut();
                        let mut out_len = 0u64;
                        let mut out_w = 0u32;
                        let mut out_h = 0u32;
                        let resp = mu_render_page_region_bgra(
                            engine, page, dev_w, dev_h, x0, y0, x1, y1,
                            std::ptr::null_mut(), &mut out_data,
                            &mut out_len, &mut out_w, &mut out_h,
                        );
                        if !resp.success {
                            let msg = if resp.error_msg.is_null() {
                                "<null>".to_string()
                            } else {
                                unsafe { CStr::from_ptr(resp.error_msg as *const i8) }
                                    .to_string_lossy()
                                    .into_owned()
                            };
                            errors.push(format!(
                                "page{page} z{z} tile({x0},{y0})-({x1},{y1}) dev{dev_w}x{dev_h}: {msg}"
                            ));
                        } else if !out_data.is_null() {
                            memory_free(out_data, out_len);
                        }
                    }
                }
            }
        }
        mu_engine_destroy(engine);

        if errors.is_empty() {
            eprintln!("repro: completed sweep with no render errors");
        } else {
            eprintln!("repro: {} render error(s):", errors.len());
            for e in errors.iter().take(30) {
                eprintln!("  {e}");
            }
        }
    }

    /// Pick up to ~5 representative column/row indices: first, a few spread
    /// across the middle, and the last (so partial edge tiles are exercised).
    fn sample_indices(n: u32) -> Vec<u32> {
        if n == 0 {
            return vec![];
        }
        let mut v: Vec<u32> = vec![0, n.saturating_sub(1)];
        if n > 2 {
            v.push(n / 2);
        }
        if n > 4 {
            v.push(n / 4);
            v.push(3 * n / 4);
        }
        v.sort_unstable();
        v.dedup();
        v
    }
}

// ============================================================================
// Faithful headless reproduction of the RIGHT-CLICK DRAG-ZOOM CRASH.
//
// The crash fires at GESTURE SETTLE (not during the live drag — the live drag
// only rescales existing GPU tiles and skips QueueVisiblePages):
//   EndRightZoomGesture -> FinishZoom -> QueueVisiblePages -> RequestRendering
//   -> N worker threads each call mu_render_page_region_bgra concurrently,
//      while the UI thread writes abort=1 (cross-thread) into in-flight cookies
//      whose MuPDF rasterization is serialized behind render_lock.
//
// This harness mirrors that exactly:
//   * tiles only (no whole-page path is used by the gesture),
//   * fractional "settle" zooms (the spring lands on an arbitrary value,
//     NOT one of the 5 discrete sweep points),
//   * concurrent renders + cross-thread abort churn.
//
// Outcome signals:
//   * A Rust panic inside a render is caught by `safe_ffi!` -> success=false
//     with "native panic in ..."; collected below as a soft finding.
//   * A C-level AV inside MuPDF kills this test process; the VEH writes
//     `crash_native_av.log` (VA + PC) BEFORE the OS terminates it. Re-run and
//     read the log.
//   * If the process survives with only "aborted *" errors -> the shared-memory
//     abort contract holds and the live crash needs GUI-only state (fz_store
//     corruption accumulated across a real session, or a zoom/scroll value
//     between the sampled points) that a static headless sweep cannot synthesize.
// ============================================================================
#[cfg(test)]
mod repro_abort_stress {
    use super::*;
    use std::ffi::{CStr, CString};
    use std::path::Path;
    use std::sync::{Arc, Mutex};
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::thread;
    use std::time::{Duration, Instant};

    // Raw pointers are !Send/!Sync, but here we only ever share the *address*
    // (pass it to the FFI, or volatile-store abort=1 into it). The pointee is
    // never dereferenced across threads, so asserting Send/Sync is sound.
    struct SendPtr<T>(*const T);
    unsafe impl<T> Send for SendPtr<T> {}
    unsafe impl<T> Sync for SendPtr<T> {}

    const FIXTURE: &str = "D:\\测试\\加强员工着装管理规定.pdf";

    #[test]
    fn repro_concurrent_abort_stress() {
        // Remove any stale AV log from a previous run so presence == this run.
        let _ = std::fs::remove_file("crash_native_av.log");

        if !Path::new(FIXTURE).exists() {
            eprintln!("repro: fixture not found at {FIXTURE}; skipping");
            return;
        }
        let cpath = match CString::new(FIXTURE) {
            Ok(p) => p,
            Err(_) => return,
        };
        let res = mu_engine_open(cpath.as_ptr() as *const u8, std::ptr::null(), 6);
        if res.engine.is_null() {
            eprintln!("repro: engine open failed");
            return;
        }
        let engine = res.engine;
        let count = mu_engine_page_count(engine);
        let mut dims: Vec<(f32, f32)> = Vec::new();
        for p in 0..count {
            let mut w = 0.0f32;
            let mut h = 0.0f32;
            mu_engine_page_dimensions(engine, p, &mut w, &mut h);
            dims.push((w, h));
        }
        if dims.is_empty() {
            eprintln!("repro: no pages");
            mu_engine_destroy(engine);
            return;
        }
        eprintln!("repro: opened, pages={count}");

        // Final-zoom candidates the right-drag spring can settle on (fractional).
        // Set GLANCE_REPRO_INT_ZOOMS=1 to use the discrete 0.1..4.0 set instead.
        let settle_zooms: &[f32] = if std::env::var("GLANCE_REPRO_INT_ZOOMS").is_ok() {
            &[0.1, 0.5, 1.0, 2.0, 4.0]
        } else {
            &[0.83, 1.27, 1.73, 2.13, 2.63, 3.41, 3.97]
        };

        // Shared registry of cookies currently in flight (the C# _curReqs analog).
        let live_cookies: Arc<Mutex<Vec<SendPtr<u8>>>> = Arc::new(Mutex::new(Vec::new()));
        let stop = Arc::new(AtomicBool::new(false));

        // ---- UI / aborter thread: writes abort=1 into a random in-flight cookie.
        // Held under the lock during the single volatile store so the worker
        // cannot free the cookie mid-write (mirrors _requestAccess guarding both
        // Abort and Free in the production code).
        // Set GLANCE_REPRO_NO_ABORT=1 to skip abort churn (isolates whether the
        // crash is the abort path specifically vs. plain concurrent rendering).
        let abort_enabled = std::env::var("GLANCE_REPRO_NO_ABORT").is_err();
        let ui_live = Arc::clone(&live_cookies);
        let ui_stop = Arc::clone(&stop);
        let ui = if abort_enabled {
            Some(thread::spawn(move || {
                let mut rng: u64 = 0x9E37_79B9_7F4A_7C15;
                while !ui_stop.load(Ordering::Relaxed) {
                    {
                        let g = ui_live.lock().unwrap();
                        if !g.is_empty() {
                            rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17;
                            let idx = (rng >> 1) % g.len() as u64;
                            let ptr: *mut u8 = g[idx as usize].0 as *mut u8;
                            // matches C# GlanceCookie.Abort: WriteInt32(cookie, 0, 1)
                            unsafe { std::ptr::write_volatile(ptr as *mut i32, 1) };
                        }
                    }
                    thread::sleep(Duration::from_micros(15));
                }
            }))
        } else {
            None
        };

        // ---- Worker threads: concurrent tile renders at settle zooms. ----
        // GLANCE_REPRO_WORKERS overrides the thread count (1 = single-threaded).
        let n_workers: usize = std::env::var("GLANCE_REPRO_WORKERS")
            .ok()
            .and_then(|s| s.parse::<usize>().ok())
            .filter(|&n| n >= 1)
            .unwrap_or(8);
        let mut handles = Vec::new();
        for wid in 0..n_workers {
            let live = Arc::clone(&live_cookies);
            let stop_w = Arc::clone(&stop);
            let dims = dims.clone();
            let engine_addr = engine as usize; // usize is Send; raw ptr is not
            let h = thread::spawn(move || {
                let mut rng: u64 = 0x1234_5678_9ABC_DEF0u64.wrapping_add((wid as u64) * 0x1111_1111_1111_1111);
                let mut real_errors: Vec<String> = Vec::new();
                let mut benign_aborts: u64 = 0;
                let deadline = Instant::now() + Duration::from_secs(6);
                while !stop_w.load(Ordering::Relaxed) && Instant::now() < deadline {
                    let p = (rng as usize) % dims.len();
                    let (pw, ph) = dims[p];
                    rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17;
                    let z = settle_zooms[(rng as usize) % settle_zooms.len()];
                    let dev_w = ((pw * z * 96.0).round() as u32).max(1);
                    let dev_h = ((ph * z * 96.0).round() as u32).max(1);
                    let tile = 512u32;
                    let nx = (dev_w + tile - 1) / tile;
                    let ny = (dev_h + tile - 1) / tile;
                    rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17;
                    let tx = (rng as u32) % nx.max(1);
                    let ty = (rng as u32) % ny.max(1);
                    let x0 = tx * tile;
                    let y0 = ty * tile;
                    let x1 = ((tx + 1) * tile).min(dev_w);
                    let y1 = ((ty + 1) * tile).min(dev_h);

                    // Allocate a 24-byte cookie (== sizeof(fz_cookie)); abort=0.
                    let cookie = Box::into_raw(Box::new([0u8; 24])) as *mut u8;
                    live.lock().unwrap().push(SendPtr(cookie as *const u8));

                    let mut od = std::ptr::null_mut();
                    let mut ol = 0u64;
                    let mut ow = 0u32;
                    let mut oh = 0u32;
                    let resp = mu_render_page_region_bgra(
                        engine_addr as *const MuEngine, p as u32, dev_w, dev_h, x0, y0, x1, y1,
                        cookie, &mut od, &mut ol, &mut ow, &mut oh,
                    );
                    {
                        let mut g = live.lock().unwrap();
                        if let Some(pos) = g.iter().position(|c| c.0 as *mut u8 == cookie) {
                            g.remove(pos);
                        }
                    }

                    if !resp.success {
                        let msg = if resp.error_msg.is_null() {
                            "<null>".to_string()
                        } else {
                            unsafe { CStr::from_ptr(resp.error_msg as *const i8) }
                                .to_string_lossy()
                                .into_owned()
                        };
                        if msg.to_lowercase().contains("abort") {
                            benign_aborts += 1;
                        } else {
                            real_errors.push(format!(
                                "page{p} z{z} tile({x0},{y0})-({x1},{y1}) dev{dev_w}x{dev_h}: {msg}"
                            ));
                        }
                    } else if !od.is_null() {
                        memory_free(od, ol);
                    }
                    // Free the cookie (lifetime owned solely by this worker).
                    unsafe { drop(Box::from_raw(cookie as *mut [u8; 24])); }
                }
                (benign_aborts, real_errors)
            });
            handles.push(h);
        }

        let mut total_benign = 0u64;
        let mut all_real: Vec<String> = Vec::new();
        for h in handles {
            if let Ok((b, re)) = h.join() {
                total_benign += b;
                all_real.extend(re);
            }
        }
        stop.store(true, Ordering::Relaxed);
        if let Some(ui) = ui { let _ = ui.join(); }
        mu_engine_destroy(engine);

        if Path::new("crash_native_av.log").exists() {
            eprintln!("repro: *** C-LEVEL ACCESS VIOLATION OCCURRED (crash_native_av.log present) ***");
        }
        eprintln!("repro: abort_churn={abort_enabled}");
        if all_real.is_empty() {
            eprintln!(
                "repro: stress completed, {total_benign} benign aborts, 0 real errors (shared-memory abort contract holds)"
            );
        } else {
            eprintln!("repro: {} REAL render error(s) (non-abort):", all_real.len());
            for e in all_real.iter().take(30) {
                eprintln!("  {e}");
            }
        }
    }
}
