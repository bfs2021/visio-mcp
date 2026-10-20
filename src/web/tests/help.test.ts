import { describe, expect, it } from 'vitest';
import { filterCommands, fixedShortcuts, type AppCommand } from '../src/editor/appCommands';
import { HelpState, type AboutInfo } from '../src/workspace/HelpState';
import { fail, ok } from '../src/model/result';

const cmd = (id: string, label: string, group: AppCommand['group'] = 'File', shortcut?: string): AppCommand => ({ id, label, group, run: () => {}, ...(shortcut ? { shortcut } : {}) });
const list = [cmd('new', 'New…', 'File', 'Ctrl+N'), cmd('open', 'Open…'), cmd('open-visio', 'Open Visio drawing…'), cmd('export', 'Export to Visio (.vsdx)…'), cmd('grid', 'Show grid', 'View'), cmd('snap', 'Snap to grid', 'View')];

describe('command palette search', () => {
  it('lists everything for an empty query, in menu order', () => {
    expect(filterCommands(list, '  ').map(c => c.id)).toEqual(list.map(c => c.id));
  });

  it('needs every typed word, ignores case, and also matches the menu name', () => {
    expect(filterCommands(list, 'VISIO').map(c => c.id)).toEqual(['open-visio', 'export']);
    expect(filterCommands(list, 'view grid').map(c => c.id)).toEqual(['grid', 'snap']);
    expect(filterCommands(list, 'zzz')).toEqual([]);
  });

  it('puts names that start with the query first, then word starts, then the rest', () => {
    const c = [cmd('a', 'Snap to grid', 'View'), cmd('b', 'Show grid', 'View'), cmd('c', 'Regrid', 'View')];
    expect(filterCommands(c, 'grid').map(x => x.id)).toEqual(['a', 'b', 'c']); // 'grid' is a word start in a and b (by order), a substring in c
    expect(filterCommands(c, 'sh').map(x => x.id)).toEqual(['b']);
    expect(filterCommands(list, 'open').map(x => x.id)).toEqual(['open', 'open-visio']);
  });
});

describe('fixed shortcuts', () => {
  it('lists the keys that are not menu commands, each once', () => {
    const keys = fixedShortcuts.map(f => f.shortcut);
    expect(new Set(keys).size).toBe(keys.length);
    expect(keys).toEqual(expect.arrayContaining(['Esc', 'Ctrl+Tab', 'Ctrl+PageDown', 'Arrow keys']));
  });
});

const info: AboutInfo = { name: 'Agentic Diagram', version: '1.2.3', build: 'abc', runtime: '.NET 10', protocolVersion: 1, companions: [], folders: [{ label: 'Recovery', path: 'C:/x' }] };

describe('help panels', () => {
  it('shows one panel at a time and toggles the palette', () => {
    let changes = 0;
    const h = new HelpState(async () => ok(info), () => { changes++; });
    void h.show('shortcuts');
    expect(h.panel).toBe('shortcuts');
    h.toggle('palette');
    expect(h.panel).toBe('palette');
    h.toggle('palette');
    expect(h.panel).toBeNull();
    expect(changes).toBeGreaterThanOrEqual(3);
  });

  it('asks the host for About each time it opens, and says so when the host cannot answer', async () => {
    let asked = 0;
    const h = new HelpState(async () => (++asked === 1 ? ok(info) : fail('not_running', 'there is no host')), () => {});
    await h.show('about');
    expect(h.about?.version).toBe('1.2.3');
    h.close();
    await h.show('about');
    expect(h.about).toBeNull();
    expect(h.aboutError).toBe('there is no host');
    expect(asked).toBe(2);
  });

  it('drops a late About answer if the panel was closed meanwhile', async () => {
    let release!: () => void;
    const h = new HelpState(() => new Promise(r => { release = () => r(ok(info)); }), () => {});
    const shown = h.show('about');
    h.close();
    release();
    await shown;
    expect(h.about).toBeNull();
  });
});
