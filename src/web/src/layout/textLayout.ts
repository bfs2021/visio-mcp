import type { Bounds, Operation, Snapshot, TextBlock } from '../contracts/generated';
import { readObjects } from '../bridge/readTools';
import { parentMap } from '../commands/groups';
import { effectiveState } from '../canvas/cellMapping';
import { visualBounds } from '../commands/geometry';
import { fail, ok, type Result } from '../model/result';
import { measureTextBox, TextMeasurementLimitError, type TextMeasurement, type TextWidthMeasurer } from './textMeasurement';

interface LayoutItem {
  id: string; pageId: string; bounds: Bounds; fontSizePt: number;
  current: TextMeasurement; recommended?: TextMeasurement; recommendedFontSizePt?: number;
  status: 'fits' | 'proposal' | 'cannot_fit' | 'locked'; warnings: string[];
}
interface TextLayoutReport {
  documentId: string; sessionId: string; revision: number;
  target: 'editor' | 'visio'; strategy: 'grow_box' | 'shrink_font';
  items: LayoutItem[]; operations: Operation[]; notes: string[];
}

/** A read of one committed snapshot; proposals are ordinary revision-checked operations, never implicit mutations. */
export function getTextLayout(snapshot: Snapshot, ctx: { measureWidth: TextWidthMeasurer }, params: Record<string, unknown>): Result<TextLayoutReport> {
  if (Object.keys(params).some(k => !['ids', 'pageId', 'strategy', 'target', 'minFontSizePt'].includes(k))) return fail('invalid_request', 'Unknown text layout option');
  const { ids, pageId, strategy = 'grow_box', target = 'editor', minFontSizePt = 8 } = params;
  if (!Array.isArray(ids) || ids.length < 1 || ids.length > 50 || ids.some(i => typeof i !== 'string' || !i || i.length > 256)) return fail('invalid_request', 'ids must contain 1 to 50 nonempty UUIDs, aliases or names');
  if (pageId !== undefined && typeof pageId !== 'string') return fail('invalid_request', 'pageId must be a page UUID');
  if (strategy !== 'grow_box' && strategy !== 'shrink_font') return fail('invalid_request', 'strategy must be grow_box or shrink_font');
  if (target !== 'editor' && target !== 'visio') return fail('invalid_request', 'target must be editor or visio');
  if (typeof minFontSizePt !== 'number' || !Number.isFinite(minFontSizePt) || minFontSizePt <= 0 || minFontSizePt > 1000) return fail('invalid_request', 'minFontSizePt must be greater than 0 and at most 1000');
  const objects = readObjects(snapshot, ids as string[], pageId);
  if (!objects.ok) return objects;
  const selected = [...new Map(objects.value.map(e => [e.id, e])).values()];
  if (selected.some(e => e.kind !== 'text' && (e.kind !== 'shape' || !e.text))) return fail('invalid_request', 'Text layout supports only text objects and shapes with text; choose their UUIDs rather than a group, image or connector');
  if (selected.reduce((n, e) => n + (e.kind === 'text' || e.kind === 'shape' ? e.text?.value.length ?? 0 : 0), 0) > 131072) return fail('limit_exceeded', 'Measure at most 131072 text characters per request');
  const report: TextLayoutReport = {
    documentId: snapshot.documentId, sessionId: snapshot.sessionId, revision: snapshot.revision, strategy, target, items: [], operations: [],
    notes: [
      'Read-only proposals from the returned revision. Apply operations with that baseRevision and current document/session identity; remeasure after any conflicting edit.',
      'Widths use editor font advances; line height is estimated at 1.2 em. Missing fonts and different renderers may change layout. Inspect and render after applying; growth can overlap neighbouring objects.',
      ...(target === 'visio' ? ['Conservative Visio estimate: at least one em per Unicode symbol, then 10% extra advance and line height plus 2 points extra inset per side. This is not a guarantee of native Visio font or wrapping behavior; verify the exported file. Line counts follow the editor wrap setting; Visio may wrap even when editor wrap is false.'] : []),
    ],
  };
  let measuredCharacters = 0;
  const boundedMeasure: TextWidthMeasurer = (value, font) => {
    measuredCharacters += value.length;
    if (measuredCharacters > 8000000) throw new TextMeasurementLimitError('Text measurement exceeds the 8000000-character work budget; request fewer labels');
    return ctx.measureWidth(value, font);
  };
  const roundUp = (n: number) => n > 1e12 ? n : Math.ceil((n - 1e-9) * 1000) / 1000;
  try {
    for (const el of selected) {
      const text = (el.kind === 'text' || el.kind === 'shape' ? el.text : undefined)!;
      const page = snapshot.document.pages.find(p => p.elements.some(e => e.id === el.id))!;
      const measure = (t: TextBlock, b: Bounds) => measureTextBox(t, b.width, b.height, boundedMeasure, target);
      const current = measure(text, el.bounds);
      const item: LayoutItem = { id: el.id, pageId: page.id, bounds: { ...el.bounds }, fontSizePt: text.fontSizePt, current, status: current.fits ? 'fits' : 'proposal', warnings: [] };
      report.items.push(item);
      if (target === 'visio' && !text.wrap && !current.fits) item.warnings.push('VISIO_WRAP_RISK');
      if (target === 'visio' && /\p{S}/u.test(text.value)) item.warnings.push('SYMBOL_FONT_FALLBACK_RISK');
      if (current.fits) continue;
      const parents = parentMap(page.elements);
      let locked = effectiveState(el, page.layers).locked;
      for (let p = parents.get(el.id); p; p = parents.get(p)) locked ||= effectiveState(page.elements.find(e => e.id === p)!, page.layers).locked;
      if (locked) { item.status = 'locked'; item.warnings.push('OBJECT_LOCKED'); continue; }
      if (strategy === 'grow_box') {
        const bounds = { ...el.bounds, width: Math.max(el.bounds.width, roundUp(current.requiredWidthPt)) };
        bounds.height = Math.max(el.bounds.height, roundUp(measure(text, bounds).requiredHeightPt));
        item.recommended = measure(text, bounds);
        const box = visualBounds({ ...el, bounds });
        if (box.x < 0 || box.y < 0 || box.x + box.width > page.widthPt || box.y + box.height > page.heightPt) item.warnings.push('PROPOSAL_OUTSIDE_PAGE');
        report.operations.push({ op: 'resize', target: el.id, bounds: { width: bounds.width, height: bounds.height } });
      } else {
        const at = (size: number) => measure({ ...text, fontSizePt: size }, el.bounds);
        if (minFontSizePt > text.fontSizePt || !at(minFontSizePt).fits) { item.status = 'cannot_fit'; item.warnings.push('MIN_FONT_SIZE_REACHED'); continue; }
        let low = minFontSizePt, high = text.fontSizePt;
        for (let i = 0; i < 24; i++) { const mid = (low + high) / 2; if (at(mid).fits) low = mid; else high = mid; }
        const fontSizePt = Math.max(minFontSizePt, Math.floor(low * 1000) / 1000);
        item.recommendedFontSizePt = fontSizePt; item.recommended = at(fontSizePt);
        report.operations.push({ op: 'set', target: el.id, patch: { text: { fontSizePt } } });
      }
    }
  } catch (error) {
    if (error instanceof TextMeasurementLimitError) return fail('limit_exceeded', error.message);
    throw error;
  }
  return ok(report);
}
