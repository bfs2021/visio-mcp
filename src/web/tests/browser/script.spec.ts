import { test, expect, type Page } from '@playwright/test';
import type { Element } from '../../src/contracts/generated';
import { centre, drag, elements, openEditor, revision, scale, snapshot } from './support';

const MM = 72 / 25.4;

async function openDrawer(page: Page, tab: 'script' | 'transactions' = 'script') {
  await page.getByTestId('menu-view').click();
  await page.getByTestId(tab === 'script' ? 'menu-script-drawer' : 'menu-transaction-drawer').click();
  await expect(page.getByTestId('drawer')).toBeVisible();
}
const writeScript = async (page: Page, text: string) => { await page.getByTestId('script-input').fill(text); };
const byAlias = async (page: Page, alias: string): Promise<Element> => {
  const e = (await elements(page)).find(x => x.alias === alias);
  if (!e) throw new Error(`no element ${alias}`);
  return e;
};

const SCRIPT = `# a small architecture sketch
add shape id=api type=roundedRect x=40mm y=30mm w=60mm h=20mm text="API"
add shape id=db type=cylinder x=40mm y=80mm w=60mm h=30mm text="Database"
connect id=api_db from=api.south to=db.north route=orthogonal endArrow=triangle
align center api db`;

test('script drawer: validate, run, see the transaction, undo it; later changes protect earlier ones', async ({ page }) => {
  const errors = await openEditor(page);
  await openDrawer(page);

  await test.step('validate reports what would happen and commits nothing', async () => {
    await writeScript(page, SCRIPT);
    const r0 = await revision(page);
    await page.getByTestId('script-validate').click();
    await expect(page.getByTestId('script-report')).toContainText('4 operations');
    await expect(page.getByTestId('script-report')).toContainText('would create 3');
    await expect(page.getByTestId('script-report')).toContainText('Nothing was committed');
    expect(await revision(page)).toBe(r0);
    expect(await elements(page)).toHaveLength(0);
  });

  await test.step('an error names its line and column and changes nothing', async () => {
    await writeScript(page, 'add shape id=a x=10mm y=10mm w=20mm h=10mm\nmove a dx=10 dy=0mm\nadd shape id=b x=0mm y=0mm w=5mm h=5mm');
    await page.getByTestId('script-validate').click();
    await expect(page.getByTestId('script-diagnostic')).toContainText('line 2, column 11');
    await expect(page.getByTestId('script-diagnostic')).toContainText('needs a unit');
    await page.getByTestId('script-run').click();
    await expect(page.getByTestId('script-error')).toContainText('line 2');
    expect(await elements(page)).toHaveLength(0); // the first line did not survive its sibling's failure
    await expect(page.getByTestId('script-input')).toHaveAttribute('aria-invalid', 'true');
  });

  let rAfter = 0;
  await test.step('run commits one revision and the canvas draws the result', async () => {
    await writeScript(page, SCRIPT);
    const r0 = await revision(page);
    await page.getByTestId('script-validate').click();
    await page.getByTestId('script-run').click();
    await expect(page.getByTestId('script-result')).toContainText(`Ran as revision ${r0 + 1}`);
    await expect(page.getByTestId('script-result')).toContainText('created 3');
    rAfter = await revision(page);
    expect(rAfter).toBe(r0 + 1);
    const api = await byAlias(page, 'api'), db = await byAlias(page, 'db');
    expect(api.bounds).toMatchObject({ x: 40 * MM, y: 30 * MM, width: 60 * MM, height: 20 * MM });
    expect(api.bounds.x + api.bounds.width / 2).toBeCloseTo(db.bounds.x + db.bounds.width / 2, 6); // align center ran
    const c = (await elements(page)).find(e => e.kind === 'connector')!;
    expect(c.kind === 'connector' && [c.from.port, c.to.port, c.route]).toEqual(['south', 'north', 'orthogonal']);
    const hit = await page.evaluate(({ x, y }) => document.elementFromPoint(x, y)?.tagName, await centre(page, api.id));
    expect(['rect', 'path', 'text', 'tspan']).toContain(hit); // really on the canvas: its own node or its label, not the page sheet
  });

  await test.step('the transaction drawer shows revision, created ids, source and the script text', async () => {
    await openDrawer(page, 'transactions');
    await expect(page.getByTestId(`tx-${rAfter}`)).toBeVisible();
    await page.getByTestId(`tx-${rAfter}`).click();
    await expect(page.getByTestId('tx-detail')).toContainText(`Revision ${rAfter}`);
    await expect(page.getByTestId('tx-detail')).toContainText('script');
    await expect(page.getByTestId('tx-created')).toContainText('(3)');
    await expect(page.getByTestId('tx-source')).toContainText('add shape id=api');
    await expect(page.getByTestId('tx-undo')).toBeEnabled();
  });

  await test.step('undo the transaction from the drawer: one step removes everything the script made', async () => {
    await page.getByTestId('tx-undo').click();
    await expect.poll(() => revision(page)).toBe(rAfter + 1);
    expect(await elements(page)).toHaveLength(0);
    await page.keyboard.press('Control+y'); // redo through the shortcut: the keyboard works outside the editor box
    await expect.poll(async () => (await elements(page)).length).toBe(3);
  });

  await test.step('a later human change protects the script transaction from selective undo', async () => {
    const api = await byAlias(page, 'api');
    const from = await centre(page, api.id);
    await page.mouse.click(from.x, from.y);
    const k = await scale(page);
    const r0 = await revision(page);
    await drag(page, from, { x: from.x + 40 * k, y: from.y + 20 * k });
    await expect.poll(() => revision(page)).toBe(r0 + 1);
    await openDrawer(page, 'transactions');
    // the newest entry is the human drag; the script transaction is below it
    const list = await page.getByTestId('tx-drawer').locator('[data-testid^="tx-r"], button[data-testid^="tx-"]').count();
    expect(list).toBeGreaterThan(2);
    await page.getByTestId(`tx-${rAfter}`).click();
    await expect(page.getByTestId('tx-undo')).toBeDisabled();
    await expect(page.getByTestId('tx-undo-note')).toContainText('Later changes sit on top');
    // the head is the drag, and that one can be undone
    await page.getByTestId(`tx-${r0 + 1}`).click();
    await expect(page.getByTestId('tx-undo')).toBeEnabled();
    await page.getByTestId('tx-undo').click();
    await expect.poll(() => revision(page)).toBe(r0 + 2);
    expect((await byAlias(page, 'api')).bounds.x).toBeCloseTo(40 * MM, 6);
  });

  await test.step('a script run after the document moved on is refused, not forced', async () => {
    await openDrawer(page, 'script');
    await writeScript(page, 'add shape id=late x=150mm y=30mm w=20mm h=10mm');
    await page.getByTestId('script-validate').click();
    await expect(page.getByTestId('script-report')).toBeVisible();
    await page.keyboard.press('Escape');
    await page.evaluate(() => (window as any).__editor.agentApply([{ op: 'set', target: 'api', patch: { name: 'Edited elsewhere' } }]));
    const r0 = await revision(page);
    await page.getByTestId('script-run').click();
    await expect(page.getByTestId('script-error')).toContainText('changed after you validated');
    expect(await revision(page)).toBe(r0);
    expect((await elements(page)).some(e => e.alias === 'late')).toBe(false);
    // validating again at the new revision lets it through
    await page.getByTestId('script-validate').click();
    await page.getByTestId('script-run').click();
    await expect(page.getByTestId('script-result')).toBeVisible();
    expect((await elements(page)).some(e => e.alias === 'late')).toBe(true);
  });

  expect(errors.filter(e => !/Failed to load resource|favicon/.test(e))).toEqual([]);
  expect((await snapshot(page)).document.pages).toHaveLength(1);
});

test('script drawer: evidence screenshot with the transaction record', async ({ page }) => {
  await openEditor(page);
  await openDrawer(page);
  await writeScript(page, SCRIPT);
  await page.getByTestId('script-run').click();
  await expect(page.getByTestId('script-result')).toBeVisible();
  await page.getByTestId('drawer-tab-transactions').click();
  await page.screenshot({ path: '../../docs/m2/evidence/I21/script-and-transactions.png' });
});
