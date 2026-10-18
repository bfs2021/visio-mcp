import { test, expect, type Page } from '@playwright/test';
import { openEditor, placeShape } from './support';

const tabs = (page: Page) => page.evaluate(() => (window as any).__editor.tabs() as Array<{ label: string; unsaved: boolean; documentId: string; active: boolean }>);
const labels = async (page: Page) => (await tabs(page)).map(t => t.label);
const activeLabel = async (page: Page) => (await tabs(page)).find(t => t.active)!.label;
const makeTabs = async (page: Page, n: number) => { for (let i = 1; i < n; i++) await page.getByTestId('tab-new').click(); };

async function dragTab(page: Page, from: number, to: number, side: 'before' | 'after') {
  const a = (await page.getByTestId('tab').nth(from).boundingBox())!;
  const b = (await page.getByTestId('tab').nth(to).boundingBox())!;
  const y = a.y + a.height / 2;
  await page.mouse.move(a.x + a.width / 2 - 10, y);
  await page.mouse.down();
  await page.mouse.move(a.x + a.width / 2 + 20, y, { steps: 3 });
  await page.mouse.move(side === 'after' ? b.x + b.width - 4 : b.x + 4, y, { steps: 8 });
  await page.mouse.up();
}

test('dragging a tab reorders it without switching to it, and the new order is remembered', async ({ page }) => {
  const errors = await openEditor(page);
  await page.evaluate(() => {
    const saves: string[][] = [];
    (window as any).__saves = saves;
    (window as any).__editor.setHost({
      workspaceLoad: async () => ({ ok: true, value: { found: false } }),
      workspaceSave: async (tabs: Array<{ title: string }>) => { saves.push(tabs.map(t => t.title)); return { ok: true, value: { saved: tabs.length } }; },
    });
  });
  await page.evaluate(() => (window as any).__editor.restoreWorkspace()); // starts remembering
  await makeTabs(page, 3);
  expect(await labels(page)).toEqual(['Untitled 1', 'Untitled 2', 'Untitled 3']);
  expect(await activeLabel(page)).toBe('Untitled 3');
  await dragTab(page, 0, 2, 'after');
  expect(await labels(page)).toEqual(['Untitled 2', 'Untitled 3', 'Untitled 1']);
  expect(await activeLabel(page)).toBe('Untitled 3'); // a drag is not a click
  await dragTab(page, 2, 0, 'before');
  expect(await labels(page)).toEqual(['Untitled 1', 'Untitled 2', 'Untitled 3']);
  await expect.poll(async () => (await page.evaluate(() => (window as any).__saves) as string[][]).at(-1)).toEqual(['Untitled 1', 'Untitled 2', 'Untitled 3']);
  await dragTab(page, 0, 1, 'after');
  await expect.poll(async () => (await page.evaluate(() => (window as any).__saves) as string[][]).at(-1)).toEqual(['Untitled 2', 'Untitled 1', 'Untitled 3']);
  // a click that is not a drag still switches
  await page.getByTestId('tab').nth(0).click();
  expect(await activeLabel(page)).toBe('Untitled 2');
  expect(errors).toEqual([]);
});

test('Ctrl+Shift+Arrow moves the focused tab and says so for screen readers; Home and End jump', async ({ page }) => {
  await openEditor(page);
  await makeTabs(page, 3);
  await page.getByTestId('tab').nth(0).click();
  await page.getByTestId('tab').nth(0).focus();
  await page.keyboard.press('Control+Shift+ArrowRight');
  expect(await labels(page)).toEqual(['Untitled 2', 'Untitled 1', 'Untitled 3']);
  await expect(page.getByTestId('tab-live')).toHaveText('Untitled 1 moved to position 2 of 3');
  await page.keyboard.press('Control+Shift+ArrowRight');
  await page.keyboard.press('Control+Shift+ArrowRight'); // already last: stays
  expect(await labels(page)).toEqual(['Untitled 2', 'Untitled 3', 'Untitled 1']);
  await page.keyboard.press('Home');
  expect(await activeLabel(page)).toBe('Untitled 2');
  await page.keyboard.press('End');
  expect(await activeLabel(page)).toBe('Untitled 1');
});

test('Ctrl+Tab steps through tabs most recently used first, and holding Ctrl keeps going back through the history', async ({ page }) => {
  await openEditor(page);
  await makeTabs(page, 3); // 1, 2, 3 with 3 showing
  await page.getByTestId('tab').nth(0).click();
  await page.getByTestId('tab').nth(1).click(); // history, newest first: 2, 1, 3
  await page.locator('body').click({ position: { x: 5, y: 5 } });
  await page.keyboard.press('Control+Tab');
  expect(await activeLabel(page)).toBe('Untitled 1');
  await page.keyboard.press('Control+Tab'); // released in between: now 1, 2, 3 -> back to 2
  expect(await activeLabel(page)).toBe('Untitled 2');
  await page.keyboard.down('Control');
  await page.keyboard.press('Tab');
  expect(await activeLabel(page)).toBe('Untitled 1');
  await page.keyboard.press('Tab');
  expect(await activeLabel(page)).toBe('Untitled 3'); // the third most recent
  await page.keyboard.press('Shift+Tab');
  expect(await activeLabel(page)).toBe('Untitled 1'); // and back
  await page.keyboard.up('Control');
  await page.keyboard.press('Control+Tab');
  expect(await activeLabel(page)).toBe('Untitled 2'); // 1 was made the most recent by releasing Ctrl
  await page.keyboard.press('Control+PageDown'); // tab order, not history
  expect(await activeLabel(page)).toBe('Untitled 3');
});

test('the tab menu: Close, Close others, Close to the right, with Cancel stopping the rest', async ({ page }) => {
  await openEditor(page);
  await page.evaluate(() => (window as any).__editor.setHost({ recoveryDiscard: async (id: string) => ({ ok: true, value: { discarded: id } }) }));
  await makeTabs(page, 5);
  await page.getByTestId('tab').nth(1).click({ button: 'right' });
  await expect(page.getByTestId('tab-menu')).toBeVisible();
  await expect(page.getByTestId('tabmenu-copy-path')).toBeDisabled(); // not saved to a file
  await expect(page.getByTestId('tabmenu-reveal')).toBeDisabled();
  await page.getByTestId('tabmenu-close-right').click();
  expect(await labels(page)).toEqual(['Untitled 1', 'Untitled 2']);
  await page.getByTestId('tab').nth(1).click({ button: 'right' });
  await expect(page.getByTestId('tabmenu-close-right')).toBeDisabled(); // nothing to its right
  await page.getByTestId('tabmenu-close').click();
  expect(await labels(page)).toEqual(['Untitled 1']);
  await page.getByTestId('tab-new').click();
  await page.getByTestId('tab-new').click();
  await page.getByTestId('tab').nth(1).click({ button: 'right' });
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('tab-menu')).toHaveCount(0);
  // work in the first and third tabs: Close others asks about each, in order, and Cancel stops
  await page.getByTestId('tab').nth(0).click();
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'a');
  await page.getByTestId('tab').nth(2).click();
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'b');
  await page.getByTestId('tab').nth(1).click({ button: 'right' });
  await page.getByTestId('tabmenu-close-others').click();
  await expect(page.getByTestId('close-prompt')).toBeVisible();
  expect((await tabs(page)).find(t => t.active)!.label).toBe('Untitled 1'); // the first one it asks about is showing
  await page.getByTestId('close-discard').click();
  await expect(page.getByTestId('close-prompt')).toBeVisible(); // now the other
  await page.getByTestId('close-cancel').click();
  expect((await tabs(page)).map(t => t.unsaved)).toEqual([false, true]); // the first went, the second said Cancel and was kept, nothing else was touched
});

test('Copy path and Show in Explorer work on a saved tab, by document id', async ({ page }) => {
  await openEditor(page);
  await page.evaluate(() => {
    const calls: string[] = [];
    (window as any).__calls = calls;
    (window as any).__editor.setHost({
      documentCopyPath: async (id: string) => { calls.push('copy:' + id); return { ok: true, value: { copied: 'x' } }; },
      documentReveal: async (id: string) => { calls.push('reveal:' + id); return { ok: true, value: { revealed: 'x' } }; },
    });
  });
  await page.evaluate(() => (window as any).__editor.markSaved('C:\\Work\\plan.diagram.json'));
  const id = (await tabs(page))[0]!.documentId;
  await page.getByTestId('tab').nth(0).click({ button: 'right' });
  await page.getByTestId('tabmenu-copy-path').click();
  await page.getByTestId('tab').nth(0).click({ button: 'right' });
  await page.getByTestId('tabmenu-reveal').click();
  await expect.poll(() => page.evaluate(() => (window as any).__calls)).toEqual([`copy:${id}`, `reveal:${id}`]);
  await expect(page.getByTestId('status-message')).toContainText('Path copied');
});

test('the menu opens from the keyboard and can be walked with the arrow keys', async ({ page }) => {
  await openEditor(page);
  await makeTabs(page, 2);
  await page.getByTestId('tab').nth(0).click();
  await page.getByTestId('tab').nth(0).focus();
  await page.keyboard.press('Shift+F10');
  await expect(page.getByTestId('tab-menu')).toBeVisible();
  await expect(page.getByTestId('tabmenu-close')).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await expect(page.getByTestId('tabmenu-close-others')).toBeFocused();
  await page.keyboard.press('ArrowUp');
  await page.keyboard.press('ArrowUp');
  await expect(page.getByTestId('tabmenu-close-right')).toBeFocused(); // wraps, skipping the disabled items
  await page.keyboard.press('Enter');
  expect(await labels(page)).toEqual(['Untitled 1']);
});

test('too many tabs scroll, with arrows and a list that jumps to any of them', async ({ page }) => {
  await page.setViewportSize({ width: 760, height: 700 });
  await openEditor(page);
  await expect(page.getByTestId('tab-list')).toHaveCount(0); // everything fits
  await makeTabs(page, 14);
  await expect(page.getByTestId('tab-list')).toBeVisible();
  await expect(page.getByTestId('tab-scroll-left')).toBeVisible();
  // the active (last) tab is in view
  const inView = async (i: number) => page.evaluate(i => {
    const t = document.querySelectorAll<HTMLElement>('[data-testid=tab]')[i]!.getBoundingClientRect();
    const s = document.querySelector<HTMLElement>('.tabs-scroll')!.getBoundingClientRect();
    return t.left >= s.left - 1 && t.right <= s.right + 1;
  }, i);
  expect(await inView(13)).toBe(true);
  expect(await inView(0)).toBe(false);
  await page.getByTestId('tab-list').click();
  await expect(page.getByTestId('tab-list-menu').getByRole('menuitemradio')).toHaveCount(14);
  await page.getByTestId('tabmenu-' + (await page.evaluate(() => document.querySelectorAll<HTMLElement>('[data-testid=tab]')[0]!.dataset.tabId))).click();
  expect(await activeLabel(page)).toBe('Untitled 1');
  await expect.poll(() => inView(0)).toBe(true); // jumping scrolls it into view
  const before = await page.evaluate(() => document.querySelector('.tabs-scroll')!.scrollLeft);
  await page.getByTestId('tab-scroll-right').click();
  await expect.poll(() => page.evaluate(() => document.querySelector('.tabs-scroll')!.scrollLeft)).toBeGreaterThan(before);
});

test('a tab says when it has unsaved changes, and the strip holds only tabs', async ({ page }) => {
  await openEditor(page);
  await placeShape(page, 'shape-rectangle', { x: 200, y: 150 }, 'a');
  await expect(page.getByRole('tab', { name: 'Untitled 1, unsaved changes' })).toBeVisible();
  await page.getByTestId('tab-new').click();
  await expect(page.getByRole('tab', { name: 'Untitled 2', exact: true })).toBeVisible();
  // the new-tab button and notices are outside the tab list, so a screen reader counts two tabs
  const strip = page.getByRole('tablist', { name: 'Open documents' });
  expect(await strip.getByRole('tab').count()).toBe(2);
  expect(await strip.locator('button:not(.tab-close)').count()).toBe(0);
});
