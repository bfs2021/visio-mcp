import type { CommandEngine } from '../commands/CommandEngine';
import type { ExpectedDocument } from './NewDocumentFlow';
import type { Result } from '../model/result';
export interface DiskVersion { exists: boolean; sha256?: string; length?: number; lastWriteUtc?: string }
export interface FileConflict { token: string; path: string; expected: ExpectedDocument; baseline?: DiskVersion; observed?: DiskVersion; readError?: string }
export interface FileOutcome { cancelled?: true; path?: string; revision?: number; reloaded?: boolean; warning?: string; conflict?: FileConflict; unchanged?: boolean }
export interface FileHost {
  saveJson(saveAs: boolean, expected?: ExpectedDocument): Promise<Result<FileOutcome>>;
  fileCheck(expected: ExpectedDocument): Promise<Result<FileOutcome>>;
  fileResolve(token: string, action: 'reload' | 'saveAs' | 'overwrite', discard?: boolean): Promise<Result<FileOutcome>>;
}
export interface FilePrompt { conflict: FileConflict; busy: boolean; confirmReload: boolean; error: string | null; source: 'save' | 'focus' }
/** A tab owns its file choice, including a pending Save/Close. Focus checks queue notices without changing a drawing. */
export class FileConflictFlow {
  prompt: FilePrompt | null = null;
  notice: FileConflict | null = null;
  canPresent = () => true;
  completed: (outcome: FileOutcome) => void = () => {};
  message: (text: string, error: boolean) => void = () => {};
  private dismissed: string | null = null;
  private checking = false;
  private disposed = false;
  private saveTask: Promise<void> | null = null;
  private finishChoice: (() => void) | null = null;
  constructor(private readonly engine: CommandEngine, private readonly host: () => FileHost, private readonly changed: () => void) {}
  private expected(): ExpectedDocument { return { ...this.engine.scope(), baseRevision: this.engine.revision }; }
  private current(e: ExpectedDocument): boolean { const s=this.engine.scope(); return s.documentId===e.documentId && s.sessionId===e.sessionId && this.engine.revision===e.baseRevision; }
  private signature(c: FileConflict): string { return JSON.stringify([c.path,c.observed,c.readError]); }
  save(saveAs = false): Promise<void> {
    if (this.disposed) return Promise.resolve();
    if (!this.saveTask) this.saveTask=this.performSave(saveAs).finally(()=>{this.saveTask=null;});
    return this.saveTask;
  }
  private async performSave(saveAs: boolean): Promise<void> {
    this.notice=null;
    if(this.prompt?.source==='focus')this.prompt=null;
    this.changed();
    try {
      const r=await this.host().saveJson(saveAs,this.expected()); if(this.disposed)return;
      if(!r.ok){this.message(r.error.message,true);return;}
      if(r.value.conflict){
        const c=r.value.conflict; if(!this.current(c.expected)){this.message('The drawing changed. Save again to check the current version.',true);return;}
        await new Promise<void>(resolve=>{this.finishChoice=resolve;this.notice=null;this.prompt={conflict:c,busy:false,confirmReload:false,error:null,source:'save'};this.changed();});
      } else if(!r.value.cancelled) { this.notice=null;this.dismissed=null;this.completed(r.value); }
    } catch(e){ if(!this.disposed)this.message(e instanceof Error?e.message:'The file could not be saved.',true); }
  }
  async check(): Promise<void> {
    if(this.disposed||this.checking||this.prompt||this.saveTask)return;
    this.checking=true; const expected=this.expected();
    try {
      const r=await this.host().fileCheck(expected); if(this.disposed||!this.current(expected)||this.prompt||this.saveTask)return;
      if(!r.ok){this.message(r.error.message,true);return;}
      if(!r.value.conflict){this.dismissed=null;this.notice=null;return;}
      if(this.signature(r.value.conflict)===this.dismissed)return;
      this.notice=r.value.conflict; this.message('The file changed on disk. Review the file choices before saving.',false); this.changed();
    } catch(e){if(!this.disposed)this.message(e instanceof Error?e.message:'The file could not be checked.',true);}
    finally{this.checking=false;}
  }
  presentNotice(): void {
    if(this.disposed||this.saveTask||this.prompt||!this.notice||!this.canPresent())return;
    if(!this.current(this.notice.expected)){this.notice=null;return;}
    this.prompt={conflict:this.notice,busy:false,confirmReload:false,error:null,source:'focus'};this.notice=null;this.changed();
  }
  cancel(): void {
    if(this.prompt?.busy)return;
    if(this.prompt)this.dismissed=this.signature(this.prompt.conflict);
    this.prompt=null;this.notice=null;this.finishChoice?.();this.finishChoice=null;this.changed();
  }
  async choose(action: 'reload'|'saveAs'|'overwrite', discard=false): Promise<void> {
    const p=this.prompt;if(!p||p.busy)return;
    if(!this.current(p.conflict.expected)){p.error='The drawing changed while this choice was open. Cancel and Save again.';this.changed();return;}
    if(action==='reload'&&this.engine.status().unsaved&&!discard){p.confirmReload=true;this.changed();return;}
    p.busy=true;p.error=null;this.changed();
    try {
      const r=await this.host().fileResolve(p.conflict.token,action,discard);if(this.disposed)return;
      if(!r.ok){p.error=r.error.message;return;}
      if(r.value.conflict){p.conflict=r.value.conflict;p.confirmReload=false;p.error='The disk version changed again. Review it before choosing.';return;}
      if(r.value.cancelled){p.error='Save as was cancelled. Your drawing and the external file were kept.';return;}
      this.prompt=null;this.notice=null;this.dismissed=null;this.completed(r.value);this.finishChoice?.();this.finishChoice=null;
    }catch(e){p.error=e instanceof Error?e.message:'The file choice could not be completed.';}
    finally{p.busy=false;this.changed();}
  }
  dispose(): void {this.disposed=true;this.finishChoice?.();this.finishChoice=null;this.prompt=null;this.notice=null;}
}
