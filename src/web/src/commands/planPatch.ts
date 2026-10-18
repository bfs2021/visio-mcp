import type {
  Asset, Bounds, ConnectorElement, DiagramDocument, Element, Endpoint, EndpointInput, GroupElement, ImageElement, Layer, LineStyle,
  Operation, Page, Point, ShapeElement, ShapeStyle, TextBlock, TextElement,
} from '../contracts/generated';
import { canonicalSerialize } from '../model/canonical';
import { fail, ok, type Result } from '../model/result';
import { toPoints } from '../model/validate';
import { computeArrange, planZ, type ArrangeItem, type ArrangeOp } from './arrange';
import { emptyDiffBody, type DiffBody } from './diff';
import {
  centreOf, isRightAngle, mapConnectorPoints, rotatePoint, rotateRect, scaleRect, translateBounds, visualBounds,
  type PointMap,
} from './geometry';
import { ancestorsOf, groupBoundsFrom, leavesUnder } from './groups';
import { lookupTarget } from './resolveTarget';
import { parsePath } from '../geometry/svgPath';
import { isOutlinePreset, PRESET_PORTS } from '../geometry/presetOutlines';

export interface PlanContext {
  newId(): string;
  /** Page scope of the request; defaults to the only page of a one-page document. */
  pageId?: string;
}

export interface Plan {
  forward: DiffBody;
  inverse: DiffBody;
  created: string[];
  changed: string[];
  deleted: string[];
  changedPageIds: string[];
  changedLayerIds: string[];
  changedAssetIds: string[];
  visuallyAffectedIds: string[];
  description: string;
  noChange: boolean;
}

const DEFAULT_TEXT: TextBlock = {
  value: '', fontFamily: 'Segoe UI', fontSizePt: 12, bold: false, italic: false, underline: false, colour: '#000000',
  horizontalAlign: 'center', verticalAlign: 'middle', wrap: true, paddingPt: 4,
};
const DEFAULT_SHAPE_STYLE: ShapeStyle = {
  fill: { kind: 'solid', colour: '#ffffff', opacity: 1 }, stroke: { kind: 'solid', colour: '#333333', opacity: 1 },
  strokeWidthPt: 1, dash: 'solid', lineCap: 'butt', lineJoin: 'miter',
};
const DEFAULT_LINE_STYLE: LineStyle = { colour: '#334155', widthPt: 1, dash: 'solid', startArrow: 'none', endArrow: 'block' };
const SHAPE_ONLY_STYLE = ['fill', 'stroke', 'strokeWidthPt', 'lineCap', 'lineJoin'];
const LINE_ONLY_STYLE = ['colour', 'widthPt', 'startArrow', 'endArrow', 'startArrowSize', 'endArrowSize'];

type PatchObj = Record<string, unknown>;
type Hit = { page: Page; pageIdx: number; index: number; element: Element };
type RectElement = ShapeElement | TextElement | ImageElement;

class Failure extends Error {
  constructor(readonly result: Result<never>) { super('plan failure'); }
}
function stop(code: Parameters<typeof fail>[0], message: string, details?: Record<string, unknown>): never {
  throw new Failure(fail(code, message, details ? { details } : {}));
}
/** An invalid_request that names the rule broken, so callers can react without parsing the message. */
function refuse(reason: string, message: string, details: Record<string, unknown> = {}): never {
  return stop('invalid_request', message, { reason, ...details });
}

const merge = <T extends object>(base: T, patch: PatchObj): T => ({ ...base, ...(patch as Partial<T>) });
const plusZero = (n: number): number => n + 0; // turns -0 into 0, so right-angle maths never leaves a negative zero behind
const sameBounds = (a: Bounds, b: Bounds): boolean => a.x === b.x && a.y === b.y && a.width === b.width && a.height === b.height;

/** Plans a batch against `doc` without touching it. All-or-nothing: any failure returns an error and no plan. */
export function planPatch(doc: DiagramDocument, operations: Operation[], ctx: PlanContext): Result<Plan> {
  try {
    return ok(new Planner(doc, ctx).run(operations));
  } catch (e) {
    if (e instanceof Failure) return e.result;
    throw e;
  }
}

interface Transform {
  /** Applied to shapes, text and images. */
  rect(el: RectElement): RectElement;
  /** Applied to free connector endpoints and waypoints. */
  point: PointMap;
}

class Planner {
  private pages: Page[];
  private assets: Asset[];
  private readonly origElements = new Map<string, { pageId: string; index: number; element: Element }>();
  private readonly origAssets = new Map<string, { index: number; asset: Asset }>();
  /** child id to its group's id. Kept in step with every structural change so ancestors are always at hand. */
  private readonly parents = new Map<string, string>();
  /** Elements written by the operation in progress; their ancestor groups get fresh derived bounds afterwards. */
  private touched = new Set<string>();
  private readonly scopePageId: string | undefined;

  constructor(private readonly doc: DiagramDocument, private readonly ctx: PlanContext) {
    this.pages = doc.pages.slice();
    this.assets = doc.assets.slice();
    doc.assets.forEach((asset, index) => this.origAssets.set(asset.id, { index, asset }));
    for (const p of doc.pages) {
      p.elements.forEach((element, index) => {
        this.origElements.set(element.id, { pageId: p.id, index, element });
        if (element.kind === 'group') for (const c of element.childIds) this.parents.set(c, element.id);
      });
    }
    this.scopePageId = ctx.pageId ?? (doc.pages.length === 1 ? doc.pages[0]!.id : undefined);
    if (ctx.pageId && !doc.pages.some(p => p.id === ctx.pageId)) stop('not_found', `page ${ctx.pageId} does not exist`);
  }

  // ---------------------------------------------------------------- lookup helpers
  private pageIndex(pageId: string): number {
    const i = this.pages.findIndex(p => p.id === pageId);
    if (i < 0) stop('not_found', `page ${pageId} does not exist`);
    return i;
  }

  private findById(id: string): Hit | undefined {
    for (let pi = 0; pi < this.pages.length; pi++) {
      const index = this.pages[pi]!.elements.findIndex(e => e.id === id);
      if (index >= 0) return { page: this.pages[pi]!, pageIdx: pi, index, element: this.pages[pi]!.elements[index]! };
    }
    return undefined;
  }

  private store(pageIdx: number, index: number, element: Element): void {
    const page = this.pages[pageIdx]!;
    const elements = page.elements.slice();
    elements[index] = element;
    this.pages[pageIdx] = { ...page, elements };
    this.touched.add(element.id);
  }

  /** Replaces an element wherever it is, by id. */
  private put(element: Element): void {
    const hit = this.findById(element.id)!;
    this.store(hit.pageIdx, hit.index, element);
  }

  private byIdMap(pageIdx: number): Map<string, Element> {
    return new Map(this.pages[pageIdx]!.elements.map(e => [e.id, e] as const));
  }

  private defaultPage(pageId: string | undefined): number {
    const id = pageId ?? this.scopePageId;
    if (!id) return stop('invalid_request', 'this request needs a pageId: the document has more than one page');
    return this.pageIndex(id);
  }

  /** UUID first, then page-scoped alias, then exact display name. Ambiguous names are reported, never guessed. */
  private resolve(ref: string, pageId?: string): Hit {
    const r = lookupTarget(this.pages, ref, pageId ?? this.scopePageId);
    if (!r.ok) return stop(r.code, r.message, r.details);
    const page = this.pages[r.pageIdx]!;
    return { page, pageIdx: r.pageIdx, index: r.index, element: page.elements[r.index]! };
  }

  /** Resolves a list of targets that must sit on one page and may not include both a group and one of its descendants. */
  private resolveSet(targets: readonly string[]): { pageIdx: number; ids: string[] } {
    const hits = targets.map(t => this.resolve(t, this.ctx.pageId));
    const pageIdx = hits[0]!.pageIdx;
    if (hits.some(h => h.pageIdx !== pageIdx)) refuse('mixed_pages', 'the targets are not all on the same page');
    const ids = hits.map(h => h.element.id);
    if (new Set(ids).size !== ids.length) refuse('duplicate_target', 'the same element is targeted twice');
    const chosen = new Set(ids);
    for (const id of ids) {
      const above = ancestorsOf(this.parents, id).find(a => chosen.has(a));
      if (above) refuse('ancestor_and_descendant_selection', 'a group and one of its own members are both targeted', { group: above, member: id });
    }
    return { pageIdx, ids };
  }

  private layerLocked(page: Page, el: Element): boolean {
    return el.layerIds.some(id => page.layers.find(l => l.id === id)?.locked);
  }

  private assertEditable(page: Page, el: Element, what: string): void {
    if (el.locked || this.layerLocked(page, el)) stop('locked_target', `${el.id} is locked: ${what} refused`, { target: el.id });
    for (const a of ancestorsOf(this.parents, el.id)) {
      const g = page.elements.find(e => e.id === a);
      if (g && (g.locked || this.layerLocked(page, g))) stop('locked_target', `${el.id} belongs to locked group ${a}: ${what} refused`, { target: el.id, group: a });
    }
  }

  /** The element and everything beneath it must be free to change. */
  private assertSubtreeEditable(pageIdx: number, rootId: string, what: string): void {
    const page = this.pages[pageIdx]!;
    const byId = this.byIdMap(pageIdx);
    this.assertEditable(page, byId.get(rootId)!, what);
    for (const id of this.descendants(byId, rootId)) {
      const el = byId.get(id)!;
      if (el.locked || this.layerLocked(page, el)) stop('locked_target', `${id} is locked: ${what} refused`, { target: id });
    }
  }

  /** Every element under a group, nested groups included, not the root itself. */
  private descendants(byId: ReadonlyMap<string, Element>, rootId: string): string[] {
    const out: string[] = [];
    const seen = new Set<string>([rootId]);
    const walk = (id: string) => {
      const el = byId.get(id);
      if (el?.kind !== 'group') return;
      for (const c of el.childIds) if (!seen.has(c)) { seen.add(c); out.push(c); walk(c); }
    };
    walk(rootId);
    return out;
  }

  private assertFinite(n: number, what: string): void {
    if (!Number.isFinite(n)) stop('invalid_request', `${what} must be a finite number`);
  }

  private assertLayers(page: Page, layerIds: readonly string[]): void {
    for (const l of layerIds) if (!page.layers.some(x => x.id === l)) stop('invalid_request', `unknown layer ${l} on page ${page.name}`, { layerId: l });
  }

  private assertAlias(page: Page, alias: string, selfId: string | undefined): void {
    if (page.elements.some(e => e.alias === alias && e.id !== selfId)) stop('invalid_request', `alias "${alias}" is already used on page ${page.name}`, { alias });
  }

  private assertAsset(assetId: string): void {
    if (!this.assets.some(a => a.id === assetId)) stop('invalid_request', `unknown asset ${assetId}`, { assetId });
  }

  private assertPositive(b: Bounds, id: string): void {
    for (const [k, v] of Object.entries(b)) this.assertFinite(v, `bounds.${k}`);
    if (!(b.width > 0 && b.height > 0)) stop('invalid_request', `element ${id} must have a positive width and height`, { target: id });
  }

  // ---------------------------------------------------------------- endpoint handling
  private endpointPoint(ep: Endpoint | EndpointInput, resolvedId?: string): Point {
    if (ep.glue === 'none') return ep.point!;
    const hit = this.findById(resolvedId ?? (ep as Endpoint).elementId!)!;
    return centreOf(hit.element.bounds);
  }

  private resolveEndpoint(input: EndpointInput, pageId: string, selfId: string): Endpoint {
    if (input.glue === 'none') {
      if (!input.point || input.elementId !== undefined || input.port !== undefined) stop('invalid_request', 'a free endpoint needs a point and no elementId or port');
      const point = input.point;
      this.assertFinite(point.x, 'point.x'); this.assertFinite(point.y, 'point.y');
      return { point: { ...point }, glue: 'none' };
    }
    if (!input.elementId || input.point) stop('invalid_request', 'a glued endpoint needs an elementId and no point');
    const hit = this.resolve(input.elementId!, pageId);
    if (hit.page.id !== pageId) stop('invalid_request', 'a connector can only attach to elements on its own page');
    if (hit.element.kind === 'connector' || hit.element.id === selfId) stop('invalid_request', 'a connector cannot attach to a connector or to itself');
    const ep: Endpoint = { elementId: hit.element.id, glue: input.glue };
    if (input.port !== undefined) ep.port = input.port;
    return ep;
  }

  private connectorBounds(from: Endpoint, to: Endpoint, waypoints: readonly Point[]): Bounds {
    const pts = [this.endpointPoint(from), this.endpointPoint(to), ...waypoints];
    const xs = pts.map(p => p.x), ys = pts.map(p => p.y);
    const x = Math.min(...xs), y = Math.min(...ys);
    return { x, y, width: Math.max(...xs) - x, height: Math.max(...ys) - y };
  }

  // ---------------------------------------------------------------- batch
  run(operations: Operation[]): Plan {
    for (const [index, op] of operations.entries()) {
      this.touched = new Set();
      try {
        this.apply(op);
        this.refreshGroups();
      } catch (e) {
        // Say which operation failed, so a script can point at the line it came from.
        if (e instanceof Failure && !e.result.ok) e.result.error.details = { ...e.result.error.details, operationIndex: index };
        throw e;
      }
    }
    this.finalValidate();
    return this.buildPlan(operations);
  }

  private nextZ(page: Page): number {
    return page.elements.reduce((m, e) => Math.max(m, e.zIndex), 0) + 1;
  }

  private newElementId(requested: string | undefined): string {
    const id = requested ?? this.ctx.newId();
    if (this.findById(id) || this.origElements.has(id)) stop('invalid_request', `element id ${id} already exists`, { id });
    return id;
  }

  private addElement(pageIdx: number, el: Element): void {
    const page = this.pages[pageIdx]!;
    this.pages[pageIdx] = { ...page, elements: [...page.elements, el] };
    this.touched.add(el.id);
  }

  private removeElements(pageIdx: number, doomed: ReadonlySet<string>): void {
    const page = this.pages[pageIdx]!;
    this.pages[pageIdx] = { ...page, elements: page.elements.filter(e => !doomed.has(e.id)) };
    // A removed member leaves its group; the group is rewritten and so reported. An emptied group is refused at the end.
    for (const id of doomed) {
      const parent = this.parents.get(id);
      this.parents.delete(id);
      if (!parent || doomed.has(parent)) continue;
      const g = this.findById(parent)!.element as GroupElement;
      this.put({ ...g, childIds: g.childIds.filter(c => !doomed.has(c)) });
    }
  }

  /** Recomputes the derived bounds of every group above (or among) the elements the last operation wrote. */
  private refreshGroups(): void {
    const groups = new Set<string>();
    for (const id of this.touched) {
      const hit = this.findById(id);
      if (!hit) { for (const a of ancestorsOf(this.parents, id)) groups.add(a); continue; }
      if (hit.element.kind === 'group') groups.add(id);
      for (const a of ancestorsOf(this.parents, id)) groups.add(a);
    }
    for (const gid of groups) {
      const hit = this.findById(gid);
      if (!hit || hit.element.kind !== 'group' || hit.element.childIds.length === 0) continue;
      const bounds = groupBoundsFrom(this.byIdMap(hit.pageIdx), hit.element);
      if (!sameBounds(bounds, hit.element.bounds)) this.store(hit.pageIdx, hit.index, { ...hit.element, bounds });
    }
  }

  // ---------------------------------------------------------------- transforms of whole subtrees
  /** Applies `xf` to every leaf under `rootId` (the element itself when it is a leaf). Connector bounds are recomputed afterwards. */
  private transformSubtree(pageIdx: number, rootId: string, xf: Transform): void {
    const byId = this.byIdMap(pageIdx);
    const leaves = leavesUnder(byId, rootId).map(id => byId.get(id)!);
    for (const el of leaves) {
      if (el.kind === 'connector' || el.kind === 'group') continue;
      this.put(xf.rect(el));
    }
    for (const el of leaves) {
      if (el.kind !== 'connector') continue;
      const next = mapConnectorPoints(el, xf.point);
      next.bounds = this.connectorBounds(next.from, next.to, next.waypoints);
      this.put(next);
    }
  }

  private translateSubtree(pageIdx: number, rootId: string, dx: number, dy: number): void {
    this.transformSubtree(pageIdx, rootId, {
      rect: el => ({ ...el, bounds: translateBounds(el.bounds, dx, dy) }) as RectElement,
      point: p => ({ x: p.x + dx, y: p.y + dy }),
    });
  }

  private resizeGroup(pageIdx: number, g: GroupElement, patch: Partial<Bounds>): void {
    const gb = g.bounds;
    const nb: Bounds = { ...gb, ...patch };
    this.assertPositive(nb, g.id);
    if (!(gb.width > 0 && gb.height > 0)) refuse('degenerate_group', 'a group with no width or height cannot be scaled', { target: g.id });
    const sx = nb.width / gb.width, sy = nb.height / gb.height;
    const uniform = Math.abs(sx - sy) <= 1e-12 * Math.max(sx, sy);
    const byId = this.byIdMap(pageIdx);
    if (!uniform) {
      const skewed = leavesUnder(byId, g.id).map(id => byId.get(id)!).filter(e => e.kind !== 'connector' && e.kind !== 'group' && !isRightAngle(e.rotationDeg));
      if (skewed.length) refuse('nonuniform_group_scale', 'a group holding members turned by other than a right angle can only be scaled uniformly', { target: g.id, members: skewed.map(e => e.id) });
    }
    const shiftX = nb.x - gb.x, shiftY = nb.y - gb.y;
    this.transformSubtree(pageIdx, g.id, {
      rect: el => {
        const scaled = scaleRect(el.bounds, el.rotationDeg, { x: gb.x, y: gb.y }, sx, sy);
        return { ...el, bounds: translateBounds(scaled, shiftX, shiftY) } as RectElement;
      },
      point: p => ({ x: nb.x + (p.x - gb.x) * sx, y: nb.y + (p.y - gb.y) * sy }),
    });
  }

  private rotateGroup(pageIdx: number, g: GroupElement, deltaDeg: number, pivot: Point | undefined): void {
    const centre = pivot ?? centreOf(g.bounds);
    this.assertFinite(centre.x, 'pivot.xPt'); this.assertFinite(centre.y, 'pivot.yPt');
    this.transformSubtree(pageIdx, g.id, {
      rect: el => {
        const r = rotateRect(el.bounds, el.rotationDeg, centre, deltaDeg);
        return { ...el, bounds: { ...r.bounds, x: plusZero(r.bounds.x), y: plusZero(r.bounds.y) }, rotationDeg: plusZero(r.rotationDeg) } as RectElement;
      },
      point: p => { const q = rotatePoint(p, centre, deltaDeg); return { x: plusZero(q.x), y: plusZero(q.y) }; },
    });
  }

  /** Removes a group but keeps its members, which move up into the group's own parent if it has one. */
  private dissolveGroup(g: GroupElement): void {
    const parent = this.parents.get(g.id);
    const pageIdx = this.findById(g.id)!.pageIdx;
    for (const c of g.childIds) { if (parent) this.parents.set(c, parent); else this.parents.delete(c); }
    this.parents.delete(g.id);
    this.pages[pageIdx] = { ...this.pages[pageIdx]!, elements: this.pages[pageIdx]!.elements.filter(e => e.id !== g.id) };
    if (parent) {
      const p = this.findById(parent)!.element as GroupElement;
      this.put({ ...p, childIds: p.childIds.flatMap(c => (c === g.id ? g.childIds : [c])) });
    }
  }

  // ---------------------------------------------------------------- operations
  private apply(op: Operation): void {
    switch (op.op) {
      case 'createShape': {
        const pageIdx = this.defaultPage(op.pageId);
        const page = this.pages[pageIdx]!;
        const id = this.newElementId(op.id);
        if (op.alias) this.assertAlias(page, op.alias, undefined);
        const layerIds = op.layerIds ?? [];
        this.assertLayers(page, layerIds);
        if (op.style) for (const k of Object.keys(op.style)) if (LINE_ONLY_STYLE.includes(k)) stop('invalid_request', `style.${k} does not apply to a shape`);
        const el: ShapeElement = {
          id, kind: 'shape', layerIds: [...layerIds], zIndex: this.nextZ(page), locked: false, hidden: false,
          bounds: { ...op.bounds }, rotationDeg: op.rotationDeg ?? 0, metadata: {},
          geometry: merge({ preset: 'rectangle' } as ShapeElement['geometry'], (op.geometry ?? {}) as PatchObj),
          style: merge(DEFAULT_SHAPE_STYLE, (op.style ?? {}) as PatchObj),
        };
        if (op.alias) el.alias = op.alias;
        if (op.name !== undefined) el.name = op.name;
        if (op.text) el.text = merge(DEFAULT_TEXT, op.text as PatchObj);
        if (op.ports) el.ports = op.ports.map(p => ({ ...p }));
        else if (isOutlinePreset(el.geometry.preset)) el.ports = PRESET_PORTS[el.geometry.preset].map(p => ({ ...p })); // a flowchart symbol starts with its own connection points
        this.assertPositive(el.bounds, id);
        this.assertFinite(el.rotationDeg, 'rotationDeg');
        if (el.geometry.svgPath !== undefined) { const parsed = parsePath(el.geometry.svgPath); if (!parsed.ok) stop('invalid_request', parsed.error.message); }
        this.addElement(pageIdx, el);
        return;
      }
      case 'createText': {
        const pageIdx = this.defaultPage(op.pageId);
        const page = this.pages[pageIdx]!;
        const id = this.newElementId(op.id);
        if (op.alias) this.assertAlias(page, op.alias, undefined);
        const layerIds = op.layerIds ?? [];
        this.assertLayers(page, layerIds);
        const el: TextElement = {
          id, kind: 'text', layerIds: [...layerIds], zIndex: this.nextZ(page), locked: false, hidden: false,
          bounds: { ...op.bounds }, rotationDeg: op.rotationDeg ?? 0, metadata: {}, text: merge(DEFAULT_TEXT, op.text as PatchObj),
        };
        if (op.alias) el.alias = op.alias;
        if (op.name !== undefined) el.name = op.name;
        this.assertPositive(el.bounds, id);
        this.assertFinite(el.rotationDeg, 'rotationDeg');
        this.addElement(pageIdx, el);
        return;
      }
      case 'createImage': {
        const pageIdx = this.defaultPage(op.pageId);
        const page = this.pages[pageIdx]!;
        const id = this.newElementId(op.id);
        if (op.alias) this.assertAlias(page, op.alias, undefined);
        const layerIds = op.layerIds ?? [];
        this.assertLayers(page, layerIds);
        this.assertAsset(op.assetId);
        const el: ImageElement = {
          id, kind: 'image', layerIds: [...layerIds], zIndex: this.nextZ(page), locked: false, hidden: false,
          bounds: { ...op.bounds }, rotationDeg: op.rotationDeg ?? 0, metadata: {}, assetId: op.assetId,
          fit: op.fit ?? 'contain', opacity: op.opacity ?? 1, preserveAspectRatio: op.preserveAspectRatio ?? true,
        };
        if (op.alias) el.alias = op.alias;
        if (op.name !== undefined) el.name = op.name;
        this.assertPositive(el.bounds, id);
        this.assertFinite(el.rotationDeg, 'rotationDeg');
        this.addElement(pageIdx, el);
        return;
      }
      case 'createConnector': {
        const pageIdx = this.defaultPage(op.pageId);
        const page = this.pages[pageIdx]!;
        const id = this.newElementId(op.id);
        if (op.alias) this.assertAlias(page, op.alias, undefined);
        const layerIds = op.layerIds ?? [];
        this.assertLayers(page, layerIds);
        if (op.style) for (const k of Object.keys(op.style)) if (SHAPE_ONLY_STYLE.includes(k)) stop('invalid_request', `style.${k} does not apply to a connector`);
        const from = this.resolveEndpoint(op.from, page.id, id);
        const to = this.resolveEndpoint(op.to, page.id, id);
        const waypoints = (op.waypoints ?? []).map(p => ({ ...p }));
        for (const p of waypoints) { this.assertFinite(p.x, 'waypoint.x'); this.assertFinite(p.y, 'waypoint.y'); }
        const el: ConnectorElement = {
          id, kind: 'connector', layerIds: [...layerIds], zIndex: this.nextZ(page), locked: false, hidden: false,
          bounds: this.connectorBounds(from, to, waypoints), rotationDeg: 0, metadata: {}, from, to,
          route: op.route ?? 'straight', waypoints, style: merge(DEFAULT_LINE_STYLE, (op.style ?? {}) as PatchObj),
        };
        if (op.alias) el.alias = op.alias;
        if (op.name !== undefined) el.name = op.name;
        if (op.label) el.label = merge(DEFAULT_TEXT, op.label as PatchObj);
        this.addElement(pageIdx, el);
        return;
      }
      case 'set': return this.applySet(op.target, op.patch as PatchObj);
      case 'move': {
        const { page, pageIdx, index, element } = this.resolve(op.target, this.ctx.pageId);
        this.assertFinite(op.delta.xPt, 'delta.xPt'); this.assertFinite(op.delta.yPt, 'delta.yPt');
        const { xPt: dx, yPt: dy } = op.delta;
        if (element.kind === 'group') {
          this.assertSubtreeEditable(pageIdx, element.id, 'move');
          return this.translateSubtree(pageIdx, element.id, dx, dy);
        }
        this.assertEditable(page, element, 'move');
        if (element.kind === 'connector') {
          const freeFrom = element.from.glue === 'none', freeTo = element.to.glue === 'none';
          if (!freeFrom && !freeTo && element.waypoints.length === 0) return stop('invalid_request', 'a glued connector is positioned by its endpoints', { target: element.id });
          const shift = (ep: Endpoint): Endpoint => (ep.glue === 'none' ? { ...ep, point: { x: ep.point!.x + dx, y: ep.point!.y + dy } } : ep);
          const next: ConnectorElement = { ...element, from: shift(element.from), to: shift(element.to), waypoints: element.waypoints.map(p => ({ x: p.x + dx, y: p.y + dy })) };
          next.bounds = this.connectorBounds(next.from, next.to, next.waypoints);
          this.store(pageIdx, index, next);
          return;
        }
        this.store(pageIdx, index, { ...element, bounds: { ...element.bounds, x: element.bounds.x + dx, y: element.bounds.y + dy } } as Element);
        return;
      }
      case 'resize': {
        const { page, pageIdx, index, element } = this.resolve(op.target, this.ctx.pageId);
        if (element.kind === 'group') {
          this.assertSubtreeEditable(pageIdx, element.id, 'resize');
          return this.resizeGroup(pageIdx, element, op.bounds);
        }
        this.assertEditable(page, element, 'resize');
        if (element.kind === 'connector') return stop('invalid_request', 'connector bounds are derived, not resized', { target: element.id });
        const bounds = { ...element.bounds, ...op.bounds };
        this.assertPositive(bounds, element.id);
        this.store(pageIdx, index, { ...element, bounds } as Element);
        return;
      }
      case 'rotate': {
        const { page, pageIdx, index, element } = this.resolve(op.target, this.ctx.pageId);
        if (element.kind === 'connector') { this.assertEditable(page, element, 'rotate'); return stop('invalid_request', 'connectors have no rotation', { target: element.id }); }
        if (element.kind === 'group') {
          this.assertSubtreeEditable(pageIdx, element.id, 'rotate');
          if (op.angleDeg !== undefined) refuse('group_rotation_requires_delta', 'a group has no orientation of its own: rotate it by deltaDeg', { target: element.id });
          this.assertFinite(op.deltaDeg!, 'deltaDeg');
          return this.rotateGroup(pageIdx, element, op.deltaDeg!, op.pivot ? { x: op.pivot.xPt, y: op.pivot.yPt } : undefined);
        }
        this.assertEditable(page, element, 'rotate');
        if (op.pivot) return stop('invalid_request', 'pivot applies only to groups');
        const angle = op.angleDeg !== undefined ? op.angleDeg : element.rotationDeg + op.deltaDeg!;
        this.assertFinite(angle, 'rotation');
        this.store(pageIdx, index, { ...element, rotationDeg: angle } as Element);
        return;
      }
      case 'delete': return this.applyDelete(op);
      case 'setPage': {
        const idx = this.defaultPage(op.pageId);
        const page = this.pages[idx]!;
        const p = op.patch;
        const next: Page = { ...page };
        if (p.name !== undefined) next.name = p.name;
        if (p.widthPt !== undefined) next.widthPt = p.widthPt;
        if (p.heightPt !== undefined) next.heightPt = p.heightPt;
        if (p.background !== undefined) next.background = p.background;
        if (p.grid) next.grid = { ...page.grid, ...p.grid };
        this.assertFinite(next.widthPt, 'widthPt'); this.assertFinite(next.heightPt, 'heightPt');
        this.pages[idx] = next;
        return;
      }
      case 'registerAsset': {
        const existing = this.assets.find(a => a.id === op.asset.id);
        if (existing) {
          if (canonicalSerialize(existing) !== canonicalSerialize(op.asset)) stop('invalid_request', `asset ${op.asset.id} already exists with different content`, { assetId: op.asset.id });
          return;
        }
        this.assets = [...this.assets, { ...op.asset, tags: [...op.asset.tags] }];
        return;
      }
      case 'group': return this.applyGroup(op);
      case 'ungroup': {
        const hit = this.resolve(op.target, this.ctx.pageId);
        if (hit.element.kind !== 'group') refuse('not_a_group', `${hit.element.id} is not a group`, { target: hit.element.id });
        this.assertEditable(hit.page, hit.element, 'ungroup');
        return this.dissolveGroup(hit.element as GroupElement);
      }
      case 'duplicate': return this.applyDuplicate(op);
      case 'align': case 'distribute': case 'setGap': return this.applyArrange(op);
      case 'reorder': return this.applyReorder(op.targets, op.to);
      case 'addPage': return this.applyAddPage(op);
      case 'reorderPage': {
        const from = this.pageIndex(op.pageId);
        const pages = this.pages.slice();
        const [moved] = pages.splice(from, 1);
        pages.splice(Math.min(op.index, pages.length), 0, moved!);
        this.pages = pages;
        return;
      }
      case 'duplicatePage': return this.applyDuplicatePage(op);
      case 'deletePage': {
        const idx = this.pageIndex(op.pageId);
        for (const e of this.pages[idx]!.elements) this.parents.delete(e.id);
        this.pages = this.pages.filter((_, i) => i !== idx);
        return;
      }
      case 'addLayer': {
        const pageIdx = this.defaultPage(op.pageId);
        const page = this.pages[pageIdx]!;
        const layerId = op.id ?? this.ctx.newId();
        if (page.layers.some(l => l.id === layerId)) stop('invalid_request', `layer id ${layerId} already exists`, { id: layerId });
        const layer: Layer = {
          id: layerId, name: op.name, visible: op.visible ?? true, locked: op.locked ?? false, printable: op.printable ?? true,
          snap: op.snap ?? true, glue: op.glue ?? true,
        };
        if (op.colourOverride) layer.colourOverride = op.colourOverride;
        this.pages[pageIdx] = { ...page, layers: [...page.layers, layer] };
        return;
      }
      case 'setLayer': {
        for (let pi = 0; pi < this.pages.length; pi++) {
          const page = this.pages[pi]!;
          const at = page.layers.findIndex(l => l.id === op.layerId);
          if (at < 0) continue;
          this.pages[pi] = { ...page, layers: page.layers.map((l, i) => (i === at ? merge(l, op.patch as PatchObj) : l)) };
          return;
        }
        return stop('not_found', `no layer ${op.layerId}`, { layerId: op.layerId });
      }
      case 'assignLayers': return this.applyAssignLayers(op.targets, op.layerIds, op.mode ?? 'add');
      case 'deleteLayer': {
        for (let pi = 0; pi < this.pages.length; pi++) {
          const page = this.pages[pi]!;
          if (!page.layers.some(l => l.id === op.layerId)) continue;
          this.pages[pi] = { ...page, layers: page.layers.filter(l => l.id !== op.layerId) };
          page.elements.forEach(e => {
            if (!e.layerIds.includes(op.layerId)) return;
            this.put({ ...e, layerIds: e.layerIds.filter(l => l !== op.layerId) });
          });
          return;
        }
        return stop('not_found', `no layer ${op.layerId}`, { layerId: op.layerId });
      }
      case 'setAsset': {
        const at = this.assets.findIndex(a => a.id === op.assetId);
        if (at < 0) return stop('not_found', `no asset ${op.assetId}`, { assetId: op.assetId });
        const next = { ...this.assets[at]!, ...(op.patch as PatchObj) } as Asset;
        if (op.patch.tags) next.tags = [...op.patch.tags];
        this.assets = this.assets.map((a, i) => (i === at ? next : a));
        return;
      }
      case 'removeAsset': {
        if (!this.assets.some(a => a.id === op.assetId)) return stop('not_found', `no asset ${op.assetId}`, { assetId: op.assetId });
        const users = this.pages.flatMap(p => p.elements.filter((e): e is ImageElement => e.kind === 'image' && e.assetId === op.assetId));
        if (users.length) stop('dependency_conflict', `asset ${op.assetId} is still used by ${users.length} image(s): replace those references first`, { assetId: op.assetId, elementIds: users.map(u => u.id) });
        this.assets = this.assets.filter(a => a.id !== op.assetId);
        return;
      }
      case 'replaceAsset': {
        this.assertAsset(op.to);
        if (!this.assets.some(a => a.id === op.from)) stop('not_found', `no asset ${op.from}`, { assetId: op.from });
        const targets: Hit[] = op.targets
          ? op.targets.map(t => this.resolve(t, this.ctx.pageId))
          : this.pages.flatMap((page, pageIdx) => page.elements
            .map((element, index) => ({ page, pageIdx, index, element }))
            .filter(h => h.element.kind === 'image' && h.element.assetId === op.from));
        for (const h of targets) {
          if (h.element.kind !== 'image' || h.element.assetId !== op.from) refuse('not_a_reference', `${h.element.id} does not reference ${op.from}`, { target: h.element.id });
          this.assertEditable(h.page, h.element, 'replace its asset');
        }
        for (const h of targets) this.put({ ...(this.findById(h.element.id)!.element as ImageElement), assetId: op.to });
        return;
      }
    }
  }

  private applyDelete(op: Extract<Operation, { op: 'delete' }>): void {
    const hit = this.resolve(op.target, this.ctx.pageId);
    const el = hit.element;
    this.assertEditable(hit.page, el, 'delete');
    if (op.children !== undefined && el.kind !== 'group') refuse('children_only_for_groups', 'children applies only when the target is a group', { target: el.id });
    const doomed = new Set<string>([el.id]);
    if (el.kind === 'group') {
      if (!op.children) refuse('group_delete_requires_choice', 'deleting a group needs an explicit choice: children "delete" or "ungroup"', { target: el.id });
      if (op.children === 'ungroup') return this.dissolveGroup(el);
      this.assertSubtreeEditable(hit.pageIdx, el.id, 'delete');
      for (const d of this.descendants(this.byIdMap(hit.pageIdx), el.id)) doomed.add(d);
    }
    const page = this.pages[hit.pageIdx]!;
    const attached = (c: ConnectorElement) => doomed.has(c.from.elementId ?? '') || doomed.has(c.to.elementId ?? '');
    if (op.withConnectors) {
      for (const c of page.elements) {
        if (c.kind !== 'connector' || doomed.has(c.id) || !attached(c)) continue;
        this.assertEditable(page, c, 'delete');
        doomed.add(c.id);
      }
    }
    this.removeElements(hit.pageIdx, doomed);
  }

  private applyGroup(op: Extract<Operation, { op: 'group' }>): void {
    const { pageIdx, ids } = this.resolveSet(op.targets);
    for (const id of ids) {
      const parent = this.parents.get(id);
      if (parent) refuse('already_grouped', `${id} already belongs to group ${parent}: ungroup it first`, { target: id, group: parent });
    }
    const page = this.pages[pageIdx]!;
    const gid = this.newElementId(op.id);
    if (op.alias) this.assertAlias(page, op.alias, undefined);
    const byId = this.byIdMap(pageIdx);
    const z = Math.max(...ids.flatMap(i => leavesUnder(byId, i)).map(l => byId.get(l)!.zIndex));
    const g: GroupElement = {
      id: gid, kind: 'group', layerIds: [], zIndex: z, locked: false, hidden: false, bounds: { x: 0, y: 0, width: 0, height: 0 },
      rotationDeg: 0, metadata: {}, childIds: [...ids],
    };
    if (op.alias) g.alias = op.alias;
    if (op.name !== undefined) g.name = op.name;
    byId.set(gid, g);
    g.bounds = groupBoundsFrom(byId, g);
    this.addElement(pageIdx, g);
    for (const c of ids) this.parents.set(c, gid);
  }

  private applyArrange(op: ArrangeOp): void {
    const { pageIdx, ids } = this.resolveSet(op.targets);
    const items: ArrangeItem[] = ids.map((id, order) => {
      this.assertSubtreeEditable(pageIdx, id, op.op);
      const el = this.findById(id)!.element;
      if (el.kind === 'connector' && el.from.glue !== 'none' && el.to.glue !== 'none' && el.waypoints.length === 0) {
        refuse('connector_positioned_by_endpoints', 'a connector glued at both ends is positioned by the shapes it joins', { target: id });
      }
      return { id, box: visualBounds(el), order };
    });
    const shifts = computeArrange(items, op);
    if (!shifts.ok) throw new Failure(shifts);
    for (const i of items) {
      const s = shifts.value.get(i.id)!;
      if (s.dx !== 0 || s.dy !== 0) this.translateSubtree(pageIdx, i.id, s.dx, s.dy);
    }
  }

  private applyReorder(targets: readonly string[], to: 'front' | 'back' | 'forward' | 'backward'): void {
    const { pageIdx, ids } = this.resolveSet(targets);
    for (const id of ids) this.assertSubtreeEditable(pageIdx, id, 'reorder');
    const byId = this.byIdMap(pageIdx);
    const block = new Set(ids.flatMap(i => leavesUnder(byId, i)));
    const entries = this.pages[pageIdx]!.elements.filter(e => e.kind !== 'group').map((e, order) => ({ id: e.id, z: e.zIndex, order }));
    const changes = planZ(entries, block, to);
    for (const [id, z] of changes) this.put({ ...byId.get(id)!, zIndex: z });
    // a group stacks with its topmost member
    const after = this.byIdMap(pageIdx);
    const groups = new Set<string>();
    for (const id of changes.keys()) for (const a of ancestorsOf(this.parents, id)) groups.add(a);
    for (const gid of groups) {
      const g = after.get(gid) as GroupElement;
      const top = Math.max(...leavesUnder(after, gid).map(l => after.get(l)!.zIndex));
      if (top !== g.zIndex) this.put({ ...g, zIndex: top });
    }
  }

  private applyAssignLayers(targets: readonly string[], layerIds: readonly string[], mode: 'set' | 'add' | 'remove'): void {
    const { pageIdx, ids } = this.resolveSet(targets);
    this.assertLayers(this.pages[pageIdx]!, layerIds);
    for (const id of ids) {
      const hit = this.findById(id)!;
      this.assertEditable(hit.page, hit.element, 'change its layers');
      const current = hit.element.layerIds;
      const next = mode === 'set' ? [...layerIds]
        : mode === 'add' ? [...current, ...layerIds.filter(l => !current.includes(l))]
          : current.filter(l => !layerIds.includes(l));
      if (next.length === current.length && next.every((l, i) => l === current[i])) continue;
      this.put({ ...hit.element, layerIds: next });
    }
  }

  private applyDuplicate(op: Extract<Operation, { op: 'duplicate' }>): void {
    const { pageIdx, ids } = this.resolveSet(op.targets);
    if (op.as !== undefined && ids.length !== 1) refuse('alias_needs_single_target', 'as= names one copy, so duplicate exactly one target', { count: ids.length });
    const page = this.pages[pageIdx]!;
    if (op.as) this.assertAlias(page, op.as, undefined);
    const dx = op.offset?.xPt ?? 0, dy = op.offset?.yPt ?? 0;
    this.assertFinite(dx, 'offset.xPt'); this.assertFinite(dy, 'offset.yPt');
    const byId = this.byIdMap(pageIdx);

    // The set to copy: each target plus everything beneath a group, in the page's own order.
    const wanted = new Set<string>();
    for (const id of ids) { wanted.add(id); for (const d of this.descendants(byId, id)) wanted.add(d); }
    const source = page.elements.filter(e => wanted.has(e.id));
    if (op.id !== undefined && ids.length !== 1) refuse('id_needs_single_target', 'id= names one copy, so duplicate exactly one target', { count: ids.length });
    const idMap = new Map(source.map(e => [e.id, this.newElementId(e.id === ids[0] ? op.id : undefined)] as const));
    const shiftPoint: PointMap = p => ({ x: p.x + dx, y: p.y + dy });
    const remapEnd = (ep: Endpoint, connectorId: string): Endpoint => {
      if (ep.glue === 'none') return { ...ep, point: shiftPoint(ep.point!) };
      const copy = idMap.get(ep.elementId!);
      if (copy) return { ...ep, elementId: copy };
      if (!op.keepExternalLinks) refuse('connector_has_external_ends', `connector ${connectorId} is attached outside the copied set: include its elements or set keepExternalLinks`, { connectorId });
      return ep;
    };

    let z = this.nextZ(page);
    const ordered = source.slice().sort((a, b) => a.zIndex - b.zIndex);
    const zOf = new Map(ordered.filter(e => e.kind !== 'group').map(e => [e.id, z++] as const));
    const copies: Element[] = [];
    const taken = new Set(page.elements.map(e => e.alias).filter((a): a is string => !!a));
    if (op.as) taken.add(op.as);
    for (const e of source) {
      const copy: Record<string, unknown> = { ...e, id: idMap.get(e.id)!, layerIds: [...e.layerIds], metadata: { ...e.metadata } };
      delete copy.alias;
      if (op.as && e.id === ids[0]) copy.alias = op.as;
      else if (op.keepAliases && e.alias) { copy.alias = uniqueAlias(e.alias, taken); taken.add(copy.alias as string); }
      if (e.kind === 'group') {
        copy.childIds = e.childIds.map(c => idMap.get(c)!);
        copy.zIndex = Math.max(...leavesUnder(byId, e.id).map(l => zOf.get(l)!));
        copy.bounds = translateBounds(e.bounds, dx, dy);
      } else {
        copy.zIndex = zOf.get(e.id)!;
        copy.bounds = translateBounds(e.bounds, dx, dy);
        if (e.kind === 'connector') {
          copy.from = remapEnd(e.from, e.id);
          copy.to = remapEnd(e.to, e.id);
          copy.waypoints = e.waypoints.map(shiftPoint);
        }
      }
      copies.push(copy as unknown as Element);
    }
    for (const c of copies) {
      this.addElement(pageIdx, c);
      if (c.kind === 'group') for (const child of c.childIds) this.parents.set(child, c.id);
    }
  }

  private applyAddPage(op: Extract<Operation, { op: 'addPage' }>): void {
    const id = op.id ?? this.ctx.newId();
    if (this.pages.some(p => p.id === id)) stop('invalid_request', `page id ${id} already exists`, { id });
    const page: Page = {
      id, name: op.name, widthPt: op.widthPt ?? toPoints(297, 'mm'), heightPt: op.heightPt ?? toPoints(210, 'mm'),
      background: { kind: 'solid', colour: '#ffffff', opacity: 1 }, grid: { visible: true, spacingPt: toPoints(5, 'mm'), snap: true }, guides: [],
      layers: [{ id: this.ctx.newId(), name: 'Layer 1', visible: true, locked: false, printable: true, snap: true, glue: true }],
      elements: [],
    };
    const pages = this.pages.slice();
    pages.splice(Math.min(op.index ?? pages.length, pages.length), 0, page);
    this.pages = pages;
  }

  private applyDuplicatePage(op: Extract<Operation, { op: 'duplicatePage' }>): void {
    const at = this.pageIndex(op.pageId);
    const src = this.pages[at]!;
    const id = op.id ?? this.ctx.newId();
    if (this.pages.some(p => p.id === id)) stop('invalid_request', `page id ${id} already exists`, { id });
    const layerMap = new Map(src.layers.map(l => [l.id, this.ctx.newId()] as const));
    const idMap = new Map(src.elements.map(e => [e.id, this.newElementId(undefined)] as const));
    const end = (ep: Endpoint): Endpoint => (ep.glue === 'none' ? ep : { ...ep, elementId: idMap.get(ep.elementId!)! });
    const elements = src.elements.map(e => {
      const copy: Record<string, unknown> = { ...e, id: idMap.get(e.id)!, layerIds: e.layerIds.map(l => layerMap.get(l)!) };
      if (e.kind === 'group') copy.childIds = e.childIds.map(c => idMap.get(c)!);
      if (e.kind === 'connector') { copy.from = end(e.from); copy.to = end(e.to); }
      return copy as unknown as Element;
    });
    const page: Page = {
      ...src, id, name: op.name ?? `${src.name} copy`, guides: src.guides.map(g => ({ ...g, id: this.ctx.newId() })),
      layers: src.layers.map(l => ({ ...l, id: layerMap.get(l.id)! })), elements,
    };
    for (const e of elements) if (e.kind === 'group') for (const c of e.childIds) this.parents.set(c, e.id);
    const pages = this.pages.slice();
    pages.splice(at + 1, 0, page);
    this.pages = pages;
  }

  private applySet(target: string, patch: PatchObj): void {
    const { page, pageIdx, index, element } = this.resolve(target, this.ctx.pageId);
    const keys = Object.keys(patch);
    if (!(keys.length === 1 && keys[0] === 'locked')) this.assertEditable(page, element, 'edit');
    const next: Record<string, unknown> = { ...element };
    const need = (cond: boolean, key: string) => { if (!cond) stop('invalid_request', `patch.${key} does not apply to a ${element.kind}`, { target: element.id, key }); };

    for (const [key, value] of Object.entries(patch)) {
      switch (key) {
        case 'name':
          if (value === null) delete next.name; else next.name = value;
          break;
        case 'alias':
          if (value === null) delete next.alias;
          else { this.assertAlias(page, value as string, element.id); next.alias = value; }
          break;
        case 'locked': next.locked = value; break;
        case 'hidden': next.hidden = value; break;
        case 'layerIds': this.assertLayers(page, value as string[]); next.layerIds = [...(value as string[])]; break;
        case 'metadata': next.metadata = { ...element.metadata, ...(value as Record<string, string>) }; break;
        case 'style': {
          need(element.kind === 'shape' || element.kind === 'connector', key);
          const forbidden = element.kind === 'shape' ? LINE_ONLY_STYLE : SHAPE_ONLY_STYLE;
          for (const k of Object.keys(value as object)) if (forbidden.includes(k)) stop('invalid_request', `style.${k} does not apply to a ${element.kind}`, { target: element.id });
          next.style = merge((element as ShapeElement | ConnectorElement).style as object, value as PatchObj);
          break;
        }
        case 'text': {
          need(element.kind === 'shape' || element.kind === 'text' || element.kind === 'connector', key);
          if (element.kind === 'connector') { next.label = merge(element.label ?? DEFAULT_TEXT, value as PatchObj); break; } // a connector's text is its label
          const base = (element as ShapeElement | TextElement).text ?? DEFAULT_TEXT;
          next.text = merge(base, value as PatchObj);
          break;
        }
        case 'geometry': {
          need(element.kind === 'shape', key); next.geometry = merge((element as ShapeElement).geometry, value as PatchObj);
          const path = (next.geometry as ShapeElement['geometry']).svgPath;
          if (path !== undefined) { const parsed = parsePath(path); if (!parsed.ok) stop('invalid_request', parsed.error.message); }
          break;
        }
        case 'ports': need(element.kind === 'shape', key); next.ports = (value as ShapeElement['ports'])!.map(p => ({ ...p })); break;
        case 'assetId': need(element.kind === 'image', key); this.assertAsset(value as string); next.assetId = value; break;
        case 'fit': case 'opacity': case 'preserveAspectRatio': need(element.kind === 'image', key); next[key] = value; break;
        case 'route': need(element.kind === 'connector', key); next.route = value; break;
        case 'waypoints': need(element.kind === 'connector', key); next.waypoints = (value as Point[]).map(p => ({ ...p })); break;
        case 'from': case 'to': {
          need(element.kind === 'connector', key);
          const ep = value as Endpoint;
          next[key] = this.resolveEndpoint(ep as EndpointInput, page.id, element.id);
          break;
        }
        default: stop('invalid_request', `unknown patch field ${key}`);
      }
    }
    if (element.kind === 'connector' && ('from' in patch || 'to' in patch || 'waypoints' in patch)) {
      const c = next as unknown as ConnectorElement;
      next.bounds = this.connectorBounds(c.from, c.to, c.waypoints);
    }
    this.store(pageIdx, index, next as unknown as Element);
  }

  // ---------------------------------------------------------------- validation and diffing
  private finalValidate(): void {
    if (this.pages.length === 0) refuse('last_page', 'a document keeps at least one page');
    for (const page of this.pages) {
      const ids = new Set(page.elements.map(e => e.id));
      const claimed = new Map<string, string>();
      for (const el of page.elements) {
        if (el.kind === 'connector') {
          const missing = [el.from, el.to].filter(ep => ep.glue !== 'none' && !ids.has(ep.elementId!));
          if (missing.length) stop('dependency_conflict', `connector ${el.id} would lose an endpoint`, { connectorIds: [el.id], elementIds: missing.map(m => m.elementId) });
        }
        if (el.kind === 'group') {
          if (el.childIds.length === 0) refuse('empty_group', `group ${el.id} would be left with no members: ungroup or delete it explicitly`, { groupId: el.id });
          const missing = el.childIds.filter(c => !ids.has(c));
          if (missing.length) stop('dependency_conflict', `group ${el.id} would lose a child`, { groupIds: [el.id], elementIds: missing });
          for (const c of el.childIds) {
            if (claimed.has(c)) refuse('already_grouped', `${c} is a member of two groups`, { target: c });
            claimed.set(c, el.id);
          }
        }
        if (el.kind === 'image' && !this.assets.some(a => a.id === el.assetId)) stop('invalid_request', `image ${el.id} references unknown asset ${el.assetId}`);
      }
      for (const id of claimed.keys()) {
        const seen = new Set<string>();
        for (let p = claimed.get(id); p !== undefined; p = claimed.get(p)) {
          if (seen.has(p) || p === id) refuse('group_cycle', 'groups cannot contain themselves', { target: id });
          seen.add(p);
        }
      }
    }
  }

  private buildPlan(operations: Operation[]): Plan {
    const forward = emptyDiffBody();
    const inverse = emptyDiffBody();
    const created: string[] = [], changed: string[] = [], deleted: string[] = [];
    const finalById = new Map<string, { pageId: string; index: number; element: Element }>();
    for (const p of this.pages) p.elements.forEach((element, index) => finalById.set(element.id, { pageId: p.id, index, element }));

    for (const [id, f] of finalById) {
      const o = this.origElements.get(id);
      if (!o) { created.push(id); continue; }
      if (o.element !== f.element && canonicalSerialize(o.element) !== canonicalSerialize(f.element)) changed.push(id);
    }
    for (const id of this.origElements.keys()) if (!finalById.has(id)) deleted.push(id);

    // ---- pages: created, deleted, reordered, edited
    const origPageIds = this.doc.pages.map(p => p.id);
    const finalPageIds = this.pages.map(p => p.id);
    const createdPages = finalPageIds.filter(p => !origPageIds.includes(p));
    const deletedPages = origPageIds.filter(p => !finalPageIds.includes(p));
    const survivingOrder = (ids: string[], other: string[]) => ids.filter(i => other.includes(i)).join('|');
    const reordered = survivingOrder(origPageIds, finalPageIds) !== survivingOrder(finalPageIds, origPageIds);
    const structural = createdPages.length > 0 || deletedPages.length > 0 || reordered;
    const meta = (p: Page) => ({ id: p.id, name: p.name, widthPt: p.widthPt, heightPt: p.heightPt, background: p.background, grid: p.grid, guides: p.guides });
    const changedPageIds: string[] = [];
    if (structural) {
      // Every page is restated at its index, in ascending order, so replay (forward, undo, redo) reproduces the order exactly.
      this.pages.forEach((page, index) => forward.upserts.pages.push({ index, page: meta(page) }));
      this.doc.pages.forEach((page, index) => inverse.upserts.pages.push({ index, page: meta(page) }));
      for (const id of deletedPages) forward.deletes.pageIds.push(id);
      for (const id of createdPages) inverse.deletes.pageIds.push(id);
      for (const id of [...createdPages, ...deletedPages]) changedPageIds.push(id);
      for (const page of this.pages) {
        if (createdPages.includes(page.id)) page.layers.forEach((layer, index) => forward.upserts.layers.push({ pageId: page.id, layer, index }));
      }
      for (const page of this.doc.pages) {
        if (deletedPages.includes(page.id)) page.layers.forEach((layer, index) => inverse.upserts.layers.push({ pageId: page.id, layer, index }));
      }
    }
    for (const page of this.pages) {
      const orig = this.doc.pages.find(p => p.id === page.id);
      if (!orig) continue;
      const edited = canonicalSerialize(meta(orig)) !== canonicalSerialize(meta(page));
      if (edited) {
        if (!structural) { forward.upserts.pages.push({ page: meta(page) }); inverse.upserts.pages.push({ page: meta(orig) }); }
        changedPageIds.push(page.id);
      } else if (reordered && !changedPageIds.includes(page.id)) {
        changedPageIds.push(page.id);
      }
    }

    // ---- layers of pages that survive
    const changedLayerIds: string[] = [];
    for (const page of this.pages) {
      const orig = this.doc.pages.find(p => p.id === page.id);
      if (!orig) continue;
      const before = new Map(orig.layers.map((l, index) => [l.id, { l, index }] as const));
      const after = new Map(page.layers.map((l, index) => [l.id, { l, index }] as const));
      page.layers.forEach((layer, index) => {
        const o = before.get(layer.id);
        if (!o) { forward.upserts.layers.push({ pageId: page.id, layer, index }); inverse.deletes.layerIds.push(layer.id); changedLayerIds.push(layer.id); }
        else if (o.l !== layer && canonicalSerialize(o.l) !== canonicalSerialize(layer)) {
          forward.upserts.layers.push({ pageId: page.id, layer }); inverse.upserts.layers.push({ pageId: page.id, layer: o.l }); changedLayerIds.push(layer.id);
        }
      });
      orig.layers.forEach((layer, index) => {
        if (after.has(layer.id)) return;
        forward.deletes.layerIds.push(layer.id); inverse.upserts.layers.push({ pageId: page.id, layer, index }); changedLayerIds.push(layer.id);
      });
    }

    // ---- elements
    const createdSorted = created.map(id => finalById.get(id)!).sort((a, b) => a.index - b.index);
    for (const c of createdSorted) forward.upserts.elements.push({ pageId: c.pageId, index: c.index, element: c.element });
    for (const id of changed) {
      const f = finalById.get(id)!;
      forward.upserts.elements.push({ pageId: f.pageId, element: f.element });
      inverse.upserts.elements.push({ pageId: f.pageId, element: this.origElements.get(id)!.element });
    }
    const deletedSorted = deleted.map(id => this.origElements.get(id)!).sort((a, b) => a.index - b.index);
    for (const d of deletedSorted) {
      forward.deletes.elementIds.push(d.element.id);
      inverse.upserts.elements.push({ pageId: d.pageId, index: d.index, element: d.element });
    }
    inverse.deletes.elementIds.push(...createdSorted.map(c => c.element.id));

    // ---- assets. Undoing a removal re-appends, so later assets are removed and restored in order to keep the catalogue order exact.
    const finalAssets = new Map(this.assets.map(a => [a.id, a] as const));
    const changedAssetIds: string[] = [];
    for (const a of this.assets) {
      const o = this.origAssets.get(a.id);
      if (!o) { forward.upserts.assets.push(a); inverse.deletes.assetIds.push(a.id); changedAssetIds.push(a.id); }
      else if (o.asset !== a && canonicalSerialize(o.asset) !== canonicalSerialize(a)) { forward.upserts.assets.push(a); inverse.upserts.assets.push(o.asset); changedAssetIds.push(a.id); }
    }
    const removed = [...this.origAssets.values()].filter(o => !finalAssets.has(o.asset.id));
    if (removed.length) {
      const first = Math.min(...removed.map(o => o.index));
      for (const o of removed) { forward.deletes.assetIds.push(o.asset.id); changedAssetIds.push(o.asset.id); }
      const tail = [...this.origAssets.values()].filter(o => o.index >= first).sort((a, b) => a.index - b.index);
      for (const o of tail) {
        if (finalAssets.has(o.asset.id) && !inverse.deletes.assetIds.includes(o.asset.id)) inverse.deletes.assetIds.push(o.asset.id);
        const already = inverse.upserts.assets.findIndex(x => x.id === o.asset.id);
        if (already >= 0) inverse.upserts.assets.splice(already, 1);
        inverse.upserts.assets.push(o.asset);
      }
    }

    // Connectors whose route changes because an attached element moved are affected visually, but are not rewritten.
    const visually = new Set<string>([...created, ...changed]);
    const touched = new Set<string>([...changed, ...deleted]);
    for (const p of this.pages) for (const el of p.elements) {
      if (el.kind === 'connector' && (touched.has(el.from.elementId ?? '') || touched.has(el.to.elementId ?? ''))) visually.add(el.id);
    }
    for (const id of deleted) visually.delete(id);

    const noChange = isNothing(forward);
    const description = operations.map(o => o.op).join(', ').slice(0, 1000);
    return {
      forward, inverse, created, changed, deleted, changedPageIds, changedLayerIds, changedAssetIds,
      visuallyAffectedIds: [...visually], description, noChange,
    };
  }
}

function uniqueAlias(alias: string, taken: ReadonlySet<string>): string {
  if (!taken.has(alias)) return alias;
  let n = 2;
  while (taken.has(`${alias}-${n}`)) n++;
  return `${alias}-${n}`;
}

function isNothing(d: DiffBody): boolean {
  return !d.upserts.elements.length && !d.upserts.assets.length && !d.upserts.pages.length && !d.upserts.layers.length &&
    !d.deletes.elementIds.length && !d.deletes.assetIds.length && !d.deletes.pageIds.length && !d.deletes.layerIds.length;
}

