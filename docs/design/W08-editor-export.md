# W08 — editor image/PDF export and Copy as image

Status: proposed for user approval, 2026-10-19. Issue: https://github.com/rp2839/visio-mcp/issues/30
Branch: `codex/w08-editor-export`, based on origin/main b9eadbd. W07 remains separate PR #46.

## Outcome and scope

A person can export or copy a diagram without asking an agent. Extend the existing File/Edit menus, snapshot renderer, native dialogs and export-path policy. Preserve the document, undo history, saved revision and active tab. This is a bounded extension of the existing export/render flow; no canonical drawing schema or public MCP tool changes.

## Proposed behaviour

- File > Export image opens a dialog with PNG, JPEG or SVG; current page, selected objects or all pages; scale choices; and page background, white or transparent. JPEG requires an opaque background and explains that choice. Empty selection is disabled.
- Single-page/selection export chooses one native destination. All-pages image export produces one ZIP containing numbered, sanitized page filenames in document order, avoiding partially committed sets of separate files. Show this packaging before export.
- File > Export PDF exports every page, with original page dimensions, correct order, backgrounds and no editor chrome. PDF uses the existing WebView2 runtime rather than requiring a PDF printer or adding a third-party package.
- Edit > Copy as image renders the selection as PNG and writes native Windows bitmap/PNG clipboard formats. It does not overwrite the app's internal object-copy buffer. No selection disables the action; clipboard contention yields a useful error without changing the drawing.
- Successful operations show the destination/count or copy result. Cancel/error leaves the drawing and previous destination unchanged.

## Implementation boundaries

Use the existing renderer through the router render barrier, including assets/fonts, rather than capture the visible canvas. Pin document/session/revision and selected IDs before dialogs. Recheck after native dialogs and between page renders so all output represents one revision; reject stale work before publishing or changing the clipboard.

Extract/reuse shared rendering and destination checks with ExportService. Extend the format/extension policy narrowly for PDF/ZIP. A native destination choice is scoped consent for that operation, not a general path grant. Retain forbidden-path, overwrite, link/reparse checks and final revalidation. Produce bytes first and publish through the existing atomic writer. Enforce total bytes/pixels/page limits before allocation; no silent clipping or downscaling.

Generate PDF from self-contained, barrier-rendered page SVGs in a short-lived host-controlled WebView2 print document. Block scripts, navigation and external network/resource loading; await image/font readiness; use PrintToPdfStreamAsync and atomically publish the returned bytes. Verify mixed page dimensions rather than assume browser print settings preserve them. Fail clearly if the installed runtime cannot produce the requested layout.

Likely files: Toolbar, App dialog integration, EditorModel/host API wiring, ExportService/HostServices, ExportPathPolicy and new small export/PDF/clipboard helpers. Public MCP export behaviour remains compatible. Coordinate these shared files through #30; rebase/merge main changes before implementation and validation.

## Verification and review

Meaningful unit tests for options, limits, page filenames, stale revision rejection and policy extensions. Browser tests for real menu/dialog controls, format/background/selection choices, Cancel and tab switching. Real Windows/WebView2 tests inspect PNG/JPEG/SVG signatures, dimensions, backgrounds and selection crop; ZIP names/count/order; PDF page count/size/rendered contents; and clipboard bytes. Include multi-page documents, embedded pictures, text, gradients and custom geometry. Test overwrite refusal, disallowed paths, renderer failure, clipboard contention and a revision change during multi-page rendering. Assert unchanged snapshots, undo and saved state.

Run frontend unit/typecheck/generated-contract/build/browser and full Windows solution tests before pushing. Keep evidence and limits in a versioned report, obtain read-only code review, open a PR, and move #30 to In review. Manual human acceptance remains NOT RUN until actually performed.

## Alternatives and limits

Direct browser downloads bypass the established host destination policy; visible-canvas capture can omit offscreen content. Both are unsuitable here. A PDF printer adds installation/UI dependencies. The existing WebView2 PDF stream keeps output inside the installed runtime and permits validation before writing.

No physical printing/print preview (W09), multi-process coordination (W15), installation changes or new MCP export formats. PDF fidelity and mixed-size pages are acceptance requirements; unsupported output is reported rather than claimed as success.
