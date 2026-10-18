import { describe, expect, it } from 'vitest';
import type { CommittedEvent, ShapeElement } from '../src/contracts/generated';
import { canonicalSerialize } from '../src/model/canonical';
import { restoreRecovery, type RecoveryPayload } from '../src/commands/recovery';
import { createTestEngine } from './support/engine';
import { id, run } from './support/ops';

type T = ReturnType<typeof createTestEngine>;

/** A session as the host would have recorded it: the checkpoint at the start, then every committed event as it was emitted. */
function record(t: T) {
  const events: CommittedEvent[] = [];
  t.engine.onCommitted(e => events.push(JSON.parse(JSON.stringify(e)) as CommittedEvent));
  const checkpoint = JSON.parse(JSON.stringify(t.store.snapshot().document));
  const payload = (over: Partial<RecoveryPayload> = {}): RecoveryPayload => ({
    sessionId: t.sessionId, documentId: t.documentId, checkpointRevision: checkpoint.revision, document: checkpoint, events, lastDurableRevision: events.at(-1)?.revision ?? checkpoint.revision, ...over,
  });
  return { events, checkpoint, payload };
}
const undo = (t: T) => t.engine.undo({ ...t.scope(), transactionId: id(7000 + t.engine.revision), baseRevision: t.engine.revision });
const redo = (t: T) => t.engine.redo({ ...t.scope(), transactionId: id(7500 + t.engine.revision), baseRevision: t.engine.revision });
const current = (t: T) => canonicalSerialize(t.store.snapshot().document);

describe('recovery: rebuilding the last durable state', () => {
  it('UndoReplayExact', async () => {
    const t = createTestEngine();
    const rec = record(t);
    await run(t, [{ op: 'createShape', alias: 'fresh', bounds: { x: 10, y: 10, width: 50, height: 30 } }]);
    await run(t, [{ op: 'move', target: t.ids.shape, delta: { xPt: 7, yPt: 3 } }]);
    await undo(t);
    await redo(t);
    await undo(t); // ends on an undone state: revision counters never rewind, so this is a new revision too
    const r = restoreRecovery(rec.payload());
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    expect(r.value.problem).toBeUndefined();
    expect(r.value.applied).toBe(rec.events.length);
    expect(r.value.revision).toBe(t.engine.revision);
    expect(canonicalSerialize(r.value.document)).toBe(current(t)); // byte for byte, revision included
    expect(rec.events.slice(2).map(e => e.resolvedDiff.source)).toEqual(['undo', 'redo', 'undo']); // undo and redo are journalled as the changes they made
  });

  it('GeneratedIdReplayExact', async () => {
    const t = createTestEngine();
    const rec = record(t);
    const made = await run(t, [{ op: 'createShape', alias: 'fresh', bounds: { x: 10, y: 10, width: 50, height: 30 } }]); // the engine chooses the id
    const originalCreatedUuid = made.created[0]!;
    await run(t, [{ op: 'set', target: originalCreatedUuid, patch: { name: 'Renamed' } }]);
    const r = restoreRecovery(rec.payload());
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    const el = r.value.document.pages[0]!.elements.find(e => e.id === originalCreatedUuid) as ShapeElement; // the same UUID, not a newly generated one
    expect(el).toBeDefined();
    expect(el.name).toBe('Renamed');
    expect(el.alias).toBe('fresh');
    expect(JSON.stringify(rec.events)).not.toContain('"op"'); // records carry resolved changes, never raw commands
    expect(canonicalSerialize(r.value.document)).toBe(current(t));
  });

  it('pictures and their removal come back with the same descriptors', async () => {
    const t = createTestEngine();
    const rec = record(t);
    const asset = { id: 'asset:extra', name: 'Extra', mimeType: 'image/png' as const, sha256: 'c'.repeat(64), tags: ['x'] };
    await run(t, [{ op: 'registerAsset', asset }, { op: 'createImage', id: id(970), bounds: { x: 0, y: 0, width: 10, height: 10 }, assetId: 'asset:extra' }]);
    await run(t, [{ op: 'delete', target: id(970) }, { op: 'removeAsset', assetId: 'asset:extra' }]);
    await undo(t);
    const r = restoreRecovery(rec.payload());
    expect(r.ok && canonicalSerialize(r.value.document)).toBe(current(t));
    expect(r.ok && r.value.document.assets.map(a => a.id)).toEqual(['asset:logo', 'asset:logo-v2', 'asset:extra']);
  });

  it('a record that does not continue the chain stops recovery before it and says why', async () => {
    const t = createTestEngine();
    const rec = record(t);
    for (let i = 0; i < 4; i++) await run(t, [{ op: 'move', target: t.ids.shape, delta: { xPt: 1, yPt: 0 } }]);
    const gap = rec.events.filter(e => e.revision !== 3); // revision 3 never reached disk
    const r = restoreRecovery(rec.payload({ events: gap }));
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    expect(r.value.revision).toBe(2);
    expect(r.value.applied).toBe(2);
    expect(r.value.problem).toMatch(/revision 4 does not follow revision 2/);
    // and what was recovered is exactly the state at revision 2
    const t2 = createTestEngine();
    await run(t2, [{ op: 'move', target: t2.ids.shape, delta: { xPt: 1, yPt: 0 } }]);
    await run(t2, [{ op: 'move', target: t2.ids.shape, delta: { xPt: 1, yPt: 0 } }]);
    expect(canonicalSerialize(r.value.document)).toBe(canonicalSerialize(t2.store.snapshot().document));
  });

  it('a damaged record is refused before anything is published', async () => {
    const t = createTestEngine();
    const rec = record(t);
    await run(t, [{ op: 'move', target: t.ids.shape, delta: { xPt: 1, yPt: 0 } }]);
    await run(t, [{ op: 'move', target: t.ids.shape, delta: { xPt: 1, yPt: 0 } }]);
    const bad = JSON.parse(JSON.stringify(rec.events)) as CommittedEvent[];
    (bad[1]!.resolvedDiff.upserts.elements[0] as unknown as { element: { bounds: { width: number } } }).element.bounds.width = -5; // a record that would make an invalid document
    const r = restoreRecovery(rec.payload({ events: bad }));
    expect(r.ok && r.value.revision).toBe(1);
    expect(r.ok && r.value.problem).toMatch(/invalid document/);
    const nonsense = restoreRecovery(rec.payload({ events: [{ nonsense: true }] }));
    expect(nonsense.ok && nonsense.value.applied).toBe(0);
    expect(nonsense.ok && nonsense.value.problem).toMatch(/no resolved diff/);
  });

  it('a record from another document is not applied, and a broken checkpoint is a hard refusal', async () => {
    const t = createTestEngine();
    const rec = record(t);
    await run(t, [{ op: 'move', target: t.ids.shape, delta: { xPt: 1, yPt: 0 } }]);
    const foreign = JSON.parse(JSON.stringify(rec.events)) as CommittedEvent[];
    foreign[0]!.documentId = id(99);
    const r = restoreRecovery(rec.payload({ events: foreign }));
    expect(r.ok && r.value.applied).toBe(0);
    expect(r.ok && r.value.problem).toMatch(/different document/);
    const broken = restoreRecovery(rec.payload({ document: { schemaVersion: 1, pages: 'nope' } }));
    expect(broken.ok).toBe(false);
    expect(!broken.ok && broken.error.message).toMatch(/not a valid document/);
    const other = restoreRecovery(rec.payload({ documentId: id(98) }));
    expect(!other.ok && other.error.code).toBe('document_mismatch');
  });

  it('retained blob hashes include what undo and redo could bring back', async () => {
    const t = createTestEngine();
    const asset = { id: 'asset:extra', name: 'Extra', mimeType: 'image/png' as const, sha256: 'd'.repeat(64), tags: [] as string[] };
    await run(t, [{ op: 'registerAsset', asset }]);
    await run(t, [{ op: 'removeAsset', assetId: 'asset:extra' }]);
    expect(t.store.snapshot().document.assets.some(a => a.id === 'asset:extra')).toBe(false); // gone from the document...
    expect(t.engine.retainedBlobHashes()).toContain('d'.repeat(64)); // ...but undo could restore it, so its bytes stay
    expect(t.engine.retainedBlobHashes()).toEqual(expect.arrayContaining(['a'.repeat(64), 'b'.repeat(64)]));
  });
});
