import type { AlignOp, Bounds, DistributeOp, Operation, ReorderOp, SetGapOp, Snapshot } from '../contracts/generated';
import { fail, ok, type Result } from '../model/result';
import { visualBounds } from './geometry';
import { defaultScope, lookupTarget } from './resolveTarget';

export type ArrangeOp = AlignOp | DistributeOp | SetGapOp;

export interface ArrangeItem {
  id: string;
  /** Axis-aligned visual bounds. */
  box: Bounds;
  /** Position in the caller's target list; breaks ties between equal positions. */
  order: number;
}
export interface Shift { dx: number; dy: number }

const bad = (message: string, reason: string): Result<never> => fail('invalid_request', message, { details: { reason } });

/**
 * Pure arithmetic of align, distribute and set-gap. Input order only breaks ties between equal positions; nothing else on the
 * page is laid out. Returns a shift for every item (zero when it already sits where it should).
 */
export function computeArrange(items: readonly ArrangeItem[], op: ArrangeOp): Result<Map<string, Shift>> {
  const shifts = new Map<string, Shift>(items.map(i => [i.id, { dx: 0, dy: 0 }]));
  const min = op.op === 'distribute' ? 3 : 2;
  if (items.length < min) return bad(`${op.op} needs at least ${min} targets`, 'too_few_targets');

  if (op.op === 'align') {
    const left = Math.min(...items.map(i => i.box.x)), right = Math.max(...items.map(i => i.box.x + i.box.width));
    const top = Math.min(...items.map(i => i.box.y)), bottom = Math.max(...items.map(i => i.box.y + i.box.height));
    for (const i of items) {
      const s = shifts.get(i.id)!;
      switch (op.edge) {
        case 'left': s.dx = left - i.box.x; break;
        case 'right': s.dx = right - (i.box.x + i.box.width); break;
        case 'center': s.dx = (left + right) / 2 - (i.box.x + i.box.width / 2); break;
        case 'top': s.dy = top - i.box.y; break;
        case 'bottom': s.dy = bottom - (i.box.y + i.box.height); break;
        case 'middle': s.dy = (top + bottom) / 2 - (i.box.y + i.box.height / 2); break;
      }
    }
    return ok(shifts);
  }

  const horizontal = op.axis === 'horizontal';
  const start = (b: Bounds) => (horizontal ? b.x : b.y);
  const size = (b: Bounds) => (horizontal ? b.width : b.height);
  const sorted = items.slice().sort((a, b) => start(a.box) - start(b.box) || a.order - b.order);
  const put = (i: ArrangeItem, position: number) => {
    const s = shifts.get(i.id)!;
    if (horizontal) s.dx = position - i.box.x; else s.dy = position - i.box.y;
  };

  if (op.op === 'setGap') {
    let cursor = start(sorted[0]!.box) + size(sorted[0]!.box);
    for (const i of sorted.slice(1)) { put(i, cursor + op.gapPt); cursor = cursor + op.gapPt + size(i.box); }
    return ok(shifts);
  }

  // distribute: the first and last (by position) stay put and the free space between them is divided equally
  const first = sorted[0]!, last = sorted[sorted.length - 1]!;
  const span = start(last.box) + size(last.box) - start(first.box);
  const gap = (span - sorted.reduce((sum, i) => sum + size(i.box), 0)) / (sorted.length - 1);
  let cursor = start(first.box) + size(first.box);
  for (const i of sorted.slice(1, -1)) { put(i, cursor + gap); cursor = cursor + gap + size(i.box); }
  return ok(shifts);
}

/** Arrange as plain `move` operations on resolved UUIDs, for callers that want the effect without committing it. */
export function planArrange(snapshot: Snapshot, op: ArrangeOp, pageId?: string): Result<Operation[]> {
  const doc = snapshot.document;
  const scope = pageId ?? defaultScope(doc);
  const items: ArrangeItem[] = [];
  let page: number | undefined;
  for (const [order, t] of op.targets.entries()) {
    const hit = lookupTarget(doc.pages, t, scope);
    if (!hit.ok) return fail(hit.code, hit.message, { details: hit.details });
    if (page !== undefined && hit.pageIdx !== page) return bad('arrange targets must all be on one page', 'mixed_pages');
    page = hit.pageIdx;
    const el = doc.pages[hit.pageIdx]!.elements[hit.index]!;
    items.push({ id: el.id, box: visualBounds(el), order });
  }
  const shifts = computeArrange(items, op);
  if (!shifts.ok) return shifts;
  const ops: Operation[] = [];
  for (const i of items) {
    const s = shifts.value.get(i.id)!;
    if (s.dx !== 0 || s.dy !== 0) ops.push({ op: 'move', target: i.id, delta: { xPt: s.dx, yPt: s.dy } });
  }
  return ok(ops);
}

// ---------------------------------------------------------------------------- z order

export interface ZEntry { id: string; z: number; order: number }

/**
 * New zIndex values for the entries that must change so the `block` ends up in the requested place. Front and back place the block
 * beyond the current extremes and touch nothing else; forward and backward swap with the neighbour they pass, and report it.
 */
export function planZ(entries: readonly ZEntry[], block: ReadonlySet<string>, to: ReorderOp['to']): Map<string, number> {
  const out = new Map<string, number>();
  const inBlock = entries.filter(e => block.has(e.id)).sort((a, b) => a.z - b.z || a.order - b.order);
  const rest = entries.filter(e => !block.has(e.id));
  if (!inBlock.length || !rest.length) return out;

  if (to === 'front' || to === 'back') {
    if (to === 'front' ? inBlock[0]!.z > Math.max(...rest.map(e => e.z)) : inBlock[inBlock.length - 1]!.z < Math.min(...rest.map(e => e.z))) return out;
    if (to === 'front') {
      const top = Math.max(...entries.map(e => e.z));
      inBlock.forEach((e, k) => out.set(e.id, top + 1 + k));
    } else {
      const bottom = Math.min(...entries.map(e => e.z));
      inBlock.forEach((e, k) => out.set(e.id, bottom - inBlock.length + k));
    }
    return out;
  }

  const list = entries.slice().sort((a, b) => a.z - b.z || a.order - b.order);
  const tied = list.some((e, i) => i > 0 && e.z === list[i - 1]!.z);
  const values = tied ? list.map((_, i) => i + 1) : list.map(e => e.z);
  const swap = (i: number, j: number) => { const t = list[i]!; list[i] = list[j]!; list[j] = t; };
  if (to === 'forward') {
    for (let i = list.length - 2; i >= 0; i--) if (block.has(list[i]!.id) && !block.has(list[i + 1]!.id)) swap(i, i + 1);
  } else {
    for (let i = 1; i < list.length; i++) if (block.has(list[i]!.id) && !block.has(list[i - 1]!.id)) swap(i, i - 1);
  }
  list.forEach((e, i) => { if (values[i] !== e.z) out.set(e.id, values[i]!); });
  return out;
}
