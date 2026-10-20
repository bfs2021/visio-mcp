import { test, expect, type Page } from '@playwright/test';
import { openEditor, placeShape, selection, byAlias, elements } from './support';

const focusCanvas = (page: Page) => page.getByTestId('object-list').focus();
const alias = async (page: Page) => { const ids = await selection(page); const els = await elements(page); return ids.map(id => els.find(e => e.id === id)?.alias); };

async function threeShapes(page: Page) {
  await placeShape(page, 'shape-rectangle', { x: 100, y: 100 }, 'top-left');
  await placeShape(page, 'shape-rectangle', { x: 400, y: 100 }, 'top-right');
  await placeShape(page, 'shape-rectangle', { x: 100, y: 300 }, 'bottom');
  await focusCanvas(page);
  await page.keyboard.press('Escape'); // clears the selection, so the keyboard starts from nothing
  expect(await selection(page)).toEqual([]);
}

test('Tab steps through the objects in reading order and leaves the canvas at either end', async ({ page }) => {
  const errors = await openEditor(page);
  await threeShapes(page);
  await focusCanvas(page);
  await page.keyboard.press('Tab');
  expect(await alias(page)).toEqual(['top-left']);
  await page.keyboard.press('Tab');
  expect(await alias(page)).toEqual(['top-right']);
  await page.keyboard.press('Tab');
  expect(await alias(page)).toEqual(['bottom']);
  await page.keyboard.press('Shift+Tab');
  expect(await alias(page)).toEqual(['top-right']);
  await page.keyboard.press('End');
  expect(await alias(page)).toEqual(['bottom']);
  await page.keyboard.press('Tab'); // past the last object: focus moves on instead of trapping the keyboard
  await expect(page.getByTestId('object-list')).not.toBeFocused();
  await focusCanvas(page);
  await page.keyboard.press('Home');
  expect(await alias(page)).toEqual(['top-left']);
  await page.keyboard.press('Shift+Tab');
  await expect(page.getByTestId('object-list')).not.toBeFocused();
  expect(errors).toEqual([]);
});

test('Ctrl+Arrow jumps to the nearest object in that direction, and says when there is none', async ({ page }) => {
  await openEditor(page);
  await threeShapes(page);
  await focusCanvas(page);
  await page.keyboard.press('Home');
  await page.keyboard.press('Control+ArrowRight');
  expect(await alias(page)).toEqual(['top-right']);
  await page.keyboard.press('Control+ArrowDown');
  expect(await alias(page)).toEqual(['top-right']); // nothing below it
  await expect(page.getByTestId('status-message')).toContainText('No object');
  await page.keyboard.press('Control+ArrowLeft');
  await page.keyboard.press('Control+ArrowDown');
  expect(await alias(page)).toEqual(['bottom']);
  await page.keyboard.press('Control+ArrowUp');
  expect(await alias(page)).toEqual(['top-left']);
});

test('arrows nudge by a grid step, Shift by 1 pt, and Ctrl+Arrow never moves anything', async ({ page }) => {
  await openEditor(page);
  await threeShapes(page);
  await focusCanvas(page);
  await page.keyboard.press('Home');
  const x0 = (await byAlias(page, 'top-left')).bounds.x;
  const step = 5 * 72 / 25.4;
  await page.keyboard.press('ArrowRight');
  await expect.poll(async () => (await byAlias(page, 'top-left')).bounds.x).toBeCloseTo(x0 + step, 4);
  await page.keyboard.press('Shift+ArrowLeft');
  await expect.poll(async () => (await byAlias(page, 'top-left')).bounds.x).toBeCloseTo(x0 + step - 1, 4);
  await page.keyboard.press('Control+ArrowRight');
  expect((await byAlias(page, 'top-left')).bounds.x).toBeCloseTo(x0 + step - 1, 4);
  expect(await alias(page)).toEqual(['top-right']); // it moved the keyboard, not the shape
});

test('Enter edits the text of the selected shape; Esc clears the selection', async ({ page }) => {
  await openEditor(page);
  await threeShapes(page);
  await focusCanvas(page);
  await page.keyboard.press('Tab');
  await page.keyboard.press('Enter');
  await page.keyboard.type('Typed with the keyboard');
  const empty = await page.getByTestId('canvas').boundingBox();
  await page.mouse.click(empty!.x + 5, empty!.y + 5);
  await expect.poll(async () => ((await byAlias(page, 'top-left')) as { text?: { value?: string } }).text?.value).toBe('Typed with the keyboard');
  await focusCanvas(page);
  await page.keyboard.press('Escape');
  expect(await selection(page)).toEqual([]);
});

test('objects are exposed with name, role, position and size in the accessibility tree', async ({ page }) => {
  await openEditor(page);
  await threeShapes(page);
  const list = page.getByTestId('object-list');
  await expect(list).toHaveAttribute('role', 'listbox');
  const options = list.getByRole('option');
  await expect(options).toHaveCount(3);
  await expect(options.first()).toHaveAccessibleName(/^top-left, shape, at x [\d.]+ y [\d.]+ mm, [\d.]+ by [\d.]+ mm$/);
  await expect(options.first()).toHaveAttribute('aria-posinset', '1');
  await expect(options.first()).toHaveAttribute('aria-setsize', '3');
  await focusCanvas(page);
  await page.keyboard.press('Home');
  await expect(options.first()).toHaveAttribute('aria-selected', 'true');
  expect(await list.getAttribute('aria-activedescendant')).toBe(await options.first().getAttribute('id'));
  await page.keyboard.press('Control+a');
  await expect(options.nth(2)).toHaveAttribute('aria-selected', 'true');
});

test('a connector can be made without a pointer by choosing both ends from lists', async ({ page }) => {
  await openEditor(page);
  await threeShapes(page);
  const count = async () => (await elements(page)).filter(e => e.kind === 'connector').length;
  await page.getByTestId('menu-edit').click();
  await page.getByTestId('menu-connect').click();
  const dlg = page.getByTestId('connect-dialog');
  await expect(dlg).toBeVisible();
  await expect(page.getByTestId('connect-from')).toBeFocused();
  await page.getByTestId('connect-to').selectOption({ label: 'top-right (shape)' });
  await page.getByTestId('connect-from').selectOption({ label: 'top-left (shape)' });
  await page.getByTestId('connect-create').click();
  await expect(dlg).toBeHidden();
  expect(await count()).toBe(1);
  const c = (await elements(page)).find(e => e.kind === 'connector') as unknown as { id: string; from: { elementId: string }; to: { elementId: string } };
  expect(c.from.elementId).toBe((await byAlias(page, 'top-left')).id);
  expect(c.to.elementId).toBe((await byAlias(page, 'top-right')).id);
  expect(await selection(page)).toEqual([c.id]); // the new connector is selected for the next step

  // the same object twice is refused with a message; Esc cancels without creating anything
  await page.getByTestId('menu-edit').click();
  await page.getByTestId('menu-connect').click();
  await page.getByTestId('connect-from').selectOption({ label: 'bottom (shape)' });
  await page.getByTestId('connect-to').selectOption({ label: 'bottom (shape)' });
  await page.getByTestId('connect-create').click();
  await expect(page.getByTestId('connect-error')).toContainText('two different');
  await page.keyboard.press('Escape');
  await expect(dlg).toBeHidden();
  expect(await count()).toBe(1);
});
