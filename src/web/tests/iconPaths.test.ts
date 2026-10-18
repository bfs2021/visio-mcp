import { expect, it } from 'vitest';
import { vertexStyle } from '../src/canvas/cellMapping';
import type { ShapeElement } from '../src/contracts/generated';
import { createTestEngine, element } from './support/engine';

it('projects a canonical custom path instead of the default rectangle', () => {
  const t = createTestEngine();
  const el = element(t.engine.store.snapshot(), t.ids.shape) as ShapeElement;
  const style = vertexStyle({ ...el, geometry: { svgPath: 'M 10 10 L 90 90 M 90 10 L 10 90' } }, false, x => x);
  expect(style.shape).toBe('custom-path');
  expect((style as Record<string, unknown>).svgPath).toBe('M 10 10 L 90 90 M 90 10 L 10 90');
});

it('rejects unsupported and malformed custom paths atomically at the transaction boundary', async () => {
  const t = createTestEngine();
  for (const svgPath of ['M 0 0 A 10 10 0 0 0 20 20', 'M 0 0 L 2', 'M 0 0 L 1e999 2', 'L 0 0 L 10 10']) {
    const r = await t.engine.execute(t.request([{ op: 'createShape', bounds: { x: 0, y: 0, width: 24, height: 24 }, geometry: { svgPath } }]), 'mcp');
    expect(r.ok).toBe(false);
    expect(t.engine.revision).toBe(0);
  }
});
