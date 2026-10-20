import { test, expect } from '@playwright/test';
import { elements, openEditor, pngFixture, ptToClient, sha256, snapshot } from './support';

test('a dropped picture is approved first, then placed at its pinned canvas point as one undoable edit', async ({ page }) => {
  await openEditor(page);
  const png = pngFixture();
  const hash = sha256(png.bytes);
  await page.route(`**/assets/${hash}`, route => route.fulfill({ status: 200, contentType: png.mime, body: png.bytes }));
  const at = await ptToClient(page, { x: 310, y: 205 });
  const before = await snapshot(page);
  const target = await page.evaluate(({ clientX, clientY }) => (window as any).__editor.captureIngressTarget(clientX, clientY), { clientX: at.x, clientY: at.y });
  await page.evaluate(({ hash, name, width, height, target }) => {
    const ed = (window as any).__editor;
    ed.setHost({
      importIngressImage: async () => ({ ok: true, value: { asset: { id: 'asset:dropped', name, mimeType: 'image/png', sha256: hash, width, height, version: 1, tags: [], scope: 'library' }, created: true, idWasTaken: false, notes: [], width, height } }),
      libraryUse: async () => ({ ok: true, value: { ref: { preparationId: 'prep-test', expiresAt: '2030-01-01T00:00:00Z', asset: { id: 'asset:dropped', name, mimeType: 'image/png', sha256: hash, tags: [], provenance: 'user-import' } }, notes: [], width, height } }),
    });
    ed.enqueueIngress({ batchId: 'picture-drop', items: [{ ticketId: 'picture-ticket', displayName: name, kind: 'image' }], rejections: [], target });
  }, { hash, name: png.name, width: png.width, height: png.height, target });

  const queue = page.getByTestId('file-ingress-queue');
  await expect(queue).toContainText('Picture ready');
  await expect(queue.getByRole('img', { name: 'Preview of checker.png' })).toBeVisible();
  expect((await snapshot(page)).revision).toBe(before.revision);
  await queue.getByRole('button', { name: /Place in/ }).click();
  await expect.poll(async () => (await elements(page)).filter(element => element.kind === 'image').length).toBe(1);
  const image = (await elements(page)).find(element => element.kind === 'image')!;
  expect(image.bounds.x + image.bounds.width / 2).toBeCloseTo(310, -1);
  expect(image.bounds.y + image.bounds.height / 2).toBeCloseTo(205, -1);
  expect((await snapshot(page)).revision).toBe(before.revision + 1);

  await page.keyboard.press('Control+z');
  await expect.poll(async () => (await elements(page)).filter(element => element.kind === 'image').length).toBe(0);
});

test('a stale placement is refused until the person chooses a fresh drawing target; keeping leaves the document unchanged', async ({ page }) => {
  await openEditor(page);
  const png = pngFixture();
  const hash = sha256(png.bytes);
  await page.route(`**/assets/${hash}`, route => route.fulfill({ status: 200, contentType: png.mime, body: png.bytes }));
  const target = await page.evaluate(() => (window as any).__editor.captureIngressTarget(20, 20));
  await page.evaluate(({ hash, name, width, height, target }) => {
    const ed = (window as any).__editor;
    ed.setHost({
      importIngressImage: async () => ({ ok: true, value: { asset: { id: 'asset:dropped', name, mimeType: 'image/png', sha256: hash, width, height, version: 1, tags: [], scope: 'library' }, created: true, idWasTaken: false, notes: [], width, height } }),
      libraryUse: async () => ({ ok: true, value: { ref: { preparationId: 'prep-test', expiresAt: '2030-01-01T00:00:00Z', asset: { id: 'asset:dropped', name, mimeType: 'image/png', sha256: hash, tags: [], provenance: 'user-import' } }, notes: [], width, height } }),
    });
    ed.enqueueIngress({ batchId: 'stale-picture', items: [{ ticketId: 'stale-ticket', displayName: name, kind: 'image' }], rejections: [], target });
  }, { hash, name: png.name, width: png.width, height: png.height, target });

  const queue = page.getByTestId('file-ingress-queue');
  await expect(queue).toContainText('Picture ready');
  await page.evaluate(async () => (window as any).__editor.agentApply([{ op: 'createShape', alias: 'revision-change', bounds: { x: 40, y: 40, width: 40, height: 30 } }]));
  await queue.getByRole('button', { name: /Place in/ }).click();
  await expect(queue).toContainText('original drawing, page, or revision changed');
  expect((await elements(page)).filter(element => element.kind === 'image')).toHaveLength(0);
  await queue.getByRole('button', { name: 'Choose current drawing' }).click();
  await queue.getByRole('button', { name: /Place in/ }).click();
  await expect.poll(async () => (await elements(page)).filter(element => element.kind === 'image').length).toBe(1);
  await expect(queue).toContainText('one undoable change');
});

test('keeping an imported picture in the library does not change the drawing', async ({ page }) => {
  await openEditor(page);
  const png = pngFixture();
  const hash = sha256(png.bytes);
  const before = await snapshot(page);
  const target = await page.evaluate(() => (window as any).__editor.captureIngressTarget(20, 20));
  await page.evaluate(({ hash, name, width, height, target }) => {
    const ed = (window as any).__editor;
    ed.setHost({ importIngressImage: async () => ({ ok: true, value: { asset: { id: 'asset:library-only', name, mimeType: 'image/png', sha256: hash, width, height, version: 1, tags: [], scope: 'library' }, created: true, idWasTaken: false, notes: [], width, height } }) });
    ed.enqueueIngress({ batchId: 'library-picture', items: [{ ticketId: 'library-ticket', displayName: name, kind: 'image' }], rejections: [], target });
  }, { hash, name: png.name, width: png.width, height: png.height, target });

  const queue = page.getByTestId('file-ingress-queue');
  await expect(queue).toContainText('Picture ready');
  await queue.getByRole('button', { name: 'Keep in library' }).click();
  await expect(queue).toContainText('was kept in the picture library');
  expect(await snapshot(page)).toMatchObject({ revision: before.revision, document: before.document });
});

test('a picture dropped outside the canvas is offered at the page centre', async ({ page }) => {
  await openEditor(page);
  const png = pngFixture();
  const hash = sha256(png.bytes);
  await page.route(`**/assets/${hash}`, route => route.fulfill({ status: 200, contentType: png.mime, body: png.bytes }));
  const before = await snapshot(page);
  const target = await page.evaluate(() => (window as any).__editor.captureIngressTarget(10, 10));
  await page.evaluate(({ hash, name, width, height, target }) => {
    const ed = (window as any).__editor;
    ed.setHost({
      importIngressImage: async () => ({ ok: true, value: { asset: { id: 'asset:center', name, mimeType: 'image/png', sha256: hash, width, height, version: 1, tags: [], scope: 'library' }, created: true, idWasTaken: false, notes: [], width, height } }),
      libraryUse: async () => ({ ok: true, value: { ref: { preparationId: 'prep-center', expiresAt: '2030-01-01T00:00:00Z', asset: { id: 'asset:center', name, mimeType: 'image/png', sha256: hash, tags: [], provenance: 'user-import' } }, notes: [], width, height } }),
    });
    ed.enqueueIngress({ batchId: 'center-picture', items: [{ ticketId: 'center-ticket', displayName: name, kind: 'image' }], rejections: [], target });
  }, { hash, name: png.name, width: png.width, height: png.height, target });

  const queue = page.getByTestId('file-ingress-queue');
  await expect(queue).toContainText('Picture ready');
  await queue.getByRole('button', { name: /Place in/ }).click();
  await expect.poll(async () => (await elements(page)).filter(element => element.kind === 'image').length).toBe(1);
  const picture = (await elements(page)).find(element => element.kind === 'image')!;
  const pageSize = before.document.pages[0]!;
  expect(picture.bounds.x + picture.bounds.width / 2).toBeCloseTo(pageSize.widthPt / 2, -1);
  expect(picture.bounds.y + picture.bounds.height / 2).toBeCloseTo(pageSize.heightPt / 2, -1);
});
