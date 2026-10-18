import type { DiagramDocument, Scope } from '../contracts/generated';
import type { CommandEngine } from '../commands/CommandEngine';
import { newDocument, type PageSize } from '../model/newDocument';
import type { Result } from '../model/result';

export type ExpectedDocument = Scope & { baseRevision: number };
export interface NewPrompt { stage: 'size' | 'confirm'; size: PageSize; expected: ExpectedDocument; saving: boolean; error: string | null }

/** One revision-pinned lifecycle action, shared by menu and keyboard; a workspace may replace create with its new-tab action. */
export class NewDocumentFlow {
  prompt: NewPrompt | null = null;
  canStart = () => true;
  save = async () => {};
  created: (result: unknown) => void = () => {};
  create: (document: DiagramDocument, expected: ExpectedDocument, discard: boolean) => Promise<Result<unknown>>;
  constructor(private readonly engine: CommandEngine, private readonly changed = () => {}) {
    this.create = (document, expected) => engine.replaceDocument(document, expected);
  }
  begin(): void {
    if (this.prompt || !this.canStart()) return;
    const p = newDocument().pages[0]!;
    this.prompt = { stage: 'size', size: { widthPt: p.widthPt, heightPt: p.heightPt }, expected: { ...this.engine.scope(), baseRevision: this.engine.revision }, saving: false, error: null };
    this.changed();
  }
  cancel(): void { if (!this.prompt?.saving) { this.prompt = null; this.changed(); } }
  private current(p: NewPrompt): boolean {
    const s = this.engine.scope();
    if (s.documentId === p.expected.documentId && s.sessionId === p.expected.sessionId && this.engine.revision === p.expected.baseRevision && this.canStart()) return true;
    p.error = 'The drawing changed while New was open. Cancel and try again to keep the newer work.'; this.changed(); return false;
  }
  async choose(size: PageSize): Promise<void> {
    const p = this.prompt; if (!p || p.saving || p.stage !== 'size') return;
    try { newDocument(undefined, size); } catch { p.error = 'Enter page dimensions greater than zero and no larger than 14,400 pt.'; this.changed(); return; }
    if (!this.current(p)) return;
    p.size = size; p.error = null;
    if (this.engine.status().unsaved) { p.stage = 'confirm'; this.changed(); } else await this.finish(p, false);
  }
  async discard(): Promise<void> { const p = this.prompt; if (p?.stage === 'confirm' && !p.saving) await this.finish(p, true); }
  async saveAndCreate(): Promise<void> {
    const p = this.prompt; if (!p || p.saving || p.stage !== 'confirm' || !this.current(p)) return;
    p.saving = true; p.error = null; this.changed();
    try {
      await this.save();
      if (!this.current(p)) return;
      if (this.engine.status().unsaved) { p.error = 'The changes were not saved, so the drawing was kept.'; return; }
      await this.finish(p, false);
    } catch (e) { p.error = e instanceof Error ? e.message : 'The drawing could not be saved.'; }
    finally { p.saving = false; this.changed(); }
  }
  private async finish(p: NewPrompt, discard: boolean): Promise<void> {
    if (!this.current(p)) return;
    p.saving = true; p.error = null; this.changed();
    try {
      const r = await this.create(newDocument(undefined, p.size), p.expected, discard);
      if (!r.ok) { p.error = r.error.message; return; }
      this.prompt = null; this.created(r.value);
    } catch (e) { p.error = e instanceof Error ? e.message : 'The new drawing could not be created.'; }
    finally { p.saving = false; this.changed(); }
  }
}

