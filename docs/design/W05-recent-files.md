# W05 — recent files, last folder and Windows jump list

Status: proposed for user approval, 2026-10-19. Issue: https://github.com/rp2839/visio-mcp/issues/27
Planned branch: `codex/w05-recent-files`, after W08. Design published with the W08 batch for review.

## Outcome and scope

A person can return to one of the last ten successfully used files from File > Recent or the Windows taskbar/Start jump list. Extend the existing host Open/Save, tab workspace and native shell integration. This is a bounded extension; no canonical document or public MCP contract changes.

## Proposed behaviour

File > Recent lists up to ten unique native JSON/Visio paths, newest first, showing the filename and enough path to distinguish duplicates. Missing/unavailable files are marked without deleting their entry automatically; offer Remove and Clear recent. Opening a missing item reports the problem without discarding work. A recent file opens through the existing workspace/tab and JSON/Visio import flow, preserving existing unsaved tabs.

Record successful native JSON Open/Save/Save as and Visio imports, not exports or cancelled/failed attempts. Normalize full paths and deduplicate case-insensitively. Persist a versioned, bounded per-user recent-files store with atomic writes, separate from workspace/recovery state. Corrupt history falls back to an empty list with a warning; it must not block opening/saving documents. Removing history never deletes a document.

Remember the last successfully chosen Open folder, shared by JSON and Visio Open; use it when it still exists, otherwise use the normal Windows dialog default. Do not move it on cancelled dialogs or exports. Recent-file availability checks handle access failures and do not block the UI.

Publish a custom Windows jump list from the same ten entries using host-owned JumpTasks with correctly quoted executable/file arguments. Remove/clear updates the list; suppress automatically maintained Recent/Frequent categories so they cannot show a contradictory list. Handle user-rejected tasks and shell failures gracefully. Add a narrow startup argument parser for one supported file path and open it after the workspace is ready through the existing tab/import flow. The page cannot turn this into an unrestricted path-opening endpoint: use host-owned entry IDs/capabilities for Recent.

## Boundaries and coordination

W15 retains single-instance activation/forwarding, file associations and Open with registration. Until W15, invoking a jump task can launch a separate app process, matching current startup behaviour; it must be documented and cannot be presented as single-instance-safe. If integration testing shows that the jump-list acceptance cannot be delivered safely without W15, stop that portion and record the dependency rather than silently expanding scope.

W02's tab/workspace code is available on main but still under Claude's review. Coordinate shared changes in #27: HostServices native Open/Save, App startup, MainWindow options, Toolbar/EditorModel/workspace, plus a recent-file store and shell adapter. Keep W07's pending save-conflict branch separate and plan its eventual merge hooks so only successful saves update history. No implicit native-file association or registry writes.

## Verification and review

Unit tests cover capacity/order/deduplication, malformed/oversized stores, atomic failure, missing/unreadable files, safe entry IDs, quoting and rejected shell items. Browser tests use actual Recent controls, removal, errors and tab/unsaved preservation. Windows integration tests cover real successful/cancelled Open/Save/import paths, reload persistence, initial-folder selection, native jump-task list and startup file activation. Include Unicode/spaces in paths and unsupported/multiple arguments. Do not claim Start-menu human acceptance from a mocked shell adapter.

Run the usual frontend and complete Windows solution checks, retain fresh evidence and explicit NOT RUN manual checks, request read-only code review, push a separate branch/PR and move #27 to In review. Keep the issue open until its intended integration is verified.

## Alternatives and limits

Storing history only in the web profile duplicates host knowledge and can forget native Save/import outcomes. Relying solely on Windows Recent/Frequent cannot guarantee the exact app-owned list or predictable removal. A small host store plus one jump-list projection keeps one source of truth.

No automatic reopen beyond W02 session restore, pinning, cloud-history sync, file watchers or arbitrary recent-file paths supplied by MCP. A failed history update must not turn a successful document save into a reported document-save failure.
