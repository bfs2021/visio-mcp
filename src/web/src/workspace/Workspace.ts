import type { CommandEngine } from '../commands/CommandEngine';
import type { Scope } from '../contracts/generated';

/**
 * One open document: its engine (history, revision, saved state) and the part of the screen that shows it. The page's
 * `EditorModel` is bound to one of these; everything the editor remembers about a single document hangs off it.
 */
export interface DocumentState<Ui = unknown> {
  readonly engine: CommandEngine;
  /** The editor model bound to this document, once the app shell has made one. */
  ui: Ui | null;
  /** The document id the host was last told about, so a restart can say which document it replaced. */
  announcedId: string;
}

export type WorkspaceEvent =
  | { kind: 'opened'; state: DocumentState }
  | { kind: 'closed'; documentId: string }
  | { kind: 'activated'; state: DocumentState };

/**
 * The documents open in this page, in tab order, and which one is active. It is the one place that maps a document id to its
 * engine, so a request from the host or an agent reaches the document it names whatever the person is looking at.
 */
export class Workspace<Ui = unknown> {
  private readonly states: Array<DocumentState<Ui>> = [];
  private activeState: DocumentState<Ui> | null = null;
  private readonly listeners = new Set<(e: WorkspaceEvent) => void>();

  get documents(): ReadonlyArray<DocumentState<Ui>> { return this.states; }
  get active(): DocumentState<Ui> | null { return this.activeState; }

  /** Adds a document at the end. It becomes the active one unless `activate` is false (an agent's document, opened in the background). */
  add(engine: CommandEngine, opts: { activate?: boolean } = {}): DocumentState<Ui> {
    const state: DocumentState<Ui> = { engine, ui: null, announcedId: engine.scope().documentId };
    this.states.push(state);
    if (opts.activate !== false || !this.activeState) this.activeState = state;
    this.emit({ kind: 'opened', state: state as DocumentState });
    return state;
  }

  find(documentId: string | undefined): DocumentState<Ui> | null {
    if (documentId === undefined) return this.activeState;
    return this.states.find(s => s.engine.scope().documentId === documentId) ?? null;
  }

  /** The engine of the named document, or of the active one when none is named. */
  engineOf = (documentId: string | undefined): CommandEngine | null => this.find(documentId)?.engine ?? null;

  scopeOf(documentId: string | undefined): Scope | null { return this.engineOf(documentId)?.scope() ?? null; }

  stateOfEngine(engine: CommandEngine): DocumentState<Ui> | null { return this.states.find(s => s.engine === engine) ?? null; }

  isActive = (documentId: string | undefined): boolean =>
    documentId === undefined || (this.activeState !== null && this.activeState.engine.scope().documentId === documentId);

  activate(documentId: string): boolean {
    const s = this.find(documentId);
    if (!s) return false;
    if (s !== this.activeState) { this.activeState = s; this.emit({ kind: 'activated', state: s as DocumentState }); }
    return true;
  }

  /** Removes a document. If it was active, the neighbour to its left (or the first) takes over. */
  close(documentId: string): boolean {
    const i = this.states.findIndex(s => s.engine.scope().documentId === documentId);
    if (i < 0) return false;
    const [gone] = this.states.splice(i, 1);
    this.emit({ kind: 'closed', documentId: gone!.announcedId });
    if (this.activeState === gone) {
      this.activeState = this.states[Math.max(0, i - 1)] ?? null;
      if (this.activeState) this.emit({ kind: 'activated', state: this.activeState as DocumentState });
    }
    return true;
  }

  /**
   * A document's engine started a new session (open, import, recovery). Returns the id the host knew it by, to say what it replaced,
   * and remembers the new one.
   */
  restarted(state: DocumentState<Ui>): string {
    const previous = state.announcedId;
    state.announcedId = state.engine.scope().documentId;
    return previous;
  }

  subscribe(fn: (e: WorkspaceEvent) => void): () => void {
    this.listeners.add(fn);
    return () => { this.listeners.delete(fn); };
  }

  private emit(e: WorkspaceEvent): void { for (const l of [...this.listeners]) l(e); }
}
