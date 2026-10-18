import {spawn} from 'node:child_process';
import {createInterface} from 'node:readline';
import {writeFileSync,readFileSync} from 'node:fs';
const exe=process.env.LOCALAPPDATA+'/Programs/AgenticDiagram/versions/r08-routing/mcp/Diagram.Mcp.exe';
const child=spawn(exe,[],{stdio:['pipe','pipe','pipe']});child.stderr.on('data',()=>{});
let id=0;const pending=new Map();
createInterface({input:child.stdout}).on('line',l=>{try{const m=JSON.parse(l);pending.get(m.id)?.(m);pending.delete(m.id)}catch{}});
const rpc=(method,params)=>new Promise((res,rej)=>{const n=++id;const t=setTimeout(()=>rej(Error('timeout '+method)),140000);pending.set(n,m=>{clearTimeout(t);m.error?rej(Error(JSON.stringify(m.error))):res(m.result)});child.stdin.write(JSON.stringify({jsonrpc:'2.0',id:n,method,params})+'\n')});
const call=async(name,args={})=>{const r=await rpc('tools/call',{name,arguments:args});if(r.isError)throw Error(name+': '+JSON.stringify(r).slice(0,1500));return r};
const sc=r=>r.structuredContent??JSON.parse(r.content.find(c=>c.type==='text').text);
try{
 await rpc('initialize',{protocolVersion:'2025-03-26',capabilities:{},clientInfo:{name:'swimlane',version:'1'}});
 child.stdin.write(JSON.stringify({jsonrpc:'2.0',method:'notifications/initialized'})+'\n');
 const s=sc(await call('get_document_summary'));
 const ident={documentId:s.documentId,sessionId:s.sessionId};
 const ops=JSON.parse(readFileSync('ops.json','utf8'));const fresh=ops.slice();for(const p of s.pages)ops.push({op:'deletePage',pageId:p.id});
 const a=process.env.EXPORT_ONLY?{}:await call('apply_operations',{...ident,transactionId:crypto.randomUUID(),baseRevision:s.revision,operations:ops});
 
 const pageId=process.env.EXPORT_ONLY?s.pages[0].id:ops[0].id;
 const r=await call('render_page',{...ident,pageId,maxWidth:2400,maxHeight:1700});
 writeFileSync('mine-r08.png',Buffer.from(r.content.find(c=>c.type==='image').data,'base64'));
 const li=sc(await call('inspect_layout',{...ident,pageId}));writeFileSync('layout.json',JSON.stringify(li,null,1));
 console.log('layout',JSON.stringify(li).slice(0,800));
 const e=await call('export_document',{...ident,format:'vsdx',path:'lego-infra-r08.vsdx'});console.log('export',JSON.stringify(sc(e)).slice(0,3000));
}finally{child.kill()}
