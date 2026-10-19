import { expect, it } from 'vitest';
import { createTestEngine } from './support/engine';
import { id, run } from './support/ops';

it('edits page guides atomically and restores stable IDs in one undo', async () => {
  const t = createTestEngine();
  const before = t.store.snapshot().document.pages[0]!;
  const guides = [{ id: id(850), orientation: 'horizontal' as const, positionPt: 72 }];
  await run(t, [{ op: 'setPage', patch: { guides } }]);
  expect(t.store.snapshot().document.pages[0]!.guides).toEqual(guides);
  expect(t.store.snapshot().document.pages[0]!.elements).toEqual(before.elements);
  const undo = await t.engine.undo({ ...t.scope(), transactionId: id(851), baseRevision: t.engine.revision });
  expect(undo.ok).toBe(true);
  expect(t.store.snapshot().document.pages[0]!.guides).toEqual([]);
});

it('rejects duplicate guide identities and preserves the original revision', async () => {
 const t=createTestEngine(); const g={id:id(852),orientation:'vertical' as const,positionPt:-5};
 const r=await t.engine.execute(t.request([{op:'setPage',patch:{guides:[g,g]}}]), 'gui');
 expect(r.ok).toBe(false); expect(t.engine.revision).toBe(0);
});
it('rejects invalid guide batches and stale guide arrays without a partial edit', async () => {
 const t=createTestEngine(); const g={id:id(853),orientation:'vertical' as const,positionPt:-5};
 for(const guides of [[{...g,positionPt:Infinity}],Array.from({length:257},(_,i)=>({...g,id:id(1000+i)}))]) {
  expect((await t.engine.execute(t.request([{op:'setPage',patch:{guides}}]), 'gui')).ok).toBe(false);
 }
 const stale=t.request([{op:'setPage',patch:{guides:[g]}}]);
 await run(t,[{op:'setPage',patch:{name:'Changed'}}]);
 expect((await t.engine.execute(stale, 'gui')).ok).toBe(false);
 expect(t.store.snapshot().document.pages[0]!.guides).toEqual([]);
});

it('discards a stale guide gesture instead of overwriting a newer guide edit',async()=>{
 const {GestureController}=await import('../src/canvas/GestureController');const t=createTestEngine();
 const controller=new GestureController(t.engine,()=>id(880));const notices:boolean[]=[];controller.onGestureState=a=>notices.push(a);
 const token=controller.begin('guide',t.scope(),[]);
 const g={id:id(881),orientation:'vertical' as const,positionPt:100};
 expect(controller.preview(token,[{op:'setPage',patch:{guides:[g]}}]).ok).toBe(true);
 await run(t,[{op:'setPage',patch:{guides:[{...g,positionPt:200}]}}]);
 const result=await controller.commit(token);expect(result.ok).toBe(false);
 expect(t.store.snapshot().document.pages[0]!.guides[0]!.positionPt).toBe(200);
 expect(notices).toEqual([true,false]);
});
