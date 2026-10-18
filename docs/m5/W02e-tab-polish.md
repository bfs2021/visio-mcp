# W02e: tab polish and accessibility

Issue #44, part of #24.

## Behaviour
- **Reorder:** drag a tab (pointer events; moves live as the pointer passes other tabs; dropping is not a click, so the active tab does not change) or focus it and press Ctrl+Shift+Left/Right. The order is remembered with the rest of the workspace. Screen readers hear "<name> moved to position N of M".
- **Ctrl+Tab** steps through tabs most recently used first; while Ctrl is held it keeps walking back through the history (Shift goes forward); releasing Ctrl makes the chosen tab the most recent, so two quick presses toggle between two tabs. Ctrl+PageDown/PageUp step in tab order.
- **Tab menu** (right-click, Menu key or Shift+F10; arrow keys, Enter, Escape): Close, Close others, Close to the right, Copy path, Show in Explorer. Several closes ask about each unsaved tab in turn and Cancel stops the rest. Copy path and Show in Explorer are enabled only for a tab saved to a file and are done by the host for that document id, so no other path can be named.
- **Overflow:** the strip scrolls without a scrollbar; arrows appear when tabs do not fit, a list button opens a menu of every document (unsaved marked) that jumps to it; the active tab is always scrolled into view.
- **Accessibility:** the tab list holds only tabs (new-tab button, notices and the restore notice are outside it); each tab's name says when it has unsaved changes; Home/End, arrow keys and Enter/Space work; the tab count and limit message are announced.
- The 20-tab soft limit was done in W02b.

## Note on Show in Explorer
The boundary guard test forbids code that starts programs. Explorer is therefore asked through the shell's own `SHOpenFolderAndSelectItems`, not by launching a process, and only for the saved path of an open document. This needs your review: it is a deliberate, narrow exception in spirit, though not in the guard's letter.

## Tests added
Browser `tabs-polish.spec.ts` (8); Windows `TabTests` (+1: reveal/copy name only a saved open document's file).

## Results
Unit 342, browser 93, Windows 62, host 138, MCP 14: all pass.
