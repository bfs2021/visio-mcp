import type { CommandEngine } from '../commands/CommandEngine';
import { replacementProposal, searchDocument, type FindHit, type FindOptions } from './findReplace';

/** Current-tab search state; proposals always carry the snapshot's revision and scope. */
export class FindReplaceFlow {
  visible = false;
  focusVersion = 0;
  options: FindOptions = {query:'',caseSensitive:false,includeHidden:false};
  replacement = '';
  currentId: string | null = null;
  message = '';
  busy = false;
  canStart: () => boolean = () => true;
  private cache: {key:string;hits:FindHit[]} | null = null;
  constructor(private readonly engine: CommandEngine, private readonly changed: () => void, private readonly visit: (hit:FindHit) => void) {}
  open(): void { if(!this.canStart())return;this.visible=true;this.focusVersion++;this.changed(); }
  close(): void { this.visible=false;this.changed(); }
  setOptions(patch: Partial<FindOptions>): void { this.options={...this.options,...patch};this.currentId=null;this.message='';this.changed(); }
  get hits(): FindHit[] {
    const s=this.engine.store.snapshot();const key=JSON.stringify([s.documentId,s.sessionId,s.revision,this.options]);
    if(this.cache?.key!==key)this.cache={key,hits:searchDocument(s,this.options)};
    return this.cache.hits;
  }
  get current(): FindHit | undefined { return this.hits.find(h=>h.id===this.currentId)??this.hits[0]; }
  choose(id: string): void { const hit=this.hits.find(h=>h.id===id);if(!hit)return;this.currentId=id;this.visit(hit);this.changed(); }
  navigate(direction: number): void {
    const hits=this.hits;if(!hits.length)return;const i=hits.findIndex(h=>h.id===this.currentId);
    this.choose(hits[(i<0?(direction<0?hits.length-1:0):(i+direction+hits.length)%hits.length)]!.id);
  }
  async replace(all: boolean): Promise<void> {
    if(this.busy||!this.canStart())return;
    const current=this.current;
    if(!all&&!current)return;
    const proposal=replacementProposal(this.engine.store.snapshot(),this.options,this.replacement,all?undefined:current!.id);
    if(!proposal.ok){this.message=proposal.error.message;this.changed();return;}
    if(!proposal.value.operations.length){this.message='No editable text would change.';this.changed();return;}
    this.busy=true;this.changed();
    try {
      const result=await this.engine.execute({...proposal.value,transactionId:crypto.randomUUID(),atomic:true},'gui');
      this.message=result.ok?`Replaced text in ${proposal.value.operations.length} object(s).`:result.error.message;
      this.currentId=null;
    } finally { this.busy=false;this.changed(); }
  }
}
