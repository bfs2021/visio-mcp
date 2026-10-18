# Supplementary I52 MCP sign-off — 2026-10-18

Source under verification: GitHub main `9ec82c3` (clean fast-forward from `a684649`). No source-code conflicts or product edits were needed.
This is an evidence update, not a replacement of the specification's manual acceptance qualifiers.

## Screenshot reproductions from the real user/agent session

The agent used the installed stdio MCP server and application to reproduce the user's dark screenshots as editable light diagrams.
The user's response to the compatibility decision was “That's really great!” before requesting the next example.
The original repository-diagram run has [request/result logs and a test report](../../../../../artifacts/reference-diagram/TEST-REPORT.md).
The four later files were retained in the user's `Documents/AgenticDiagram/Exports` folder; they do not have complete archived MCP transcripts.

`screenshot-files/` contains byte-for-byte copies of those five original exports, including the repository drawing.
The later exports are cumulative: the final application-release-flow file has all four later diagrams on separate pages.
These files establish actual output from the scenario; they alone cannot establish transaction atomicity, unrelated-object preservation,
DrawScript use, app reimport, connector behavior, or a manual Office edit pass.

[screenshot-file-verification.json](screenshot-file-verification.json) records fresh read-only native Visio opens, source timestamps,
SHA-256 hashes, target page names, editable shape/text counts, expected labels and unique `User.AgentId` values.
The verification script neither saves nor edits the originals or archived drawings; native automation suppresses alerts and makes no claim of a human observing repair prompts.
Reproduce on a Windows machine with Visio:

```powershell
./docs/acceptance/evidence/I52/mcp-signoff/verify-screenshot-files.ps1
# To verify the archived copies without the original user's export folder:
./docs/acceptance/evidence/I52/mcp-signoff/verify-screenshot-files.ps1 -SourceDirectory ./docs/acceptance/evidence/I52/mcp-signoff/screenshot-files
```

## Relevant acceptance coverage

| Definition-of-done item | Evidence accepted | Boundary |
|---|---|---|
| 1, 2, 6 | Human-directed screenshot reproduction with individually editable shapes and styled text | Does not exercise every manual editor control |
| 8, 15 | Native VSDX shape/text inspection and stable UUIDs in all five target pages | Not a claim of pixel-identical screenshot reproduction |
| 10, 11, 12, 13 | Repository-run `get_document_summary`, `apply_operations`, `render_page`, `inspect_layout`, `export_document` logs; live-stack regression tests | Original layout inspection had thin-rule warnings; no misalignment detection |
| 20 | `LiveControlTests.VisualLoopInspectRenderOneObjectPatchLeavesTheRestExact`, measured text-fit preservation test and demo snapshot comparison | A surviving export by itself does not prove this |
| 9, 14, 16, 17, 19 | Full real MCP/editor demonstration and Office acceptance suite | These are automated, not inferred from the screenshot files |
| 18/19 manual | Earlier [M0 G1 manual results](../../../../m0/results.md) and newly pulled G1 Visio saves/Word document | Historical manual coverage; no new full current-build manual pass claimed |

## Reverification on latest source

`test-results/*.trx` records the full .NET solution run, including the MCP shim tests and real Windows editor tests.
The Windows suite starts isolated application instances and unique pipes; it does not modify the user's live document.
Relevant tests include all `LiveControlTests`, `ReferenceDiagramTextFitTests`, `ReferenceComparisonTests`, `VectorIconTests`,
`LayoutComponentTests`, `DemoTests` and `OfficeAcceptanceTests`.

```powershell
npm --prefix src/web test
npm --prefix src/web run typecheck
npm --prefix src/web run generate:check
npm --prefix src/web run build
npx --prefix src/web playwright test --config src/web/playwright.config.ts
dotnet test Diagram.Windows.slnx --logger 'trx;LogFilePrefix=i52-signoff' --results-directory ./docs/acceptance/evidence/I52/mcp-signoff/test-results
node tools/demo/verify-demo.mjs
```

`verification-summary.json` records counts and scope. The demo verifier checks 49 semantic assertions against saved transaction reports and snapshots;
`DEMO/result.json` must also have every step PASS and no NOT RUN entries. The live installed MCP additionally answered `get_document_summary`
successfully ([live-summary.json](live-summary.json)); this smoke check is separate from testing the newly built source.

Fresh results: **301 frontend tests, 47 browser tests, and 331 .NET tests passed** (Contracts 24, Host 119, MCP 13, Visio 124, IPC 17, Windows 34).
Typecheck, production build and generated-contract checks passed. The newly generated demonstration passed **49/49** checks with no NOT RUN steps;
its 15 recorded entries cover all 14 steps (step 14 checks both the app export and Visio's saved file).
All five original screenshot files passed native verification: **248 shapes with unique stable IDs and 117 nonempty text objects** on the target pages.

The first sandboxed Vitest attempt could not load modules from sandbox temporary paths (ENOENT); rerunning with normal filesystem access passed.
That environment failure was not counted as a product pass. Existing maxGraph eval/chunk-size and C# analyzer warnings remain warnings.

## Sign-off boundary

MCP-driven creation/read/edit/render/inspection/export, transaction safety, the complete automated demonstration and automated Office checks
are signed off to the extent shown by the attached passing results. Current-build manual editor/Visio/Word acceptance, the second-user pipe check,
misalignment detection, the test-harness startup pause and I51 cleanup/recovery gaps remain explicit open items.
Unrelated generated screenshots and timing-file changes were restored to the pulled versions; this run is not a new controlled benchmark campaign.
