import { useEffect, useRef } from 'react';
import type { EditorModel } from '../editor/EditorModel';
import { useModel } from './useModel';

export function FindPanel({model}:{model:EditorModel}) {
  useModel(model);
  const flow=model.find;
  const input=useRef<HTMLInputElement>(null);
  const opener=useRef<HTMLElement | null>(null);
  useEffect(()=>{if(flow.visible)opener.current=document.activeElement instanceof HTMLElement?document.activeElement:null;},[flow.visible]);
  const close=()=>{flow.close();queueMicrotask(()=>{if(document.querySelector('[aria-modal=true]'))return;if(opener.current?.isConnected)opener.current.focus();else model.canvas?.graph.container.focus();});};
  useEffect(()=>{if(flow.visible){input.current?.focus();input.current?.select();}},[flow.visible,flow.focusVersion]);
  if(!flow.visible)return null;
  const hits=flow.hits,current=flow.current;
  const editable=hits.filter(h=>h.replaceable).length;
  const locked=hits.filter(h=>h.locked).length,hidden=hits.filter(h=>h.hidden).length;
  return <section className="find-panel" role="region" aria-label="Find and replace" data-testid="find-panel" onKeyDown={e=>{
    if(e.key==='Escape'){e.preventDefault();e.stopPropagation();close();}
    else if(e.key==='Enter' && e.target instanceof HTMLInputElement && e.target.type==='text'){e.preventDefault();flow.navigate(e.shiftKey?-1:1);}
  }}>
    <div className="row"><h2>Find and replace</h2><button type="button" aria-label="Close find" onClick={close}>×</button></div>
    <label>Find literal text<input ref={input} aria-label="Find literal text" value={flow.options.query} onChange={e=>flow.setOptions({query:e.target.value})} /></label>
    <div className="row"><label><input type="checkbox" checked={flow.options.caseSensitive} onChange={e=>flow.setOptions({caseSensitive:e.target.checked})} />Match case</label><label><input type="checkbox" checked={flow.options.includeHidden} onChange={e=>flow.setOptions({includeHidden:e.target.checked})} />Include hidden</label></div>
    <p data-testid="find-count" aria-live="polite">{hits.length} matches · {editable} editable · {locked} locked · {hidden} hidden · {hits.filter(h=>!h.fields.includes('text')).length} name/alias only</p>
    <div className="row"><button type="button" disabled={!hits.length} onClick={()=>flow.navigate(-1)}>Previous</button><button type="button" disabled={!hits.length} onClick={()=>flow.navigate(1)}>Next</button><span>{current?hits.indexOf(current)+1:0} / {hits.length}</span></div>
    <label>Replace with<input aria-label="Replace with" value={flow.replacement} onChange={e=>{flow.replacement=e.target.value;model.bump();}} /></label>
    <div className="row"><button type="button" disabled={flow.busy||!current?.replaceable} onClick={()=>void flow.replace(false)}>Replace current</button><button type="button" disabled={flow.busy||!editable} onClick={()=>void flow.replace(true)}>Replace all</button></div>
    <p className="muted">Replacement changes text only. Names, aliases and hidden or locked objects are preserved.</p>
    <p role="status" data-testid="find-message">{flow.message}</p>
    {hits.length>200&&<p>Showing the first 200 results. Next and Previous navigate all matches.</p>}
    <ol className="find-results">{hits.slice(0,200).map(hit=><li key={hit.id}><button type="button" data-testid={'find-result-'+hit.id} aria-current={current?.id===hit.id?'true':undefined} onClick={()=>flow.choose(hit.id)}><strong>{hit.pageName} · {hit.label}</strong><span>{hit.fields.join(', ')}{hit.hidden?' · hidden':''}{hit.locked?' · locked':''}</span><span>{hit.snippet}</span></button></li>)}</ol>
  </section>;
}
