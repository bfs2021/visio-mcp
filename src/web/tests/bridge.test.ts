import { describe, expect, it, vi } from 'vitest';
import type { RequestEnvelope, ResponseEnvelope, Snapshot } from '../src/contracts/generated';
import { BridgeClient, type WebViewLike } from '../src/bridge/BridgeClient';
import { startApp } from '../src/bridge/bootstrap';

/** Two ends of the WebView2 channel: what the page posts goes to `toHost`; `fromHost` delivers to the page. */
function fakeWebView() {
  const toHost: any[] = [];
  const listeners: Array<(e: { data: unknown }) => void> = [];
  const wv: WebViewLike = { postMessage: m => { toHost.push(JSON.parse(JSON.stringify(m))); }, addEventListener: (_t, l) => { listeners.push(l); } };
  const fromHost = (m: unknown) => listeners.forEach(l => l({ data: JSON.parse(JSON.stringify(m)) }));
  return { wv, toHost, fromHost };
}

const flush = () => new Promise(r => setTimeout(r, 0));
const U = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const req = (method: string, scope: { documentId: string; sessionId: string }, params: object = {}, requestId = 'r1'): RequestEnvelope =>
  ({ protocolVersion: 1, kind: 'request', requestId, method, ...scope, params } as RequestEnvelope);

describe('BridgeClient', () => {
  it('keeps an editor export pending while the native dialog exceeds the ordinary timeout',async()=>{
    vi.useFakeTimers();try{
      const {wv,toHost,fromHost}=fakeWebView(),client=new BridgeClient(wv);let done=false;
      const call=client.request({protocolVersion:1,kind:'request',requestId:'export',method:'host.exportImage',params:{}}).then(r=>{done=true;return r;});
      await vi.advanceTimersByTimeAsync(20000);expect(done).toBe(false);
      fromHost({protocolVersion:1,kind:'response',requestId:toHost[0].requestId,result:{path:'chosen.png'}});expect((await call).result).toEqual({path:'chosen.png'});
    }finally{vi.useRealTimers();}
  });
  it('mints unique correlation ids and returns each caller its own id', async () => {
    const { wv, toHost, fromHost } = fakeWebView();
    const client = new BridgeClient(wv);
    const calls = [1, 2, 3].map(() => client.request({ protocolVersion: 1, kind: 'request', requestId: 'same', method: 'host.ping', params: {} }));
    expect(new Set(toHost.map(m => m.requestId)).size).toBe(3);
    expect(toHost.every(m => m.requestId !== 'same')).toBe(true);
    for (const m of [...toHost].reverse()) fromHost({ protocolVersion: 1, kind: 'response', requestId: m.requestId, result: { for: m.requestId } });
    const results = await Promise.all(calls);
    expect(results.map(r => r.requestId)).toEqual(['same', 'same', 'same']);
    expect(results.map(r => (r.result as { for: string }).for)).toEqual(toHost.map(m => m.requestId));
  });

  it('ignores responses it did not ask for and settles unanswered calls as unknown', async () => {
    vi.useFakeTimers();
    try {
      const { wv, fromHost } = fakeWebView();
      const client = new BridgeClient(wv, 1000);
      const call = client.request({ protocolVersion: 1, kind: 'request', requestId: 'x', method: 'host.ping', params: {} });
      fromHost({ protocolVersion: 1, kind: 'response', requestId: 'forged', result: {} });
      await vi.advanceTimersByTimeAsync(1001);
      const r = await call;
      expect(r.error).toMatchObject({ code: 'timeout_unknown', outcome: 'unknown' });
    } finally { vi.useRealTimers(); }
  });

  it('answers a host request with not_ready until a handler exists, and reports handler failures as internal_error', async () => {
    const { wv, toHost, fromHost } = fakeWebView();
    const client = new BridgeClient(wv);
    fromHost({ protocolVersion: 1, kind: 'request', requestId: 'h1', method: 'doc.snapshot', params: {} });
    await flush();
    expect(toHost.at(-1)).toMatchObject({ kind: 'response', requestId: 'h1', error: { code: 'not_ready' } });
    client.onRequest(async () => { throw new Error('kaboom'); });
    fromHost({ protocolVersion: 1, kind: 'request', requestId: 'h2', method: 'doc.snapshot', params: {} });
    await flush();
    expect(toHost.at(-1)).toMatchObject({ requestId: 'h2', error: { code: 'internal_error', message: 'kaboom' } });
  });

  it('drops messages that are not protocol v1', async () => {
    const { wv, toHost, fromHost } = fakeWebView();
    new BridgeClient(wv).onRequest(async r => ({ protocolVersion: 1, kind: 'response', requestId: r.requestId, result: {} }));
    fromHost({ protocolVersion: 2, kind: 'request', requestId: 'a', method: 'doc.snapshot', params: {} });
    fromHost('garbage');
    fromHost(null);
    await flush();
    expect(toHost).toEqual([]);
  });
});

describe('startApp', () => {
  function boot() {
    const f = fakeWebView();
    const app = startApp(f.wv, null);
    const ready = f.toHost[0];
    const scope = { documentId: ready.params.documentId, sessionId: ready.params.sessionId };
    const ask = async (method: string, params: object = {}, s = scope, id = 'q'): Promise<ResponseEnvelope> => {
      f.fromHost(req(method, s, params, id));
      await flush();
      return f.toHost.filter(m => m.kind === 'response' && m.requestId === id).at(-1);
    };
    return { ...f, app, ready, scope, ask };
  }

  it('AppStartsReadyWithA4Snapshot', async () => {
    const { ready, ask, toHost } = boot();
    expect(ready).toMatchObject({ kind: 'event', method: 'editor.ready', params: { protocolVersion: 1, revision: 0 } });
    expect(toHost).toHaveLength(1); // ready is the first and only thing sent
    const snap = (await ask('doc.snapshot')).result as Snapshot;
    expect(snap.revision).toBe(0);
    expect(snap.document.pages).toHaveLength(1);
    expect(snap.document.pages[0]!.widthPt).toBeCloseTo(841.89, 2);
    expect(snap.document.pages[0]!.heightPt).toBeCloseTo(595.28, 2);
    expect(snap.document.pages[0]!.layers).toHaveLength(1);
  });

  it('applies a batch, answers a duplicate from the engine cache and mirrors the commit to the host', async () => {
    const { ask, toHost } = boot();
    const params = {
      transactionId: U(9001), baseRevision: 0, atomic: true,
      operations: [{ op: 'createShape', alias: 'box', bounds: { x: 72, y: 72, width: 144, height: 72 }, text: { value: 'Hi' } }],
    };
    const first = await ask('doc.apply', params, undefined, 'a1');
    expect(first.error).toBeUndefined();
    expect((first.result as { revision: number; created: string[] }).revision).toBe(1);
    const again = await ask('doc.apply', params, undefined, 'a2');
    expect(again.result).toEqual(first.result); // engine cache, not a second commit
    expect(toHost.filter(m => m.method === 'editor.committed')).toHaveLength(1);
    expect(toHost.find(m => m.method === 'editor.committed').params).toMatchObject({ sequence: 1, revision: 1 });
  });

  it('rejects invalid batches and stale scope without changing anything', async () => {
    const { ask, scope } = boot();
    const bad = await ask('doc.apply', { transactionId: U(9002), baseRevision: 0, atomic: true, operations: [{ op: 'move', target: 'x', delta: { xPt: 1, yPt: 0 }, extra: 1 }] });
    expect(bad.error?.code).toBe('invalid_request');
    const stale = await ask('doc.snapshot', {}, { ...scope, sessionId: U(7777) });
    expect(stale.error?.code).toBe('session_mismatch');
    const unknown = await ask('doc.nope');
    expect(unknown.error?.code).toBe('method_not_found');
    expect(((await ask('doc.snapshot')).result as Snapshot).revision).toBe(0);
  });

  it('undo and redo go through the engine as new revisions', async () => {
    const { ask } = boot();
    await ask('doc.apply', { transactionId: U(9003), baseRevision: 0, atomic: true, operations: [{ op: 'createShape', alias: 'a', bounds: { x: 0, y: 0, width: 10, height: 10 } }] });
    const undo = await ask('doc.undo', { transactionId: U(9004), baseRevision: 1 });
    expect((undo.result as { revision: number }).revision).toBe(2);
    const redo = await ask('doc.redo', { transactionId: U(9005), baseRevision: 2 });
    expect((redo.result as { revision: number }).revision).toBe(3);
    const changes = await ask('doc.changes', { sinceRevision: 0 });
    expect((changes.result as { transactions: Array<{ source: string }> }).transactions.map(t => t.source)).toEqual(['mcp', 'undo', 'redo']);
  });

  it('replace starts a new session, announces it, and an old-session request is refused', async () => {
    const { ask, toHost, scope } = boot();
    const snap = (await ask('doc.snapshot')).result as Snapshot;
    const replaced = await ask('doc.replace', { baseRevision: 0, document: { ...snap.document, title: 'Opened' } });
    expect(replaced.error).toBeUndefined();
    const session = toHost.find(m => m.method === 'editor.session');
    expect(session.params.sessionId).not.toBe(scope.sessionId);
    expect(session.params.documentId).toBe(scope.documentId);
    const old = await ask('doc.snapshot', {}, scope);
    expect(old.error?.code).toBe('session_mismatch');
    const fresh = await ask('doc.snapshot', {}, { documentId: session.params.documentId, sessionId: session.params.sessionId });
    expect((fresh.result as Snapshot).document.title).toBe('Opened');
  });

  it('markSaved reports dirty state through doc.status and refuses old sessions', async () => {
    const { ask, scope } = boot();
    expect(((await ask('doc.status')).result as { dirty: boolean }).dirty).toBe(true); // never saved
    await ask('doc.markSaved', { revision: 0, path: 'C:\\x.json' });
    expect(((await ask('doc.status')).result as { dirty: boolean }).dirty).toBe(false);
    await ask('doc.apply', { transactionId: U(9006), baseRevision: 0, atomic: true, operations: [{ op: 'createShape', bounds: { x: 0, y: 0, width: 5, height: 5 } }] });
    expect(((await ask('doc.status')).result as { dirty: boolean }).dirty).toBe(true);
    const bad = await ask('doc.markSaved', { revision: 1, path: 'C:\\x.json' }, { ...scope, sessionId: U(7777) });
    expect(bad.error?.code).toBe('session_mismatch');
  });
});
