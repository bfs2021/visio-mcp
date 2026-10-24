import { describe, expect, it } from 'vitest';
import type { Element, TextBlock } from '../src/contracts/generated';
import { effectiveObstaclePolicy, passesBehind, selectObstacles } from '../src/layout/routingPolicy';

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

  // R23: levels decide what is behind the connector.
  const connector = (level?: number): Element => ({ ...shape('c', 0, 0, 0, 0), kind: 'connector', ...(level === undefined ? {} : { level }) }) as unknown as Element;

  it('a_shape_on_a_lower_level_than_the_connector_is_passed_through', () => {
    const behind = shape('behind', 50, 50, 20, 20, { level: -1 });
    const same = shape('same', 150, 50, 20, 20);
    expect(selectObstacles([behind, same, connector()], [], opts.endIds, opts.endRects, opts.freePoints, opts.pageArea, measureWidth, 'c').map(o => o.id)).toEqual(['same']);
    expect(passesBehind(behind, 0)).toBe(true);
    expect(passesBehind(same, 0)).toBe(false);
  });

  it('a_shape_on_the_same_or_a_higher_level_stays_an_obstacle', () => {
    const els = [shape('front', 50, 50, 20, 20, { level: 3 }), shape('level', 150, 50, 20, 20, { level: 2 }), connector(2)];
    expect(selectObstacles(els, [], opts.endIds, opts.endRects, opts.freePoints, opts.pageArea, measureWidth, 'c').map(o => o.id)).toEqual(['front', 'level']);
    // and a connector placed on a low level keeps avoiding what is in front of it
    expect(selectObstacles([shape('wall', 50, 50, 20, 20), connector(-1)], [], opts.endIds, opts.endRects, opts.freePoints, opts.pageArea, measureWidth, 'c').map(o => o.id)).toEqual(['wall']);
  });

  it('an_explicit_obstacle_policy_beats_the_level_rule', () => {
    const forbidden = shape('forbidden', 50, 50, 20, 20, { level: -5, routingPolicy: { obstacle: 'forbidden' } });
    const soft = shape('soft', 150, 50, 20, 20, { level: -5, routingPolicy: { obstacle: 'avoid' } });
    const automatic = shape('automatic', 250, 50, 20, 20, { level: -5, routingPolicy: { obstacle: 'automatic' } });
    const open = shape('open', 350, 50, 20, 20, { level: 4, routingPolicy: { obstacle: 'passThrough' } });
    const selected = selectObstacles([forbidden, soft, automatic, open, connector()], [], opts.endIds, opts.endRects, opts.freePoints, opts.pageArea, measureWidth, 'c');
    expect(selected.map(o => [o.id, o.policy])).toEqual([['forbidden', 'forbidden'], ['soft', 'avoid']]);
  });

  it('protected_text_on_a_lower_level_is_still_protected', () => {
    const label = shape('label', 10, 20, 100, 40, { kind: 'text', text: textBlock, level: -2, routingPolicy: { text: 'avoid' } });
    expect(selectObstacles([label, connector()], [], opts.endIds, opts.endRects, opts.freePoints, opts.pageArea, measureWidth, 'c')).toMatchObject([{ id: 'label', source: 'text' }]);
  });

  it('a_shape_behind_the_connector_still_makes_the_shape_around_it_a_container', () => {
    // before levels, a frame holding a smaller shape was a container, not an obstacle; sending the inner shape back must not change that
    const frame = shape('frame', 100, 100, 200, 200), inner = shape('inner', 150, 150, 20, 20, { level: -1 });
    expect(selectObstacles([frame, inner, connector()], [], opts.endIds, opts.endRects, opts.freePoints, opts.pageArea, measureWidth, 'c')).toEqual([]);
  });

  it('without_a_connector_the_level_rule_does_not_apply', () => {
    expect(selectObstacles([shape('behind', 50, 50, 20, 20, { level: -1 })], [], opts.endIds, opts.endRects, opts.freePoints, opts.pageArea).map(o => o.id)).toEqual(['behind']);
  });
});