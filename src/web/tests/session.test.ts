import { describe, expect, it } from 'vitest';
import type { Operation, ScriptRequest } from '../src/contracts/generated';
import type { ScriptCompiler } from '../src/commands/CommandEngine';
import { ok } from '../src/model/result';
import { createTestEngine, element, exactElementHashes } from './support/engine';

const setName = (target: string, name: string): Operation => ({ op: 'set', target, patch: { name } });

describe('session, cache and revision ordering', () => {
  it('missing_identity_rejects', async () => {
    const { engine, request, ids } = createTestEngine();
    const req = request([setName(ids.shape, 'x')]);
    const { sessionId: _s, ...noSession } = req;
    const { documentId: _d, ...noDocument } = req;
    for (const bad of [noSession, noDocument]) {
      const r = await engine.execute(bad as never, 'mcp');
      expect(!r.ok && r.error.code).toBe('invalid_request');
    }
    expect(engine.revision).toBe(0);
  });

  it('wrong_document_and_wrong_session_are_distinguished', async () => {
    const { engine, request, ids } = createTestEngine();
    const wrongDoc = await engine.execute(request([setName(ids.shape, 'x')], { documentId: '00000000-0000-4000-8000-0000000000ff' }), 'mcp');
    expect(!wrongDoc.ok && wrongDoc.error.code).toBe('document_mismatch');
    const wrongSession = await engine.execute(request([setName(ids.shape, 'x')], { sessionId: '00000000-0000-4000-8000-0000000000ff' }), 'mcp');
    expect(!wrongSession.ok && wrongSession.error.code).toBe('session_mismatch');
  });

  it('cached_retry_after_commit', async () => {
    const { engine, request, ids } = createTestEngine();
    const req = request([{ op: 'set', target: ids.shape, patch: { name: 'New name' } }]);
    const original = await engine.execute(req, 'mcp');
    const retry = await engine.execute(req, 'mcp');
    expect(original.ok).toBe(true);
    expect(retry).toEqual(original); // baseRevision is stale, but the cache wins
    expect(engine.revision).toBe(1);
  });

  it('same_id_changed_payload_conflicts', async () => {
    const { engine, request, ids } = createTestEngine();
    const req = request([setName(ids.shape, 'A')]);
    await engine.execute(req, 'mcp');
    const changed = await engine.execute({ ...req, operations: [setName(ids.shape, 'B')] }, 'mcp');
    expect(!changed.ok && changed.error.code).toBe('transaction_id_conflict');
    expect((element(engine.store.snapshot(), ids.shape) as { name?: string }).name).toBe('A');
  });

  it('same_id_different_baseRevision_conflicts', async () => {
    const { engine, request, ids } = createTestEngine();
    const req = request([setName(ids.shape, 'A')]);
    await engine.execute(req, 'mcp');
    const r = await engine.execute({ ...req, baseRevision: 1 }, 'mcp');
    expect(!r.ok && r.error.code).toBe('transaction_id_conflict');
  });

  it('queued_duplicate_commits_once', async () => {
    const { engine, request, ids } = createTestEngine();
    const req = request([{ op: 'move', target: ids.shape, delta: { xPt: 10, yPt: 0 } }]);
    const [a, b, c] = await Promise.all([engine.execute(req, 'mcp'), engine.execute(req, 'mcp'), engine.execute(req, 'script')]);
    expect(engine.revision).toBe(1);
    expect(b).toEqual(a);
    expect(c).toEqual(a); // transport source is not part of the identity
    expect((element(engine.store.snapshot(), ids.shape) as { bounds: { x: number } }).bounds.x).toBe(110); // moved once
  });

  it('transport_fields_are_not_part_of_the_request_hash', async () => {
    const { engine, request, ids } = createTestEngine();
    const req = request([setName(ids.shape, 'A')]);
    const first = await engine.execute(req, 'gui');
    const again = await engine.execute({ ...req }, 'mcp');
    expect(again).toEqual(first);
  });

  it('apply_vs_script_same_id_conflicts', async () => {
    const { engine, request, ids } = createTestEngine();
    const req = request([setName(ids.shape, 'A')]);
    await engine.execute(req, 'mcp');
    const compile: ScriptCompiler = () => ok({ operations: [setName(ids.shape, 'A')] });
    const script: ScriptRequest = { documentId: req.documentId, sessionId: req.sessionId, transactionId: req.transactionId, baseRevision: 0, script: 'set a', atomic: true };
    const r = await engine.executeScript(script, compile);
    expect(!r.ok && r.error.code).toBe('transaction_id_conflict');
  });

  it('script_retry_hits_the_cache_before_compiling_again', async () => {
    const { engine, request, ids } = createTestEngine();
    let compiles = 0;
    const compile: ScriptCompiler = () => { compiles++; return ok({ operations: [{ op: 'createShape', bounds: { x: 0, y: 0, width: 10, height: 10 } }, setName(ids.shape, 'S')] as Operation[] }); };
    const base = request([]);
    const script: ScriptRequest = { documentId: base.documentId, sessionId: base.sessionId, transactionId: base.transactionId, baseRevision: 0, script: 'rect', atomic: true };
    const first = await engine.executeScript(script, compile);
    const retry = await engine.executeScript(script, compile);
    expect(first.ok && first.value.created).toHaveLength(1);
    expect(retry).toEqual(first); // identical created UUIDs: no second allocation
    expect(compiles).toBe(1);
    expect(engine.store.snapshot().document.pages[0]!.elements).toHaveLength(21);
  });

  it('reopen_same_revision_rejects_old_session', async () => {
    const { engine, request, ids } = createTestEngine();
    const oldScope = engine.scope();
    const stale = request([setName(ids.shape, 'old')]);
    const snap = engine.store.snapshot();
    const replaced = await engine.replaceDocument(snap.document, { ...oldScope, baseRevision: snap.revision });
    expect(replaced.ok).toBe(true);
    expect(engine.sessionId).not.toBe(oldScope.sessionId); // engine mints the new session
    expect(engine.revision).toBe(snap.revision); // same revision, different session
    const r = await engine.execute(stale, 'mcp');
    expect(!r.ok && r.error.code).toBe('session_mismatch');
    const fresh = await engine.execute({ ...stale, sessionId: engine.sessionId, transactionId: '00000000-0000-4000-8000-0000000fffff' }, 'mcp');
    expect(fresh.ok).toBe(true);
  });

  it('uncached_stale_revision_preserves_human_edit', async () => {
    const { engine, request, ids } = createTestEngine();
    const agentRead = engine.revision; // the agent reads revision 0
    await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: 40, yPt: 0 } }]), 'gui'); // human edits meanwhile
    const before = exactElementHashes(engine.store.snapshot());
    const r = await engine.execute(request([{ op: 'move', target: ids.shape, delta: { xPt: -40, yPt: 0 } }], { baseRevision: agentRead }), 'mcp');
    expect(r.ok).toBe(false);
    if (!r.ok) {
      expect(r.error.code).toBe('revision_conflict');
      expect(r.error.details).toMatchObject({ expected: 0, actual: 1 });
      const changes = r.error.details!.changesSinceExpected as Array<{ source: string; changed: string[] }>;
      expect(changes).toHaveLength(1);
      expect(changes[0]).toMatchObject({ source: 'gui', changed: [ids.shape] });
    }
    expect(exactElementHashes(engine.store.snapshot())).toEqual(before);
  });

  it('cache_is_checked_before_the_revision_but_after_the_scope', async () => {
    const { engine, request, ids } = createTestEngine();
    const req = request([setName(ids.shape, 'A')]);
    await engine.execute(req, 'mcp');
    const wrongSession = await engine.execute({ ...req, sessionId: '00000000-0000-4000-8000-0000000000ff' }, 'mcp');
    expect(!wrongSession.ok && wrongSession.error.code).toBe('session_mismatch'); // never answered from another session's cache
  });

  it('invalid_requests_are_not_cached_so_a_corrected_retry_can_reuse_the_id', async () => {
    const { engine, request, ids } = createTestEngine();
    const bad = request([{ op: 'set', target: ids.image, patch: { assetId: 'asset:missing' } }]);
    expect((await engine.execute(bad, 'mcp')).ok).toBe(false);
    const good = await engine.execute({ ...bad, operations: [{ op: 'set', target: ids.image, patch: { assetId: 'asset:logo-v2' } }] }, 'mcp');
    expect(good.ok).toBe(true);
  });

  it('result_cache_is_bounded_by_count', async () => {
    const { engine, request, ids } = createTestEngine();
    const small = new (engine.constructor as typeof import('../src/commands/CommandEngine').CommandEngine)({
      document: engine.store.snapshot().document, retention: { results: 3 },
    });
    const req = (n: number) => ({ documentId: small.documentId, sessionId: small.sessionId, transactionId: `00000000-0000-4000-8000-00000000${String(n).padStart(4, '0')}`, baseRevision: small.revision, atomic: true as const, operations: [setName(ids.shape, `n${n}`)] });
    const first = req(1);
    await small.execute(first, 'mcp');
    for (let i = 2; i <= 6; i++) await small.execute(req(i), 'mcp');
    // The first entry was evicted: replaying it is now just a stale request, not a cache hit.
    const replay = await small.execute(first, 'mcp');
    expect(!replay.ok && replay.error.code).toBe('revision_conflict');
    void request;
  });

  it('projection_failure_keeps_the_commit_and_retry_does_not_recommit', async () => {
    const { engine, request, ids } = createTestEngine();
    let calls = 0;
    engine.setProjector(() => { calls++; return { ok: false, error: { code: 'projection_failed', message: 'canvas exploded', retryable: false } }; });
    const req = request([setName(ids.shape, 'kept')]);
    const r = await engine.execute(req, 'gui');
    expect(r.ok && r.value.projectionStatus).toBe('failed');
    expect(engine.revision).toBe(1);
    expect((element(engine.store.snapshot(), ids.shape) as { name?: string }).name).toBe('kept');
    expect(engine.status().projectionHealthy).toBe(false);
    const retry = await engine.execute(req, 'gui');
    expect(retry).toEqual(r);
    expect(engine.revision).toBe(1);
    expect(calls).toBe(1);
  });

  it('a_throwing_projector_is_a_failed_projection_not_a_failed_commit', async () => {
    const { engine, request, ids } = createTestEngine();
    engine.setProjector(() => { throw new Error('boom'); });
    const r = await engine.execute(request([setName(ids.shape, 'kept')]), 'gui');
    expect(r.ok && r.value.projectionStatus).toBe('failed');
    expect(engine.revision).toBe(1);
  });
});
