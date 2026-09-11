//! Vector direct-render command-stream exporter.
//!
//! Replays a page's `DisplayList` through a custom `NativeDevice` and serializes
//! every drawable as RESOLUTION-INDEPENDENT commands (paths in page user space,
//! images as bitmaps referenced by id). The C# side parses this once into
//! cached `CanvasGeometry` + `CanvasBitmap` and per frame only changes the view
//! matrix — so ANY zoom level re-rasterizes at exactly the display resolution,
//! no bitmap cache, no density ladder, no "wait for full-page re-raster".
//!
//! Text is converted to glyph outlines (paths) on export (same as MuPDF's own
//! draw device does via `fz_outline_glyph`), so the stream contains NO fonts —
//! the renderer never needs the embedded font, and glyphs stay vector-sharp at
//! every zoom.
//!
//! Command stream binary layout (little-endian):
//! ```text
//! u32 magic "GVDC" | u32 version=1 | f32 page_w_pt | f32 page_h_pt
//! u32 n_cmds | u32 n_images
//! commands...   (each: u8 tag + payload, see Tag enum)
//! images...     (each: u32 id, u32 w, u32 h, u32 data_len, BGRA bytes)
//! ```
//! A path payload is `u32 n_cmds` then per command `u8 type (0=move 1=line
//! 2=curve 3=close)` + coordinates (move/line: 2×f32; curve: 6×f32).
//!
//! Prototype phase degradations (counted in `SkippedTag` counts, surfaced to C#
//! so the hit rate is measurable): shades (gradients), soft masks, isolated
//! blend groups, tiles and stroked text are skipped. They are real PDF
//! features, but the point of this prototype is to verify the PERFORMANCE
//! proposition (geometry-cache + per-frame matrix redraw); the skipped
//! features are a completeness problem, not a feasibility one.

use std::cell::RefCell;
use std::num::NonZero;
use std::rc::Rc;

use mupdf::{
    ColorParams, Colorspace, Device, DisplayList, Image, Matrix, NativeDevice, Path, PathWalker,
    StrokeState, Text,
};

// ---- Command tags (mirrored in C#) ----
const TAG_FILL_PATH: u8 = 1;
const TAG_STROKE_PATH: u8 = 2;
const TAG_CLIP_PATH: u8 = 3;
const TAG_POP_CLIP: u8 = 4;
const TAG_FILL_IMAGE: u8 = 5;
// C# 端 VectorSceneBuilder 仍保留 TagFillImageMask(6) 兼容分支，命令流布局须镜像。
#[allow(dead_code)]
const TAG_FILL_IMAGE_MASK: u8 = 6;
const TAG_SKIP: u8 = 7;

const MAGIC: u32 = 0x4344_5647; // "GVDC" little-endian bytes
const VERSION: u32 = 1;

/// One image bitmap, id-referenced from commands.
pub struct ExportedImage {
    pub id: u32,
    pub w: u32,
    pub h: u32,
    pub bgra: Vec<u8>,
}

/// The exported page: command bytes + referenced image bitmaps.
pub struct PageCommands {
    pub commands: Vec<u8>,
    pub images: Vec<ExportedImage>,
    pub page_w_pt: f32,
    pub page_h_pt: f32,
    /// tag -> count of commands skipped (for C# to measure degradation rate)
    pub skipped: Vec<(u8, u32)>,
}

// ---------------------------------------------------------------------------
// Binary writer helpers
// ---------------------------------------------------------------------------

struct Writer(Vec<u8>);

impl Writer {
    fn new() -> Self {
        Writer(Vec::new())
    }
    fn u8(&mut self, v: u8) {
        self.0.push(v);
    }
    fn u32(&mut self, v: u32) {
        self.0.extend_from_slice(&v.to_le_bytes());
    }
    fn f32(&mut self, v: f32) {
        self.0.extend_from_slice(&v.to_le_bytes());
    }
    fn mat(&mut self, m: &Matrix) {
        self.f32(m.a);
        self.f32(m.b);
        self.f32(m.c);
        self.f32(m.d);
        self.f32(m.e);
        self.f32(m.f);
    }
    fn rgba(&mut self, r: f32, g: f32, b: f32, a: f32) {
        self.f32(r);
        self.f32(g);
        self.f32(b);
        self.f32(a);
    }
}

/// Convert a straight-alpha BGRA buffer to premultiplied (Win2D 的
/// B8G8R8A8UIntNormalized 语义 = premultiplied; 直通 alpha 在羽化半透明像素上会
/// 被错误加亮 → 边缘发白 halo/"白底"观感与过锐)。
fn premultiply_bgra(bgra: &mut [u8]) {
    for px in bgra.chunks_exact_mut(4) {
        let a = px[3] as u32;
        if a == 255 {
            continue;
        }
        px[0] = ((px[0] as u32 * a + 127) / 255) as u8;
        px[1] = ((px[1] as u32 * a + 127) / 255) as u8;
        px[2] = ((px[2] as u32 * a + 127) / 255) as u8;
    }
}

/// Serialize a MuPDF path as command stream path payload.
fn write_path(w: &mut Writer, path: &Path) {
    // First pass counts commands (cheap; typical paths are short).
    let mut counter = PathCounter(0);
    let _ = path.walk(&mut counter);
    w.u32(counter.0);
    let _ = path.walk(&mut PathPusher(w));
}

/// Serialize a MuPDF path with a device matrix applied to every vertex.
/// MuPDF passes un-transformed path (PDF user space) plus the op's ctm
/// (page y-flip + content transforms); the draw device applies ctm internally,
/// so to land in the same device space we must too. Ignoring ctm made page-
/// graphics (horizontal rules, rects) stay in user space -> upside-down on
/// y-up PDFs.
fn write_path_xf(w: &mut Writer, path: &Path, mat: &Matrix) {
    let mut counter = PathCounter(0);
    let _ = path.walk(&mut counter);
    w.u32(counter.0);
    let _ = path.walk(&mut PathPusherXf(w, mat.clone()));
}

struct PathPusherXf<'a>(&'a mut Writer, Matrix);

impl PathWalker for PathPusherXf<'_> {
    fn move_to(&mut self, x: f32, y: f32) {
        let p = &self.1 * mupdf::Point { x, y };
        self.0.u8(0);
        self.0.f32(p.x);
        self.0.f32(p.y);
    }
    fn line_to(&mut self, x: f32, y: f32) {
        let p = &self.1 * mupdf::Point { x, y };
        self.0.u8(1);
        self.0.f32(p.x);
        self.0.f32(p.y);
    }
    fn curve_to(&mut self, c1x: f32, c1y: f32, c2x: f32, c2y: f32, ex: f32, ey: f32) {
        let c1 = &self.1 * mupdf::Point { x: c1x, y: c1y };
        let c2 = &self.1 * mupdf::Point { x: c2x, y: c2y };
        let e = &self.1 * mupdf::Point { x: ex, y: ey };
        self.0.u8(2);
        self.0.f32(c1.x);
        self.0.f32(c1.y);
        self.0.f32(c2.x);
        self.0.f32(c2.y);
        self.0.f32(e.x);
        self.0.f32(e.y);
    }
    fn close(&mut self) {
        self.0.u8(3);
    }
}

struct PathCounter(u32);

impl PathWalker for PathCounter {
    fn move_to(&mut self, _: f32, _: f32) {
        self.0 += 1;
    }
    fn line_to(&mut self, _: f32, _: f32) {
        self.0 += 1;
    }
    fn curve_to(&mut self, _: f32, _: f32, _: f32, _: f32, _: f32, _: f32) {
        self.0 += 1;
    }
    fn close(&mut self) {
        self.0 += 1;
    }
}

struct PathPusher<'a>(&'a mut Writer);

impl PathWalker for PathPusher<'_> {
    fn move_to(&mut self, x: f32, y: f32) {
        self.0.u8(0);
        self.0.f32(x);
        self.0.f32(y);
    }
    fn line_to(&mut self, x: f32, y: f32) {
        self.0.u8(1);
        self.0.f32(x);
        self.0.f32(y);
    }
    fn curve_to(&mut self, c1x: f32, c1y: f32, c2x: f32, c2y: f32, ex: f32, ey: f32) {
        self.0.u8(2);
        self.0.f32(c1x);
        self.0.f32(c1y);
        self.0.f32(c2x);
        self.0.f32(c2y);
        self.0.f32(ex);
        self.0.f32(ey);
    }
    fn close(&mut self) {
        self.0.u8(3);
    }
}

/// Apply the image's SoftMask luminance to its alpha (paint the mask over the
/// main pixmap). MuPDF's draw device does this internally; `Image::to_pixmap`
/// alone does not. Without it, an image whose mask crops the content (JPEG
/// stamp on a black ground) renders as an opaque black rectangle.
fn apply_softmask(pm: &mupdf::Pixmap, w: u32, h: u32, img: &Image) -> Vec<u8> {
    // 主图转 BGRA（保留其自身 alpha，若为无 alpha 通道则全不透明）
    let mut bgra = pixmap_to_bgra(pm, w, h);
    // 蒙版：通常也是图像；取其亮度作 alpha。
    if let Some(mask_img) = img.mask() {
        if let Ok(mp) = mask_img.to_pixmap() {
            let mw = mp.width();
            let mh = mp.height();
            let ms = mp.samples();
            let mn = mp.n() as usize;
            let stride = mp.stride().max(0) as usize;
            if mw >= w / 2 && mh >= h / 2 {
                for y in 0..h as usize {
                    let my = (y * mh as usize) / h as usize;
                    let row = &ms[my * stride..my * stride + (mw as usize) * mn];
                    for (x, px) in bgra[y * (w as usize) * 4..(y + 1) * (w as usize) * 4].chunks_exact_mut(4).enumerate() {
                        let mx = (x * mw as usize) / w as usize;
                        let gray = row[mx * mn]; // 蒙版多为灰度/带灰度的首通道
                        // alpha = 主图自身 alpha × 蒙版亮度（luminance→alpha，黑=透）
                        let a = ((px[3] as f32) * (gray as f32) / 255.0) as u8;
                        px[3] = a;
                    }
                }
            }
        }
    }
    bgra
}

// Map MuPDF cap/join enums to Win2D-friendly values (done in Rust so the C#
// side needs no enum translation).
//   MuPDF LineCap: Butt=0 Round=1 Square=2
//   Win2D CanvasCapStyle: Flat=0 Square=1 Round=2
//   MuPDF LineJoin: Miter=0 Round=1 Bevel=2
//   Win2D CanvasLineJoin: Miter=0 Bevel=1 Round=2
fn cap_to_win2d(cap: mupdf::LineCap) -> u8 {
    match cap {
        mupdf::LineCap::Butt => 0,     // Flat
        mupdf::LineCap::Square => 1,   // Square
        mupdf::LineCap::Round => 2,    // Round
        mupdf::LineCap::Triangle => 2, // rare; approximate with Round
    }
}

fn join_to_win2d(join: mupdf::LineJoin) -> u8 {
    match join {
        mupdf::LineJoin::Miter => 0,
        mupdf::LineJoin::Bevel => 1,
        mupdf::LineJoin::Round => 2,
        mupdf::LineJoin::MiterXps => 0, // rare; approximate with Miter
    }
}

/// Convert a MuPDF colorspace + color to linear RGBA in [0,1]. Falls back to
/// black on any conversion failure.
fn color_to_rgba(cs: &Colorspace, color: &[f32], alpha: f32) -> (f32, f32, f32, f32) {
    let rgb = Colorspace::device_rgb();
    let converted = if cs.is_rgb() {
        Some(color.iter().take(3).copied().collect::<Vec<_>>())
    } else {
        cs.convert_color(color, &rgb, None, ColorParams::default())
            .ok()
    };
    match converted {
        Some(c) if c.len() >= 3 => (c[0].clamp(0.0, 1.0), c[1].clamp(0.0, 1.0), c[2].clamp(0.0, 1.0), alpha.clamp(0.0, 1.0)),
        _ => (0.0, 0.0, 0.0, alpha.clamp(0.0, 1.0)),
    }
}

/// Convert a MuPDF pixmap's samples to tight BGRA (row-major, stride honored).
/// Honors the pixmap colorspace: CMYK (n=4 with a cmyk colorspace) is converted
/// to RGB; gray (n=1/2) is replicated; RGB/RGBA (n=3/4) is channel-swapped.
/// `img.to_pixmap()` returns the image's NATIVE colorspace — a CMYK JPEG would
/// otherwise be mistaken for RGBA and come out color-inverted.
fn pixmap_to_bgra(pm: &mupdf::Pixmap, w: u32, h: u32) -> Vec<u8> {
    let n = pm.n() as usize;
    let stride = pm.stride().max(0) as usize;
    let samples = pm.samples();
    let is_cmyk = pm.color_space().map(|cs| cs.is_cmyk()).unwrap_or(false);
    let mut out = Vec::with_capacity((w as usize) * (h as usize) * 4);
    for y in 0..h as usize {
        let row = &samples[y * stride..y * stride + (w as usize) * n];
        for px in row.chunks_exact(n) {
            if is_cmyk && n >= 4 {
                // C=M=Y=0,K=0 -> white; 1-K scales the ink. Convert to sRGB.
                let c = px[0] as f32 / 255.0;
                let m = px[1] as f32 / 255.0;
                let y_ = px[2] as f32 / 255.0;
                let k = px[3] as f32 / 255.0;
                let r = ((1.0 - c) * (1.0 - k) * 255.0).clamp(0.0, 255.0) as u8;
                let g = ((1.0 - m) * (1.0 - k) * 255.0).clamp(0.0, 255.0) as u8;
                let b = ((1.0 - y_) * (1.0 - k) * 255.0).clamp(0.0, 255.0) as u8;
                out.push(b);
                out.push(g);
                out.push(r);
                out.push(255);
            } else {
                match n {
                    4 => {
                        // RGBA
                        out.push(px[2]); // B
                        out.push(px[1]); // G
                        out.push(px[0]); // R
                        out.push(px[3]);
                    }
                    3 => {
                        out.push(px[2]);
                        out.push(px[1]);
                        out.push(px[0]);
                        out.push(255);
                    }
                    2 => {
                        // Gray + alpha
                        out.push(px[0]);
                        out.push(px[0]);
                        out.push(px[0]);
                        out.push(px[1]);
                    }
                    _ => {
                        // 1 = gray; unknown channels default to gray-of-first
                        let v = px[0];
                        out.push(v);
                        out.push(v);
                        out.push(v);
                        out.push(255);
                    }
                }
            }
        }
    }
    out
}

/// Compute the layout matrix for a Text span item.
///
/// Mirrors MuPDF's draw-text: `tm = span->trm; tm.e = item->x; tm.f = item->y`
/// then `fz_outline_glyph(font, gid, tm)` produces the glyph outline in page
/// space, ready to fill.
fn span_item_matrix(span: &mupdf::text::TextSpan, x: f32, y: f32) -> Matrix {
    let mut tm = span.trm();
    tm.e = x;
    tm.f = y;
    tm
}

// ---------------------------------------------------------------------------
// The device
// ---------------------------------------------------------------------------

pub struct VectorExportDevice {
    w: Writer,
    images: Vec<ExportedImage>,
    skipped: Vec<(u8, u32)>,
    /// dedupe images by (w,h,samples-hash-lite): images are usually referenced
    /// once, but masks repeat; simple id counter avoids over-engineering.
    next_image_id: u32,
    cmd_count: u32,
    /// soft-mask 捕获深度：begin_mask..end_mask 之间的内容只是"透明度/形状源"，
    /// 不是最终画面 —— 若当普通绘制导出会画成黑色块（图章黑底真凶）。>0 时所有
    /// 绘制/剪裁回调一律跳过（含 clip/pop 配对、fill/stroke/image/text）。
    mask_depth: u32,
}

impl VectorExportDevice {
    pub fn new() -> Self {
        VectorExportDevice {
            w: Writer::new(),
            images: Vec::new(),
            skipped: Vec::new(),
            next_image_id: 0,
            cmd_count: 0,
            mask_depth: 0,
        }
    }

    /// 当前是否处于 soft-mask 捕获段（该段内容不导出）。
    fn in_mask(&self) -> bool {
        self.mask_depth > 0
    }

    /// Write a command tag: bumps the command counter (header nCmds) and emits
    /// the tag byte.
    fn tag(&mut self, t: u8) {
        self.cmd_count += 1;
        self.w.u8(t);
    }

    fn skip(&mut self, tag: u8) {
        if let Some(slot) = self.skipped.iter_mut().find(|(t, _)| *t == tag) {
            slot.1 += 1;
        } else {
            self.skipped.push((tag, 1));
        }
    }

    fn add_image(&mut self, img: &Image) -> u32 {
        let id = self.next_image_id;
        self.next_image_id += 1;
        // 若图像带 SoftMask（图章/抠图 PNG 常见）：蒙版亮度决定主图的 alpha。
        // mupdf 的 Image::to_pixmap 只解主图本身（不含 SMask 应用），需要手动合成，
        // 否则黑底（JPEG 图章常为黑底红章）会整块不透地画出来。
        let has_mask = img.mask().is_some();
        let pm = img.to_pixmap().ok();
        if let Some(pm) = pm {
            let w = pm.width();
            let h = pm.height();
            if w > 0 && h > 0 {
                let mut bgra = if has_mask {
                    // 与蒙版逐像素乘 alpha：主图混合 sRGB，蒙版亮度直接作 alpha。
                    apply_softmask(&pm, w, h, img)
                } else {
                    pixmap_to_bgra(&pm, w, h)
                };
                premultiply_bgra(&mut bgra);
                // 保持源分辨率：缩放全部交给 C# 端高质量插值（HighQualityCubic）。
                // 任何 CPU 预缩 + GPU 二次缩放都会双重平均 → 发糊（已实测回退）。
                self.images.push(ExportedImage { id, w, h, bgra });
                return id;
            }
        }
        // failed decode: emit 1x1 transparent so commands still reference a valid id
        self.images.push(ExportedImage {
            id,
            w: 1,
            h: 1,
            bgra: vec![0, 0, 0, 0],
        });
        id
    }

    fn fill_text_outlines(&mut self, text: &Text, ctm: Matrix, cs: &Colorspace, color: &[f32], alpha: f32) {
        let (r, g, b, a) = color_to_rgba(cs, color, alpha);
        // 字形最终位置 = tm（span 缩放 + item 相对坐标）再乘页面/行变换 ctm
        // （ctm 含页面 y 翻转与文本行起点平移，缺它封面缺翻转、正文丢行位）。顺序与
        // MuPDF draw device 一致：trm = fz_concat(tm, ctm)。
        for span in text.spans() {
            let font = span.font();
            for item in span.items() {
                let tm = span_item_matrix(&span, item.x(), item.y());
                let full = &tm * &ctm;
                if let Ok(Some(path)) = font.outline_glyph_with_ctm(item.gid(), &full) {
                    self.tag(TAG_FILL_PATH);
                    self.w.u8(0); // even_odd=false
                    self.w.rgba(r, g, b, a);
                    write_path(&mut self.w, &path);
                }
            }
        }
    }
}

impl NativeDevice for VectorExportDevice {
    fn fill_path(
        &mut self,
        path: &Path,
        even_odd: bool,
        ctm: Matrix,
        color_space: &Colorspace,
        color: &[f32],
        alpha: f32,
        _cp: ColorParams,
    ) {
        if self.in_mask() {
            return;
        }
        let (r, g, b, a) = color_to_rgba(color_space, color, alpha);
        self.tag(TAG_FILL_PATH);
        self.w.u8(if even_odd { 1 } else { 0 });
        self.w.rgba(r, g, b, a);
        write_path_xf(&mut self.w, path, &ctm);
    }

    fn stroke_path(
        &mut self,
        path: &Path,
        stroke_state: &StrokeState,
        ctm: Matrix,
        color_space: &Colorspace,
        color: &[f32],
        alpha: f32,
        _cp: ColorParams,
    ) {
        if self.in_mask() {
            return;
        }
        let (r, g, b, a) = color_to_rgba(color_space, color, alpha);
        self.tag(TAG_STROKE_PATH);
        self.w.u8(cap_to_win2d(stroke_state.start_cap()));
        self.w.u8(join_to_win2d(stroke_state.line_join()));
        self.w.f32(stroke_state.line_width());
        self.w.f32(stroke_state.miter_limit());
        let dashes = stroke_state.dashes();
        self.w.u32(dashes.len() as u32);
        for d in &dashes {
            self.w.f32(*d);
        }
        self.w.rgba(r, g, b, a);
        write_path_xf(&mut self.w, path, &ctm);
    }

    fn clip_path(&mut self, path: &Path, even_odd: bool, ctm: Matrix, _scissor: mupdf::Rect) {
        if self.in_mask() {
            return;
        }
        self.tag(TAG_CLIP_PATH);
        self.w.u8(if even_odd { 1 } else { 0 });
        write_path_xf(&mut self.w, path, &ctm);
    }

    fn pop_clip(&mut self) {
        if self.in_mask() {
            return;
        }
        self.tag(TAG_POP_CLIP);
    }

    fn fill_text(
        &mut self,
        text: &Text,
        ctm: Matrix,
        color_space: &Colorspace,
        color: &[f32],
        alpha: f32,
        _cp: ColorParams,
    ) {
        if self.in_mask() {
            return;
        }
        self.fill_text_outlines(text, ctm, color_space, color, alpha);
    }

    fn stroke_text(
        &mut self,
        text: &Text,
        stroke_state: &StrokeState,
        ctm: Matrix,
        color_space: &Colorspace,
        color: &[f32],
        alpha: f32,
        _cp: ColorParams,
    ) {
        if self.in_mask() {
            return;
        }
        // 描边文字：MuPDF draw 即对字形轮廓执行 stroke（fz_outline_glyph → stroke
        // path），这里用命令流 TAG_STROKE_PATH 输出同源字形轮廓+描边参数，C# 端
        // DrawGeometry(…) 描边 → 与 draw device 行为一致（加粗/空心描边文字不再丢失）。
        let (r, g, b, a) = color_to_rgba(color_space, color, alpha);
        for span in text.spans() {
            let font = span.font();
            for item in span.items() {
                let tm = span_item_matrix(&span, item.x(), item.y());
                let full = &tm * &ctm;
                if let Ok(Some(path)) = font.outline_glyph_with_ctm(item.gid(), &full) {
                    self.tag(TAG_STROKE_PATH);
                    self.w.u8(cap_to_win2d(stroke_state.start_cap()));
                    self.w.u8(join_to_win2d(stroke_state.line_join()));
                    self.w.f32(stroke_state.line_width());
                    self.w.f32(stroke_state.miter_limit());
                    let dashes = stroke_state.dashes();
                    self.w.u32(dashes.len() as u32);
                    for d in &dashes {
                        self.w.f32(*d);
                    }
                    self.w.rgba(r, g, b, a);
                    write_path(&mut self.w, &path);
                }
            }
        }
    }

    fn fill_image(&mut self, img: &Image, ctm: Matrix, alpha: f32, _cp: ColorParams) {
        if self.in_mask() {
            return;
        }
        let id = self.add_image(img);
        self.tag(TAG_FILL_IMAGE);
        self.w.mat(&ctm);
        self.w.u32(id);
        self.w.f32(alpha.clamp(0.0, 1.0));
    }

    fn fill_image_mask(
        &mut self,
        img: &Image,
        ctm: Matrix,
        color_space: &Colorspace,
        color: &[f32],
        alpha: f32,
        _cp: ColorParams,
    ) {
        if self.in_mask() {
            return;
        }
        // FillImageMask 语义 = 用图像的蒙版亮度作 alpha、以填充色着色（图章式抠图反白）。
        // 直接 DrawImage(蒙版原图) 会把"黑底白字"的蒙版整幅画成黑块/白字，正是图表标题
        // 反色（黑底白字）的根因。这里在 Rust 侧把蒙版转成「填充色 × alpha」的 BGRA 位图，
        // 以普通图像(tag5)导出 → C# 端 DrawImage 即正确着色。
        let (cr, cg, cb, _) = color_to_rgba(color_space, color, 1.0);
        let id = self.next_image_id;
        self.next_image_id += 1;
        let mut emitted = false;
        if let Ok(pm) = img.to_pixmap() {
            let (w, h) = (pm.width(), pm.height());
            if w > 0 && h > 0 {
                let stride = pm.stride().max(0) as usize;
                let samples = pm.samples();
                let n = pm.n() as usize;
                let mut bgra = Vec::with_capacity((w as usize) * (h as usize) * 4);
                // 蒙版字形是 1-bit 硬边位图。与普通图像（logo 等）同路径：保持源
                // 分辨率、填充色×alpha 直出，最终缩放由 C# 端 DrawImage 的
                // HighQualityCubic 一次完成（欧美克 logo 已验证该路径最佳）。
                // 实测结论：任何 CPU 预缩放（box 面积平均/双线性/膨胀）只要把位图
                // 压到低于显示所需分辨率，GPU 放大即糊；SUPER 调小更劣。直出 +
                // Cubic 是本机验证的最优组合，勿再预缩。
                let wa = w as usize;
                let ha = h as usize;
                for y in 0..ha {
                    let row = &samples[y * stride..y * stride + wa.min(stride)];
                    for (_x, px) in row.chunks_exact(n.min(row.len())).enumerate() {
                        let lum = if n >= 4 { px[3] as f32 / 255.0 } else { px[0] as f32 / 255.0 };
                        let a = (lum * alpha).clamp(0.0, 1.0);
                        bgra.push((cb * 255.0) as u8); // B
                        bgra.push((cg * 255.0) as u8); // G
                        bgra.push((cr * 255.0) as u8); // R
                        bgra.push((a * 255.0) as u8);
                    }
                }
                premultiply_bgra(&mut bgra);
                self.images.push(ExportedImage { id, w, h, bgra });
                emitted = true;
            }
        }
        if !emitted {
            self.images.push(ExportedImage { id, w: 1, h: 1, bgra: vec![0, 0, 0, 0] });
        }
        self.tag(TAG_FILL_IMAGE);
        self.w.mat(&ctm);
        self.w.u32(id);
        self.w.f32(1.0); // alpha 已并入着色位图
    }

    // ---- Prototype-phase degradations: count and skip ----
    fn fill_shade(&mut self, _shade: &mupdf::Shade, _cmt: Matrix, _alpha: f32, _cp: ColorParams) {
        self.skip(TAG_SKIP);
    }

    fn begin_mask(&mut self, _area: mupdf::Rect, _luminosity: bool, _cs: &Colorspace, _color: &[f32], _cp: ColorParams) {
        self.skip(TAG_SKIP);
        self.mask_depth = self.mask_depth.saturating_add(1);
    }

    fn end_mask(&mut self, _f: &mupdf::Function) {
        if self.mask_depth > 0 {
            self.mask_depth -= 1;
        }
    }

    fn begin_group(
        &mut self,
        _area: mupdf::Rect,
        _cs: &Colorspace,
        _isolated: bool,
        _knockout: bool,
        _blendmode: mupdf::BlendMode,
        _alpha: f32,
    ) {
        self.skip(TAG_SKIP);
    }

    fn begin_tile(
        &mut self,
        _area: mupdf::Rect,
        _view: mupdf::Rect,
        _x_step: f32,
        _y_step: f32,
        _ctm: Matrix,
        _id: Option<NonZero<i32>>,
        _doc_id: Option<NonZero<i32>>,
    ) -> Option<NonZero<i32>> {
        self.skip(TAG_SKIP);
        None
    }

    fn ignore_text(&mut self, _text: &Text, _cmt: Matrix) {}
}

/// Export `page`'s display list as resolution-independent commands + images.
pub fn export_page_commands(dl: &DisplayList, page_w_pt: f32, page_h_pt: f32) -> PageCommands {
    let device_state = Rc::new(RefCell::new(VectorExportDevice::new()));
    let dev = Device::from_native(device_state.clone()).expect("build vector export device");
    let area = dl.bounds();
    let _ = dl.run(&dev, &Matrix::IDENTITY, area);

    let mut inner = device_state.borrow_mut();
    let skipped = std::mem::take(&mut inner.skipped);
    let images = std::mem::take(&mut inner.images);

    let mut header = Writer::new();
    header.u32(MAGIC);
    header.u32(VERSION);
    header.f32(page_w_pt);
    header.f32(page_h_pt);
    header.u32(inner.cmd_count);
    header.u32(images.len() as u32);
    header.u32(skipped.len() as u32);

    // Assemble: header + skipped table + commands + images
    let mut commands = header.0;
    for (tag, count) in &skipped {
        commands.push(*tag);
        commands.extend_from_slice(&count.to_le_bytes());
    }
    commands.append(&mut inner.w.0);
    for img in &images {
        commands.extend_from_slice(&img.id.to_le_bytes());
        commands.extend_from_slice(&img.w.to_le_bytes());
        commands.extend_from_slice(&img.h.to_le_bytes());
        commands.extend_from_slice(&(img.bgra.len() as u32).to_le_bytes());
        commands.extend_from_slice(&img.bgra);
    }

    PageCommands {
        commands,
        images,
        page_w_pt,
        page_h_pt,
        skipped,
    }
}
