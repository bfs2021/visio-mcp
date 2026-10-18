import { describe, expect, it } from 'vitest';
import { documentTitle, savedLine } from '../src/editor/documentTitle';

describe('document title', () => {
  it('shows the file name, a dot for unsaved work and Untitled for a document with no file', () => {
    expect(documentTitle({ savedPath: null, unsaved: false })).toBe('Untitled - Diagram');
    expect(documentTitle({ savedPath: null, unsaved: true })).toBe('● Untitled - Diagram');
    expect(documentTitle({ savedPath: 'C:\\Work\\plan.diagram.json', unsaved: false })).toBe('plan.diagram.json - Diagram');
    expect(documentTitle({ savedPath: 'C:/Work/plan.diagram.json', unsaved: true })).toBe('● plan.diagram.json - Diagram');
  });

  it('describes the file in the status bar and keeps the full path for the hover text', () => {
    expect(savedLine({ savedPath: null, unsaved: true }, null).text).toBe('Untitled, not saved');
    const saved = savedLine({ savedPath: 'C:\\Work\\plan.diagram.json', unsaved: false }, new Date('2026-10-18T10:00:00'));
    expect(saved.text).toMatch(/^plan\.diagram\.json: saved at /);
    expect(saved.title).toBe('C:\\Work\\plan.diagram.json');
    expect(savedLine({ savedPath: 'C:\\Work\\plan.diagram.json', unsaved: true }, null).text).toBe('plan.diagram.json: unsaved changes');
  });
});
