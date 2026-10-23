export interface ClipboardDescriptor {
  sequence: number;
  internalCopyId: string | null;
  hasBitmap: boolean;
  hasText: boolean;
}

export type ClipboardPasteAction = 'internal' | 'image' | 'text' | 'none';

/** Decide which paste source owns this explicit Ctrl+V gesture. */
export function clipboardPasteAction(
  descriptor: ClipboardDescriptor | null,
  copyId: string | null,
  hasInternalCopy: boolean,
): ClipboardPasteAction {
  if (descriptor?.internalCopyId && descriptor.internalCopyId === copyId && hasInternalCopy) return 'internal';
  if (descriptor?.hasBitmap) return 'image';
  if (descriptor?.hasText) return 'text';
  if (!descriptor && hasInternalCopy) return 'internal';
  return 'none';
}
