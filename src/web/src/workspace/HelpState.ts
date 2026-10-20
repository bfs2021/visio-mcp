import type { Result } from '../model/result';

export type HelpPanel = 'shortcuts' | 'about' | 'palette';

export interface AboutInfo {
  name: string;
  version: string;
  build: string;
  runtime: string;
  protocolVersion: number;
  companions: Array<{ label: string; version: string | null }>;
  folders: Array<{ label: string; path: string }>;
}

/** Which Help panel is showing, and what the About box learned from the host. The window owns one; the panels only draw it. */
export class HelpState {
  panel: HelpPanel | null = null;
  about: AboutInfo | null = null;
  aboutError: string | null = null;

  constructor(private readonly load: () => Promise<Result<AboutInfo>>, private readonly changed: () => void) {}

  async show(panel: HelpPanel): Promise<void> {
    this.panel = panel;
    this.changed();
    if (panel !== 'about') return;
    this.about = null; this.aboutError = null;
    const r = await this.load();
    if (this.panel !== 'about') return; // closed while loading
    if (r.ok) this.about = r.value; else this.aboutError = r.error.message;
    this.changed();
  }

  close(): void { if (this.panel) { this.panel = null; this.changed(); } }
  toggle(panel: HelpPanel): void { if (this.panel === panel) this.close(); else void this.show(panel); }
}
