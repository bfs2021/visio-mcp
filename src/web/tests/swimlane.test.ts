import { describe, expect, it } from 'vitest';
import { planComponent } from '../src/layout/components';
import { inspectLayout } from '../src/layout/inspectLayout';
import { findSwimlanes, membershipOf } from '../src/layout/swimlane';
import { selectObstacles } from '../src/layout/obstacles';
import { readSummary } from '../src/bridge/readTools';
import { newDocument } from '../src/model/newDocument';
import { createTestEngine, element } from './support/engine';
import type { Operation } from '../src/contracts/generated';

const measureWidth = (s: string, f: { sizePt: number }) => s.length * f.sizePt * .5;
const ctx = { measureWidth };

/** A blank A4 page with a 3-lane, 3-phase swim-lane at (20, 20), 760 wide. */
async function withSwimlane(content?: Record<string, unknown>, layout: Record<string, unknown> = { widthPt: 760 }) {
  const t = createTestEngine({ document: newDocument() });
  const page = t.store.snapshot().document.pages[0]!.id;
  const plan = planComponent(t.store.snapshot(), ctx, { pageId: page, templateId: 'swimlane', x: 20, y: 20, layout, ...(content ? { content } : {}) });
  if (!plan.ok) throw new Error(plan.error.message);
  expect((await t.engine.execute(t.request(plan.value.operations), 'mcp')).ok).toBe(true);
  return { t, page, plan: plan.value };
}
const place = async (t: Awaited<ReturnType<typeof withSwimlane>>['t'], page: string, alias: string, x: number, y: number, w = 60, h = 30) =>
  t.engine.execute(t.request([{ op: 'createShape', alias, pageId: page, bounds: { x, y, width: w, height: h } } as Operation]), 'gui');
const summary = (t: Awaited<ReturnType<typeof withSwimlane>>['t']) => (readSummary(t.store.snapshot()) as { ok: true; value: any }).value;

describe('swim-lane component', () => {
  it('builds a frame, title bar, phase header row and stacked lanes with header cells and column rules', async () => {
    const { t, page, plan } = await withSwimlane();
    expect(plan.templateId).toBe('swimlane');
    const p = t.store.snapshot().document.pages[0]!;
    const role = (r: string) => p.elements.find(e => e.metadata['layout.role'] === r)!;
    // 3 lanes of 90 under a 30 title bar and a 26 header row: 326 tall, as wide as asked
    const frame = role('frame');
    expect(frame.bounds).toEqual({ x: 20, y: 20, width: 760, height: 326 });
    expect(role('title.bar').bounds).toMatchObject({ x: 20, y: 20, width: 760, height: 30 });
    expect([0, 1, 2].map(i => role(`lane.${i}.band`).bounds.y)).toEqual([76, 166, 256]);
    expect(role('lane.0.header').bounds).toMatchObject({ x: 20, width: 96, height: 90 });
    expect([0, 1, 2].map(i => role(`phase.${i}.header`).bounds.width)).toEqual([664 / 3, 664 / 3, 664 / 3]);
    expect([1, 2].map(i => role(`phase.${i}.rule`).bounds.width)).toEqual([1, 1]);
    expect(role('lane.1.title').kind).toBe('text');
    // structure is marked; text and the group are not
    const structure = p.elements.filter(e => e.metadata['swimlane.structure'] === '1').map(e => e.metadata['layout.role']);
    expect(structure).toContain('frame'); expect(structure).toContain('lane.2.header'); expect(structure).toContain('phase.1.rule');
    expect(structure).not.toContain('title');
    expect(plan.parts.frame).toBe(frame.id);
    expect(page).toBeTruthy();
  });

  it('is one editable group created and undone in one step', async () => {
    const { t, plan } = await withSwimlane();
    const group = element(t.store.snapshot(), plan.groupId);
    expect(group.kind).toBe('group'); expect(group.metadata['layout.template']).toBe('swimlane');
    expect(group.bounds.width).toBeCloseTo(760);
    const before = t.engine.revision;
    await t.engine.undo({ ...t.scope(), transactionId: crypto.randomUUID(), baseRevision: before });
    expect(t.store.snapshot().document.pages[0]!.elements).toHaveLength(0);
  });

  it('lanes without phases have no header row, and lane colours, heights and header width are honoured', async () => {
    const { t, plan } = await withSwimlane({ title: 'Lanes only', lanes: [{ title: 'A', heightPt: 60, colour: '#112233' }, { title: 'B', heightPt: 120 }], laneHeaderWidthPt: 120 }, { widthPt: 500 });
    expect(plan.parts.corner).toBeUndefined();
    expect(plan.parts['phase.0.header']).toBeUndefined();
    const a = element(t.store.snapshot(), plan.parts['lane.0.header']!);
    expect(a.kind === 'shape' && a.style.fill).toEqual({ kind: 'solid', colour: '#112233', opacity: 1 });
    expect(a.bounds).toMatchObject({ width: 120, height: 60 });
    expect(element(t.store.snapshot(), plan.parts.frame!).bounds.height).toBe(30 + 60 + 120);
  });

  it('refuses nonsense: no lanes, too many, bad colours and heights, unknown keys, columns that cannot fit', () => {
    const t = createTestEngine({ document: newDocument() });
    const page = t.store.snapshot().document.pages[0]!.id;
    const bad = (content: unknown, layout: object = { widthPt: 760 }) => planComponent(t.store.snapshot(), ctx, { pageId: page, templateId: 'swimlane', content, layout }).ok;
    expect(bad({ title: 'x', lanes: [] })).toBe(false);
    expect(bad({ title: 'x', lanes: Array.from({ length: 11 }, () => ({ title: 'l' })) })).toBe(false);
    expect(bad({ title: 'x', lanes: [{ title: 'l', colour: 'red' }] })).toBe(false);
    expect(bad({ title: 'x', lanes: [{ title: 'l', heightPt: 5 }] })).toBe(false);
    expect(bad({ title: 'x', lanes: [{ title: 'l', mystery: 1 }] })).toBe(false);
    expect(bad({ title: 'x', lanes: [{ title: 'l' }], phases: Array.from({ length: 9 }, () => ({ title: 'p' })) })).toBe(false);
    expect(bad({ title: 'x', lanes: [{ title: 'l' }], phases: [{ title: 'p' }, { title: 'q' }, { title: 'r' }] }, { widthPt: 200 })).toBe(false);
    expect(bad({ title: 'x', lanes: [{ title: 'l' }] })).toBe(true);
  });

  it('reflows to a new width keeping lane heights, stable ids and what a person typed', async () => {
    const { t, page, plan } = await withSwimlane();
    await t.engine.execute(t.request([{ op: 'set', target: plan.parts['lane.1.title']!, patch: { text: { value: 'Build pipeline', colour: '#ffff00' } } }]), 'gui');
    const typed = element(t.store.snapshot(), plan.parts['lane.1.title']!);
    const reflow = planComponent(t.store.snapshot(), ctx, { pageId: page, componentId: plan.groupId, layout: { widthPt: 600 } });
    expect(reflow.ok).toBe(true); if (!reflow.ok) return;
    expect(reflow.value.parts).toEqual(plan.parts);
    expect((await t.engine.execute(t.request(reflow.value.operations), 'mcp')).ok).toBe(true);
    const snap = t.store.snapshot();
    expect(element(snap, plan.parts.frame!).bounds).toMatchObject({ width: 600, height: 326 });
    expect(element(snap, plan.parts['phase.2.header']!).bounds.x).toBeCloseTo(20 + 96 + 2 * (504 / 3));
    const after = element(snap, plan.parts['lane.1.title']!);
    expect(after.kind === 'text' && typed.kind === 'text' && after.text).toEqual(typed.kind === 'text' && typed.text);
    expect(element(snap, plan.parts['lane.2.band']!).bounds.y).toBe(256);
  });
});

describe('lane and phase membership', () => {
  it('reports the lane and phase of every element by its middle, and none for elements outside', async () => {
    const { t, page } = await withSwimlane({ title: 'PSGL', lanes: [{ title: 'Corona' }, { title: 'Pipeline' }, { title: 'Ops' }], phases: [{ title: 'Day 1' }, { title: 'Day 2' }, { title: 'Day 3' }] });
    await place(t, page, 'a', 160, 100);   // lane 0, phase 0
    await place(t, page, 'b', 600, 190);   // lane 1, phase 2
    await place(t, page, 'c', 380, 280);   // lane 2, phase 1
    await place(t, page, 'out', 100, 400); // below the swim-lane
    await place(t, page, 'header', 40, 100); // in the lane header column: a lane but no phase
    const s = summary(t);
    const byAlias = (a: string) => s.pages[0].elements.find((e: any) => e.alias === a);
    expect(byAlias('a').lane).toEqual({ swimlane: 'PSGL', lane: 'Corona', phase: 'Day 1' });
    expect(byAlias('b').lane).toEqual({ swimlane: 'PSGL', lane: 'Pipeline', phase: 'Day 3' });
    expect(byAlias('c').lane).toEqual({ swimlane: 'PSGL', lane: 'Ops', phase: 'Day 2' });
    expect(byAlias('out').lane).toBeUndefined();
    expect(byAlias('header').lane).toEqual({ swimlane: 'PSGL', lane: 'Corona' });
    // the structure itself is not "in" a lane; the swim-lane is described once
    const names = s.pages[0].elements.filter((e: any) => e.lane).map((e: any) => e.alias).sort();
    expect(names).toEqual(['a', 'b', 'c', 'header']);
    expect(s.pages[0].swimlanes).toHaveLength(1);
    expect(s.pages[0].swimlanes[0].lanes.map((l: any) => l.title)).toEqual(['Corona', 'Pipeline', 'Ops']);
  });

  it('follows the lanes when they are resized by ordinary edits', async () => {
    const { t, page, plan } = await withSwimlane({ title: 'S', lanes: [{ title: 'Top' }, { title: 'Bottom' }] });
    await place(t, page, 'x', 200, 170); // middle at y=185: lane 1 (76..166)? No: lanes start at 20+30 = 50
    const before = findSwimlanes(t.store.snapshot().document.pages[0]!)[0]!;
    expect(before.lanes.map(l => l.bounds.y)).toEqual([50, 140]);
    // grow the top lane by 100pt and push the one below it down
    await t.engine.execute(t.request([
      { op: 'resize', target: plan.parts['lane.0.band']!, bounds: { x: 20, y: 50, width: 760, height: 190 } },
      { op: 'move', target: plan.parts['lane.1.band']!, delta: { xPt: 0, yPt: 100 } },
    ] as Operation[]), 'gui');
    const after = findSwimlanes(t.store.snapshot().document.pages[0]!)[0]!;
    expect(membershipOf([before], { x: 200, y: 170, width: 60, height: 30 })!.lane).toBe('Bottom');
    expect(membershipOf([after], { x: 200, y: 170, width: 60, height: 30 })!.lane).toBe('Top');
  });

  it('the smallest swim-lane holding an element wins when they are nested', () => {
    const t = createTestEngine({ document: newDocument() });
    expect(membershipOf([], { x: 0, y: 0, width: 1, height: 1 })).toBeUndefined();
    void t;
  });
});

describe('layout inspection understands lanes', () => {
  it('does not report the structure as collisions, but still reports real ones and shapes spilling over a lane boundary', async () => {
    const { t, page } = await withSwimlane({ title: 'S', lanes: [{ title: 'Top' }, { title: 'Bottom' }], phases: [{ title: 'One' }, { title: 'Two' }] });
    await place(t, page, 'in-lane', 140, 70, 80, 40);   // wholly inside the top lane
    await place(t, page, 'neighbour', 150, 75, 80, 40); // genuinely overlaps in-lane
    await place(t, page, 'spill', 420, 120, 80, 50);    // top lane ends at 140: this one crosses into the bottom lane
    const report = inspectLayout(t.store.snapshot(), ctx);
    const codes = report.issues.map(i => i.code);
    const overlaps = report.issues.filter(i => i.code === 'OVERLAP');
    expect(overlaps).toHaveLength(1); // only the two real shapes, not the lane bands, header cells or frame
    const ids = (a: string) => t.store.snapshot().document.pages[0]!.elements.find(e => e.alias === a)!.id;
    expect(overlaps[0]!.elementIds.sort()).toEqual([ids('in-lane'), ids('neighbour')].sort());
    expect(codes).not.toContain('ZERO_SIZE'); // the 1pt column rules are rules, not mistakes
    const straddle = report.issues.filter(i => i.code === 'STRADDLES_LANES');
    expect(straddle.map(i => i.elementIds[0])).toEqual([ids('spill')]);
    expect(straddle[0]!.severity).toBe('info');
    expect(straddle[0]!.evidence.lanesTouched).toEqual(['Top', 'Bottom']);
  });

  it('connectors between lanes are not blamed for crossing the lane structure', async () => {
    const { t, page } = await withSwimlane({ title: 'S', lanes: [{ title: 'Top' }, { title: 'Bottom' }], phases: [{ title: 'One' }, { title: 'Two' }] });
    await place(t, page, 'from', 140, 70);
    await place(t, page, 'to', 440, 170);
    const els = t.store.snapshot().document.pages[0]!.elements;
    const from = els.find(e => e.alias === 'from')!.id, to = els.find(e => e.alias === 'to')!.id;
    await t.engine.execute(t.request([{ op: 'createConnector', pageId: page, from: { elementId: from }, to: { elementId: to } } as Operation]), 'gui');
    const issues = inspectLayout(t.store.snapshot(), ctx).issues.map(i => i.code);
    expect(issues).not.toContain('CONNECTOR_CROSSES_SHAPE');
  });

  it('routing treats lane structure as the ground a connector runs on, not as obstacles', async () => {
    const { t } = await withSwimlane({ title: 'S', lanes: [{ title: 'Top' }], phases: [{ title: 'One' }, { title: 'Two' }] });
    const p = t.store.snapshot().document.pages[0]!;
    const obstacles = selectObstacles(p.elements, p.layers, new Set(), [], [], p.widthPt * p.heightPt);
    expect(obstacles).toEqual([]);
  });
});
