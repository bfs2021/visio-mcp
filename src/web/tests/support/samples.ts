import { readFileSync } from 'node:fs';

type Json = unknown;
const raw = JSON.parse(readFileSync(new URL('../../../../contracts/fixtures/samples.json', import.meta.url), 'utf8')) as Record<string, Json>;

function get(value: Json, path: string): Json {
  return path.split('/').reduce<Json>((v, k) => (v as Record<string, Json>)[k], value);
}

/** Resolves {"$sample": "Name", "path"?: "a/0/b"} placeholders against the other samples. */
function resolve(value: Json): Json {
  if (Array.isArray(value)) return value.map(resolve);
  if (value && typeof value === 'object') {
    const o = value as Record<string, Json>;
    if (typeof o.$sample === 'string') {
      const target = resolve(raw[o.$sample]);
      return typeof o.path === 'string' ? get(target, o.path) : target;
    }
    return Object.fromEntries(Object.entries(o).map(([k, v]) => [k, resolve(v)]));
  }
  return value;
}

export const samples: Record<string, Json> = Object.fromEntries(
  Object.entries(raw).filter(([k]) => k !== '$comment').map(([k, v]) => [k, resolve(v)]),
);
