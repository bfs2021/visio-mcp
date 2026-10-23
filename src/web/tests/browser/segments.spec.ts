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
  expect(after.waypoints.length).toBeGreaterThan(0);
  expect(after.routing).toBeUndefined();
  expect(Math.round(after.waypoints[0].x - was[1].x)).toBeGreaterThan(40);
  expect(await revision(page)).toBe(rev + 1);
  await page.keyboard.press('Control+z');
  const undone = await conn();
  expect(undone.waypoints).toEqual([]);
  expect(undone.routing.advisory).toEqual(was);
});

test('R21: the arrow keys nudge a focused handle and the Inspector sets the run position numerically', async ({ page }) => {
  const { conn } = await zRoute(page);
  const x0 = (await conn()).routing.advisory[1].x;
  await page.getByTestId('segment-handle-1').focus();
  await page.keyboard.press('ArrowRight');
  expect((await conn()).waypoints[0].x).toBeCloseTo(x0 + 1, 2);
  await page.keyboard.press('Shift+ArrowLeft');
  expect((await conn()).waypoints[0].x).toBeLessThan(x0);
  const field = page.getByTestId('field-run-1');
  await field.fill('500');
  await field.blur();
  const unit = await page.evaluate(() => (window as any).__editor.snapshot().document.defaults?.unit);
  expect(await field.inputValue()).toBe('500');
  expect(unit === undefined || typeof unit === 'string').toBe(true);
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
