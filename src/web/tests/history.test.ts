import { describe, expect, it } from 'vitest';
import type { CommittedEvent, Operation, Projection } from '../src/contracts/generated';
import { CommandEngine } from '../src/commands/CommandEngine';
import { canonicalSerialize } from '../src/model/canonical';
import { ok } from '../src/model/result';
import { createTestEngine, element, exactElementHashes, sequenceIds } from './support/engine';

const U = (engine: { scope(): object }, n: number) => ({ ...engine.scope(), transactionId: `00000000-0000-4000-8000-00000000${String(n).padStart(4, '0')}` });
const undoReq = (t: ReturnType<typeof createTestEngine>, n: number) => ({ ...U(t.engine, n), baseRevision: t.engine.revision }) as never;

describe('undo, redo and history', () => {
  it('undo_restores_exact_element_serialization_with_a_higher_revision', async () => {
    const t = createTestEngine();
    const { engine, request, ids } = t;
    const originalElementJson = canonicalSerialize(element(engine.store.snapshot(), ids.shape));
    const allBefore = exactElementHashes(engine.store.snapshot());
    const afterEdit = await engine.execute(request([
      { op: 'move', target: ids.shape, delta: { xPt: 12.5, yPt: -3 } },
      { op: 'set', target: ids.shape, patch: { name: 'Changed', style: { strokeWidthPt: 4 } } },
      { op: 'rotate', target: ids.shape, angleDeg: 33 },
    ]), 'gui');
    expect(afterEdit.ok).toBe(true);
    const undone = await engine.undo(undoReq(t, 1));
    expect(undone.ok).toBe(true);
    const restoredElementJson = canonicalSerialize(element(engine.store.snapshot(), ids.shape));
    expect(restoredElementJson).toBe(originalElementJson);
    expect(exactElementHashes(engine.store.snapshot())).toEqual(allBefore);
    expect(undone.ok && undone.value.revision).toBe((afterEdit.ok ? afterEdit.value.revision : 0) + 1); // revisions never rewind
    expect(undone.ok && undone.value.changed).toEqual([ids.shape]);
  });

  it('undo_of_create_and_delete_restores_array_order_and_ids', async () => {
    const t = createTestEngine();
    const { engine, request, ids } = t;
    const order = () => engine.store.snapshot().document.pages[0]!.elements.map(e => e.id);
    const original = order();
    await engine.execute(request([{ op: 'delete', target: ids.shapes[3]! }, { op: 'delete', target: ids.shapes[7]! }, { op: 'createShape', alias: 'n', bounds: { x: 0, y: 0, width: 5, height: 5 } }]), 'mcp');
    expect(order()).toHaveLength(original.length - 1);
    const hashesAfterEdit = exactElementHashes(engine.store.snapshot());
    expect((await engine.undo(undoReq(t, 1))).ok).toBe(true);
    expect(order()).toEqual(original);
    expect((await engine.redo(undoReq(t, 2))).ok).toBe(true);
    expect(exactElementHashes(engine.store.snapshot())).toEqual(hashesAfterEdit); // redo replays exactly, including the created UUID
  });

  it('redo_is_cleared_by_a_new_edit', async () => {
    const t = createTestEngine();
    const { engine, request, ids } = t;
    await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 1, yPt: 0 } }]), 'gui');
    await engine.undo(undoReq(t, 1));
    await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 2, yPt: 0 } }]), 'gui');
    const redo = await engine.redo(undoReq(t, 2));
    expect(!redo.ok && redo.error.code).toBe('history_unavailable');
  });

  it('undo_and_redo_are_idempotent_by_transaction_id', async () => {
    const t = createTestEngine();
    const { engine, request, ids } = t;
    await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 1, yPt: 0 } }]), 'gui');
    const req = undoReq(t, 1);
    const first = await engine.undo(req);
    const again = await engine.undo(req);
    expect(again).toEqual(first);
    expect(engine.revision).toBe(2);
    const sameIdAsRedo = await engine.redo(req);
    expect(!sameIdAsRedo.ok && sameIdAsRedo.error.code).toBe('transaction_id_conflict');
  });

  it('undo_with_a_stale_base_revision_conflicts', async () => {
    const t = createTestEngine();
    const { engine, request, ids } = t;
    await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 1, yPt: 0 } }]), 'gui');
    const r = await engine.undo({ ...U(engine, 1), baseRevision: 0 } as never);
    expect(!r.ok && r.error.code).toBe('revision_conflict');
  });

  it('retention_eviction_returns_history_unavailable', async () => {
    const seed = createTestEngine();
    const engine = new CommandEngine({ document: seed.store.snapshot().document, retention: { history: 3 }, idFactory: sequenceIds('2') });
    const ids = seed.ids;
    const tx = sequenceIds('8');
    const run = (dx: number) => engine.execute({ ...engine.scope(), transactionId: tx(), baseRevision: engine.revision, atomic: true, operations: [{ op: 'move', target: ids.shape, delta: { xPt: dx, yPt: 0 } }] }, 'gui');
    for (let i = 1; i <= 5; i++) await run(i);
    const old = await engine.getChanges(engine.scope(), 0);
    expect(!old.ok && old.error.code).toBe('history_unavailable');
    if (!old.ok) expect(old.error.details).toMatchObject({ earliestRetainedRevision: 2 });
    const recent = await engine.getChanges(engine.scope(), 2);
    expect(recent.ok && recent.value.transactions.map(x => x.revision)).toEqual([3, 4, 5]);

    // Undo works for what is retained, then reports that older history is gone.
    for (let i = 0; i < 3; i++) expect((await engine.undo({ ...engine.scope(), transactionId: tx(), baseRevision: engine.revision })).ok).toBe(true);
    const gone = await engine.undo({ ...engine.scope(), transactionId: tx(), baseRevision: engine.revision });
    expect(!gone.ok && gone.error.code).toBe('history_unavailable');
    if (!gone.ok) expect(gone.error.details).toMatchObject({ reason: 'evicted' });
  });

  it('get_changes_lists_transactions_with_source_and_ids_from_the_first_commit', async () => {
    const t = createTestEngine();
    const { engine, request, ids } = t;
    await engine.execute(request([{ op: 'createShape', alias: 'n', bounds: { x: 0, y: 0, width: 5, height: 5 } }]), 'mcp');
    await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 1, yPt: 0 } }]), 'gui');
    const changes = await engine.getChanges(engine.scope(), 0);
    expect(changes.ok).toBe(true);
    if (!changes.ok) return;
    expect(changes.value.transactions.map(x => [x.source, x.revision, x.description])).toEqual([['mcp', 1, 'createShape'], ['gui', 2, 'move']]);
    expect(changes.value.transactions[0]!.created).toHaveLength(1);
    expect(changes.value.transactions[1]!.changed).toEqual([ids.shape]);
  });
});

describe('barriers and lifecycle', () => {
  it('snapshot_waits_for_queued_mutations_and_checks_scope', async () => {
    const t = createTestEngine();
    const { engine, request, ids } = t;
    const mutation = engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 10, yPt: 0 } }]), 'gui');
    const snap = await engine.snapshot(engine.scope()); // queued behind the mutation: sees its result
    await mutation;
    expect(snap.ok && snap.value.revision).toBe(1);
    const wrong = await engine.snapshot({ ...engine.scope(), sessionId: '00000000-0000-4000-8000-0000000000ff' });
    expect(!wrong.ok && wrong.error.code).toBe('session_mismatch');
  });

  it('markSaved_for_an_old_session_fails', async () => {
    const t = createTestEngine();
    const { engine } = t;
    const old = engine.scope();
    const snap = engine.store.snapshot();
    await engine.replaceDocument(snap.document, { ...old, baseRevision: 0 });
    const markSavedForOldSession = await engine.markSaved(old, 0, 'c:/x.json');
    expect(markSavedForOldSession.ok).toBe(false);
    expect(!markSavedForOldSession.ok && markSavedForOldSession.error.code).toBe('session_mismatch');
  });

  it('saving_revision_R_does_not_clean_R_plus_1', async () => {
    const t = createTestEngine();
    const { engine, request, ids } = t;
    await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 1, yPt: 0 } }]), 'gui');
    const savedRevision = engine.revision; // the host serialised this revision
    await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 1, yPt: 0 } }]), 'gui'); // edited while writing
    expect((await engine.markSaved(engine.scope(), savedRevision, 'c:/x.json')).ok).toBe(true);
    expect(engine.status()).toMatchObject({ savedRevision: 1, dirty: true });
    expect((await engine.markSaved(engine.scope(), engine.revision, 'c:/x.json')).ok).toBe(true);
    expect(engine.status().dirty).toBe(false);
    expect((await engine.markSaved(engine.scope(), 99, 'c:/x.json')).ok).toBe(false);
  });

  it('replaceDocument_checks_the_expected_revision_so_late_imports_cannot_discard_edits', async () => {
    const t = createTestEngine();
    const { engine, request, ids } = t;
    const importStartedAt = engine.revision; // import begins at revision 0
    const imported = structuredClone(engine.store.snapshot().document);
    await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 5, yPt: 0 } }]), 'gui'); // user keeps editing during IO
    const late = await engine.replaceDocument(imported, { ...engine.scope(), baseRevision: importStartedAt });
    expect(!late.ok && late.error.code).toBe('revision_conflict');
    expect((element(engine.store.snapshot(), ids.shape) as { bounds: { x: number } }).bounds.x).toBe(105); // edit survived
  });

  it('replaceDocument_rejects_an_invalid_document_and_resets_history_and_cache_when_valid', async () => {
    const t = createTestEngine();
    const { engine, request, ids } = t;
    const bad = structuredClone(engine.store.snapshot().document) as { pages: unknown[] };
    bad.pages = [];
    const rejected = await engine.replaceDocument(bad as never, { ...engine.scope(), baseRevision: 0 });
    expect(!rejected.ok && rejected.error.code).toBe('invalid_request');
    const req = request([{ op: 'move', target: ids.shape, delta: { xPt: 1, yPt: 0 } }]);
    await engine.execute(req, 'gui');
    const good = await engine.replaceDocument(engine.store.snapshot().document, { ...engine.scope(), baseRevision: 1 });
    expect(good.ok).toBe(true);
    expect((await engine.getChanges(engine.scope(), 1)).ok && true).toBe(true);
    const retryOldSession = await engine.execute(req, 'gui');
    expect(!retryOldSession.ok && retryOldSession.error.code).toBe('session_mismatch');
  });

  it('exportSnapshot_projects_the_committed_snapshot_and_checks_the_projection_scope', async () => {
    const t = createTestEngine();
    const { engine, request, ids } = t;
    const projection = (revision: number, sessionId = engine.sessionId): Projection => ({ documentId: engine.documentId, sessionId, revision, connectors: {}, groupVisualBounds: {} });
    const none = await engine.exportSnapshot(engine.scope());
    expect(!none.ok && none.error.code).toBe('projection_failed');
    engine.setSnapshotProjector(s => ok(projection(s.revision)));
    await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 1, yPt: 0 } }]), 'gui');
    const exp = await engine.exportSnapshot(engine.scope());
    expect(exp.ok && exp.value.projection.revision).toBe(1);
    expect(exp.ok && exp.value.revision).toBe(1);
    engine.setSnapshotProjector(s => ok(projection(s.revision + 1)));
    const mismatch = await engine.exportSnapshot(engine.scope());
    expect(!mismatch.ok && mismatch.error.code).toBe('projection_failed');
  });
});

describe('committed events', () => {
  it('arrive_in_commit_order_with_session_revision_and_resolved_ids', async () => {
    const t = createTestEngine();
    const { engine, request, ids } = t;
    const events: CommittedEvent[] = [];
    const off = engine.onCommitted(e => events.push(e));
    await engine.execute(request([{ op: 'createShape', alias: 'n', bounds: { x: 0, y: 0, width: 5, height: 5 } }]), 'mcp');
    await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 1, yPt: 0 } }]), 'gui');
    await engine.undo(undoReq(t, 1));
    off();
    await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 1, yPt: 0 } }]), 'gui');
    expect(events.map(e => [e.sequence, e.revision, e.resolvedDiff.source])).toEqual([[1, 1, 'mcp'], [2, 2, 'gui'], [3, 3, 'undo']]);
    expect(events.every(e => e.sessionId === engine.sessionId && e.documentId === engine.documentId)).toBe(true);
    expect(events[0]!.resolvedDiff.upserts.elements).toHaveLength(1);
    expect(events[0]!.resolvedDiff.upserts.elements[0]!.element.id).toMatch(/^00000000-0000-4000-8000-1/); // resolved UUID, not the alias
    expect(events[1]!.resolvedDiff.upserts.elements.map(u => u.element.id)).toEqual([ids.shape]);
    expect(events[2]!.resolvedDiff.previousRevision).toBe(2);
  });

  it('are_delivered_even_when_the_projector_fails', async () => {
    const t = createTestEngine();
    const { engine, request, ids } = t;
    const events: CommittedEvent[] = [];
    engine.onCommitted(e => events.push(e));
    engine.setProjector(() => ({ ok: false, error: { code: 'projection_failed', message: 'x', retryable: false } }));
    const r = await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 1, yPt: 0 } }]), 'gui');
    expect(r.ok && r.value.projectionStatus).toBe('failed');
    expect(events).toHaveLength(1);
  });

  it('a_failing_durability_listener_cannot_roll_back_state_and_requests_a_checkpoint', async () => {
    const t = createTestEngine();
    const { engine, request, ids } = t;
    const seen: number[] = [];
    engine.onCommitted(() => { throw new Error('disk full'); });
    engine.onCommitted(e => seen.push(e.revision)); // later listeners still run
    const r = await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 1, yPt: 0 } }]), 'gui');
    expect(r.ok).toBe(true);
    expect(engine.revision).toBe(1);
    expect(seen).toEqual([1]);
    expect(engine.status()).toMatchObject({ recoveryDegraded: true, checkpointRequested: true });
  });
});

describe('structural guarantees', () => {
  it('committed_snapshots_are_deeply_frozen', async () => {
    const t = createTestEngine();
    const snap = t.engine.store.snapshot();
    expect(() => { (snap.document.pages[0]!.elements[0]!.bounds as { x: number }).x = 999; }).toThrow();
    expect(Object.isFrozen(snap.document)).toBe(true);
  });

  it('replaying_a_forward_diff_reproduces_the_committed_document_exactly', async () => {
    const t = createTestEngine();
    const { engine, request, ids } = t;
    const ops: Operation[] = [
      { op: 'createShape', alias: 'a', bounds: { x: 1, y: 2, width: 30, height: 40 } },
      { op: 'delete', target: ids.shapes[5]! },
      { op: 'rotate', target: ids.shape, angleDeg: 12 },
    ];
    const events: CommittedEvent[] = [];
    engine.onCommitted(e => events.push(e));
    const before = structuredClone(engine.store.snapshot().document);
    await engine.execute(request(ops), 'mcp');
    const { applyDiff } = await import('../src/commands/diff');
    const replayed = applyDiff(before, events[0]!.resolvedDiff, 1);
    expect(canonicalSerialize(replayed)).toBe(canonicalSerialize(engine.store.snapshot().document));
  });
});
