# W02b: tab strip, New/Open as tabs, per-tab close

Issue #41, part of #24. Design: `docs/design/tabbed-documents.md`.

## Behaviour
- A tab strip under the menu: file name (or `Untitled N`), a dot while the tab has unsaved work, a close button, middle-click closes, `+` makes a new document. ARIA `tablist`/`tab`; arrow keys, Enter and Space move and select.
- Ctrl+N new, Ctrl+W close tab, Ctrl+Tab / Ctrl+Shift+Tab next / previous, File > New / Close tab.
- Each tab has its own editor: history, selection, tool, drawers, zoom and scroll (restored on return). The clipboard is shared, so copy in one tab pastes in another. Window title, status bar, panels and inspector follow the active tab.
- Open (and Open Visio) uses an untouched blank tab, otherwise adds one; a cancelled or failed open leaves no tab behind; opening a file that is already open shows that tab (path compared case-insensitively, `/` = `\`).
- Closing a tab with unsaved work asks Save / Don't save / Cancel. Don't save discards only that tab's recovery session; Cancel does not touch the host. Closing the last tab leaves a fresh blank one.
- 20-tab soft limit with a message.
- Documents added to the workspace by anything else (an agent, in the background) get an editor and a tab, and do not take over.
- Window close (interim, until W02c): one prompt for all unsaved tabs, "Save all" / "Don't save any" / Cancel. Decision 2 (silent exit) lands with session restore in W02c, because silent exit without restore would bring back the recovery dialog.

## Code
- Page: `workspace/WorkspaceModel.ts` (tabs, open/close/activate, window close), `components/TabStrip.tsx`, `App` renders the active tab's panels (keyed per tab) and the close dialog of whichever tab is asking. `EditorModel` gains tab id/label, `isBlank`, view-state save/restore, tab close prompt, shared clipboard; `open()` reports the path.
- Host: `RecoveryManager.OnClosed` removes the session at once; `DiscardAllAsync` for the window; `host.recovery.discard/finish` refuse only sessions that belong to an open document.

## Tests added
- Browser `tabs.spec.ts` (14), Windows `TabTests` (5: documents known to the host, Don't save on a tab, window close asks once, Save all writes both files, crash keeps both sessions).

## Results
Unit 335, browser 74, Windows 46, host 127, MCP 13: all pass. Installed locally as `w02b`.
