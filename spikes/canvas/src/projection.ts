import {
  Graph, Cell, Point, ImageExport, SvgCanvas2D, xmlUtils, constants,
  VertexHandler, ElbowEdgeHandler, EdgeHandler, SelectionHandler, VertexHandlerConfig, InternalEvent, EventObject,
  type CellStyle,
} from '@maxgraph/core';
import { PNG_BASE64, SVG_TEXT } from './fixtures';

// ---- DTO: plain data only, no maxGraph objects ------------------------------------------------

export interface ElementDto { id: string; x: number; y: number; w: number; h: number; angle: number; text: string }
export interface AssetDto extends ElementDto { mime: string; bytes: Uint8Array }
export interface EndpointDto { elementId: string; port: { x: number; y: number } }
export interface ConnectorDto { id: string; from: EndpointDto; to: EndpointDto; bends: Array<{ x: number; y: number }> }
export interface ProbeSceneDto {
  revision: number;
  rect: ElementDto;
  ellipse: ElementDto;
  picture: AssetDto;
  vector: AssetDto;
  connector: ConnectorDto;
}
export type GestureKind = 'move' | 'resize' | 'rotate' | 'bend' | 'text';
export interface GestureEvent { phase: 'begin' | 'commit' | 'cancel'; kind: GestureKind; id: string; t: number }

// ---- fixture ----------------------------------------------------------------------------------

const b64 = (s: string) => Uint8Array.from(atob(s), c => c.charCodeAt(0));

export function fixture(): ProbeSceneDto {
  return {
    revision: 0,
    rect: { id: 'rect', x: 60, y: 80, w: 120, h: 70, angle: 0, text: 'Probe' },
    ellipse: { id: 'ellipse', x: 360, y: 90, w: 110, h: 70, angle: 33, text: '' },
    picture: { id: 'picture', x: 60, y: 260, w: 96, h: 96, angle: 0, text: '', mime: 'image/png', bytes: b64(PNG_BASE64) },
    vector: { id: 'vector', x: 260, y: 260, w: 96, h: 96, angle: 0, text: '', mime: 'image/svg+xml', bytes: new TextEncoder().encode(SVG_TEXT) },
    connector: {
      id: 'connector',
      from: { elementId: 'rect', port: { x: 1, y: 0.5 } },
      to: { elementId: 'ellipse', port: { x: 0, y: 0.5 } },
      bends: [],
    },
  };
}

// ---- projection -------------------------------------------------------------------------------

const toBase64 = (b: Uint8Array) => { let s = ''; for (const x of b) s += String.fromCharCode(x); return btoa(s); };
const dataUri = (mime: string, bytes: Uint8Array) => `data:${mime};base64,${toBase64(bytes)}`;

export class Projection {
  readonly graph: Graph;
  private revision = 0;
  private suspended = 0;
  readonly log: GestureEvent[] = [];
  readonly timing: Record<string, number> = {};
  private mimes = new Map<string, string>();

  constructor(container: HTMLElement) {
    VertexHandlerConfig.rotationEnabled = true;
    container.addEventListener('contextmenu', e => e.preventDefault());
    this.graph = new Graph(container);
    this.graph.setPanning(false);
    this.graph.setConnectable(false); // connectors come from the scene, not from drag-to-connect
    this.graph.setAllowDanglingEdges(false);
    this.graph.setDisconnectOnMove(false);
    this.graph.setCellsDisconnectable(false);
    this.graph.getDataModel().addListener(InternalEvent.CHANGE, () => { if (!this.suspended) this.revision++; });
    document.addEventListener('keydown', e => { if (e.key === 'Escape') this.graph.escape(e); });
    installGestureHooks(this);
  }

  /** Replaces the whole model with the scene; this is not a user edit, so it sets rather than bumps the revision. */
  project(scene: ProbeSceneDto): void {
    const t0 = performance.now();
    const g = this.graph;
    this.suspended++;
    try {
      g.batchUpdate(() => {
        const root = g.getDefaultParent();
        g.removeCells(g.getChildCells(root, true, true), true);
        this.mimes.clear();
        const vertex = (e: ElementDto, style: CellStyle) => {
          const c = g.insertVertex({ parent: root, id: e.id, value: e.text, position: [e.x, e.y], size: [e.w, e.h], style });
          return c;
        };
        vertex(scene.rect, { fillColor: '#dbeafe', strokeColor: '#1e5ac8', rotation: scene.rect.angle });
        vertex(scene.ellipse, { shape: 'ellipse', fillColor: '#fde68a', strokeColor: '#a16207', rotation: scene.ellipse.angle });
        for (const a of [scene.picture, scene.vector]) {
          this.mimes.set(a.id, a.mime);
          vertex(a, { shape: 'image', image: dataUri(a.mime, a.bytes), imageAspect: false, rotation: a.angle, strokeColor: '#999999' });
        }
        const c = scene.connector;
        const src = g.getDataModel().getCell(c.from.elementId);
        const tgt = g.getDataModel().getCell(c.to.elementId);
        const edge = g.insertEdge({
          parent: root, id: c.id, source: src, target: tgt, value: '',
          style: {
            edgeStyle: 'elbowEdgeStyle', elbow: 'horizontal', endArrow: 'block', strokeColor: '#334155',
            exitX: c.from.port.x, exitY: c.from.port.y, entryX: c.to.port.x, entryY: c.to.port.y,
          },
        });
        if (c.bends.length) edge.getGeometry()!.points = c.bends.map(p => new Point(p.x, p.y));
      });
    } finally {
      this.suspended--;
    }
    this.revision = scene.revision;
    this.timing.projectMs = performance.now() - t0;
  }

  readProbeState(): ProbeSceneDto {
    const m = this.graph.getDataModel();
    const el = (id: string): ElementDto => {
      const c = m.getCell(id)!;
      const geo = c.getGeometry()!;
      return {
        id, x: geo.x, y: geo.y, w: geo.width, h: geo.height,
        angle: Number((c.getStyle() as CellStyle | null)?.rotation ?? 0),
        text: typeof c.getValue() === 'string' ? (c.getValue() as string) : '',
      };
    };
    const asset = (id: string): AssetDto => {
      const c = m.getCell(id)!;
      const uri = String((c.getStyle() as CellStyle).image);
      const bytes = b64(uri.slice(uri.indexOf(',') + 1).replace(/^base64,/, ''));
      return { ...el(id), mime: this.mimes.get(id) ?? '', bytes };
    };
    const edge = m.getCell('connector')!;
    const st = edge.getStyle() as CellStyle;
    return {
      revision: this.revision,
      rect: el('rect'),
      ellipse: el('ellipse'),
      picture: asset('picture'),
      vector: asset('vector'),
      connector: {
        id: 'connector',
        from: { elementId: edge.getTerminal(true)!.getId()!, port: { x: Number(st.exitX), y: Number(st.exitY) } },
        to: { elementId: edge.getTerminal(false)!.getId()!, port: { x: Number(st.entryX), y: Number(st.entryY) } },
        bends: (edge.getGeometry()!.points ?? []).map(p => ({ x: p.x, y: p.y })),
      },
    };
  }

  /** Renders the whole scene (not the viewport) to a PNG using maxGraph's canvas export, then rasterises the SVG. */
  async renderProbe(scale = 2): Promise<Uint8Array> {
    const t0 = performance.now();
    const g = this.graph;
    const view = g.getView();
    const bounds = g.getGraphBounds();
    const pad = 8;
    const doc = xmlUtils.createXmlDocument();
    const root = doc.createElementNS(constants.NS_SVG, 'svg') as SVGElement;
    const w = Math.ceil(bounds.width / view.scale + pad * 2), h = Math.ceil(bounds.height / view.scale + pad * 2);
    root.setAttribute('xmlns', constants.NS_SVG);
    root.setAttribute('xmlns:xlink', constants.NS_XLINK);
    root.setAttribute('width', String(w));
    root.setAttribute('height', String(h));
    root.setAttribute('viewBox', `0 0 ${w} ${h}`);
    const canvas = new SvgCanvas2D(root, false);
    // Export at scale 1 whatever the on-screen zoom: states are in zoomed view coordinates, so unscale and re-origin them.
    canvas.scale(1 / view.scale);
    canvas.translate(-bounds.x + pad * view.scale, -bounds.y + pad * view.scale);
    new ImageExport().drawState(view.getState(g.getDataModel().getRoot()!)!, canvas);
    const svg = new XMLSerializer().serializeToString(root);
    this.timing.exportSvgMs = performance.now() - t0;
    const png = await rasterise(svg, w, h, scale);
    this.timing.renderMs = performance.now() - t0;
    return png;
  }
}

async function rasterise(svg: string, w: number, h: number, scale: number): Promise<Uint8Array> {
  const url = URL.createObjectURL(new Blob([svg], { type: 'image/svg+xml' }));
  try {
    const img = new Image();
    img.src = url;
    await img.decode();
    const cv = document.createElement('canvas');
    cv.width = w * scale;
    cv.height = h * scale;
    const ctx = cv.getContext('2d')!;
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(0, 0, cv.width, cv.height);
    ctx.drawImage(img, 0, 0, cv.width, cv.height);
    // toBlob throws a SecurityError if any non-CORS image tainted the canvas.
    const blob = await new Promise<Blob | null>(r => cv.toBlob(r, 'image/png'));
    if (!blob) throw new Error('toBlob returned null');
    return new Uint8Array(await blob.arrayBuffer());
  } finally {
    URL.revokeObjectURL(url);
  }
}

/** Separate probe used by the taint test: draws each asset kind on a canvas and tries to read it back. */
export async function taintCheck(scene: ProbeSceneDto): Promise<{ png: boolean; svg: boolean }> {
  const attempt = async (a: AssetDto) => {
    const img = new Image();
    img.src = dataUri(a.mime, a.bytes);
    await img.decode();
    const cv = document.createElement('canvas');
    cv.width = 64; cv.height = 64;
    cv.getContext('2d')!.drawImage(img, 0, 0);
    try { cv.toDataURL('image/png'); return false; } catch { return true; }
  };
  return { png: await attempt(scene.picture), svg: await attempt(scene.vector) };
}

// ---- gesture lifecycle hooks ------------------------------------------------------------------

/**
 * begin/commit/cancel are derived from maxGraph's own handlers, not from model changes:
 * a gesture begins when a handler first previews, commits when its mouseUp runs, and is cancelled
 * when the handler resets without a commit (Escape). Selection and zoom never reach these handlers.
 */
function installGestureHooks(p: Projection): void {
  let active: { kind: GestureKind; id: string } | null = null;
  let committing = false;
  const emit = (phase: GestureEvent['phase'], kind: GestureKind, id: string) =>
    p.log.push({ phase, kind, id, t: performance.now() });
  const begin = (kind: GestureKind, id: string) => { if (!active) { active = { kind, id }; emit('begin', kind, id); } };

  const wrap = (proto: any, name: string, around: (self: any, orig: (...a: any[]) => any, args: any[]) => any) => {
    const orig = proto[name];
    proto[name] = function (this: any, ...args: any[]) { return around(this, (...a) => orig.apply(this, a), args); };
  };
  const finishWith = (self: any, orig: (...a: any[]) => any, args: any[]) => {
    const was = active;
    committing = true;
    try { return orig(...args); } finally {
      committing = false;
      if (was && active === was) { emit('commit', was.kind, was.id); active = null; }
    }
  };
  const resetWith = (orig: (...a: any[]) => any, args: any[]) => {
    const was = active;
    const r = orig(...args);
    if (was && !committing && active === was) { emit('cancel', was.kind, was.id); active = null; }
    return r;
  };

  // Resize / rotate: VertexHandler.start(x, y, index) grabs a handle.
  wrap(VertexHandler.prototype, 'start', (s, orig, a) => {
    orig(...a);
    const index = a[2] as number;
    const kind: GestureKind = index === InternalEvent.ROTATION_HANDLE ? 'rotate' : 'resize';
    if (index !== InternalEvent.LABEL_HANDLE) begin(kind, s.state.cell.getId());
  });
  wrap(VertexHandler.prototype, 'mouseUp', (s, orig, a) => finishWith(s, orig, a));
  // maxGraph 0.24.0 defect: VertexHandler.reset() reads sizers[index].node, but the rotation handle has index -2 and
  // no sizer, so Escape during a rotate drag throws before the handler is cleaned up. Give reset() a stand-in sizer.
  wrap(VertexHandler.prototype, 'reset', (s, orig, a) => {
    const i = s.index as number | null;
    const patch = i !== null && s.sizers?.[i] === undefined;
    if (patch) s.sizers[i!] = { node: { style: { display: '' } } };
    try { return resetWith(orig, a); } finally { if (patch) delete s.sizers[i!]; }
  });

  // Bend: EdgeHandler (and the elbow subclass) grab a bend handle.
  for (const H of [EdgeHandler, ElbowEdgeHandler]) {
    wrap(H.prototype, 'start', (s, orig, a) => { orig(...a); begin('bend', s.state.cell.getId()); });
    wrap(H.prototype, 'mouseUp', (s, orig, a) => finishWith(s, orig, a));
    wrap(H.prototype, 'reset', (_s, orig, a) => resetWith(orig, a));
  }

  // Move: SelectionHandler previews only after the mouse moves past tolerance.
  wrap(SelectionHandler.prototype, 'mouseMove', (s, orig, a) => {
    const r = orig(...a);
    if (s.cell && s.first && s.currentDx != null && s.currentDy != null && (s.currentDx !== 0 || s.currentDy !== 0)) {
      begin('move', s.cell.getId());
    }
    return r;
  });
  wrap(SelectionHandler.prototype, 'mouseUp', (s, orig, a) => finishWith(s, orig, a));
  wrap(SelectionHandler.prototype, 'reset', (_s, orig, a) => resetWith(orig, a));

  // In-place text edit.
  p.graph.addListener(InternalEvent.START_EDITING, (_s: unknown, e: EventObject) => {
    const cell = e.getProperty('cell') as Cell;
    emit('begin', 'text', cell.getId()!);
  });
  p.graph.addListener(InternalEvent.EDITING_STOPPED, (_s: unknown, e: EventObject) => {
    const cell = (e.getProperty('cell') as Cell | undefined)?.getId() ?? '';
    emit(e.getProperty('cancel') ? 'cancel' : 'commit', 'text', cell);
  });
}

