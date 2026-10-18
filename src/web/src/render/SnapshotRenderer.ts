import type { Bounds, Element, Page, Projection, Snapshot } from '../contracts/generated';
import { unionBounds, visualBounds } from '../commands/geometry';
import { defaultScope, lookupTarget } from '../commands/resolveTarget';
import { MaxGraphAdapter } from '../canvas/MaxGraphAdapter';
import { effectiveState } from '../canvas/cellMapping';
import { fail, ok, type Result } from '../model/result';
import { parsePath } from '../geometry/svgPath';

export const MAX_PIXELS = 16_000_000;
export const MAX_BYTES = 16 * 1024 * 1024;
const MARGIN_PT = 40; // the canvas adapter's fixed offset between model and svg coordinates
const MAX_SCALE = 8;

export interface RenderParams {
  scope: 'page' | 'region';
  pageId?: string;
  format?: 'png' | 'jpeg' | 'svg';
  mode?: 'clean' | 'debug';
  maxWidth?: number;
  maxHeight?: number;
  bounds?: Bounds;
  elementIds?: string[];
  paddingPt?: number;
}

export interface RenderResult {
  mimeType: 'image/png' | 'image/jpeg' | 'image/svg+xml';
  /** Base64 of the encoded image. */
  base64: string;
  byteLength: number;
  widthPx: number;
  heightPx: number;
  /** The part of the page shown, in points. */
  cropPt: Bounds;
  pageId: string;
  mode: 'clean' | 'debug';
  format: 'png' | 'jpeg' | 'svg';
  /** The snapshot this image shows, not whatever the live document has become since. */
  documentId: string;
  sessionId: string;
  revision: number;
}

type Dims = { crop: Bounds; widthPx: number; heightPx: number; scale: number };

const finitePositive = (n: unknown, max = 100000): n is number => typeof n === 'number' && Number.isFinite(n) && n > 0 && n <= max;

/** Validates the request and works out what to crop and how big to make it. Pure, so it is tested without a browser. */
export function planRender(snapshot: Snapshot, projection: Projection | undefined, p: RenderParams): Result<{ page: Page; dims: Dims; format: 'png' | 'jpeg' | 'svg'; mode: 'clean' | 'debug' }> {
  const format = p.format ?? 'png', mode = p.mode ?? 'clean';
  if (format !== 'png' && format !== 'jpeg' && format !== 'svg') return fail('invalid_request', 'format must be png, jpeg or svg');
  if (mode !== 'clean' && mode !== 'debug') return fail('invalid_request', 'mode must be clean or debug');
  const maxWidth = p.maxWidth ?? 1600, maxHeight = p.maxHeight ?? 1200;
  if (!finitePositive(maxWidth, 8192) || !finitePositive(maxHeight, 8192)) return fail('invalid_request', 'maxWidth and maxHeight must be between 1 and 8192');
  const doc = snapshot.document;
  const pageId = p.pageId ?? defaultScope(doc);
  const page = doc.pages.find(x => x.id === pageId);
  if (!page) return fail(pageId ? 'not_found' : 'invalid_request', pageId ? `page ${pageId} does not exist` : 'the document has more than one page: say which with pageId');
  for (const el of page.elements) if (el.kind === 'shape' && el.geometry.svgPath !== undefined && effectiveState(el, page.layers).visible) {
    const parsed = parsePath(el.geometry.svgPath);
    if (!parsed.ok) return fail('invalid_request', parsed.error.message, { details: { elementId: el.id } });
  }

  let crop: Bounds;
  if (p.scope === 'page') {
    if (p.bounds || p.elementIds) return fail('invalid_request', 'a page render takes no bounds or elementIds');
    crop = { x: 0, y: 0, width: page.widthPt, height: page.heightPt };
  } else {
    const hasBounds = p.bounds !== undefined, hasIds = p.elementIds !== undefined;
    if (hasBounds === hasIds) return fail('invalid_request', 'a region render needs exactly one of bounds or elementIds');
    const padding = p.paddingPt ?? 12;
    if (!(typeof padding === 'number' && Number.isFinite(padding) && padding >= 0 && padding <= 2000)) return fail('invalid_request', 'paddingPt must be between 0 and 2000');
    let core: Bounds;
    if (hasBounds) {
      const b = p.bounds!;
      if (![b.x, b.y, b.width, b.height].every(Number.isFinite) || !(b.width > 0 && b.height > 0)) return fail('invalid_request', 'bounds needs finite x, y and a positive width and height');
      core = b;
    } else {
      if (!p.elementIds!.length || p.elementIds!.length > 500) return fail('invalid_request', 'elementIds must list 1 to 500 elements');
      const boxes: Bounds[] = [];
      for (const ref of p.elementIds!) {
        const hit = lookupTarget(doc.pages, ref, page.id);
        if (!hit.ok) return fail(hit.code, hit.message, { details: hit.details });
        const el = doc.pages[hit.pageIdx]!.elements[hit.index]!;
        if (doc.pages[hit.pageIdx]!.id !== page.id) return fail('invalid_request', `${ref} is on another page`);
        boxes.push(elementBox(el, page, projection));
      }
      core = unionBounds(boxes);
    }
    crop = { x: core.x - padding, y: core.y - padding, width: core.width + 2 * padding, height: core.height + 2 * padding };
  }
  const scale = Math.min(maxWidth / crop.width, maxHeight / crop.height, MAX_SCALE);
  const widthPx = Math.max(1, Math.round(crop.width * scale)), heightPx = Math.max(1, Math.round(crop.height * scale));
  if (widthPx * heightPx > MAX_PIXELS) return fail('limit_exceeded', `${widthPx} x ${heightPx} pixels is over the ${MAX_PIXELS / 1e6} megapixel limit`, { details: { widthPx, heightPx, limit: MAX_PIXELS } });
  return ok({ page, dims: { crop, widthPx, heightPx, scale: widthPx / crop.width }, format, mode });
}

/** The box that frames an element: a rotated shape's extent, a routed connector's resolved extent, a group's derived bounds. */
function elementBox(el: Element, page: Page, projection: Projection | undefined): Bounds {
  if (el.kind === 'connector') {
    const routed = projection?.connectors[el.id]?.visualBounds;
    return routed ?? visualBounds(el);
  }
  if (el.kind === 'group') {
    const routed = projection?.groupVisualBounds[el.id];
    return routed ?? el.bounds;
  }
  void page;
  return visualBounds(el);
}

const toBase64 = (bytes: Uint8Array): string => {
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
};

const blobToDataUrl = (blob: Blob): Promise<string> => new Promise((resolve, reject) => {
  const r = new FileReader();
  r.onload = () => resolve(String(r.result));
  r.onerror = () => reject(r.error);
  r.readAsDataURL(blob);
});

export interface RendererDeps {
  assetUrl: (snapshot: Snapshot, assetId: string) => string;
  /** Test seam; defaults to fetch. */
  fetchAsset?: (url: string) => Promise<Response>;
}

/**
 * Renders an immutable snapshot with the same style and geometry mapping the canvas uses, in a hidden canvas of its own. Selection
 * handles and editor chrome are never part of it, and the live drag or text-edit preview cannot leak in. Images are inlined from
 * their approved bytes so the raster is never tainted, and a missing asset or font that is not ready fails the render instead of
 * silently drawing a blank.
 */
export function createSnapshotRenderer(deps: RendererDeps) {
  let host: HTMLDivElement | null = null;
  let adapter: MaxGraphAdapter | null = null;
  let current: Snapshot | null = null;
  const fetchAsset = deps.fetchAsset ?? ((u: string) => fetch(u));

  const ensure = () => {
    if (adapter) return adapter;
    host = document.createElement('div');
    host.setAttribute('aria-hidden', 'true');
    host.style.cssText = 'position:fixed;left:-30000px;top:0;width:1600px;height:1200px;visibility:hidden;pointer-events:none;';
    document.body.appendChild(host);
    adapter = new MaxGraphAdapter(host, { onIntent() {}, onSelection() {}, onGesture() {}, onViewport() {}, onPlace() {} }, id => (current ? deps.assetUrl(current, id) : ''));
    adapter.setZoom(1);
    return adapter;
  };

  async function render(snapshot: Snapshot, projection: Projection | undefined, params: RenderParams): Promise<Result<RenderResult>> {
    const planned = planRender(snapshot, projection, params);
    if (!planned.ok) return planned;
    const { page, dims, format, mode } = planned.value;
    try {
      const a = ensure();
      current = snapshot;
      a.setZoom(1);
      a.project(snapshot, page.id);
      const svgRoot = (host!.querySelector('svg') as SVGSVGElement | null);
      if (!svgRoot) return fail('internal_error', 'the drawing surface produced no image');
      const svg = svgRoot.cloneNode(true) as SVGSVGElement;

      // images: inline approved bytes, and fail rather than draw nothing
      for (const img of Array.from(svg.querySelectorAll('image'))) {
        const href = img.getAttribute('href') ?? img.getAttribute('xlink:href') ?? '';
        if (!href || href.startsWith('data:')) { if (!href) return fail('not_found', 'a picture has no approved bytes to draw'); continue; }
        let res: Response;
        try { res = await fetchAsset(href); } catch { return fail('not_found', `the picture ${href.split('/').pop()?.slice(0, 12)} could not be loaded`); }
        if (!res.ok) return fail('not_found', `the picture ${href.split('/').pop()?.slice(0, 12)} is not available (${res.status})`);
        const data = await blobToDataUrl(await res.blob());
        img.setAttribute('href', data);
        img.removeAttribute('xlink:href');
      }

      svg.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
      svg.setAttribute('xmlns:xlink', 'http://www.w3.org/1999/xlink');
      svg.setAttribute('width', String(dims.widthPx));
      svg.setAttribute('height', String(dims.heightPx));
      svg.setAttribute('viewBox', `${dims.crop.x + MARGIN_PT} ${dims.crop.y + MARGIN_PT} ${dims.crop.width} ${dims.crop.height}`);
      svg.setAttribute('style', 'overflow:hidden;background:none');
      await document.fonts.ready;

      if (format === 'svg') {
        // a vector file: the page background, then the drawing, with the same inlined pictures
        const bgColour = page.background.kind === 'solid' ? page.background.colour : null;
        if (bgColour) {
          const rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
          for (const [k, v] of Object.entries({ x: String(dims.crop.x + MARGIN_PT), y: String(dims.crop.y + MARGIN_PT), width: String(dims.crop.width), height: String(dims.crop.height), fill: bgColour })) rect.setAttribute(k, v);
          svg.insertBefore(rect, svg.firstChild);
        }
        svg.setAttribute('style', '');
        const text = new XMLSerializer().serializeToString(svg);
        const bytes = new TextEncoder().encode(text);
        if (bytes.length > MAX_BYTES) return fail('limit_exceeded', `the drawing is ${bytes.length} bytes as SVG; the limit is ${MAX_BYTES}`);
        return ok({
          mimeType: 'image/svg+xml', base64: toBase64(bytes), byteLength: bytes.length, widthPx: dims.widthPx, heightPx: dims.heightPx, cropPt: dims.crop, pageId: page.id, mode,
          format: 'svg', documentId: snapshot.documentId, sessionId: snapshot.sessionId, revision: snapshot.revision,
        });
      }

      const url = URL.createObjectURL(new Blob([new XMLSerializer().serializeToString(svg)], { type: 'image/svg+xml;charset=utf-8' }));
      const image = new Image();
      try {
        image.src = url;
        await image.decode();
      } catch { return fail('internal_error', 'the drawing could not be turned into an image'); } finally { URL.revokeObjectURL(url); }

      const canvas = document.createElement('canvas');
      canvas.width = dims.widthPx;
      canvas.height = dims.heightPx;
      const g = canvas.getContext('2d')!;
      const bg = page.background.kind === 'solid' ? page.background.colour : format === 'jpeg' ? '#ffffff' : null;
      if (bg) { g.fillStyle = bg; g.fillRect(0, 0, canvas.width, canvas.height); }
      g.drawImage(image, 0, 0, dims.widthPx, dims.heightPx);
      if (mode === 'debug') drawDebug(g, snapshot, page, projection, dims);

      const blob: Blob | null = await new Promise(res => canvas.toBlob(res, format === 'jpeg' ? 'image/jpeg' : 'image/png', 0.9));
      if (!blob) return fail('internal_error', 'the image could not be encoded');
      if (blob.size > MAX_BYTES) return fail('limit_exceeded', `the image is ${blob.size} bytes; the limit is ${MAX_BYTES}`, { details: { bytes: blob.size, limit: MAX_BYTES } });
      const bytes = new Uint8Array(await blob.arrayBuffer());
      return ok({
        mimeType: format === 'jpeg' ? 'image/jpeg' : 'image/png', base64: toBase64(bytes), byteLength: bytes.length, widthPx: dims.widthPx, heightPx: dims.heightPx,
        cropPt: dims.crop, pageId: page.id, mode, format, documentId: snapshot.documentId, sessionId: snapshot.sessionId, revision: snapshot.revision,
      });
    } catch (e) {
      return fail('internal_error', e instanceof Error ? e.message : String(e));
    }
  }

  /** An asset at a bounded size as a PNG, drawn by the browser from the approved bytes (used when a format cannot hold an SVG). */
  async function rasterise(sha256: string, maxPixels: number): Promise<Result<{ base64: string; widthPx: number; heightPx: number }>> {
    try {
      const res = await fetchAsset(`/assets/${sha256}`);
      if (!res.ok) return fail('not_found', `the asset ${sha256.slice(0, 12)} is not available (${res.status})`);
      const url = URL.createObjectURL(await res.blob());
      const image = new Image();
      try { image.src = url; await image.decode(); } catch { return fail('internal_error', 'the picture could not be drawn'); } finally { URL.revokeObjectURL(url); }
      const w0 = image.naturalWidth || maxPixels, h0 = image.naturalHeight || maxPixels;
      const k = Math.min(1, maxPixels / Math.max(w0, h0)) * (w0 < 256 && h0 < 256 ? Math.min(4, 512 / Math.max(w0, h0)) : 1);
      const canvas = document.createElement('canvas');
      canvas.width = Math.max(1, Math.round(w0 * k)); canvas.height = Math.max(1, Math.round(h0 * k));
      canvas.getContext('2d')!.drawImage(image, 0, 0, canvas.width, canvas.height);
      const blob: Blob | null = await new Promise(r => canvas.toBlob(r, 'image/png'));
      if (!blob) return fail('internal_error', 'the picture could not be encoded');
      return ok({ base64: toBase64(new Uint8Array(await blob.arrayBuffer())), widthPx: canvas.width, heightPx: canvas.height });
    } catch (e) { return fail('internal_error', e instanceof Error ? e.message : String(e)); }
  }

  return { render, rasterise, dispose() { adapter?.dispose(); host?.remove(); adapter = null; host = null; } };
}

/** Small ids and bounds over the picture, so a model can match pixels to elements. Drawn after the image and never part of a clean render. */
function drawDebug(g: CanvasRenderingContext2D, snapshot: Snapshot, page: Page, projection: Projection | undefined, d: Dims): void {
  const palette = ['#e11d48', '#2563eb', '#16a34a', '#d97706', '#7c3aed', '#0891b2'];
  g.save();
  g.lineWidth = 1;
  g.font = '11px Consolas, monospace';
  g.textBaseline = 'top';
  let i = 0;
  for (const el of page.elements) {
    if (el.kind === 'group' || !effectiveState(el, page.layers).visible) continue;
    const b = elementBox(el, page, projection);
    const x = (b.x - d.crop.x) * d.scale, y = (b.y - d.crop.y) * d.scale, w = b.width * d.scale, h = b.height * d.scale;
    if (x > d.widthPx || y > d.heightPx || x + w < 0 || y + h < 0) continue;
    const colour = palette[i++ % palette.length]!;
    g.strokeStyle = colour; g.setLineDash([4, 3]); g.strokeRect(x, y, w, h);
    const label = el.alias ?? el.id.slice(0, 8);
    const tw = g.measureText(label).width + 6;
    g.setLineDash([]); g.fillStyle = colour; g.fillRect(x, Math.max(0, y - 13), tw, 13);
    g.fillStyle = '#ffffff'; g.fillText(label, x + 3, Math.max(0, y - 13) + 1);
  }
  g.restore();
  void snapshot;
}
