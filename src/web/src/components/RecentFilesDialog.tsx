import {useEffect,useRef,useState} from 'react';
import type {WorkspaceModel} from '../workspace/WorkspaceModel';
import type {RecentList,RecentEntry} from '../editor/EditorModel';
import type {Result} from '../model/result';

/** Host-owned history IDs are the only values used to open recent files. */
export function RecentFilesDialog({ws}:{ws:WorkspaceModel}) {
 const [list,setList]=useState<RecentList>({entries:[]}),[busy,setBusy]=useState(true),[error,setError]=useState('');
 const box=useRef<HTMLDivElement>(null);
 useEffect(()=>{let live=true;const previous=document.activeElement as HTMLElement|null;
  void ws.host.recentList().then(r=>{if(live){if(r.ok)setList(r.value);else setError(r.error.message);setBusy(false);}}).catch(e=>{if(live){setError(String(e));setBusy(false);}});
  return()=>{live=false;if(previous?.isConnected)previous.focus();};},[ws]);
 useEffect(()=>{if(busy)box.current?.focus();else box.current?.querySelector<HTMLElement>('button:not(:disabled)')?.focus();},[busy]);
 async function update(action:()=>Promise<Result<RecentList>>) {setBusy(true);setError('');try{const r=await action();if(r.ok)setList(r.value);else setError(r.error.message);}catch(e){setError(String(e));}finally{setBusy(false);}}
 async function open(entry:RecentEntry){setBusy(true);setError('');try{const r=await ws.openRecent(entry);if(r.opened)ws.closeRecent();else setError(r.error??'The file was not opened.');}catch(e){setError(String(e));}finally{setBusy(false);}}
 return <div className="dialog-backdrop" role="presentation"><div className="dialog recent-files" ref={box} tabIndex={-1} role="dialog" aria-modal="true" aria-labelledby="recent-title" data-testid="recent-files" onKeyDown={e=>{
  e.stopPropagation();if(e.key==='Escape'){e.preventDefault();if(!busy)ws.closeRecent();}
  if(e.key==='Tab'){const buttons=Array.from(box.current!.querySelectorAll<HTMLButtonElement>('button:not(:disabled)'));const first=buttons[0],last=buttons.at(-1);if(!buttons.length){e.preventDefault();box.current?.focus();}else if(e.shiftKey&&(document.activeElement===first||document.activeElement===box.current)){e.preventDefault();last?.focus();}else if(!e.shiftKey&&(document.activeElement===last||document.activeElement===box.current)){e.preventDefault();first?.focus();}}
 }}><h2 id="recent-title">Recent files</h2><p>The last ten files opened or saved. Removing history keeps the files on disk.</p>
 {busy&&<p role="status">Working…</p>}{error&&<p role="alert">{error}</p>}{list.warning&&<p role="alert">{list.warning}</p>}
 {!busy&&!list.entries.length&&<p>No recent files.</p>}
 <ul className="recent-items">{list.entries.map(entry=><li key={entry.id} data-testid="recent-entry"><strong>{entry.path.split(/[\\/]/).pop()}</strong><div className="recent-path">{entry.path}</div>{entry.availability!=='available'&&<div>{entry.availability==='missing'?'File is missing':'File is unavailable'}</div>}<div className="row"><button type="button" disabled={busy} onClick={()=>void open(entry)}>Open</button><button type="button" disabled={busy} onClick={()=>void update(()=>ws.host.recentRemove(entry.id))}>Remove</button></div></li>)}</ul>
 <div className="row dialog-actions"><button type="button" disabled={busy||!list.entries.length} onClick={()=>void update(()=>ws.host.recentClear())}>Clear recent</button><button type="button" disabled={busy} onClick={()=>ws.closeRecent()}>Close</button></div></div></div>;
}
