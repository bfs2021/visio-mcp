import { expect, it } from 'vitest';
import { searchDocument, replacementProposal, matchingSelection } from '../src/editor/findReplace';
import { createTestEngine, seedDocument, TEXT } from './support/engine';
import { id, run } from './support/ops';

const options={query:'Shape',caseSensitive:false,includeHidden:false};
it('finds literal text, names and aliases once per object in page order',()=>{
 const t=createTestEngine();const s=structuredClone(t.store.snapshot());const p=s.document.pages[0]!;
 p.elements[1]!.name='[Queue]';p.elements[1]!.alias='[queue]';if(p.elements[1]!.kind==='shape')p.elements[1]!.text!.value='[QUEUE] and café';
 const hits=searchDocument(s,{...options,query:'[queue]'});expect(hits).toHaveLength(1);expect(hits[0]).toMatchObject({id:t.ids.shape,fields:['text','name','alias']});
 expect(searchDocument(s,{...options,query:'[queue]',caseSensitive:true})[0]!.fields).toEqual(['alias']);
 expect(searchDocument(s,{...options,query:''})).toEqual([]);
 expect(searchDocument(s,{...options,query:'CAFÉ'})).toHaveLength(1);
});
it('excludes hidden content by default and reports hidden and locked matches explicitly',()=>{
 const t=createTestEngine();const s=structuredClone(t.store.snapshot());const p=s.document.pages[0]!;p.elements[1]!.hidden=true;p.elements[2]!.locked=true;
 expect(searchDocument(s,options).some(h=>h.id===t.ids.shape)).toBe(false);
 expect(searchDocument(s,{...options,includeHidden:true}).find(h=>h.id===t.ids.shape)).toMatchObject({hidden:true,replaceable:false});
 expect(searchDocument(s,options).find(h=>h.id===t.ids.shapes[1])).toMatchObject({locked:true,replaceable:false});
});
it('replaces literal text across pages as one undo while preserving formatting and references',async()=>{
 const t=createTestEngine();await run(t,[{op:'addPage',id:id(920),name:'Second'},{op:'createText',pageId:id(920),id:id(921),bounds:{x:10,y:20,width:100,height:30},text:{...TEXT,value:'Shape Shape',bold:true}}]);
 const before=t.store.snapshot();const proposal=replacementProposal(before,options,'Queue');expect(proposal.ok).toBe(true);if(!proposal.ok)return;
 expect(proposal.value.operations).toHaveLength(20);
 const applied=await t.engine.execute({...proposal.value,transactionId:id(922),atomic:true},'gui');expect(applied.ok).toBe(true);
 const after=t.store.snapshot();expect(after.document.pages[1]!.elements[0]).toMatchObject({id:id(921),text:{value:'Queue Queue',bold:true}});
 expect(after.document.pages[0]!.elements[1]).toMatchObject({alias:'s0',name:'Shape 0',bounds:before.document.pages[0]!.elements[1]!.bounds});
 expect((await t.engine.undo({...t.scope(),baseRevision:t.engine.revision,transactionId:id(923)})).ok).toBe(true);
 expect(t.store.snapshot().document.pages).toEqual(before.document.pages);
});
it('does not rename name-only matches and refuses oversized replacement batches',()=>{
 const seed=seedDocument(1002);const t=createTestEngine({document:seed.document});
 expect(replacementProposal(t.store.snapshot(),options,'New').ok).toBe(false);
 const s=structuredClone(t.store.snapshot());const el=s.document.pages[0]!.elements[1]!;if(el.kind==='shape')el.text!.value='Other';
 const p=replacementProposal(s,{...options,query:'Shape 0'},'New',el.id);expect(p.ok).toBe(true);if(p.ok)expect(p.value.operations).toEqual([]);
});
it('matches style without text value and exact normalized layer membership',()=>{
 const t=createTestEngine();const s=structuredClone(t.store.snapshot());const p=s.document.pages[0]!;const el=p.elements[1]!;
 expect(matchingSelection(p,el.id,'style').ids).toHaveLength(19);
 p.elements[2]!.locked=true;p.elements[3]!.hidden=true;
 expect(matchingSelection(p,el.id,'style').ids).toHaveLength(17);
 expect(matchingSelection(p,el.id,'layers').ids).toHaveLength(18);
});
