import type { Element, Layer, RoutingPolicy, TextBlock } from '../contracts/generated';
import { effectiveState } from '../canvas/cellMapping';
import { measureTextBox, textInsets, type TextWidthMeasurer } from './textMeasurement';
import type { Rect } from './obstacleRoute';
import { isLaneStructure } from './swimlane';
import { BACKGROUND_SHARE, visualRect } from './obstacleGeometry';

export type EffectiveObstaclePolicy = 'forbidden' | 'avoid' | 'passThrough';
export interface RoutingObstacle {
  id: string;
  rect: Rect;
  policy: Exclude<EffectiveObstaclePolicy, 'passThrough'>;
  clearancePt: number;
  source: 'element' | 'text';
}

const contains = (outer: Rect, inner: Rect, tol = 0.5): boolean =>
  outer.x <= inner.x + tol && outer.y <= inner.y + tol && outer.x + outer.width >= inner.x + inner.width - tol && outer.y + outer.height >= inner.y + inner.height - tol;
const holds = (r: Rect, p: { x: number; y: number }): boolean => p.x > r.x && p.x < r.x + r.width && p.y > r.y && p.y < r.y + r.height;
const defaultMeasureWidth: TextWidthMeasurer = (value, font) => value.length * font.sizePt * 0.5;
const policyOf = (element: Element): RoutingPolicy | undefined => 'routingPolicy' in element ? element.routingPolicy : undefined;

/** Resolves an explicit policy first; legacy-selected obstacles remain hard obstacles. */
export function effectiveObstaclePolicy(
  element: Element,
  endIds: ReadonlySet<string>,
  endRects: readonly Rect[],
  freePoints: ReadonlyArray<{ x: number; y: number }>,
  pageArea: number,
): EffectiveObstaclePolicy | undefined {
  if (element.kind === 'connector' || element.kind === 'group') return undefined;
  const requested = policyOf(element)?.obstacle;
  if (requested === 'passThrough' || requested === 'avoid' || requested === 'forbidden') return requested;
  if (element.kind !== 'shape' || endIds.has(element.id)) return undefined;
  const rect = visualRect(element);
  if (rect.width * rect.height >= BACKGROUND_SHARE * pageArea) return undefined;
  if (endRects.some(e => contains(rect, e)) || freePoints.some(p => holds(rect, p))) return undefined;
  return 'forbidden';
}

function textRect(element: Exclude<Element, { kind: 'image' } | { kind: 'group' }>, text: TextBlock, measureWidth: TextWidthMeasurer): Rect | undefined {
  const measured = measureTextBox(text, element.bounds.width, element.bounds.height, measureWidth);
  if (measured.contentWidthPt <= 0 || measured.contentHeightPt <= 0) return undefined;
  const insets = textInsets(text), width = measured.contentWidthPt, height = measured.contentHeightPt;
  const x = text.horizontalAlign === 'center' ? element.bounds.x + (element.bounds.width - width) / 2
    : text.horizontalAlign === 'right' ? element.bounds.x + element.bounds.width - insets.right - width : element.bounds.x + insets.left;
  const y = text.verticalAlign === 'middle' ? element.bounds.y + (element.bounds.height - height) / 2
    : text.verticalAlign === 'bottom' ? element.bounds.y + element.bounds.height - insets.bottom - height : element.bounds.y + insets.top;
  const local = { x, y, width, height };
  const angle = element.rotationDeg * Math.PI / 180;
  if (!angle) return local;
  const cx = element.bounds.x + element.bounds.width / 2, cy = element.bounds.y + element.bounds.height / 2;
  const corners = [
    { x, y }, { x: x + width, y }, { x: x + width, y: y + height }, { x, y: y + height },
  ].map(p => ({ x: cx + (p.x - cx) * Math.cos(angle) - (p.y - cy) * Math.sin(angle), y: cy + (p.x - cx) * Math.sin(angle) + (p.y - cy) * Math.cos(angle) }));
  const left = Math.min(...corners.map(p => p.x)), top = Math.min(...corners.map(p => p.y));
  return { x: left, y: top, width: Math.max(...corners.map(p => p.x)) - left, height: Math.max(...corners.map(p => p.y)) - top };
}

/** Returns hard/soft obstacle records after applying explicit policy or the unchanged legacy candidate heuristic. */
export function selectObstacles(
  elements: readonly Element[],
  layers: readonly Layer[],
  endIds: ReadonlySet<string>,
  endRects: readonly Rect[],
  freePoints: ReadonlyArray<{ x: number; y: number }>,
  pageArea: number,
  measureWidth: TextWidthMeasurer = defaultMeasureWidth,
  connectorId?: string,
): RoutingObstacle[] {
  const legacyCandidates: Array<{ id: string; rect: Rect }> = [];
  const explicit: RoutingObstacle[] = [];
  const textRecords: RoutingObstacle[] = [];
  for (const el of elements) {
    if (el.kind === 'group') continue;
    const policy = policyOf(el);
    const isHidden = !effectiveState(el, layers).visible;
    if (isHidden) continue;
    if (policy?.text === 'avoid' && el.id !== connectorId) {
      const text = el.kind === 'shape' || el.kind === 'text' ? el.text : el.kind === 'connector' ? el.label : undefined;
      if (text && el.kind !== 'image') {
        const rect = textRect(el, text, measureWidth);
        if (rect) textRecords.push({ id: el.id, rect, policy: 'forbidden', clearancePt: policy.clearancePt ?? 8, source: 'text' });
      }
    }
    if (el.kind === 'connector') continue;
    const requested = policy?.obstacle;
    const isExplicit = requested === 'forbidden' || requested === 'avoid' || requested === 'passThrough';
    if (isExplicit && requested === 'passThrough') continue;
    const effective = effectiveObstaclePolicy(el, endIds, endRects, freePoints, pageArea);
    if (!effective || effective === 'passThrough') continue;
    const rect = visualRect(el);
    if (isExplicit) {
      explicit.push({ id: el.id, rect, policy: effective, clearancePt: policy?.clearancePt ?? 8, source: 'element' });
      continue;
    }
    if (el.kind !== 'shape' || isLaneStructure(el)) continue;
    if (el.style.fill.kind === 'none' && el.style.stroke.kind === 'none') continue;
    legacyCandidates.push({ id: el.id, rect });
  }
  const legacy = legacyCandidates
    .filter(c => !legacyCandidates.some(o => o.id !== c.id && o.rect.width * o.rect.height < c.rect.width * c.rect.height && contains(c.rect, o.rect)))
    .map(c => ({ id: c.id, rect: c.rect, policy: 'forbidden' as const, clearancePt: policyOf(elements.find(e => e.id === c.id)!)?.clearancePt ?? 8, source: 'element' as const }));
  return [...explicit, ...legacy, ...textRecords];
}