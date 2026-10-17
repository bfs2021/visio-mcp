import { useEffect, useState } from 'react';
import type { EditorModel } from '../editor/EditorModel';
import { useModel } from './useModel';

type Selected = { scope: 'document' | 'library'; id: string };

/**
 * Pictures. "In this document" are the descriptors the document pins; "Library" is the user's approved collection. Using a library
 * picture pins it into the document in the same transaction. Single and global replacement are separate buttons on purpose.
 */
export function AssetsPanel({ model }: { model: EditorModel }) {
  useModel(model);
  const [sel, setSel] = useState<Selected | null>(null);
  const [name, setName] = useState('');
  const [tags, setTags] = useState('');
  const [withId, setWithId] = useState('');
  useEffect(() => { void model.refreshLibrary(); }, [model]);

  const docAssets = model.documentAssets();
  const docIds = new Set(docAssets.map(a => a.id));
  const all = [
    ...docAssets.map(a => ({ scope: 'document' as const, id: a.id, name: a.name, sha256: a.sha256, tags: a.tags, usedBy: a.usedBy, newer: a.newerInLibrary })),
    ...model.library.map(a => ({ scope: 'library' as const, id: a.id, name: a.name, sha256: a.sha256, tags: a.tags, usedBy: 0, newer: false, inDoc: docIds.has(a.id) })),
  ];
  const chosen = sel ? all.find(a => a.scope === sel.scope && a.id === sel.id) : undefined;
  const choose = (a: typeof all[number]) => { setSel({ scope: a.scope, id: a.id }); setName(a.name); setTags(a.tags.join(', ')); };
  const others = all.filter(a => !(chosen && a.scope === chosen.scope && a.id === chosen.id));
  const source = (id: string) => { const a = all.find(x => `${x.scope}:${x.id}` === id); return a ? { scope: a.scope, id: a.id } : null; };

  const row = (a: typeof all[number]) => (
    <li key={`${a.scope}:${a.id}`} className={`asset-row${chosen && chosen.scope === a.scope && chosen.id === a.id ? ' selected' : ''}`} data-testid="asset-row" data-asset-id={a.id} data-scope={a.scope}
      draggable onDragStart={e => { e.dataTransfer.setData('application/x-diagram-asset', JSON.stringify({ scope: a.scope, id: a.id })); e.dataTransfer.effectAllowed = 'copy'; }}
      onClick={() => choose(a)}>
      <img src={`/assets/${a.sha256}`} alt="" width={28} height={28} data-testid="asset-thumb" />
      <span className="asset-text"><strong>{a.name}</strong><small data-testid="asset-id">{a.id}</small></span>
      {a.scope === 'document' && <span className="badge" title="Images using it">{a.usedBy}</span>}
      {'inDoc' in a && a.inDoc && <span className="badge" title="This document already pins a picture with this id">in document</span>}
      {a.newer && <span className="badge warn" title="The library has a different version of this picture">newer</span>}
    </li>
  );

  return (
    <section className="side-section" aria-label="Pictures" data-testid="assets-panel">
      <h2>Pictures</h2>
      <div className="row"><button type="button" data-testid="asset-import" onClick={() => void model.importToLibrary()}>Add to library…</button><button type="button" data-testid="asset-refresh" title="Read the library again" onClick={() => void model.refreshLibrary()}>Refresh</button></div>
      {model.libraryNote && <p className="muted" data-testid="library-note">{model.libraryNote}</p>}
      <h3>In this document</h3>
      <ul className="asset-list" data-testid="document-assets">{docAssets.length ? all.filter(a => a.scope === 'document').map(row) : <li className="muted">None yet.</li>}</ul>
      <h3>Library</h3>
      <ul className="asset-list" data-testid="library-assets">{model.library.length ? all.filter(a => a.scope === 'library').map(row) : <li className="muted">Empty.</li>}</ul>
      {chosen && (
        <div className="asset-actions" data-testid="asset-actions">
          <div className="row">
            <button type="button" data-testid="asset-place" onClick={() => void model.placeAsset(chosen)}>Place on page</button>
            <button type="button" data-testid="asset-use-one" title="Change only the selected picture on the page" onClick={() => void model.useForSelectedImage(chosen)}>Use for selected picture</button>
          </div>
          {chosen.scope === 'document' && (
            <div className="row">
              <select data-testid="asset-replace-with" value={withId} aria-label="Replace every use with" onChange={e => setWithId(e.target.value)}>
                <option value="">Replace every use with…</option>
                {others.map(o => <option key={`${o.scope}:${o.id}`} value={`${o.scope}:${o.id}`}>{o.name} ({o.scope})</option>)}
              </select>
              <button type="button" data-testid="asset-replace-all" disabled={!source(withId)} title="Changes every picture that uses this asset"
                onClick={() => { const s = source(withId); if (s) void model.replaceEverywhere(chosen.id, s); }}>Replace everywhere</button>
            </div>
          )}
          <div className="row">
            <input value={name} data-testid="asset-name" aria-label="Picture name" onChange={e => setName(e.target.value)} />
            <button type="button" data-testid="asset-rename" onClick={() => void model.renameAsset(chosen.scope, chosen.id, name)}>Rename</button>
          </div>
          <div className="row">
            <input value={tags} data-testid="asset-tags" aria-label="Tags, separated by commas" placeholder="tags, comma separated" onChange={e => setTags(e.target.value)} />
            <button type="button" data-testid="asset-tag-apply" onClick={() => void model.tagAsset(chosen.scope, chosen.id, tags.split(','))}>Tag</button>
          </div>
          <div className="row">
            <button type="button" data-testid="asset-copy-id" onClick={() => void model.copyAssetId(chosen.id)}>Copy ID</button>
            {chosen.scope === 'library' && <button type="button" data-testid="asset-replace-source" onClick={() => void model.replaceLibrarySource(chosen.id)}>Replace source…</button>}
            <button type="button" data-testid="asset-delete" onClick={() => void model.deleteAsset(chosen.scope, chosen.id)}>Delete</button>
          </div>
        </div>
      )}
    </section>
  );
}
