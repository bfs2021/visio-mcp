# W09 — Print and print preview

Status: revised proposal; review requested for the print-dispatch detail. Issue: https://github.com/rp2839/visio-mcp/issues/31
Related design: [W08 editor export](W08-editor-export.md).

## Outcome

A person can inspect the physical sheets a drawing will use, choose a page range and print layout, then send the reviewed result to a printer or save that same result as PDF. Preview, printer output and Print to PDF must share one composed print document so they cannot disagree about scaling, tiling, margins, orientation or page order.

## Existing foundation

W08 already pins document/session/revision before export, renders pages through the host render barrier, builds a self-contained offline WebView2 PDF document and atomically publishes files through the export path policy. Its PDF export preserves each drawing page's original size. W09 will reuse that rendering, PDF composition and destination policy; print layout will be a separate presentation of a pinned snapshot and will not modify the diagram.

## User flow

- Add **File → Print…** and **Ctrl+P**. It opens an accessible in-app print dialog with a sheet preview before any printing starts.
- Let the user select an installed printer and supported paper size, then choose portrait/landscape orientation, margins, a drawing-page range, and **Fit** or **Tile** layout. Printer and media choices come from Windows print capabilities; unavailable or changed devices are reported and must be reselected.
- Fit places each selected drawing page on one physical sheet while preserving aspect ratio inside the chosen margins. Tile keeps the drawing at its native page scale and splits it into consecutive sheets in row-major order. Tiled sheets have no implicit overlap; the preview makes their count and cut boundaries visible.
- Page ranges use 1-based drawing-page numbers and accept comma-separated pages and inclusive ranges (for example, `1,3-5`). Invalid, reversed, empty or out-of-document ranges are explained before printing.
- Preview uses the same page ordering, paper dimensions, margins, orientation and scale calculations that produce the final print document. Show sheet numbers and the source drawing-page number(s) on each preview sheet; these labels are preview chrome and are not printed.
- **Print** sends the composed sheets to the selected system printer using the exact settings shown in the in-app preview. The in-app dialog owns printer and layout settings; do not open a second settings dialog that could silently change the reviewed layout. **Save as PDF** writes the same composed sheets through W08's approved PDF destination and atomic-write path. File → Export PDF remains the W08 action that exports every drawing page at its original page dimensions.
- Cancel closes the dialog without printing, saving, or changing the document. A stale preview is rejected if the active document, session or revision changes; the user can reopen Print to preview the latest revision.

## Architecture and data flow

1. The host queries installed printer and media capabilities. The editor captures the active document ID, session ID, revision, selected printer and paper dimensions, requested page range and print settings. Settings belong to the print job and are not persisted in the drawing or global preferences.
2. The host validates the captured identity/revision and range, renders only the requested drawing pages through the existing render barrier, and checks that all images are from the same revision. Existing page-count, raster-size and byte limits remain in force; tile expansion has an explicit maximum sheet count and fails with a useful message before printing or publishing.
3. A pure print-layout component maps page SVGs to physical sheets. Fit computes a uniform scale into the printable area. Tile computes deterministic horizontal and vertical slices at native scale, in row-major order. It returns the composed sheet geometry and the source-page mapping used by both the preview and the output renderer.
4. The preview presents those composed sheets without taking a screenshot of the visible canvas. Re-rendering or printing rechecks the pinned revision. One shared offline WebView2 composition path produces print-ready output from the page SVGs; it has scripting and external navigation/resources disabled as in W08.
5. Physical printing submits the composed document to the selected printer with the exact settings used by preview through WebView2's print engine. The in-app preview dialog is the sole settings UI so no second dialog can silently override the selected layout. Saving as PDF serializes the same composed pages and writes via W08's destination checks, overwrite consent and atomic writer. Cancelling in the in-app dialog or encountering a print/PDF error leaves the source drawing unchanged and reports that no file was published where applicable.

The implementation should verify the installed WebView2 runtime's print-settings behavior for printer media, orientation and margins before relying on it. Drawing-page ranges and Fit/Tile are applied in the shared sheet composition layer. Any setting that the runtime cannot preserve consistently must be applied in the shared sheet composition layer or be reported as unsupported; the app must not silently print a layout that differs from preview.

## Boundaries

- No changes to canonical document schema, undo history, saved revision, layers or the W08 Export PDF behavior.
- No printer management, remembered printer settings, copies/collation/duplex controls, custom paper editor, selection printing, page reordering, or external PDF viewer workflow in this issue.
- No new PDF, printer or raster package dependency. Continue using the installed Windows/WebView2 stack.
- Public MCP and DrawScript contracts remain unchanged.

## Failure handling

- Invalid ranges and layouts are blocked before rendering.
- A changed document identity/revision, unavailable printer, unsupported paper size, over-limit sheet expansion, renderer failure, WebView2 print failure, rejected destination or writer failure yields an actionable error and no partial PDF publication.
- Cancelling before print dispatch in the in-app dialog is a normal cancellation and does not change document state.
- If the selected printer reports non-printable margins, the preview uses those margins or warns that the configured margin cannot be honored. Never claim exact edge-to-edge output when the printer cannot provide it.

## Verification

- Unit-test page-range parsing, fit scale and centering, tile row/column counts and ordering, margins/orientation, single-page and mixed-size documents, and upper sheet limits.
- Browser-test File → Print, Ctrl+P, accessible controls, page-range errors, fit/tile preview updates, cancel and tab/revision changes while preview is open.
- Windows/WebView2 tests print to a virtual PDF printer and verify page count, media dimensions, margins, orientation, page ordering, fit/tile placement and image/text fidelity against the in-app preview. Verify Save as PDF uses W08's path policy, atomic output and cancellation behavior.
- Assert every print and PDF action leaves the canonical snapshot, revision, saved marker and undo/redo state unchanged. Include failure cases for stale revision, printer unavailable, rendering timeout, rejected path and cancelled native dialog.
- Record automated evidence separately from human acceptance. Do not mark real-printer acceptance complete unless it is performed on a real printer.

## Alternatives considered

- **Open the W08 PDF in another application:** low implementation cost, but it does not provide an in-app preview or guarantee that fit/tile settings match the preview.
- **Create a separate native drawing/printing renderer:** offers printer-specific control but duplicates rendering and risks output differences from the editor and W08 PDF path.
- **Recommended: shared print-sheet composition:** reuse W08's pinned snapshot and SVG pipeline, add a print-only layout and preview, and feed the identical composed sheets to printer/PDF output. This keeps output consistent and avoids document mutations or a new rendering dependency.

## References

- W09 issue: https://github.com/rp2839/visio-mcp/issues/31
- W08 issue: https://github.com/rp2839/visio-mcp/issues/30
- W08 design: [docs/design/W08-editor-export.md](W08-editor-export.md)
- WebView2 print settings reference: https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2printsettings
