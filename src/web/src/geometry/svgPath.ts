import { fail, ok, type Result } from '../model/result';

export type PathStep = { op: 'M' | 'L'; x: number; y: number } | { op: 'Z' };
const cache = new Map<string, Result<PathStep[]>>();

/** Canonical paths use a 100×100 box. Match Visio's M/L/H/V/C/Q/Z subset and 16-segment curve approximation. */
export function parsePath(path: string): Result<PathStep[]> {
  const old = cache.get(path); if (old) return old;
  const result = parse(path);
  if (cache.size >= 128) cache.delete(cache.keys().next().value!);
  cache.set(path, result); return result;
}
function parse(path: string): Result<PathStep[]> {
  const bad = (reason: string) => fail('invalid_request', `Unsupported custom path: ${reason}. Use M, L, H, V, C, Q and Z in a 100×100 box.`);
  if (!path.length || path.length > 65536) return bad('path length must be 1–65536 characters');
  const tokens: (string | number)[] = [];
  const re = /[a-zA-Z]|[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?/gy;
  let p = 0;
  while (p < path.length) {
    if (/[\s,]/.test(path[p]!)) { p++; continue; }
    re.lastIndex = p; const m = re.exec(path); if (!m) return bad(`invalid token at character ${p}`);
    const text = m[0]; const value = /^[a-zA-Z]$/.test(text) ? text : Number(text);
    if (typeof value === 'number' && (!Number.isFinite(value) || Math.abs(value) > 1e6)) return bad('nonfinite or excessive coordinate');
    tokens.push(value); p = re.lastIndex;
  }
  const out: PathStep[] = [];
  let i = 0, cmd = '', x = 0, y = 0, sx = 0, sy = 0, drawn = false;
  const push = (op: 'M' | 'L', a: number, b: number) => { out.push({ op, x: a, y: b }); x = a; y = b; };
  while (i < tokens.length) {
    if (typeof tokens[i] === 'string') cmd = tokens[i++] as string;
    if (!'MLHV CQZmlhvcqz'.replace(/ /g, '').includes(cmd) || !cmd) return bad(`command ${cmd || '(missing)'}`);
    if (!out.length && cmd.toUpperCase() !== 'M') return bad('the first command must be M');
    const upper = cmd.toUpperCase(), relative = cmd !== upper;
    if (upper === 'Z') { out.push({ op: 'Z' }); x = sx; y = sy; cmd = ''; continue; }
    const count = ({ M: 2, L: 2, H: 1, V: 1, C: 6, Q: 4 } as Record<string, number>)[upper]!;
    const args = tokens.slice(i, i + count); if (args.length !== count || args.some(a => typeof a !== 'number')) return bad(`missing numbers for ${cmd}`);
    i += count;
    const n = args as number[], x0 = x, y0 = y;
    if (relative) {
      for (let j = 0; j < n.length; j++) n[j] = n[j]! + (upper === 'V' ? y0 : upper === 'H' ? x0 : j % 2 === 0 ? x0 : y0);
    }
    if (upper === 'M') { push('M', n[0]!, n[1]!); sx = x; sy = y; cmd = relative ? 'l' : 'L'; }
    else if (upper === 'L') { push('L', n[0]!, n[1]!); drawn = true; }
    else if (upper === 'H') { push('L', n[0]!, y); drawn = true; }
    else if (upper === 'V') { push('L', x, n[0]!); drawn = true; }
    else {
      for (let k = 1; k <= 16; k++) {
        const t = k / 16, u = 1 - t;
        if (upper === 'C') push('L', u ** 3 * x0 + 3 * u * u * t * n[0]! + 3 * u * t * t * n[2]! + t ** 3 * n[4]!, u ** 3 * y0 + 3 * u * u * t * n[1]! + 3 * u * t * t * n[3]! + t ** 3 * n[5]!);
        else push('L', u * u * x0 + 2 * u * t * n[0]! + t * t * n[2]!, u * u * y0 + 2 * u * t * n[1]! + t * t * n[3]!);
      }
      drawn = true;
    }
    if (out.length > 32768 || Math.abs(x) > 1e6 || Math.abs(y) > 1e6) return bad('path exceeds the coordinate or segment limit');
  }
  return drawn ? ok(out) : bad('path draws nothing');
}
