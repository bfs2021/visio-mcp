import type { Bounds, Element, Guide, Page, Operation } from '../contracts/generated';
export interface GuideSnapOptions { enabled: boolean; scale: number; preserveSize?: boolean }
export const layersSnap = (page: Page, element: Pick<Element, 'layerIds'>) => element.layerIds.every(id => page.layers.find(l => l.id === id)?.snap !== false);
/** Choose the nearest authored guide, then page position and UUID for deterministic ties. */
export function guideCorrection(page: Page, axis: 'x' | 'y', anchors: number[], options?: GuideSnapOptions): number | undefined {
 if (!options?.enabled || !Number.isFinite(options.scale) || options.scale <= 0) return;
 const orientation: Guide['orientation'] = axis === 'x' ? 'vertical' : 'horizontal';
 const candidates = page.guides.filter(g => g.orientation === orientation).flatMap(g => anchors.map(anchor => ({delta:g.positionPt-anchor,guide:g})));
 candidates.sort((a,b)=>Math.abs(a.delta)-Math.abs(b.delta)||a.guide.positionPt-b.guide.positionPt||a.guide.id.localeCompare(b.guide.id));
 const best=candidates[0]; return best && Math.abs(best.delta)*options.scale <= 8 ? best.delta : undefined;
}
export const anchorsOf = (b: Bounds, axis: 'x' | 'y') => axis === 'x' ? [b.x,b.x+b.width/2,b.x+b.width] : [b.y,b.y+b.height/2,b.y+b.height];

/** New GUI objects use their authored destination layers, never an unrelated active layer. */
export function snapCreation(op: Operation,page: Page,options: GuideSnapOptions): Operation {
 if(op.op!=='createShape' && op.op!=='createText' && op.op!=='createImage')return op;
 if(!layersSnap(page,{layerIds:op.layerIds??[]}))return op;
 const g=page.grid;const roundGrid=(v:number)=>g.snap && op.op!=='createImage' && !options.preserveSize?Math.round(v/g.spacingPt)*g.spacingPt:v;
 const b={...op.bounds,width:g.snap&&op.op!=='createImage'&&!options.preserveSize?Math.max(g.spacingPt,roundGrid(op.bounds.width)):op.bounds.width,height:g.snap&&op.op!=='createImage'?Math.max(g.spacingPt,roundGrid(op.bounds.height)):op.bounds.height};
 const gx=guideCorrection(page,'x',anchorsOf(b,'x'),options),gy=guideCorrection(page,'y',anchorsOf(b,'y'),options);
 return {...op,bounds:{x:gx===undefined?roundGrid(b.x):b.x+gx,y:gy===undefined?roundGrid(b.y):b.y+gy,width:b.width,height:b.height}};
}
