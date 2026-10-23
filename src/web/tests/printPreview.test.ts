import { expect, it } from 'vitest';
import { EditorModel, noHost } from '../src/editor/EditorModel';
import { createTestEngine } from './support/engine';
import { ok } from '../src/model/result';

it('pins print options, invalidates prepared sheets on changes, and consumes output once', async () => {
  const t = createTestEngine();
  const model = new EditorModel(t.engine);
  const calls: Array<{ action: string; params?: Record<string, unknown> }> = [];
  model.host = {
    ...noHost,
    editorPrint: async (action, params) => {
      calls.push({ action, params });
      if (action === 'devices') return ok({ devices: [{ id: 'printer-a', label: 'Printer A', media: [{ id: 'letter', label: 'Letter', widthPt: 612, heightPt: 792, minimumMargins: { leftPt: 0, topPt: 0, rightPt: 0, bottomPt: 0 } }] }] });
      if (action === 'preview') return ok({ jobId: 'opaque-job', sheets: [{ sheetNumber: 1, paperWidthPt: 612, paperHeightPt: 792, sourcePageNumbers: [1], svg: '<svg/>' }], marginWarnings: [] });
      return ok({ printed: true });
    },
  };
  model.beginPrint();
  await new Promise(resolve => setTimeout(resolve, 0));
  expect(model.printPrompt?.options.expected).toEqual({ ...t.scope(), baseRevision: 0 });
  expect(model.printPrompt?.options.printerId).toBe('printer-a');
  await model.preparePrint();
  expect(model.printPrompt?.jobId).toBe('opaque-job');
  expect(model.printPrompt?.sheets).toHaveLength(1);
  model.changePrintOptions({ layout: 'tile' });
  expect(model.printPrompt?.jobId).toBeNull();
  expect(model.printPrompt?.sheets).toEqual([]);
  await model.preparePrint();
  await model.finishPrint('print');
  expect(model.printPrompt).toBeNull();
  expect(calls.map(x => x.action)).toEqual(['devices', 'preview', 'preview', 'print']);
  expect(calls.at(-1)?.params).toEqual({ jobId: 'opaque-job' });
});

it('refuses output if the drawing revision changes after preview', async () => {
  const t = createTestEngine();
  const model = new EditorModel(t.engine);
  const calls: string[] = [];
  model.host = {
    ...noHost,
    editorPrint: async action => {
      calls.push(action);
      if (action === 'devices') return ok({ devices: [{ id: 'printer-a', label: 'Printer A', media: [{ id: 'letter', label: 'Letter', widthPt: 612, heightPt: 792, minimumMargins: { leftPt: 0, topPt: 0, rightPt: 0, bottomPt: 0 } }] }] });
      return ok({ jobId: 'opaque-job', sheets: [{ sheetNumber: 1, paperWidthPt: 612, paperHeightPt: 792, sourcePageNumbers: [1], svg: '<svg/>' }] });
    },
  };
  model.beginPrint();
  await new Promise(resolve => setTimeout(resolve, 0));
  await model.preparePrint();
  await t.engine.execute(t.request([{ op: 'setPage', pageId: t.ids.page, patch: { name: 'Updated page' } }]), 'mcp');
  await model.finishPrint('print');
  expect(model.printPrompt?.jobId).toBeNull();
  expect(model.printPrompt?.error).toContain('drawing changed');
  expect(calls).toEqual(['devices', 'preview']);
});
