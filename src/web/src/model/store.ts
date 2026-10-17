import type { DiagramDocument, Snapshot } from '../contracts/generated';
import { deepFreeze } from './canonical';

/**
 * Holds the committed document. Documents are immutable values: a commit replaces the root with a new tree that shares
 * every unchanged subtree with the previous one, so unchanged elements keep their identity. Only the command engine commits.
 */
export class DocumentStore {
  private doc: DiagramDocument;
  sessionId: string;

  constructor(document: DiagramDocument, sessionId: string) {
    this.doc = deepFreeze(structuredClone(document));
    this.sessionId = sessionId;
  }

  get documentId(): string { return this.doc.id; }
  get revision(): number { return this.doc.revision; }

  snapshot(): Snapshot {
    return { documentId: this.doc.id, sessionId: this.sessionId, revision: this.doc.revision, document: this.doc };
  }

  /** Engine-internal: starts a new session on another (or the same) document; used by open, replace and recovery. */
  replace(document: DiagramDocument, sessionId: string): void {
    this.doc = deepFreeze(structuredClone(document));
    this.sessionId = sessionId;
  }

  /** Engine-internal. Freezes only the parts of the new tree that are not already frozen. */
  commit(next: DiagramDocument): void {
    this.doc = deepFreeze(next);
  }
}

export function createDocumentStore(document: DiagramDocument, sessionId: string): DocumentStore {
  return new DocumentStore(document, sessionId);
}
