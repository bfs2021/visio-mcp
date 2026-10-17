import type { RequestEnvelope } from '../contracts/generated';
import { fail, ok, type Result } from '../model/result';
import type { BridgeClient } from './BridgeClient';

let n = 0;
const envelope = (method: string, params: Record<string, unknown> = {}): RequestEnvelope => ({
  protocolVersion: 1, kind: 'request', requestId: `h${++n}`, method, params,
});

/** Typed wrappers for host.* calls. The host owns dialogs, files and approved bytes; the page only asks. */
export function createHostServices(client: BridgeClient) {
  async function call<T>(method: string, params: Record<string, unknown> = {}): Promise<Result<T>> {
    const r = await client.request(envelope(method, params));
    return r.error ? { ok: false, error: r.error } : ok(r.result as T);
  }
  return {
    ping: () => call<{ pong: true }>('host.ping'),
    call,
  };
}

export type HostServices = ReturnType<typeof createHostServices>;
export { fail };
