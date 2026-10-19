import { describe, expect, it } from 'vitest';
import { measureTextBox } from '../src/layout/textMeasurement';
import { planComponent } from '../src/layout/components';
import { newDocument } from '../src/model/newDocument';
import { createTestEngine } from './support/engine';
import type { Element, Operation, TextBlock } from '../src/contracts/generated';
import { compile } from '../src/script/compiler';
import { sequenceIds } from './support/engine';

const measure = (text: string, font: { sizePt: number }) => text.length * font.sizePt * 0.5;
const block = (over: Partial<TextBlock> = {}): TextBlock => ({ value: 'Corona Sensical', fontFamily: 'Arial', fontSizePt: 12, bold: false, italic: false, underline: false, colour: '#000000', horizontalAlign: 'center', verticalAlign: 'middle', wrap: true, paddingPt: 4, ...over });

describe('text insets and orientation', () => {
  it('measures per-side insets in place of the uniform padding', () => {
    const uniform = measureTextBox(block(), 200, 100, measure);
    expect(uniform.availableWidthPt).toBe(192);
    const sided = measureTextBox(block({ insetsPt: { top: 0, right: 10, bottom: 20, left: 30 } }), 200, 100, measure);
    expect(sided.availableWidthPt).toBe(160);
    expect(sided.availableHeightPt).toBe(80);
  });

  it('vertical text flows along the shape height and reports in the shape frame', () => {
    const wide = measureTextBox(block({ value: 'Corona Sensical Lane', paddingPt: 0 }), 30, 300, measure);
    const turned = measureTextBox(block({ value: 'Corona Sensical Lane', paddingPt: 0, orientation: 'vertical' }), 30, 300, measure);
    expect(wide.fits).toBe(false); // 20 characters at 6pt do not fit 30pt across
    expect(turned.fits).toBe(true); // ...but fit running up a 300pt column
    expect(turned.lines).toBe(1);
    expect(turned.availableHeightPt).toBe(300);
    expect(turned.requiredHeightPt).toBeCloseTo(120, 5); // the length of the line, along the shape's height
    expect(turned.requiredWidthPt).toBeCloseTo(14.4, 5); // one line of 12pt text across
  });

  it('takes margins in the text frame: for vertical text left and right shorten the line, top and bottom narrow the column', () => {
    const m = measureTextBox(block({ orientation: 'vertical', insetsPt: { top: 10, right: 20, bottom: 30, left: 40 } }), 100, 300, measure);
    expect(m.availableHeightPt).toBe(240); // 300 along the shape, less left+right
    expect(m.availableWidthPt).toBe(60); // 100 across, less top+bottom
  });

  it('merges inset patches per side and returns to uniform padding', async () => {
    const t = createTestEngine({ document: newDocument() });
    const page = t.store.snapshot().document.pages[0]!.id;
    const run = (ops: object[]) => t.engine.execute(t.request(ops.map(o => ((o as { op: string }).op.startsWith('create') ? { pageId: page, ...o } : o) as Operation)), 'mcp');
    expect((await run([{ op: 'createShape', alias: 's', bounds: { x: 0, y: 0, width: 100, height: 100 }, text: { value: 'x', paddingPt: 6, insetsPt: { top: 1, right: 2, bottom: 3, left: 4 }, orientation: 'vertical' } }])).ok).toBe(true);
    const text = () => (t.store.snapshot().document.pages[0]!.elements.find(e => e.alias === 's') as Element & { text: TextBlock }).text;
    expect(text().insetsPt).toEqual({ top: 1, right: 2, bottom: 3, left: 4 });
    expect(text().orientation).toBe('vertical');
    await run([{ op: 'set', target: 's', patch: { text: { insetsPt: { left: 9 } } } }]);
    expect(text().insetsPt).toEqual({ top: 1, right: 2, bottom: 3, left: 9 });
    await run([{ op: 'set', target: 's', patch: { text: { orientation: 'horizontal' } } }]);
    expect(text().orientation).toBeUndefined();
    await run([{ op: 'set', target: 's', patch: { text: { insetsPt: null } } }]);
    expect(text().insetsPt).toBeUndefined();
    await run([{ op: 'set', target: 's', patch: { text: { insetsPt: { top: 5 } } } }]);
    expect(text().insetsPt).toEqual({ top: 5, right: 6, bottom: 6, left: 6 }); // sides not given start from the uniform padding
    await run([{ op: 'set', target: 's', patch: { text: { paddingPt: 2 } } }]);
    expect(text().insetsPt).toBeUndefined(); // a new uniform padding wins
    const bad = await run([{ op: 'set', target: 's', patch: { text: { insetsPt: { top: -1 } } } }]);
    expect(bad.ok).toBe(false);
  });

  it('DrawScript sets margins and direction', () => {
    const t = createTestEngine({ document: newDocument() });
    const r = compile('add text id=t x=0mm y=0mm w=20mm h=5mm text="hi" insetLeft=6pt insetTop=2pt textDir=vertical', { snapshot: t.store.snapshot(), newUuid: sequenceIds('7') });
    if (!r.ok) throw new Error(r.error.message);
    const json = JSON.stringify(r.value.operations);
    expect(json).toContain('"insetsPt":{"top":2,"left":6}');
    expect(json).toContain('"orientation":"vertical"');
  });
});

describe('swim-lane vertical titles', () => {
  it('turns lane titles to read up the header and keeps the font size', async () => {
    const t = createTestEngine();
    const content = { title: 'Flow', laneTitles: 'vertical', lanes: [{ title: 'Corona Sensical', heightPt: 120 }, { title: 'Ops', heightPt: 90 }] };
    const r = planComponent(t.store.snapshot(), { measureWidth: measure }, { pageId: t.ids.page, templateId: 'swimlane', content, x: 20, y: 20, layout: { widthPt: 600 } });
    if (!r.ok) throw new Error(r.error.message);
    const title = r.value.parts['lane.0.title']!;
    expect(title).toBeTruthy();
    expect((await t.engine.execute(t.request(r.value.operations), 'mcp')).ok).toBe(true);
    const lane = t.store.snapshot().document.pages[0]!.elements.find(e => e.kind === 'text' && e.text.value === 'Corona Sensical') as Element & { text: TextBlock };
    expect(lane.text.orientation).toBe('vertical');
    expect(lane.text.fontSizePt).toBe(12);
    expect(lane.bounds.height).toBeGreaterThan(lane.bounds.width); // the title box is the whole header column, not a one-line strip
  });
  it('refuses a vertical title that cannot fit the lane height', () => {
    const t = createTestEngine();
    const content = { title: 'Flow', laneTitles: 'vertical', lanes: [{ title: 'A very long lane title that will not fit sideways', heightPt: 40 }] };
    const r = planComponent(t.store.snapshot(), { measureWidth: measure }, { pageId: t.ids.page, templateId: 'swimlane', content, x: 20, y: 20, layout: { widthPt: 600 } });
    expect(r.ok).toBe(false);
  });
});
