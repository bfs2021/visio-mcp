import { expect, it } from 'vitest';
import { createEngineHandler } from '../src/bridge/engineHandlers';
import { createTestEngine } from './support/engine';
import type { RequestEnvelope } from '../src/contracts/generated';

it('text layout requires a real measurement context and validates session before measuring', async () => {
  const t = createTestEngine();
  const env: RequestEnvelope = { protocolVersion: 1, kind: 'request', requestId: 'measure', method: 'doc.textLayout', documentId: t.documentId, sessionId: t.sessionId, params: { ids: [t.ids.shapes[0]!] } };
  const missing = await createEngineHandler(t.engine)(env);
  expect(missing.error?.code).toBe('not_ready');
  let measured = 0;
  const handler = createEngineHandler(t.engine, { layoutContext: async () => { measured++; return { measureWidth: (text, f) => text.length * f.sizePt }; } });
  const wrong = await handler({ ...env, sessionId: '00000000-0000-4000-8000-000000999999' });
  expect(wrong.error?.code).toBe('session_mismatch');
  expect(measured).toBe(0);
  const valid = await handler(env);
  expect(valid.error).toBeUndefined();
  expect(valid.result).toMatchObject({ revision: 0, documentId: t.documentId, sessionId: t.sessionId, items: [{ id: t.ids.shapes[0] }] });
  expect(t.store.snapshot().revision).toBe(0);
});
