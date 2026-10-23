import type { AppPreferences, CapturedDocumentDefaults } from '../contracts/generated';
import { createPreferencesDefaults } from '../contracts/generated';
import * as V from './validators.generated.js';
import { fail, ok, type Result } from './result';

export function defaultPreferences(): AppPreferences { return createPreferencesDefaults(); }

export function validatePreferences(value: unknown): Result<AppPreferences> {
  // Copy only the branch requiring normalization; never alter the caller's draft.
  let normalized = value;
  if (value && typeof value === 'object' && !Array.isArray(value)) {
    const p = value as Record<string, unknown>;
    const font = p.font;
    if (font && typeof font === 'object' && !Array.isArray(font)) {
      const f = font as Record<string, unknown>;
      if (typeof f.family === 'string') normalized = { ...p, font: { ...f, family: f.family.trim() } };
    }
  }
  if (!V.AppPreferences(normalized)) {
    const e = V.AppPreferences.errors![0]!;
    return fail('invalid_request', `${e.instancePath} ${e.message ?? 'Invalid preferences'}`, { details: { path: e.instancePath, keyword: e.keyword } });
  }
  const p = normalized as AppPreferences;
  if (![p.grid.spacingPt, p.font.sizePt, p.recovery.checkpointSeconds].every(Number.isFinite)) return fail('invalid_request', 'Preferences numbers must be finite');
  return ok(structuredClone(p));
}

export function captureDefaults(p: AppPreferences): CapturedDocumentDefaults { return structuredClone({ grid: p.grid, font: p.font }); }
