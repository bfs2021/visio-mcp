import { useEffect, useRef } from 'react';
import { arrangeActions } from '../editor/arrangeActions';
import type { EditorModel } from '../editor/EditorModel';
import { useModel } from './useModel';

interface Item { id: string; label: string; shortcut?: string; disabled: boolean; run: () => void }

/** Right-click menu on the canvas: the same actions as the Arrange menu, plus the everyday edit commands. */
export function ContextMenu({ model, at, onClose }: { model: EditorModel; at: { x: number; y: number }; onClose: () => void }) {
  useModel(model);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const away = (e: Event) => { if (!ref.current?.contains(e.target as Node)) onClose(); };
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('mousedown', away, true);
    document.addEventListener('keydown', esc);
    return () => { document.removeEventListener('mousedown', away, true); document.removeEventListener('keydown', esc); };
  }, [onClose]);

  const actions = arrangeActions(model);
  const has = model.selection.length > 0;
  const sections: Item[][] = [
    [
      { id: 'copy', label: 'Copy', shortcut: 'Ctrl+C', disabled: !has, run: () => model.copy() },
      { id: 'paste', label: 'Paste', shortcut: 'Ctrl+V', disabled: false, run: () => void model.paste() },
      { id: 'duplicate', label: 'Duplicate', shortcut: 'Ctrl+D', disabled: !has, run: () => void model.duplicate() },
      { id: 'delete', label: 'Delete', shortcut: 'Del', disabled: !has, run: () => void model.deleteSelection() },
    ],
    actions.filter(a => a.section === 'group'),
    actions.filter(a => a.section === 'order'),
    actions.filter(a => a.section === 'align' || a.section === 'distribute'),
  ];
  return (
    <div className="context-menu" role="menu" data-testid="context-menu" ref={ref} style={{ left: at.x, top: at.y }}>
      {sections.map((items, i) => (
        <div key={i} className="context-section">
          {items.map(it => (
            <button key={it.id} type="button" role="menuitem" data-testid={`context-${it.id}`} disabled={it.disabled} onClick={() => { onClose(); it.run(); }}>
              <span>{it.label}</span><kbd>{it.shortcut ?? ''}</kbd>
            </button>
          ))}
        </div>
      ))}
    </div>
  );
}
