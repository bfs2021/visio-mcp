/** The text for the window title: file name (or Untitled), a dot while there is unsaved work, and the app name. */
export function documentTitle(status: { savedPath: string | null; unsaved: boolean }): string {
  const name = status.savedPath ? status.savedPath.split(/[\\/]/).pop() : null;
  return `${status.unsaved ? '● ' : ''}${name || 'Untitled'} - Diagram`;
}

/** The status-bar line about the file: where it lives, whether it is up to date, and when it was last saved here. */
export function savedLine(status: { savedPath: string | null; unsaved: boolean }, lastSavedAt: Date | null): { text: string; title: string } {
  const name = status.savedPath ? status.savedPath.split(/[\\/]/).pop() : null;
  if (!status.savedPath) return { text: status.unsaved ? 'Untitled, not saved' : 'Untitled', title: 'This diagram has not been saved to a file yet' };
  const time = lastSavedAt && !Number.isNaN(lastSavedAt.getTime()) ? ` at ${lastSavedAt.toLocaleTimeString()}` : '';
  return { text: status.unsaved ? `${name}: unsaved changes` : `${name}: saved${time}`, title: status.savedPath };
}
