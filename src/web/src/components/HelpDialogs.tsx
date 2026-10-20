import { useEffect, useMemo, useRef, useState } from 'react';
import type { EditorModel } from '../editor/EditorModel';
import { appCommands, filterCommands, fixedShortcuts } from '../editor/appCommands';
import type { WorkspaceModel } from '../workspace/WorkspaceModel';

/** Help > Keyboard shortcuts, Help > About and the Ctrl+Shift+P command palette. One of the three shows at a time. */
export function HelpDialogs({ ws, model }: { ws: WorkspaceModel; model: EditorModel }) {
  const panel = ws.help.panel;
  if (panel === 'shortcuts') return <ShortcutList ws={ws} model={model} />;
  if (panel === 'about') return <About ws={ws} />;
  if (panel === 'palette') return <Palette ws={ws} model={model} />;
  return null;
}

function Frame({ title, id, onClose, children, testId }: { title: string; id: string; onClose: () => void; children: React.ReactNode; testId: string }) {
  return (
    <div className="dialog-backdrop" role="presentation" onMouseDown={e => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="dialog" role="dialog" aria-modal="true" aria-labelledby={id} data-testid={testId} onKeyDown={e => { if (e.key === 'Escape') { e.stopPropagation(); onClose(); } }}>
        <h2 id={id}>{title}</h2>
        {children}
        <div className="row dialog-actions"><button type="button" autoFocus data-testid={`${testId}-close`} onClick={onClose}>Close</button></div>
      </div>
    </div>
  );
}

function ShortcutList({ ws, model }: { ws: WorkspaceModel; model: EditorModel }) {
  const groups = useMemo(() => {
    const by = new Map<string, Array<{ label: string; shortcut: string }>>();
    const add = (g: string, label: string, shortcut: string) => by.set(g, [...(by.get(g) ?? []), { label, shortcut }]);
    for (const c of appCommands(model, ws)) if (c.shortcut) add(c.group, c.label.replace(/…$/, ''), c.shortcut);
    for (const f of fixedShortcuts) add(f.group, f.label, f.shortcut);
    return [...by.entries()];
  }, [model, ws]);
  const noKey = appCommands(model, ws).filter(c => !c.shortcut).map(c => c.label.replace(/…$/, ''));
  return (
    <Frame title="Keyboard shortcuts" id="shortcuts-title" testId="help-shortcuts" onClose={() => ws.help.close()}>
      {groups.map(([group, rows]) => (
        <section key={group}>
          <h3>{group}</h3>
          <table className="shortcut-table">
            <tbody>{rows.map(r => <tr key={r.label}><td>{r.label}</td><td><kbd>{r.shortcut}</kbd></td></tr>)}</tbody>
          </table>
        </section>
      ))}
      <p className="hint" data-testid="shortcuts-unbound">Also available from the menus or the command palette (Ctrl+Shift+P): {noKey.join(', ')}.</p>
    </Frame>
  );
}

function About({ ws }: { ws: WorkspaceModel }) {
  const { about, aboutError } = ws.help;
  return (
    <Frame title={about?.name ?? 'About'} id="about-title" testId="help-about" onClose={() => ws.help.close()}>
      {aboutError && <p className="diag diag-warning" data-testid="about-error">{aboutError}</p>}
      {!about && !aboutError && <p>Loading…</p>}
      {about && (
        <>
          <dl className="about-list">
            <dt>Version</dt><dd data-testid="about-version">{about.version}{about.build ? ` (build ${about.build})` : ''}</dd>
            <dt>Runtime</dt><dd>{about.runtime}</dd>
            <dt>MCP protocol</dt><dd>version {about.protocolVersion}</dd>
            <dt>MCP companion</dt>
            <dd data-testid="about-companions">
              {about.companions.length === 0
                ? 'Not connected. Start the Diagram.Mcp server from your agent to connect it.'
                : about.companions.map((c, i) => <div key={i}>{c.label}: {c.version ? `version ${c.version}` : 'version not reported'}</div>)}
            </dd>
          </dl>
          <h3>Data folders</h3>
          <table className="shortcut-table" data-testid="about-folders">
            <tbody>{about.folders.map(f => <tr key={f.label}><td>{f.label}</td><td className="path">{f.path}</td></tr>)}</tbody>
          </table>
        </>
      )}
    </Frame>
  );
}

function Palette({ ws, model }: { ws: WorkspaceModel; model: EditorModel }) {
  const [query, setQuery] = useState('');
  const [index, setIndex] = useState(0);
  const listRef = useRef<HTMLUListElement>(null);
  const all = appCommands(model, ws).filter(c => c.id !== 'palette');
  const shown = filterCommands(all, query);
  const at = Math.min(index, Math.max(0, shown.length - 1));
  useEffect(() => { listRef.current?.querySelector('[aria-selected="true"]')?.scrollIntoView?.({ block: 'nearest' }); }, [at, query]);
  const run = (c: (typeof shown)[number] | undefined) => {
    if (!c || c.disabled) return;
    ws.help.close();
    setTimeout(c.run, 0); // after the palette is gone, so a command that opens a dialog or takes focus is not fighting it
  };
  return (
    <div className="dialog-backdrop palette-backdrop" role="presentation" onMouseDown={e => { if (e.target === e.currentTarget) ws.help.close(); }}>
      <div className="dialog palette" role="dialog" aria-modal="true" aria-label="Command palette" data-testid="help-palette"
        onKeyDown={e => {
          if (e.key === 'Escape') { e.stopPropagation(); ws.help.close(); }
          else if (e.key === 'ArrowDown') { e.preventDefault(); setIndex(Math.min(at + 1, shown.length - 1)); }
          else if (e.key === 'ArrowUp') { e.preventDefault(); setIndex(Math.max(at - 1, 0)); }
          else if (e.key === 'Enter') { e.preventDefault(); run(shown[at]); }
        }}>
        <input autoFocus type="text" role="combobox" aria-expanded="true" aria-controls="palette-list" aria-activedescendant={shown[at] ? `palette-${shown[at]!.id}` : undefined}
          aria-label="Type a command" placeholder="Type a command" value={query} data-testid="palette-input"
          onChange={e => { setQuery(e.target.value); setIndex(0); }} />
        <ul id="palette-list" role="listbox" ref={listRef} data-testid="palette-list">
          {shown.length === 0 && <li className="palette-empty">No command matches “{query}”.</li>}
          {shown.map((c, i) => (
            <li key={c.id} id={`palette-${c.id}`} role="option" aria-selected={i === at} aria-disabled={c.disabled || undefined} data-testid={`palette-item-${c.id}`}
              className={i === at ? 'active' : ''} onMouseMove={() => setIndex(i)} onClick={() => run(c)}>
              <span className="palette-group">{c.group}</span><span>{c.label.replace(/…$/, '')}</span><kbd>{c.shortcut ?? ''}</kbd>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}
