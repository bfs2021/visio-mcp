import { describe, expect, it } from 'vitest';
import { CommandEngine } from '../src/commands/CommandEngine';
import { newDocument } from '../src/model/newDocument';
import { FileConflictFlow, type FileConflict, type FileHost, type FileOutcome } from '../src/editor/FileConflictFlow';
import { ok } from '../src/model/result';
function setup() {
  const engine=new CommandEngine({document:newDocument()});
  const conflict:FileConflict={token:'a',path:'C:/shared.json',expected:{...engine.scope(),baseRevision:0},observed:{exists:true,sha256:'b',length:10}};
  const host:FileHost={saveJson:async()=>ok({conflict}),fileCheck:async()=>ok({conflict}),fileResolve:async()=>ok({path:'C:/shared.json'})};
  const flow=new FileConflictFlow(engine,()=>host,()=>{});return{engine,conflict,host,flow};
}
const tick=async()=>{await Promise.resolve();await Promise.resolve();};
describe('file conflict lifecycle',()=>{
  it('keeps Save pending until a choice succeeds or Cancel is chosen',async()=>{
    const {flow,engine}=setup();const before=engine.store.snapshot();let done=false;const saving=flow.save().then(()=>{done=true;});await tick();expect(done).toBe(false);expect(flow.prompt).not.toBeNull();flow.cancel();await saving;expect(engine.store.snapshot()).toEqual(before);
  });
  it('keeps the choice pending after cancelled Save as and a changed disk version',async()=>{
    const {flow,host,conflict}=setup();let completed=0;flow.completed=()=>completed++;const save=flow.save();await tick();
    host.fileResolve=async()=>ok({cancelled:true});await flow.choose('saveAs');expect(flow.prompt?.error).toContain('cancelled');expect(completed).toBe(0);
    host.fileResolve=async()=>ok({conflict:{...conflict,token:'next'}});await flow.choose('overwrite');expect(flow.prompt?.conflict.token).toBe('next');
    host.fileResolve=async()=>ok({path:'C:/saved.json'});await flow.choose('overwrite');await save;expect(completed).toBe(1);expect(flow.prompt).toBeNull();
  });
  it('queues focus detection during another action and deduplicates a cancelled observed version',async()=>{
    const {flow}=setup();flow.canPresent=()=>false;await flow.check();flow.presentNotice();expect(flow.prompt).toBeNull();expect(flow.notice).not.toBeNull();flow.canPresent=()=>true;flow.presentNotice();expect(flow.prompt?.source).toBe('focus');flow.cancel();await flow.check();expect(flow.notice).toBeNull();
  });
  it('ignores a stale focus response after document identity changes',async()=>{
    const {flow,host,engine,conflict}=setup();let release!:(v:ReturnType<typeof ok<FileOutcome>>)=>void;host.fileCheck=()=>new Promise(resolve=>{release=resolve;});const checking=flow.check();await engine.replaceDocument(newDocument(),{...engine.scope(),baseRevision:0});release(ok({conflict}));await checking;expect(flow.notice).toBeNull();expect(flow.prompt).toBeNull();
  });
  it('disposal releases a waiting Save and ignores its later action result',async()=>{
    const {flow}=setup();let completed=false;flow.completed=()=>{completed=true;};const save=flow.save();await tick();flow.dispose();await save;expect(completed).toBe(false);expect(flow.prompt).toBeNull();
  });
});
it('successful Save clears a deferred focus notice and cannot present it during the native save',async()=>{
 const {flow,host}=setup();flow.canPresent=()=>false;await flow.check();expect(flow.notice).not.toBeNull();
 let release!:(v:ReturnType<typeof ok<FileOutcome>>)=>void;host.saveJson=()=>new Promise(resolve=>{release=resolve;});
 const save=flow.save();flow.canPresent=()=>true;flow.presentNotice();expect(flow.prompt).toBeNull();release(ok({path:'C:/saved.json'}));await save;expect(flow.notice).toBeNull();
});
