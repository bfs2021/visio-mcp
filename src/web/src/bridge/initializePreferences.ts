import type { AppPreferences } from '../contracts/generated';
import { defaultPreferences, validatePreferences } from '../model/preferences';
import { ok } from '../model/result';
import { PreferencesModel } from '../preferences/PreferencesModel';
import { BridgeClient, type WebViewLike } from './BridgeClient';
import { createHostServices } from './hostServices';

export interface PreferencesSnapshot { preferences: AppPreferences; versionTag: string; diagnostic?: string | null; installedFonts?: string[] }
export interface PreferencesService {
  get(): Promise<import('../model/result').Result<PreferencesSnapshot>>;
  save(preferences: AppPreferences, expectedVersionTag: string, resetInvalid?: boolean): Promise<import('../model/result').Result<PreferencesSnapshot>>;
}
/** Each browser-only view has isolated, deterministic settings; no browser persistence. */
export function memoryPreferencesService(): PreferencesService {
  let value = defaultPreferences(), version = 0;
  const snapshot = (): PreferencesSnapshot => ({ preferences: structuredClone(value), versionTag: `memory:${version}`, diagnostic: null, installedFonts: ['Arial', 'Consolas', 'Segoe UI'] });
  return {
    get: async () => ok(snapshot()),
    save: async (preferences, expectedVersionTag) => {
      const validated = validatePreferences(preferences);
      if (!validated.ok) return validated;
      if (expectedVersionTag !== `memory:${version}`) return { ok: false, error: { code: 'revision_conflict', message: 'Preferences changed. Reload before saving.', retryable: true, details: { reason: 'preferences_changed', ...snapshot() } } };
      value = validated.value; version++; return ok(snapshot());
    },
  };
}
/** Runs before either synchronous bootstrap. This client is retained, so responses and request ids never have competing listeners. */
export async function initializePreferences(webview?: WebViewLike) {
  const client = webview ? new BridgeClient(webview) : undefined;
  const host = client ? createHostServices(client) : undefined;
  const preferencesService: PreferencesService = host ? {
    get: () => host.call('host.preferences.get'),
    save: (preferences, expectedVersionTag, resetInvalid = false) => host.call('host.preferences.save', { preferences, expectedVersionTag, resetInvalid }),
  } : memoryPreferencesService();
  let preferences: PreferencesModel;
  try {
    const result = await preferencesService.get();
    if (!result.ok) throw new Error(result.error.message);
    const validated = validatePreferences(result.value.preferences);
    if (!validated.ok) throw new Error(validated.error.message);
    preferences = new PreferencesModel(validated.value, result.value.versionTag, result.value.diagnostic ?? null);
    preferences.installedFonts = [...(result.value.installedFonts ?? [])];
  } catch (error) {
    preferences = new PreferencesModel(defaultPreferences(), 'unreadable', `Preferences could not be loaded. Using defaults. ${error instanceof Error ? error.message : String(error)}`);
  }
  preferences.useService(preferencesService);
  return { ...(client ? { client } : {}), preferences, preferencesService };
}
