import {useEffect,useRef} from 'react';
import type {EditorModel} from '../editor/EditorModel';
import type {EditorExportOptions} from '../editor/EditorExportOptions';
import {useModel} from './useModel';
export function ExportImageDialog({model}:{model:EditorModel}){useModel(model);return model.exportPrompt?<Picker model={model}/>:null;}
function Picker({model}:{model:EditorModel}){
 const p=model.exportPrompt!,o=p.options,box=useRef<HTMLDivElement>(null),pdf=o.format==='pdf';
 useEffect(()=>{const old=document.activeElement as HTMLElement|null;box.current?.querySelector<HTMLElement>('select:not(:disabled),button:not(:disabled)')?.focus();return()=>{if(old?.isConnected)old.focus();};},[]);
 useEffect(()=>{if(p.busy)box.current?.focus();else box.current?.querySelector<HTMLElement>('select:not(:disabled),button:not(:disabled)')?.focus();},[p.busy]);
 const change=(v:Partial<EditorExportOptions>)=>{Object.assign(o,v);model.bump();};
 return <div className="dialog-backdrop"><div className="dialog" ref={box} tabIndex={-1} role="dialog" aria-modal="true" aria-labelledby="image-export-title" data-testid="image-export" onKeyDown={e=>{e.stopPropagation();if(e.key==='Escape'){e.preventDefault();model.cancelImageExport();}if(e.key==='Tab'){const items=Array.from(box.current!.querySelectorAll<HTMLElement>('select:not(:disabled),button:not(:disabled)'));if(!items.length){e.preventDefault();box.current?.focus();}else if(e.shiftKey&&document.activeElement===items[0]){e.preventDefault();items.at(-1)?.focus();}else if(!e.shiftKey&&document.activeElement===items.at(-1)){e.preventDefault();items[0]?.focus();}}}}>
 <h2 id="image-export-title">{pdf?'Export PDF':'Export image'}</h2>
 <div className="export-options">{!pdf&&<><label>Format <select aria-label="Format" disabled={p.busy} value={o.format} onChange={e=>change({format:e.target.value as 'png',...(e.target.value==='jpeg'&&o.background==='transparent'?{background:'white'}:{})})}><option value="png">PNG</option><option value="jpeg">JPEG</option><option value="svg">SVG</option></select></label>
 <label>Pages or selection <select aria-label="Pages or selection" disabled={p.busy} value={o.scope} onChange={e=>change({scope:e.target.value as 'page'})}><option value="page">Current page</option><option value="selection" disabled={!o.elementIds.length}>Selection</option><option value="all">All pages</option></select></label>
 <label>Scale <select aria-label="Scale" value={o.scale} disabled={p.busy} onChange={e=>change({scale:Number(e.target.value)})}><option value="1">1 pixel per point</option><option value="2">2 pixels per point</option><option value="4">4 pixels per point</option></select></label></>}
 <label>Background <select aria-label="Background" disabled={p.busy} value={o.background} onChange={e=>change({background:e.target.value as 'page'})}><option value="page">Page background</option><option value="white">White</option><option value="transparent" disabled={o.format==='jpeg'||pdf}>Transparent</option></select></label></div>
 {o.format==='jpeg'&&<p>JPEG uses an opaque background.</p>}{pdf?<p>Every page is exported at its original page size.</p>:o.scope==='all'&&<p>All pages will be saved in one ZIP file.</p>}
 {p.error&&<p role="alert" className="diag diag-warning">{p.error}</p>}
 <div className="dialog-actions"><button disabled={p.busy} onClick={()=>void model.finishImageExport()}>{o.copy?'Copy again':'Export'}</button><button disabled={p.busy} onClick={()=>model.cancelImageExport()}>Cancel</button></div>{p.busy&&<p role="status">Preparing export…</p>}
 </div></div>;
}
