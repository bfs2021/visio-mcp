import { mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { test, expect } from '@playwright/test';
import { openEditor, snapshot, revision, selection, ptToClient, centre } from './support';
const id=(n:number)=>'00000000-0000-4000-8000-'+String(n).padStart(12,'0');
test('find navigates pages and replaces editable text in one undo without renaming references',async({page})=>{
 await openEditor(page);await page.evaluate(ids=>(window as any).__editor.agentApply([
 {op:'createShape',id:ids[0],alias:'stable-a',name:'Stable A',bounds:{x:100,y:100,width:100,height:50},text:{value:'Service Service',bold:true}},
 {op:'createShape',id:ids[1],name:'Service name',bounds:{x:250,y:100,width:100,height:50},text:{value:'Other'}},
 {op:'addPage',id:ids[2],name:'Second'},
 {op:'createText',pageId:ids[2],id:ids[3],bounds:{x:30,y:30,width:100,height:30},text:{value:'Service',italic:true}}
 ]),[id(960),id(961),id(962),id(963)]);
 const before=await snapshot(page);await page.keyboard.press('Control+f');await expect(page.getByRole('region',{name:'Find and replace'})).toBeVisible();
 await page.getByLabel('Find literal text').fill('Service');await expect(page.getByTestId('find-count')).toContainText('3 matches');await expect(page.getByTestId('find-count')).toContainText('2 editable');
 await page.getByTestId('find-result-'+id(963)).click();await expect(page.getByTestId('page-tab').nth(1)).toHaveAttribute('aria-selected','true');expect(await selection(page)).toEqual([id(963)]);
 const evidence=fileURLToPath(new URL('../../../../artifacts/w11-find-replace/',import.meta.url));mkdirSync(evidence,{recursive:true});await page.screenshot({path:evidence+'/find-panel.png'});
 await page.getByLabel('Replace with').fill('Queue');const rev=await revision(page);await page.getByRole('button',{name:'Replace all',exact:true}).click();
 await expect.poll(()=>revision(page)).toBe(rev+1);const after=await snapshot(page);
 expect(after.document.pages[0]!.elements[0]).toMatchObject({alias:'stable-a',name:'Stable A',text:{value:'Queue Queue',bold:true}});
 expect(after.document.pages[0]!.elements[1]).toMatchObject({name:'Service name',text:{value:'Other'}});expect(after.document.pages[1]!.elements[0]).toMatchObject({text:{value:'Queue',italic:true}});
 await page.getByRole('button',{name:'Close find',exact:true}).click();await page.keyboard.press('Control+z');await expect.poll(async()=>(await snapshot(page)).document.pages).toEqual(before.document.pages);
});
test('hidden and locked results are reported and never silently edited',async({page})=>{
 await openEditor(page);await page.evaluate(ids=>(window as any).__editor.agentApply([
 {op:'createText',id:ids[0],bounds:{x:100,y:100,width:100,height:30},text:{value:'Queue'}},
 {op:'createText',id:ids[1],bounds:{x:100,y:160,width:100,height:30},text:{value:'Queue'}},
 {op:'set',target:ids[0],patch:{hidden:true}},{op:'set',target:ids[1],patch:{locked:true}}
 ]),[id(970),id(971)]);
 await page.keyboard.press('Control+f');await page.getByLabel('Find literal text').fill('Queue');await expect(page.getByTestId('find-count')).toContainText('1 matches');await page.getByLabel('Include hidden').check();
 await expect(page.getByTestId('find-count')).toContainText('2 matches');await expect(page.getByTestId('find-count')).toContainText('0 editable');await page.getByTestId('find-result-'+id(970)).click();await expect(page.getByTestId('find-message')).toContainText('hidden');
 const before=await snapshot(page);await page.getByLabel('Replace with').fill('New');await expect(page.getByRole('button',{name:'Replace all',exact:true})).toBeDisabled();expect(await snapshot(page)).toEqual(before);
});

test('matching selection uses style or exact layer membership without adding revisions',async({page})=>{
 await openEditor(page);await page.evaluate(ids=>(window as any).__editor.agentApply([
 {op:'createShape',id:ids[0],bounds:{x:100,y:100,width:80,height:40},text:{value:'A'}},
 {op:'createShape',id:ids[1],bounds:{x:250,y:100,width:80,height:40},text:{value:'B'}},
 {op:'createShape',id:ids[2],bounds:{x:400,y:100,width:80,height:40},text:{value:'C'},style:{fill:{kind:'solid',colour:'#ff0000',opacity:1}}}
 ]),[id(980),id(981),id(982)]);
 const at=await ptToClient(page,{x:140,y:120});await page.mouse.click(at.x,at.y);const rev=await revision(page);
 await page.getByTestId('menu-edit').click();await page.getByTestId('menu-select-style').click();expect((await selection(page)).sort()).toEqual([id(980),id(981)]);
 await page.mouse.click(at.x,at.y);await page.getByTestId('menu-edit').click();await page.getByTestId('menu-select-layers').click();expect((await selection(page)).sort()).toEqual([id(980),id(981),id(982)]);
 expect(await revision(page)).toBe(rev);
});
test('search updates after agent edits and stays scoped to each document tab',async({page})=>{
 await openEditor(page);await page.evaluate(ids=>(window as any).__editor.agentApply([{op:'createText',id:ids[0],bounds:{x:100,y:100,width:100,height:30},text:{value:'Unique'}}]),[id(990)]);
 await page.keyboard.press('Control+f');await page.getByLabel('Find literal text').fill('Unique');await expect(page.getByTestId('find-count')).toContainText('1 matches');
 await page.evaluate(target=>(window as any).__editor.agentApply([{op:'set',target,patch:{text:{value:'Changed'}}}]),id(990));await expect(page.getByTestId('find-count')).toContainText('0 matches');
 await page.getByRole('button',{name:'Close find',exact:true}).click();await page.keyboard.press('Control+n');await expect(page.getByTestId('new-document')).toBeVisible();await page.keyboard.press('Control+f');await expect(page.getByTestId('find-panel')).toHaveCount(0);await page.getByTestId('new-document').getByRole('button',{name:/Create/}).click();
 await page.keyboard.press('Control+f');await page.getByLabel('Find literal text').fill('Changed');await expect(page.getByTestId('find-count')).toContainText('0 matches');
});

test('connector labels replace safely and named groups reveal only visible members',async({page})=>{
 await openEditor(page);await page.evaluate(ids=>(window as any).__editor.agentApply([
 {op:'setPage',patch:{heightPt:3000}},
 {op:'createConnector',id:ids[0],from:{glue:'none',point:{x:20,y:20}},to:{glue:'none',point:{x:100,y:100}},label:{value:'Approval',bold:true}},
 {op:'createShape',id:ids[1],bounds:{x:100,y:2000,width:80,height:40}},
 {op:'createShape',id:ids[2],bounds:{x:250,y:2000,width:80,height:40}},
 {op:'group',id:ids[3],name:'Offscreen group',targets:[ids[1],ids[2]]},
 {op:'set',target:ids[2],patch:{hidden:true}}
 ]),[id(995),id(996),id(997),id(998)]);
 await page.getByTestId('menu-view').click();await page.getByTestId('menu-zoom-100').click();
 await page.keyboard.press('Control+f');await page.getByLabel('Find literal text').fill('Approval');await expect(page.getByTestId('find-count')).toContainText('1 editable');await page.getByLabel('Replace with').fill('Approved');await page.getByRole('button',{name:'Replace current',exact:true}).click();
 await expect.poll(async()=>(await snapshot(page)).document.pages[0]!.elements.find(e=>e.id===id(995))).toMatchObject({label:{value:'Approved',bold:true}});
 await page.getByLabel('Find literal text').fill('Offscreen group');await page.getByTestId('find-result-'+id(998)).click();expect(await selection(page)).toEqual([id(996)]);
 const point=await centre(page,id(996));const canvas=await page.getByTestId('canvas').boundingBox();expect(point.y).toBeGreaterThan(canvas!.y);expect(point.y).toBeLessThan(canvas!.y+canvas!.height);
});
