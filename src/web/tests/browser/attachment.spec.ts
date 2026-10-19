import { test, expect, type Page } from '@playwright/test';
import { centre, drag, openEditor, ptToClient, snapshot } from './support';

const text = (value: string) => ({ value, fontSizePt: 11 });

async function scene(page: Page) {
  const r = await page.evaluate(o => (window as any).__editor.agentApply(o), [
    { op: 'createShape', alias: 'a', bounds: { x: 40, y: 100, width: 80, height: 40 }, text: text('Source') },
    { op: 'createShape', alias: 'b', bounds: { x: 380, y: 100, width: 80, height: 40 }, text: text('Target') },
    { op: 'createConnector', alias: 'feed', from: { elementId: 'a', glue: 'dynamic' }, to: { elementId: 'b', glue: 'dynamic' }, label: text('feed'), labelAnchor: { position: 0.25, offsetPt: -14 } },
    { op: 'createShape', alias: 'report', geometry: { preset: 'document' }, bounds: { x: 0, y: 0, width: 56, height: 36 }, text: text('PSGL Report'), attachment: { connector: 'feed', position: 0.6 } },
  ]);
  expect(r.ok).toBe(true);
}
const el = async (page: Page, alias: string) => (await snapshot(page)).document.pages[0]!.elements.find(e => e.alias === alias) as any;
const mid = (e: any) => ({ x: e.bounds.x + e.bounds.width / 2, y: e.bounds.y + e.bounds.height / 2 });

test('a shape pinned to a connector follows it when an end shape is dragged, in one undo step', async ({ page }) => {
  const errors = await openEditor(page);
  await scene(page);
  const start = mid(await el(page, 'report'));
  expect(start.x).toBeCloseTo(276, 0); expect(start.y).toBeCloseTo(120, 0);

  const b = await el(page, 'b');
  const from = await centre(page, b.id);
  await drag(page, from, { x: from.x, y: from.y + 120 });
  const moved = mid(await el(page, 'report'));
  expect(moved.y).toBeGreaterThan(start.y + 40);
  expect((await el(page, 'b')).bounds.y).toBeGreaterThan(200);

  await page.keyboard.press('Control+z');
  await expect.poll(async () => mid(await el(page, 'report')).y).toBeCloseTo(start.y, 0);
  expect((await el(page, 'b')).bounds.y).toBeCloseTo(100, 0);
  expect(errors).toEqual([]);
});

test('the inspector moves a pinned shape along its connector and detaches it; dragging slides it', async ({ page }) => {
  await openEditor(page);
  await scene(page);
  const report = await el(page, 'report');
  const c = await centre(page, report.id);
  await page.mouse.click(c.x, c.y);
  await expect(page.getByTestId('pin')).toBeVisible();
  await page.getByTestId('field-pin-at').fill('25');
  await page.getByTestId('field-pin-at').press('Enter');
  await expect.poll(async () => mid(await el(page, 'report')).x).toBeCloseTo(185, 0);
  await page.getByTestId('field-pin-offset').fill('30');
  await page.getByTestId('field-pin-offset').press('Enter');
  await expect.poll(async () => mid(await el(page, 'report')).y).toBeCloseTo(150, 0);

  // dragging slides it along the line, keeping it exactly where it is dropped
  const now = await centre(page, report.id);
  await drag(page, now, { x: now.x + 60, y: now.y });
  await expect.poll(async () => (await el(page, 'report')).attachment.position).toBeGreaterThan(0.4);

  await page.getByTestId('pin-detach').click();
  await expect.poll(async () => (await el(page, 'report')).attachment).toBeUndefined();
  await expect(page.getByTestId('pin')).toHaveCount(0);
});

test('a connector label is placed from the inspector and drawn there', async ({ page }) => {
  await openEditor(page);
  await scene(page);
  const feed = await el(page, 'feed');
  // a spot on the line clear of the pinned shape
  const c = await ptToClient(page, { x: 150, y: 120 });
  await page.mouse.click(c.x, c.y);
  expect(feed.kind).toBe('connector');
  await page.getByTestId('field-label-at').fill('75');
  await page.getByTestId('field-label-at').press('Enter');
  await expect.poll(async () => (await el(page, 'feed')).labelAnchor?.position).toBe(0.75);
  await page.getByTestId('field-label-offset').fill('20');
  await page.getByTestId('field-label-offset').press('Enter');
  await expect.poll(async () => (await el(page, 'feed')).labelAnchor?.offsetPt).toBe(20);
  // the label text is drawn in the right quarter of the line and below it (to the right of travel)
  const where = await page.evaluate(() => {
    const t = [...document.querySelectorAll('svg text, svg foreignObject')].find(n => /feed/.test(n.textContent ?? ''));
    const r = t?.getBoundingClientRect(); return r ? { x: r.x + r.width / 2, y: r.y + r.height / 2 } : null;
  });
  expect(where).not.toBeNull();
  const a = await centre(page, (await el(page, 'a')).id), b = await centre(page, (await el(page, 'b')).id);
  expect(where!.x).toBeGreaterThan(a.x + (b.x - a.x) * 0.6);
  expect(where!.y).toBeGreaterThan(a.y + 8);
});

test('pin to connector: a selected shape and a connector', async ({ page }) => {
  await openEditor(page);
  await scene(page);
  expect((await page.evaluate(o => (window as any).__editor.agentApply(o), [{ op: 'createText', alias: 'free', bounds: { x: 200, y: 300, width: 60, height: 18 }, text: text('loose') }])).ok).toBe(true);
  const free = await el(page, 'free'), feed = await el(page, 'feed');
  const f = await centre(page, free.id), c = await ptToClient(page, { x: 150, y: 120 });
  await page.mouse.click(f.x, f.y);
  await page.keyboard.down('Control');
  await page.mouse.click(c.x, c.y);
  await page.keyboard.up('Control');
  await page.getByTestId('pin-to-connector').click();
  await expect.poll(async () => (await el(page, 'free')).attachment?.connectorId).toBe(feed.id);
  expect(mid(await el(page, 'free')).y).toBeCloseTo(120, 0);
});
