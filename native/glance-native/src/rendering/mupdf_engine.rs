// Glance - a high-performance PDF reader
// Copyright (C) 2026 Adrian
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
// SPDX-License-Identifier: AGPL-3.0

// MuPDF single-process, multi-thread rendering engine.
//
// This is the replacement for the out-of-process PDFium worker pool. The whole
// bet ("彻底：单进程多线程") rests on one fact: MuPDF DISPLAY LISTS are the one
// parsed object that is SAFE to replay concurrently from many threads (they are
// reference-counted, context-independent recording of drawing ops; the mupdf
// crate even marks `DisplayList` as `Send + Sync`). So this engine:
//
//   * opens the document exactly once in-process (no more N worker processes);
//   * keeps a bounded LRU of parsed `DisplayList`s, keyed by page index;
//   * hands out `Arc<DisplayList>` clones so any number of threads can hold
//     references to the same page (lock-free sharing); the actual
//     `fz_run_display_list` is serialized behind `render_lock` (see field doc),
//     because MuPDF cannot safely run concurrently on one shared `fz_document`
//     — each render still runs on its own thread-local `fz_context` (which share
//     the one `fz_store` via `fz_clone_context`).
//
// All `Document` access (load/open/reload/dims) is serialized behind a single
// `Mutex`; only the already-parsed `Arc<DisplayList>` is shared lock-free for
// rendering. This is exactly the concurrency model the spike measured as ~6.3x
// on concurrent tile fill, without any cross-process marshalling.

use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex};

use mupdf::{Colorspace, Device, DisplayList, Document, IRect, Matrix, Pixmap, Rect};

/// Error returned when opening a PDF document.
pub enum EngineOpenError {
    /// The document is encrypted and no (correct) password was supplied.
    PasswordRequired,
    /// Any other failure (file not found, parse error, etc.).
    Other(String),
}

/// A page that has been parsed ONCE into a display list, plus its point size.
/// `dl` is `Arc` so many render threads can hold independent references.
struct ParsedPage {
    dl: Arc<DisplayList>,
    w_pt: f32,
    h_pt: f32,
}

/// Everything that touches the `Document` or the parsed-page cache. Guarded by
/// the engine's single `Mutex`; the render path only clones an `Arc<DisplayList>`
/// out of here and never holds this lock while rasterizing.
struct MupdfState {
    doc: Document,
    cache: HashMap<u32, Arc<ParsedPage>>,
    /// Page indices in recency order, front = most recently used.
    order: VecDeque<u32>,
    /// Max resident parsed pages before the LRU victim is evicted.
    /// `None` = unbounded (= Sumatra EngineMupdf 的 pageInfos：每页 FzPageInfo
    /// 含 display list 永久持有，文档生命周期内不逐出)。
    capacity: Option<usize>,
    path: String,
    password: Option<String>,
}

/// Single-process MuPDF engine. `&self` methods are thread-safe: rendering is
/// lock-free (shares `Arc<DisplayList>`), document access is serialized.
pub struct MupdfEngineImpl {
    inner: Mutex<MupdfState>,
    /// Serializes the actual MuPDF rasterization (`fz_run_display_list`) against
    /// the single shared document, 1:1 with Sumatra's `renderLock`
    /// (EngineMupdf.cpp:6517).
    ///
    /// WHY THIS IS REQUIRED (root-cause fix, not a band-aid): the old
    /// "out-of-process PDFium worker pool" design gave each worker its OWN
    /// document instance, so firing many concurrent renders was safe (isolated
    /// documents). The single-process refactor kept that "fire N concurrent
    /// renders, no lock" assumption against ONE shared `fz_document` — which is
    /// UNSAFE. Sumatra's own source documents the corruption at
    /// EngineMupdf.cpp:6308: concurrent `fz_run_page`/`fz_run_display_list` on one
    /// document race on shared content-stream filter state. The practical cost we
    /// observed: at high zoom a magazine page's full scan decodes to ~1 GB; 16
    /// threads each trigger that full decode at once (16 GB transient) and thrash
    /// the shared `fz_store` (cloned per-thread contexts share ONE store via
    /// `fz_clone_context`) so every tile re-decodes → drop-time CPU spike + memory
    /// "开倒车". Serializing lets the FIRST tile decode the image and every sibling
    /// tile of the page reuse it (store amortization), eliminating both symptoms.
    render_lock: Mutex<()>,
}

// The engine is shared across C# thread-pool calls; the only shared objects are
// the mutex (safe) and `DisplayList` (Send+Sync), so these are sound.
unsafe impl Send for MupdfEngineImpl {}
unsafe impl Sync for MupdfEngineImpl {}

impl MupdfEngineImpl {
    /// Open `pdf_path` (optionally with a password) and build an in-process engine.
    ///
    /// `hot_capacity` bounds the parsed-page LRU. `0` = unbounded（1:1 Sumatra
    /// pageInfos：DL 每页永久缓存。⚠ 这不是纯内存优化问题：LRU 逐出后重建
    /// `to_display_list` 会生成全新 fz_image 对象，store 里旧页的已解码图变成
    /// 孤儿被逐出 → 每次瓦片渲染重新解码整页扫描图（~200ms/块）= CPU 风暴根因）。
    /// `n > 0` = bounded LRU（诊断/探针页用）。密码错误/缺失抛 PasswordRequired。
    pub fn new(pdf_path: &str, password: Option<&str>, hot_capacity: usize) -> Result<Self, EngineOpenError> {
        let capacity = if hot_capacity == 0 { None } else { Some(hot_capacity) };
        let mut doc = Document::open(&pdf_path).map_err(|e| {
            EngineOpenError::Other(format!("mupdf failed to open '{}': {e}", pdf_path))
        })?;

        // Encrypted document: need the right password.
        let needs_pw = doc.needs_password().map_err(|e| {
            EngineOpenError::Other(format!("mupdf needs_password failed: {e}"))
        })?;
        if needs_pw {
            match password {
                Some(pw) => {
                    let ok = doc.authenticate(pw).map_err(|e| {
                        EngineOpenError::Other(format!("mupdf authenticate failed: {e}"))
                    })?;
                    if !ok {
                        return Err(EngineOpenError::PasswordRequired);
                    }
                }
                None => return Err(EngineOpenError::PasswordRequired),
            }
        }

        Ok(MupdfEngineImpl {
            inner: Mutex::new(MupdfState {
                doc,
                cache: HashMap::new(),
                order: VecDeque::new(),
                capacity,
                path: pdf_path.to_string(),
                password: password.map(str::to_string),
            }),
            render_lock: Mutex::new(()),
        })
    }

    /// Get a page's parsed display list (build + cache on miss), WITHOUT holding
    /// the lock after the `Arc` is cloned — so the caller can render concurrently.
    fn get_parsed(&self, page: u32) -> Result<Arc<ParsedPage>, String> {
        let mut st = self.inner.lock().unwrap_or_else(|p| p.into_inner());

        if st.cache.contains_key(&page) {
            // Clone the Arc out to END the immutable borrow of `st`, then reorder.
            let pp = st.cache.get(&page).unwrap().clone();
            st.order.retain(|&i| i != page);
            st.order.push_front(page);
            return Ok(pp);
        }

        // Cache miss: parse the page once (serialized by the lock).
        let page_obj = st.doc.load_page(page as i32)
            .map_err(|e| format!("mupdf load_page({page}) failed: {e}"))?;
        let dl = Arc::new(page_obj.to_display_list(false)
            .map_err(|e| format!("mupdf to_display_list({page}) failed: {e}"))?);
        let b = dl.bounds();
        let w_pt = b.x1 - b.x0;
        let h_pt = b.y1 - b.y0;
        let pp = Arc::new(ParsedPage { dl, w_pt, h_pt });

        // Evict LRU victim if at capacity（None = 不逐出，1:1 Sumatra pageInfos）。
        if let Some(cap) = st.capacity {
            if st.cache.len() >= cap {
                if let Some(victim) = st.order.pop_back() {
                    st.cache.remove(&victim);
                }
            }
        }
        st.cache.insert(page, Arc::clone(&pp));
        st.order.push_front(page);
        Ok(pp)
    }

    /// Cold-start warmup: parse a page into its DisplayList and cache it
    /// (no-op if already cached). Returns true when the page is ready for
    /// concurrent tile rendering.
    pub fn warmup_page(&self, page: u32) -> bool {
        self.get_parsed(page).is_ok()
    }

    /// Total number of pages.
    pub fn get_page_count(&self) -> u32 {
        let st = self.inner.lock().unwrap_or_else(|p| p.into_inner());
        // MuPDF keeps the page count cheap; fall back to the cached count.
        st.doc.page_count().unwrap_or(0).max(0) as u32
    }

    /// Page dimensions in points. Reads from the parsed-page cache when present,
    /// otherwise loads the page (MuPDF page load is lazy/cheap).
    pub fn get_page_dimensions(&self, page: u32) -> Result<(f32, f32), String> {
        let st = self.inner.lock().unwrap_or_else(|p| p.into_inner());
        // Prefer cache to avoid re-loading.
        if let Some(pp) = st.cache.get(&page) {
            return Ok((pp.w_pt, pp.h_pt));
        }
        let page_obj = st.doc.load_page(page as i32)
            .map_err(|e| format!("mupdf load_page({page}) failed: {e}"))?;
        let b = page_obj.bounds()
            .map_err(|e| format!("mupdf bounds({page}) failed: {e}"))?;
        Ok((b.x1 - b.x0, b.y1 - b.y0))
    }

    /// Fetch dimensions for pages 0..count in one call, writing 2 floats per page
    /// (w, h) into `out`. Prefers the parsed-page cache; pages that fail to load
    /// are written as (0.0, 0.0). A SINGLE lock hold + single FFI round trip
    /// replaces N per-page calls, so the C# load path can enumerate every page's
    /// size without paying N cross-language calls + N mutex acquisitions (this
    /// matters on multi-thousand-page PDFs).
    pub fn get_all_page_dimensions(&self, count: u32, out: &mut [f32]) {
        let mut st = self.inner.lock().unwrap_or_else(|p| p.into_inner());
        let n = (count as usize).min(out.len() / 2);
        for i in 0..n {
            if let Some(pp) = st.cache.get(&(i as u32)) {
                out[i * 2] = pp.w_pt;
                out[i * 2 + 1] = pp.h_pt;
                continue;
            }
            match st.doc.load_page(i as i32) {
                Ok(page_obj) => {
                    let b = match page_obj.bounds() {
                        Ok(b) => b,
                        Err(_) => {
                            out[i * 2] = 0.0;
                            out[i * 2 + 1] = 0.0;
                            continue;
                        }
                    };
                    out[i * 2] = b.x1 - b.x0;
                    out[i * 2 + 1] = b.y1 - b.y0;

                    // 冷启动提速：维度遍历本来就要逐页 load_page，顺带把第 0 页解析成
                    // DisplayList 并缓存。首屏第一块瓦片渲染时 get_parsed(0) 直接命中缓存，
                    // 跳过内容流解析，缩短"看到第一页"的时延（仅一页，成本可忽略）。
                    if i == 0 {
                        if let Ok(dl) = page_obj.to_display_list(false) {
                            let pp = Arc::new(ParsedPage {
                                dl: Arc::new(dl),
                                w_pt: b.x1 - b.x0,
                                h_pt: b.y1 - b.y0,
                            });
                            if let Some(cap) = st.capacity {
                                if st.cache.len() >= cap {
                                    if let Some(victim) = st.order.pop_back() {
                                        st.cache.remove(&victim);
                                    }
                                }
                            }
                            st.cache.insert(0, pp);
                            st.order.push_front(0);
                        }
                    }
                }
                Err(_) => {
                    out[i * 2] = 0.0;
                    out[i * 2 + 1] = 0.0;
                }
            }
        }
    }

    /// Render a full page directly to tightly-packed BGRA (fast path for the
    /// C# WriteableBitmap pipeline). Thread-safe (cancels through the cache).
    pub fn render_page_to_bgra(&self, page: u32, width: u32, height: u32) -> Result<(Vec<u8>, u32, u32), String> {
        self.render_page_to_bgra_cookie(page, width, height, std::ptr::null_mut())
    }

    /// `render_page_to_bgra` with a `fz_cookie`-compatible abort block
    /// (see `render_region_bgra` for the layout / ownership contract).
    pub fn render_page_to_bgra_cookie(
        &self,
        page: u32,
        width: u32,
        height: u32,
        cookie: *mut u8,
    ) -> Result<(Vec<u8>, u32, u32), String> {
        if width == 0 || height == 0 {
            return Err("invalid render size 0".into());
        }
        self.render_region_bgra(page, width, height, 0, 0, width, height, true, cookie)
    }

    /// Render an axis-aligned RECTANGLE of a page's unrotated device space
    /// (at raster density fixed by `dev_w`/`dev_h`) into a small BGRA bitmap
    /// exactly that size. Thread-safe: multiple concurrent tiles render in
    /// parallel from a shared display list, each on its own fz_context.
    ///
    /// Returns (bgra_bytes, tile_w, tile_h).
    pub fn render_page_region_bgra(
        &self,
        page: u32,
        dev_w: u32,
        dev_h: u32,
        x0: u32,
        y0: u32,
        x1: u32,
        y1: u32,
    ) -> Result<(Vec<u8>, u32, u32), String> {
        self.render_page_region_bgra_cookie(page, dev_w, dev_h, x0, y0, x1, y1, std::ptr::null_mut())
    }

    /// `render_page_region_bgra` with a `fz_cookie`-compatible abort block
    /// (see `render_region_bgra` for the layout / ownership contract).
    pub fn render_page_region_bgra_cookie(
        &self,
        page: u32,
        dev_w: u32,
        dev_h: u32,
        x0: u32,
        y0: u32,
        x1: u32,
        y1: u32,
        cookie: *mut u8,
    ) -> Result<(Vec<u8>, u32, u32), String> {
        if x1 <= x0 || y1 <= y0 || x1 > dev_w || y1 > dev_h {
            return Err(format!("invalid tile rect {x0},{y0}-{x1},{y1} in {dev_w}x{dev_h}"));
        }
        self.render_region_bgra(page, dev_w, dev_h, x0, y0, x1, y1, false, cookie)
    }

    fn render_region_bgra(
        &self,
        page: u32,
        dev_w: u32,
        dev_h: u32,
        x0: u32,
        y0: u32,
        x1: u32,
        y1: u32,
        whole_page: bool,
        cookie: *mut u8,
    ) -> Result<(Vec<u8>, u32, u32), String> {
        // fz_cookie 语义 1:1 Sumatra（EngineMupdf.cpp:6432 FitzAbortCookie）：
        // aborter（C# 侧，持 _requestAccess）跨线程写 cookie.abort=1，本渲染在
        // **阶段边界**（parse 前 / 抢 render_lock 前 / run 前）尽早放弃 ——
        // 注意不再交给 MuPDF 的 display-list 循环自查，原因见 render_tile_rgb。
        if cookie_aborted(cookie) {
            return Err("aborted before render".into());
        }
        let parsed = self.get_parsed(page)?;
        if cookie_aborted(cookie) {
            return Err("aborted after page parse".into());
        }
        let w_pt = parsed.w_pt;
        let h_pt = parsed.h_pt;
        if w_pt <= 0.0 || h_pt <= 0.0 {
            return Err("invalid page dimensions".into());
        }
        let dl = Arc::clone(&parsed.dl);

        let tw = (x1 - x0) as usize;
        let th = (y1 - y0) as usize;

        // Serialize the actual MuPDF rasterization behind renderLock — 1:1 with
        // Sumatra (EngineMupdf.cpp:6517). The display list is an `Arc` (safe to
        // clone outside the lock); only `fz_run_display_list` must be serialized
        // because it touches the shared document's stream-decoder state. Per-thread
        // cloned contexts share ONE `fz_store` (fz_clone_context), so serializing
        // lets the first tile decode an image and every sibling tile of the page
        // reuse it, instead of N threads each decoding the same ~1 GB scan at once.
        // Drop the guard right after the run so the RGB→BGRA copy stays lock-free.
        let rgb = {
            let _guard = self
                .render_lock
                .lock()
                .unwrap_or_else(|e| e.into_inner());
            if cookie_aborted(cookie) {
                return Err("aborted before render".into());
            }
            render_tile_rgb(&dl, w_pt, h_pt, dev_w, x0 as f32, y0 as f32, tw, th, whole_page, cookie)?
        };

        // RGB -> BGRA (WriteableBitmap's byte order).
        let mut bgra = Vec::with_capacity(tw * th * 4);
        for px in rgb.chunks_exact(3) {
            bgra.push(px[2]);
            bgra.push(px[1]);
            bgra.push(px[0]);
            bgra.push(255);
        }
        let _ = dev_h; // density is fully described by dev_w; dev_h kept for symmetry
        Ok((bgra, tw as u32, th as u32))
    }

    /// Drop every parsed page and re-open the document from disk. This releases
    /// all display lists + the old document's resources back to the OS (the in-
    /// process analogue of the worker `reload`). Page count/dims are unchanged.
    pub fn reload(&self) -> Result<(), String> {
        let mut st = self.inner.lock().unwrap_or_else(|p| p.into_inner());
        let mut doc = Document::open(&st.path).map_err(|e| format!("mupdf reload open failed: {e}"))?;
        if doc.needs_password().map_err(|e| format!("needs_password failed: {e}"))? {
            if let Some(pw) = st.password.as_deref() {
                let ok = doc.authenticate(pw).map_err(|e| format!("authenticate failed: {e}"))?;
                if !ok {
                    return Err("mupdf reload: wrong password".into());
                }
            } else {
                return Err("mupdf reload: password required".into());
            }
        }
        st.doc = doc;
        st.cache.clear();
        st.order.clear();
        Ok(())
    }
}

/// Cross-thread abort probe. `cookie` (may be null) is a C#-allocated 24-byte
/// block laid out exactly like MuPDF's `fz_cookie` (fitz/device.h):
/// `int abort; int progress; size_t progress_max; int errors; int incomplete;`
/// — i.e. `abort` at byte offset 0. The C# aborter writes 1 with a volatile
/// store while holding the request lock; we read with `read_volatile`.
///
/// ⚠ 只在「阶段边界」探测（parse 之前 / run 之前 / 锁之前），**绝不**把这个块
/// 交给 MuPDF 的 display-list 循环 —— 见 `render_tile_rgb` 顶部那段根因说明：
/// 中途 break 会把 draw device 留在不平衡状态，随后 crate 的裸 `Device::drop`
/// 会把 `fz_close_device` 的 rethrow 变成 `exit(EXIT_FAILURE)`（静默闪退）。
#[inline]
fn cookie_aborted(cookie: *mut u8) -> bool {
    if cookie.is_null() {
        return false;
    }
    unsafe { (cookie as *const i32).read_volatile() != 0 }
}

/// Rasterize one tile of a display list into an RGB row-major buffer of exactly
/// `tw`×`th`, mapping the page's device-pixel region (top-left at `x0`,`y0` of
/// the full page at density `dev_w`/w_pt) into buffer origin (0,0).
///
/// The caller (`render_region_bgra`) holds `render_lock` across this call,
/// serializing the MuPDF run against the shared `fz_document`. The
/// `Arc<DisplayList>` keeps the list alive; `fz_context` is per-thread (shared
/// store). Under the lock, only one thread rasterizes at a time, so the shared
/// `fz_store` amortizes image decodes across a page's tiles instead of each
/// thread decoding the same scan independently.
fn render_tile_rgb(
    dl: &DisplayList,
    w_pt: f32,
    _h_pt: f32,
    dev_w: u32,
    x0: f32,
    y0: f32,
    tw: usize,
    th: usize,
    whole_page: bool,
    cookie: *mut u8,
) -> Result<Vec<u8>, String> {
    let scale = dev_w as f32 / w_pt;

    let cs = Colorspace::device_rgb();
    let mut pm = Pixmap::new_with_w_h(&cs, tw as i32, th as i32, false)
        .map_err(|e| format!("pixmap alloc {tw}x{th} failed: {e}"))?;
    // White background (opaque page). 255 fills every RGB channel = white.
    pm.clear_with(255).map_err(|e| format!("clear failed: {e}"))?;

    // Translate+scale so the region's top-left lands at buffer (0,0), then clip
    // to the buffer via the pixmap's IRect bounds.
    let ctm = Matrix::new(scale, 0.0, 0.0, scale, -x0, -y0);
    let clip = IRect::new(0, 0, tw as i32, th as i32);
    let dev = Device::from_pixmap_with_clip(&pm, clip)
        .map_err(|e| format!("draw device failed: {e}"))?;

    // 1:1 with Sumatra (RenderCache.cpp:1107 -> EngineMupdf.cpp:6542): pass a
    // finite scissor instead of INFINITE so fz_run_display_list can cull ops
    // whose device bbox misses the tile (list-device.c:1891: node rects are
    // transformed by top_ctm into DEVICE space, then intersected with the
    // scissor — the scissor does NOT get transformed by the ctm; Sumatra gets
    // away with a user-space rect only because its replay ctm is fz_identity).
    //
    // ⚠ Corrected after a native A/B test (examples/tile_check.rs): the first
    // attempt passed the tile's USER-space rect, which the runner read as a
    // DEVICE-space scissor — 4 of 5 probe tiles lost their image op entirely
    // (white tiles). The correct scissor is the tile-local DEVICE rect.
    //
    // The whole-page path (render_page_to_bgra) keeps INFINITE: it renders the
    // entire page anyway (culling saves nothing) and preserves its historical
    // behavior for content that bleeds outside the mediabox.
    //
    // ⚠ Regression watch (all-white tiles): if white tiles ever appear again,
    // widen this rect — do NOT go back to INFINITE.
    let area = if whole_page {
        Rect::new(f32::NEG_INFINITY, f32::NEG_INFINITY, f32::INFINITY, f32::INFINITY)
    } else {
        Rect::new(0.0, 0.0, tw as f32, th as f32)
    };
    // ⛔ 绝不把 abort 标志交给 MuPDF 的 display-list 循环（`dl.run_with_cookie`）。
    //
    // 根因（2026-09-13 定位；crash_native_av.log 已完整符号化复原调用栈）：
    //   MuPDF 在节点间隙看到 cookie->abort 时是 **`break`**，不是 throw
    //   （list-device.c:1757 `if (cookie->abort) break;`）。这次运行就可能在
    //   `begin_group`/clip 之后中断，把 fz_draw_device 留在**不平衡**状态
    //   （draw-device.c:3092 的判据 `dev->top > dev->resolve_spots`）。
    //   紧接着 mupdf crate 的 `Device::drop`（mupdf-0.8.0/src/device.rs:645）
    //   **裸调** fz_close_device —— 它外面没有任何 fz_try 帧 —— 于是
    //   fz_draw_close_device 抛 "items left on stack in draw device"，
    //   fz_close_device 的 `fz_catch { fz_rethrow }`（device.c:83）在无外层 try
    //   时落到 throw() 的 `exit(EXIT_FAILURE)` 分支（error.c:223）
    //   → **进程静默消失**：无异常码、无信号、任何处理器都接不到。
    //   这正是「右键拖拽缩放松手后闪退、appcrash 无记录」的真身。
    //
    // Sumatra 无恙的原因（EngineMupdf.cpp:6542-6563）：它自己持有 fz_context*，
    //   `fz_run_display_list` + `fz_close_device` 都在 fz_try 里，抛错被 fz_catch
    //   接住；MuPDF 官方同类 remedy 见 fitz/document.c:1008 —— 被 abort 之后直接
    //   `dev->close_device = NULL`（注释原话 "aborted run, don't warn about
    //   unclosed device"），close 于是退化为 no-op。
    //   mupdf crate 把 fz_context* / fz_device* / fz_display_list* 全部封成
    //   pub(crate)（已逐一核对 src/context.rs:62、device.rs:184-185、
    //   display_list.rs:12），我们**无法**在 crate 外复刻上述任一结构。
    //   因此选择**不把 abort 交给引擎**：只在 run 之前检查一次，在途的那一块
    //   瓦片跑完即弃。代价有界——瓦片渲染 ≤ 屏幕尺寸、图像解码走共享 fz_store，
    //   取代整页渲染后这点浪费可忽略；真正的风暴抑制由 TileRenderCache 的
    //   队列丢弃承担。
    let run_result = if cookie_aborted(cookie) {
        // 设备尚未 run（dev->top == 0），此处 close 不可能抛。
        drop(dev);
        return Err("aborted before display list run".into());
    } else {
        dl.run(&dev, &ctm, area)
    };
    let run_result = match run_result {
        Ok(()) => Ok(()),
        Err(e) => {
            if cookie_aborted(cookie) {
                Err("aborted during display list run".to_string())
            } else {
                Err(format!("run display list failed: {e}"))
            }
        }
    };
    // `dl.run` 已在内层 fz_try/fz_catch 里吞掉逐 op 错误（list-device.c:2109-2118
    // "Swallow the error"），正常结束时设备必然平衡，故下面的 drop(=close+free)
    // 不会抛 —— 这正是本函数现在能保证「进程不会因渲染而 exit(1)」的原因。
    run_result?;
    drop(dev);

    Ok(pm.samples().to_vec())
}