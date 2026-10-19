import type { AppPreferences } from '../contracts/generated';
import { captureDefaults, defaultPreferences, validatePreferences } from '../model/preferences';

/** Effective settings only. Call apply after host persistence succeeds. */
export class PreferencesModel {
  private value: AppPreferences;
  private readonly listeners = new Set<() => void>();
  constructor(initial: AppPreferences = defaultPreferences(), public versionTag = 'missing', public diagnostic: string | null = null) {
    const result = validatePreferences(initial);
    this.value = result.ok ? result.value : defaultPreferences();
  }
  get current(): AppPreferences { return structuredClone(this.value); }
  snapshotDefaults = () => captureDefaults(this.value);
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  apply(value: AppPreferences, versionTag = this.versionTag): void {
    const result = validatePreferences(value);
    if (!result.ok) throw new Error(result.error.message);
    this.value = result.value; this.versionTag = versionTag;
    for (const listener of this.listeners) listener();
  }
}
