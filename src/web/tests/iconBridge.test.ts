import { expect, it } from 'vitest';
import { createTestEngine } from './support/engine';
import { createEngineHandler } from '../src/bridge/engineHandlers';
import { ok } from '../src/model/result';
import type { RequestEnvelope, Snapshot } from '../src/contracts/generated';

it('returns isolated icon previews and placement proposals without changing the document', async () => {
  const t = createTestEngine(); const before = structuredClone(t.store.snapshot());
  let preview: Snapshot | undefined;
  const handler = createEngineHandler(t.engine, { render: async (s) => {
    preview = s;
    return ok({ documentId: s.documentId, sessionId: s.sessionId, revision: s.revision, base64: 'eA==', mimeType: 'image/png', byteLength: 1, widthPx: 100, heightPx: 100, pageId: s.document.pages[0]!.id, mode: 'clean', format: 'png', cropPt: { x: 0, y: 0, width: 100, height: 100 } });
  } });
  const call = (method: string, params: Record<string, unknown>) => handler({ protocolVersion: 1, kind: 'request', requestId: crypto.randomUUID(), ...t.scope(), method, params } as RequestEnvelope);
  const result = await call('icons.search', { query: 'docker' });
  expect(result.error).toBeUndefined();
  expect((result.result as any).icons[0].id).toBe('lucide:container');
  expect((result.result as any).preview).toBe(true);
  expect(preview!.document.pages[0]!.elements[0]!.kind).toBe('shape');
  const detail = await call('icons.get', { pageId: t.ids.page, iconId: 'lucide:git-branch', x: 60, y: 70, sizePt: 24 });
  expect(detail.error).toBeUndefined();
  expect((detail.result as any).operations).toHaveLength(2);
  expect((await call('icons.get', { pageId: t.ids.page, iconId: 'missing' })).error?.code).toBe('not_found');
  expect((await call('icons.get', { pageId: t.ids.page, iconId: 'lucide:container', sizePt: -1 })).error?.code).toBe('invalid_request');
  expect(t.store.snapshot()).toEqual(before);
});
