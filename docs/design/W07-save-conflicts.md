# W07 — external file changes and safe save choices

Status: approved by the user on 2026-10-18. [Issue #29](https://github.com/rp2839/visio-mcp/issues/29).
Branch: `codex/w07-save-conflicts`, based on main `b8d9fee`.

## Intent and scope

A diagram edited elsewhere must not be silently overwritten by Save. Extend the existing native JSON Open/Save flow, per document tab, with a remembered disk version and an explicit conflict dialog. Preserve the drawing and its recovery until a choice succeeds. This is a bounded extension of existing host file actions, atomic writing and editor dialogs; no new canonical drawing schema or public MCP tools are planned.

The issue requests detection on Save or window focus, modified time and size, and Reload / Keep mine (Save as) / Overwrite. This design also includes Cancel, deleted/unreadable files and checks for a second external change while the dialog is open. Native Visio imports already become unsaved drawings with JSON Save as; native export remains under its existing export/overwrite consent flow.

## Recommended approach

Use per-tab content fingerprints (SHA-256, byte length, last-write UTC and existence), checked on Save and on returning focus. Timestamp-only detection can miss same-size writes or preserved timestamps. A continuous file watcher adds coordination and sync-event noise without replacing the required save check, so it is deferred.

Record the fingerprint of the actual bytes opened or successfully written, rather than a later independent reread. Identity/session and per-document path bookkeeping follow the existing workspace. Keep fingerprints host-side and remove them when their document closes. Recovered unsaved drawings with a path but no trustworthy disk baseline must offer Save as or explicitly confirm the currently observed disk version before overwrite; recovery must not silently adopt a new baseline.

## User flow

On Save, compare the chosen target with the baseline before writing. If it changed, leave both file and drawing unchanged and show a dialog naming the file with the remembered and current modified times/sizes. Missing files are shown as missing, not as zero-byte files. Read failures are reported without assuming the file is unchanged.

- **Reload from disk:** validate/read the file before replacing the intended tab. If local edits would be discarded, ask explicitly. Pin document/session/revision so edits made during the dialog or loading are preserved by refusing a stale reload. Only retire discarded recovery after the replacement is durable. Missing/unreadable/invalid files cannot be reloaded.
- **Keep mine (Save as):** open the normal Save as picker. Cancellation preserves the conflict and unsaved work. Choosing the conflicted path again does not bypass confirmation. Existing alternate targets retain native overwrite consent and are checked again before commit.
- **Overwrite this version:** confirm the currently displayed disk fingerprint. Recheck it after serialisation/temp-file writing, immediately before atomic replacement. If the observed version changed again, refresh the dialog and require a new choice.
- **Cancel:** change neither disk nor drawing and retain the old baseline, so the next Save still checks for the unresolved conflict.

After a successful save, update only the written revision and that tab's baseline/path. Newer local edits remain unsaved. After a successful reload, refresh that tab's page/selection/view consistently with Open, mark the loaded revision saved and update its baseline. Other tabs remain unchanged.

On window focus, check saved open tabs with bounded, deduplicated work. Queue tab-specific notices; present at most one active-tab dialog, deferred during gestures or existing New/Close/recovery dialogs. Do not repeatedly interrupt for the same observed version after Cancel; Save always checks again. Tab switches or closes cannot redirect an in-flight response to another drawing.

## Implementation boundaries and limits

Extend SessionCoordinator open/save results to carry the version of the bytes actually read/written. Add a small host-side fingerprint/check service and an optional pre-replace guard in AtomicFileWriter; preserve existing unguarded callers. Host conflict operations accept pinned scope/revision and host-issued observed-version evidence, not arbitrary replacement paths from the page. Reuse the existing file-action serialisation gate for resolution actions.

Extend the private HostApi/Open/Save replies, EditorModel and a focused conflict dialog/focus hook. Use the existing result/error vocabulary; no new public operation schemas or MCP routes. Save invoked from Close must resolve or cancel its conflict before the tab/window can be treated as saved.

The fingerprint comparison plus final guard is best-effort conflict detection, not a filesystem-wide transaction with other processes. Windows/cloud-sync writers can still race in the interval between the final comparison and replacement. State that limit honestly; do not claim an atomic compare-and-swap guarantee. Do not introduce automatic merging of diagram documents, unattended reloads or automatic overwrite.

## Verification

- Host/unit: unchanged file; external content change; same-size and preserved-timestamp changes; deletion; unreadable file; baseline from exact opened/written bytes; final-guard refusal without changing the target; overwrite evidence invalidated by a second external change; failed writes do not mark saved.
- Browser: dialog fields/actions, Cancel, cancelled Save as, reload-discard confirmation, failed/invalid reload, focus deduplication, deferred prompts and separate tabs. Existing New/Close/Save flows keep their protection.
- Real Windows/WebView2: external write between Open and Save; safe refusal with disk and local work intact; explicit overwrite; Save as preserves the externally changed original; revision/identity changes during a choice; per-tab targeting and recovery. Record fresh evidence and manual checks as NOT RUN unless performed by a person.
- Before code pushes: usual frontend, browser, .NET, typecheck, contract-generation and production-build checks, plus independent review. Keep small commits on the feature branch and coordinate shared-file changes through the issue. Do not change the historical Markdown status table.

## Approval

The user approved this written design in chat. Implementation results and limits are recorded in docs/m5/W07-save-conflicts.md.
