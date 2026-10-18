# W02c: session restore and silent window close

Issue #42, part of #24. Design: `docs/design/tabbed-documents.md` section 6.

## Behaviour
- The page tells the host which tabs are open (order, document and session ids, title, unsaved) whenever that changes (150 ms debounce, unchanged lists are not resent). The host adds each tab's file path and writes `workspace.json` next to the recovery folder (atomic). The file says `cleanExit: false` while the app runs; a normal shutdown sets it true.
- At start the page reads it and, tab by tab in order: a tab with a surviving recovery session is rebuilt from it and stays unsaved; otherwise a tab with a file reopens from disk; untouched untitled tabs are dropped. The previously active tab is shown again. The initial blank tab is used for the first restored tab.
- Nothing is asked. After a crash a notice says "Restored N documents after an unexpected exit. Unsaved changes are kept."; after a normal exit it only appears when unsaved work came back ("Reopened N documents, M with unsaved changes."). Journal damage messages and files that could not be reopened are listed in the notice, which stays until dismissed.
- Sessions that no remembered tab owns are still offered in the recovery dialog; **Recover now opens the session as its own tab** (an untouched blank tab is used first) and never replaces the work in the tab being used.
- With no remembered tabs the old behaviour applies (offer unfinished work).
- Closing the window is **silent** (decision 2): no question, unsaved work stays protected and returns as unsaved tabs. The prompt remains available as an option (`AppOptions.PromptOnClose`, to be exposed by W13).
- Reopening a tab from its file needs no dialog, but the host only honours paths the last workspace file listed.

## Fixed on the way
- Opening a JSON file never marked the document as saved at its path, so the tab and window said "Untitled" with an unsaved dot and the same-file check could not match. The host now marks the opened revision saved.

## Tests added
Host `WorkspaceStoreTests` (5); browser `workspace.spec.ts` (5: order/active/notice/orphans, orphan as tab, unreadable file, fallback, persist without resend); Windows `WorkspaceRestoreTests` (4: crash, normal exit, orphan session, path allow-list). Two existing recovery specs and four Windows tests were adapted: a restart with no workspace file is the orphan case, and Recover opens beside the user's work.

## Results
Unit 335, browser 79, Windows 50, host 132, MCP 13: all pass.
