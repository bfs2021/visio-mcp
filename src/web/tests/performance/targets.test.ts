import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import type { DiagramDocument, Operation, ShapeElement } from '../../src/contracts/generated';
import { canonicalSerialize } from '../../src/model/canonical';
import { createTestEngine, seedDocument } from '../support/engine';
import { run } from '../support/ops';

/**
 * Engine-level timings for spec section 18 (single edit, 100-operation batch, 500-element read). The transport, the pipe, rendering and
 * the VSDX save are measured on the real stack in tests/Diagram.Windows.Tests/PerformanceTests.cs. Preferred budgets are reported, not
 * gated: the hard assertions here are generous ceilings that only a real regression would cross, plus correctness of what was measured.
 */
const RUNS = 15;
const out: Record<string, unknown> = { machine: { node: process.version, platform: process.platform, arch: process.arch }, runs: RUNS };

function stats(ms: number[]) {
  const s = [...ms].sort((a, b) => a - b);
  const at = (q: number) => s[Math.min(s.length - 1, Math.floor(q * s.length))]!;
  return { min: +s[0]!.toFixed(2), median: +at(0.5).toFixed(2), p95: +at(0.95).toFixed(2), max: +s[s.length - 1]!.toFixed(2) };
}

function bigDocument(visible: number, pages = 1): DiagramDocument {
  const seed = seedDocument(2);
  const doc = structuredClone(seed.document);
  const template = doc.pages[0]!;
  const shapeTemplate = template.elements[1] as ShapeElement;
  const perPage = Math.ceil(visible / pages);
  doc.pages = Array.from({ length: pages }, (_, p) => {
    const page = structuredClone(template);
    page.id = `00000000-0000-4000-8000-${String(100 + p * 2).padStart(12, '0')}`;
    page.name = `Page ${p + 1}`;
    page.layers = [{ ...page.layers[0]!, id: `00000000-0000-4000-8000-${String(101 + p * 2).padStart(12, '0')}` }];
    page.elements = Array.from({ length: perPage }, (_, i) => ({
      ...structuredClone(shapeTemplate),
      id: `00000000-0000-4000-8000-${String(10000 + p * 1000 + i).padStart(12, '0')}`, alias: `p${p}s${i}`, name: `Shape ${p}-${i}`, layerIds: [page.layers[0]!.id], zIndex: i + 1,
      bounds: { x: 10 + (i % 25) * 32, y: 10 + Math.floor(i / 25) * 26, width: 28, height: 20 },
      text: { ...shapeTemplate.text!, value: `S${i}` },
    }));
    return page;
  });
  return doc;
}

describe('performance targets (engine only)', () => {
  it('single_edit_on_500_elements', async () => {
    const t = createTestEngine({ document: bigDocument(500) });
    const target = t.store.snapshot().document.pages[0]!.elements[250]!.id;
    const times: number[] = [];
    for (let i = 0; i < RUNS; i++) {
      const t0 = performance.now();
      await run(t, [{ op: 'move', target, delta: { xPt: 1, yPt: 0 } }]);
      times.push(performance.now() - t0);
    }
    out.singleEdit = { elements: 500, ...stats(times), preferredMs: 100, acceptableMs: 250 };
    expect(t.store.snapshot().document.pages[0]!.elements).toHaveLength(500);
    expect(stats(times).median).toBeLessThan(250);
  });

  it('batch_of_100_operations_is_atomic_and_changes_only_what_it_names', async () => {
    const t = createTestEngine({ document: bigDocument(500) });
    const before = t.store.snapshot();
    const named = before.document.pages[0]!.elements.slice(0, 100).map(e => e.id);
    const ops: Operation[] = named.map(id => ({ op: 'set', target: id, patch: { style: { fill: { kind: 'solid', colour: '#ffe4e6', opacity: 1 } } } }) as Operation);
    const times: number[] = [];
    for (let i = 0; i < 5; i++) {
      const fresh = createTestEngine({ document: bigDocument(500) });
      const t0 = performance.now();
      await run(fresh, ops.map((o, k) => ({ ...(o as object), target: fresh.store.snapshot().document.pages[0]!.elements[k]!.id }) as Operation));
      times.push(performance.now() - t0);
    }
    out.batch100 = { elements: 500, operations: 100, ...stats(times), preferredMs: 500 };
    await run(t, ops);
    const after = t.store.snapshot();
    const untouched = after.document.pages[0]!.elements.filter(e => !named.includes(e.id));
    expect(untouched).toHaveLength(400);
    for (const e of untouched) expect(canonicalSerialize(e)).toBe(canonicalSerialize(before.document.pages[0]!.elements.find(x => x.id === e.id)!));
    expect(stats(times).median).toBeLessThan(1000);
  });

  it('read_of_500_elements_across_pages', () => {
    const t = createTestEngine({ document: bigDocument(500, 5) });
    const times: number[] = [];
    let bytes = 0;
    for (let i = 0; i < RUNS; i++) {
      const t0 = performance.now();
      const snap = t.store.snapshot();
      bytes = canonicalSerialize(snap.document as never).length;
      times.push(performance.now() - t0);
    }
    out.read = { elements: 500, pages: 5, serialisedBytes: bytes, ...stats(times), preferredMs: 250 };
    expect(t.store.snapshot().document.pages).toHaveLength(5);
    expect(stats(times).median).toBeLessThan(500);
  });

  it('writes_the_report', () => {
    const dir = join(__dirname, '..', '..', '..', '..', 'docs', 'acceptance', 'evidence', 'PERF');
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, 'engine-timings.json'), JSON.stringify({ recorded: new Date().toISOString(), ...out }, null, 2));
    expect(out.singleEdit).toBeDefined();
  });
});
