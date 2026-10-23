import { describe, expect, it } from 'vitest';
import { clipboardPasteAction } from '../src/commands/clipboardPaste';

describe('clipboard paste precedence', () => {
  it('prefers the matching in-memory shape or group copy across tabs', () => {
    expect(clipboardPasteAction({ sequence: 4, internalCopyId: 'copy-4', hasBitmap: false, hasText: false }, 'copy-4', true)).toBe('internal');
  });

  it('uses a newer external bitmap instead of stale in-memory shapes', () => {
    expect(clipboardPasteAction({ sequence: 5, internalCopyId: null, hasBitmap: true, hasText: false }, 'copy-4', true)).toBe('image');
  });

  it('does not treat W08 Copy as image as an internal shape copy', () => {
    expect(clipboardPasteAction({ sequence: 6, internalCopyId: null, hasBitmap: true, hasText: true }, 'copy-4', true)).toBe('image');
  });

  it('leaves unsupported external text alone', () => {
    expect(clipboardPasteAction({ sequence: 7, internalCopyId: null, hasBitmap: false, hasText: true }, 'copy-4', true)).toBe('text');
  });

  it('falls back to the in-memory copy when the native clipboard is unavailable', () => {
    expect(clipboardPasteAction(null, 'copy-4', true)).toBe('internal');
  });
});
