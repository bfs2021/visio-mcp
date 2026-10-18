import { EditorModel, noHost, type HostApi } from '../editor/EditorModel';
import type { DocumentState } from './Workspace';
import type { startApp } from '../bridge/bootstrap';

type App = ReturnType<typeof startApp>;

/** Soft limit: past this the person is told to close some. Each tab keeps its own history, so the cost is bounded but real. */
export const TAB_LIMIT = 20;

/**
 * The window's tabs: one `EditorModel` per open document, the active one shown. It owns what belongs to the window rather than a
 * document: which tab is active, opening, closing and switching, and the two ways a close can be asked (a tab, or the whole window).
 */
export class WorkspaceModel {
  private readonly listeners = new Set<() => void>();
  private version = 0;
  private untitled = 0;
  private hostApi: HostApi = noHost;
  private readonly unsubscribe = new Map<EditorModel, () => void>();
  /** Set by the page: gives a new tab its editor wiring (component previews, renderer, recovery hooks). */
  configure: (model: EditorModel, state: DocumentState) => void = () => {};
  /** A short message the strip shows (for example, the tab limit). */
  notice: string | null = null;

  constructor(private readonly app: App, first: EditorModel) {
    this.adopt(first, app.workspace.active!);
    // every document that appears in the workspace gets an editor, whoever opened it (the person, or an agent in the background)
    app.workspace.subscribe(e => {
      if (e.kind === 'opened' && !e.state.ui) {
        const model = new EditorModel(e.state.engine);
        this.adopt(model, e.state);
        this.configure(model, e.state);
      }
      this.bump();
    });
  }

  subscribe = (l: () => void) => { this.listeners.add(l); return () => { this.listeners.delete(l); }; };
  getVersion = () => this.version;
  bump(): void { this.version++; for (const l of this.listeners) l(); }

  get host(): HostApi { return this.hostApi; }
  set host(h: HostApi) { this.hostApi = h; for (const m of this.tabs) m.host = h; }

  /** Tabs in order. */
  get tabs(): EditorModel[] { return this.app.workspace.documents.map(d => d.ui as EditorModel).filter(Boolean); }
  get active(): EditorModel { return this.app.workspace.active!.ui as EditorModel; }

  private adopt(model: EditorModel, state: DocumentState): void {
    state.ui = model;
    model.untitledNo = ++this.untitled;
    model.host = this.hostApi;
    model.closeTabNow = discard => this.closeNow(model, discard);
    this.unsubscribe.set(model, model.subscribe(() => this.bump())); // what any tab shows can change the strip (its name, its unsaved dot)
  }

  private stateOf(model: EditorModel): DocumentState | null { return this.app.workspace.stateOfEngine(model.engine); }

  // ------------------------------------------------------------------ tabs
  /** A new untitled document in a new tab, which becomes the active one. Null if the tab limit was reached. */
  newTab(opts: { activate?: boolean } = {}): EditorModel | null {
    if (this.app.workspace.documents.length >= TAB_LIMIT) { this.notice = `You have ${TAB_LIMIT} documents open. Close some before opening more.`; this.bump(); return null; }
    this.notice = null;
    const state = this.app.openDocument(opts.activate === undefined ? {} : { activate: opts.activate });
    return state.ui as EditorModel;
  }

  activate(model: EditorModel): void {
    const s = this.stateOf(model);
    if (s && this.app.workspace.active !== s) { this.app.activateDocument(s.engine.scope().documentId); this.bump(); }
  }

  /** Steps to the next (or previous) tab, wrapping round. */
  step(delta: 1 | -1): void {
    const tabs = this.tabs;
    if (tabs.length < 2) return;
    const i = tabs.indexOf(this.active);
    this.activate(tabs[(i + delta + tabs.length) % tabs.length]!);
  }

  /** Opens a file in a tab: an untouched blank tab is used up first; opening a file that is already open shows that tab instead. */
  async openFile(kind: 'json' | 'vsdx' = 'json'): Promise<void> {
    const reuse = this.active.isBlank ? this.active : null;
    const target = reuse ?? this.newTab();
    if (!target) return;
    const outcome: { opened: boolean; path?: string } = kind === 'json' ? await target.open() : await target.openVsdx();
    if (!outcome.opened) { if (!reuse) await this.closeNow(target, true); return; }
    const path = outcome.path;
    if (path) {
      const existing = this.tabs.find(t => t !== target && sameFile(t.engine.status().savedPath, path));
      if (existing) { await this.closeNow(target, true); this.activate(existing); existing.say(`${path} is already open.`, 'info'); }
    }
    this.bump();
  }

  /** The person asked to close a tab. Unsaved work asks first. */
  async closeTab(model: EditorModel): Promise<void> {
    if (!this.tabs.includes(model)) return;
    this.activate(model);
    if (model.beginTabClose()) return; // the dialog asks; its answer comes back through closeTabNow
    await this.closeNow(model, false);
  }

  /** Closes a tab without asking. `discard` throws away its recovery session (Don't save); a closed unchanged tab leaves nothing behind either way. */
  async closeNow(model: EditorModel, discard: boolean): Promise<void> {
    const state = this.stateOf(model);
    if (!state) return;
    const scope = state.engine.scope();
    if (this.app.workspace.documents.length === 1) this.newTab(); // the window always shows a document
    this.app.closeDocument(scope.documentId);
    if (discard) await this.hostApi.recoveryDiscard(scope.sessionId);
    this.unsubscribe.get(model)?.(); this.unsubscribe.delete(model);
    model.dispose();
    this.bump();
  }

  // ------------------------------------------------------------------ closing the window
  /** Tabs with work nobody saved. */
  get unsavedTabs(): EditorModel[] { return this.tabs.filter(t => t.engine.status().unsaved); }

  /** The host wants to close the window. Nothing unsaved: it may. Otherwise one prompt covers every unsaved tab. */
  beginClose(): { dirty: boolean } {
    const dirty = this.unsavedTabs;
    if (dirty.length === 0) return { dirty: false };
    const first = dirty[0]!;
    this.activate(first);
    first.saveAllForClose = () => this.saveAll();
    return first.beginClose(dirty.length);
  }

  /** Saves each unsaved tab in turn, showing it; stops at the first that does not save (a cancelled Save as). */
  private async saveAll(): Promise<boolean> {
    for (const t of this.unsavedTabs) {
      this.activate(t);
      await t.save(false);
      if (t.engine.status().unsaved) return false;
    }
    return true;
  }
}

const norm = (p: string | null | undefined) => (p ?? '').replace(/\//g, '\\').toLowerCase();
const sameFile = (a: string | null | undefined, b: string | null | undefined) => !!a && !!b && norm(a) === norm(b);
