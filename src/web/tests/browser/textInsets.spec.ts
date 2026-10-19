import { test, expect } from '@playwright/test';
import { centre, openEditor, snapshot } from './support';

const text = (value: string, over: object = {}) => ({ value, fontSizePt: 12, ...over });

test('vertical text and per-side margins are drawn by the editor, and the inspector sets them', async ({ page }) => {
  const errors = await openEditor(page);
  const r = await page.evaluate(o => (window as any).__editor.agentApply(o), [
    { op: 'createShape', alias: 'lane', bounds: { x: 60, y: 60, width: 60, height: 200 }, text: text('Corona Sensical', { orientation: 'vertical', paddingPt: 0 }) },
    { op: 'createShape', alias: 'v2', bounds: { x: 400, y: 60, width: 100, height: 200 }, text: text('Margin probe', { orientation: 'vertical', horizontalAlign: 'left', verticalAlign: 'top', insetsPt: { top: 10, right: 0, bottom: 0, left: 30 } }) },
    { op: 'createShape', alias: 'pad', bounds: { x: 200, y: 60, width: 160, height: 80 }, text: text('Left margin', { horizontalAlign: 'left', insetsPt: { top: 0, right: 0, bottom: 0, left: 40 } }) },
  ]);
  expect(r.ok).toBe(true);
  const box = (re: RegExp) => page.evaluate(src => {
    const t = [...document.querySelectorAll('svg text, svg foreignObject, svg div, svg span')].filter(n => new RegExp(src).test(n.textContent ?? '')).pop();
    const q = t?.getBoundingClientRect(); return q ? { x: q.x, y: q.y, w: q.width, h: q.height } : null;
  }, re.source);
  const lane = await box(/Corona/), pad = await box(/Left margin/);
  expect(lane).not.toBeNull(); expect(pad).not.toBeNull();
  expect(lane!.h).toBeGreaterThan(lane!.w); // the title runs up the column
  await page.screenshot({ path: 'test-results/text-insets.png' });

  // the inspector turns the text and sets one margin
  const padEl = (await snapshot(page)).document.pages[0]!.elements.find(e => e.alias === 'pad')!;
  const c = await centre(page, padEl.id);
  await page.mouse.click(c.x, c.y);
  await page.getByTestId('field-vertical').check();
  await expect.poll(async () => ((await snapshot(page)).document.pages[0]!.elements.find(e => e.alias === 'pad') as any).text.orientation).toBe('vertical');
  await page.getByTestId('field-margin-left').fill('12');
  await page.getByTestId('field-margin-left').press('Enter');
  await expect.poll(async () => ((await snapshot(page)).document.pages[0]!.elements.find(e => e.alias === 'pad') as any).text.insetsPt).toEqual({ top: 0, right: 0, bottom: 0, left: 12 });
  await page.getByTestId('field-vertical').uncheck();
  await expect.poll(async () => ((await snapshot(page)).document.pages[0]!.elements.find(e => e.alias === 'pad') as any).text.orientation).toBeUndefined();
  expect(errors).toEqual([]);
});
