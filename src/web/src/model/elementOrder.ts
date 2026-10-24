import type { Element } from '../contracts/generated';

/** An omitted level is the legacy/default stacking level. */
export function effectiveLevel(element: Pick<Element, 'level'>): number {
  return element.level ?? 0;
}

/** Compares drawable elements back-to-front without packing their independent ordering values. */
export function compareElementOrder(a: Pick<Element, 'level' | 'zIndex'>, b: Pick<Element, 'level' | 'zIndex'>): number {
  const levelA = effectiveLevel(a), levelB = effectiveLevel(b);
  if (levelA !== levelB) return levelA < levelB ? -1 : 1;
  if (a.zIndex === b.zIndex) return 0;
  return a.zIndex < b.zIndex ? -1 : 1;
}
