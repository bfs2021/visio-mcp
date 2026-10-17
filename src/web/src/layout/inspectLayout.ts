import type { Bounds, Element, Page, Point, Projection, Snapshot, TextBlock } from '../contracts/generated';
import { visualBounds } from '../commands/geometry';
import { parentMap } from '../commands/groups';
import { effectiveState } from '../canvas/cellMapping';

export type Severity = 'info' | 'warning' | 'error';

export interface LayoutIssue {
  code: string;
  severity: Severity;
  elementIds: string[];
  pageId: string;
  bounds: Bounds;
  message: string;
  evidence: Record<string, unknown>;
}

export interface LayoutContext {
  /** Width in points of `text` on one line in the given font. The caller decides how: a canvas in the page, a fixed table in tests. */
  measureWidth(text: string, font: { family: string; sizePt: number; bold: boolean; italic: boolean }): number;
  /** Natural pixel size of an asset if known; images are only checked for distortion when it is. */
  assetSize?(assetId: string): { width: number; height: number } | undefined;
  /** Resolved connector routes (derived, never canonical). Without them a connector is judged by the straight line between its ends. */
  projection?: Projection;
}

export interface LayoutReport { issues: LayoutIssue[]; notes: string[] }

const EPS = 0.01;
const LINE_HEIGHT = 1.2;
const isBox = (e: Element): e is Exclude<Element, { kind: 'connector' } | { kind: 'group' }> => e.kind !== 'connector' && e.kind !== 'group';

const intersect = (a: Bounds, b: Bounds): Bounds | null => {
  const x = Math.max(a.x, b.x), y = Math.max(a.y, b.y);
  const r = Math.min(a.x + a.width, b.x + b.width), t = Math.min(a.y + a.height, b.y + b.height);
  return r > x + EPS && t > y + EPS ? { x, y, width: r - x, height: t - y } : null;
};
const area = (b: Bounds) => b.width * b.height;
const union = (list: Bounds[]): Bounds => {
  const x = Math.min(...list.map(b => b.x)), y = Math.min(...list.map(b => b.y));
  return { x, y, width: Math.max(...list.map(b => b.x + b.width)) - x, height: Math.max(...list.map(b => b.y + b.height)) - y };
};

/** Does the segment pass through the interior of the rectangle (Liang-Barsky)? Touching the edge does not count. */
function segmentCrosses(a: Point, b: Point, r: Bounds): boolean {
  const dx = b.x - a.x, dy = b.y - a.y;
  let t0 = 0, t1 = 1;
  const clip = (p: number, q: number): boolean => {
    if (p === 0) return q > 0;
    const t = q / p;
    if (p < 0) { if (t > t1) return false; if (t > t0) t0 = t; } else { if (t < t0) return false; if (t < t1) t1 = t; }
    return true;
  };
  const inset = 0.5;
  const ok = clip(-dx, a.x - (r.x + inset)) && clip(dx, r.x + r.width - inset - a.x) && clip(-dy, a.y - (r.y + inset)) && clip(dy, r.y + r.height - inset - a.y);
  return ok && t1 - t0 > 1e-6;
}

// ---- text

function wrap(text: string, avail: number | null, font: { family: string; sizePt: number; bold: boolean; italic: boolean }, ctx: LayoutContext): { width: number; lines: number; longestWord: number } {
  let width = 0, lines = 0, longestWord = 0;
  for (const paragraph of text.split('\n')) {
    if (avail === null) { const w = ctx.measureWidth(paragraph, font); width = Math.max(width, w); lines++; continue; }
    let line = '';
    let any = false;
    for (const word of paragraph.split(/\s+/).filter(Boolean)) {
      const ww = ctx.measureWidth(word, font);
      longestWord = Math.max(longestWord, ww);
      const candidate = line ? `${line} ${word}` : word;
      if (line && ctx.measureWidth(candidate, font) > avail) { width = Math.max(width, ctx.measureWidth(line, font)); lines++; line = word; } else line = candidate;
      any = true;
    }
    width = Math.max(width, line ? ctx.measureWidth(line, font) : 0);
    lines += any || paragraph === '' ? 1 : 0;
  }
  return { width, lines: Math.max(lines, 1), longestWord };
}

const luminance = (hex: string): number => {
  const c = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16) / 255).map(v => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
  return 0.2126 * c[0]! + 0.7152 * c[1]! + 0.0722 * c[2]!;
};
export const contrastRatio = (a: string, b: string): number => {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi! + 0.05) / (lo! + 0.05);
};

/**
 * Machine-readable layout warnings for a committed snapshot. A pure read: nothing here touches the document, and measurements of text
 * are estimates (the report says so), so overflow findings carry the sizes they were judged by.
 */
export function inspectLayout(snapshot: Snapshot, ctx: LayoutContext, opts: { pageId?: string } = {}): LayoutReport {
  const issues: LayoutIssue[] = [];
  const notes = ['Text size is estimated from font metrics measured in the editor; a borderline overflow may render differently.'];
  if (!ctx.projection) notes.push('Connector routes were not available, so crossings are judged by straight lines between the connected elements.');
  if (!ctx.assetSize) notes.push('Image sizes were not available, so picture distortion was not checked.');

  for (const page of snapshot.document.pages) {
    if (opts.pageId && page.id !== opts.pageId) continue;
    inspectPage(page, ctx, issues);
  }
  issues.sort((a, b) => a.pageId.localeCompare(b.pageId) || a.code.localeCompare(b.code) || a.elementIds[0]!.localeCompare(b.elementIds[0]!));
  return { issues, notes };
}

function inspectPage(page: Page, ctx: LayoutContext, issues: LayoutIssue[]): void {
  const byId = new Map(page.elements.map(e => [e.id, e] as const));
  const parents = parentMap(page.elements);
  const shown = page.elements.filter(e => effectiveState(e, page.layers).visible);
  const add = (code: string, severity: Severity, ids: string[], bounds: Bounds, message: string, evidence: Record<string, unknown> = {}) =>
    issues.push({ code, severity, elementIds: ids, pageId: page.id, bounds, message, evidence });
  const pageBox: Bounds = { x: 0, y: 0, width: page.widthPt, height: page.heightPt };

  for (const el of shown) {
    if (el.kind === 'group') continue;
    const box = visualBounds(el);

    // outside the page
    const over = { left: Math.max(0, -box.x), top: Math.max(0, -box.y), right: Math.max(0, box.x + box.width - page.widthPt), bottom: Math.max(0, box.y + box.height - page.heightPt) };
    if (over.left > EPS || over.top > EPS || over.right > EPS || over.bottom > EPS) {
      const fully = !intersect(box, pageBox);
      add('OUTSIDE_PAGE', fully ? 'error' : 'warning', [el.id], box, fully ? 'The element is entirely outside the page.' : 'The element extends past the page edge.', { overhangPt: over, pageWidthPt: page.widthPt, pageHeightPt: page.heightPt });
    }

    if (isBox(el)) {
      if (el.bounds.width < 1 || el.bounds.height < 1) add('ZERO_SIZE', el.bounds.width < EPS || el.bounds.height < EPS ? 'error' : 'warning', [el.id], box, 'The element is zero or almost zero in size and will not be seen.', { widthPt: el.bounds.width, heightPt: el.bounds.height });

      const text = el.kind === 'shape' ? el.text : el.kind === 'text' ? el.text : undefined;
      if (text && text.value.trim()) textChecks(el, text, box, add, ctx, page);

      if (el.kind === 'image') {
        const nat = ctx.assetSize?.(el.assetId);
        if (nat && nat.width > 0 && nat.height > 0 && (el.fit === 'stretch' || !el.preserveAspectRatio)) {
          const want = nat.width / nat.height, got = el.bounds.width / el.bounds.height;
          if (Math.abs(got / want - 1) > 0.05) add('IMAGE_DISTORTION', 'warning', [el.id], box, 'The picture is stretched to a different aspect ratio than the original.', { naturalAspect: want, shownAspect: got });
        }
      }
    }

    if (el.kind === 'connector') connectorChecks(el, add, ctx, byId, parents, shown);
  }

  // hidden objects that sit on layers which are themselves visible: easy to forget, so report them quietly
  for (const el of page.elements) {
    if (el.hidden && el.kind !== 'group') add('HIDDEN_OBJECT', 'info', [el.id], visualBounds(el), 'The object is hidden although its layers are visible.', {});
  }

  // overlapping shapes and pictures: sweep along x
  const boxes = shown.filter(e => (e.kind === 'shape' || e.kind === 'image')).map(e => ({ e, b: visualBounds(e) })).sort((a, b) => a.b.x - b.b.x);
  for (let i = 0; i < boxes.length; i++) {
    for (let j = i + 1; j < boxes.length && boxes[j]!.b.x < boxes[i]!.b.x + boxes[i]!.b.width; j++) {
      const a = boxes[i]!, b = boxes[j]!;
      const hit = intersect(a.b, b.b);
      if (!hit) continue;
      const smaller = Math.min(area(a.b), area(b.b));
      if (hit.width * hit.height >= 0.98 * smaller) continue; // one sits inside the other: a container, not a collision
      if (hit.width * hit.height < 0.1 * smaller) continue;
      if (sharesGroup(a.e.id, b.e.id, parents)) continue;
      add('OVERLAP', 'warning', [a.e.id, b.e.id].sort(), hit, 'Two shapes overlap without one containing the other.', { overlapAreaPt2: Math.round(hit.width * hit.height), fractionOfSmaller: Math.round((hit.width * hit.height / smaller) * 100) / 100 });
    }
  }
}

function sharesGroup(a: string, b: string, parents: ReadonlyMap<string, string>): boolean {
  return parents.get(a) !== undefined && parents.get(a) === parents.get(b);
}

function textChecks(el: Exclude<Element, { kind: 'connector' } | { kind: 'group' }>, text: TextBlock, box: Bounds, add: (c: string, s: Severity, ids: string[], b: Bounds, m: string, e?: Record<string, unknown>) => void, ctx: LayoutContext, page: Page): void {
  const font = { family: text.fontFamily, sizePt: text.fontSizePt, bold: text.bold, italic: text.italic };
  const availW = Math.max(0, el.bounds.width - 2 * text.paddingPt), availH = Math.max(0, el.bounds.height - 2 * text.paddingPt);
  const m = wrap(text.value, text.wrap ? availW : null, font, ctx);
  const needH = m.lines * text.fontSizePt * LINE_HEIGHT;
  const tooWide = text.wrap ? m.longestWord > availW + 0.5 : m.width > availW + 0.5;
  const tooTall = needH > availH + 0.5;
  if (tooWide || tooTall) {
    add('TEXT_OVERFLOW', 'warning', [el.id], box, tooTall ? 'The text needs more height than the element has and will be clipped.' : 'The text is wider than the element.', {
      neededWidthPt: Math.round((text.wrap ? m.longestWord : m.width) * 10) / 10, availableWidthPt: Math.round(availW * 10) / 10,
      neededHeightPt: Math.round(needH * 10) / 10, availableHeightPt: Math.round(availH * 10) / 10, lines: m.lines, measured: 'estimated',
    });
  }
  // contrast against what is behind the text: the shape's own fill, or the page
  const back = el.kind === 'shape' ? (el.style.fill.kind === 'solid' && el.style.fill.opacity >= 0.5 ? el.style.fill.colour : el.style.fill.kind === 'none' ? undefined : undefined) : undefined;
  const bg = back ?? (page.background.kind === 'solid' ? page.background.colour : '#ffffff');
  const ratio = contrastRatio(text.colour, bg);
  if (ratio < 3) add('LOW_CONTRAST', 'warning', [el.id], box, 'The text colour is hard to read against its background.', { textColour: text.colour, backgroundColour: bg, contrastRatio: Math.round(ratio * 100) / 100, minimum: 3 });
}

function connectorChecks(
  el: Extract<Element, { kind: 'connector' }>,
  add: (c: string, s: Severity, ids: string[], b: Bounds, m: string, e?: Record<string, unknown>) => void,
  ctx: LayoutContext, byId: ReadonlyMap<string, Element>, parents: ReadonlyMap<string, string>, shown: Element[],
): void {
  const box = visualBounds(el);
  const ends: Array<{ side: 'from' | 'to'; ep: typeof el.from }> = [{ side: 'from', ep: el.from }, { side: 'to', ep: el.to }];
  for (const { side, ep } of ends) {
    if (ep.glue === 'none') add('UNGLUED_CONNECTOR_END', 'warning', [el.id], box, `The connector's ${side} end is not attached to a shape.`, { end: side, point: ep.point });
    else if (!ep.elementId || !byId.has(ep.elementId)) add('DANGLING_CONNECTOR', 'error', [el.id], box, `The connector's ${side} end points at an element that does not exist.`, { end: side, elementId: ep.elementId });
  }
  const centre = (id?: string): Point | undefined => { const t = id ? byId.get(id) : undefined; return t ? { x: t.bounds.x + t.bounds.width / 2, y: t.bounds.y + t.bounds.height / 2 } : undefined; };
  const route: Point[] = ctx.projection?.connectors[el.id]?.routePoints?.length
    ? ctx.projection.connectors[el.id]!.routePoints
    : [el.from.glue === 'none' ? el.from.point : centre(el.from.elementId), ...el.waypoints, el.to.glue === 'none' ? el.to.point : centre(el.to.elementId)].filter((p): p is Point => !!p);
  if (route.length < 2) return;
  const ancestorsOfEnds = new Set<string>();
  for (const id of [el.from.elementId, el.to.elementId]) for (let p = id ? parents.get(id) : undefined; p; p = parents.get(p)) ancestorsOfEnds.add(p);
  const crossed: string[] = [];
  for (const other of shown) {
    if (other.id === el.from.elementId || other.id === el.to.elementId || other.kind !== 'shape' && other.kind !== 'image') continue;
    if (ancestorsOfEnds.has(other.id)) continue;
    const b = visualBounds(other);
    for (let i = 0; i + 1 < route.length; i++) if (segmentCrosses(route[i]!, route[i + 1]!, b)) { crossed.push(other.id); break; }
  }
  if (crossed.length) add('CONNECTOR_CROSSES_SHAPE', 'warning', [el.id, ...crossed.sort()], union([box, ...crossed.map(id => visualBounds(byId.get(id)!))]), 'The connector passes through shapes it is not attached to.', { crossedElementIds: crossed.sort(), routeFrom: ctx.projection?.connectors[el.id] ? 'resolved route' : 'straight line between ends' });
}

export { union as unionBounds };
