# W07 — external file changes and save conflicts

[Design](../design/W07-save-conflicts.md) · [Issue #29](https://github.com/rp2839/visio-mcp/issues/29)

Native JSON saves now compare a host-owned fingerprint against the exact bytes last opened or saved. SHA-256 detects content changes even when file size and modified time were preserved. Missing files and read failures are distinct. Save stops before overwriting an external version and offers Reload, Keep mine (Save as), Overwrite this version and Cancel, showing both versions' times and sizes.

Choices belong to one document/session/revision. Switching tabs cannot retarget a save or reload, and later edits invalidate a stale choice. Choosing the original path through Save as still requires conflict consent; another destination preserves the external file. Cancelled Save as keeps the dialog and local work. Explicit overwrite consent expires if the disk changes again, with another comparison after the temporary output is durable and before replacement. Failed guards leave disk and local work intact.

Reload validates the native file before replacement, requires explicit consent to discard local edits, and preserves the previous recovery until the replacement checkpoint is actually durable. A checkpoint-failure injection verified that old recovery stays available and a warning is returned. Background-tab reloads use a per-document recovery path provider rather than the active tab's path.

On focus, checks run once at a time across the open tabs. Notices are queued for their owning tabs, deferred during gestures/New/Close/recovery and deduplicated after Cancel. Save always checks again. Pending Save/Close workflows wait for conflict resolution; failed or cancelled choices cannot make an unsaved drawing count as saved. The dialog traps focus, supports Escape when idle, and remains focused while busy.

The lifecycle tests exposed an existing workspace.json teardown race. MainWindow now exposes completion of its existing asynchronous Closed cleanup; the Windows harness waits for actual closed-window cleanup before shutting down its dispatcher. The window close policy is unchanged.

## Verification and evidence

[Evidence folder](../../artifacts/w07-save-conflicts) contains the real WebView2 dialog and UI-to-host Save as preservation result, targeted TRX files and final suite results. Deliberate negative controls reproduced the original silent-overwrite behavior, obsolete deferred notices and stacked dialogs. Earlier reload-fault tests used a DocumentReplaced hook which does not fire when a reload keeps the document ID; the final injection uses SessionChanged and asserts that it fired.

Verified product commit: `3c9e515` (plus final documentation/evidence only).

- `npm --prefix src/web run typecheck`, `generate:check` and `build`: PASS.
- `npm --prefix src/web test`: **371 passed** across 40 files.
- `npm --prefix src/web run test:browser`: **104 passed**, including seven W07 modal/preservation cases.
- `dotnet test Diagram.Windows.slnx --logger "trx;LogFilePrefix=w07-final" --results-directory artifacts/w07-save-conflicts/final-test-results`: **411 passed**, zero failed/skipped across six test projects (including **77 Windows/WebView2** tests).
- Read-only code review: no remaining substantive findings after the durability and modal fixes.
- Manual human acceptance: **NOT RUN**.

## Limits

This is best-effort external-change detection with a final pre-replace guard. It is not a filesystem-wide atomic compare-and-swap with other writers; Windows or cloud-sync writers can still race between the final comparison and replacement. No automatic document merge, unattended reload, file watcher, public MCP contract or canonical drawing-schema changes are included. Native Visio import continues to produce an unsaved drawing with JSON Save as; native export retains its existing consent flow.
