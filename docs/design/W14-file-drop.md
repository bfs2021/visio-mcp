# W14 — file drops and clipboard pictures

Status: written design approved in Codex chat, 2026-10-19; product implementation has not started.
Issue: https://github.com/rp2839/visio-mcp/issues/36
Planned branch: codex/w14-file-drop, after W13. Design published on codex/w13-preferences for review.

## Intent and success

Let a person drop .diagram.json, .vsdx and supported picture files onto the app, or paste a clipboard picture, using the app's existing Open, tabs and approved picture-library behavior. Preserve unsaved work, reference identities, normal text editing and diagram copy/paste. Pictures enter the library first; placement in a drawing is an explicit choice.

This is a bounded extension to existing Open/tab/library/placement flows, with native input adapters. W02 is present on main. W13 is implemented first to avoid simultaneous edits to shared host/editor/modal files; W14 does not require new preferences behavior to open a file.

## Existing foundations and important boundary

WorkspaceModel.openFile reuses an untouched blank tab or opens another tab and reveals an already-open path. Host open/save already pins scope/revision and enforces file conflicts. Library import uses PrepareFile/AssetPreparer and durable approved bytes; SVG is sanitized. EditorModel.placeAsset registers and places approved images transactionally.

The current host.openJson(path) route is restricted to files remembered from the previous session. Do not turn it into an arbitrary path-open API. Native user input needs a separate, narrow ingress authority that feeds the same underlying open/import helpers.

## Native ingress and bounded tickets

The Windows shell owns external drop/clipboard capture. Disable conflicting WebView navigation/default file-drop handling, while preserving internal palette/asset drags. Only actual native user gestures mint ingress tickets; JavaScript cannot nominate a source path or create a ticket.

Each ticket is opaque, single-use, bound to this window/visible editor channel and expires after five minutes. Its host-side record holds the normalized native file path or captured bitmap and kind. At most 32 files are admitted per gesture, and pending ticket/bitmap memory is bounded. Reject excess items with a clear summary; never enumerate directories, expand shortcuts or recurse. Validate extension and content using existing loaders. Supported pictures are PNG, JPEG, BMP and SVG, as in the existing library. Reuse existing byte/dimension/SVG limits; clipboard bitmap dimensions are checked before encoding, then pass through the same approval pipeline.

Deliver a notification to the ready visible editor with ticket IDs, display names and kinds. Internal consume operations take a ticket and expected target document/session/revision, not a file path. Host validation/consumption is serialized with existing file lifecycle gates. Replayed, expired, wrong-window or consumed tickets fail without a side effect. Cancel/dismiss frees the ticket. Window/channel replacement or shutdown clears pending ingress. No public MCP endpoint, general upload path or relaxed previous-session restore allowlist.

## Diagrams and Visio drawings

Process a mixed drop in input order through a small queue. Before each item, honor gestures, current modal dialogs, New/close/recovery/export and file-conflict state. If the app is busy, keep the pending request visible with Cancel until it can be handled; do not replace a dialog or silently retarget another tab.

For .diagram.json and .vsdx, route through the existing workspace open behavior: reuse only an untouched blank tab; otherwise open a new tab. An already-open normalized path activates its existing tab without losing local edits or reopening it. Preserve tab limits and distinguish native Visio's imported/unsaved status from saved JSON. A newly created target tab is removed on failed/cancelled open, while the existing drawing remains intact. Any actual replacement path retains its existing unsaved-changes protection and pinned revision checks.

Extract internal path-specific open helpers so dialog-open, remembered reopen and ticket-open share parsing, compatibility reporting, path bookkeeping and disk fingerprints. Their admission rules remain distinct. Read/validate the file at consumption using the existing importer and disk-version checks; never trust a display name or extension alone. A file changing after the drop does not bypass validation or overwrite a newer document revision.

A failed/unsupported item does not roll back successfully opened separate files. Show an item-by-item result and allow cancellation of remaining items. Do not silently omit errors or activate a failed empty tab.

## Pictures and placement offers

A picture ticket uses the current approved image preparation and library import pipeline, retaining hash/version/deduplication behavior and sanitized SVG notes. Opening the library is not a drawing edit and adds no undo entry. Re-importing the same approved asset does not create duplicate bytes unnecessarily.

After import, show the picture name/thumbnail and choices Place in [drawing name] or Keep in library. Do not place automatically. A successful import remains in the library if the placement offer is closed, cancelled or stale; explain that outcome.

Pin the placement offer to the intended document/session/revision and, for a canvas drop, the page-point location calculated from that gesture's viewport. A drop elsewhere or a pasted picture uses the page centre. Before Place, revalidate scope/page/revision and the asset reference. A changed/closed/replaced target requires a fresh placement choice; never place into whichever tab happens to be active. Use existing placeAsset operations so registration/creation is one atomic edit and undo step, with existing snapping/size/aspect-ratio behavior. Switching tabs does not move or dismiss another drawing's offer silently.

## Clipboard precedence

Native bitmap capture is only requested by an explicit Paste gesture outside text inputs, textareas, contenteditable text and the canvas text editor. Text editing keeps normal OS/browser paste. Internal canvas shape/group copy/paste remains supported across tabs. W08 Copy as image remains a picture-producing clipboard action and must not be mistaken for an internal shape copy.

Use a host clipboard descriptor/sequence plus a private per-window internal-copy marker to distinguish the app's current copied element payload from a subsequently copied external bitmap. The marker refers to existing window-memory clipboard data, not a file path or serialized executable content. A matching current marker uses existing element paste; a newer external bitmap is imported to the library and offered for placement. Do not paste stale internal shapes over an external picture. Unsupported external text outside text editing is reported, not converted to a new feature that creates drawing text.

Capture/check the native clipboard sequence before consuming a bitmap; if it changes, fail cleanly or ask the person to paste again rather than importing unrelated content. Clipboard-busy/access failure is non-destructive and retryable. Preserve the current local element-copy fallback when the native clipboard is unavailable, with a diagnostic. Native and DOM handlers have one owner so one keypress cannot both paste elements and import a picture. Do not intercept ordinary Ctrl+V before the focused text editor can handle it.

## Internal interfaces and coordination

Add typed internal host ingress/clipboard messages and visible-editor queue/offer state:

- host.ingress.available is a host-to-visible-editor notification with batchId and items containing ticketId, display name and kind (json, vsdx or image); it never carries an unapproved path for the renderer to submit elsewhere.
- host.ingress.open takes ticketId and expected documentId/sessionId/baseRevision and returns the existing JSON/Visio open result. It consumes only a host-minted ticket and reuses the lifecycle gate, importer and disk-version/path bookkeeping.
- host.ingress.importImage takes an image ticketId and returns the existing approved library asset plus notes. Library import is window-owned and may complete if its original drawing changes; only later placement requires the pinned drawing scope/revision.
- host.ingress.dismiss takes bounded ticketIds and releases them idempotently within their owning window.
- host.clipboard.describe returns only the current sequence, internal-copy marker and supported-bitmap/text presence flags, not arbitrary clipboard content.
- host.clipboard.markInternalCopy takes the new copyId for this window's existing in-memory element clipboard and returns the native sequence after setting the private clipboard format. This follows the person's explicit Copy command; no document bytes or paths are exposed by the marker.
- host.clipboard.importImage takes expectedSequence during an explicit Paste command, captures the still-current supported bitmap and returns an approved imported library asset/notes through the same image pipeline. It is not a generic clipboard-content reader.

These operations validate bounded ticket IDs, owning channels and expected scopes where a document can change. They cannot be reached through public MCP/live-control method discovery. No arbitrary frontend-provided file paths, generic clipboard read/export tool or background clipboard polling.

Likely integration: MainWindow/WebViewHost native drop and clipboard adapters, HostServices factored open/library helpers, WorkspaceModel open-by-ingress queue, EditorModel placement offers and internal-copy metadata, App modal/keyboard guards, and the existing picture panel. Preserve W12's Help/palette work and W13's preferences dialog; coordinate shared shell/menu files through the issues. Use a separate branch/PR and leave historical IMPLEMENTATION_PROGRESS.md untouched.

## Verification and acceptance

- Unit tests exercise queue order/cancellation/limits, normalized-path deduplication, blank-tab reuse, failed-open cleanup and pinned target/revision admission.
- Ticket tests reject fabricated/replayed/expired/wrong-window tickets, directory/shortcut drops and unsupported/malformed input without changing documents or weakening remembered-reopen restrictions.
- Real native drop adapters are tested with user-gesture data objects; test seams do not expose a production ticket-minting shortcut. Cover JSON, Visio, picture and mixed-file drops, modal/gesture delays, duplicate paths, tab limits and partial failure summaries.
- Picture import verifies approved bytes, SVG sanitization, dimensions/deduplication, explicit placement, Keep in library, stale target refusal, one undo, and zoom/scroll drop coordinates.
- Clipboard tests cover real bitmap capture, changed/busy clipboard, text-input paste, internal cross-tab shape paste, stale shape clipboard versus a newer external image, and W08 Copy as image when its PR is integrated. Preserve other apps' clipboard behavior by limiting ownership to explicit copy/paste gestures.
- Browser controls exercise offers, focus, cancellation and lifecycle/modal guards. Native Windows tests verify actual shell/clipboard integration and file save/open/native Visio roundtrips; browser-only tests use a deterministic in-memory host.
- Run normal frontend/typecheck/generated/build, full browser and Windows solution checks, with bounded workers and sequential desktop/clipboard runs. Retain failures and retries. Human manual acceptance stays explicit rather than inferred from automated adapters.
- Request read-only review for ingress authority, clipboard precedence and stale target behavior. Deliver a separate PR linked to #36; no automatic main merge.

## Scope limits and review gate

No directory import, shortcut expansion, web URL download, arbitrary HTML/rich-text paste, clipboard monitoring, installer/file associations or changes to diagram formats. W15 owns file association/open-with; W08 owns picture export/Copy as image.

The approach is approved. Review this written design before implementation. W13's architectural specification and implementation-plan review take place first; W14 then proceeds through the normal bounded-change workflow using this approved design.
