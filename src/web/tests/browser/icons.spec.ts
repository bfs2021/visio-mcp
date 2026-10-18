import { test, expect } from '@playwright/test';
import { openEditor, elements, snapshot, ptToClient } from './support';
import { mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

test('searches the palette and inserts one editable icon with the requested size, then undoes it', async ({ page }) => {
  const errors = await openEditor(page);
  await page.getByLabel('Icon search', { exact: true }).fill('git branch');
  await page.getByLabel('Icon size (pt)', { exact: true }).fill('36');
  await page.getByRole('button', { name: 'Icon: git branch', exact: true }).click();
  const point = await ptToClient(page, { x: 160, y: 150 });
  await page.mouse.click(point.x, point.y);
  await expect.poll(async () => (await elements(page)).length).toBe(1);
  const icon = (await elements(page))[0]!;
  expect(icon.kind).toBe('shape'); expect(icon.bounds.width).toBe(36);
  expect(icon.metadata['icon.id']).toBe('lucide:git-branch');
  const folder = fileURLToPath(new URL('../../../../artifacts/r03-icons/', import.meta.url)); mkdirSync(folder, { recursive: true });
  await page.screenshot({ path: folder + '/icons-panel.png' });
  await page.keyboard.press('Control+z');
  await expect.poll(async () => (await elements(page)).length).toBe(0);
  expect(errors).toEqual([]);
});

test('preview and placed custom geometry draw a hollow branch node rather than a filled rectangle', async ({ page }) => {
  const errors = await openEditor(page); const snap = await snapshot(page); const pageId = snap.document.pages[0]!.id;
  const proposal = await page.evaluate(p => (window as any).__editor.callTool('icons.get', p), { pageId, iconId: 'lucide:git-branch', x: 100, y: 100, sizePt: 48 });
  expect(proposal.error).toBeUndefined(); expect((await snapshot(page)).revision).toBe(0);
  expect((await page.evaluate(ops => (window as any).__editor.agentApply(ops), proposal.result.operations)).ok).toBe(true);
  const rendered = await page.evaluate(p => (window as any).__editor.callTool('doc.render', p), { scope: 'region', pageId, bounds: { x: 100, y: 100, width: 48, height: 48 }, paddingPt: 0, maxWidth: 384, maxHeight: 384 });
  expect(rendered.error).toBeUndefined();
  const pixels = await page.evaluate(async b64 => {
    const img = new Image(); img.src = 'data:image/png;base64,' + b64; await img.decode();
    const c = document.createElement('canvas'); c.width = 384; c.height = 384; const g = c.getContext('2d')!; g.drawImage(img, 0, 0);
    return [g.getImageData(96, 288, 1, 1).data[0], g.getImageData(144, 288, 1, 1).data[0]];
  }, rendered.result.base64);
  expect(pixels[0]).toBeGreaterThan(240); expect(pixels[1]).toBeLessThan(100);
  expect(errors).toEqual([]);
});
