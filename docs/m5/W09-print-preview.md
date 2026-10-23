# W09 — Print and print preview

The editor now prepares revision-pinned print sheets from the selected drawing pages. The Print dialog shows the exact host-composed SVG sheets, paper size, source-page mapping and any printer-margin adjustments. Settings changes invalidate the prepared job; print and Print to PDF accept only the host-issued, one-use job ID. Print-layout PDFs preserve the selected sheet dimensions, while W08's Export PDF continues to export all drawing pages at their original sizes.

Automated verification on 2026-10-23:

- `dotnet test Diagram.Windows.slnx --no-restore` — passed: contracts 50, IPC 33, MCP 14, Visio 156, Host 194, Windows 154 (601 total). After the final printer-capability freshness guard was added, the focused Windows print suite passed 14/14.
- `dotnet test tests/Diagram.Windows.Tests/Diagram.Windows.Tests.csproj --no-restore --filter FullyQualifiedName~EditorExportTests` — passed 8/8; W08 export behavior remains intact.
- `npm test` — passed 55 files, 513 tests.
- `npm run typecheck` — passed.
- `npm run build` — passed. Existing warnings remain for maxgraph's direct `eval` use and the large browser-context chunk.
- `npm run test:browser` — passed 160/160, including print dialog, settings invalidation and Ctrl+P coverage.
- `npm run generate:check` — passed.
- The two-page print-layout PDF integration test verifies source-page order, exact nested SVG equality with the host renderer (including Unicode text), and Letter-sized output pages.

Physical-printer acceptance: **NOT RUN**. Automated dispatch uses an injected printer seam and verifies that an unavailable selected printer is reported without switching devices. No physical printer smoke test was performed.

Implementation commits on `codex/w09-print-preview`: `654aae8`, `edef7d6`, `b010985`, and `28a3dd0`. The branch also contains the pre-existing R05 baseline correction `790d009`, which refreshed the verified provenance hash discovered during the approved baseline investigation.
