import { EdgeHandler, ElbowEdgeHandler, EventObject, InternalEvent, SelectionHandler, VertexHandler, type Graph } from '@maxgraph/core';
import type { GestureNotice } from './CanvasAdapter';
import type { GestureKind } from './GestureController';

/**
 * begin/commit/cancel for the human gestures, derived from maxGraph's own handlers (it has no such events): a gesture begins when
 * a handler first previews, commits when its mouseUp runs and cancels when the handler resets without one (Escape).
 * Selection, panning and zoom never reach these handlers, so they never raise the interlock. Wrapping happens once per page.
 */
let installed = false;
/**
 * Each graph reports to its own sink. The prototypes are shared by every maxGraph instance on the page, including the hidden ones that
 * derive routes and draw pictures, so a single global sink would be taken over by whichever instance was created last.
 */
const sinks = new WeakMap<object, (n: GestureNotice) => void>();

export function installGestureHooks(graph: Graph, sink: (n: GestureNotice) => void): () => void {
  if (!installed) { installed = true; installOnPrototypes(); }
  sinks.set(graph, sink);
  graph.addListener(InternalEvent.START_EDITING, (_s: unknown, e: EventObject) => {
    const id = (e.getProperty('cell') as { getId(): string | null } | undefined)?.getId();
    sinks.get(graph)?.({ phase: 'begin', kind: 'text', ids: id ? [id] : [] });
  });
  graph.addListener(InternalEvent.EDITING_STOPPED, (_s: unknown, e: EventObject) => {
    const id = (e.getProperty('cell') as { getId(): string | null } | undefined)?.getId();
    sinks.get(graph)?.({ phase: e.getProperty('cancel') ? 'cancel' : 'commit', kind: 'text', ids: id ? [id] : [] });
  });
  return () => { sinks.delete(graph); };
}

type Active = { kind: GestureKind; ids: string[] };

function installOnPrototypes(): void {
  // gesture state is per graph for the same reason the sinks are
  const state = new WeakMap<object, { active: Active | null; committing: boolean }>();
  const st = (graph: object) => { let v = state.get(graph); if (!v) { v = { active: null, committing: false }; state.set(graph, v); } return v; };
  const emit = (graph: object, phase: GestureNotice['phase'], a: Active) => sinks.get(graph)?.({ phase, kind: a.kind, ids: a.ids });
  const begin = (graph: object, kind: GestureKind, ids: string[]) => { const v = st(graph); if (!v.active) { v.active = { kind, ids }; emit(graph, 'begin', v.active); } };

  const wrap = (proto: any, name: string, around: (self: any, orig: (...a: any[]) => any, args: any[]) => any) => {
    const orig = proto[name];
    proto[name] = function (this: any, ...args: any[]) { return around(this, (...a) => orig.apply(this, a), args); };
  };
  const finish = (self: any, orig: (...a: any[]) => any, args: any[]) => {
    const v = st(self.graph);
    const was = v.active;
    v.committing = true;
    try { return orig(...args); } finally {
      v.committing = false;
      if (was && v.active === was) { v.active = null; emit(self.graph, 'commit', was); }
    }
  };
  const reset = (self: any, orig: (...a: any[]) => any, args: any[]) => {
    const v = st(self.graph);
    const was = v.active;
    const r = orig(...args);
    if (was && !v.committing && v.active === was) { v.active = null; emit(self.graph, 'cancel', was); }
    return r;
  };

  wrap(VertexHandler.prototype, 'start', (s, orig, a) => {
    orig(...a);
    const index = a[2] as number;
    if (index !== InternalEvent.LABEL_HANDLE) begin(s.graph, index === InternalEvent.ROTATION_HANDLE ? 'rotate' : 'resize', [s.state.cell.getId()]);
  });
  wrap(VertexHandler.prototype, 'mouseUp', (s, orig, a) => finish(s, orig, a));
  // maxGraph 0.24.0 defect: VertexHandler.reset() reads sizers[index].node, but the rotation handle (index -2) has no sizer, so Escape
  // during a rotate drag throws before cleanup. Give reset() a stand-in sizer for the duration of the call.
  wrap(VertexHandler.prototype, 'reset', (s, orig, a) => {
    const i = s.index as number | null;
    const patch = i !== null && s.sizers?.[i] === undefined;
    if (patch) s.sizers[i!] = { node: { style: { display: '' } } };
    try { return reset(s, orig, a); } finally { if (patch) delete s.sizers[i!]; }
  });

  for (const H of [EdgeHandler, ElbowEdgeHandler]) {
    wrap(H.prototype, 'start', (s, orig, a) => { orig(...a); begin(s.graph, 'bend', [s.state.cell.getId()]); });
    wrap(H.prototype, 'mouseUp', (s, orig, a) => finish(s, orig, a));
    wrap(H.prototype, 'reset', (s, orig, a) => reset(s, orig, a));
  }

  wrap(SelectionHandler.prototype, 'mouseMove', (s, orig, a) => {
    const r = orig(...a);
    if (s.cell && s.first && s.currentDx != null && s.currentDy != null && (s.currentDx !== 0 || s.currentDy !== 0)) {
      begin(s.graph, 'move', (s.graph.getSelectionCells() as Array<{ getId(): string }>).map(c => c.getId()));
    }
    return r;
  });
  wrap(SelectionHandler.prototype, 'mouseUp', (s, orig, a) => finish(s, orig, a));
  wrap(SelectionHandler.prototype, 'reset', (s, orig, a) => reset(s, orig, a));
}
