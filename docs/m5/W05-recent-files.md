# W05 — recent files and last Open folder

[Issue #27](https://github.com/rp2839/visio-mcp/issues/27) · [approved design](../design/W05-recent-files.md)

File > Recent files lists ten unique successful JSON Open/Save/Save as and Visio imports, newest first. Filename and full path distinguish duplicate names; missing/unavailable entries remain removable. Clear removes history, never drawings. A successful recent Open uses the existing workspace flow, retaining unsaved tabs and activating an already-open JSON file. Failed or thrown opens close provisional tabs and report their error in the dialog. Busy actions retain focus and suppress editor shortcuts.

The host stores version 1 history alongside the per-user blob/workspace data, with atomic writes, a 128 KiB input bound, ten entries, normalized absolute paths of at most 4096 characters, case-insensitive deduplication and host-owned entry IDs. Recent opens resolve IDs and file kinds in the host; the page cannot supply an arbitrary recent path. Invalid history is treated as empty with a warning, including when the next successful update replaces it. History failures do not turn a successful drawing save into a reported failure.

The last successful Open/import/recent folder is shared by both native Open dialogs. Save/Save as, export, cancellation, failure and automatic workspace reopen do not change that folder. Directory and file availability probes run outside the UI thread, share pending work per path, use at most 16 dedicated workers, and return unavailable after 750 ms. File probes run in parallel; an overdue directory falls back to the normal Windows dialog default. Existing missing files are not silently removed. An OS filesystem call cannot be cancelled reliably; overdue workers stay bounded until the OS returns.

Open/import pins the intended tab and revision before waiting on another file action, disk check or native picker. A late reply cannot capture a newly active tab or discard edits made while it waited. Regression tests against the previous host reproduced late JSON and Visio opens discarding a committed edit; the corrected implementation refuses both with revision_conflict and preserves the edited snapshot.

## W15 dependency and remaining work

The approved design explicitly permits deferring jump-list work when it cannot be delivered safely without W15. Current App.OnStartup always creates MainWindow with default options; MainWindow starts the default MCP pipe and PipeServer uses FirstPipeInstance. A second process cannot forward its requested file and instead collides with the live instance. Therefore **Start/taskbar jump lists and startup argument activation are not implemented**; they remain in #27 pending [W15 #37](https://github.com/rp2839/visio-mcp/issues/37). No registry/association/IPC changes or placeholder jump tasks were added. This PR does not close #27.

W07's separate conflict-aware Save path must retain the success-only RecordRecentAsync hook when integrated: cancelled, conflicted and failed saves never record history. W08 and W05 both touch host dispatch and App modal composition; retain both handlers and modal guards during integration. This branch is independent of those PRs.

## Verification

Product commit: 70106b9; main anchor e34999e. Complete Windows solution: **429 passed, 0 failed, 0 skipped** (Contracts 24, Host 149, IPC 17, MCP 14, Visio 148, Windows 77). Frontend 391 tests and browser 107 tests passed; typecheck, generated-contract check and production web build passed. Targeted native feature checks: five passed. Store/deadline checks: eight passed. Read-only review findings were fixed and re-reviewed without remaining substantive findings.

Commands: dotnet test Diagram.Windows.slnx --logger trx --results-directory artifacts/w05-recent-files/verified-results; in src/web: npm test, npm run typecheck, npm run generate:check, npm run build, npm run test:browser.

Evidence: [artifacts/w05-recent-files](../../artifacts/w05-recent-files). Native tests exercise actual Save/Open/Save as/import, cancellation, failure, history persistence, missing files, kind/ID refusal, history-write warning and edit-during-Open preservation. Browser tests exercise real Recent controls, Remove/Clear, duplicate-tab activation, unsaved drawing preservation, thrown bridge errors and busy focus. The injected slow-drive test checks the deadline and shared probe without requiring a real disconnected drive. The last-folder value and production picker wiring are verified; interactive native picker placement and manual human acceptance are **NOT RUN**. Jump-list/startup tests are **NOT RUN — pending W15**.

The first complete .NET attempt was deliberately interrupted before the final remembered-folder fix; it is not a passing result. verified-results is the final run. Existing bridge unknown-outcome timeouts still apply to native file operations; pinned identities/revisions prevent late replacement of a closed/changed target. No new public MCP or canonical drawing contracts.
