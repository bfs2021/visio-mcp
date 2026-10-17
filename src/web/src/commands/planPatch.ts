import type {
  Asset, Bounds, ConnectorElement, DiagramDocument, Element, Endpoint, EndpointInput, ImageElement, LineStyle, Operation, Page,
  Point, ShapeElement, ShapeStyle, TextBlock, TextElement,
} from '../contracts/generated';
import { canonicalSerialize } from '../model/canonical';
import { fail, ok, type Result } from '../model/result';
import { emptyDiffBody, type DiffBody } from './diff';

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

const UUID = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

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
const LINE_ONLY_STYLE = ['colour', 'widthPt', 'startArrow', 'endArrow'];

type PatchObj = Record<string, unknown>;

class Failure extends Error {
  constructor(readonly result: Result<never>) { super('plan failure'); }
}
function stop(code: Parameters<typeof fail>[0], message: string, details?: Record<string, unknown>): never {
  throw new Failure(fail(code, message, details ? { details } : {}));
}

const merge = <T extends object>(base: T, patch: PatchObj): T => ({ ...base, ...(patch as Partial<T>) });

/** Plans a batch against `doc` without touching it. All-or-nothing: any failure returns an error and no plan. */
export function planPatch(doc: DiagramDocument, operations: Operation[], ctx: PlanContext): Result<Plan> {
  try {
    return ok(new Planner(doc, ctx).run(operations));
  } catch (e) {
    if (e instanceof Failure) return e.result;
    throw e;
  }
}

class Planner {
  private pages: Page[];
  private assets: Asset[];
  private readonly origElements = new Map<string, { pageId: string; index: number; element: Element }>();
  private readonly origAssets: Set<string>;
  private readonly createdIds: string[] = [];
  private readonly createdAssetIds: string[] = [];
  private readonly scopePageId: string | undefined;

  constructor(private readonly doc: DiagramDocument, private readonly ctx: PlanContext) {
    this.pages = doc.pages.slice();
    this.assets = doc.assets.slice();
    this.origAssets = new Set(doc.assets.map(a => a.id));
    for (const p of doc.pages) p.elements.forEach((element, index) => this.origElements.set(element.id, { pageId: p.id, index, element }));
    this.scopePageId = ctx.pageId ?? (doc.pages.length === 1 ? doc.pages[0]!.id : undefined);
    if (ctx.pageId && !doc.pages.some(p => p.id === ctx.pageId)) stop('not_found', `page ${ctx.pageId} does not exist`);
  }

  // ---------------------------------------------------------------- lookup helpers
  private pageIndex(pageId: string): number {
    const i = this.pages.findIndex(p => p.id === pageId);
    if (i < 0) stop('not_found', `page ${pageId} does not exist`);
    return i;
  }

  private findById(id: string): { page: Page; pageIdx: number; index: number; element: Element } | undefined {
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
  }

  private defaultPage(pageId: string | undefined): number {
    const id = pageId ?? this.scopePageId;
    if (!id) return stop('invalid_request', 'this request needs a pageId: the document has more than one page');
    return this.pageIndex(id);
  }

  /** UUID first, then page-scoped alias, then exact display name. Ambiguous names are reported, never guessed. */
  private resolve(ref: string, pageId?: string): { page: Page; pageIdx: number; index: number; element: Element } {
    if (UUID.test(ref)) {
      const hit = this.findById(ref);
      if (!hit) stop('not_found', `no element ${ref}`, { target: ref });
      return hit!;
    }
    const scope = pageId ?? this.scopePageId;
    if (!scope) return stop('invalid_request', `"${ref}" is not a UUID and the request has no page scope`, { target: ref });
    const pageIdx = this.pageIndex(scope);
    const page = this.pages[pageIdx]!;
    const byAlias = page.elements.findIndex(e => e.alias === ref);
    if (byAlias >= 0) return { page, pageIdx, index: byAlias, element: page.elements[byAlias]! };
    const named = page.elements.map((e, i) => ({ e, i })).filter(x => x.e.name === ref);
    if (named.length === 1) return { page, pageIdx, index: named[0]!.i, element: named[0]!.e };
    if (named.length > 1) return stop('ambiguous_target', `"${ref}" matches ${named.length} elements`, { target: ref, candidates: named.map(x => x.e.id) });
    return stop('not_found', `nothing is called "${ref}"`, { target: ref });
  }

  private layerLocked(page: Page, el: Element): boolean {
    return el.layerIds.some(id => page.layers.find(l => l.id === id)?.locked);
  }

  private assertEditable(page: Page, el: Element, what: string): void {
    if (el.locked || this.layerLocked(page, el)) stop('locked_target', `${el.id} is locked: ${what} refused`, { target: el.id });
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
    const b = hit.element.bounds;
    return { x: b.x + b.width / 2, y: b.y + b.height / 2 };
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

  // ---------------------------------------------------------------- operations
  run(operations: Operation[]): Plan {
    for (const op of operations) this.apply(op);
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
    this.createdIds.push(el.id);
  }

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
        this.assertPositive(el.bounds, id);
        this.assertFinite(el.rotationDeg, 'rotationDeg');
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
        this.assertEditable(page, element, 'move');
        this.assertFinite(op.delta.xPt, 'delta.xPt'); this.assertFinite(op.delta.yPt, 'delta.yPt');
        const { xPt: dx, yPt: dy } = op.delta;
        if (element.kind === 'group') return stop('invalid_request', 'group moves arrive with the group operations (I20)', { target: element.id });
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
        this.assertEditable(page, element, 'resize');
        if (element.kind === 'connector' || element.kind === 'group') return stop('invalid_request', `${element.kind} bounds are derived, not resized`, { target: element.id });
        const bounds = { ...element.bounds, ...op.bounds };
        this.assertPositive(bounds, element.id);
        this.store(pageIdx, index, { ...element, bounds } as Element);
        return;
      }
      case 'rotate': {
        const { page, pageIdx, index, element } = this.resolve(op.target, this.ctx.pageId);
        this.assertEditable(page, element, 'rotate');
        if (element.kind === 'connector') return stop('invalid_request', 'connectors have no rotation', { target: element.id });
        if (element.kind === 'group') return stop('invalid_request', 'group rotation arrives with the group operations (I20)', { target: element.id });
        if (op.pivot) return stop('invalid_request', 'pivot applies only to groups');
        const angle = op.angleDeg !== undefined ? op.angleDeg : element.rotationDeg + op.deltaDeg!;
        this.assertFinite(angle, 'rotation');
        this.store(pageIdx, index, { ...element, rotationDeg: angle } as Element);
        return;
      }
      case 'delete': {
        const hit = this.resolve(op.target, this.ctx.pageId);
        this.assertEditable(hit.page, hit.element, 'delete');
        if (hit.element.kind === 'group') return stop('invalid_request', 'group deletion arrives with the group operations (I20)', { target: hit.element.id });
        const dependents = hit.page.elements.filter((e): e is ConnectorElement => e.kind === 'connector' && (e.from.elementId === hit.element.id || e.to.elementId === hit.element.id));
        const doomed = new Set<string>([hit.element.id]);
        if (op.withConnectors) for (const c of dependents) { this.assertEditable(hit.page, c, 'delete'); doomed.add(c.id); }
        const page = this.pages[hit.pageIdx]!;
        this.pages[hit.pageIdx] = { ...page, elements: page.elements.filter(e => !doomed.has(e.id)) };
        return;
      }
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
        this.createdAssetIds.push(op.asset.id);
        return;
      }
    }
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
        case 'geometry': need(element.kind === 'shape', key); next.geometry = merge((element as ShapeElement).geometry, value as PatchObj); break;
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
    for (const page of this.pages) {
      const ids = new Set(page.elements.map(e => e.id));
      for (const el of page.elements) {
        if (el.kind === 'connector') {
          const missing = [el.from, el.to].filter(ep => ep.glue !== 'none' && !ids.has(ep.elementId!));
          if (missing.length) stop('dependency_conflict', `connector ${el.id} would lose an endpoint`, { connectorIds: [el.id], elementIds: missing.map(m => m.elementId) });
        }
        if (el.kind === 'group') {
          const missing = el.childIds.filter(c => !ids.has(c));
          if (missing.length) stop('dependency_conflict', `group ${el.id} would lose a child`, { groupIds: [el.id], elementIds: missing });
        }
        if (el.kind === 'image' && !this.assets.some(a => a.id === el.assetId)) stop('invalid_request', `image ${el.id} references unknown asset ${el.assetId}`);
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

    const changedPageIds: string[] = [];
    for (const page of this.pages) {
      const orig = this.doc.pages.find(p => p.id === page.id);
      if (!orig) continue;
      const meta = (p: Page) => ({ id: p.id, name: p.name, widthPt: p.widthPt, heightPt: p.heightPt, background: p.background, grid: p.grid, guides: p.guides });
      if (canonicalSerialize(meta(orig)) !== canonicalSerialize(meta(page))) {
        forward.upserts.pages.push({ page: meta(page) });
        inverse.upserts.pages.push({ page: meta(orig) });
        changedPageIds.push(page.id);
      }
    }

    const newAssets = this.createdAssetIds.filter(id => !this.origAssets.has(id));
    for (const id of newAssets) {
      forward.upserts.assets.push(this.assets.find(a => a.id === id)!);
      inverse.deletes.assetIds.push(id);
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
      forward, inverse, created, changed, deleted, changedPageIds, changedLayerIds: [], changedAssetIds: newAssets,
      visuallyAffectedIds: [...visually], description, noChange,
    };
  }
}

function isNothing(d: DiffBody): boolean {
  return !d.upserts.elements.length && !d.upserts.assets.length && !d.upserts.pages.length && !d.upserts.layers.length &&
    !d.deletes.elementIds.length && !d.deletes.assetIds.length && !d.deletes.pageIds.length && !d.deletes.layerIds.length;
}
