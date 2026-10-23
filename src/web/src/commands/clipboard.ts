import type { ConnectorElement, Element, Endpoint, Operation, Page, Snapshot } from '../contracts/generated';

export interface ClipboardData { pageId: string; elements: Element[] }

/** Copies the selected elements. Connectors travel only if both ends are in the selection; others would point outside the copy. */
export function copyElements(snapshot: Snapshot, ids: string[]): ClipboardData | null {
  const page = snapshot.document.pages.find(p => ids.some(id => p.elements.some(e => e.id === id)));
  if (!page) return null;
  const wanted = new Set(ids);
  const elements = page.elements.filter(e => wanted.has(e.id) && e.kind !== 'group').filter(e =>
    e.kind !== 'connector' || [e.from, e.to].every(ep => ep.glue === 'none' || (ep.elementId && wanted.has(ep.elementId))));
  return elements.length ? { pageId: page.id, elements: structuredClone(elements) as Element[] } : null;
}

function uniqueAlias(alias: string, taken: Set<string>): string {
  if (!taken.has(alias)) return alias;
  let n = 2;
  while (taken.has(`${alias}-${n}`)) n++;
  return `${alias}-${n}`;
}

/**
 * Builds create operations for a paste. Every element gets a fresh UUID (supplied by `newId`), aliases are made unique
 * on the target page, and connectors between copied elements are re-attached to the copies.
 */
export function pasteOperations(clip: ClipboardData, target: Page, offsetPt: number, newId: () => string): { operations: Operation[]; newIds: string[] } {
  const idMap = new Map<string, string>();
  for (const e of clip.elements) idMap.set(e.id, newId());
  const taken = new Set(target.elements.map(e => e.alias).filter((a): a is string => !!a));
  const layerOk = (ids: string[]) => ids.filter(id => target.layers.some(l => l.id === id));
  const operations: Operation[] = [];
  const base = (e: Element) => {
    const o: { id: string; pageId: string; layerIds: string[]; name?: string; alias?: string } = { id: idMap.get(e.id)!, pageId: target.id, layerIds: layerOk(e.layerIds) };
    if (e.name !== undefined) o.name = e.name;
    if (e.alias) { const a = uniqueAlias(e.alias, taken); taken.add(a); o.alias = a; }
    return o;
  };
  const moved = (e: Element) => ({ x: e.bounds.x + offsetPt, y: e.bounds.y + offsetPt, width: e.bounds.width, height: e.bounds.height });
  // Shapes first, then connectors, so every endpoint exists when the connector is created.
  for (const e of clip.elements) {
    if (e.kind === 'shape') {
      operations.push({
        op: 'createShape', ...base(e), bounds: moved(e), rotationDeg: e.rotationDeg, geometry: { ...e.geometry }, style: { ...e.style },
        ...(e.text ? { text: { ...e.text } } : {}), ...(e.ports ? { ports: e.ports.map(p => ({ ...p })) } : {}),
      });
    } else if (e.kind === 'text') {
      operations.push({ op: 'createText', ...base(e), bounds: moved(e), rotationDeg: e.rotationDeg, text: { ...e.text } });
    } else if (e.kind === 'image') {
      operations.push({
        op: 'createImage', ...base(e), bounds: moved(e), rotationDeg: e.rotationDeg, assetId: e.assetId, fit: e.fit, opacity: e.opacity,
        preserveAspectRatio: e.preserveAspectRatio,
      });
    }
  }
  // A branch is created after the connector it joins, so order the connectors by how deep their joins run.
  const connectors = clip.elements.filter((e): e is ConnectorElement => e.kind === 'connector');
  const byId = new Map(connectors.map(c => [c.id, c] as const));
  const depth = (c: ConnectorElement, seen = new Set<string>()): number => {
    seen.add(c.id);
    const ids = [c.from, c.to].map(ep => ep.join?.connectorId).filter((id): id is string => !!id && byId.has(id) && !seen.has(id));
    return ids.length ? 1 + Math.max(...ids.map(id => depth(byId.get(id)!, new Set(seen)))) : 0;
  };
  for (const c of connectors.map(c => [depth(c), c] as const).sort((a, b) => a[0] - b[0]).map(x => x[1])) {
    const remap = (ep: Endpoint) => ep.join && idMap.has(ep.join.connectorId)
      ? { glue: 'none' as const, join: { connector: idMap.get(ep.join.connectorId)!, position: ep.join.position } }
      : ep.glue === 'none'
        ? { glue: 'none' as const, point: { x: ep.point!.x + offsetPt, y: ep.point!.y + offsetPt } }
        : { glue: ep.glue, elementId: idMap.get(ep.elementId!)!, ...(ep.port ? { port: ep.port } : {}) };
    operations.push({
      op: 'createConnector', ...base(c), from: remap(c.from), to: remap(c.to), route: c.route,
      waypoints: c.waypoints.map(p => ({ x: p.x + offsetPt, y: p.y + offsetPt })), style: { ...c.style }, ...(c.label ? { label: { ...c.label } } : {}),
      ...(c.hops ? { hops: c.hops } : {}),
    });
  }
  return { operations, newIds: [...idMap.values()] };
}
