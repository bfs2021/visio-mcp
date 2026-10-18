# W01, W04, W06: closing, naming and recovering documents

Recorded 2026-10-18. GitHub issues #23 (W01), #26 (W04), #28 (W06). Prompted by the "Recover unsaved work?" dialog appearing on almost every start.

## Cause
`MainWindow` had no Closing handler, so closing a window with unsaved changes just exited. The work stayed in recovery and the next start offered
it. There was no way to close without saving and without being asked about it later.

## W01: save on close
- `MainWindow.OnClosing` holds the window (`e.Cancel`) and sends `app.beforeClose` to the page. Nothing unsaved: it closes. Unsaved: the page shows
  **Save / Don't save / Cancel** (`CloseDialog`) and answers through `host.app.close` or `host.app.closeCancelled`.
- Save runs the normal Save (Save as for an untitled file) and closes only if the document is then saved; a cancelled file dialog keeps the
  window open and says so. Don't save calls `RecoveryManager.DiscardCurrentAsync`, so the next start offers nothing. Cancel keeps the window open
  and a later close asks again. A page that does not answer within 5 s cannot trap the window: it closes and recovery still holds the work.
- A killed process or crash still leaves recovery behind and offers it, as before.
- `EngineStatus.unsaved` is new: a blank untouched document is not "unsaved", but a document opened or recovered into the page is, until saved.
- `AppOptions.PromptOnClose` is on for the real app and off for test harnesses that close windows programmatically.

## W04: names and markers
The page title is `[● ]<file name or Untitled> - Diagram`, and WebView2's `DocumentTitleChanged` sets the window and taskbar title. The status bar
says `Untitled`, `Untitled, not saved`, `<file>: saved at <time>` or `<file>: unsaved changes`, with the full path on hover.

## W06: recovery dialog
- `host.recovery.list` returns every recoverable session with title, file, page and object counts (at the last checkpoint), revision and time.
- The dialog lists each session with a thumbnail drawn from the recovered state (checkpoint plus journal, so the counts are corrected once it is
  rebuilt), the file name, any loss messages and its own **Recover** and **Discard**. **Not now** leaves all of them for next time.
- After a recovery the report offers **Save as…**, which lands the work in a file of the person's choosing.
- Single-document limit: Recover replaces the current empty document (refused if it has been edited), so the other sessions stay on offer for the
  next start. Opening each as its own tab is part of W02.
- Not changed here: the lifecycle of a restored session that is then left unedited (H01, #19).

## Evidence
| Check | Result |
|---|---|
| Browser: close prompt, Cancel, Don't save, Save, cancelled Save as, title and status | `tests/browser/close.spec.ts` (7) |
| Browser: several sessions, thumbnails, per-session discard, Save as | `tests/browser/recovery.spec.ts` (6) |
| Engine: unsaved semantics | `tests/unsaved.test.ts`; `tests/documentTitle.test.ts` |
| Real window: clean close, Don't save leaves nothing to recover, Cancel keeps it and asks again, Save writes then closes, cancelled Save as stays open, window title, recovery list | `tests/Diagram.Windows.Tests/CloseTests.cs` (7) |
| Existing crash-recovery tests with the new dialog | pass (CrashRecoveryTests) |
| Suites | 329 unit, 60 browser, 36 + 7 Windows (excluding Office, Performance, Demo), 119 host |
| Screenshots | `docs/m5/evidence/w01-w06/close-prompt.png`, `recovery-list.png` |
