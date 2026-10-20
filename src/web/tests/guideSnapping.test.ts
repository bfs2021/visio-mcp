import { expect, it } from 'vitest';
import { moveOperations, resizeOperations } from '../src/canvas/intents';
import { createTestEngine, seedDocument } from './support/engine';
import { id } from './support/ops';

function fixture() {
 const s=seedDocument(); const p=s.document.pages[0]!; p.grid.snap=false;
 p.guides=[{id:id(860),orientation:'vertical',positionPt:115},{id:id(861),orientation:'horizontal',positionPt:45}];
 return createTestEngine({document:s.document});
}
it('snaps a rigid selection to guides with grid disabled',()=>{
 const t=fixture(); const ops=moveOperations(t.store.snapshot(),t.ids.shapes.slice(0,2),10,2,{enabled:true,scale:1});
 expect(ops).toEqual(t.ids.shapes.slice(0,2).map(target=>({op:'move',target,delta:{xPt:15,yPt:5}})));
});
it('uses an eight screen pixel threshold and disables guides when hidden',()=>{
 const t=fixture();const s=t.store.snapshot(); const ids=[t.ids.shape];
 expect(moveOperations(s,ids,10,0,{enabled:true,scale:2})).toEqual([{op:'move',target:ids[0],delta:{xPt:10,yPt:0}}]);
 expect(moveOperations(s,ids,10,2,{enabled:false,scale:1})).toEqual([{op:'move',target:ids[0],delta:{xPt:10,yPt:2}}]);
});
it('does not guide-snap any member when a selected layer forbids snapping',()=>{
 const t=fixture(); const s=structuredClone(t.store.snapshot());s.document.pages[0]!.layers[0]!.snap=false;
 expect(moveOperations(s,[t.ids.shape],10,2,{enabled:true,scale:1})).toEqual([{op:'move',target:t.ids.shape,delta:{xPt:10,yPt:2}}]);
});
it('snaps a changed resize edge while keeping the opposite edge fixed',()=>{
 const t=fixture();const s=structuredClone(t.store.snapshot());s.document.pages[0]!.guides=[{id:id(862),orientation:'vertical',positionPt:185}];
 expect(resizeOperations(s,t.ids.shape,{x:100,y:40,width:82,height:50},{enabled:true,scale:1})).toEqual([{op:'resize',target:t.ids.shape,bounds:{x:100,y:40,width:85,height:50}}]);
});

it('snaps creation to guides but respects destination layer snapping',async()=>{
 const { snapCreation }=await import('../src/canvas/guideSnapping');
 const t=fixture();const page=structuredClone(t.store.snapshot().document.pages[0]!);
 const op={op:'createShape' as const,bounds:{x:110,y:42,width:100,height:60},geometry:{preset:'rectangle' as const},layerIds:[t.ids.layer]};
 expect(snapCreation(op,page,{enabled:true,scale:1})).toMatchObject({bounds:{x:115,y:45,width:100,height:60}});
 page.layers[0]!.snap=false;
 expect(snapCreation(op,page,{enabled:true,scale:1})).toEqual(op);
});

it('aligns rotated visual bounds and keeps group selection rigid',()=>{
 const t=fixture(); const s=structuredClone(t.store.snapshot());const el=s.document.pages[0]!.elements.find(e=>e.id===t.ids.shape)!;
 el.rotationDeg=90;s.document.pages[0]!.guides=[{id:id(870),orientation:'vertical',positionPt:135}];
 expect(moveOperations(s,[el.id],10,0,{enabled:true,scale:1})).toEqual([{op:'move',target:el.id,delta:{xPt:15,yPt:0}}]);
});
it('uses deterministic nearest guide ties independent of array order',()=>{
 const t=fixture();const s=structuredClone(t.store.snapshot());const p=s.document.pages[0]!;
 p.guides=[{id:id(871),orientation:'vertical',positionPt:114},{id:id(872),orientation:'vertical',positionPt:106}];
 const want=[{op:'move',target:t.ids.shape,delta:{xPt:6,yPt:0}}];
 expect(moveOperations(s,[t.ids.shape],10,0,{enabled:true,scale:1})).toEqual(want);
 p.guides.reverse();expect(moveOperations(s,[t.ids.shape],10,0,{enabled:true,scale:1})).toEqual(want);
});

it('keeps the chosen guide aligned after grid dimensions round during creation',async()=>{
 const {snapCreation}=await import('../src/canvas/guideSnapping');const t=fixture();const p=structuredClone(t.store.snapshot().document.pages[0]!);
 p.grid={visible:true,snap:true,spacingPt:10};p.guides=[{id:id(890),orientation:'vertical',positionPt:142.5}];
 const result=snapCreation({op:'createShape',bounds:{x:101,y:80,width:83,height:43}},p,{enabled:true,scale:1});
 expect(result).toMatchObject({bounds:{x:102.5,y:80,width:80,height:40}});
});

it('aligns rotated resizing against final bounds after the other axis grid-snaps',()=>{
 const t=fixture();const s=structuredClone(t.store.snapshot());const p=s.document.pages[0]!;const el=p.elements.find(e=>e.id===t.ids.shape)!;
 el.bounds={x:100,y:40,width:80,height:50};el.rotationDeg=90;p.grid={visible:true,snap:true,spacingPt:10};p.guides=[{id:id(891),orientation:'vertical',positionPt:115}];
 expect(resizeOperations(s,el.id,{x:100,y:40,width:83,height:53},{enabled:true,scale:1})).toEqual([{op:'resize',target:el.id,bounds:{x:98.5,y:40,width:83,height:50}}]);
});

it('preserves picture dimensions with a coarse grid while snapping its centre',async()=>{
 const {snapCreation}=await import('../src/canvas/guideSnapping');const t=fixture();const p=structuredClone(t.store.snapshot().document.pages[0]!);
 p.grid={visible:true,snap:true,spacingPt:20};p.guides=[{id:id(892),orientation:'vertical',positionPt:150}];
 expect(snapCreation({op:'createImage',assetId:'asset:logo',bounds:{x:100,y:100,width:96,height:71}},p,{enabled:true,scale:1})).toMatchObject({bounds:{x:102,y:100,width:96,height:71}});
});
