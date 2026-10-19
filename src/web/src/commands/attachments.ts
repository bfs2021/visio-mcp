import type { Attachment, ConnectorElement, DiagramDocument, Element, Page, Projection, ShapeElement, Snapshot, TextElement } from '../contracts/generated';
import type { Result } from '../model/result';
import { applyDiff, type DiffBody } from './diff';
import { centreOf } from './geometry';
import { nearestOnRoute, pointOnRoute } from './pathAnchor';
import type { Plan } from './planPatch';

type Attached = ShapeElement | TextElement;
export type RouteSource = (snapshot: Snapshot) => Result<Projection>;

const isAttached = (e: Element): e is Attached & { attachment: Attachment } => (e.kind === 'shape' || e.kind === 'text') && e.attachment !== undefined;
const hasAttachments = (doc: DiagramDocument): boolean => doc.pages.some(p => p.elements.some(isAttached));

/**
 * Keeps the elements attached to connectors where their connectors are. After a plan, every attached element is put on its place along the
 * route as drawn; one the person (or an agent) moved in the same step is instead re-attached where it was put, so dragging slides it along
 * the line; one whose connector is gone is detached and stays where it is. The changes join the plan, so one undo returns all of it.
 */
export function settleAttachments(before: DiagramDocument, plan: Plan, source: RouteSource | null, snapshotOf: (doc: DiagramDocument) => Snapshot): Plan {
  if (plan.noChange) return plan;
  const after = applyDiff(before, plan.forward, before.revision + 1);
  if (!hasAttachments(after) || !source) return plan;
  const projected = source(snapshotOf(after));
  if (!projected.ok) return plan;
  const routes = projected.value.connectors;

  const beforeById = new Map<string, Element>();
  for (const p of before.pages) for (const e of p.elements) beforeById.set(e.id, e);
  const touched = new Set([...plan.created, ...plan.changed]);
  const forward: DiffBody = { upserts: { ...plan.forward.upserts, elements: plan.forward.upserts.elements.slice() }, deletes: plan.forward.deletes };
  const inverse: DiffBody = { upserts: { ...plan.inverse.upserts, elements: plan.inverse.upserts.elements.slice() }, deletes: plan.inverse.deletes };
  const changed: string[] = [], pageIds = new Set(plan.changedPageIds);

  const put = (page: Page, next: Element) => {
    const old = beforeById.get(next.id);
    const f = forward.upserts.elements.findIndex(u => u.element.id === next.id);
    if (f >= 0) forward.upserts.elements[f] = { ...forward.upserts.elements[f]!, element: next };
    else forward.upserts.elements.push({ pageId: page.id, element: next });
    if (old && !inverse.upserts.elements.some(u => u.element.id === next.id)) inverse.upserts.elements.push({ pageId: page.id, element: old });
    if (old && !plan.created.includes(next.id)) changed.push(next.id);
    pageIds.add(page.id);
  };

  for (const page of after.pages) {
    const byId = new Map(page.elements.map(e => [e.id, e] as const));
    for (const el of page.elements) {
      if (!isAttached(el)) continue;
      const connector = byId.get(el.attachment.connectorId);
      if (!connector || connector.kind !== 'connector') { const { attachment: _gone, ...rest } = el; put(page, rest as Element); continue; }
      const route = routes[connector.id]?.routePoints;
      if (!route?.length) continue;
      const old = beforeById.get(el.id) as Attached | undefined;
      const connectorMoved = touched.has(connector.id) || endsTouched(connector, touched);
      const putThere = old && !connectorMoved && el.attachment === (old as Attached).attachment && !sameCentre(old, el);
      const attachment = el.attachment;
      if (putThere) {
        const spot = nearestOnRoute(route, centreOf(el.bounds));
        if (spot) { put(page, { ...el, attachment: { ...attachment, ...spot } } as Element); continue; } // it stays exactly where it was put
      }
      const on = pointOnRoute(route, attachment.position, attachment.offsetPt);
      if (!on) continue;
      const b = el.bounds;
      const bounds = { ...b, x: round(on.point.x - b.width / 2), y: round(on.point.y - b.height / 2) };
      if (Math.abs(bounds.x - b.x) < 0.005 && Math.abs(bounds.y - b.y) < 0.005) continue;
      put(page, { ...el, bounds } as Element);
    }
  }
  if (changed.length === 0 && forward.upserts.elements.length === plan.forward.upserts.elements.length && forward.upserts.elements.every((u, i) => u === plan.forward.upserts.elements[i])) return plan;
  return {
    ...plan, forward, inverse,
    changed: [...new Set([...plan.changed, ...changed])], changedPageIds: [...pageIds],
    visuallyAffectedIds: [...new Set([...plan.visuallyAffectedIds, ...changed])],
  };
}

const round = (n: number): number => Math.round(n * 100) / 100;
const endsTouched = (c: ConnectorElement, touched: ReadonlySet<string>): boolean => touched.has(c.from.elementId ?? '') || touched.has(c.to.elementId ?? '');
const sameCentre = (a: Attached, b: Attached): boolean => {
  const p = centreOf(a.bounds), q = centreOf(b.bounds);
  return Math.abs(p.x - q.x) < 0.005 && Math.abs(p.y - q.y) < 0.005;
};

