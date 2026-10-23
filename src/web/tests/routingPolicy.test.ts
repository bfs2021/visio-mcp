import { describe, expect, it } from 'vitest';
import type { Element, TextBlock } from '../src/contracts/generated';
import { effectiveObstaclePolicy, selectObstacles } from '../src/layout/routingPolicy';

const solid = { kind: 'solid', colour: '#fff', opacity: 1 } as const;
const shape = (id: string, x: number, y: number, width: number, height: number, extra: object = {}): Element => ({
  id, kind: 'shape', layerIds: [], zIndex: 1, locked: false, hidden: false, bounds: { x, y, width, height }, rotationDeg: 0, metadata: {}, geometry: { preset: 'rectangle' },
  style: { fill: solid, stroke: solid, strokeWidthPt: 1, dash: 'solid', lineCap: 'butt', lineJoin: 'miter' }, ...extra,
}) as Element;
const opts = { endIds: new Set<string>(), endRects: [] as Array<{ x: number; y: number; width: number; height: number }>, freePoints: [] as Array<{ x: number; y: number }>, pageArea: 1_000_000 };
const measureWidth = (value: string) => value.length * 10;

const textBlock: TextBlock = {
  value: 'hello', fontFamily: 'Test', fontSizePt: 10, bold: false, italic: false, underline: false, colour: '#000000',
  horizontalAlign: 'left', verticalAlign: 'top', wrap: false, paddingPt: 2,
};

describe('routing policy resolution', () => {
  it('legacy_policy_matches_existing_obstacle_selection', () => {
    const elements = [shape('backdrop', 0, 0, 900, 900), shape('container', 100, 100, 100, 100), shape('inner', 120, 120, 20, 20), shape('wall', 300, 300, 20, 20)];
    expect(selectObstacles(elements, [], opts.endIds, opts.endRects, opts.freePoints, opts.pageArea).map(o => o.id)).toEqual(['inner', 'wall']);
  });

  it('automatic_legacy_obstacles_keep_hard_behavior', () => {
    const wall = shape('wall', 50, 50, 20, 20, { routingPolicy: { obstacle: 'automatic' } });
    expect(effectiveObstaclePolicy(wall, opts.endIds, opts.endRects, opts.freePoints, opts.pageArea)).toBe('forbidden');
    expect(selectObstacles([wall], [], opts.endIds, opts.endRects, opts.freePoints, opts.pageArea)[0]).toMatchObject({ id: 'wall', policy: 'forbidden', clearancePt: 8, source: 'element' });
  });

  it('explicit_forbidden_large_region_is_not_filtered', () => {
    const wall = shape('wall', 0, 0, 900, 900, { routingPolicy: { obstacle: 'forbidden' } });
    expect(selectObstacles([wall], [], opts.endIds, opts.endRects, opts.freePoints, opts.pageArea)).toMatchObject([{ id: 'wall', policy: 'forbidden' }]);
  });

  it('pass_through_is_omitted', () => {
    const passage = shape('passage', 0, 0, 900, 900, { routingPolicy: { obstacle: 'passThrough' } });
    expect(effectiveObstaclePolicy(passage, opts.endIds, opts.endRects, opts.freePoints, opts.pageArea)).toBe('passThrough');
    expect(selectObstacles([passage], [], opts.endIds, opts.endRects, opts.freePoints, opts.pageArea)).toEqual([]);
  });

  it('protected_text_uses_measured_bounds', () => {
    const label = shape('label', 10, 20, 100, 40, { kind: 'text', text: textBlock, routingPolicy: { text: 'avoid' } });
    const selected = selectObstacles([label], [], opts.endIds, opts.endRects, opts.freePoints, opts.pageArea, measureWidth);
    expect(selected).toMatchObject([{ id: 'label', rect: { x: 12, y: 22, width: 50, height: 12 }, policy: 'forbidden', source: 'text' }]);
    const ignored = shape('ignored', 100, 20, 100, 40, { kind: 'text', text: textBlock });
    expect(selectObstacles([ignored], [], opts.endIds, opts.endRects, opts.freePoints, opts.pageArea, measureWidth)).toEqual([]);
  });
});