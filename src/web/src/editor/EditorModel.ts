import type { Element, Operation, PreparedAssetRef, Snapshot, TransactionResult } from '../contracts/generated';
import { CommandEngine } from '../commands/CommandEngine';
import { copyElements, pasteOperations, type ClipboardData } from '../commands/clipboard';
import { MaxGraphAdapter, DEFAULT_SCALE } from '../canvas/MaxGraphAdapter';
import type { GestureNotice, Tool, ViewportInfo } from '../canvas/CanvasAdapter';
import { GestureController, type GestureKind, type GestureToken } from '../canvas/GestureController';
import { createSnapshotProjector } from '../canvas/SnapshotProjector';
import { collapseToGroups, expandToGroups, leavesOfTargets } from '../canvas/selection';
import { deleteOperations, findElement, intentOperations, nudgeOperations, pageContaining, pageOf, type Intent } from '../canvas/intents';
import { fail, ok, type Result } from '../model/result';
import { drawScriptCompiler, validateScript, type ValidationReport } from '../script/compiler';
import { toPoints, type Unit } from '../model/validate';

/** What the page needs from the host. In a plain browser (tests, dev) a stub stands in. */
export interface HostApi {
  pickImage(): Promise<Result<{ cancelled: true } | { ref: PreparedAssetRef; notes: string[]; width: number; height: number }>>;
  openJson(): Promise<Result<{ cancelled?: true; path?: string; notes?: string[] }>>;
  saveJson(saveAs: boolean): Promise<Result<{ cancelled?: true; path?: string; revision?: number }>>;
  openVsdx(): Promise<Result<NativeOutcome>>;
  /** `path` repeats a choice the person already made when they agree to a lossy export. */
  exportVsdx(opts: { acceptLoss?: boolean; path?: string }): Promise<Result<NativeOutcome>>;
}

export interface DiagnosticItem { severity: 'info' | 'warning' | 'error'; code: string; message: string; elementId?: string; pageId?: string; action?: string }
export interface NativeOutcome { cancelled?: true; path?: string; summary?: string; diagnostics?: DiagnosticItem[]; lossy?: boolean; revision?: number; bytes?: number }

/** What a person is shown about a Visio file: what came across and what did not, with an optional action (export anyway). */
export interface CompatibilityView { title: string; summary: string; diagnostics: DiagnosticItem[]; confirm?: { label: string; run: () => void } }

export const noHost: HostApi = {
  openVsdx: async () => fail('not_running', 'there is no host to open files'),
  exportVsdx: async () => fail('not_running', 'there is no host to save files'),
  pickImage: async () => fail('not_running', 'there is no host to choose a picture'),
  openJson: async () => fail('not_running', 'there is no host to open files'),
  saveJson: async () => fail('not_running', 'there is no host to save files'),
};

const DEFAULT_SIZE: Record<string, { w: number; h: number }> = {
  rectangle: { w: 120, h: 72 }, 'rounded-rectangle': { w: 120, h: 72 }, ellipse: { w: 96, h: 72 }, diamond: { w: 96, h: 72 }, triangle: { w: 90, h: 80 },
  hexagon: { w: 108, h: 72 }, parallelogram: { w: 120, h: 60 }, cylinder: { w: 72, h: 96 }, cloud: { w: 120, h: 80 }, callout: { w: 120, h: 80 },
};

export type ScriptReport =
  | ({ ok: true; source: string } & ValidationReport)
  | { ok: false; source: string; revision: number; message: string; line?: number; column?: number; hint?: string };
export type ScriptOutcome =
  | { ok: true; transactionId: string; revision: number; created: string[]; changed: string[]; deleted: string[]; noChange: boolean; warnings: string[] }
  | { ok: false; message: string; code: string; line?: number; column?: number; hint?: string };

export interface Status { text: string; kind: 'info' | 'warning' | 'error' }

/**
 * The editor's non-React core. Every document change goes through GestureController or CommandEngine; the canvas only emits intents.
 * `version` changes whenever anything the UI shows may have changed (React subscribes through `subscribe`/`getVersion`).
 */
export class EditorModel {
  readonly engine: CommandEngine;
  readonly controller: GestureController;
  private adapter: MaxGraphAdapter | null = null;
  private readonly projector = createSnapshotProjector((snapshot, assetId) => this.assetUrlIn(snapshot, assetId));
  private listeners = new Set<() => void>();
  private version = 0;
  private clipboard: ClipboardData | null = null;
  private pastes = 0;

  /** The script and transaction drawer. */
  drawerOpen = false;
  drawerTab: 'script' | 'transactions' = 'script';
  scriptText = '';
  scriptReport: ScriptReport | null = null;
  scriptOutcome: ScriptOutcome | null = null;
  /** What each script transaction ran, so the transaction drawer can show its source. */
  readonly scriptSources = new Map<string, string>();

  host: HostApi = noHost;
  /** The compatibility report dialog, when one is showing. */
  report: CompatibilityView | null = null;
  selection: string[] = [];
  tool: Tool = { kind: 'select' };
  unit: Unit = 'mm';
  status: Status = { text: 'Ready', kind: 'info' };
  viewport: ViewportInfo | null = null;
  /** Called when the interlock should hold agent mutations (I30 wires this to the admission gate). */
  onGestureState: (active: boolean) => void = () => {};

  constructor(engine: CommandEngine) {
    this.engine = engine;
    this.controller = new GestureController(engine);
    this.controller.onGestureState = a => this.onGestureState(a);
    this.controller.onDiscard = reason => {
      this.adapter?.project(this.engine.store.snapshot()); // show committed state again
      if (reason) this.say(reason, 'warning');
      this.bump();
    };
    engine.setProjector(snapshot => this.adapter ? this.adapter.project(snapshot) : ok(undefined));
    engine.setSnapshotProjector(this.projector.project);
    engine.onCommitted(() => { this.syncGroupFrames(); this.bump(); });
  }

  // ------------------------------------------------------------------ React subscription
  subscribe = (l: () => void) => { this.listeners.add(l); return () => { this.listeners.delete(l); }; };
  getVersion = () => this.version;
  bump(): void { this.version++; for (const l of this.listeners) l(); }
  say(text: string, kind: Status['kind'] = 'info'): void { this.status = { text, kind }; this.bump(); }

  get snapshot(): Snapshot { return this.engine.store.snapshot(); }
  get currentPage() { return pageOf(this.snapshot); }
  /** What the user has selected, with every complete group standing for its members. The canvas itself only ever holds the members. */
  get targets(): string[] { return collapseToGroups(this.snapshot, this.selection); }
  selectedElements(): Element[] { return this.targets.map(id => findElement(this.snapshot, id)).filter((e): e is Element => !!e); }

  /** Picking a member of a group picks the whole group, unless Ctrl was held to reach into it. */
  private handleSelection(ids: string[], subSelect: boolean): void {
    const next = subSelect ? ids : expandToGroups(this.snapshot, ids);
    if (next.length !== ids.length || next.some((id, i) => id !== ids[i])) this.adapter?.setSelection(next);
    this.selection = next;
    this.syncGroupFrames();
    this.bump();
  }

  private syncGroupFrames(): void {
    const frames = this.selectedElements().filter(e => e.kind === 'group').map(e => e.bounds);
    this.adapter?.setGroupFrames(frames);
  }

  assetUrlIn(snapshot: Snapshot, assetId: string): string {
    const a = snapshot.document.assets.find(x => x.id === assetId);
    return a ? `/assets/${a.sha256}` : '';
  }

  // ------------------------------------------------------------------ canvas lifecycle
  attachCanvas(container: HTMLElement): MaxGraphAdapter {
    this.adapter?.dispose();
    this.adapter = new MaxGraphAdapter(container, {
      onIntent: i => void this.handleIntent(i),
      onSelection: (ids, info) => this.handleSelection(ids, info?.subSelect ?? false),
      onGesture: n => this.handleGesture(n),
      onViewport: v => { this.viewport = v; this.syncGroupFrames(); this.bump(); },
      onPlace: (what, at) => void this.place(what, at),
    }, id => this.assetUrlIn(this.snapshot, id));
    this.adapter.project(this.snapshot);
    this.adapter.setTool(this.tool);
    queueMicrotask(() => this.adapter?.zoomToFit());
    return this.adapter;
  }

  detachCanvas(): void { this.adapter?.dispose(); this.adapter = null; }
  get canvas(): MaxGraphAdapter | null { return this.adapter; }

  // ------------------------------------------------------------------ gestures and intents
  private handleGesture(n: GestureNotice): void {
    if (n.phase === 'begin') this.controller.begin(n.kind, this.engine.scope(), n.ids);
    // Commit and cancel are driven by the intent (which carries the operations) or by cancel below.
    else if (n.phase === 'cancel' && this.controller.active) this.controller.cancel(this.controller.active);
  }

  /** The canvas reported a finished edit. Plan it from the committed snapshot the gesture began with, then commit once. */
  async handleIntent(intent: Intent): Promise<void> {
    let token = this.controller.active;
    if (token && !gestureMatches(token.kind, intent)) token = null;
    const oneShot = !token;
    if (!token) token = this.controller.begin(intent.type === 'connect' ? 'create' : 'inspector', this.engine.scope(), []);
    const operations = intentOperations(token.startSnapshot, intent);
    const planned = this.controller.preview(token, operations);
    if (!planned.ok) { this.controller.cancel(token); this.say(planned.error.message, 'warning'); return; }
    const r = await this.controller.commit(token);
    if (!r.ok) { if (r.error.code !== 'revision_conflict') this.say(r.error.message, 'warning'); return; }
    if (r.value.noChange) this.adapter?.project(this.engine.store.snapshot()); // a snapped-back drag changes nothing: redraw committed state
    if (oneShot && intent.type === 'connect') this.setTool({ kind: 'select' });
  }

  /** Runs operations as one GUI transaction (inspector, nudge, paste, create, delete). */
  async apply(kind: GestureKind, operations: Operation[]): Promise<Result<TransactionResult>> {
    if (!operations.length) return fail('invalid_request', 'nothing to do');
    const token: GestureToken = this.controller.begin(kind, this.engine.scope(), []);
    const planned = this.controller.preview(token, operations);
    if (!planned.ok) { this.controller.cancel(token); this.say(planned.error.message, 'warning'); return planned; }
    const r = await this.controller.commit(token);
    if (!r.ok && r.error.code !== 'revision_conflict') this.say(r.error.message, 'warning');
    return r;
  }

  // ------------------------------------------------------------------ commands
  setTool(tool: Tool): void {
    this.tool = tool;
    this.adapter?.setTool(tool);
    this.status = tool.kind === 'place' ? { text: 'Click on the page to place the shape. Esc cancels.', kind: 'info' }
      : tool.kind === 'connector' ? { text: 'Drag from one shape to another to connect them.', kind: 'info' } : { text: 'Ready', kind: 'info' };
    this.bump();
  }

  async place(what: string, at: { x: number; y: number }): Promise<void> {
    const page = this.currentPage;
    let op: Operation;
    if (what === 'text') {
      op = { op: 'createText', bounds: { x: at.x - 60, y: at.y - 15, width: 120, height: 30 }, text: { value: 'Text', horizontalAlign: 'left' } };
    } else {
      const size = DEFAULT_SIZE[what] ?? DEFAULT_SIZE.rectangle!;
      op = { op: 'createShape', bounds: { x: at.x - size.w / 2, y: at.y - size.h / 2, width: size.w, height: size.h }, geometry: { preset: what as 'rectangle' } };
    }
    const snapped = snapCreate(op, page.grid);
    const r = await this.apply('create', [snapped]);
    this.setTool({ kind: 'select' });
    if (r.ok && r.value.created[0]) this.canvas?.setSelection([r.value.created[0]]), this.selection = [r.value.created[0]], this.bump();
  }

  async insertPicture(): Promise<void> {
    const picked = await this.host.pickImage();
    if (!picked.ok) { this.say(picked.error.message, 'warning'); return; }
    if ('cancelled' in picked.value) return;
    const { ref, notes, width, height } = picked.value;
    const page = this.currentPage;
    const maxW = page.widthPt / 2, maxH = page.heightPt / 2;
    const natW = toPoints(width, 'px'), natH = toPoints(height, 'px');
    const k = Math.min(1, maxW / natW, maxH / natH);
    const w = natW * k, h = natH * k;
    const exists = this.snapshot.document.assets.some(a => a.id === ref.asset.id);
    const ops: Operation[] = [];
    if (!exists) ops.push({ op: 'registerAsset', asset: ref.asset });
    ops.push({ op: 'createImage', assetId: ref.asset.id, name: ref.asset.name, bounds: { x: (page.widthPt - w) / 2, y: (page.heightPt - h) / 2, width: w, height: h }, fit: 'contain', preserveAspectRatio: true });
    const r = await this.apply('create', ops);
    if (r.ok && notes.length) this.say(`Picture inserted. ${notes.join('; ')}`, 'info');
    if (r.ok && r.value.created[0]) { this.selection = [r.value.created[0]]; this.canvas?.setSelection(this.selection); this.bump(); }
  }

  async undo(): Promise<void> {
    const r = await this.engine.undo({ ...this.engine.scope(), transactionId: crypto.randomUUID(), baseRevision: this.engine.revision });
    if (!r.ok) this.say(r.error.code === 'history_unavailable' ? 'Nothing to undo.' : r.error.message, 'info');
  }

  async redo(): Promise<void> {
    const r = await this.engine.redo({ ...this.engine.scope(), transactionId: crypto.randomUUID(), baseRevision: this.engine.revision });
    if (!r.ok) this.say(r.error.code === 'history_unavailable' ? 'Nothing to redo.' : r.error.message, 'info');
  }

  async deleteSelection(): Promise<void> { await this.apply('inspector', deleteOperations(this.snapshot, this.selection)); this.selection = []; this.bump(); }

  copy(): void {
    const clip = copyElements(this.snapshot, this.selection);
    if (!clip) { this.say('Select something to copy.', 'info'); return; }
    this.clipboard = clip;
    this.pastes = 0;
    this.say(`Copied ${clip.elements.length} object${clip.elements.length === 1 ? '' : 's'}.`, 'info');
  }

  async paste(): Promise<void> {
    if (!this.clipboard) { this.say('Nothing to paste.', 'info'); return; }
    this.pastes++;
    const page = this.currentPage;
    const target = this.clipboard.pageId === page.id ? page : page;
    const { operations, newIds } = pasteOperations(this.clipboard, target, 14.1732283465 * this.pastes, () => crypto.randomUUID());
    const r = await this.apply('paste', operations);
    if (r.ok) { this.selection = newIds; this.canvas?.setSelection(newIds); this.bump(); }
  }

  /** Copies the selection in place of a clipboard round trip, so groups and the connectors between copied shapes keep their structure. */
  async duplicate(): Promise<void> {
    const targets = this.targets;
    if (!targets.length) { this.say('Select something to duplicate.', 'info'); return; }
    const snapshot = this.snapshot;
    const chosen = new Set(targets.flatMap(t => leavesOfTargets(snapshot, [t])));
    // A connector is copied only with both of its shapes, like a pasted selection; one left dangling would have to point at the original.
    const copyable = targets.filter(t => {
      const el = findElement(snapshot, t);
      return el && (el.kind !== 'connector' || [el.from, el.to].every(ep => ep.glue === 'none' || (ep.elementId && chosen.has(ep.elementId))));
    });
    if (!copyable.length) return;
    this.pastes++;
    const offset = 14.1732283465 * this.pastes;
    const before = new Set(this.currentPage.elements.map(e => e.id));
    const r = await this.apply('paste', [{ op: 'duplicate', targets: copyable, offset: { xPt: offset, yPt: offset }, keepAliases: true }]);
    if (!r.ok) return;
    const created = r.value.created.filter(id => !before.has(id));
    const leaves = leavesOfTargets(this.snapshot, created.filter(id => findElement(this.snapshot, id)?.kind !== 'group'));
    this.selection = [...new Set(leaves)];
    this.canvas?.setSelection(this.selection);
    this.bump();
  }

  // ------------------------------------------------------------------ script and transaction drawer
  toggleDrawer(tab?: 'script' | 'transactions'): void {
    if (tab) { this.drawerTab = tab; this.drawerOpen = true; } else this.drawerOpen = !this.drawerOpen;
    this.bump();
  }

  private scriptContext() {
    return { snapshot: this.snapshot, newUuid: () => crypto.randomUUID(), pageId: this.currentPage.id };
  }

  /** Compiles and plans against the current revision without committing anything. */
  validateScriptText(): void {
    const source = this.scriptText;
    const snapshot = this.snapshot;
    const r = validateScript(source, this.scriptContext());
    this.scriptOutcome = null;
    if (r.ok) {
      const { compiled: _compiled, ...report } = r.value;
      this.scriptReport = { ok: true, source, ...report };
    } else {
      this.scriptReport = { ok: false, source, revision: snapshot.revision, message: r.error.message, ...diagnosticOf(r.error.details), };
    }
    this.bump();
  }

  /** Runs the script as one atomic transaction. If it was validated and the document has moved on since, the engine refuses it rather than guess. */
  async runScriptText(): Promise<void> {
    const source = this.scriptText;
    const validated = this.scriptReport?.ok && this.scriptReport.source === source ? this.scriptReport.revision : null;
    const transactionId = crypto.randomUUID();
    const r = await this.engine.executeScript({
      ...this.engine.scope(), transactionId, baseRevision: validated ?? this.engine.revision, pageId: this.currentPage.id, atomic: true, script: source,
    }, drawScriptCompiler);
    if (r.ok) {
      this.scriptSources.set(transactionId, source);
      this.scriptOutcome = { ok: true, transactionId, revision: r.value.revision, created: r.value.created, changed: r.value.changed, deleted: r.value.deleted, noChange: r.value.noChange, warnings: r.value.warnings.map(w => w.message) };
      this.scriptReport = null;
      if (r.value.noChange) this.say('The script changed nothing.', 'info');
      else this.say(`Script ran as revision ${r.value.revision}.`, 'info');
    } else {
      const stale = r.error.code === 'revision_conflict';
      this.scriptOutcome = {
        ok: false, code: r.error.code,
        message: stale ? `The document changed after you validated (now at revision ${this.engine.revision}). Validate again, then run.` : r.error.message,
        ...diagnosticOf(r.error.details),
      };
    }
    this.bump();
  }

  // ------------------------------------------------------------------ groups, arrangement, stacking
  /** Runs one structural operation on the current targets as a single transaction and keeps the members selected. */
  private async arrangeWith(build: (targets: string[]) => Operation, minimum: number, what: string): Promise<Result<TransactionResult> | null> {
    const targets = this.targets;
    if (targets.length < minimum) { this.say(`Select at least ${minimum} object${minimum === 1 ? '' : 's'} to ${what}.`, 'info'); return null; }
    const r = await this.apply('arrange', [build(targets)]);
    if (r.ok) this.canvas?.setSelection(this.selection);
    return r;
  }

  async group(): Promise<void> {
    const r = await this.arrangeWith(targets => ({ op: 'group', targets }), 2, 'group');
    if (r?.ok) this.say('Grouped.', 'info');
  }

  async ungroup(): Promise<void> {
    const groups = this.targets.filter(t => findElement(this.snapshot, t)?.kind === 'group');
    if (!groups.length) { this.say('Select a group to ungroup.', 'info'); return; }
    const r = await this.apply('arrange', groups.map(target => ({ op: 'ungroup', target }) as Operation));
    if (r.ok) this.say('Ungrouped.', 'info');
  }

  async align(edge: 'left' | 'center' | 'right' | 'top' | 'middle' | 'bottom'): Promise<void> {
    await this.arrangeWith(targets => ({ op: 'align', targets, edge }), 2, 'align');
  }

  async distribute(axis: 'horizontal' | 'vertical'): Promise<void> {
    await this.arrangeWith(targets => ({ op: 'distribute', targets, axis }), 3, 'distribute');
  }

  async setGap(axis: 'horizontal' | 'vertical', gapPt: number): Promise<void> {
    if (!Number.isFinite(gapPt) || gapPt < 0) { this.say('The gap must be zero or more.', 'warning'); return; }
    await this.arrangeWith(targets => ({ op: 'setGap', targets, axis, gapPt }), 2, 'set a gap between');
  }

  async reorder(to: 'front' | 'back' | 'forward' | 'backward'): Promise<void> {
    await this.arrangeWith(targets => ({ op: 'reorder', targets, to }), 1, 'reorder');
  }


  async nudge(dx: number, dy: number): Promise<void> { await this.apply('nudge', nudgeOperations(this.snapshot, this.selection, dx, dy)); }

  selectAll(): void {
    const ids = this.currentPage.elements.filter(e => e.kind !== 'group').map(e => e.id);
    this.selection = ids;
    this.canvas?.setSelection(ids);
    this.bump();
  }

  async toggleGrid(field: 'visible' | 'snap'): Promise<void> {
    const page = this.currentPage;
    // Grid settings are page properties; changing them goes through the same engine so undo and recovery see them.
    this.say(`Grid ${field === 'visible' ? (page.grid.visible ? 'hidden' : 'shown') : (page.grid.snap ? 'snapping off' : 'snapping on')}.`, 'info');
    await this.setPageGrid({ [field]: !page.grid[field] });
  }

  private async setPageGrid(change: { visible?: boolean; snap?: boolean }): Promise<void> {
    await this.apply('inspector', [{ op: 'setPage', pageId: this.currentPage.id, patch: { grid: change } }]);
  }

  zoomBy(factor: number): void { const a = this.adapter; if (a) a.setZoom(a.graph.view.scale * factor); }
  zoomToFit(): void { this.adapter?.zoomToFit(); }
  zoomReset(): void { this.adapter?.setZoom(DEFAULT_SCALE); }

  async open(): Promise<void> {
    const r = await this.host.openJson();
    if (!r.ok) { this.say(r.error.message, 'error'); return; }
    if (r.value.cancelled) return;
    this.selection = [];
    this.adapter?.project(this.engine.store.snapshot());
    this.say(`Opened ${r.value.path ?? 'file'}.${r.value.notes?.length ? ' ' + r.value.notes.join('; ') : ''}`, 'info');
  }

  async openVsdx(): Promise<void> {
    const r = await this.host.openVsdx();
    if (!r.ok) { this.say(r.error.message, 'error'); return; }
    if (r.value.cancelled) return;
    this.selection = [];
    this.adapter?.project(this.engine.store.snapshot());
    const items = r.value.diagnostics ?? [];
    this.say(`Opened ${r.value.path ?? 'file'}. ${r.value.summary ?? ''}`.trim(), r.value.lossy ? 'warning' : 'info');
    if (items.length) this.report = { title: 'Opened a Visio drawing', summary: r.value.summary ?? '', diagnostics: items };
    this.bump();
  }

  /** Exports an editable Visio file. If Visio cannot hold something, nothing is written until the person has seen what and agreed. */
  async exportVsdx(opts: { acceptLoss?: boolean; path?: string } = {}): Promise<void> {
    const r = await this.host.exportVsdx(opts);
    if (!r.ok) {
      if (r.error.code === 'compatibility_loss') {
        const d = r.error.details as { diagnostics?: DiagnosticItem[]; path?: string } | undefined;
        const path = d?.path;
        this.report = {
          title: 'Export to Visio', summary: r.error.message, diagnostics: d?.diagnostics ?? [],
          confirm: { label: 'Export anyway', run: () => { this.report = null; void this.exportVsdx({ acceptLoss: true, ...(path ? { path } : {}) }); } },
        };
        this.bump();
        return;
      }
      this.say(r.error.message, 'error');
      return;
    }
    if (r.value.cancelled) return;
    const items = r.value.diagnostics ?? [];
    this.say(`Exported ${r.value.path ?? ''} at revision ${r.value.revision}. ${r.value.summary ?? ''}`.trim(), r.value.lossy ? 'warning' : 'info');
    if (items.length) this.report = { title: 'Exported to Visio', summary: r.value.summary ?? '', diagnostics: items };
    this.bump();
  }

  closeReport(): void { this.report = null; this.bump(); }

  async save(saveAs = false): Promise<void> {
    const r = await this.host.saveJson(saveAs);
    if (!r.ok) { this.say(r.error.message, 'error'); return; }
    if (r.value.cancelled) return;
    this.say(`Saved ${r.value.path ?? ''}.`, 'info');
  }

  /** Called when the host replaced the document (open) and the engine started a new session. */
  sessionReplaced(): void {
    this.selection = [];
    this.adapter?.project(this.engine.store.snapshot());
    this.bump();
  }

  /** Unit-aware helpers for the inspector. */
  toUnit(pt: number): number { return pt / toPoints(1, this.unit); }
  fromUnit(value: number): number { return toPoints(value, this.unit); }

  pageOfElement(id: string) { return pageContaining(this.snapshot, id); }
  dispose(): void { this.detachCanvas(); this.projector.dispose(); }
}

function gestureMatches(kind: GestureKind, intent: Intent): boolean {
  switch (intent.type) {
    case 'move': return kind === 'move';
    case 'resize': return kind === 'resize';
    case 'rotate': return kind === 'rotate';
    case 'bend': return kind === 'bend';
    case 'reconnect': return kind === 'bend';
    case 'text': return kind === 'text';
    case 'connect': return false;
  }
}

/** New shapes snap to the grid like moved ones (page grid, absolute coordinates). */
function snapCreate(op: Operation, grid: { snap: boolean; spacingPt: number }): Operation {
  if (!grid.snap || (op.op !== 'createShape' && op.op !== 'createText')) return op;
  const g = grid.spacingPt;
  const s = (v: number) => Math.round(v / g) * g;
  const b = op.bounds;
  return { ...op, bounds: { x: s(b.x), y: s(b.y), width: Math.max(g, s(b.width)), height: Math.max(g, s(b.height)) } };
}

function diagnosticOf(details: Record<string, unknown> | undefined): { line?: number; column?: number; hint?: string } {
  const out: { line?: number; column?: number; hint?: string } = {};
  if (typeof details?.line === 'number') out.line = details.line;
  if (typeof details?.column === 'number') out.column = details.column;
  if (typeof details?.hint === 'string') out.hint = details.hint;
  return out;
}
