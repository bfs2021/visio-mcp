import type { Asset, Bounds, Element, Page, Snapshot } from '../contracts/generated';
import { defaultScope, lookupTarget } from '../commands/resolveTarget';
import { fail, ok, type Result } from '../model/result';

export interface SummaryOptions { pageId?: string; includeGeometry?: boolean; includeStyle?: boolean; includeHidden?: boolean }

export interface ElementSummary {
  id: string;
  kind: Element['kind'];
  alias?: string;
  name?: string;
  bounds?: Bounds;
  rotationDeg?: number;
  text?: string;
  hidden?: boolean;
  locked?: boolean;
  layerIds?: string[];
  zIndex?: number;
  style?: unknown;
  /** connector relationships */
  from?: unknown;
  to?: unknown;
  route?: string;
  /** image */
  assetId?: string;
  /** group */
  childIds?: string[];
}

const TEXT_LIMIT = 500;

/** One element as the semantic summary shows it. Geometry and style are opt-out and opt-in, as in the tool contract. */
export function summariseElement(el: Element, o: { includeGeometry: boolean; includeStyle: boolean }): ElementSummary {
  const s: ElementSummary = { id: el.id, kind: el.kind };
  if (el.alias !== undefined) s.alias = el.alias;
  if (el.name !== undefined) s.name = el.name;
  if (o.includeGeometry) { s.bounds = el.bounds; if (el.rotationDeg !== 0) s.rotationDeg = el.rotationDeg; }
  const text = el.kind === 'shape' || el.kind === 'text' ? el.text?.value : el.kind === 'connector' ? el.label?.value : undefined;
  if (text) s.text = text.length > TEXT_LIMIT ? `${text.slice(0, TEXT_LIMIT)}…` : text;
  if (el.hidden) s.hidden = true;
  if (el.locked) s.locked = true;
  if (el.layerIds.length) s.layerIds = el.layerIds;
  s.zIndex = el.zIndex;
  if (o.includeStyle && (el.kind === 'shape' || el.kind === 'connector')) s.style = el.style;
  if (el.kind === 'connector') { s.from = el.from; s.to = el.to; s.route = el.route; }
  if (el.kind === 'image') s.assetId = el.assetId;
  if (el.kind === 'group') s.childIds = el.childIds;
  return s;
}

const hiddenByLayer = (page: Page, el: Element): boolean =>
  el.layerIds.length > 0 && el.layerIds.every(id => page.layers.find(l => l.id === id)?.visible === false);

/** Compact semantic context for a model: pages, layers, elements with relationships, and the assets in use. A pure read of a snapshot. */
export function readSummary(snapshot: Snapshot, options: SummaryOptions = {}): Result<unknown> {
  const doc = snapshot.document;
  const pages = options.pageId ? doc.pages.filter(p => p.id === options.pageId) : doc.pages;
  if (options.pageId && pages.length === 0) return fail('not_found', `page ${options.pageId} does not exist`);
  const o = { includeGeometry: options.includeGeometry ?? true, includeStyle: options.includeStyle ?? false };
  const includeHidden = options.includeHidden ?? false;
  const used = new Set<string>();
  const outPages = pages.map(page => {
    const shown = page.elements.filter(e => includeHidden || (!e.hidden && !hiddenByLayer(page, e)));
    for (const e of shown) if (e.kind === 'image') used.add(e.assetId);
    return {
      id: page.id, name: page.name, widthPt: page.widthPt, heightPt: page.heightPt,
      layers: page.layers.map(l => ({ id: l.id, name: l.name, visible: l.visible, locked: l.locked, elementCount: page.elements.filter(e => e.layerIds.includes(l.id)).length })),
      elementCount: page.elements.length,
      omittedHidden: page.elements.length - shown.length,
      elements: shown.map(e => summariseElement(e, o)),
    };
  });
  return ok({
    documentId: snapshot.documentId, sessionId: snapshot.sessionId, revision: snapshot.revision, title: doc.title,
    pages: outPages, assetIds: [...used].sort(),
  });
}

/** Complete canonical objects. Each id is a UUID, or an alias/name on the only page; ambiguity is reported, never guessed. */
export function readObjects(snapshot: Snapshot, ids: readonly string[], pageId?: string): Result<Element[]> {
  const doc = snapshot.document;
  const scope = pageId ?? defaultScope(doc);
  const out: Element[] = [];
  for (const ref of ids) {
    const hit = lookupTarget(doc.pages, ref, scope);
    if (!hit.ok) return fail(hit.code, hit.message, { details: hit.details });
    out.push(doc.pages[hit.pageIdx]!.elements[hit.index]!);
  }
  return ok(out);
}

export interface AssetEntry extends Asset { references: number }

/** The document's catalogue, filtered by a case-insensitive text query over id and name and by tags (any match). */
export function listAssets(snapshot: Snapshot, query?: string, tags?: readonly string[]): AssetEntry[] {
  const counts = new Map<string, number>();
  for (const p of snapshot.document.pages) for (const e of p.elements) if (e.kind === 'image') counts.set(e.assetId, (counts.get(e.assetId) ?? 0) + 1);
  const q = query?.trim().toLowerCase();
  return snapshot.document.assets
    .filter(a => !q || a.id.toLowerCase().includes(q) || a.name.toLowerCase().includes(q))
    .filter(a => !tags?.length || tags.some(t => a.tags.includes(t)))
    .map(a => ({ ...a, references: counts.get(a.id) ?? 0 }));
}
