import { describe, expect, it } from 'vitest';
import { createTestEngine } from './support/engine';
import { byId, id, run } from './support/ops';
import { compareElementOrder } from '../src/model/elementOrder';

describe('level inheritance', () => {
  it('inherits_connector_level_from_shape_text_image_or_joined_connector_start', async () => {
    const t = createTestEngine();
    const textId = id(710), shapeConnector = id(711), textConnector = id(712), imageConnector = id(713), branch = id(714), override = id(715), free = id(716);
    await run(t, [
      { op: 'set', target: t.ids.shapes[0]!, patch: { level: -2 } },
      { op: 'set', target: t.ids.image, patch: { level: 4 } },
      { op: 'createText', id: textId, level: 3, bounds: { x: 80, y: 200, width: 90, height: 24 }, text: { value: 'source' } },
      { op: 'createConnector', id: shapeConnector, from: { elementId: t.ids.shapes[0]!, glue: 'dynamic' }, to: { glue: 'none', point: { x: 20, y: 400 } } },
      { op: 'createConnector', id: textConnector, from: { elementId: textId, glue: 'dynamic' }, to: { glue: 'none', point: { x: 140, y: 400 } } },
      { op: 'createConnector', id: imageConnector, from: { elementId: t.ids.image, glue: 'dynamic' }, to: { glue: 'none', point: { x: 260, y: 400 } } },
      { op: 'createConnector', id: branch, from: { glue: 'none', join: { connector: shapeConnector, position: 0.5 } }, to: { glue: 'none', point: { x: 380, y: 400 } } },
      { op: 'createConnector', id: override, level: 6, from: { elementId: t.ids.image, glue: 'dynamic' }, to: { glue: 'none', point: { x: 500, y: 400 } } },
      { op: 'createConnector', id: free, from: { glue: 'none', point: { x: 0, y: 0 } }, to: { glue: 'none', point: { x: 40, y: 0 } } },
    ]);
    expect(byId(t, shapeConnector).level).toBe(-2);
    expect(byId(t, textConnector).level).toBe(3);
    expect(byId(t, imageConnector).level).toBe(4);
    expect(byId(t, branch).level).toBe(-2);
    expect(byId(t, override).level).toBe(6);
    expect(byId(t, free)).not.toHaveProperty('level');
    await run(t, [{ op: 'set', target: t.ids.shapes[0]!, patch: { level: 1 } }]);
    expect(byId(t, shapeConnector).level).toBe(-2);
  });

  it('duplicate_keeps_mixed_level_effective_order', async () => {
    const t = createTestEngine(), high = id(720), low = id(721);
    await run(t, [
      { op: 'createShape', id: high, level: 3, alias: 'high', bounds: { x: 20, y: 20, width: 10, height: 10 } },
      { op: 'createShape', id: low, level: -2, alias: 'low', bounds: { x: 40, y: 20, width: 10, height: 10 } },
    ]);
    const result = await run(t, [{ op: 'duplicate', targets: [high, low] }]);
    const copies = result.created.map(id => byId(t, id)).sort(compareElementOrder);
    expect(copies.map(e => e.level)).toEqual([-2, 3]);
  });
});
