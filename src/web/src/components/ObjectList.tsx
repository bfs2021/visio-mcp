import { useMemo } from 'react';
import type { EditorModel } from '../editor/EditorModel';
import { describe, navigableObjects } from '../editor/objectNav';

/**
 * The canvas is a picture, so this is its keyboard and accessibility twin: a list of the page's objects that takes focus like any other
 * control. Assistive technology (and UI Automation, which reads the page's accessibility tree) sees each object with its name, role,
 * position and size, and the one the keyboard is on is announced through aria-activedescendant. It is hidden from sight; the canvas shows
 * the selection and a focus ring is drawn around the canvas while this has focus.
 *
 * Keys: Tab / Shift+Tab step through the objects (and leave the canvas at either end), Ctrl+Arrow jumps to the nearest object in that
 * direction, Home / End go to the first / last, Enter or F2 edits text, arrows nudge by the grid step (Shift: 1 pt), Esc clears the selection.
 */
export function ObjectList({ model }: { model: EditorModel }) {
  const page = model.currentPage;
  const unit = model.unit;
  const revision = model.snapshot.revision;
  const items = useMemo(() => {
    const order = navigableObjects(page);
    const fmt = (pt: number) => String(Math.round(model.toUnit(pt) * 10) / 10);
    return order.map((e, i) => ({ id: e.id, label: describe(page, e, fmt, unit), pos: i + 1, count: order.length }));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [page, unit, revision]);
  const selected = new Set(model.targets);
  const active = model.activeObjectId;

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.altKey || e.metaKey) return;
    const handled = () => { e.preventDefault(); e.stopPropagation(); };
    if (e.key === 'Tab' && !e.ctrlKey) { if (model.stepObject(e.shiftKey ? -1 : 1)) handled(); return; }
    if (e.ctrlKey && e.key.startsWith('Arrow')) { handled(); model.jumpObject(e.key === 'ArrowLeft' ? 'left' : e.key === 'ArrowRight' ? 'right' : e.key === 'ArrowUp' ? 'up' : 'down'); return; }
    if (e.ctrlKey) return; // Ctrl+C, Ctrl+Z and the rest belong to the editor shortcuts
    if (e.key === 'Home' || e.key === 'End') { handled(); model.endObject(e.key === 'Home' ? 'first' : 'last'); return; }
    if (e.key === 'Enter' || e.key === 'F2') { handled(); model.editSelectedText(); return; }
    if (e.key === 'Escape' && model.selection.length && model.tool.kind === 'select') { handled(); model.selectObjects([]); }
  };

  return (
    <div
      className="object-list" role="listbox" tabIndex={0} data-testid="object-list"
      aria-label={`Objects on ${page.name}. Tab moves between objects, Ctrl and arrow keys jump in a direction, arrow keys move the selection, Enter edits text.`}
      aria-multiselectable="true" aria-orientation="vertical" aria-activedescendant={active ? `obj-${active}` : undefined}
      onKeyDown={onKeyDown}
    >
      {items.length === 0 && <div role="status">This page has no objects.</div>}
      {items.map(it => (
        <div key={it.id} id={`obj-${it.id}`} role="option" aria-selected={selected.has(it.id)} aria-posinset={it.pos} aria-setsize={it.count}
          aria-label={it.label} data-testid="object-option" onClick={() => model.selectObjects([it.id])}>
          {it.label}
        </div>
      ))}
    </div>
  );
}
