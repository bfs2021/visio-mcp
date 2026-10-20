import { test, expect } from '@playwright/test';
import { elements, openEditor, placeShape, pngFixture, sha256, snapshot } from './support';

test('Ctrl+V routes one external bitmap into the existing approved placement offer', async ({ page }) => {
  await openEditor(page);
  const png = pngFixture();
  const hash = sha256(png.bytes);
  await page.route(`**/assets/${hash}`, route => route.fulfill({ status: 200, contentType: png.mime, body: png.bytes }));
  const before = await snapshot(page);
  await page.evaluate(({ hash, name, width, height }) => {
    let imports = 0;
    const ed = (window as any).__editor;
    ed.setHost({
      clipboardDescribe: async () => ({ ok: true, value: { sequence: 44, internalCopyId: null, hasBitmap: true, hasText: false } }),
      importClipboardImage: async () => {
        imports++;
        return { ok: true, value: { batchId: 'clipboard-44', item: { ticketId: 'clipboard-ticket', displayName: 'Clipboard image.png', kind: 'image' } } };
      },
      importIngressImage: async () => ({ ok: true, value: { asset: { id: 'asset:clipboard', name, mimeType: 'image/png', sha256: hash, width, height, version: 1, tags: [], scope: 'library' }, created: true, idWasTaken: false, notes: [], width, height } }),
      libraryUse: async () => ({ ok: true, value: { ref: { preparationId: 'clipboard-prep', expiresAt: '2030-01-01T00:00:00Z', asset: { id: 'asset:clipboard', name, mimeType: 'image/png', sha256: hash, tags: [], provenance: 'user-import' } }, notes: [], width, height } }),
    });
    (window as any).__clipboardTest = { imports: () => imports };
  }, { hash, name: png.name, width: png.width, height: png.height });

  await page.keyboard.press('Control+V');
  const queue = page.getByTestId('file-ingress-queue');
  await expect(queue).toContainText('Picture ready');
  expect(await page.evaluate(() => (window as any).__clipboardTest.imports())).toBe(1);
  expect((await snapshot(page)).revision).toBe(before.revision);
  await queue.getByRole('button', { name: /Place in/ }).click();
  await expect.poll(async () => (await elements(page)).filter(element => element.kind === 'image').length).toBe(1);
  expect((await snapshot(page)).revision).toBe(before.revision + 1);
  await page.keyboard.press('Control+z');
  await expect.poll(async () => (await elements(page)).filter(element => element.kind === 'image').length).toBe(0);
});

test('Ctrl+V does not query or import the system clipboard while typing in text controls', async ({ page }) => {
  await openEditor(page);
  await page.evaluate(() => {
    let described = 0;
    (window as any).__editor.setHost({ clipboardDescribe: async () => { described++; return { ok: true, value: { sequence: 1, internalCopyId: null, hasBitmap: true, hasText: false } }; } });
    (window as any).__clipboardTest = { described: () => described };
    const area = document.createElement('textarea'); area.setAttribute('aria-label', 'paste test textarea'); document.body.append(area);
    const rich = document.createElement('div'); rich.contentEditable = 'true'; rich.setAttribute('role', 'textbox'); rich.setAttribute('aria-label', 'paste test rich text'); document.body.append(rich);
  });
  await page.getByRole('textbox', { name: 'paste test textarea' }).focus();
  await page.keyboard.press('Control+V');
  await page.getByRole('textbox', { name: 'paste test rich text' }).focus();
  await page.keyboard.press('Control+V');
  const shape = await placeShape(page, 'shape-rectangle', { x: 350, y: 220 }, 'text-edit-target');
  await page.evaluate(id => (window as any).__editor.startTextEditing(id), shape.id);
  const canvasEditor = page.locator('[contenteditable="true"]').last();
  await expect(canvasEditor).toBeVisible();
  await canvasEditor.focus();
  await page.keyboard.press('Control+V');
  expect(await page.evaluate(() => (window as any).__clipboardTest.described())).toBe(0);
});

test('a matching internal copy marker keeps shape paste available across tabs', async ({ page }) => {
  await openEditor(page);
  await page.evaluate(() => {
    let marker: string | null = null;
    (window as any).__editor.setHost({
      markInternalCopy: async (copyId: string) => { marker = copyId; return { ok: true, value: { sequence: 1 } }; },
      clipboardDescribe: async () => ({ ok: true, value: { sequence: 1, internalCopyId: marker, hasBitmap: false, hasText: false } }),
    });
  });
  await placeShape(page, 'shape-rectangle', { x: 180, y: 140 }, 'copied-across-tabs');
  await page.keyboard.press('Control+C');
  await page.evaluate(() => (window as any).__editor.newTab());
  await page.keyboard.press('Control+V');
  await expect.poll(async () => (await elements(page)).length).toBe(1);
  expect((await elements(page))[0]!.alias).toBe('copied-across-tabs');
});

test('unsupported external clipboard text is not interpreted as a diagram image or shape', async ({ page }) => {
  await openEditor(page);
  await page.evaluate(() => {
    let imports = 0;
    (window as any).__editor.setHost({
      clipboardDescribe: async () => ({ ok: true, value: { sequence: 9, internalCopyId: null, hasBitmap: false, hasText: true } }),
      importClipboardImage: async () => { imports++; return { ok: false, error: { code: 'invalid_request', message: 'unexpected import', retryable: false } }; },
    });
    (window as any).__clipboardTest = { imports: () => imports };
  });
  await page.keyboard.press('Control+V');
  await expect(page.getByTestId('status-message')).toContainText('Text from the system clipboard cannot be pasted');
  expect(await page.evaluate(() => (window as any).__clipboardTest.imports())).toBe(0);
  expect((await snapshot(page)).document.pages[0]!.elements).toHaveLength(0);
});
