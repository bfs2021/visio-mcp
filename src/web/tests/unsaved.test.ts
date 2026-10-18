import { describe, expect, it } from 'vitest';
import { createTestEngine } from './support/engine';
import { run } from './support/ops';

describe('unsaved work', () => {
  it('a blank document nobody touched is not unsaved, an edit makes it so, and a save clears it', async () => {
    const t = createTestEngine();
    expect(t.engine.status().unsaved).toBe(false);
    await run(t, [{ op: 'createShape', bounds: { x: 0, y: 0, width: 10, height: 10 } }]);
    expect(t.engine.status().unsaved).toBe(true);
    await t.engine.markSaved(t.scope(), t.engine.revision, 'C:/x.diagram.json');
    expect(t.engine.status().unsaved).toBe(false);
    await run(t, [{ op: 'createShape', bounds: { x: 20, y: 0, width: 10, height: 10 } }]);
    expect(t.engine.status().unsaved).toBe(true);
  });

  it('a document that was opened or recovered into the page counts as unsaved until it is marked saved', async () => {
    const t = createTestEngine();
    const doc = t.store.snapshot().document;
    const r = await t.engine.replaceDocument(doc, { ...t.scope(), baseRevision: t.engine.revision });
    expect(r.ok).toBe(true);
    expect(t.engine.status().unsaved).toBe(true); // recovered work stays marked unsaved
    await t.engine.markSaved(t.engine.scope(), t.engine.revision, 'C:/y.diagram.json');
    expect(t.engine.status().unsaved).toBe(false);
  });
});
