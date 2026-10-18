import { test, expect } from '@playwright/test';
import { openEditor, snapshot } from './support';

test('near-alignment hints and thin-rule inspection are read-only and clear after one targeted correction', async ({ page }) => {
  await openEditor(page);
  const id = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
  await page.evaluate(async ids => (window as any).__editor.agentApply([
    { op: 'createShape', id: ids[0], bounds: { x: 60, y: 100, width: 100, height: 60 } },
    { op: 'createShape', id: ids[1], bounds: { x: 260, y: 105, width: 100, height: 60 } },
    { op: 'createShape', id: ids[2], bounds: { x: 60, y: 250, width: 300, height: 0.8 }, style: { fill: { kind: 'solid', colour: '#E2E8F0', opacity: 1 }, stroke: { kind: 'none' } } },
  ]), [id(1), id(2), id(3)]);
  const before = await snapshot(page);
  const report = await page.evaluate(() => (window as any).__editor.callTool('doc.inspectLayout', {}));
  expect(report.error).toBeUndefined();
  expect(report.result.issues.filter((i: any) => i.code === 'NEAR_ALIGNMENT')).toEqual([
    expect.objectContaining({ severity: 'info', elementIds: [id(1), id(2)], evidence: expect.objectContaining({ axis: 'y', deltaPt: 5, heuristic: true }) }),
  ]);
  expect(report.result.issues.some((i: any) => i.code === 'ZERO_SIZE')).toBe(false);
  expect(await snapshot(page)).toEqual(before);
  const correction = await page.evaluate(target => (window as any).__editor.agentApply([{ op: 'move', target, delta: { xPt: 0, yPt: -5 } }]), id(2));
  expect(correction.ok).toBe(true);
  const after = await snapshot(page);
  for (const untouched of [id(1), id(3)]) expect(after.document.pages[0]!.elements.find(e => e.id === untouched)).toEqual(before.document.pages[0]!.elements.find(e => e.id === untouched));
  const corrected = await page.evaluate(() => (window as any).__editor.callTool('doc.inspectLayout', {}));
  expect(corrected.result.issues.some((i: any) => i.code === 'NEAR_ALIGNMENT')).toBe(false);
});
