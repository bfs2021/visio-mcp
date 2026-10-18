import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';

const root = join(__dirname, '..', 'src');
function* files(dir: string): Generator<string> {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) yield* files(p);
    else if (/\.(ts|tsx)$/.test(name) && !p.includes(join('src', 'contracts'))) yield p;
  }
}
const hits = (pattern: RegExp) => [...files(root)].flatMap(f => readFileSync(f, 'utf8').split('\n').map((text, i) => ({ f: relative(root, f).replace(/\\/g, '/'), line: i + 1, text })).filter(l => pattern.test(l.text) && !/^\s*(\/\/|\*|\/\*)/.test(l.text)));

describe('the page never reaches out on its own', () => {
  it('has no way to open windows, navigate, or run text as code', () => {
    const found = hits(/window\.open|\blocation\.(href|assign|replace)\s*=|target=["']_blank|\beval\(|new Function\(|(await|=|return)\s+import\(|document\.write/);
    expect(found, JSON.stringify(found)).toEqual([]);
  });

  it('opens no network connection except reading approved pictures from its own origin', () => {
    const found = hits(/\bfetch\(|XMLHttpRequest|new WebSocket|new EventSource|sendBeacon/);
    expect(found.map(f => f.f)).toEqual(['render/SnapshotRenderer.ts']); // a picture is fetched by its hash from the host origin, to be drawn into an export
    expect(found[0]!.text).toContain('deps.fetchAsset ?? ((u: string) => fetch(u))');
  });

  it('treats hyperlinks as plain data: nothing renders a link from document content', () => {
    const found = hits(/<a\s[^>]*href=\{|dangerouslySetInnerHTML|innerHTML\s*=/).filter(l => !/innerHTML\s*=\s*['"]{2}\s*;/.test(l.text)); // clearing a container is fine; filling one from content is not
    expect(found).toEqual([]);
  });

  it('keeps unsafe-eval out of the content security policy', () => {
    const html = readFileSync(join(root, '..', 'index.html'), 'utf8');
    expect(html).not.toMatch(/unsafe-eval/);
  });
});
