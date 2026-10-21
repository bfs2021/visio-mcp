import type { AppPreferences } from '../contracts/generated';
import type { PreferencesService, PreferencesSnapshot } from '../bridge/initializePreferences';
import { captureDefaults, defaultPreferences, validatePreferences } from '../model/preferences';
import { fail, type Result } from '../model/result';

/** Effective host settings and a disposable dialog draft. Existing drawings never enter this model. */
export class PreferencesModel {
  private value: AppPreferences;
  private readonly listeners = new Set<() => void>();
  private service?: PreferencesService;
  draft: AppPreferences | null = null;
  dialogOpen = false;
  draftEpoch = 0;
  errors: Record<string, string> = {};
  busy = false;
  conflicted = false;
  installedFonts: string[] = [];
  onSaved: (preferences: AppPreferences) => void = () => {};
  private resetInvalid = false;

  constructor(initial: AppPreferences = defaultPreferences(), public versionTag = 'missing', public diagnostic: string | null = null, service?: PreferencesService) {
    const result = validatePreferences(initial);
    this.value = result.ok ? result.value : defaultPreferences();
    this.service = service;
  }
  get current(): AppPreferences { return structuredClone(this.value); }
  snapshotDefaults = () => captureDefaults(this.value);
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private changed(): void { for (const listener of this.listeners) listener(); }
  useService(service: PreferencesService): void { this.service = service; }

  /** Apply a value only after persistence, or when explicitly loading the host's current value. */
  apply(value: AppPreferences, versionTag = this.versionTag): void {
    const result = validatePreferences(value);
    if (!result.ok) throw new Error(result.error.message);
    this.value = result.value; this.versionTag = versionTag;
    this.changed();
  }

  async open(): Promise<void> {
    if (this.busy || this.dialogOpen) return;
    this.dialogOpen = true; this.changed();
    await this.read();
  }
  async reload(): Promise<void> {
    if (this.busy) return;
    await this.read();
  }
  private async read(): Promise<void> {
    this.busy = true; this.errors = {}; this.changed();
    try {
      const read = this.service ? await this.service.get() : { ok: true as const, value: { preferences: this.current, versionTag: this.versionTag, diagnostic: this.diagnostic } };
      if (!read.ok) { this.errors.form = read.error.message; this.draft ??= this.current; return; }
      const valid = validatePreferences(read.value.preferences);
      if (!valid.ok) { this.errors.form = valid.error.message; this.draft ??= this.current; return; }
      this.apply(valid.value, read.value.versionTag);
      this.diagnostic = read.value.diagnostic ?? null;
      this.installedFonts = [...(read.value.installedFonts ?? [])];
      this.draft = structuredClone(valid.value);
      this.draftEpoch++;
      this.conflicted = false; this.resetInvalid = false;
    } catch (error) {
      this.errors.form = error instanceof Error ? error.message : String(error);
      this.draft ??= this.current;
    } finally { this.busy = false; this.changed(); }
  }
  edit(patch: Partial<AppPreferences>): void {
    if (!this.draft || this.busy) return;
    this.draft = { ...this.draft, ...structuredClone(patch) };
    this.errors = {}; this.changed();
  }
  resetDraft(): void {
    if (!this.draft || this.busy) return;
    this.draft = defaultPreferences(); this.draftEpoch++; this.errors = {}; this.conflicted = false; this.resetInvalid = true; this.changed();
  }
  cancel(): void {
    if (this.busy) return;
    this.draft = null; this.dialogOpen = false; this.errors = {}; this.conflicted = false; this.resetInvalid = false; this.changed();
  }
  async save(): Promise<Result<PreferencesSnapshot>> {
    if (!this.draft || this.busy) return fail('invalid_request', 'Preferences are not ready to save');
    const valid = validatePreferences(this.draft);
    if (!valid.ok) {
      const path = valid.error.details?.path;
      this.errors = { [path === '/font/family' || path === '/font/sizePt' ? 'font' : path === '/grid/spacingPt' ? 'grid' : 'form']: valid.error.message };
      this.changed(); return valid;
    }
    if (!this.service) return fail('not_ready', 'Preferences storage is unavailable');
    this.busy = true; this.errors = {}; this.changed();
    try {
      const saved = await this.service.save(valid.value, this.versionTag, this.resetInvalid);
      if (!saved.ok) {
        this.conflicted = saved.error.code === 'revision_conflict' && saved.error.details?.reason === 'preferences_changed';
        this.errors.form = saved.error.message;
        return saved;
      }
      this.apply(saved.value.preferences, saved.value.versionTag);
      this.diagnostic = saved.value.diagnostic ?? null;
      this.draft = null; this.dialogOpen = false; this.conflicted = false; this.resetInvalid = false;
      this.onSaved(this.current);
      return saved;
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      this.errors.form = message;
      return fail('io_error', message);
    } finally { this.busy = false; this.changed(); }
  }
}
