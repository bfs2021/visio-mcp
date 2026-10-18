import Ajv from 'ajv';
import { expect, it } from 'vitest';
import { planComponent, componentCatalog } from '../src/layout/components';
import { createTestEngine, element } from './support/engine';
const measureWidth = (s: string, f: { sizePt: number }) => s.length * f.sizePt * .5;

for (const template of ['repository-card', 'labelled-container', 'environment-columns', 'pipeline', 'swimlane']) it(`creates ${template} as an editable group in one undo step without mutating during planning`, async () => {
  const t = createTestEngine(); const before = t.store.snapshot();
  const result = planComponent(before, { measureWidth }, { pageId: t.ids.page, templateId: template, x: 20, y: 20, layout: { widthPt: 500 } });
  expect(result.ok).toBe(true); if (!result.ok) return;
  expect(t.store.snapshot()).toEqual(before);
  expect(result.value.parts.frame).toBeTruthy(); expect(result.value.parts.title).toBeTruthy();
  expect((await t.engine.execute(t.request(result.value.operations), 'mcp')).ok).toBe(true);
  const group = element(t.store.snapshot(), result.value.groupId);
  expect(group.kind).toBe('group'); expect(group.metadata['layout.template']).toBe(template);
  expect(group.bounds.width).toBeCloseTo(500);
  await t.engine.undo({ ...t.scope(), transactionId: crypto.randomUUID(), baseRevision: t.engine.revision });
  expect(t.store.snapshot().document.pages).toEqual(before.document.pages);
});

it('reflows current text and styles with stable IDs, fixed font sizes and measured wrapping', async () => {
  const t = createTestEngine();
  const first = planComponent(t.store.snapshot(), { measureWidth }, { pageId: t.ids.page, templateId: 'repository-card', layout: { widthPt: 480 }, content: { title: 'Infrastructure repository', items: ['Bicep modules', 'Release manifests'] } });
  expect(first.ok).toBe(true); if (!first.ok) return;
  expect((await t.engine.execute(t.request(first.value.operations), 'mcp')).ok).toBe(true);
  const textId = first.value.parts['item.0']!;
  await t.engine.execute(t.request([{ op: 'set', target: textId, patch: { text: { value: 'A substantially longer requirement that must wrap onto several measured lines', colour: '#15803D', fontSizePt: 16 } } }]), 'gui');
  const before = element(t.store.snapshot(), textId);
  const reflow = planComponent(t.store.snapshot(), { measureWidth }, { pageId: t.ids.page, componentId: first.value.groupId, layout: { widthPt: 240 } });
  expect(reflow.ok).toBe(true); if (!reflow.ok) return;
  expect(reflow.value.groupId).toBe(first.value.groupId); expect(reflow.value.parts).toEqual(first.value.parts);
  expect((await t.engine.execute(t.request(reflow.value.operations), 'mcp')).ok).toBe(true);
  const after = element(t.store.snapshot(), textId);
  expect(after.kind === 'text' && before.kind === 'text' && after.text).toEqual(before.kind === 'text' && before.text);
  expect(after.bounds.height).toBeGreaterThan(before.bounds.height);
  expect(element(t.store.snapshot(), first.value.groupId).bounds.width).toBeCloseTo(240);
});

it('refuses damaged, locked and rotated component structures without suggesting destructive reconstruction', async () => {
  for (const patch of [{ locked: true }, { rotationDeg: 20 }, { metadata: { 'layout.role': 'broken' } }]) {
    const t = createTestEngine(); const p = planComponent(t.store.snapshot(), { measureWidth }, { pageId: t.ids.page, templateId: 'labelled-container' });
    if (!p.ok) throw new Error(p.error.message);
    expect((await t.engine.execute(t.request(p.value.operations), 'mcp')).ok).toBe(true);
    const change = 'rotationDeg' in patch ? { op: 'rotate', target: p.value.parts.title!, angleDeg: 20 } : { op: 'set', target: p.value.parts.title!, patch };
    expect((await t.engine.execute(t.request([change as never]), 'gui')).ok).toBe(true);
    const before = t.store.snapshot();
    expect(planComponent(before, { measureWidth }, { pageId: t.ids.page, componentId: p.value.groupId }).ok).toBe(false);
    expect(t.store.snapshot()).toEqual(before);
  }
});

it('bounds inputs and rejects ambiguous or unsupported options', () => {
  const t = createTestEngine(); const base = { pageId: t.ids.page, templateId: 'pipeline' };
  for (const bad of [{ layout: { widthPt: NaN } }, { layout: { paddingPt: 9999 } }, { layout: { mystery: 1 } }, { content: { steps: Array.from({ length: 30 }, () => ({ title: 'Step' })) } }, { content: { title: 'x'.repeat(20000) } }, { templateId: 'unknown' }, { componentId: t.ids.shape }])
    expect(planComponent(t.store.snapshot(), { measureWidth }, { ...base, ...bad }).ok).toBe(false);
  expect(componentCatalog.components).toHaveLength(5);
});

it('reflows duplicated components independently and preserves the original geometry', async () => {
  const t = createTestEngine(); const p = planComponent(t.store.snapshot(), { measureWidth }, { pageId: t.ids.page, templateId: 'repository-card' });
  if (!p.ok) throw new Error(p.error.message);
  expect((await t.engine.execute(t.request(p.value.operations), 'mcp')).ok).toBe(true);
  const copyId = crypto.randomUUID();
  expect((await t.engine.execute(t.request([{ op: 'duplicate', id: copyId, targets: [p.value.groupId], offset: { xPt: 20, yPt: 300 } }]), 'gui')).ok).toBe(true);
  const original = structuredClone(element(t.store.snapshot(), p.value.groupId));
  const copy = planComponent(t.store.snapshot(), { measureWidth }, { pageId: t.ids.page, componentId: copyId, layout: { widthPt: 300 } });
  expect(copy.ok).toBe(true); if (!copy.ok) return;
  expect(Object.values(copy.value.parts).every(id => !Object.values(p.value.parts).includes(id))).toBe(true);
  expect((await t.engine.execute(t.request(copy.value.operations), 'mcp')).ok).toBe(true);
  expect(element(t.store.snapshot(), p.value.groupId)).toEqual(original);
});

it('does not allow component reflow through a locked ancestor group', async () => {
  const t = createTestEngine(); const p = planComponent(t.store.snapshot(), { measureWidth }, { pageId: t.ids.page, templateId: 'labelled-container' });
  if (!p.ok) throw new Error(p.error.message);
  expect((await t.engine.execute(t.request(p.value.operations), 'mcp')).ok).toBe(true);
  const ancestor = crypto.randomUUID();
  expect((await t.engine.execute(t.request([{ op: 'group', id: ancestor, targets: [p.value.groupId, t.ids.shape] }, { op: 'set', target: ancestor, patch: { locked: true } }]), 'gui')).ok).toBe(true);
  const result = planComponent(t.store.snapshot(), { measureWidth }, { pageId: t.ids.page, componentId: p.value.groupId });
  expect(result.ok).toBe(false); if (!result.ok) expect(result.error.message).toContain('locked');
});

it('publishes content schemas that accept examples and reject missing structural arrays', () => {
  const ajv = new Ajv();
  for (const component of componentCatalog.components) {
    const validate = ajv.compile(component.contentSchema);
    expect(validate(component.example), JSON.stringify(validate.errors)).toBe(true);
    if (component.id === 'pipeline' || component.id === 'environment-columns') expect(validate({ title: 'Custom title' })).toBe(false);
    expect(validate({ ...component.example, unknown: true })).toBe(false);
  }
});