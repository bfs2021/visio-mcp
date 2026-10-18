import type { ResolvedDiff, TransactionRecord, TransactionSummary } from '../contracts/generated';

/**
 * Bounded transaction history for one session. `records` backs get_changes; the undo and redo stacks hold the
 * same records. Undo applies a record's stored inverse as a *new* revision, so revision counters never rewind.
 */
export class History {
  private records: TransactionRecord[] = [];
  private undoStack: TransactionRecord[] = [];
  private redoStack: TransactionRecord[] = [];
  private base: number;
  private evicted = false;

  constructor(startRevision: number, private readonly maxSummaries = 1000) {
    this.base = startRevision;
  }

  /** The oldest `sinceRevision` get_changes can still answer. */
  get earliestRetainedRevision(): number { return this.base; }
  get hasEvictedUndo(): boolean { return this.evicted; }
  get canUndo(): boolean { return this.undoStack.length > 0; }
  get canRedo(): boolean { return this.redoStack.length > 0; }

  /** A new human/agent edit: clears redo. */
  pushEdit(record: TransactionRecord): void {
    this.redoStack = [];
    this.undoStack.push(record);
    while (this.undoStack.length > this.maxSummaries) { this.undoStack.shift(); this.evicted = true; } // bounded separately from the change log
    this.append(record);
  }

  /** Records an undo or redo transaction in the change log without putting it on the undo stack. */
  pushReplay(record: TransactionRecord): void {
    this.append(record);
  }

  private append(record: TransactionRecord): void {
    this.records.push(record);
    while (this.records.length > this.maxSummaries) {
      this.base = this.records.shift()!.summary.revision;
    }
  }

  peekUndo(): TransactionRecord | undefined { return this.undoStack[this.undoStack.length - 1]; }
  peekRedo(): TransactionRecord | undefined { return this.redoStack[this.redoStack.length - 1]; }
  moveUndoToRedo(): void { const r = this.undoStack.pop(); if (r) this.redoStack.push(r); }
  moveRedoToUndo(): void { const r = this.redoStack.pop(); if (r) this.undoStack.push(r); }

  /** Hashes of pictures that undo or redo could bring back: every asset a stored diff registers or removes the registration of. */
  retainedAssetHashes(): string[] {
    const out = new Set<string>();
    for (const r of [...this.undoStack, ...this.redoStack]) for (const d of [r.forward, r.inverse]) for (const a of d.upserts.assets) out.add(a.sha256);
    return [...out];
  }

  /** The newest summaries, newest last. */
  recent(limit: number): TransactionSummary[] { return this.records.slice(-limit).map(r => r.summary); }

  since(revision: number): TransactionSummary[] | null {
    if (revision < this.base) return null;
    return this.records.filter(r => r.summary.revision > revision).map(r => r.summary);
  }

  /** Rewrites a stored diff for replay under a new transaction identity and revision. */
  static asReplay(diff: ResolvedDiff, transactionId: string, source: 'undo' | 'redo', previousRevision: number): ResolvedDiff {
    return { ...diff, transactionId, source, previousRevision, revision: previousRevision + 1 };
  }
}
