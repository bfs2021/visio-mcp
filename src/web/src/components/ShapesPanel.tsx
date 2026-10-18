import { ComponentsPanel } from './ComponentsPanel';
import type { EditorModel } from '../editor/EditorModel';
import { useModel } from './useModel';
import { IconsPanel } from './IconsPanel';

type Item = { what: string; label: string; icon: React.ReactNode };

const stroke = { fill: 'none', stroke: 'currentColor', strokeWidth: 1.5 } as const;
const icons: Record<string, React.ReactNode> = {
  rectangle: <rect x="3" y="6" width="18" height="12" {...stroke} />,
  'rounded-rectangle': <rect x="3" y="6" width="18" height="12" rx="4" {...stroke} />,
  ellipse: <ellipse cx="12" cy="12" rx="9" ry="6" {...stroke} />,
  diamond: <path d="M12 3 21 12 12 21 3 12Z" {...stroke} />,
  triangle: <path d="M12 4 21 20H3Z" {...stroke} />,
  hexagon: <path d="M7 5h10l5 7-5 7H7l-5-7Z" {...stroke} />,
  parallelogram: <path d="M7 6h14l-4 12H3Z" {...stroke} />,
  cylinder: <g {...stroke}><ellipse cx="12" cy="6" rx="7" ry="3" /><path d="M5 6v12c0 1.7 3 3 7 3s7-1.3 7-3V6" /></g>,
  cloud: <path d="M7 18a4 4 0 0 1-.5-8 5.5 5.5 0 0 1 10.6-1A4.5 4.5 0 0 1 17 18Z" {...stroke} />,
  callout: <path d="M3 4h18v12H11l-4 4v-4H3Z" {...stroke} />,
  terminator: <path d="M8 6h8a6 6 0 0 1 0 12H8a6 6 0 0 1 0-12Z" {...stroke} />,
  'stored-data': <path d="M3 6h15a2.5 6 0 0 0 0 12H3a2.5 6 0 0 1 0-12Z" {...stroke} />,
  document: <path d="M3 5h18v11c-3 3-6-2.5-9 0s-6 2.5-9 0Z" {...stroke} />,
  'lined-document': <g {...stroke}><path d="M3 5h18v11c-3 3-6-2.5-9 0s-6 2.5-9 0Z" /><path d="M6 5v11" /></g>,
  text: <path d="M5 6h14M12 6v13M9 19h6" {...stroke} />,
};

const item = (what: string, label: string): Item => ({ what, label, icon: icons[what] });

const LIBRARIES: Array<{ name: string; items: Item[] }> = [
  { name: 'Basic shapes', items: [item('rectangle', 'Rectangle'), item('rounded-rectangle', 'Rounded rectangle'), item('ellipse', 'Ellipse'), item('triangle', 'Triangle'), item('text', 'Text box')] },
  { name: 'Flowchart', items: [item('rounded-rectangle', 'Process'), item('diamond', 'Decision'), item('parallelogram', 'Input / output'), item('ellipse', 'Start / end'), item('terminator', 'Terminator'), item('stored-data', 'Stored data'), item('document', 'Document'), item('lined-document', 'Lined document'), item('callout', 'Note')] },
  { name: 'Network', items: [item('cylinder', 'Database'), item('cloud', 'Cloud'), item('hexagon', 'Gateway'), item('rectangle', 'Server')] },
];

export function ShapesPanel({ model }: { model: EditorModel }) {
  useModel(model);
  const armed = model.tool.kind === 'place' ? model.tool.what : null;
  return (
    <aside className="panel panel-left" aria-label="Shapes">
      <h2>Shapes</h2>
      {LIBRARIES.map(lib => (
        <details key={lib.name} open>
          <summary>{lib.name}</summary>
          <div className="shape-grid">
            {lib.items.map(it => (
              <button
                key={`${lib.name}-${it.label}`} type="button" className={`shape-item${armed === it.what ? ' armed' : ''}`} title={it.label}
                data-testid={`shape-${lib.name === 'Basic shapes' ? '' : lib.name.toLowerCase() + '-'}${it.what}`} aria-label={it.label} draggable
                onDragStart={e => { e.dataTransfer.setData('application/x-diagram-shape', it.what); e.dataTransfer.effectAllowed = 'copy'; }}
                onClick={() => model.setTool(armed === it.what ? { kind: 'select' } : { kind: 'place', what: it.what })}
              >
                <svg viewBox="0 0 24 24" width="28" height="28" aria-hidden="true">{it.icon}</svg>
                <span>{it.label}</span>
              </button>
            ))}
          </div>
        </details>
      ))}
      <ComponentsPanel model={model} />
      <IconsPanel model={model} />
      <details open>
        <summary>Pictures</summary>
        <button type="button" className="wide" data-testid="insert-picture" onClick={() => void model.insertPicture()}>Insert picture…</button>
      </details>
    </aside>
  );
}
