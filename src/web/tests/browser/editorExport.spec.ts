import {test,expect} from '@playwright/test';
import {openEditor,placeShape,snapshot} from './support';
test('export menu options, cancelled destinations and copy preserve the drawing',async({page})=>{
 await openEditor(page);await placeShape(page,'shape-rectangle',{x:200,y:150},'exported');const before=await snapshot(page);
 await page.evaluate(()=>{(window as any).__editor.setHost({editorExport:async(p:any)=>{(window as any).__exportRequest=p;return {ok:true,value:{cancelled:true}};}});});
 await page.getByTestId('menu-file').click();await page.getByTestId('menu-export-image').click();
 await expect(page.getByRole('dialog',{name:'Export image'})).toBeVisible();
 await page.getByLabel('Format',{exact:true}).selectOption('jpeg');await expect(page.getByRole('option',{name:'Transparent',exact:true})).toHaveAttribute('disabled','');
 await page.getByLabel('Pages or selection').selectOption('all');await expect(page.getByText('All pages will be saved in one ZIP file.')).toBeVisible();
 await page.getByRole('button',{name:'Export',exact:true}).click();await expect(page.getByRole('dialog')).toHaveCount(0);
 expect(await snapshot(page)).toEqual(before);const request=await page.evaluate(()=>(window as any).__exportRequest);expect(request).toMatchObject({format:'jpeg',scope:'all',scale:1,expected:{documentId:before.documentId,sessionId:before.sessionId,baseRevision:before.revision}});
 await page.getByTestId('menu-edit').click();await page.getByTestId('menu-copy-image').click();await expect.poll(()=>page.evaluate(()=>(window as any).__exportRequest?.copy)).toBe(true);expect(await snapshot(page)).toEqual(before);
});
test('selection export excludes unselected overlapping objects and keeps transparent pixels',async({page})=>{
 await openEditor(page);const ids=['00000000-0000-4000-8000-000000009001','00000000-0000-4000-8000-000000009002'];
 await page.evaluate(async ids=>{await (window as any).__editor.agentApply(ids.map((id:string,i:number)=>({op:'createShape',id,bounds:{x:100,y:100,width:180,height:80},text:{value:i?'DO NOT COPY':'SELECTED'},style:{fill:{kind:'solid',colour:i?'#ff0000':'#00ff00',opacity:1}}})));},ids);
 const r=await page.evaluate(async ids=>await (window as any).__editor.callTool('doc.render',{scope:'region',elementIds:[ids[0]],selectionOnly:true,format:'svg',background:'transparent',scale:1}),ids);
 expect(r.error).toBeUndefined();const svg=Buffer.from(r.result.base64,'base64').toString();expect(svg).toContain('SELECTED');expect(svg).not.toContain('DO NOT COPY');
});
test('page background opacity is preserved and white/transparent overrides are explicit',async({page})=>{
 await openEditor(page);const snap=await snapshot(page);
 const applied=await page.evaluate(async id=>await (window as any).__editor.agentApply([{op:'setPage',pageId:id,patch:{background:{kind:'solid',colour:'#ff0000',opacity:0.25}}}]),snap.document.pages[0]!.id);expect(applied.error).toBeUndefined();
 async function corner(background:string){return page.evaluate(async bg=>{const r=await (window as any).__editor.callTool('doc.render',{scope:'page',format:'png',scale:1,background:bg});if(r.error)throw Error(r.error.message);const image=new Image();image.src='data:image/png;base64,'+r.result.base64;await image.decode();const c=document.createElement('canvas');c.width=image.naturalWidth;c.height=image.naturalHeight;const g=c.getContext('2d')!;g.drawImage(image,0,0);return Array.from(g.getImageData(10,10,1,1).data);},background);}
 expect((await corner('page'))[3]).toBeGreaterThanOrEqual(63);expect((await corner('page'))[3]).toBeLessThanOrEqual(65);expect(await corner('transparent')).toEqual([0,0,0,0]);expect(await corner('white')).toEqual([255,255,255,255]);
});
test('busy Copy keeps focus inside its modal and does not activate the canvas shortcuts',async({page})=>{
 await openEditor(page);await placeShape(page,'shape-rectangle',{x:200,y:150},'copy');const before=await snapshot(page);
 await page.evaluate(()=>{(window as any).__editor.setHost({editorExport:()=>new Promise(resolve=>{(window as any).__finishExport=resolve;})});});
 await page.getByTestId('menu-edit').click();await page.getByTestId('menu-copy-image').click();const dialog=page.getByRole('dialog',{name:'Export image'});await expect(dialog).toBeFocused();await page.keyboard.press('Tab');await expect(dialog).toBeFocused();await page.keyboard.press('Control+n');await expect(page.getByTestId('new-document')).toHaveCount(0);expect(await snapshot(page)).toEqual(before);
 await page.evaluate(()=>(window as any).__finishExport({ok:false,error:{code:'clipboard_unavailable',message:'Busy clipboard',retryable:true}}));await expect(dialog.getByRole('alert')).toHaveText('Busy clipboard');await page.keyboard.press('Escape');await expect(dialog).toHaveCount(0);
});
