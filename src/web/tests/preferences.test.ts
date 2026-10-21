import { describe, expect, it } from 'vitest';
import { defaultPreferences, validatePreferences, captureDefaults } from '../src/model/preferences';
import { createPreferencesDefaults } from '../src/contracts/generated';

describe('preferences canonical contract', () => {
  it('returns independent generated defaults for callers that mutate nested settings', () => {
    const first = createPreferencesDefaults();
    first.grid.visible = false;
    first.font.family = 'Custom';
    const second = createPreferencesDefaults();
    expect(second.grid.visible).toBe(true);
    expect(second.font.family).toBe('Segoe UI');
    expect(defaultPreferences()).toEqual(second);
  });
  it('supplies independent legacy defaults and captured copies', () => {
    const p = defaultPreferences();
    expect(p).toEqual({ schemaVersion: 1, units: 'mm', grid: { spacingPt: 72 * 5 / 25.4, visible: true, snap: true }, font: { family: 'Segoe UI', sizePt: 12 }, recovery: { checkpointSeconds: 60 }, theme: 'light' });
    const capture = captureDefaults(p);
    p.grid.visible = false;
    p.font.family = 'Changed';
    expect(capture.grid.visible).toBe(true);
    expect(capture.font.family).toBe('Segoe UI');
    expect(defaultPreferences().grid.visible).toBe(true);
    expect(Object.keys(capture).sort()).toEqual(['font', 'grid']);
  });
  it.each([
    ['schemaVersion', 2], ['schemaVersion', '1'], ['units', 'feet'], ['theme', 'system'], ['extra', true],
    ['grid.spacingPt', 0.09], ['grid.spacingPt', 1440.01], ['grid.spacingPt', NaN], ['grid.spacingPt', Infinity], ['grid.spacingPt', '5'],
    ['grid.visible', 1], ['grid.snap', 'true'], ['grid.extra', 1],
    ['font.family', ''], ['font.family', '   '], ['font.family', 'x'.repeat(129)], ['font.family', 42],
    ['font.sizePt', 0], ['font.sizePt', 1001], ['font.sizePt', -Infinity],
    ['recovery.checkpointSeconds', 9], ['recovery.checkpointSeconds', 3601], ['recovery.checkpointSeconds', 10.5], ['recovery.checkpointSeconds', '60'],
  ])('rejects %s = %s', (path, value) => {
    const p: any = defaultPreferences();
    const keys = path.split('.');
    if (keys.length === 2) p[keys[0]!][keys[1]!] = value; else p[path] = value;
    const result = validatePreferences(p);
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.error.code).toBe('invalid_request');
  });
  it.each([null, [], {}, true, 'settings'])('rejects malformed root %s', value => expect(validatePreferences(value).ok).toBe(false));
  it('normalizes family before bounds, accepts unavailable names and boundary values', () => {
    for (const [spacingPt, sizePt, checkpointSeconds] of [[0.1, 1, 10], [1440, 1000, 3600]]) {
      const p = defaultPreferences();
      p.grid.spacingPt = spacingPt!; p.font.sizePt = sizePt!; p.recovery.checkpointSeconds = checkpointSeconds!;
      p.font.family = '  ' + 'x'.repeat(128) + '  ';
      const result = validatePreferences(p);
      expect(result.ok).toBe(true);
      if (result.ok) expect(result.value.font.family).toBe('x'.repeat(128));
      expect(p.font.family.startsWith('  ')).toBe(true);
    }
  });
});

// Shared JSON fixtures catch cross-language normalization and numeric representation differences.
import boundaries from '../../../contracts/fixtures/preferences-boundaries.json';
it('agrees with host on shared boundary fixtures', () => {
  for (const [group, cases] of Object.entries(boundaries)) for (const fixture of cases) {
    const p: any = defaultPreferences();
    const keys = fixture.path.split('.');
    if (keys.length === 2) p[keys[0]!][keys[1]!] = fixture.value; else p[keys[0]!] = fixture.value;
    const result = validatePreferences(p);
    expect(result.ok, JSON.stringify(fixture)).toBe(group === 'valid');
    if (result.ok && 'normalized' in fixture) expect(result.value.font.family).toBe(fixture.normalized);
  }
});
