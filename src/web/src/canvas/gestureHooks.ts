import { EdgeHandler, ElbowEdgeHandler, EventObject, InternalEvent, SelectionHandler, VertexHandler, type Graph } from '@maxgraph/core';
import type { GestureNotice } from './CanvasAdapter';
import type { GestureKind } from './GestureController';

/**
 * begin/commit/cancel for the human gestures, derived from maxGraph's own handlers (it has no such events): a gesture begins when
 * a handler first previews, commits when its mouseUp runs and cancels when the handler resets without one (Escape).
 * Selection, panning and zoom never reach these handlers, so they never raise the interlock. Wrapping happens once per page.
 */
let installed = false;
let sink: ((n: GestureNotice) => void) | null = null;

export function setGestureSink(next: ((n: GestureNotice) => void) | null): void { sink = next; }

export function installGestureHooks(graph: Graph): void {
  if (!installed) { installed = true; installOnPrototypes(); }
  graph.addListener(InternalEvent.START_EDITING, (_s: unknown, e: EventObject) => {
    const id = (e.getProperty('cell') as { getId(): string | null } | undefined)?.getId();
    sink?.({ phase: 'begin', kind: 'text', ids: id ? [id] : [] });
  });
  graph.addListener(InternalEvent.EDITING_STOPPED, (_s: unknown, e: EventObject) => {
    const id = (e.getProperty('cell') as { getId(): string | null } | undefined)?.getId();
    sink?.({ phase: e.getProperty('cancel') ? 'cancel' : 'commit', kind: 'text', ids: id ? [id] : [] });
  });
}

type Active = { kind: GestureKind; ids: string[] };

function installOnPrototypes(): void {
  let active: Active | null = null;
  let committing = false;
  const emit = (phase: GestureNotice['phase'], a: Active) => sink?.({ phase, kind: a.kind, ids: a.ids });
  const begin = (kind: GestureKind, ids: string[]) => { if (!active) { active = { kind, ids }; emit('begin', active); } };

  const wrap = (proto: any, name: string, around: (self: any, orig: (...a: any[]) => any, args: any[]) => any) => {
    const orig = proto[name];
    proto[name] = function (this: any, ...args: any[]) { return around(this, (...a) => orig.apply(this, a), args); };
  };
  const finish = (orig: (...a: any[]) => any, args: any[]) => {
    const was = active;
    committing = true;
    try { return orig(...args); } finally {
      committing = false;
      if (was && active === was) { active = null; emit('commit', was); }
    }
  };
  const reset = (orig: (...a: any[]) => any, args: any[]) => {
    const was = active;
    const r = orig(...args);
    if (was && !committing && active === was) { active = null; emit('cancel', was); }
    return r;
  };

  wrap(VertexHandler.prototype, 'start', (s, orig, a) => {
    orig(...a);
    const index = a[2] as number;
    if (index !== InternalEvent.LABEL_HANDLE) begin(index === InternalEvent.ROTATION_HANDLE ? 'rotate' : 'resize', [s.state.cell.getId()]);
  });
  wrap(VertexHandler.prototype, 'mouseUp', (_s, orig, a) => finish(orig, a));
  // maxGraph 0.24.0 defect: VertexHandler.reset() reads sizers[index].node, but the rotation handle (index -2) has no sizer, so Escape
  // during a rotate drag throws before cleanup. Give reset() a stand-in sizer for the duration of the call.
  wrap(VertexHandler.prototype, 'reset', (s, orig, a) => {
    const i = s.index as number | null;
    const patch = i !== null && s.sizers?.[i] === undefined;
    if (patch) s.sizers[i!] = { node: { style: { display: '' } } };
    try { return reset(orig, a); } finally { if (patch) delete s.sizers[i!]; }
  });

  for (const H of [EdgeHandler, ElbowEdgeHandler]) {
    wrap(H.prototype, 'start', (s, orig, a) => { orig(...a); begin('bend', [s.state.cell.getId()]); });
    wrap(H.prototype, 'mouseUp', (_s, orig, a) => finish(orig, a));
    wrap(H.prototype, 'reset', (_s, orig, a) => reset(orig, a));
  }

  wrap(SelectionHandler.prototype, 'mouseMove', (s, orig, a) => {
    const r = orig(...a);
    if (s.cell && s.first && s.currentDx != null && s.currentDy != null && (s.currentDx !== 0 || s.currentDy !== 0)) {
      begin('move', (s.graph.getSelectionCells() as Array<{ getId(): string }>).map(c => c.getId()));
    }
    return r;
  });
  wrap(SelectionHandler.prototype, 'mouseUp', (_s, orig, a) => finish(orig, a));
  wrap(SelectionHandler.prototype, 'reset', (_s, orig, a) => reset(orig, a));
}
