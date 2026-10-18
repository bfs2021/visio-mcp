import { expect, it } from 'vitest';
import { createTestEngine } from './support/engine';
import { createEngineHandler } from '../src/bridge/engineHandlers';
import { ok } from '../src/model/result';
import type { RequestEnvelope, Snapshot } from '../src/contracts/generated';

it('discovers component examples and returns isolated previews and revision-tagged editable proposals', async () => {
  const t = createTestEngine(); const before = t.store.snapshot(); let preview: Snapshot | undefined;
  const handle = createEngineHandler(t.engine, { layoutContext: async () => ({ measureWidth: (s, f) => s.length * f.sizePt * .5 }), render: async s => {
    preview = s; return ok({ documentId: s.documentId, sessionId: s.sessionId, revision: s.revision, base64: 'eA==', mimeType: 'image/png', byteLength: 1, widthPx: 100, heightPx: 100, pageId: t.ids.page, mode: 'clean', format: 'png', cropPt: { x: 0, y: 0, width: 100, height: 100 } });
  } });
  const call = (method: string, params: Record<string, unknown>) => handle({ protocolVersion: 1, kind: 'request', requestId: crypto.randomUUID(), ...t.scope(), method, params } as RequestEnvelope);
  const list = await call('components.list', {}); expect(list.error).toBeUndefined(); expect((list.result as any).components).toHaveLength(5);
  for (const templateId of ['repository-card', 'labelled-container', 'environment-columns', 'pipeline', 'swimlane']) {
    const revision = t.engine.revision; const current = t.store.snapshot();
    const proposal = await call('components.get', { pageId: t.ids.page, templateId });
    expect(proposal.error).toBeUndefined(); const value = proposal.result as any;
    expect(value.preview).toBe(true); expect(value.baseRevision).toBe(revision); expect(t.store.snapshot()).toEqual(current); expect(value.parts.title).toBeTruthy();
    expect(preview!.document.pages[0]!.elements.every(e => e.id === value.groupId || Object.values(value.parts).includes(e.id))).toBe(true);
    expect((await t.engine.execute(t.request(value.operations), 'mcp')).ok).toBe(true);
    const reflow = await call('components.get', { pageId: t.ids.page, componentId: value.groupId }); expect(reflow.error).toBeUndefined();
    await t.engine.undo({ ...t.scope(), transactionId: crypto.randomUUID(), baseRevision: t.engine.revision });
  }
  expect(t.store.snapshot().document.pages).toEqual(before.document.pages);
});

it('refuses to guess component text geometry when a font context is unavailable', async () => {
  const t = createTestEngine(); const handle = createEngineHandler(t.engine);
  const r = await handle({ protocolVersion: 1, kind: 'request', requestId: crypto.randomUUID(), ...t.scope(), method: 'components.get', params: { pageId: t.ids.page, templateId: 'repository-card' } } as RequestEnvelope);
  expect(r.error?.code).toBe('not_ready');
});
