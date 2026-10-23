import { test, expect, type Page } from '@playwright/test';
import { drag, elements, openEditor, ptToClient, revision, scale } from './support';

const apply = (page: Page, operations: unknown[]) => page.evaluate(ops => (window as any).__editor.agentApply(ops), operations);
const U = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const FILL = { kind: 'solid', colour: '#ff0000', opacity: 1 };
const box = (x: number, y: number) => ({ x, y, width: 100, height: 60 });

async function zRoute(page: Page) {
  await openEditor(page);
  await apply(page, [
    { op: 'createShape', id: U(1), bounds: box(100, 100), style: { fill: FILL, stroke: { kind: 'none' } } },
    { op: 'createShape', id: U(2), bounds: box(600, 300), style: { fill: FILL, stroke: { kind: 'none' } } },
    { op: 'createConnector', id: U(3), from: { elementId: U(1), port: 'east', glue: 'static' }, to: { elementId: U(2), port: 'west', glue: 'static' }, route: 'orthogonal' },
  ]);
  const conn = async () => (await elements(page)).find(e => e.id === U(3)) as any;
  const first = await ptToClient(page, { x: 210, y: 130 }); // on the first run, to select the connector
  await page.mouse.click(first.x, first.y);
  return { conn };
}

test('R21: a selected connector shows a handle on its middle run; dragging it previews, commits once, undoes in one step and Escape cancels', async ({ page }) => {
  const { conn } = await zRoute(page);
  const handle = page.getByTestId('segment-handle-1');
  await expect(handle).toBeVisible();
  await expect(page.getByTestId('segment-handle-0')).toHaveCount(0); // the runs that leave a port have no handle
  const was = (await conn()).routing.advisory;
  const k = await scale(page);
  const box0 = (await handle.boundingBox())!;
  const from = { x: box0.x + box0.width / 2, y: box0.y + box0.height / 2 };

  // Escape puts it back, with no edit
  const rev = await revision(page);
  await drag(page, from, { x: from.x + 60 * k, y: from.y }, { release: false });
  await expect(page.getByTestId('segment-preview')).toBeVisible();
  await page.keyboard.press('Escape');
  await page.mouse.up();
  expect(await revision(page)).toBe(rev);

  // A real drag commits one edit
  await drag(page, from, { x: from.x + 60 * k, y: from.y }, { release: false });
  await page.mouse.up();
  const after = await conn();
  expect(after.waypoints).toEqual([]);
  expect(after.routing.constraints).toHaveLength(1);
  expect(Math.round(after.routing.constraints[0].at - was[1].x)).toBeGreaterThan(40);
  expect(await revision(page)).toBe(rev + 1);
  await page.keyboard.press('Control+z');
  const undone = await conn();
  expect(undone.routing.constraints).toBeUndefined();
  expect(undone.routing.advisory).toEqual(was);
});

test('R21: the arrow keys nudge a focused handle and the Inspector sets the run position numerically', async ({ page }) => {
  const { conn } = await zRoute(page);
  const x0 = (await conn()).routing.advisory[1].x;
  await page.getByTestId('segment-handle-1').focus();
  await page.keyboard.press('ArrowRight');
  expect((await conn()).routing.constraints[0].at).toBeCloseTo(x0 + 1, 2);
  await page.keyboard.press('Shift+ArrowLeft');
  expect((await conn()).routing.constraints[0].at).toBeLessThan(x0);
  const field = page.getByTestId('field-run-1');
  const before = (await conn()).routing.constraints[0].at;
  await field.fill('100');
  await field.blur();
  const at = (await conn()).routing.constraints[0].at;
  expect(at).not.toBe(before);
  expect(Number(await field.inputValue())).toBeCloseTo(100, 2); // the field is in the document unit and shows what was set
  expect(at).toBeGreaterThan(50);
});

test('R21: handles keep a generous hit area at 50% and 400% zoom', async ({ page }) => {
  const { conn } = await zRoute(page);
  const percent = async () => Number((await page.getByTestId('zoom').innerText()).replace('%', ''));
  for (const [button, until] of [['tool-zoom-out', (z: number) => z <= 50], ['tool-zoom-in', (z: number) => z >= 400]] as const) {
    for (let i = 0; i < 30 && !until(await percent()); i++) await page.getByTestId(button).click();
    expect(until(await percent())).toBe(true);
    const hit = page.getByTestId('segment-handle-1').locator('rect').first();
    await expect(hit).toHaveCount(1);
    const b = (await hit.boundingBox())!;
    expect(b.width).toBeGreaterThanOrEqual(15);
    expect(b.height).toBeGreaterThanOrEqual(15);
  }
  expect((await conn()).routing.advisory.length).toBeGreaterThan(2);
});

test('R21: a run can be pinned and released from its handle or the Inspector, and Reset to Automatic drops the pin', async ({ page }) => {
  const { conn } = await zRoute(page);
  const handle = page.getByTestId('segment-handle-1');
  await expect(handle).toHaveAttribute('data-pinned', 'false');
  await handle.dispatchEvent('dblclick');
  await expect(handle).toHaveAttribute('data-pinned', 'true');
  expect((await conn()).routing.constraints).toHaveLength(1);
  await expect(page.getByTestId('run-pin-1')).toBeChecked();
  await page.getByTestId('run-pin-1').uncheck();
  await expect(page.getByTestId('segment-handle-1')).toHaveAttribute('data-pinned', 'false');
  expect((await conn()).routing.constraints).toBeUndefined();
  await page.getByTestId('run-pin-1').check();
  await expect(page.getByTestId('field-route-mode')).toHaveValue('guided');
  await page.getByTestId('field-route-mode').selectOption('automatic');
  expect((await conn()).routing.constraints).toBeUndefined();
});

test('R21: a locked connector explains the refusal as soon as a handle is grabbed and changes nothing', async ({ page }) => {
  const { conn } = await zRoute(page);
  await apply(page, [{ op: 'set', target: U(3), patch: { locked: true } }]);
  const rev = await revision(page);
  const h = (await page.getByTestId('segment-handle-1').boundingBox())!;
  const at = { x: h.x + h.width / 2, y: h.y + h.height / 2 };
  await page.mouse.move(at.x, at.y);
  await page.mouse.down();
  await expect(page.getByTestId('status-message')).toContainText(/lock/i);
  await page.mouse.move(at.x + 40, at.y, { steps: 4 });
  await page.mouse.up();
  expect(await revision(page)).toBe(rev);
  expect((await conn()).routing.constraints).toBeUndefined();
});

test('R21: moving a run on the 60 shape / 60 connector page commits quickly', async ({ page }) => {
  await openEditor(page);
  const ops: unknown[] = [];
  const cols = 10, rows = 6, sides = ['east', 'west', 'north', 'south'];
  for (let r = 0; r < rows; r++) for (let c = 0; c < cols; c++) ops.push({ op: 'createShape', id: U(100 + r * cols + c), bounds: { x: 30 + c * 85, y: 30 + r * 85, width: 60, height: 40 }, style: { fill: FILL, stroke: { kind: 'none' } } });
  for (let i = 0; i < 60; i++) {
    const a = (i * 7) % (cols * rows), b = (i * 13 + 5) % (cols * rows);
    if (a === b) continue;
    ops.push({ op: 'createConnector', id: U(300 + i), from: { elementId: U(100 + a), port: sides[i % 4], glue: 'static' }, to: { elementId: U(100 + b), port: sides[(i + 1) % 4], glue: 'static' }, route: 'orthogonal' });
  }
  await apply(page, ops);
  const pick = (await elements(page)).find((e: any) => e.kind === 'connector' && e.routing?.advisory?.length >= 4 && e.routing.advisory.length <= 6) as any;
  expect(pick).toBeDefined();
  const run = pick.routing.advisory.findIndex((p: any, i: number, a: any[]) => i > 0 && i < a.length - 2);
  const t = await page.evaluate(async ([id, seg]) => {
    const t0 = performance.now();
    await (window as any).__editor.agentApply([{ op: 'moveSegment', target: id, segment: seg, by: 7 }]);
    return performance.now() - t0;
  }, [pick.id, run] as const);
  console.log(`moveSegment commit on 60/60 page: ${t.toFixed(0)} ms`);
  expect(t).toBeLessThan(1500);
});
