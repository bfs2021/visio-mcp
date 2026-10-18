import data from '../../../../contracts/icons/catalog.json';
import type { Operation } from '../contracts/generated';
import { fail, ok, type Result } from '../model/result';

export const iconCatalog = data;
export type CatalogIcon = typeof data.icons[number];
const uuid = /^[a-f\d]{8}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{12}$/i;

export function searchIcons(query: unknown = '', limit: unknown = 24) {
  if (typeof query !== 'string' || query.length > 256 || !Number.isInteger(limit) || (limit as number) < 1 || (limit as number) > 50)
    return fail<{ icons: CatalogIcon[]; total: number }>('invalid_request', 'query must be at most 256 characters and limit an integer from 1 to 50');
  const words = query.trim().toLowerCase().split(/[\s:-]+/).filter(Boolean);
  const matches = data.icons.filter(icon => words.every(w => [icon.id, icon.name, ...icon.tags].join(' ').includes(w)));
  matches.sort((a, b) => (b.name === query.trim().toLowerCase() ? 1 : 0) - (a.name === query.trim().toLowerCase() ? 1 : 0) || a.id.localeCompare(b.id));
  return ok({ icons: matches.slice(0, limit as number), total: matches.length });
}

export interface IconPlacement { icon: CatalogIcon; elementId: string; operations: Operation[]; sizePt: number; colour: string; strokeWidthPt: number }
export function iconOperations(p: Record<string, unknown>): Result<IconPlacement> {
  const keys = new Set(['iconId', 'catalogVersion', 'pageId', 'elementId', 'x', 'y', 'sizePt', 'colour', 'strokeWidthPt']);
  if (Object.keys(p).some(k => !keys.has(k))) return fail('invalid_request', 'unknown icon placement option');
  const icon = data.icons.find(i => i.id === p.iconId);
  if (!icon) return fail('not_found', 'unknown iconId; call search_icons for available IDs');
  if (p.catalogVersion !== undefined && p.catalogVersion !== data.version) return fail('invalid_request', 'that catalogue version is unavailable; search again before choosing an icon');
  if (typeof p.pageId !== 'string' || !uuid.test(p.pageId)) return fail('invalid_request', 'pageId must be a UUID');
  const id = p.elementId ?? crypto.randomUUID();
  if (typeof id !== 'string' || !uuid.test(id)) return fail('invalid_request', 'elementId must be a UUID');
  const x = p.x ?? 0, y = p.y ?? 0, size = p.sizePt ?? 24, colour = p.colour ?? '#334155', stroke = p.strokeWidthPt ?? (typeof size === 'number' ? size / 12 : 2);
  if (![x, y, size, stroke].every(v => typeof v === 'number' && Number.isFinite(v)) || Math.abs(x as number) > 1e6 || Math.abs(y as number) > 1e6 || (size as number) < 1 || (size as number) > 1000 || (stroke as number) <= 0 || (stroke as number) > (size as number) / 2 || typeof colour !== 'string' || !/^#[\da-f]{6}$/i.test(colour))
    return fail('invalid_request', 'icon needs finite position, sizePt 1–1000, positive strokeWidthPt no greater than half its size, and a #RRGGBB colour');
  const operations: Operation[] = [
    { op: 'createShape', id, pageId: p.pageId, name: icon.name, bounds: { x: x as number, y: y as number, width: size as number, height: size as number },
      geometry: { svgPath: icon.path }, style: { fill: { kind: 'none' }, stroke: { kind: 'solid', colour, opacity: 1 }, strokeWidthPt: stroke as number, lineCap: 'round', lineJoin: 'round' } },
    { op: 'set', target: id, patch: { metadata: { 'icon.id': icon.id, 'icon.catalogVersion': data.version, 'icon.geometrySha256': icon.sha256, 'icon.sourceCommit': data.sourceCommit, 'icon.license': data.licenceText } } },
  ];
  return ok({ icon, elementId: id, operations, sizePt: size as number, colour, strokeWidthPt: stroke as number });
}

export function iconInfo(icon: CatalogIcon) { return { id: icon.id, name: icon.name, tags: [...icon.tags], geometrySha256: icon.sha256 }; }
