# W13 — persisted preferences and new-document defaults

Status: written design approved in Codex chat, 2026-10-19. Implementation plan is prepared for review; implementation is not approved yet.
Issue: https://github.com/rp2839/visio-mcp/issues/35
Branch: codex/w13-preferences. Base: origin/main e67f2e7. Implement before W14.

## Intent and success

Give the Windows app one per-user Preferences dialog for display units, grid defaults, default font/size, recovery checkpoint interval and light/dark editor theme. Preferences survive restart. Changing preferences must not rewrite, dirty, recolour or reformat an existing drawing. New drawings capture their document defaults so reopening them, or creating objects through MCP, does not depend on the machine's current settings.

This is architectural: storage, startup, canonical object defaults, host/renderer messages and recovery timing meet here. The approved approach uses host-owned storage, rather than browser localStorage or a second independent host configuration. This document is the written specification; the implementation plan follows its approval.

## Existing foundations

- WorkspaceStore already persists per-user state with AtomicFileWriter. Reuse those storage patterns, not the workspace.json payload.
- MainWindow owns the user-data folders, HostServices and RecoveryManager. Agent views share the host.
- newDocument creates one A4 landscape page, a 5 mm visible/snapping grid and an empty metadata map. Workspace/NewDocumentFlow already coordinate new tabs, revision checks and recovery protection.
- The canonical planner supplies Segoe UI 12 pt defaults to new text-bearing objects. Explicit formatting already overrides those values.
- RecoveryManager journals commits independently and checkpoints every 50 commits, 60 seconds with pending edits, and on save. Its timer is currently constructed once.
- The editor has a unit selector and CSS colour tokens, plus some hard-coded chrome colours that need conversion for a coherent dark theme.

## Settings contract and validation

Use a versioned preferences record, schemaVersion 1, and one canonical validation definition for the host and frontend. No new public MCP tool and no document schema-version change.

| Field | Allowed values | Initial/default value |
|---|---|---|
| units | mm, cm, in, pt, px | mm |
| grid.spacingPt | finite number, 0.1 through 1440 pt | 72 * 5 / 25.4 pt |
| grid.visible / grid.snap | booleans | true / true |
| font.family | trimmed, nonempty, 1–128 characters | Segoe UI |
| font.sizePt | finite number, 1 through 1000 pt | 12 |
| recovery.checkpointSeconds | integer, 10 through 3600 | 60 |
| theme | light or dark | light |

The UI displays grid spacing in the selected display unit and persists points. Font controls offer installed family names where available; a valid unavailable family is retained and uses normal platform fallback, with a note. Do not download fonts. Grid visibility/snapping defaults concern new pages, not the current page's View toggles. Guide preference persistence is outside this issue's acceptance scope.

## Ownership, persistence and failures

Store preferences.json alongside the existing per-user workspace.json, derived from the configured application-data root. Test roots remain isolated. Do not accept a file path from the renderer or put settings inside WebView storage. Bound the settings file to 64 KiB and reject unknown schema versions for writes.

PreferencesStore serializes access and uses AtomicFileWriter. Read returns validated effective settings, a content/version tag and an optional diagnostic. An absent file uses defaults without writing anything. An unreadable, malformed or future-version file remains untouched; startup uses safe legacy defaults and exposes a nonblocking diagnostic. Preferences UI offers Reload and explicit Reset; resetting an unreadable/future file preserves its previous bytes in a backup before an atomic replacement. Never silently replace a file on startup.

An apply operation includes the expected content tag. Re-read/check that tag before replacement; a concurrent settings change returns a conflict and current settings, preserving the draft and the disk file. Reload is explicit; do not silently combine two drafts. I/O failure keeps the old effective settings and draft. Publish settings to the live editor/recovery timer only after persistence succeeds. If a backup/write fails, keep the previous file and report failure.

The host is authoritative. The visible editor loads settings before constructing a fresh document; restored/opened drawings keep their own content. Agent/headless document creation obtains the same host defaults. The browser-only development/test host supplies deterministic in-memory defaults and never reads a user's real settings. Other app instances re-read when Preferences opens/applies; live cross-process theme synchronization is not required.

## Internal messages and UI

Add internal host.preferences.get and host.preferences.save requests with typed, validated payloads. get returns preferences, versionTag and optional diagnostic. save accepts preferences plus expectedVersionTag and returns the new effective record/tag or a conflict/I/O/validation error. These methods are not added to public MCP discovery or the external live-control allowlist. No arbitrary filesystem access or runtime code is configurable.

Expose Edit > Preferences with a modal dialog matching existing modal/focus protections. Fields edit a local draft. Save validates, persists and then applies; Cancel/Escape makes no change; Reset to defaults changes the draft and still requires Save. Keep focus and errors inside the dialog and restore the opener on close. Block conflicting lifecycle actions, drag gestures and overlapping modal dialogs while applying. A failed save leaves the draft visible. Labels explain which choices affect display, future drawings or recovery.

## App choices versus document defaults

Units and theme are view choices. On successful Save, update the window's tab views without a canonical transaction, revision change, undo entry or unsaved marker. A person's later toolbar unit override remains a per-tab session choice; it does not silently rewrite the saved default. Explicitly saving Preferences applies the selected unit to open tab views. Theme changes editor chrome/rulers/panels only; page background, shape/text colours, thumbnails and clean/native exports preserve document colours. A white diagram page stays white in dark editor chrome. Native shell/dialog styling follows the chosen theme where the app controls it; OS-owned file dialogs may retain Windows styling.

When a new document is created, copy grid/font defaults into that document once. The first page gets the captured grid. Retain the captured defaults in the existing document metadata map under a versioned, app-owned key, editor.defaults.v1 (a bounded JSON string containing grid and font). Do not store theme, recovery timing or display-unit preference in the drawing. JSON/Visio metadata roundtrips must preserve the captured values.

A shared validated parser supplies captured defaults to new pages and new text-bearing objects in that document, including shapes, text boxes, connector labels, GUI and canonical MCP/DSL operations. Explicit operation/page/text values always win. When text or a connector label is first materialized on a previously empty object, use the captured defaults; editing an already materialized TextBlock preserves its current formatting unless an operation explicitly changes it. Existing objects are never reformatted by a preference change. Documents without valid captured metadata retain today's legacy defaults; opening/recovery must not add metadata or create a revision. Malformed captured defaults fall back safely, remain unmodified and produce a diagnostic. Copy/import retains authored object formatting. Component/icon builders with explicit formatting retain that formatting.

All fresh-document paths must use the same captured snapshot of preferences: startup blank, File > New/Ctrl+N, plus-tab creation, document registry/MCP creation and fallback after closing the final tab. A preference change while New's picker is open does not change its captured values behind the person's back. Add-page defaults come from the document capture, not the latest global preferences. This keeps old drawings and headless agent edits independent of user settings changed later.

## Recovery timing

Expose an awaitable, validated RecoveryManager interval update using its existing serialization/locking. Reschedule the existing timer after a successful preference save; do not replace the manager, discard queued jobs, reset session identity or clear counters. Retain the 50-commit threshold, journal flush behavior, save checkpoints and pending-change check. The interval cannot disable journaling or recovery. Existing queued checkpoints may finish normally; updates/disposal cannot schedule work after shutdown. Test timing with a controlled clock/scheduler seam, not long real sleeps.

## Coordination and boundaries

Claude owns W12 (#34), including shell/help/palette/menu work. Coordinate overlapping MainWindow/HostServices/Toolbar/App changes through issues and preserve its commands. W10/W11 and W05/W08 remain separate review branches; integrate their shared UI guards if they land, without silently merging another PR. Use a feature branch, small commits and the existing issue workflow. Do not edit historical IMPLEMENTATION_PROGRESS.md.

No installer, association changes, new font assets, user-defined CSS, automatic diagram recolouring, existing-document migration, disabled recovery or general settings synchronization service.

## Verification and acceptance

- Store validation, bounds/non-finite values, missing/corrupt/future files, backup/reset failure, atomic writes and stale tags preserve previous effective state and bytes.
- Restart and separate test roots demonstrate per-user persistence. Browser-only tests remain deterministic.
- Every fresh-document route captures the same settings; canonical GUI/MCP/DSL creation uses captured fonts/grid, explicit values win, and legacy drawings remain byte/semantic-identical apart from intentional edits. Add-page, undo/recovery and JSON/native Visio metadata roundtrips are covered.
- Preference Save/Cancel/Reset/validation/conflict, keyboard/focus/modal behavior, unit override semantics and light/dark screenshots are exercised through real browser controls.
- Compare page/export pixels and saved document hashes before/after theme/unit changes; only editor chrome changes.
- Recovery interval update, pending work, save/commit thresholds and disposal races use deterministic tests; a native run verifies durability after a settings change.
- Run frontend unit/typecheck/generated/build, full browser and Windows solution suites with bounded workers. Native desktop tests run sequentially. Retain initial failures and retries in evidence; manual human acceptance is explicitly PASS/FAIL/NOT RUN.
- Fresh read-only review covers storage/startup races, captured-default consistency and recovery safety. Deliver a separate PR linked to #35; no automatic main merge.

## Review gate

The approach is approved. Approval of this written specification permits preparation of W13's implementation plan. That plan will be provided for review with an execution-method choice before product implementation begins.

