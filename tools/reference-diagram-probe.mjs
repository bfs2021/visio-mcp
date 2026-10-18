import {spawn} from 'node:child_process';
import {createInterface} from 'node:readline';
import {mkdirSync,writeFileSync,readFileSync} from 'node:fs';
const out='C:/Dev/diagram-app/artifacts/reference-diagram'; mkdirSync(out,{recursive:true});
const child=spawn('C:/Users/claude/AppData/Local/Programs/AgenticDiagram/mcp/Diagram.Mcp.exe',[],{stdio:['pipe','pipe','pipe']});
child.stderr.on('data',()=>{});
let id=0;const pending=new Map();
createInterface({input:child.stdout}).on('line',line=>{try{const m=JSON.parse(line);if(pending.has(m.id)){pending.get(m.id)(m);pending.delete(m.id);}}catch{}});
const rpc=(method,params)=>new Promise((resolve,reject)=>{const n=++id;const timer=setTimeout(()=>reject(Error('timeout '+method)),140000);pending.set(n,m=>{clearTimeout(timer);m.error?reject(Error(JSON.stringify(m.error))):resolve(m.result)});child.stdin.write(JSON.stringify({jsonrpc:'2.0',id:n,method,params})+'\n');});
const call=async(name,args={})=>{const r=await rpc('tools/call',{name,arguments:args});writeFileSync(`${out}/${name}.json`,JSON.stringify(r,null,2));if(r.isError)throw Error(JSON.stringify(r));return r;};
try{
 await rpc('initialize',{protocolVersion:'2025-03-26',capabilities:{},clientInfo:{name:'reference-diagram-probe',version:'1.0'}});
 child.stdin.write(JSON.stringify({jsonrpc:'2.0',method:'notifications/initialized'})+'\n');
 const summary=await call('get_document_summary');
 const s=summary.structuredContent??JSON.parse(summary.content.find(c=>c.type==='text').text);
 console.log(JSON.stringify({documentId:s.documentId,revision:s.revision,pages:s.pages.map(p=>({id:p.id,name:p.name,elementCount:p.elementCount}))}));
 if(process.argv[2]){
 const ops=JSON.parse(readFileSync(process.argv[2],'utf8'));
 const identity={documentId:s.documentId,sessionId:s.sessionId};
 const applied=await call('apply_operations',{...identity,transactionId:crypto.randomUUID(),baseRevision:s.revision,operations:ops});console.log('Applied revision',applied.structuredContent?.transaction?.revision);
 const pageId=process.argv[3]??ops.find(o=>o.op==='addPage')?.id??ops.find(o=>o.pageId)?.pageId??s.pages[0].id;
 const render=await call('render_page',{...identity,pageId,maxWidth:1600,maxHeight:1300});
 writeFileSync(`${out}/light-mode.png`,Buffer.from(render.content.find(c=>c.type==='image').data,'base64'));
 await call('inspect_layout',{...identity,pageId});
 const exported=await call('export_document',{...identity,format:'vsdx',path:'repository-deployment-light.vsdx'});console.log('Export',exported.structuredContent?.path);
 }
}finally{child.kill();}
