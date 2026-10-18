import { useEffect, useRef } from 'react';
import type { EditorModel } from '../editor/EditorModel';
import type { DiskVersion } from '../editor/FileConflictFlow';
import { useModel } from './useModel';
const details=(v?:DiskVersion)=> !v?'Unknown':!v.exists?'Missing':`${v.length ?? 'Unknown'} bytes · ${v.lastWriteUtc?new Date(v.lastWriteUtc).toLocaleString():'Unknown modified time'}`;
export function FileConflictDialog({model}:{model:EditorModel}) {
  useModel(model);const p=model.files.prompt;return p?<Choices model={model}/>:null;
}
function Choices({model}:{model:EditorModel}) {
  const flow=model.files,p=flow.prompt!,box=useRef<HTMLDivElement>(null);
  useEffect(()=>{const old=document.activeElement as HTMLElement|null;box.current?.querySelector<HTMLElement>('button')?.focus();return()=>{if(old?.isConnected)old.focus();};},[]);
  useEffect(()=>{if(p.busy)box.current?.focus();else box.current?.querySelector<HTMLElement>('button:not(:disabled)')?.focus();},[p.busy,p.confirmReload]);
  return <div className="dialog-backdrop" role="presentation"><div ref={box} tabIndex={-1} className="dialog" role="dialog" aria-modal="true" aria-labelledby="file-title" aria-describedby="file-description" data-testid="file-conflict" onKeyDown={e=>{
    e.stopPropagation();if(e.key==='Escape'){e.preventDefault();flow.cancel();}
    if(e.key==='Tab'){const items=Array.from(box.current!.querySelectorAll<HTMLButtonElement>('button:not(:disabled)'));if(!items.length){e.preventDefault();box.current?.focus();}else if(e.shiftKey&&document.activeElement===items[0]){e.preventDefault();items.at(-1)?.focus();}else if(!e.shiftKey&&document.activeElement===items.at(-1)){e.preventDefault();items[0]?.focus();}}
  }}>
    <h2 id="file-title">File changed on disk</h2><p id="file-description">Choose which version to keep. Your drawing has not been overwritten.</p>
    <p className="muted" style={{overflowWrap:'anywhere'}}>{p.conflict.path}</p>
    <dl><dt>Opened or last saved</dt><dd>{details(p.conflict.baseline)}</dd><dt>On disk now</dt><dd>{details(p.conflict.observed)}</dd></dl>
    {p.conflict.readError&&<p className="diag diag-warning">The file could not be read: {p.conflict.readError}</p>}
    {p.error&&<p className="diag diag-warning" role="alert" data-testid="file-error">{p.error}</p>}
    {p.confirmReload?<><p>Discard local changes and replace this tab with the disk version?</p><div className="dialog-actions"><button disabled={p.busy} onClick={()=>void flow.choose('reload',true)}>Discard local changes and reload</button><button disabled={p.busy} onClick={()=>{p.confirmReload=false;model.bump();}}>Back</button><button disabled={p.busy} onClick={()=>flow.cancel()}>Cancel</button></div></>:
    <div className="dialog-actions"><button disabled={p.busy||!p.conflict.observed?.exists} onClick={()=>void flow.choose('reload')}>Reload from disk</button><button disabled={p.busy} onClick={()=>void flow.choose('saveAs')}>Keep mine (Save as)</button><button disabled={p.busy||!p.conflict.observed} onClick={()=>void flow.choose('overwrite')}>Overwrite this version</button><button disabled={p.busy} onClick={()=>flow.cancel()}>Cancel</button></div>}
    {p.busy&&<p role="status">Working…</p>}
  </div></div>;
}
