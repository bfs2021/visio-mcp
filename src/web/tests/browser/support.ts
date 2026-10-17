import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { expect, type Page } from '@playwright/test';
import type { Element, Snapshot, TransactionResult } from '../../src/contracts/generated';

export interface Pt { x: number; y: number }

export async function openEditor(page: Page): Promise<string[]> {
  const errors: string[] = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  await page.goto('/');
  await page.waitForFunction(() => !!(window as any).__editor && (window as any).__editor.sheetRect().width > 0);
  return errors;
}

export const snapshot = (page: Page) => page.evaluate(() => (window as any).__editor.snapshot()) as Promise<Snapshot>;
export const selection = (page: Page) => page.evaluate(() => (window as any).__editor.selection()) as Promise<string[]>;
export const status = (page: Page) => page.evaluate(() => (window as any).__editor.status()) as Promise<{ text: string; kind: string; dirty: boolean; savedRevision: number | null }>;
export const centre = (page: Page, id: string) => page.evaluate((i: string) => (window as any).__editor.centre(i), id) as Promise<Pt>;
export const handle = (page: Page, id: string, which: 'rotate' | 'se' | 'bend') => page.evaluate(([i, w]) => (window as any).__editor.handle(i, w), [id, which] as const) as Promise<Pt>;

export async function elements(page: Page): Promise<Element[]> { return (await snapshot(page)).document.pages[0]!.elements; }

export async function byAlias(page: Page, alias: string): Promise<Element> {
  const el = (await elements(page)).find(e => e.alias === alias);
  if (!el) throw new Error(`no element with alias ${alias}`);
  return el;
}

/** Page point (pt) to client pixels, using the visible page sheet. */
export async function ptToClient(page: Page, p: Pt): Promise<Pt> {
  const sheet = await page.evaluate(() => (window as any).__editor.sheetRect()) as { x: number; y: number; width: number; height: number };
  const snap = await snapshot(page);
  const pg = snap.document.pages[0]!;
  const k = sheet.width / pg.widthPt;
  return { x: sheet.x + p.x * k, y: sheet.y + p.y * k };
}

export async function scale(page: Page): Promise<number> {
  const sheet = await page.evaluate(() => (window as any).__editor.sheetRect()) as { width: number };
  const snap = await snapshot(page);
  return sheet.width / snap.document.pages[0]!.widthPt;
}

export async function drag(page: Page, from: Pt, to: Pt, opts: { release?: boolean; steps?: number } = {}): Promise<void> {
  await page.mouse.move(from.x, from.y);
  await page.mouse.down();
  await page.mouse.move((from.x + to.x) / 2, (from.y + to.y) / 2, { steps: opts.steps ?? 4 });
  await page.mouse.move(to.x, to.y, { steps: opts.steps ?? 4 });
  if (opts.release !== false) await page.mouse.up();
}

/** Arms a palette shape and clicks the page at the given page point. Returns the created element. */
export async function placeShape(page: Page, testId: string, at: Pt, alias?: string): Promise<Element> {
  const before = new Set((await elements(page)).map(e => e.id));
  await page.getByTestId(testId).click();
  const px = await ptToClient(page, at);
  await page.mouse.click(px.x, px.y);
  await expect.poll(async () => (await elements(page)).length).toBe(before.size + 1);
  const created = (await elements(page)).find(e => !before.has(e.id))!;
  if (alias) {
    await page.evaluate(([id, a]) => (window as any).__editor.agentApply([{ op: 'set', target: id, patch: { alias: a } }]), [created.id, alias] as const);
    await expect.poll(async () => (await elements(page)).find(e => e.id === created.id)?.alias).toBe(alias);
  }
  return (await elements(page)).find(e => e.id === created.id)!;
}

export const revision = async (page: Page) => (await snapshot(page)).revision;

// ---- image fixtures ---------------------------------------------------------------------------------------------

export interface ImageFixture { name: string; mime: 'image/png' | 'image/jpeg' | 'image/bmp' | 'image/svg+xml'; bytes: Buffer; width: number; height: number }

export const sha256 = (b: Buffer) => createHash('sha256').update(b).digest('hex');

export function pngFixture(): ImageFixture {
  const text = readFileSync(new URL('../../../../spikes/canvas/src/fixtures.ts', import.meta.url), 'utf8');
  const b64 = /PNG_BASE64 = '([^']+)'/.exec(text)![1]!;
  return { name: 'checker.png', mime: 'image/png', bytes: Buffer.from(b64, 'base64'), width: 64, height: 64 };
}

export function bmpFixture(): ImageFixture {
  const w = 16, h = 16, row = (w * 3 + 3) & ~3, size = 54 + row * h;
  const b = Buffer.alloc(size);
  b.write('BM'); b.writeUInt32LE(size, 2); b.writeUInt32LE(54, 10); b.writeUInt32LE(40, 14); b.writeInt32LE(w, 18); b.writeInt32LE(h, 22);
  b.writeUInt16LE(1, 26); b.writeUInt16LE(24, 28); b.writeUInt32LE(row * h, 34);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) { const o = 54 + y * row + x * 3; b[o] = x * 16; b[o + 1] = y * 16; b[o + 2] = 128; }
  return { name: 'gradient.bmp', mime: 'image/bmp', bytes: b, width: w, height: h };
}

export function svgFixture(): ImageFixture {
  const text = '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64"><circle cx="32" cy="32" r="28" fill="#c8501e"/></svg>';
  return { name: 'logo.svg', mime: 'image/svg+xml', bytes: Buffer.from(text), width: 64, height: 64 };
}

export async function jpegFixture(page: Page): Promise<ImageFixture> {
  const b64 = await page.evaluate(() => {
    const c = document.createElement('canvas'); c.width = 24; c.height = 16;
    const g = c.getContext('2d')!; g.fillStyle = '#2255aa'; g.fillRect(0, 0, 24, 16); g.fillStyle = '#ffffff'; g.fillRect(4, 4, 8, 8);
    return c.toDataURL('image/jpeg', 0.9).split(',')[1]!;
  });
  return { name: 'photo.jpg', mime: 'image/jpeg', bytes: Buffer.from(b64, 'base64'), width: 24, height: 16 };
}

/** Makes the next Insert picture click behave like the host: it returns a prepared reference for these bytes, which are served at /assets/<sha>. */
export async function armPicture(page: Page, fx: ImageFixture): Promise<string> {
  const sha = sha256(fx.bytes);
  await page.route(`**/assets/${sha}`, route => route.fulfill({ status: 200, contentType: fx.mime, body: fx.bytes }));
  await page.evaluate(([fixture, hash]) => {
    (window as any).__editor.setHost({
      pickImage: async () => ({
        ok: true,
        value: {
          ref: {
            preparationId: 'prep-test', expiresAt: '2030-01-01T00:00:00Z',
            asset: { id: `asset:${fixture.name.replace(/\W+/g, '-')}~${hash}`, name: fixture.name, mimeType: fixture.mime, sha256: hash, tags: [], provenance: 'user-import' },
          },
          notes: [], width: fixture.width, height: fixture.height,
        },
      }),
    });
  }, [{ name: fx.name, mime: fx.mime, width: fx.width, height: fx.height }, sha] as const);
  return sha;
}

export type { TransactionResult };
