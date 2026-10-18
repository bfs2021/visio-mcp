import { describe, expect, it } from 'vitest';
import { inspectLayout, type LayoutContext } from '../src/layout/inspectLayout';
import { getTextLayout } from '../src/layout/textLayout';
import { measureTextBox } from '../src/layout/textMeasurement';
import { createTestEngine, exactElementHashes } from './support/engine';
import { id, run } from './support/ops';

const ctx: LayoutContext = { measureWidth: (text, font) => text.length * font.sizePt * 0.5 };
async function scene() {
  const doc = structuredClone(createTestEngine().store.snapshot().document);
  doc.pages[0]!.elements = [];
  const t = createTestEngine({ document: doc });
  await run(t, [
    { op: 'createText', id: id(1), alias: 'label', bounds: { x: 30, y: 30, width: 50, height: 20 }, text: { value: 'Long unbroken label', fontSizePt: 20, wrap: false, paddingPt: 2 } },
    { op: 'createText', id: id(2), alias: 'wrapped', bounds: { x: 30, y: 100, width: 80, height: 20 }, text: { value: 'alpha beta gamma', fontSizePt: 12, wrap: true, paddingPt: 2 } },
    { op: 'createShape', id: id(3), alias: 'other', bounds: { x: 300, y: 200, width: 70, height: 40 } },
  ]);
  return t;
}

describe('text layout proposals', () => {
  it('does not break nonbreaking spaces, bounds measurement work, and rejects nonfinite computed geometry', async () => {
    const t = await scene();
    await run(t, [{ op: 'set', target: id(1), patch: { text: { value: 'hello\u00a0world', fontSizePt: 12, wrap: true, paddingPt: 4 } } }, { op: 'resize', target: id(1), bounds: { width: 40, height: 50 } }]);
    const nbsp = getTextLayout(t.store.snapshot(), ctx, { ids: [id(1)] });
    expect(nbsp.ok && nbsp.value.items[0]!.current.fits).toBe(false);
    const el = t.store.snapshot().document.pages[0]!.elements.find(e => e.id === id(1))!;
    if (el.kind !== 'text') throw Error('expected text');
    let measuredCharacters = 0;
    measureTextBox({ ...el.text, value: 'a '.repeat(32768) }, 1000000, 50, (value, font) => { measuredCharacters += value.length; return ctx.measureWidth(value, font); });
    expect(measuredCharacters).toBeLessThan(400000);
    await run(t, [{ op: 'set', target: id(1), patch: { text: { paddingPt: 1e308 } } }]);
    const extreme = getTextLayout(t.store.snapshot(), ctx, { ids: [id(1)] });
    expect(extreme.ok).toBe(false);
    if (!extreme.ok) expect(extreme.error.code).toBe('limit_exceeded');
  });
  it('does not propose a resize or font change for an empty label', async () => {
    const t = await scene();
    await run(t, [{ op: 'set', target: id(1), patch: { text: { value: '' } } }]);
    const r = getTextLayout(t.store.snapshot(), ctx, { ids: [id(1)] });
    expect(r.ok && r.value.items[0]!.current).toMatchObject({ fits: true, lines: 0 });
    expect(r.ok && r.value.operations).toEqual([]);
  });
  it('measures a committed snapshot without changing it, and grow-box fixes only the requested object', async () => {
    const t = await scene();
    const snap = t.store.snapshot(), before = exactElementHashes(snap);
    const result = getTextLayout(snap, ctx, { ids: ['label'] });
    expect(result.ok).toBe(true); if (!result.ok) return;
    expect(result.value.revision).toBe(snap.revision);
    expect(result.value.items[0]!.current).toMatchObject({ fits: false, lines: 1, requiredWidthPt: 194, requiredHeightPt: 28 });
    expect(result.value.operations).toEqual([{ op: 'resize', target: id(1), bounds: { width: 194, height: 28 } }]);
    expect(exactElementHashes(t.store.snapshot())).toEqual(before);
    await run(t, result.value.operations);
    expect(inspectLayout(t.store.snapshot(), ctx).issues.filter(i => i.code === 'TEXT_OVERFLOW' && i.elementIds.includes(id(1)))).toEqual([]);
    expect(exactElementHashes(t.store.snapshot())[id(2)]).toEqual(before[id(2)]);
    expect(exactElementHashes(t.store.snapshot())[id(3)]).toEqual(before[id(3)]);
  });

  it('grows wrapped text vertically, respects blank lines, padding and long words', async () => {
    const t = await scene();
    const r = getTextLayout(t.store.snapshot(), ctx, { ids: ['wrapped'] });
    expect(r.ok).toBe(true); if (!r.ok) return;
    expect(r.value.items[0]!.current.lines).toBe(2);
    expect(r.value.operations[0]).toMatchObject({ op: 'resize', bounds: { width: 80, height: 32.8 } });
    await run(t, [{ op: 'set', target: id(2), patch: { text: { value: 'a\n\nb', wrap: false } } }]);
    const blank = getTextLayout(t.store.snapshot(), ctx, { ids: [id(2)] });
    expect(blank.ok && blank.value.items[0]!.current.lines).toBe(3);
    await run(t, [{ op: 'set', target: id(2), patch: { text: { value: 'Unbreakablelongword', wrap: true } } }]);
    const long = getTextLayout(t.store.snapshot(), ctx, { ids: [id(2)] });
    expect(long.ok && long.value.items[0]!.current.fits).toBe(false);
    expect(long.ok && long.value.items[0]!.recommended?.fits).toBe(true);
  });

  it('shrinks fonts only to the configured floor and returns no misleading proposal if it cannot fit', async () => {
    const t = await scene();
    const r = getTextLayout(t.store.snapshot(), ctx, { ids: ['label'], strategy: 'shrink_font', minFontSizePt: 4 });
    expect(r.ok).toBe(true); if (!r.ok) return;
    expect(r.value.operations[0]).toMatchObject({ op: 'set', target: id(1) });
    expect(r.value.items[0]!.recommended).toMatchObject({ fits: true });
    await run(t, r.value.operations);
    const still = getTextLayout((await scene()).store.snapshot(), ctx, { ids: ['label'], strategy: 'shrink_font', minFontSizePt: 18 });
    expect(still.ok && still.value.operations).toEqual([]);
    expect(still.ok && still.value.items[0]!.status).toBe('cannot_fit');
  });

  it('offers conservative Visio measurements even when nowrap text fits in the editor', async () => {
    const t = await scene();
    await run(t, [{ op: 'set', target: id(1), patch: { text: { value: '↓↑', fontSizePt: 20, wrap: false } } }, { op: 'resize', target: id(1), bounds: { width: 24, height: 28 } }]);
    const editor = getTextLayout(t.store.snapshot(), ctx, { ids: ['label'] });
    const visio = getTextLayout(t.store.snapshot(), ctx, { ids: ['label'], target: 'visio' });
    expect(editor.ok && editor.value.items[0]!.current.fits).toBe(true);
    expect(visio.ok && visio.value.items[0]!.current.fits).toBe(false);
    // Real Visio measured two 24pt arrows at 48.88pt while the browser measured 24pt.
    // Reserve at least one em per symbol before applying Visio's safety allowance.
    expect(visio.ok && visio.value.items[0]!.current.requiredWidthPt).toBe(52);
    expect(visio.ok && visio.value.notes.join(' ')).toContain('not a guarantee');
    expect(visio.ok && visio.value.items[0]!.warnings).toContain('VISIO_WRAP_RISK');
  });

  it('validates options, target scope, object kinds and locks, and labels proposals outside the page', async () => {
    const t = await scene(), snap = t.store.snapshot();
    for (const options of [{ ids: [] }, { ids: Array(51).fill('label') }, { ids: ['label'], target: 'bad' }, { ids: ['label'], strategy: 'bad' }, { ids: ['label'], minFontSizePt: NaN }, { ids: ['label'], unexpected: true }])
      expect(getTextLayout(snap, ctx, options).ok).toBe(false);
    expect(getTextLayout(snap, ctx, { ids: ['missing'] }).ok).toBe(false);
    expect(getTextLayout(snap, ctx, { ids: ['other'] }).ok).toBe(false);
    await run(t, [{ op: 'set', target: id(1), patch: { locked: true } }]);
    const locked = getTextLayout(t.store.snapshot(), ctx, { ids: [id(1)] });
    expect(locked.ok && locked.value.items[0]!.status).toBe('locked');
    expect(locked.ok && locked.value.operations).toEqual([]);
    await run(t, [{ op: 'set', target: id(2), patch: { text: { value: 'a'.repeat(1000), wrap: false } } }]);
    const out = getTextLayout(t.store.snapshot(), ctx, { ids: [id(2)] });
    expect(out.ok && out.value.items[0]!.warnings).toContain('PROPOSAL_OUTSIDE_PAGE');
  });

  it('uses UUIDs on multipage documents, de-duplicates requests, and respects ancestor/layer locks', async () => {
    const t = await scene();
    await run(t, [{ op: 'group', targets: [id(1), id(2)], id: id(10) }, { op: 'set', target: id(10), patch: { locked: true } }]);
    const locked = getTextLayout(t.store.snapshot(), ctx, { ids: [id(1), id(1)] });
    expect(locked.ok && locked.value.items).toHaveLength(1);
    expect(locked.ok && locked.value.items[0]!.status).toBe('locked');
    await run(t, [{ op: 'addPage', id: id(20), name: 'Second page' }]);
    expect(getTextLayout(t.store.snapshot(), ctx, { ids: ['label'] }).ok).toBe(false);
    expect(getTextLayout(t.store.snapshot(), ctx, { ids: [id(1)] }).ok).toBe(true);
    expect(getTextLayout(t.store.snapshot(), ctx, { ids: ['label'], pageId: t.store.snapshot().document.pages[0]!.id }).ok).toBe(true);
    const fresh = await scene();
    await run(fresh, [{ op: 'addLayer', id: id(30), name: 'Locked layer' }, { op: 'assignLayers', targets: [id(1)], layerIds: [id(30)] }, { op: 'setLayer', layerId: id(30), patch: { locked: true } }]);
    const layerLocked = getTextLayout(fresh.store.snapshot(), ctx, { ids: [id(1)] });
    expect(layerLocked.ok && layerLocked.value.items[0]!.status).toBe('locked');
  });
});
