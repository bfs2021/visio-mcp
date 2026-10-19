# R15 — import_visio: read a .vsdx as a reference through MCP

Issue #10. Design approved by the owner in conversation: open the file as a new background document rather than a separate flattened-JSON format.

## Behaviour
- MCP tool `import_visio(path)`. Result: `documentId`, `sessionId`, `revision`, `title`, `pages`, `sourceFile`, `lossy`, `summary`, `diagnostics` (loss report) and `identity` (preserved / generated / repaired element ids).
- The file becomes a **new background document** (same view as `create_document`). Read it with `get_document_summary` / `get_objects` (absolute page coordinates in points; groups, layers, text, geometry and connector ends are the importer's normal output), `render_page`, and `show_document`.
- **Mutation semantics:** read-only for the file, and no already open document is touched. The new document has a fresh id (importing the same file twice gives two documents) and no save path, so it cannot overwrite the reference.
- **Access policy:** `ExportPathPolicy.ResolveReadAsync` — the same approved folders as export (open document's folder, export folder; relative names mean the export folder). Elsewhere needs the person's yes through the existing consent window. UNC/device paths, alternate data streams, reserved names and links leaving an approved folder are refused regardless. The path is revalidated immediately before the bytes are read.
- **Limits and hostile files:** unchanged `ImportOptions` (64 MB package, 256 MB expanded, 2,000 entries, 200 pages, 20,000 shapes) and the importer's existing zip-slip, expansion-bomb and XXE protections. Pictures are stored durably before the document is published.

## Files
`ExportPathPolicy.cs` (read policy), `NativeFileCoordinator.ImportFileAsync` (shared with Open), `DocumentService.ImportVisioAsync`, `LiveControl` allow-list, `agentApp.ts` (`app.adoptDocument` in the agent view), `DiagramTools.ImportVisio`.

## Verification (run 2026-10-19, Windows, no Visio instance used)
- `dotnet test tests/Diagram.Host.Tests`: 152 passed (new read-policy tests).
- `dotnet test tests/Diagram.Visio.Tests`: 151 passed (new `ImportFileAsync` tests: no editor touched, durable pictures, revalidation, damaged file, wrong extension).
- `dotnet test tests/Diagram.Mcp.Tests`: 14 passed (tool listed; only `path` required).
- `npx vitest run` in `src/web`: 399 passed (new `agentAdopt.test.ts`).
- `dotnet test tests/Diagram.Windows.Tests --filter AgentViewTests`: 9 passed, including `AVisioFileOpensAsABackgroundReferenceWithAReportAndLeavesTheEditorAndTheFileAlone`, which imports the Visio-saved `sample-resaved.vsdx` through the live host.

## Importer fixes found by importing real files (2026-10-19)
Importing the owner's `Lego Infrastructure - v2.vsdx` and `Swim-lane_flow_diag.vsdx` through the live host showed importer defects, fixed here:
- **Master merge:** a shape that overrides only some cells of a master row (a `LineTo` with just a `Y`) now takes the others from the master's row, and merged rows are put back in row order so a path opens with its `MoveTo`. Before, swim-lane rectangles came out as triangles and diagonal lines.
- **NURBS and elliptical arcs** are evaluated and flattened instead of being drawn through their control points (this caused long spikes and white wedges across the Lego diagram). A curve that cannot be evaluated becomes a line to its end point and is reported.
- **Open outlines:** a shape whose outlines are all `NoFill` is imported with no fill (11 polyline connectors in the Lego file were filled white).
- **Rules with no width or height** become a 0.01 pt hairline (`zero_size_adjusted`) instead of failing the whole import with "must have positive size".

Result with the owner's files: swim-lane imports with 0 `geometry_approximated` and renders as the original layout; Lego went from 233 diagnostics to 97 (none `geometry_approximated`) and shows SVS, AZ-WDC01, Domain Controllers, BYOD and LVDB that were missing. Renders compared by eye with `artifacts/lego-infra/original-visio.png`.

## Limits
- The owner's Lego and swim-lane files are private and not committed. They were imported once through the live host and judged by eye (above); the committed regression tests build the same situations from small synthetic edits (`ReferenceImportFidelityTests`). The master-merge fix has no committed test of its own.
- Still different from the original Lego diagram: the Azure Active Directory box has no label (stencil text not carried), container borders are solid where Visio's are dotted, and 8 themed colours were approximated as black (`colour_approximated`).
- Fidelity is that of `NativeVsdxImporter`; shapes it reports as approximated appear in `diagnostics`.
- Manual Visio/editor sign-off: **NOT RUN**.
