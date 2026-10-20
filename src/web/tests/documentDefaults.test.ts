import { describe, expect, it } from 'vitest';
import { newDocument } from '../src/model/newDocument';
import { readDocumentDefaults, serializeDocumentDefaults } from '../src/model/documentDefaults';
import { captureDefaults, defaultPreferences } from '../src/model/preferences';
import { canonicalSerialize } from '../src/model/canonical';
import { validateDocument } from '../src/model/validate';
import { compile } from '../src/script/compiler';
import { restoreRecovery } from '../src/commands/recovery';
import { CommandEngine } from '../src/commands/CommandEngine';
import { planComponent } from '../src/layout/components';
import { iconOperations } from '../src/icons/catalog';
import { copyElements, pasteOperations } from '../src/commands/clipboard';
import type { DiagramDocument, Operation } from '../src/contracts/generated';

const captured = () => {
  const p = defaultPreferences();
  p.grid = { spacingPt: 27, visible: false, snap: false };
  p.font = { family: 'Aptos', sizePt: 19 };
  return captureDefaults(p);
};
const id = () => crypto.randomUUID();
const box = { x: 10, y: 10, width: 80, height: 40 };
const free = (x: number) => ({ glue: 'none' as const, point: { x, y: 10 } });
function fixture(document: DiagramDocument) {
  const engine = new CommandEngine({ document });
  const run = (operations: Operation[]) => engine.execute({ ...engine.scope(), transactionId: id(), baseRevision: engine.revision, atomic: true, operations }, 'mcp');
  return { engine, run };
}

describe('captured document defaults', () => {
  it('captures a canonical validated snapshot for the first page', () => {
    const defaults = captured();
    const doc = newDocument(id, undefined, defaults);
    expect(doc.metadata['editor.defaults.v1']).toBe(JSON.stringify({ grid: defaults.grid, font: defaults.font }));
    expect(doc.pages[0]!.grid).toEqual(defaults.grid);
    expect(readDocumentDefaults(doc)).toEqual({ defaults });
    defaults.grid.spacingPt = 99;
    expect(doc.pages[0]!.grid.spacingPt).toBe(27);
    expect(newDocument(id).metadata).toEqual({});
    expect(() => newDocument(id, undefined, { ...captured(), font: { family: '', sizePt: 12 } })).toThrow();
  });

  it('serializes equal captures to identical bytes regardless of input key insertion order', () => {
    const defaults = captured();
    const reordered = {
      font: { sizePt: defaults.font.sizePt, family: defaults.font.family },
      grid: { snap: defaults.grid.snap, visible: defaults.grid.visible, spacingPt: defaults.grid.spacingPt },
    };
    expect(serializeDocumentDefaults(reordered)).toBe(serializeDocumentDefaults(defaults));
    expect(newDocument(id, undefined, reordered).metadata).toEqual(newDocument(id, undefined, defaults).metadata);
  });

  it('keeps captured settings after the source preferences change, and leaves legacy documents untouched', async () => {
    const preferences = defaultPreferences();
    preferences.font = { family: 'Aptos', sizePt: 19 };
    preferences.grid = { spacingPt: 27, visible: false, snap: false };
    const doc = newDocument(id, undefined, captureDefaults(preferences));
    preferences.font.family = 'Later Font';
    preferences.grid.spacingPt = 55;
    const { engine, run } = fixture(doc);
    await run([{ op: 'createText', bounds: box, text: { value: 'first' } }, { op: 'addPage', name: 'Later' }]);
    expect(engine.store.snapshot().document.pages[0]!.elements[0]).toMatchObject({ text: { fontFamily: 'Aptos', fontSizePt: 19 } });
    expect(engine.store.snapshot().document.pages[1]!.grid.spacingPt).toBe(27);

    const legacy = newDocument(id);
    const before = canonicalSerialize(legacy);
    expect(readDocumentDefaults(legacy).diagnostic).toBeUndefined();
    expect(canonicalSerialize(legacy)).toBe(before);
    expect(legacy.metadata).toEqual({});
  });
  it('uses captured font for new and first-materialized text while preserving existing format', async () => {
    const { engine, run } = fixture(newDocument(id, undefined, captured()));
    const r = await run([
      { op: 'createShape', alias: 'shape', bounds: box, text: { value: 'shape' } },
      { op: 'createText', alias: 'text', bounds: box, text: { value: 'text', fontSizePt: 22 } },
      { op: 'createConnector', alias: 'line', from: free(10), to: free(100), label: { value: 'line', fontFamily: 'Explicit' } },
      { op: 'createShape', alias: 'empty', bounds: box },
      { op: 'createConnector', alias: 'empty-line', from: free(20), to: free(120) },
    ]);
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    const elements = engine.store.snapshot().document.pages[0]!.elements;
    const byAlias = (alias: string) => elements.find(e => e.alias === alias)!;
    expect(byAlias('shape')).toMatchObject({ text: { fontFamily: 'Aptos', fontSizePt: 19 } });
    expect(byAlias('text')).toMatchObject({ text: { fontFamily: 'Aptos', fontSizePt: 22 } });
    expect(byAlias('line')).toMatchObject({ label: { fontFamily: 'Explicit', fontSizePt: 19 } });
    await run([{ op: 'set', target: 'empty', patch: { text: { value: 'first' } } }, { op: 'set', target: 'empty-line', patch: { text: { value: 'first' } } }, { op: 'set', target: 'text', patch: { text: { value: 'edited' } } }]);
    const changed = engine.store.snapshot().document.pages[0]!.elements;
    expect(changed.find(e => e.alias === 'empty')).toMatchObject({ text: { fontFamily: 'Aptos', fontSizePt: 19 } });
    expect(changed.find(e => e.alias === 'empty-line')).toMatchObject({ label: { fontFamily: 'Aptos', fontSizePt: 19 } });
    expect(changed.find(e => e.alias === 'text')).toMatchObject({ text: { fontFamily: 'Aptos', fontSizePt: 22 } });
  });

  it('uses captured grid on addPage and restores exactly on undo', async () => {
    const { engine, run } = fixture(newDocument(id, undefined, captured()));
    const before = canonicalSerialize({ ...engine.store.snapshot().document, revision: 0 });
    const result = await run([{ op: 'addPage', name: 'Second' }]);
    expect(result.ok).toBe(true);
    expect(engine.store.snapshot().document.pages[1]!.grid).toEqual(captured().grid);
    await engine.undo({ ...engine.scope(), transactionId: id(), baseRevision: engine.revision });
    expect(canonicalSerialize({ ...engine.store.snapshot().document, revision: 0 })).toBe(before);
  });

  it('keeps capture and UUIDs through JSON validation and recovery replay', async () => {
    const original = newDocument(id, undefined, captured());
    const parsed = validateDocument(JSON.parse(JSON.stringify(original)));
    expect(parsed.ok).toBe(true);
    if (!parsed.ok) return;
    expect(parsed.value.metadata).toEqual(original.metadata);
    expect(parsed.value.id).toBe(original.id);
    const { engine, run } = fixture(parsed.value);
    const events: unknown[] = [];
    engine.onCommitted(event => events.push(JSON.parse(JSON.stringify(event))));
    const r = await run([{ op: 'createText', bounds: box, text: { value: 'replay' } }]);
    expect(r.ok).toBe(true);
    const restored = restoreRecovery({ documentId: original.id, sessionId: engine.sessionId, checkpointRevision: 0, document: original, events, lastDurableRevision: engine.revision });
    expect(restored.ok).toBe(true);
    if (restored.ok) expect(canonicalSerialize(restored.value.document)).toBe(canonicalSerialize(engine.store.snapshot().document));
  });

  it('canonical and DSL creation use the same captured font and grid', async () => {
    const document = newDocument(id, undefined, captured());
    const direct = fixture(structuredClone(document));
    const scripted = fixture(structuredClone(document));
    await direct.run([{ op: 'createShape', alias: 'node', bounds: box, text: { value: 'Node' } }, { op: 'addPage', name: 'Second' }]);
    const script = 'add shape id=node x=10pt y=10pt w=80pt h=40pt text="Node"\npage add "Second"';
    const compiled = compile(script, { snapshot: scripted.engine.store.snapshot(), newUuid: id });
    expect(compiled.ok).toBe(true);
    if (!compiled.ok) return;
    await scripted.run(compiled.value.operations);
    const a = direct.engine.store.snapshot().document;
    const b = scripted.engine.store.snapshot().document;
    expect(a.pages[0]!.elements[0]).toMatchObject({ text: { fontFamily: 'Aptos', fontSizePt: 19 } });
    expect(b.pages[0]!.elements[0]).toMatchObject({ text: { fontFamily: 'Aptos', fontSizePt: 19 } });
    expect(a.pages[1]!.grid).toEqual(b.pages[1]!.grid);
  });
  it('preserves component and icon authored formatting through clipboard and JSON import', async () => {
    const { engine, run } = fixture(newDocument(id, undefined, captured()));
    const pageId = engine.store.snapshot().document.pages[0]!.id;
    const proposal = planComponent(engine.store.snapshot(), { measureWidth: (text, font) => text.length * font.sizePt / 2 }, {
      pageId, templateId: 'repository-card', content: { title: 'Authored title', items: ['One item'] },
    });
    expect(proposal.ok).toBe(true);
    if (!proposal.ok) return;
    expect((await run(proposal.value.operations)).ok).toBe(true);
    const icon = iconOperations({ pageId, iconId: 'lucide:git-branch', colour: '#16A34A', sizePt: 24 });
    expect(icon.ok).toBe(true);
    if (!icon.ok) return;
    expect((await run(icon.value.operations)).ok).toBe(true);
    const source = engine.store.snapshot();
    const title = source.document.pages[0]!.elements.find(e => e.metadata['layout.role'] === 'title')!;
    const iconShape = source.document.pages[0]!.elements.find(e => e.id === icon.value.elementId)!;
    expect(title).toMatchObject({ kind: 'text', text: { fontFamily: 'Arial', fontSizePt: 15, colour: '#1e293b' } });
    expect(iconShape).toMatchObject({ kind: 'shape', style: { stroke: { colour: '#16A34A' } } });
    const clip = copyElements(source, [title.id, iconShape.id])!;
    const copied = pasteOperations(clip, source.document.pages[0]!, 32, id);
    expect((await run(copied.operations)).ok).toBe(true);
    const imported = validateDocument(JSON.parse(JSON.stringify(engine.store.snapshot().document)));
    expect(imported.ok).toBe(true);
    if (!imported.ok) return;
    const pasted = imported.value.pages[0]!.elements.filter(e => copied.newIds.includes(e.id));
    expect(pasted.find(e => e.kind === 'text')).toMatchObject({ text: { fontFamily: 'Arial', fontSizePt: 15, colour: '#1e293b' } });
    expect(pasted.find(e => e.kind === 'shape')).toMatchObject({ style: { stroke: { colour: '#16A34A' } } });
    expect(imported.value.metadata['editor.defaults.v1']).toBe(source.document.metadata['editor.defaults.v1']);
  });
  it.each(['{', JSON.stringify({ grid: { spacingPt: 0, visible: true, snap: true }, font: { family: 'X', sizePt: 12 } }), 'é'.repeat(4097)])('falls back without changing malformed metadata', async raw => {
    const doc = newDocument(id);
    doc.metadata['editor.defaults.v1'] = raw;
    const before = canonicalSerialize(doc);
    const { engine, run } = fixture(doc);
    const snapshot = engine.store.snapshot();
    const revision = engine.revision;
    expect(readDocumentDefaults(snapshot.document).diagnostic).toBeTruthy();
    expect(engine.store.snapshot()).toEqual(snapshot);
    expect(canonicalSerialize(engine.store.snapshot().document)).toBe(before);
    expect(engine.revision).toBe(revision);
    const result = await run([{ op: 'createText', bounds: box, text: { value: 'legacy' } }]);
    expect(result.ok).toBe(true);
    if (result.ok) expect(result.value.warnings.some(w => w.code === 'DOCUMENT_DEFAULTS_INVALID')).toBe(true);
    expect(engine.store.snapshot().document.metadata['editor.defaults.v1']).toBe(raw);
    expect(engine.store.snapshot().document.pages[0]!.elements[0]).toMatchObject({ text: { fontFamily: 'Segoe UI', fontSizePt: 12 } });
  });
});
