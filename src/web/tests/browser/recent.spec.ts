import {test,expect} from '@playwright/test';
import {openEditor,placeShape,snapshot} from './support';
test('Recent removes missing history and preserves work on failed Open',async({page})=>{
 await openEditor(page);await placeShape(page,'shape-rectangle',{x:200,y:150},'kept');const before=await snapshot(page);
 await page.evaluate(()=>{let entries=[{id:'gone',path:'C:/Gone/plan.diagram.json',kind:'json',availability:'missing'}];(window as any).__editor.setHost({recentList:async()=>({ok:true,value:{entries}}),recentRemove:async()=>{entries=[];return {ok:true,value:{entries}};},openJson:async()=>({ok:false,error:{code:'io_error',message:'File is missing',retryable:false}})});});
 await page.getByTestId('menu-file').click();await page.getByTestId('menu-recent').click();const d=page.getByRole('dialog',{name:'Recent files'});await expect(d).toContainText('missing');await page.keyboard.press('Control+n');await expect(page.getByTestId('new-document')).toHaveCount(0);
 await d.getByRole('button',{name:'Open',exact:true}).click();await expect(d.getByRole('alert')).toContainText('File is missing');expect(await snapshot(page)).toEqual(before);await expect(page.getByTestId('tab')).toHaveCount(1);await d.getByRole('button',{name:'Remove',exact:true}).click();await expect(d).toContainText('No recent files');await page.keyboard.press('Escape');await expect(d).toHaveCount(0);
});
test('Recent uses host IDs, opens beside unsaved work and reuses existing tabs',async({page})=>{
 await openEditor(page);await placeShape(page,'shape-rectangle',{x:200,y:150},'kept');
 await page.evaluate(()=>{const w=window as any;w.__calls=[];w.__editor.setHost({recentList:async()=>({ok:true,value:{entries:[{id:'host-id',path:'C:/Work/plan.diagram.json',kind:'json',availability:'available'}]}}),recentClear:async()=>({ok:true,value:{entries:[]}}),openJson:async(path:string|undefined,id:string)=>{w.__calls.push([path??null,id]);await w.__editor.markSaved('C:/Work/plan.diagram.json');return {ok:true,value:{path:'C:/Work/plan.diagram.json'}};}});});
 for(let i=0;i<2;i++){await page.getByTestId('menu-file').click();await page.getByTestId('menu-recent').click();await page.getByRole('dialog',{name:'Recent files'}).getByRole('button',{name:'Open',exact:true}).click();await expect(page.getByRole('dialog',{name:'Recent files'})).toHaveCount(0);}
 await expect(page.getByTestId('tab')).toHaveCount(2);expect(await page.evaluate(()=>(window as any).__calls)).toEqual([[null,'host-id']]);await page.getByTestId('tab').first().click();expect((await snapshot(page)).document.pages[0]!.elements.map(e=>e.alias)).toContain('kept');
 await page.getByTestId('menu-file').click();await page.getByTestId('menu-recent').click();await page.getByRole('button',{name:'Clear recent',exact:true}).click();await expect(page.getByRole('dialog',{name:'Recent files'})).toContainText('No recent files');
});

test('Recent contains thrown bridge errors and busy focus without leaving an empty tab',async({page})=>{
 await openEditor(page);await placeShape(page,'shape-rectangle',{x:200,y:150},'kept');const before=await snapshot(page);
 await page.evaluate(()=>{const w=window as any;w.__editor.setHost({recentList:async()=>({ok:true,value:{entries:[{id:'id',path:'C:/Work/plan.diagram.json',kind:'json',availability:'available'}]}}),openJson:async()=>new Promise((_resolve,reject)=>{w.__rejectRecent=reject;})});});
 await page.getByTestId('menu-file').click();await page.getByTestId('menu-recent').click();const d=page.getByRole('dialog',{name:'Recent files'});await d.getByRole('button',{name:'Open',exact:true}).click();await expect(d).toContainText('Working');await expect(d).toBeFocused();await page.keyboard.press('Tab');await expect(d).toBeFocused();await page.keyboard.press('Escape');await expect(d).toBeVisible();
 await page.evaluate(()=>(window as any).__rejectRecent(new Error('Bridge unavailable')));await expect(d.getByRole('alert')).toContainText('Bridge unavailable');await expect(page.getByTestId('tab')).toHaveCount(1);expect(await snapshot(page)).toEqual(before);
});
