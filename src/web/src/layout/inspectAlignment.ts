import type { Bounds, Element, Page } from '../contracts/generated';
import type { LayoutIssue } from './inspectLayout';

/** Conservative hints, never constraints. Only top-level, comparable, unrotated boxes are considered. */
export function inspectNearAlignments(page: Page, shown: Element[], parents: ReadonlyMap<string, string>): { issues: LayoutIssue[]; truncated: boolean } {
  const candidates = shown.filter(e => {
    if (e.kind !== 'shape' && e.kind !== 'image' || parents.has(e.id) || Math.abs(e.rotationDeg % 360) > 0.01) return false;
    if (e.kind === 'image' && e.opacity <= 0) return false;
    if (e.kind === 'shape' && (e.geometry.svgPath !== undefined || !paintedShape(e))) return false;
    const { width: w, height: h } = e.bounds;
    return Math.min(w, h) >= 8 && Math.max(w, h) / Math.min(w, h) <= 8;
  }).sort((a, b) => a.id.localeCompare(b.id));
  const issues: LayoutIssue[] = [];
  let comparisons = 0;
  for (let i = 0; i < candidates.length; i++) for (let j = i + 1; j < candidates.length; j++) {
    if (++comparisons > 50_000 || issues.length >= 100) return { issues, truncated: true };
    const a = candidates[i]!, b = candidates[j]!, x = a.bounds, y = b.bounds;
    if (a.kind !== b.kind || a.kind === 'shape' && b.kind === 'shape' && a.geometry.preset !== b.geometry.preset) continue;
    if (Math.max(x.width, y.width) / Math.min(x.width, y.width) > 1.25 || Math.max(x.height, y.height) / Math.min(x.height, y.height) > 1.25) continue;
    const horizontalGap = Math.max(x.x, y.x) - Math.min(x.x + x.width, y.x + y.width);
    const verticalGap = Math.max(x.y, y.y) - Math.min(x.y + x.height, y.y + y.height);
    const axis = horizontalGap >= 4 && horizontalGap <= 4 * Math.max(x.width, y.width) && verticalGap <= -0.5 * Math.min(x.height, y.height) ? 'y'
      : verticalGap >= 4 && verticalGap <= 4 * Math.max(x.height, y.height) && horizontalGap <= -0.5 * Math.min(x.width, y.width) ? 'x' : undefined;
    if (!axis) continue;
    const size = axis === 'y' ? 'height' : 'width';
    const anchors = [0, 0.5, 1].map(f => ({ f, a: x[axis] + f * x[size], b: y[axis] + f * y[size] }));
    // Any already aligned edge/centre is evidence of an intentional arrangement.
    if (anchors.some(p => Math.abs(p.a - p.b) <= 0.5)) continue;
    const best = anchors.sort((a, b) => Math.abs(a.a - a.b) - Math.abs(b.a - b.b))[0]!;
    const delta = Math.abs(best.a - best.b), tolerance = Math.min(12, 0.25 * Math.min(x[size], y[size]));
    if (delta > tolerance) continue;
    const bounds: Bounds = { x: Math.min(x.x, y.x), y: Math.min(x.y, y.y), width: Math.max(x.x + x.width, y.x + y.width) - Math.min(x.x, y.x), height: Math.max(x.y + x.height, y.y + y.height) - Math.min(x.y, y.y) };
    issues.push({ code: 'NEAR_ALIGNMENT', severity: 'info', elementIds: [a.id, b.id], pageId: page.id, bounds,
      message: 'Comparable peer shapes are nearly aligned. Check whether this offset is intentional before changing either object.',
      evidence: { heuristic: true, axis, anchor: best.f === 0.5 ? 'centre' : best.f === 0 ? 'start' : 'end', deltaPt: delta, tolerancePt: tolerance, coordinatesPt: { [a.id]: best.a, [b.id]: best.b } } });
  }
  return { issues, truncated: false };
}

function paintedShape(e: Extract<Element, { kind: 'shape' }>): boolean {
  return e.style.fill.kind === 'solid' && e.style.fill.opacity > 0 || e.style.stroke.kind === 'solid' && e.style.stroke.opacity > 0 && e.style.strokeWidthPt > 0 || !!e.text?.value.trim();
}

/** Painted divider rectangles have meaningful visible area despite a dimension below one point. */
export function isVisibleThinRule(el: Element): boolean {
  if (el.kind !== 'shape' || el.geometry.preset !== 'rectangle' || el.geometry.svgPath !== undefined) return false;
  const short = Math.min(el.bounds.width, el.bounds.height), long = Math.max(el.bounds.width, el.bounds.height);
  const painted = el.style.fill.kind === 'solid' && el.style.fill.opacity > 0 || el.style.stroke.kind === 'solid' && el.style.stroke.opacity > 0 && el.style.strokeWidthPt >= 0.25;
  return painted && short >= 0.25 && short < 1 && long >= 16 && long / short >= 20;
}
