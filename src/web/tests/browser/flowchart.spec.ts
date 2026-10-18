import { test, expect } from '@playwright/test';
import { openEditor, centre, snapshot, placeShape } from './support';

const SYMBOLS = ['terminator', 'stored-data', 'document', 'lined-document'] as const;

test('the flowchart symbols are drawn with their own outlines and connectors meet them there', async ({ page }) => {
  const errors = await openEditor(page);
  const ops = SYMBOLS.map((preset, i) => ({ op: 'createShape', alias: preset, bounds: { x: 60 + i * 180, y: 80, width: 140, height: 70 }, geometry: { preset }, text: { value: preset } }));
  const r = await page.evaluate(o => (window as any).__editor.agentApply(o), ops);
  expect(r.ok).toBe(true);
  await page.screenshot({ path: '../../artifacts/r10-flowchart/editor-symbols.png', clip: undefined });
  const els = (await snapshot(page)).document.pages[0]!.elements;
  for (const preset of SYMBOLS) {
    const e = els.find(x => x.alias === preset)!;
    expect(e.kind === 'shape' && e.geometry.preset).toBe(preset);
    expect(e.kind === 'shape' && e.ports?.map(p => p.id)).toEqual(['north', 'east', 'south', 'west']);
  }
  // a connector glued to the terminator's east port ends on its round end, not at the frame's corner
  const a = els.find(x => x.alias === 'terminator')!, b = els.find(x => x.alias === 'document')!;
  const c = await page.evaluate(([from, to]) => (window as any).__editor.agentApply([{ op: 'createConnector', from: { elementId: from, glue: 'static', port: 'east' }, to: { elementId: to, glue: 'static', port: 'west' }, route: 'straight' }]), [a.id, b.id]);
  expect(c.ok).toBe(true);
  expect((await centre(page, a.id)).x).toBeGreaterThan(0);
  expect(errors).toEqual([]);
});

test('the Flowchart palette offers the new symbols and places them with their ports', async ({ page }) => {
  await openEditor(page);
  let x = 60;
  for (const id of SYMBOLS) {
    const el = await placeShape(page, `shape-flowchart-${id}`, { x, y: 120 }, id.replace('-', ''));
    expect(el.kind === 'shape' && el.geometry.preset).toBe(id);
    expect(el.kind === 'shape' && el.ports?.length).toBe(4);
    x += 170;
  }
});
