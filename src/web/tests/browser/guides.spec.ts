import { mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { test, expect } from '@playwright/test';
import { openEditor, ptToClient, snapshot, drag, revision, pngFixture } from './support';
test('ruler guides create, move, cancel, delete and undo without altering shapes',async({page})=>{
 await openEditor(page); const before=await snapshot(page);
 const ruler=await page.getByTestId('ruler-h').boundingBox();
 const target=await ptToClient(page,{x:100,y:100});
 await drag(page,{x:ruler!.x+100,y:ruler!.y+10},target);
 await expect.poll(async()=> (await snapshot(page)).document.pages[0]!.guides.length).toBe(1);
 const g=(await snapshot(page)).document.pages[0]!.guides[0]!;
 const evidence=fileURLToPath(new URL('../../../../artifacts/w10-guides/',import.meta.url));mkdirSync(evidence,{recursive:true});await page.screenshot({path:evidence+'/ruler-guide.png'});
 expect(g.orientation).toBe('horizontal');expect(g.positionPt).toBeCloseTo(100,0);
 expect((await snapshot(page)).document.pages[0]!.elements).toEqual(before.document.pages[0]!.elements);
 const guide=page.getByTestId('guide-'+g.id); const box=await guide.boundingBox();
 const unchanged=await revision(page);await page.mouse.click(box!.x+40,box!.y+3);expect(await revision(page)).toBe(unchanged);
 await drag(page,{x:box!.x+40,y:box!.y},await ptToClient(page,{x:100,y:140}));
 await expect.poll(async()=>(await snapshot(page)).document.pages[0]!.guides[0]!.positionPt).toBeCloseTo(140,0);
 const rev=await revision(page);const moved=await guide.boundingBox();
 await page.mouse.move(moved!.x+40,moved!.y+3);await page.mouse.down();await page.mouse.move(target.x,target.y);await page.keyboard.press('Escape');await page.mouse.up();
 expect(await revision(page)).toBe(rev);
 await page.getByRole('button',{name:'View',exact:true}).click();await page.getByTestId('menu-guides').click();
 await expect(guide).toHaveCount(0);
 await page.getByRole('button',{name:'View',exact:true}).click();await page.getByTestId('menu-guides').click();
 const shown=await guide.boundingBox();await drag(page,{x:shown!.x+40,y:shown!.y+3},{x:ruler!.x+100,y:ruler!.y+10});
 await expect.poll(async()=>(await snapshot(page)).document.pages[0]!.guides.length).toBe(0);
 await page.keyboard.press('Control+z');await expect.poll(async()=>(await snapshot(page)).document.pages[0]!.guides[0]!.id).toBe(g.id);
});

test('authored guides win over a coarse grid in a real canvas drag',async({page})=>{
 await openEditor(page);
 await page.evaluate(()=>(window as any).__editor.agentApply([
 {op:'setPage',patch:{grid:{snap:true,spacingPt:20},guides:[{id:'00000000-0000-4000-8000-000000008880',orientation:'vertical',positionPt:110}]}},
 {op:'createShape',id:'00000000-0000-4000-8000-000000008881',bounds:{x:100,y:100,width:80,height:60}}
 ]));
 const from=await ptToClient(page,{x:140,y:130});const to=await ptToClient(page,{x:149,y:130});
 await drag(page,from,to);
 await expect.poll(async()=>(await snapshot(page)).document.pages[0]!.elements[0]!.bounds.x).toBeCloseTo(110,3);
});

test('picture placement uses page guides without changing its natural dimensions',async({page})=>{
 await openEditor(page);const hash='a'.repeat(64);const png=pngFixture();await page.route('**/assets/'+hash,r=>r.fulfill({contentType:'image/png',body:png.bytes}));
 const pg=(await snapshot(page)).document.pages[0]!;const guide=pg.widthPt/2+4;
 await page.evaluate(({hash,guide})=>(window as any).__editor.agentApply([
 {op:'registerAsset',asset:{id:'asset:guide-picture',name:'Picture',mimeType:'image/png',sha256:hash,tags:[]}},
 {op:'setPage',patch:{grid:{snap:false},guides:[{id:'00000000-0000-4000-8000-000000008882',orientation:'vertical',positionPt:guide}]}}
 ]),{hash,guide});
 await page.locator('[data-testid="asset-row"][data-scope="document"]').click();await page.getByTestId('asset-place').click();
 await expect.poll(async()=>{const e=(await snapshot(page)).document.pages[0]!.elements[0]!;return e?e.bounds.x+e.bounds.width/2:0;}).toBeCloseTo(guide,4);
 expect((await snapshot(page)).document.pages[0]!.elements[0]!.bounds.width).toBe(96);
});

test('vector icon placement snaps to a guide and retains the chosen size',async({page})=>{
 await openEditor(page);await page.evaluate(()=>(window as any).__editor.agentApply([{op:'setPage',patch:{guides:[{id:'00000000-0000-4000-8000-000000008883',orientation:'vertical',positionPt:164}]}}]));
 await page.getByLabel('Icon search',{exact:true}).fill('git branch');await page.getByRole('button',{name:'Icon: git branch',exact:true}).click();await page.getByLabel('Icon size (pt)',{exact:true}).fill('36');
 const at=await ptToClient(page,{x:160,y:150});await page.mouse.click(at.x,at.y);
 await expect.poll(async()=>{const e=(await snapshot(page)).document.pages[0]!.elements[0];return e?e.bounds.x+e.bounds.width/2:0;}).toBeCloseTo(164,3);
 expect((await snapshot(page)).document.pages[0]!.elements[0]!.bounds.width).toBe(36);
});

test('vertical ruler conversion survives zoom and scroll and clean renders omit guides',async({page})=>{
 await openEditor(page);await page.getByTestId('tool-zoom-in').click();await page.getByTestId('tool-zoom-in').click();
 await page.getByTestId('canvas').evaluate(el=>{el.scrollTop=80;});
 const ruler=await page.getByTestId('ruler-v').boundingBox();const at=await ptToClient(page,{x:150,y:150});
 await drag(page,{x:ruler!.x+10,y:ruler!.y+80},at);
 const snap=await snapshot(page);const g=snap.document.pages[0]!.guides[0]!;expect(g.orientation).toBe('vertical');expect(g.positionPt).toBeCloseTo(150,0);
 const rendered=await page.evaluate(()=>(window as any).__editor.callTool('doc.render',{scope:'page',maxWidth:600,maxHeight:600}));expect(rendered.error).toBeUndefined();
 const blue=await page.evaluate(async(base64)=>{const im=new Image();im.src='data:image/png;base64,'+base64;await im.decode();const c=document.createElement('canvas');c.width=im.width;c.height=im.height;const ctx=c.getContext('2d')!;ctx.drawImage(im,0,0);const d=ctx.getImageData(0,0,c.width,c.height).data;let blue=0;for(let i=0;i<d.length;i+=4)if(d[i]!<100&&d[i+2]!>150)blue++;return blue;},rendered.result.base64);
 expect(blue).toBe(0);
 const box=await page.getByTestId('guide-'+g.id).boundingBox();const before=await revision(page);
 await drag(page,{x:box!.x,y:box!.y+80},{x:ruler!.x+2,y:ruler!.y-5});expect(await revision(page)).toBe(before);
});
