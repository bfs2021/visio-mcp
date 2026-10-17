import type { RequestEnvelope, ResponseEnvelope } from '../contracts/generated';
import type { CommandEngine } from '../commands/CommandEngine';
import { AdmissionGate } from './AdmissionGate';
import { createEngineHandler, type EngineHandlerOptions } from './engineHandlers';

/**
 * Entry point for every host-initiated request. A request that carries a host-stamped origin came from an outside client (an MCP
 * connection) and passes the admission gate; one without an origin is the host's own lifecycle traffic (open, save, status) and goes
 * straight to the handler. Both end at the same engine.
 */
export function createRequestRouter(engine: CommandEngine, opts: EngineHandlerOptions = {}) {
  const handler = createEngineHandler(engine, opts);
  const gate = new AdmissionGate(handler, { scope: () => engine.scope() });
  return {
    gate,
    dispatch: (env: RequestEnvelope): Promise<ResponseEnvelope> => (env.origin ? gate.accept(env.origin.connectionId, env) : handler(env)),
    setGestureActive: (active: boolean) => gate.setGestureActive(active),
    cancelGesture: () => gate.cancelGesture(),
  };
}
