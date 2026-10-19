import { expect, it } from 'vitest';
import { FindReplaceFlow } from '../src/editor/FindReplaceFlow';
import { createTestEngine } from './support/engine';
import { id, run } from './support/ops';
it('opens without editing, navigates with wrap and replaces current as one pinned edit',async()=>{
 const t=createTestEngine();const visits:string[]=[];const f=new FindReplaceFlow(t.engine,()=>{},hit=>visits.push(hit.id));
 f.open();f.setOptions({query:'Shape'});expect(t.engine.revision).toBe(0);
 f.navigate(1);expect(visits.at(-1)).toBe(t.ids.shape);f.navigate(-1);expect(visits.at(-1)).toBe(t.ids.shapes.at(-1));
 f.replacement='Queue';await f.replace(false);expect(t.engine.revision).toBe(1);
 expect(t.store.snapshot().document.pages[0]!.elements.filter(e=>'text' in e&&e.text?.value.startsWith('Queue'))).toHaveLength(1);
 f.close();expect(t.engine.revision).toBe(1);
});
it('blocks replacement during a gesture and refreshes results after an agent edit',async()=>{
 const t=createTestEngine();const f=new FindReplaceFlow(t.engine,()=>{},()=>{});f.open();f.setOptions({query:'Shape 0'});f.replacement='Queue';
 f.canStart=()=>false;await f.replace(true);expect(t.engine.revision).toBe(0);
 f.canStart=()=>true;await run(t,[{op:'set',target:t.ids.shape,patch:{text:{value:'Updated'},name:'Updated'}}]);
 expect(f.hits).toEqual([]);await f.replace(true);expect(t.engine.revision).toBe(1);
});
it('a captured replacement proposal cannot overwrite a later lock change',async()=>{
 const {replacementProposal}=await import('../src/editor/findReplace');const t=createTestEngine();
 const p=replacementProposal(t.store.snapshot(),{query:'Shape',caseSensitive:false,includeHidden:false},'New');expect(p.ok).toBe(true);if(!p.ok)return;
 await run(t,[{op:'set',target:t.ids.shape,patch:{locked:true}}]);
 const result=await t.engine.execute({...p.value,transactionId:id(950),atomic:true},'gui');expect(result.ok).toBe(false);
 expect(t.store.snapshot().document.pages[0]!.elements[1]).toMatchObject({text:{value:'Shape 0'},locked:true});
});
