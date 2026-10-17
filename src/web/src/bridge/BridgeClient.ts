import type { AppError, RequestEnvelope, ResponseEnvelope } from '../contracts/generated';

/** The part of window.chrome.webview the page uses. Messages are plain JSON in both directions; there are no host objects. */
export interface WebViewLike {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
}

export type RequestHandler = (request: RequestEnvelope) => Promise<ResponseEnvelope>;

const failure = (requestId: string, error: AppError): ResponseEnvelope => ({ protocolVersion: 1, kind: 'response', requestId, error });

/**
 * Page side of the bridge. `request` sends a page-initiated call to the host (host.* methods) and resolves with its response;
 * `onRequest` registers the handler for host-initiated calls (doc.* methods); `sendEvent` publishes editor events.
 */
export class BridgeClient {
  private readonly pending = new Map<string, (r: ResponseEnvelope) => void>();
  private handler: RequestHandler | null = null;
  private counter = 0;

  constructor(private readonly webview: WebViewLike, private readonly timeoutMs = 15_000) {
    webview.addEventListener('message', e => void this.receive(e.data));
  }

  onRequest(handler: RequestHandler): void { this.handler = handler; }

  sendEvent(method: string, params: Record<string, unknown>): void {
    this.webview.postMessage({ protocolVersion: 1, kind: 'event', method, params });
  }

  request(envelope: RequestEnvelope): Promise<ResponseEnvelope> {
    // Correlation ids are minted here, so two callers can never collide.
    const id = `p${++this.counter}-${Math.random().toString(36).slice(2, 10)}`;
    return new Promise(resolve => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        resolve(failure(envelope.requestId, { code: 'timeout_unknown', message: 'the host did not answer', retryable: false, outcome: 'unknown' }));
      }, this.timeoutMs);
      this.pending.set(id, r => { clearTimeout(timer); resolve({ ...r, requestId: envelope.requestId }); });
      this.webview.postMessage({ ...envelope, requestId: id });
    });
  }

  private async receive(data: unknown): Promise<void> {
    const msg = data as { protocolVersion?: number; kind?: string; requestId?: string } | null;
    if (!msg || msg.protocolVersion !== 1 || typeof msg.requestId !== 'string') return;
    if (msg.kind === 'response') {
      const done = this.pending.get(msg.requestId);
      if (done) { this.pending.delete(msg.requestId); done(msg as ResponseEnvelope); }
      return;
    }
    if (msg.kind !== 'request') return;
    const request = msg as RequestEnvelope;
    let response: ResponseEnvelope;
    try {
      response = this.handler
        ? await this.handler(request)
        : failure(request.requestId, { code: 'not_ready', message: 'no request handler is registered', retryable: true });
    } catch (e) {
      response = failure(request.requestId, { code: 'internal_error', message: e instanceof Error ? e.message : String(e), retryable: false });
    }
    this.webview.postMessage({ ...response, requestId: request.requestId });
  }
}
