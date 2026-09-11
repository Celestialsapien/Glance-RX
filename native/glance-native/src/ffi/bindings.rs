// FFI Bindings - extern "C" functions callable from C#

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
}

/// Reload the document from disk, dropping every parsed display list so the
/// old document's resources (image streams etc.) return to the OS. This is the
/// in-process analogue of the worker `reload`, used by the idle cache-flush.
/// Returns true on success.
#[unsafe(no_mangle)]
pub extern "C" fn mu_engine_reload(engine: *const MuEngine) -> bool {
    if engine.is_null() {
        return false;
    }
    let eng = unsafe { &*engine };
    eng.reload().is_ok()
}

/// Destroy a MuPDF engine and free its document + display lists.
#[unsafe(no_mangle)]
pub extern "C" fn mu_engine_destroy(engine: *const MuEngine) {
    if !engine.is_null() {
        unsafe {
            drop(Box::from_raw(engine as *mut MuEngine));
        }
    }
}

/// Get total page count. Cheap; no global lock needed.
#[unsafe(no_mangle)]
pub extern "C" fn mu_engine_page_count(engine: *const MuEngine) -> u32 {
    if engine.is_null() {
        return 0;
    }
    let eng = unsafe { &*engine };
    eng.get_page_count()
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
    if engine.is_null() || out_dims.is_null() || count == 0 {
        return false;
    }
    let eng = unsafe { &*engine };
    let out = unsafe { std::slice::from_raw_parts_mut(out_dims, (count as usize) * 2) };
    eng.get_all_page_dimensions(count, out);
    true
}

/// Pre-parse a page into its DisplayList and cache it (cold-start warmup: the
/// first tile render of that page then hits the cache instead of paying the
/// content-stream parse). No-op if already cached. Returns true on success;
/// false on null engine / invalid page.
#[unsafe(no_mangle)]
pub extern "C" fn mu_engine_warmup_page(engine: *const MuEngine, page: u32) -> bool {
    if engine.is_null() {
        return false;
    }
    let eng = unsafe { &*engine };
    eng.warmup_page(page)
}

/// Render one tile (an axis-aligned sub-rectangle of a page's device space) to
/// tightly-packed BGRA (stride = width*4), matching WinUI WriteableBitmap byte
/// order. THREAD-SAFE: call concurrently from the C# thread pool with the same
/// engine handle — each thread renders on its own fz_context, no global lock.
///
/// `dev_w`/`dev_h` define the full-page raster density; `x0,y0,x1,y1` are pixel
/// coords of the tile within that page. On success writes out_data (free with
/// memory_free(ptr, len)), out_len, out_w, out_h and returns success=true.
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
    out_data: *mut *mut u8,
    out_len: *mut u64,
    out_w: *mut u32,
    out_h: *mut u32,
) -> Response {
    if engine.is_null() || out_data.is_null() || out_len.is_null() || out_w.is_null() || out_h.is_null() {
        return Response {
            success: false,
            error_msg: crate::utils::error::make_error_string("Invalid parameters"),
            data_ptr: std::ptr::null_mut(),
            data_len: 0,
        };
    }
    let eng = unsafe { &*engine };
    match eng.render_page_region_bgra(page, dev_w, dev_h, x0, y0, x1, y1) {
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
}

/// Render a whole page to tightly-packed BGRA. Thread-safe. Convenience for
/// single page render (page jumps) without tile arithmetic.
#[unsafe(no_mangle)]
pub extern "C" fn mu_render_page_bgra(
    engine: *const MuEngine,
    page: u32,
    width: u32,
    height: u32,
    out_data: *mut *mut u8,
    out_len: *mut u64,
    out_w: *mut u32,
    out_h: *mut u32,
) -> Response {
    if engine.is_null() || out_data.is_null() || out_len.is_null() || out_w.is_null() || out_h.is_null() {
        return Response {
            success: false,
            error_msg: crate::utils::error::make_error_string("Invalid parameters"),
            data_ptr: std::ptr::null_mut(),
            data_len: 0,
        };
    }
    let eng = unsafe { &*engine };
    match eng.render_page_to_bgra(page, width, height) {
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
}

/// Export a page's display list as SVG (resolution-independent vector command
/// stream in page user space). THREAD-SAFE. On success `data_ptr` holds the
/// UTF-8 SVG string (free with memory_free(ptr, len)).
#[unsafe(no_mangle)]
pub extern "C" fn mu_export_page_svg(
    engine: *const MuEngine,
    page: u32,
    out_data: *mut *mut u8,
    out_len: *mut u64,
) -> Response {
    if engine.is_null() || out_data.is_null() || out_len.is_null() {
        return Response {
            success: false,
            error_msg: crate::utils::error::make_error_string("Invalid parameters"),
            data_ptr: std::ptr::null_mut(),
            data_len: 0,
        };
    }
    let eng = unsafe { &*engine };
    match eng.export_page_svg(page) {
        Ok(svg) => {
            let boxed_slice = svg.into_bytes().into_boxed_slice();
            let len = boxed_slice.len() as u64;
            let ptr = Box::into_raw(boxed_slice) as *mut u8;
            unsafe {
                *out_data = ptr;
                *out_len = len;
            }
            Response {
                success: true,
                error_msg: std::ptr::null(),
                data_ptr: ptr,
                data_len: len,
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

/// Export a page's display list as a resolution-independent binary command
/// stream (paths in page user space + referenced image bitmaps). THREAD-SAFE.
/// On success `data_ptr` holds the stream (free with memory_free(ptr, len)).
/// Layout: u32 magic "GVDC", u32 version, f32 page_w, f32 page_h,
/// u32 n_cmds, u32 n_images, u32 n_skipped, then skipped table
/// (n_skipped × [u8 tag, u32 count]), then commands, then images
/// (each: u32 id, u32 w, u32 h, u32 data_len, BGRA bytes).
#[unsafe(no_mangle)]
pub extern "C" fn mu_export_page_commands(
    engine: *const MuEngine,
    page: u32,
    out_data: *mut *mut u8,
    out_len: *mut u64,
) -> Response {
    if engine.is_null() || out_data.is_null() || out_len.is_null() {
        return Response {
            success: false,
            error_msg: crate::utils::error::make_error_string("Invalid parameters"),
            data_ptr: std::ptr::null_mut(),
            data_len: 0,
        };
    }
    let eng = unsafe { &*engine };
    match eng.export_page_commands(page) {
        Ok(cmds) => {
            let boxed_slice = cmds.commands.into_boxed_slice();
            let len = boxed_slice.len() as u64;
            let ptr = Box::into_raw(boxed_slice) as *mut u8;
            unsafe {
                *out_data = ptr;
                *out_len = len;
            }
            Response {
                success: true,
                error_msg: std::ptr::null(),
                data_ptr: ptr,
                data_len: len,
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

// ============================================================================
// Annotation Functions (Phase 2-3)
// ============================================================================

/// Save annotations to JSON file
#[unsafe(no_mangle)]
pub extern "C" fn annotations_save(
    json: *const u8,
    path: *const u8,
) -> Response {
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
}

/// Load annotations from JSON file
#[unsafe(no_mangle)]
pub extern "C" fn annotations_load(
    path: *const u8,
) -> Response {
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
}

/// Process and validate annotations
#[unsafe(no_mangle)]
pub extern "C" fn annotations_process(
    json: *const u8,
) -> Response {
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
}

// ============================================================================
// Memory Management
// ============================================================================

/// Free memory allocated by Rust and returned to C#
/// If len is 0, ptr is treated as a C-style null-terminated string and freed using CString.
/// If len > 0, ptr is treated as a boxed slice of bytes (Box<[u8]>) and freed using Box.
#[unsafe(no_mangle)]
pub extern "C" fn memory_free(ptr: *mut u8, len: u64) {
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
}

// ============================================================================
// Test Helper Function (Phase 1 only)
// ============================================================================

/// Simple test function to verify DLL loads
#[unsafe(no_mangle)]
pub extern "C" fn test_ffi_works() -> bool {
    true
}
