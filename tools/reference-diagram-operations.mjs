import {writeFileSync,mkdirSync} from 'node:fs';
const pageId=crypto.randomUUID(), ops=[];
const solid=colour=>({kind:'solid',colour,opacity:1}),none={kind:'none'};
ops.push({op:'addPage',id:pageId,name:'Repository deployment · light',widthPt:1220,heightPt:950});
ops.push({op:'setPage',pageId,patch:{background:solid('#FFFFFF'),grid:{visible:false,snap:false}}});
function box(alias,x,y,width,height,fill='#FFFFFF',stroke='#CBD5E1',radius=10,preset='rounded-rectangle'){
 ops.push({op:'createShape',pageId,alias,bounds:{x,y,width,height},geometry:{preset,cornerRadiusPt:radius},style:{fill:fill?solid(fill):none,stroke:stroke?solid(stroke):none,strokeWidthPt:1}});
}
function text(alias,value,x,y,width,height=28,size=18,bold=false,align='left',colour='#172033'){
 ops.push({op:'createText',pageId,alias,bounds:{x,y,width,height},text:{value,fontFamily:'Arial',fontSizePt:size,bold,colour,horizontalAlign:align,verticalAlign:'middle',wrap:false,paddingPt:0}});
}
function rule(alias,x,y,w){box(alias,x,y,w,0.8,'#E2E8F0',null,0,'rectangle');}
box('outer-frame',35,10,1150,930,'#FFFFFF','#CBD5E1',18);
for(const [prefix,x,title,owner,items,pipeline] of [
 ['infra',72,'Infrastructure repository','Owned by Sensical',['Bicep modules','Environment parameters','Network, security and identities','Infrastructure release manifests'],'Validate → Plan → Deploy → Verify'],
 ['app',638,'Application repository','Owned by external developer',['API and worker source code','Tests and database migrations','Infrastructure requirements','Application release manifests'],'Build → Test → Verify → Deploy']]){
 text(prefix+'-icon',prefix==='infra'?'⑂':'‹/›',x,52,30,28,25);
 text(prefix+'-title',title,x+36,52,480,30,24,true);
 text(prefix+'-owner',owner,x,98,512,25,18);
 rule(prefix+'-rule',x,145,512);
 items.forEach((v,i)=>text(prefix+'-item-'+i,v,x,170+i*36,512,26,18));
 box(prefix+'-actions',x,314,512,82,'#EEF2F6',null,9);
 text(prefix+'-action-title',(prefix==='infra'?'Infrastructure':'Application')+' GitHub Actions',x,325,512,26,18,true,'center');
 text(prefix+'-pipeline',pipeline,x,358,512,29,20,false,'center');
}
box('release-record',90,426,1040,84,'#F8FAFC','#CBD5E1',9);
text('release-title','Release compatibility and deployment record',105,440,1010,27,20,true,'center');
text('release-details','Versioned contract · Approval gates · Deployment evidence',105,473,1010,27,20,false,'center');
text('release-left-arrows','↓↑',50,455,30,30,24,false,'center','#475569');
text('release-right-arrows','↓↑',1140,455,30,30,24,false,'center','#475569');
box('tenancy-frame',54,523,1112,396,'#FFFFFF','#CBD5E1',12);
box('cloud-icon',481,551,23,18,null,'#2563EB',0,'cloud');
text('tenancy-title','Client Azure tenancy',516,547,270,32,24,true);
for(const [i,label,colour,caption] of [[0,'DEV','#2563EB','Rapid iteration'],[1,'TEST','#334155','Integration / UAT'],[2,'PROD','#15803D','Approved releases']]){
 const x=85+i*362, center=x+163;
 text(label+'-heading',label,x,615,326,30,20,false,'center',colour);
 box(label+'-server-1',center-10,656,20,8,null,'#64748B',2);
 box(label+'-server-2',center-10,669,20,8,null,'#64748B',2);
 box(label+'-led-1',center-7,659,1.6,1.6,'#64748B',null,0,'ellipse');
 box(label+'-led-2',center-7,672,1.6,1.6,'#64748B',null,0,'ellipse');
 text(label+'-resources','Azure resource groups',x,688,326,26,15,false,'center');
 rule(label+'-rule',x,731,326);
 text(label+'-infra-version','Infrastructure vX',x,752,326,26,15,false,'center');
 text(label+'-app-version','Application vY',x,784,326,26,15,false,'center');
 text(label+'-caption',caption,x,817,326,32,20,false,'center');
}
text('tenancy-footer','Each environment has independent infrastructure, application deployments, identities and configuration.',74,874,1072,30,20,false,'center');
mkdirSync('artifacts/reference-diagram',{recursive:true});
writeFileSync('artifacts/reference-diagram/operations.json',JSON.stringify(ops,null,2));
console.log(ops.length+' operations');
