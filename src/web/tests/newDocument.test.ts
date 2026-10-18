import { describe, expect, it } from 'vitest';
import { newDocument } from '../src/model/newDocument';
import { CommandEngine } from '../src/commands/CommandEngine';
import { NewDocumentFlow } from '../src/editor/NewDocumentFlow';
import { fail } from '../src/model/result';

const size = { widthPt: 612, heightPt: 792 };
const setup = () => { const engine = new CommandEngine({ document: newDocument() }); return { engine, flow: new NewDocumentFlow(engine) }; };
const edit = async (engine: CommandEngine) => engine.execute({ ...engine.scope(), baseRevision: engine.revision, transactionId: crypto.randomUUID(), atomic: true, operations: [{ op: 'createShape', bounds: { x: 20, y: 20, width: 80, height: 40 } }] }, 'gui');

describe('explicit New document', () => {
  it('creates an empty independent document with validated custom dimensions', () => {
    const a = newDocument(undefined, size), b = newDocument(undefined, size);
    expect(a.pages[0]).toMatchObject({ ...size, elements: [] }); expect(a.id).not.toBe(b.id);
    for (const widthPt of [0, -1, NaN, Infinity, 14401]) expect(() => newDocument(undefined, { ...size, widthPt })).toThrow();
  });
  it('cancel preserves the exact document, identity and history', async () => {
    const { engine, flow } = setup(); await edit(engine); const before = engine.store.snapshot();
    flow.begin(); await flow.choose(size); expect(flow.prompt?.stage).toBe('confirm'); flow.cancel();
    expect(engine.store.snapshot()).toEqual(before); await engine.undo({ ...engine.scope(), baseRevision: engine.revision, transactionId: crypto.randomUUID() }); expect(engine.store.snapshot().document.pages[0]!.elements).toHaveLength(0);
  });
  it('discard creates a new unsaved identity and empty history; old identities cannot edit it', async () => {
    const { engine, flow } = setup(); await edit(engine); const old = engine.scope();
    flow.begin(); await flow.choose(size); await flow.discard();
    expect(flow.prompt).toBeNull(); expect(engine.scope()).not.toEqual(old); expect(engine.status()).toMatchObject({ unsaved: true, savedPath: null });
    await engine.undo({ ...engine.scope(), baseRevision: engine.revision, transactionId: crypto.randomUUID() }); expect(engine.store.snapshot().document.pages[0]).toMatchObject({ ...size, elements: [] });
    expect((await engine.undo({ ...old, baseRevision: 0, transactionId: crypto.randomUUID() })).ok).toBe(false);
  });
  it('failed or cancelled save retains the original; successful save can continue', async () => {
    const { engine, flow } = setup(); await edit(engine); const old = engine.scope();
    flow.save = async () => {}; flow.begin(); await flow.choose(size); await flow.saveAndCreate();
    expect(engine.scope()).toEqual(old); expect(flow.prompt?.error).toMatch(/not saved/i);
    flow.save = async () => { await engine.markSaved(engine.scope(), engine.revision, 'C:/plan.diagram.json'); };
    await flow.saveAndCreate(); expect(engine.scope()).not.toEqual(old); expect(engine.status().savedPath).toBeNull();
  });
  it('does not discard edits that arrive while a prompt is open or a save is pending', async () => {
    const { engine, flow } = setup(); await edit(engine); flow.begin(); await flow.choose(size); await edit(engine);
    await flow.discard(); expect(flow.prompt?.error).toMatch(/changed/i); expect(engine.store.snapshot().document.pages[0]!.elements).toHaveLength(2);
    flow.cancel(); flow.begin(); await flow.choose(size);
    flow.save = async () => { await edit(engine); await engine.markSaved(engine.scope(), engine.revision, 'C:/plan.diagram.json'); };
    await flow.saveAndCreate(); expect(flow.prompt?.error).toMatch(/changed/i); expect(engine.store.snapshot().document.pages[0]!.elements).toHaveLength(3);
  });
  it('guards gestures and repeated requests and surfaces creation failures without losing work', async () => {
    const { engine, flow } = setup(); flow.canStart = () => false; flow.begin(); expect(flow.prompt).toBeNull();
    flow.canStart = () => true; flow.begin(); const first = flow.prompt; flow.begin(); expect(flow.prompt).toBe(first);
    flow.create = async () => fail('io_error', 'Creation failed'); const before = engine.store.snapshot();
    await flow.choose(size); expect(flow.prompt?.error).toBe('Creation failed'); expect(engine.store.snapshot()).toEqual(before);
  });
});

