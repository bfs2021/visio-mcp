import type { CapturedDocumentDefaults, DiagramDocument, TextBlock } from '../contracts/generated';
import * as V from './validators.generated.js';
import { captureDefaults, defaultPreferences } from './preferences';

export const DOCUMENT_DEFAULTS_KEY = 'editor.defaults.v1';
const MAX_CAPTURE_BYTES = 4096;
const legacyDefaults = (): CapturedDocumentDefaults => captureDefaults(defaultPreferences());

/** Captures only drawing defaults; document metadata is a bounded, stable JSON value. */
export function serializeDocumentDefaults(defaults: CapturedDocumentDefaults): string {
  if (!V.CapturedDocumentDefaults(defaults) || !Number.isFinite(defaults.grid.spacingPt) || !Number.isFinite(defaults.font.sizePt) || defaults.font.family.trim() !== defaults.font.family) {
    throw new RangeError('Invalid captured document defaults');
  }
  const value = JSON.stringify({
    grid: { spacingPt: defaults.grid.spacingPt, visible: defaults.grid.visible, snap: defaults.grid.snap },
    font: { family: defaults.font.family, sizePt: defaults.font.sizePt },
  });
  if (new TextEncoder().encode(value).byteLength > MAX_CAPTURE_BYTES) throw new RangeError('Captured document defaults exceed 4096 UTF-8 bytes');
  return value;
}

/** A read-only fallback: never repairs or inserts metadata in an opened drawing. */
export function readDocumentDefaults(document: DiagramDocument): { defaults: CapturedDocumentDefaults; diagnostic?: string } {
  const raw = document.metadata[DOCUMENT_DEFAULTS_KEY];
  if (raw === undefined) return { defaults: legacyDefaults() };
  if (new TextEncoder().encode(raw).byteLength > MAX_CAPTURE_BYTES) return { defaults: legacyDefaults(), diagnostic: 'Captured document defaults exceed 4096 UTF-8 bytes' };
  try {
    const value: unknown = JSON.parse(raw);
    if (V.CapturedDocumentDefaults(value)) {
      const defaults = value as CapturedDocumentDefaults;
      if (Number.isFinite(defaults.grid.spacingPt) && Number.isFinite(defaults.font.sizePt) && defaults.font.family.trim() === defaults.font.family) return { defaults: structuredClone(defaults) };
    }
  } catch { /* preserve malformed source */ }
  return { defaults: legacyDefaults(), diagnostic: 'Captured document defaults are malformed or invalid' };
}

export function textDefaults(document: DiagramDocument): TextBlock {
  const { font } = readDocumentDefaults(document).defaults;
  return {
    value: '', fontFamily: font.family, fontSizePt: font.sizePt, bold: false, italic: false, underline: false, colour: '#000000',
    horizontalAlign: 'center', verticalAlign: 'middle', wrap: true, paddingPt: 4,
  };
}
