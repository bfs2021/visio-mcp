# W09 Print and Print Preview Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (only if the owner chooses it) or `superpowers:executing-plans`. Execute one task at a time, following test-first steps.

**Goal:** Add an in-app print preview that sends the exact reviewed sheets to a selected printer or saves them as a print-layout PDF.

**Architecture:** Extend the existing editor export path. The host pins the current document/session/revision, renders selected page SVGs through `BridgeRouter.RenderAsync`, and uses a pure layout planner to compose physical-sheet SVGs. A bounded, one-use host job preserves those exact sheets between preview and output. The React dialog owns all printer and layout controls. A hidden offline WebView2 composes the same sheets for PDF or dispatches them through `CoreWebView2.PrintAsync` with the selected device settings. Existing File → Export PDF behavior stays unchanged.

**Tech Stack:** TypeScript 7, React 19, Vitest, Playwright, .NET 10 WPF, WebView2 1.0.4258.31, xUnit.

**Spec:** [`docs/design/W09-print-preview.md`](../../design/W09-print-preview.md)

## Global constraints

- All output paths consume one host-composed sheet representation; preview, printer output, and Print to PDF share order, paper size, scale, tile slicing, margins, and orientation.
- Pin `ExpectedState` (document ID, session ID, base revision). Recheck it after rendering and immediately before output; reject stale jobs.
- Keep print settings transient. Do not add print settings to document schema or global preferences.
- Preserve W08's existing `PdfExport.CreateAsync` and File → Export PDF semantics: all drawing pages, original page dimensions.
- Use offline WebView2 rendering with the current script, navigation, and resource restrictions. Add no printer, PDF, or raster dependency.
- Reuse the export service's 32-page, 64 MiB, export path policy, overwrite consent, and atomic writer limits. Enforce a separate tested cap of 32 composed sheets.
- The client never supplies SVG, printer path, or a filesystem destination. Host selects paths through the established native dialog and validates again before publication.
- Neither preview, print nor Print to PDF changes document content, revision, saved state, undo, or redo.

## Review focus

- Parser and planner reject malformed/out-of-range ranges, invalid/non-finite dimensions, non-positive printable area, and expansion over 32 sheets before expensive allocation.
- Margins use the greater of the requested value and device-reported non-printable margins; make any adjustment visible before output.
- If printer discovery, selected media, WebView2 print settings, or dispatch fails, report the failure without silently substituting a device or layout.
- A stale/expired/consumed prepared job cannot print or publish a PDF.
- Cancel, rejected path, and output errors leave the drawing and any existing destination unchanged.

## Files and responsibilities

- Create `src/Diagram.App/PrintLayout.cs` for page-range parsing, fit/tile planning and deterministic composed sheet SVGs.
- Create `src/Diagram.App/ExportService.Print.cs` as an `ExportService` partial for device discovery, prepared jobs, preview, printing, and print-layout PDF output.
- Modify `src/Diagram.App/PdfExport.cs` by adding a print-sheet path while leaving the existing `CreateAsync(MainWindow, IReadOnlyList<(Page Page, byte[] Bytes)>, CancellationToken)` behavior intact.
- Modify `src/Diagram.App/HostServices.cs` to route `host.print` into the export service and serialize each print action through the existing file-action gate.
- Create `src/web/src/editor/EditorPrintOptions.ts` and `src/web/src/components/PrintPreviewDialog.tsx`; modify `EditorModel.ts`, `appCommands.ts`, `App.tsx`, `main.tsx`, and `styles.css` for state, host bridge, File → Print, Ctrl+P, controls, and preview.
- Create `tests/Diagram.Windows.Tests/PrintLayoutTests.cs`, `tests/Diagram.Windows.Tests/EditorPrintTests.cs`, `src/web/tests/printPreview.test.ts`, and `src/web/tests/browser/printPreview.spec.ts`. Add W08 regression assertions in `tests/Diagram.Windows.Tests/EditorExportTests.cs`.
- Create `docs/m5/W09-print-preview.md` with fresh automated evidence and physical-printer acceptance status.

## Task 1: Pure range and sheet planning

**Interfaces**

- `PrintPageRange.Parse(string range, int pageCount)` returns unique 1-based source page numbers in document order; invalid input throws `PrintLayoutException` with a stable code and user-safe message.
- `PrintLayoutPlanner.Build(IReadOnlyList<PrintSourcePage>, PrintLayoutOptions)` returns ordered `PrintSheet` records with physical dimensions, source-page numbers, and self-contained SVG.
- `PrintSourcePage` has page ID, page number, width/height in points, and source SVG. `PrintLayoutOptions` has paper dimensions, orientation, requested/hardware minimum margins, and `fit` or `tile`. `PrintSheet` has sheet number, physical dimensions, source-page numbers, and SVG.
- Effective margins are per-edge max(requested, hardware minimum). Fit uniformly scales and centers each source page in one printable area. Tile preserves page-point scale and slices row-major. Every sheet SVG includes its physical paper canvas and places the source SVG inside the printable region.

**TDD steps**

- [ ] Add parser tests for whitespace, `1,3-5`, overlaps/duplicates, empty/malformed/reversed/zero/out-of-range input, and stable error codes.
- [ ] Run `dotnet test tests/Diagram.Windows.Tests/Diagram.Windows.Tests.csproj --filter FullyQualifiedName~PrintPageRange` and observe the missing implementation fail.
- [ ] Implement the parser; rerun focused tests.
- [ ] Add planner tests for fit scale/centering, orientation, asymmetric/effective margins, tile rows/columns/order, mixed source sizes, 1-point overflow, invalid dimensions, zero printable area, maximum sheet count, and deterministic SVG geometry.
- [ ] Run the focused planner test and observe missing behavior fail; implement deterministic planning and SVG composition; rerun all `PrintLayout` tests.
- [ ] Commit `feat(w09): plan print ranges and sheets`.

## Task 2: Pinned host preview and prepared-job lifecycle

**Interfaces**

- `PrintJobStore` retains at most one prepared job per host window; a new preview replaces the old job. Jobs use random opaque IDs, expire after five minutes, are one-use, and are capped at 64 MiB. Validate ID, document/session/revision and expiry when taking the job; clear it when the owning document/session closes or is replaced.
- `IPrinterCapabilities` is an injectable seam returning installed printer names, supported media dimensions, and imageable-area margins. Production discovery uses Windows printer capabilities; tests use a fake and do not require an installed printer.
- `host.print` actions: `devices`, `preview`, `print`, `savePdf`. Preview accepts expected state, selected page range, device/media and layout options; it returns job ID, exact sheet SVG previews, resolved options and visible margin adjustment warnings. Output accepts only the opaque job ID. No caller-supplied SVGs or paths.
- Extend `ExportService` with `HandlePrintEditorAsync`. It follows `HandleEditorAsync`'s expected-state checks, render barrier, limits, approved destination picker, path policy, and writer. It renders only selected pages through `Router.RenderAsync` and stores the composed sheets.

**TDD steps**

- [ ] Add failing store tests for one-use IDs, scope/revision mismatch, expiry, replacement, size limit, and session cleanup.
- [ ] Add failing capability/preview tests for no printers, unsupported media, margin adjustment, malformed settings, render revision mismatch, page/sheet limits, and no stored job on failure.
- [ ] Implement the store, capability seam, and preview preparation; render and validate all pages against one `ExpectedState` before exposing a job.
- [ ] Add failing host-route tests for `host.print` actions and verify the new/open/save/export gate behavior; add the route in `HostServices.cs` and clear jobs on document close/replacement.
- [ ] Add output guard tests for stale active tab/revision, expired/consumed job, and no print/write callback after rejection. Implement final validation immediately before the output callback.
- [ ] Run `dotnet test tests/Diagram.Windows.Tests/Diagram.Windows.Tests.csproj --filter FullyQualifiedName~EditorPrint`; commit `feat(w09): prepare revision-pinned print jobs`.

## Task 3: Printer and print-layout PDF output

**Interfaces**

- Add a `PdfExport.CreatePrintSheetsAsync` path accepting the prepared `PrintSheet` sequence and producing one PDF page per sheet at its declared paper size. Keep W08's original method and call site unchanged.
- Add `PdfExport.PrintAsync` accepting the same prepared sheets and a validated `PrintDeviceSettings`, returning a typed print result. It dispatches through WebView2 `CoreWebView2.PrintAsync` to the named device and applies the selected media/orientation/margins. The in-app dialog is the only settings UI; no second native UI may alter the reviewed layout.
- Print to PDF uses W08's approved save dialog, `ExportPathPolicy`, `AtomicFileWriter`, and final expected-state/destination checks. It does not route through Microsoft Print to PDF.

**TDD steps**

- [ ] Add failing tests for a mixed-paper-size print-layout PDF and regression assertions proving W08 export still exports every original-sized drawing page.
- [ ] Add a print-dispatch seam and tests for exact device settings, unavailable device, WebView2 failure/cancellation, and no fallback printer; do not require a physical printer in automated tests.
- [ ] Extend the offline WebView2 document to load composed sheet SVGs under the existing CSP and network restrictions, with a CSS page size per sheet.
- [ ] Implement physical dispatch with the pinned WebView2 runtime. Validate every print setting the runtime exposes and fail visibly if it cannot honor the preview. Add no second settings UI.
- [ ] Implement print-layout PDF serialization and approved atomic publication from the prepared job.
- [ ] Run `dotnet test tests/Diagram.Windows.Tests/Diagram.Windows.Tests.csproj --filter FullyQualifiedName~EditorPrint` and `... --filter FullyQualifiedName~EditorExportTests`; commit `feat(w09): print composed sheets and save print pdf`.

## Task 4: Editor controls and end-to-end preview behavior

**Interfaces**

- `EditorPrintOptions` contains page range, printer/media, orientation, per-edge margins, and `fit | tile`.
- `EditorPrintHost` exposes device listing, preview preparation, print, and save-PDF calls. Results are discriminated; only the host returns a prepared-job ID.
- `EditorModel.beginPrint()` opens the dialog; changing settings invalidates the current prepared ID; output is enabled only after a matching fresh preview; cancel discards transient state.
- The dialog displays the host-produced SVG sheets, sheet count and source-page mapping; no client-side layout calculation is authoritative.

**TDD steps**

- [ ] Add failing model tests for open/cancel, preview preparation, settings invalidation, stale document/revision, print/PDF result and actionable error cases.
- [ ] Add File → Print and Ctrl+P using existing command/shortcut patterns, then add the host bridge request in `main.tsx`.
- [ ] Implement the accessible dialog with device/media, page range, orientation, margins and fit/tile controls. Prepare a new host job for each settings change; expose errors and a clear previewing/printing state.
- [ ] Add browser tests for menu/shortcut entry, valid/invalid ranges, fit/tile, margin warnings, settings refresh, active-tab/revision changes, cancellation and disabled output until prepared.
- [ ] Run from `src/web`: `npm test -- tests/printPreview.test.ts`, `npm run typecheck`, and `npm run test:browser -- --grep "print preview"`; commit `feat(w09): add print preview dialog`.

## Task 5: Regression, Windows verification and evidence

- [ ] Add host integration tests for a mixed-size document with selected pages, fit output dimensions, tile sheet count/order, Unicode and embedded SVG content, comparing output pages against the exact preview sheets with a documented antialiasing tolerance.
- [ ] Add failure/preservation tests for printer/media loss, stale revision, cancel, render timeout, rejected PDF destination and failed atomic write; assert no drawing/history or destination mutation.
- [ ] Run focused .NET tests: `dotnet test tests/Diagram.Windows.Tests/Diagram.Windows.Tests.csproj --filter FullyQualifiedName~EditorPrint` and `... --filter FullyQualifiedName~EditorExportTests`.
- [ ] Run branch checks: from `src/web`, `npm run generate:check`, `npm test`, `npm run typecheck`, `npm run build`, `npm run test:browser`; from repository root, `dotnet test Diagram.Windows.slnx`.
- [ ] Record exact results and a real-printer smoke test in `docs/m5/W09-print-preview.md`. If no printer is available, mark physical-printer acceptance **NOT RUN**. Automated print-dispatch seam tests do not count as a physical printer check.
- [ ] Run `git diff --check`, verify changed files against the plan and preserve unrelated `artifacts/publish/` files. Commit `test(w09): verify print preview and output`.

## Self-review

- Coverage: page ranges, fit/tile, media/orientation/margins and composed previews (Tasks 1 and 4); pinned identity and stale-state rejection (Task 2); physical print, print PDF and W08 regression (Task 3); cancel/failure preservation and acceptance evidence (Tasks 2–5).
- Cross-task types: Task 1 defines `PrintSheet`; Task 2 stores and returns it; Task 3 consumes it; Task 4 passes only host-issued IDs and displays its preview; Task 5 verifies the same page mapping/output.
- Scope boundaries: no MCP tool/schema changes, no document schema changes, no persistent preferences and no new dependency.
- Main risk: Windows printer capability reporting and the installed WebView2 runtime may expose or honor settings differently. The print seam makes failure testable; Task 3 must verify runtime behavior and report unsupported settings rather than silently producing a different result.
