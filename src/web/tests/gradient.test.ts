import { describe, expect, it } from 'vitest';
import { planPatch } from '../src/commands/planPatch';
import { newDocument } from '../src/model/newDocument';
import { validateDocument } from '../src/model/validate';
import { paintProblem } from '../src/model/paint';
import { compile } from '../src/script/compiler';
import { inspectLayout } from '../src/layout/inspectLayout';
import { createTestEngine, sequenceIds } from './support/engine';
import type { LinearGradientPaint, Operation } from '../src/contracts/generated';

type Stop = [number, string, number?];
const grad = (angleDeg = 90, ...stops: Stop[]): LinearGradientPaint => ({
  kind: 'linear-gradient', angleDeg,
  stops: (stops.length ? stops : ([[0, '#ffffff'], [1, '#336699']] as Stop[])).map(([offset, colour, opacity]) => ({ offset, colour, opacity: opacity ?? 1 })),
});

const plan = (ops: object[]) => {
  const doc = newDocument(); const pageId = doc.pages[0]!.id;
  return planPatch(doc, ops.map(o => ({ pageId, ...o }) as Operation), { pageId, newId: () => crypto.randomUUID() });
};
const shape = (style: object) => ({ op: 'createShape', bounds: { x: 0, y: 0, width: 100, height: 50 }, style });

describe('gradient paints', () => {
  it('a shape can be created with a gradient fill, with its stops, angle and per-stop opacity kept', () => {
    const r = plan([shape({ fill: grad(30, [0, '#ff0000'], [0.4, '#00ff00', 0.5], [1, '#0000ff']) })]);
    expect(r.ok).toBe(true); if (!r.ok) return;
    const el = r.value.forward.upserts.elements[0]!.element as unknown as { style: { fill: LinearGradientPaint } };
    expect(el.style.fill.kind).toBe('linear-gradient');
    expect(el.style.fill.angleDeg).toBe(30);
    expect(el.style.fill.stops.map(s => [s.offset, s.opacity])).toEqual([[0, 1], [0.4, 0.5], [1, 1]]);
  });

  it('gradients are fills only: strokes and page backgrounds refuse them', () => {
    expect(paintProblem(grad(), 'stroke')).toMatch(/fill a shape/);
    expect(paintProblem(grad(), 'background')).toMatch(/fill a shape/);
    expect(paintProblem(grad(), 'fill')).toBeNull();
    expect(paintProblem({ kind: 'solid', colour: '#000000', opacity: 1 }, 'stroke')).toBeNull();
    expect(plan([shape({ stroke: grad() })]).ok).toBe(false);
    expect(plan([{ op: 'setPage', patch: { background: grad() } }]).ok).toBe(false);
  });

  it('stops must be in order, and a changed fill replaces the old paint whole', async () => {
    expect(paintProblem(grad(90, [0.6, '#ffffff'], [0.2, '#000000']), 'fill')).toMatch(/in order/);
    expect(plan([shape({ fill: grad(90, [0.6, '#ffffff'], [0.2, '#000000']) })]).ok).toBe(false);
    const t = createTestEngine({ document: newDocument() });
    const page = t.store.snapshot().document.pages[0]!.id;
    await t.engine.execute(t.request([{ op: 'createShape', pageId: page, alias: 'a', bounds: { x: 0, y: 0, width: 80, height: 40 } } as Operation]), 'mcp');
    const target = t.store.snapshot().document.pages[0]!.elements[0]!.id;
    expect((await t.engine.execute(t.request([{ op: 'set', target, patch: { style: { fill: grad(0) } } } as Operation]), 'mcp')).ok).toBe(true);
    expect((t.store.snapshot().document.pages[0]!.elements[0] as unknown as { style: { fill: { kind: string } } }).style.fill.kind).toBe('linear-gradient');
    expect((await t.engine.execute(t.request([{ op: 'set', target, patch: { style: { fill: { kind: 'solid', colour: '#123456', opacity: 1 } } } } as Operation]), 'mcp')).ok).toBe(true);
    expect((t.store.snapshot().document.pages[0]!.elements[0] as unknown as { style: { fill: object } }).style.fill).toEqual({ kind: 'solid', colour: '#123456', opacity: 1 }); // no gradient leftovers
  });

  it('the schema bounds the number of stops, offsets, opacity, colours and the angle', () => {
    const accepts = (fill: unknown) => {
      const d = newDocument();
      d.pages[0]!.elements.push({ id: crypto.randomUUID(), kind: 'shape', layerIds: [], zIndex: 1, locked: false, hidden: false, bounds: { x: 0, y: 0, width: 10, height: 10 }, rotationDeg: 0, metadata: {}, geometry: { preset: 'rectangle' }, style: { fill, stroke: { kind: 'none' }, strokeWidthPt: 1, dash: 'solid', lineCap: 'butt', lineJoin: 'miter' } } as never);
      return validateDocument(d).ok;
    };
    expect(accepts(grad())).toBe(true);
    expect(accepts({ kind: 'linear-gradient', angleDeg: 90, stops: [{ offset: 0, colour: '#ffffff', opacity: 1 }] })).toBe(false);
    expect(accepts({ ...grad(), stops: Array.from({ length: 9 }, (_, i) => ({ offset: i / 8, colour: '#ffffff', opacity: 1 })) })).toBe(false);
    expect(accepts(grad(400))).toBe(false);
    expect(accepts(grad(90, [0, '#ffffff', 2], [1, '#000000']))).toBe(false);
    expect(accepts(grad(90, [-0.1, '#ffffff'], [1, '#000000']))).toBe(false);
    expect(accepts(grad(90, [0, 'white'], [1, '#000000']))).toBe(false);
  });
});

describe('DrawScript gradients', () => {
  const t = createTestEngine(); // the seed has shapes s0, s1, ...
  const run = (text: string) => compile(`set s0 fill="${text}"`, { snapshot: t.store.snapshot(), newUuid: sequenceIds('7') });
  const fillOf = (text: string): LinearGradientPaint => {
    const r = run(text);
    if (!r.ok) throw new Error(r.error.message);
    return (r.value.operations[0] as unknown as { patch: { style: { fill: LinearGradientPaint } } }).patch.style.fill;
  };

  it('"gradient ANGLE STOPS" gives a gradient, with offsets spread evenly unless given', () => {
    expect(fillOf('gradient 90 #ffffff #888888 #000000')).toEqual({ kind: 'linear-gradient', angleDeg: 90, stops: [{ offset: 0, colour: '#ffffff', opacity: 1 }, { offset: 0.5, colour: '#888888', opacity: 1 }, { offset: 1, colour: '#000000', opacity: 1 }] });
    expect(fillOf('gradient 0 #ff0000 #ffff0080@0.3 #0000ff').stops.map(s => [s.offset, s.opacity])).toEqual([[0, 1], [0.3, 0.502], [1, 1]]);
    expect(fillOf('linear-gradient -45 #ffffff #000000').angleDeg).toBe(-45);
  });

  it('mistakes are refused with a message that says what to write', () => {
    for (const bad of ['gradient 90 #ffffff', 'gradient x #ffffff #000000', 'gradient 90 #ffffff red', 'gradient 90 #ffffff@2 #000000', 'gradient 500 #ffffff #000000', 'gradient 90 #ffffff@0.8 #000000@0.2']) {
      const r = run(bad);
      expect(r.ok, bad).toBe(false);
      if (!r.ok) expect(r.error.message).toMatch(/gradient/);
    }
  });
});

describe('layout inspection with gradients', () => {
  it('text is judged against the worst stop behind it', async () => {
    const t = createTestEngine({ document: newDocument() });
    const page = t.store.snapshot().document.pages[0]!.id;
    const make = (alias: string, y: number, fill: LinearGradientPaint) => ({ op: 'createShape', pageId: page, alias, bounds: { x: 20, y, width: 200, height: 40 }, style: { fill }, text: { value: 'Label', colour: '#ffffff', fontSizePt: 12 } }) as Operation;
    await t.engine.execute(t.request([make('dark', 20, grad(0, [0, '#003366'], [1, '#000000'])), make('mixed', 90, grad(0, [0, '#003366'], [1, '#f5f5f5']))]), 'mcp');
    const report = inspectLayout(t.store.snapshot(), { measureWidth: (s, f) => s.length * f.sizePt * 0.5 });
    const low = report.issues.filter(i => i.code === 'LOW_CONTRAST');
    const idOf = (a: string) => t.store.snapshot().document.pages[0]!.elements.find(e => e.alias === a)!.id;
    expect(low.map(i => i.elementIds[0])).toEqual([idOf('mixed')]); // white text on a stop that is nearly white
  });
});
