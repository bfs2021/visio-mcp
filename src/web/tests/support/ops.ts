import type { AppError, Element, GroupElement, Operation, ShapeElement, TransactionResult } from '../../src/contracts/generated';
import type { createTestEngine } from './engine';

export type TestEngine = ReturnType<typeof createTestEngine>;

/** Runs one atomic batch and returns the committed result; a failure throws with the engine's message. */
export async function run(t: TestEngine, operations: Operation[], pageId?: string): Promise<TransactionResult> {
  const r = await t.engine.execute(t.request(operations, pageId ? { pageId } : {}), 'mcp');
  if (!r.ok) throw new Error(`${r.error.code}: ${r.error.message}`);
  return r.value;
}

/** Runs a batch that must be refused, and asserts nothing was committed. */
export async function refused(t: TestEngine, operations: Operation[], pageId?: string): Promise<AppError> {
  const before = t.store.snapshot();
  const r = await t.engine.execute(t.request(operations, pageId ? { pageId } : {}), 'mcp');
  if (r.ok) throw new Error('expected the batch to be refused');
  if (t.store.snapshot().document !== before.document || t.store.snapshot().revision !== before.revision) throw new Error('a refused batch changed state');
  return r.error;
}

export const els = (t: TestEngine, pageIndex = 0): Element[] => t.store.snapshot().document.pages[pageIndex]!.elements;
export const byId = <T extends Element = Element>(t: TestEngine, id: string): T => {
  for (const p of t.store.snapshot().document.pages) for (const e of p.elements) if (e.id === id) return e as T;
  throw new Error(`no element ${id}`);
};
export const shape = (t: TestEngine, id: string): ShapeElement => byId<ShapeElement>(t, id);
export const group = (t: TestEngine, id: string): GroupElement => byId<GroupElement>(t, id);
export const has = (t: TestEngine, id: string): boolean => els(t).some(e => e.id === id);
export const id = (n: number): string => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
