import { test, expect, type Page } from '@playwright/test';
import { centre, drag, openEditor, snapshot } from './support';

const el = async (page: Page, alias: string) => (await snapshot(page)).document.pages[0]!.elements.find(e => e.alias === alias) as any;
const apply = (page: Page, ops: object[]) => page.evaluate(o => (window as any).__editor.agentApply(o), ops);
/** Distinct line paths maxGraph drew with a curve in them (it draws a second copy for hit testing; the hidden drawing used for projections is outside the canvas): for these straight connectors a hop is the only curve. */
const hopCount = (page: Page) => page.evaluate(() => new Set([...document.querySelectorAll('[data-testid=canvas] svg path')].map(p => p.getAttribute('d') ?? '').filter(d => /L.*C/.test(d))).size);

async function bus(page: Page) {
  const r = await apply(page, [
    { op: 'createShape', alias: 'a', bounds: { x: 40, y: 100, width: 60, height: 40 } },
    { op: 'createShape', alias: 'b', bounds: { x: 400, y: 100, width: 60, height: 40 } },
    { op: 'createShape', alias: 'c', bounds: { x: 200, y: 300, width: 60, height: 40 } },
    { op: 'createConnector', alias: 'bus', from: { elementId: 'a', glue: 'dynamic' }, to: { elementId: 'b', glue: 'dynamic' } },
    { op: 'createConnector', alias: 'tap', from: { elementId: 'c', glue: 'dynamic' }, to: { glue: 'none', join: { connector: 'bus', position: 0.5 } } },
  ]);
  expect((r as any).ok).toBe(true);
}

test('a branch stays on the connector it joins when an end shape is dragged, and one undo returns both', async ({ page }) => {
  const errors = await openEditor(page);
  await bus(page);
  const start = (await el(page, 'tap')).to.point;
  expect(start).toEqual({ x: 250, y: 120 });

  const b = await el(page, 'b');
  const from = await centre(page, b.id);
  await drag(page, from, { x: from.x, y: from.y + 100 });
  const moved = (await el(page, 'tap')).to.point;
  expect(moved.y).toBeGreaterThan(start.y + 20);                   // the bus now slopes down to b, and the branch rode with it
  const nb = (await el(page, 'b')).bounds;
  const x0 = 70, y0 = 120, x1 = nb.x + 30, y1 = nb.y + 20;         // the end is on the straight line from a's middle to b's middle
  expect(Math.abs((y1 - y0) * (moved.x - x0) - (x1 - x0) * (moved.y - y0)) / Math.hypot(x1 - x0, y1 - y0)).toBeLessThan(1);

  await page.keyboard.press('Control+z');
  await expect.poll(async () => (await el(page, 'tap')).to.point).toEqual(start);
  expect((await el(page, 'b')).bounds.y).toBeCloseTo(100, 0);
  expect(errors).toEqual([]);
});

test('hops are drawn where a connector crosses another, only when asked for, and not at a join', async ({ page }) => {
  const errors = await openEditor(page);
  const r = await apply(page, [
    { op: 'createShape', alias: 'l', bounds: { x: 40, y: 200, width: 40, height: 40 } },
    { op: 'createShape', alias: 'r', bounds: { x: 440, y: 200, width: 40, height: 40 } },
    { op: 'createShape', alias: 't', bounds: { x: 240, y: 40, width: 40, height: 40 } },
    { op: 'createShape', alias: 'u', bounds: { x: 240, y: 380, width: 40, height: 40 } },
    { op: 'createConnector', alias: 'across', from: { elementId: 'l', glue: 'dynamic' }, to: { elementId: 'r', glue: 'dynamic' } },
    { op: 'createConnector', alias: 'down', from: { elementId: 't', glue: 'dynamic' }, to: { elementId: 'u', glue: 'dynamic' } },
  ]);
  expect((r as any).ok).toBe(true);
  expect(await hopCount(page)).toBe(0);

  expect(((await apply(page, [{ op: 'set', target: 'down', patch: { hops: 'arc' } }])) as any).ok).toBe(true);
  await expect.poll(() => hopCount(page)).toBe(1);

  // turning it off straightens it again
  await apply(page, [{ op: 'set', target: 'down', patch: { hops: 'none' } }]);
  await expect.poll(() => hopCount(page)).toBe(0);

  // a branch that joins the line is not a crossing, even if it hops
  await apply(page, [
    { op: 'set', target: 'down', patch: { hops: 'arc' } },
    { op: 'createConnector', alias: 'tap', from: { elementId: 'u', glue: 'dynamic' }, to: { glue: 'none', join: { connector: 'across', position: 0.8 } }, hops: 'arc' },
  ]);
  await expect.poll(() => hopCount(page)).toBe(1);
  expect(errors).toEqual([]);
});
