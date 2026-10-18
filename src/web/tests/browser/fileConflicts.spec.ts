import { test, expect } from '@playwright/test';
import { openEditor, placeShape, snapshot } from './support';
async function arm(page: import('@playwright/test').Page) {
  await page.evaluate(() => {
    const e=(window as any).__editor; const s=e.snapshot();
    const offer={ token:'choice-1',path:'C:\\Work\\shared.diagram.json',expected:{documentId:s.documentId,sessionId:s.sessionId,baseRevision:s.revision},baseline:{exists:true,sha256:'old',length:100,lastWriteUtc:'2026-10-18T10:00:00Z'},observed:{exists:true,sha256:'external',length:120,lastWriteUtc:'2026-10-18T11:00:00Z'} };
    (window as any).__fileCalls=[];
    e.setHost({saveJson:async()=>({ok:true,value:{conflict:offer}}),fileCheck:async()=>({ok:true,value:{conflict:offer}}),fileResolve:async(_token:string,action:string)=>{(window as any).__fileCalls.push(action);return {ok:true,value:{cancelled:true}};}});
  });
}
test('Save conflict shows both versions, Cancel preserves content and a cancelled Save as stays open',async({page})=>{
  await openEditor(page);await placeShape(page,'shape-rectangle',{x:200,y:150},'keep');const before=await snapshot(page);await arm(page);
  await page.keyboard.press('Control+s');await expect(page.getByRole('dialog',{name:'File changed on disk',exact:true})).toBeVisible();
  await expect(page.getByTestId('file-conflict')).toContainText('100 bytes');await expect(page.getByTestId('file-conflict')).toContainText('120 bytes');
  await page.getByRole('button',{name:'Keep mine (Save as)',exact:true}).click();await expect(page.getByTestId('file-error')).toContainText('cancelled');
  expect(await snapshot(page)).toEqual(before);await page.keyboard.press('Escape');await expect(page.getByTestId('file-conflict')).toHaveCount(0);expect(await snapshot(page)).toEqual(before);
});
test('Reload asks before losing local edits and a host failure leaves work intact',async({page})=>{
  await openEditor(page);await placeShape(page,'shape-rectangle',{x:200,y:150},'keep');const before=await snapshot(page);await arm(page);
  await page.keyboard.press('Control+s');await page.getByRole('button',{name:'Reload from disk',exact:true}).click();
  await expect(page.getByTestId('file-conflict')).toContainText('Discard local changes');expect(await page.evaluate(()=>(window as any).__fileCalls)).toEqual([]);
  await page.evaluate(()=>(window as any).__editor.setHost({fileResolve:async()=>({ok:false,error:{code:'invalid_request',message:'Invalid disk diagram',retryable:false}})}));
  await page.getByRole('button',{name:'Discard local changes and reload',exact:true}).click();await expect(page.getByTestId('file-error')).toContainText('Invalid disk diagram');expect(await snapshot(page)).toEqual(before);
});
test('focus notices wait for New and Cancel deduplicates them, but Save checks again',async({page})=>{
 await openEditor(page);await arm(page);await page.keyboard.press('Control+n');await page.evaluate(()=>window.dispatchEvent(new Event('focus')));
 await expect(page.getByTestId('new-document')).toBeVisible();await expect(page.getByTestId('file-conflict')).toHaveCount(0);
 await page.getByRole('button',{name:'Cancel',exact:true}).click();await expect(page.getByTestId('file-conflict')).toBeVisible();
 await page.keyboard.press('Escape');await page.evaluate(()=>window.dispatchEvent(new Event('focus')));await expect(page.getByTestId('file-conflict')).toHaveCount(0);
 await page.keyboard.press('Control+s');await expect(page.getByTestId('file-conflict')).toBeVisible();
});
test('Save all on a later tab shows only the file dialog and cancelling keeps the close prompt',async({page})=>{
 await openEditor(page);await placeShape(page,'shape-rectangle',{x:200,y:150},'first');
 await page.getByTestId('tab-new').click();await placeShape(page,'shape-rectangle',{x:200,y:150},'second');
 await page.evaluate(()=>{
  const e=(window as any).__editor;(window as any).__closingCalls=[];
  e.setHost({saveJson:async()=>{
   const s=e.snapshot();if(s.document.pages[0].elements.some((x:any)=>x.alias==='first')){await e.markSaved('C:/first.json');return{ok:true,value:{path:'C:/first.json'}};}
   return{ok:true,value:{conflict:{token:'second',path:'C:/second.json',expected:{documentId:s.documentId,sessionId:s.sessionId,baseRevision:s.revision},observed:{exists:true,length:8}}}};
  },appClose:async()=>{(window as any).__closingCalls.push('close');return{ok:true,value:{closing:true}};}});
 });
 await page.evaluate(()=>(window as any).__editor.beginClose());await page.getByTestId('close-save').click();
 await expect(page.getByTestId('file-conflict')).toBeVisible();await expect(page.getByTestId('close-prompt')).toHaveCount(0);
 await page.keyboard.press('Escape');await expect(page.getByTestId('close-prompt')).toBeVisible();expect(await page.evaluate(()=>(window as any).__closingCalls)).toEqual([]);
});
test('focus conflicts wait for the recovery report instead of stacking dialogs',async({page})=>{
 await openEditor(page);
 await page.evaluate(()=>{
  const e=(window as any).__editor,doc=structuredClone(e.snapshot().document);doc.id=crypto.randomUUID();const loss={tornTailDiscarded:false,corruptionAfterRevision:null,messages:[]};
  const session={sessionId:'recover',documentId:doc.id,originalPath:null,title:'Recovered',checkpointRevision:0,lastDurableRevision:0,lastWrite:'2026-10-18T10:00:00Z',pageCount:1,objectCount:0,loss};
  e.setHost({recoveryList:async()=>({ok:true,value:{auto:false,sessions:[session]}}),recoveryFetch:async()=>({ok:true,value:{...session,document:doc,events:[]}}),recoveryFinish:async()=>({ok:true,value:{finished:'recover'}})});
 });
 await page.evaluate(()=>(window as any).__editor.checkRecovery());await page.getByTestId('recovery-restore').click();await expect(page.getByTestId('recovery-report')).toBeVisible();
 await arm(page);await page.evaluate(()=>window.dispatchEvent(new Event('focus')));await expect(page.getByRole('dialog')).toHaveCount(1);await expect(page.getByTestId('file-conflict')).toHaveCount(0);
 await page.getByTestId('recovery-close').click();await expect(page.getByTestId('file-conflict')).toBeVisible();
});
test('window Close cannot hide a focus conflict when it activates an earlier unsaved tab',async({page})=>{
 await openEditor(page);await placeShape(page,'shape-rectangle',{x:200,y:150},'first');await page.getByTestId('tab-new').click();await arm(page);
 await page.evaluate(()=>window.dispatchEvent(new Event('focus')));await expect(page.getByTestId('file-conflict')).toBeVisible();
 await page.evaluate(()=>(window as any).__editor.beginClose());await expect(page.getByTestId('file-conflict')).toBeVisible();await expect(page.getByRole('dialog')).toHaveCount(1);
 await page.keyboard.press('Escape');await expect(page.getByTestId('close-prompt')).toBeVisible();
});
test('Save as from a recovery report shows one modal and Cancel returns to the report',async({page})=>{
 await openEditor(page);await page.evaluate(()=>{
  const e=(window as any).__editor,doc=structuredClone(e.snapshot().document);doc.id=crypto.randomUUID();const loss={tornTailDiscarded:false,corruptionAfterRevision:null,messages:[]};
  const session={sessionId:'recover',documentId:doc.id,originalPath:null,title:'Recovered',checkpointRevision:0,lastDurableRevision:0,lastWrite:'2026-10-18T10:00:00Z',pageCount:1,objectCount:0,loss};
  e.setHost({recoveryList:async()=>({ok:true,value:{auto:false,sessions:[session]}}),recoveryFetch:async()=>({ok:true,value:{...session,document:doc,events:[]}}),recoveryFinish:async()=>({ok:true,value:{finished:'recover'}})});
 });
 await page.evaluate(()=>(window as any).__editor.checkRecovery());await page.getByTestId('recovery-restore').click();await expect(page.getByTestId('recovery-report')).toBeVisible();
 await arm(page);await page.getByTestId('recovery-save-as').click();await expect(page.getByTestId('file-conflict')).toBeVisible();await expect(page.getByRole('dialog')).toHaveCount(1);
 await page.keyboard.press('Escape');await expect(page.getByTestId('recovery-report')).toBeVisible();
});
