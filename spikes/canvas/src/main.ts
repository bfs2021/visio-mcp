import type { CellEditorHandler, SelectionCellsHandler } from '@maxgraph/core';
import { Projection, fixture, taintCheck, type ProbeSceneDto, type GestureEvent } from './projection';

const container = document.getElementById('graph')!;
const p = new Projection(container);

type Pt = { x: number; y: number };

/** Client coordinates (what a mouse event needs) for a point in graph view coordinates. */
function client(x: number, y: number): Pt {
  const r = container.getBoundingClientRect();
  return { x: r.left + x - container.scrollLeft, y: r.top + y - container.scrollTop };
}

const cellOf = (id: string) => p.graph.getDataModel().getCell(id)!;
const handlerOf = (id: string) =>
  (p.graph.getPlugin('SelectionCellsHandler') as SelectionCellsHandler).getHandler(cellOf(id)) as any;

function centre(id: string): Pt {
  const s = p.graph.getView().getState(cellOf(id))!;
  if (cellOf(id).isEdge()) {
    const [a, b] = s.absolutePoints as Array<{ x: number; y: number }>;
    return client((a.x + b.x) / 2, (a.y + b.y) / 2);
  }
  return client(s.getCenterX(), s.getCenterY());
}

function handle(id: string, which: 'rotate' | 'se' | 'bend'): Pt {
  const h = handlerOf(id);
  if (!h) throw new Error(`no handler for ${id}: select it first`);
  const shape = which === 'rotate' ? h.rotationShape : which === 'se' ? h.sizers[7] : h.bends[1];
  if (!shape) throw new Error(`no ${which} handle on ${id}`);
  const b = shape.bounds;
  return client(b.x + b.width / 2, b.y + b.height / 2);
}

function textEdit(id: string, text: string | null): void {
  const g = p.graph;
  g.startEditingAtCell(cellOf(id));
  const ed = g.getPlugin('CellEditorHandler') as CellEditorHandler;
  if (text !== null) ed.textarea!.innerText = text;
  g.stopEditing(text === null);
}

(window as unknown as { probe: object }).probe = {
  fixture,
  project: (s: ProbeSceneDto) => p.project(s),
  readProbeState: () => p.readProbeState(),
  renderProbe: () => p.renderProbe(),
  gestures: (): GestureEvent[] => p.log.map(e => ({ ...e })).map(({ t: _t, ...rest }) => ({ ...rest, t: 0 })),
  clearGestures: () => { p.log.length = 0; },
  centre,
  handle,
  tainted: () => taintCheck(p.readProbeState()),
  zoom: (f: number) => p.graph.zoomTo(f),
  textEdit,
  timing: () => ({ ...p.timing }),
};
