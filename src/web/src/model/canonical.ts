import type { DiagramDocument, Element } from '../contracts/generated';

/**
 * Deterministic serialization used for mutation-regression hashes: object keys sorted, exact validated
 * numbers/strings/arrays retained, undefined omitted. Derived data (routes, visual bounds) never enters the model,
 * so it cannot enter the hash. Engine-computed group bounds are ordinary canonical fields and are included.
 */
export function canonicalSerialize(value: DiagramDocument | Element | unknown): string {
  return JSON.stringify(sortKeys(value));
}

function sortKeys(v: unknown): unknown {
  if (Array.isArray(v)) return v.map(sortKeys);
  if (v && typeof v === 'object') {
    const o = v as Record<string, unknown>;
    const out: Record<string, unknown> = {};
    for (const k of Object.keys(o).sort()) if (o[k] !== undefined) out[k] = sortKeys(o[k]);
    return out;
  }
  return v;
}

export function deepFreeze<T>(value: T): T {
  if (value && typeof value === 'object' && !Object.isFrozen(value)) {
    Object.freeze(value);
    for (const v of Object.values(value as object)) deepFreeze(v);
  }
  return value;
}
