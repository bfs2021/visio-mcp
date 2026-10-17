import { InternalEvent } from '@maxgraph/core';
import type { Projection } from './projection';

/** Messages cross the WebView2 boundary as plain JSON only. No host objects, no script evaluation. */
interface Request {
  protocolVersion: 1;
  kind: 'request';
  requestId: string;
  method: string;
  documentId?: string;
  sessionId?: string;
  params: Record<string, any>;
}

type Op =
  | { op: 'moveElement'; id: string; dx: number; dy: number }
  | { op: 'setText'; id: string; text: string }
  | { op: 'setImage'; id: string; assetUrl: string; sha256: string }
  | { op: 'test.hang' };

declare global {
  interface Window {
    chrome?: { webview?: { postMessage(m: unknown): void; addEventListener(t: 'message', cb: (e: MessageEvent) => void): void } };
  }
}

const hex = (b: ArrayBuffer) => [...new Uint8Array(b)].map(x => x.toString(16).padStart(2, '0')).join('');
const toBase64 = (b: Uint8Array) => { let s = ''; for (const x of b) s += String.fromCharCode(x); return btoa(s); };

export function startBridge(p: Projection): void {
  const wv = window.chrome?.webview;
  if (!wv) return; // opened in a plain browser: no host to talk to
  let scope: { sessionId: string; documentId: string } | null = null;

  const post = (m: unknown) => wv.postMessage(m);
  const ok = (r: Request, result: unknown) => post({ protocolVersion: 1, kind: 'response', requestId: r.requestId, result });
  const fail = (r: Request, code: string, message: string) =>
    post({ protocolVersion: 1, kind: 'response', requestId: r.requestId, error: { code, message, retryable: false } });

  async function apply(ops: Op[]): Promise<{ revision: number; changedIds: string[] }> {
    // Fetch and verify every asset before touching the model, so the whole batch is one revision or nothing.
    const assets = new Map<string, string>();
    for (const o of ops) {
      if (o.op !== 'setImage') continue;
      const res = await fetch(o.assetUrl); // same virtual origin only; the CSP refuses anything else
      if (!res.ok) throw new Error(`asset ${o.assetUrl} returned ${res.status}`);
      const bytes = new Uint8Array(await res.arrayBuffer());
      if (hex(await crypto.subtle.digest('SHA-256', bytes)) !== o.sha256) throw new Error('asset hash does not match its URL');
      assets.set(o.id, `data:${res.headers.get('content-type') ?? 'image/png'};base64,${toBase64(bytes)}`);
    }
    const g = p.graph;
    const m = g.getDataModel();
    const changed = new Set<string>();
    g.batchUpdate(() => {
      for (const o of ops) {
        if (o.op === 'test.hang') continue;
        const cell = m.getCell(o.id);
        if (!cell) throw new Error(`no element ${o.id}`);
        if (o.op === 'moveElement') g.moveCells([cell], o.dx, o.dy, false);
        else if (o.op === 'setText') m.setValue(cell, o.text);
        else if (o.op === 'setImage') g.setCellStyles('image', assets.get(o.id)!, [cell]);
        changed.add(o.id);
      }
    });
    return { revision: p.currentRevision, changedIds: [...changed] };
  }

  async function handle(r: Request) {
    try {
      switch (r.method) {
        case 'session.start':
          scope = { sessionId: r.params.sessionId, documentId: r.params.documentId };
          document.body.dataset.sessionId = scope.sessionId;
          return ok(r, { revision: p.currentRevision });
        case 'doc.apply': {
          const ops = r.params.operations as Op[];
          if (ops.some(o => o.op === 'test.hang')) return; // never answers: used to prove crash settlement
          return ok(r, await apply(ops));
        }
        case 'doc.summary': {
          const s = p.readProbeState();
          return ok(r, { revision: s.revision, elementIds: ['rect', 'ellipse', 'picture', 'vector', 'connector'], rect: s.rect, ellipse: s.ellipse });
        }
        case 'doc.render': {
          const png = await p.renderProbe();
          return ok(r, { png: toBase64(png), bytes: png.length });
        }
        default:
          return fail(r, 'method_not_found', `unknown method ${r.method}`);
      }
    } catch (e) {
      return fail(r, 'internal_error', e instanceof Error ? e.message : String(e));
    }
  }

  wv.addEventListener('message', e => {
    const r = e.data as Request;
    if (r && r.kind === 'request' && r.protocolVersion === 1) void handle(r);
  });

  // Mirror the revision into the DOM so a screenshot or DOM read proves the canvas, not just the store, changed.
  const label = document.getElementById('rev');
  const show = () => { if (label) label.textContent = `rev ${p.currentRevision}`; document.body.dataset.revision = String(p.currentRevision); };
  p.onChange = show;
  p.graph.getDataModel().addListener(InternalEvent.CHANGE, show);
  show();

  post({ protocolVersion: 1, kind: 'event', method: 'editor.ready', params: { protocolVersion: 1 } });
}
