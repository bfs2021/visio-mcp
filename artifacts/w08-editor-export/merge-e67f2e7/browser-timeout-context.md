# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: render.spec.ts >> the picture shows the revision that was committed when it was asked for, not the live one
- Location: tests\browser\render.spec.ts:82:1

# Error details

```
Test timeout of 60000ms exceeded.
```

```
Error: page.evaluate: Test timeout of 60000ms exceeded.
```

# Test source

```ts
  1   | import { test, expect, type Page } from '@playwright/test';
  2   | import type { RenderResult } from '../../src/render/SnapshotRenderer';
  3   | import { armPicture, centre, drag, elements, openEditor, placeShape, pngFixture, revision, scale, sha256, snapshot } from './support';
  4   | 
  5   | type Envelope = { result?: any; error?: { code: string; message: string; details?: any } };
  6   | const call = (page: Page, method: string, params: Record<string, unknown>): Promise<Envelope> => page.evaluate(([m, p]) => (window as any).__editor.callTool(m, p), [method, params] as const);
> 7   | const apply = (page: Page, operations: unknown[]) => page.evaluate(ops => (window as any).__editor.agentApply(ops), operations);
      |                                                           ^ Error: page.evaluate: Test timeout of 60000ms exceeded.
  8   | 
  9   | /** Decodes the image the tool returned and reads pixels from it, so the test judges real pixels, not metadata. */
  10  | async function pixels(page: Page, r: RenderResult, points: Array<[number, number]>): Promise<{ width: number; height: number; at: number[][]; dark: number; coloured: (rgb: [number, number, number], tol?: number) => Promise<number> }> {
  11  |   const out = await page.evaluate(async ([b64, mime, pts]) => {
  12  |     const img = new Image();
  13  |     img.src = `data:${mime};base64,${b64}`;
  14  |     await img.decode();
  15  |     const c = document.createElement('canvas'); c.width = img.naturalWidth; c.height = img.naturalHeight;
  16  |     const g = c.getContext('2d')!; g.drawImage(img, 0, 0);
  17  |     const at = (pts as Array<[number, number]>).map(([x, y]) => Array.from(g.getImageData(Math.min(c.width - 1, Math.max(0, Math.round(x))), Math.min(c.height - 1, Math.max(0, Math.round(y))), 1, 1).data));
  18  |     const all = g.getImageData(0, 0, c.width, c.height).data;
  19  |     let dark = 0;
  20  |     for (let i = 0; i < all.length; i += 4) if (all[i]! < 90 && all[i + 1]! < 90 && all[i + 2]! < 90 && all[i + 3]! > 200) dark++;
  21  |     return { width: c.width, height: c.height, at, dark };
  22  |   }, [r.base64, r.mimeType, points] as const);
  23  |   return {
  24  |     ...out,
  25  |     coloured: (rgb, tol = 24) => page.evaluate(async ([b64, mime, want, t]) => {
  26  |       const img = new Image(); img.src = `data:${mime};base64,${b64}`; await img.decode();
  27  |       const c = document.createElement('canvas'); c.width = img.naturalWidth; c.height = img.naturalHeight;
  28  |       const g = c.getContext('2d')!; g.drawImage(img, 0, 0);
  29  |       const d = g.getImageData(0, 0, c.width, c.height).data; let n = 0;
  30  |       for (let i = 0; i < d.length; i += 4) if (Math.abs(d[i]! - want[0]) <= t && Math.abs(d[i + 1]! - want[1]) <= t && Math.abs(d[i + 2]! - want[2]) <= t && d[i + 3]! > 200) n++;
  31  |       return n;
  32  |     }, [r.base64, r.mimeType, rgb, tol] as const),
  33  |   };
  34  | }
  35  | 
  36  | const RED = { kind: 'solid', colour: '#ff0000', opacity: 1 };
  37  | const BLUE = { kind: 'solid', colour: '#0000ff', opacity: 1 };
  38  | const box = (x: number, y: number, w = 100, h = 60) => ({ x, y, width: w, height: h });
  39  | const rect = (id: string, b: object, fill: object = RED, extra: object = {}) => ({ op: 'createShape', id, bounds: b, style: { fill, stroke: { kind: 'none' } }, ...extra });
  40  | const U = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
  41  | 
  42  | test('a clean render shows committed shapes and no editor chrome, with the committed revision', async ({ page }) => {
  43  |   const errors = await openEditor(page);
  44  |   await apply(page, [rect(U(1), box(100, 100), RED, { alias: 'red' }), rect(U(2), box(400, 300), BLUE)]);
  45  |   // select a shape: the live canvas now shows handles, which must not appear in a render
  46  |   await page.evaluate(() => (window as any).__editor);
  47  |   const res = await call(page, 'doc.render', { scope: 'page' });
  48  |   expect(res.error).toBeUndefined();
  49  |   const r = res.result as RenderResult;
  50  |   expect(r.mimeType).toBe('image/png');
  51  |   expect(r.widthPx).toBe(1600);
  52  |   expect(r.revision).toBe(await revision(page));
  53  |   expect(r.mode).toBe('clean');
  54  | 
  55  |   const k = r.widthPx / r.cropPt.width;
  56  |   const px = await pixels(page, r, [[150 * k, 130 * k], [450 * k, 330 * k], [10, 10]]);
  57  |   expect(px.at[0]!.slice(0, 3)).toEqual([255, 0, 0]); // the red shape's centre
  58  |   expect(px.at[1]!.slice(0, 3)).toEqual([0, 0, 255]);
  59  |   expect(px.at[2]!.slice(0, 3)).toEqual([255, 255, 255]); // page background in the corner
  60  |   expect(await px.coloured([0, 255, 0], 40)).toBe(0); // no selection-handle green anywhere
  61  | 
  62  |   // selecting and rendering again gives the very same bytes: the render has no interest in selection
  63  |   const centre = await page.evaluate(id => (window as any).__editor.centre(id), U(1));
  64  |   await page.mouse.click(centre.x, centre.y);
  65  |   expect(await page.evaluate(() => (window as any).__editor.selection())).toEqual([U(1)]);
  66  |   const again = (await call(page, 'doc.render', { scope: 'page' })).result as RenderResult;
  67  |   expect(again.base64).toBe(r.base64);
  68  |   expect(errors.filter(e => !/Failed to load resource|favicon/.test(e))).toEqual([]);
  69  | });
  70  | 
  71  | test('debug mode adds ids and bounds over the picture; clean never does', async ({ page }) => {
  72  |   await openEditor(page);
  73  |   await apply(page, [rect(U(1), box(100, 100), { kind: 'solid', colour: '#dddddd', opacity: 1 }, { alias: 'gateway' })]);
  74  |   const clean = (await call(page, 'doc.render', { scope: 'page', mode: 'clean' })).result as RenderResult;
  75  |   const debug = (await call(page, 'doc.render', { scope: 'page', mode: 'debug' })).result as RenderResult;
  76  |   expect(debug.base64).not.toBe(clean.base64);
  77  |   expect(debug.mode).toBe('debug');
  78  |   expect(await (await pixels(page, clean, [])).coloured([225, 29, 72], 10)).toBe(0); // the first overlay colour does not occur in a clean render
  79  |   expect(await (await pixels(page, debug, [])).coloured([225, 29, 72], 10)).toBeGreaterThan(50); // the label chip and the dashed box do
  80  | });
  81  | 
  82  | test('the picture shows the revision that was committed when it was asked for, not the live one', async ({ page }) => {
  83  |   await openEditor(page);
  84  |   await apply(page, [rect(U(1), box(100, 100), RED)]);
  85  |   const asked = await revision(page);
  86  |   const pending = call(page, 'doc.render', { scope: 'page' }); // queued behind committed work, drawn after
  87  |   await apply(page, [{ op: 'set', target: U(1), patch: { style: { fill: BLUE } } }]); // a later commit while it renders
  88  |   const r = (await pending).result as RenderResult;
  89  |   expect(r.revision).toBe(asked);
  90  |   expect(await revision(page)).toBe(asked + 1);
  91  |   const k = r.widthPx / r.cropPt.width;
  92  |   expect((await pixels(page, r, [[150 * k, 130 * k]])).at[0]!.slice(0, 3)).toEqual([255, 0, 0]); // still red: that is what revision `asked` looked like
  93  | });
  94  | 
  95  | test('pictures and text are drawn, and a missing picture fails instead of leaving a blank', async ({ page }) => {
  96  |   await openEditor(page);
  97  |   const fx = pngFixture();
  98  |   const sha = await armPicture(page, fx);
  99  |   const assetId = `asset:${fx.name.replace(/\W+/g, '-')}~${sha}`;
  100 |   await apply(page, [
  101 |     { op: 'registerAsset', asset: { id: assetId, name: fx.name, mimeType: fx.mime, sha256: sha, tags: [], provenance: 'test' } },
  102 |     { op: 'createImage', id: U(1), assetId, bounds: box(100, 100, 128, 128) },
  103 |     { op: 'createText', id: U(2), bounds: box(400, 100, 300, 60), text: { value: 'Hello render', fontSizePt: 28, colour: '#000000' } },
  104 |   ]);
  105 |   const ok = await call(page, 'doc.render', { scope: 'region', elementIds: [U(1), U(2)], paddingPt: 10 });
  106 |   expect(ok.error, JSON.stringify(ok.error)).toBeUndefined();
  107 |   const r = ok.result as RenderResult;
```