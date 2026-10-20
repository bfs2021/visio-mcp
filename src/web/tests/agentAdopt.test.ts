import { describe, expect, it } from 'vitest';
import { startAgentApp } from '../src/bridge/agentApp';
import { newDocument } from '../src/model/newDocument';

const envelope = (method: string, params: object) => ({ protocolVersion: 1, kind: 'request' as const, requestId: 'r1', method, params } as any);

describe('agent view: adopting an imported document', () => {
  it('opens it in the background under its own id with a fresh revision history', async () => {
    const { workspace, router } = startAgentApp(undefined);
    const doc = { ...newDocument(), id: '00000000-0000-4000-8000-0000000000a1', title: 'Imported reference' };
    const r = await router.dispatch(envelope('app.adoptDocument', { document: doc, activate: false }));
    expect(r.error).toBeUndefined();
    const result = r.result as { documentId: string; sessionId: string; revision: number };
    expect(result.documentId).toBe(doc.id);
    expect(result.sessionId).toBeTruthy();
    expect(workspace.engineOf(doc.id)?.scope().documentId).toBe(doc.id);
    expect(workspace.close(doc.id)).toBe(true); // the host can still drop it
  });

  it('refuses a document that does not satisfy the contract', async () => {
    const { workspace, router } = startAgentApp(undefined);
    const r = await router.dispatch(envelope('app.adoptDocument', { document: { id: 'nope' }, activate: false }));
    expect(r.error?.code).toBe('invalid_request');
    expect(workspace.engineOf('nope')).toBeFalsy();
  });
});
