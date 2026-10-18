import type { Paint } from '../contracts/generated';

/** Why a paint cannot be used where it is, or null. Gradients are fills only, and their stops must rise from 0 to 1. */
export function paintProblem(paint: Paint | undefined, where: 'fill' | 'stroke' | 'background'): string | null {
  if (!paint || paint.kind !== 'linear-gradient') return null;
  if (where !== 'fill') return `a gradient can only fill a shape: ${where === 'stroke' ? 'strokes' : 'page backgrounds'} are solid or none`;
  for (let i = 1; i < paint.stops.length; i++) if (paint.stops[i]!.offset < paint.stops[i - 1]!.offset) return 'gradient stops must be in order, with offsets rising from 0 to 1';
  return null;
}
