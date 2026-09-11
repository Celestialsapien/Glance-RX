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
//   * hands out `Arc<DisplayList>` clones so any number of threads can render
//     DIFFERENT tiles of a page (or different pages) at the same time, each on
//     its own thread-local `fz_context`, with no global FFI lock.
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
    capacity: usize,
    path: String,
    password: Option<String>,
}

/// Single-process MuPDF engine. `&self` methods are thread-safe: rendering is
/// lock-free (shares `Arc<DisplayList>`), document access is serialized.
pub struct MupdfEngineImpl {
    inner: Mutex<MupdfState>,
}

// The engine is shared across C# thread-pool calls; the only shared objects are
// the mutex (safe) and `DisplayList` (Send+Sync), so these are sound.
unsafe impl Send for MupdfEngineImpl {}
unsafe impl Sync for MupdfEngineImpl {}

impl MupdfEngineImpl {
    /// Open `pdf_path` (optionally with a password) and build an in-process engine.
    ///
    /// `hot_capacity` bounds the parsed-page LRU (mirrors the C# sliding-window
    /// page count). `0` falls back to a sane default. A password-protected doc
    /// with no/wrong password yields `EngineOpenError::PasswordRequired`.
    pub fn new(pdf_path: &str, password: Option<&str>, hot_capacity: usize) -> Result<Self, EngineOpenError> {
        let capacity = if hot_capacity == 0 { 6 } else { hot_capacity };
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
                order: VecDeque::with_capacity(capacity),
                capacity,
                path: pdf_path.to_string(),
                password: password.map(str::to_string),
            }),
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

        // Evict LRU victim if at capacity.
        if st.cache.len() >= st.capacity {
            if let Some(victim) = st.order.pop_back() {
                st.cache.remove(&victim);
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
                            if st.cache.len() >= st.capacity {
                                if let Some(victim) = st.order.pop_back() {
                                    st.cache.remove(&victim);
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
        if width == 0 || height == 0 {
            return Err("invalid render size 0".into());
        }
        self.render_region_bgra(page, width, height, 0, 0, width, height)
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
        if x1 <= x0 || y1 <= y0 || x1 > dev_w || y1 > dev_h {
            return Err(format!("invalid tile rect {x0},{y0}-{x1},{y1} in {dev_w}x{dev_h}"));
        }
        self.render_region_bgra(page, dev_w, dev_h, x0, y0, x1, y1)
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
    ) -> Result<(Vec<u8>, u32, u32), String> {
        let parsed = self.get_parsed(page)?;
        let w_pt = parsed.w_pt;
        let h_pt = parsed.h_pt;
        if w_pt <= 0.0 || h_pt <= 0.0 {
            return Err("invalid page dimensions".into());
        }
        let dl = Arc::clone(&parsed.dl);

        let tw = (x1 - x0) as usize;
        let th = (y1 - y0) as usize;

        // Do the actual rasterization on the CURRENT thread's fz_context, with
        // NO global lock held — this is what makes concurrent tiling possible.
        let rgb = render_tile_rgb(&dl, w_pt, h_pt, dev_w, x0 as f32, y0 as f32, tw, th)?;

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

    /// Export a page's display list as SVG — a resolution-independent vector
    /// command stream (paths / text-as-paths / images / gradients in page user
    /// space, NO raster density baked in). This is the "vector direct render"
    /// prototype path: the C# side loads this once into a vector scene and
    /// redraws it per frame under an arbitrary view matrix, so any zoom level
    /// is re-rasterized at exactly the display resolution — no bitmap cache,
    /// no density ladder, no "wait for full-page re-raster".
    ///
    /// Thread-safety: same as tile rendering — clones the Arc<DisplayList> out
    /// of the engine and replays it on the caller's fz_context.
    pub fn export_page_svg(&self, page: u32) -> Result<String, String> {
        let parsed = self.get_parsed(page)?;
        let dl = Arc::clone(&parsed.dl);
        dl.to_svg(&Matrix::IDENTITY)
            .map_err(|e| format!("mupdf to_svg({page}) failed: {e}"))
    }

    /// Export a page's display list as a resolution-independent command stream
    /// (paths in page user space + images). See `vector_export` for the binary
    /// layout. This is the geometry-cache prototype path: the C# side parses it
    /// once into cached CanvasGeometry/CanvasBitmap and per frame only changes
    /// the view matrix — no per-frame SVG re-parse, no bitmap density ladder.
    pub fn export_page_commands(&self, page: u32) -> Result<crate::rendering::vector_export::PageCommands, String> {
        let parsed = self.get_parsed(page)?;
        let dl = Arc::clone(&parsed.dl);
        Ok(crate::rendering::vector_export::export_page_commands(&dl, parsed.w_pt, parsed.h_pt))
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

/// Rasterize one tile of a display list into an RGB row-major buffer of exactly
/// `tw`×`th`, mapping the page's device-pixel region (top-left at `x0`,`y0` of
/// the full page at density `dev_w`/w_pt) into buffer origin (0,0).
///
/// No global lock is taken here: the caller holds an `Arc<DisplayList>` so the
/// list cannot be freed while we run, and `fz_context` is per-thread.
fn render_tile_rgb(
    dl: &DisplayList,
    w_pt: f32,
    _h_pt: f32,
    dev_w: u32,
    x0: f32,
    y0: f32,
    tw: usize,
    th: usize,
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

    // IMPORTANT: the `area` passed as bbox hint to fz_run_display_list must be
    // the FULL page, NOT the tile's own region. A too-tight area culls drawing
    // ops by their *recorded* bbox; for clipped/grouped/shaded/image content the
    // recorded bbox can be inaccurate (media-box origin, rotated pages, nested
    // clips), so ops that would actually paint into this tile get dropped ->
    // all-white tiles. Passing ANY finite bbox still risks that for pages whose
    // content extends beyond the assumed (0,0)..(w_pt,h_pt) rect, so we use an
    // INFINITE area: fz replays every op with zero bbox filtering, and the
    // pixmap clip (0..tw, 0..th) does the real pixel culling. Cost is negligible
    // (later ops that don't touch the clip are still skipped by the device).
    let area = Rect::new(f32::NEG_INFINITY, f32::NEG_INFINITY, f32::INFINITY, f32::INFINITY);
    dl.run(&dev, &ctm, area).map_err(|e| format!("run display list failed: {e}"))?;
    drop(dev);

    Ok(pm.samples().to_vec())
}