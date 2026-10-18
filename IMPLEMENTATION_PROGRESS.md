# Implementation progress

Updated: 2026-10-18 (Europe/London).

Status: **M0 (I00 to I05) and M1 (I10, I11, I12) done, see [docs/m1/results.md](docs/m1/results.md); I20, I21, I30 and I31 done, see [docs/m2/results.md](docs/m2/results.md); I40 done, see [docs/m4/results.md](docs/m4/results.md); I41 done, see [docs/acceptance/WA-results-2026-10-17.md](docs/acceptance/WA-results-2026-10-17.md); I50 done, see [docs/m5/results.md](docs/m5/results.md)**; I51 done, see [docs/m5/results.md](docs/m5/results.md); I52 done, see [docs/acceptance/definition-of-done.md](docs/acceptance/definition-of-done.md) and [docs/acceptance/performance.md](docs/acceptance/performance.md). This file splits future implementation into bounded work
packages. It is a progress and dependency register, not a claim that feasibility
spikes, builds, tests or Windows acceptance have run.

The original [implementation plan](docs/superpowers/plans/2026-10-02-agentic-diagram-implementation.md)
was reviewed by Claude; Codex incorporated the requested corrections. The package table
below records implementation status. Older planning and milestone narratives describe
the state at the time they were written, rather than the current product state.
Package ownership below is proposed: Claude currently
has read-only broker access, so a future implementation session must either enable
write-capable delegation or have Claude return reviewable patches for Codex to apply.

| ID | Milestone / package | Proposed implementer | Depends on | Status | Exit evidence |
|---|---|---|---|---|---|
| I00 | Freeze shared contracts, dependency versions/licences for isolated probes | Claude (implemented); Codex review | Design review | done (Codex review 68fcf783 corrections applied) | `spikes/contract-tests` 16/16 pass; versions in `docs/m0/dependencies.json` |
| I01 | M0: OfficeIMO editable VSDX round-trip harness | Claude design / Codex execution | I00 | done: PASS with backend caveat | `docs/m0/evidence/G1/report.md`; `spikes/vsdx.tests` 10/10 on generated and Visio-saved v7 |
| I02 | M0: maxGraph interaction and PNG probe | Codex | I00 | done: PASS | `docs/m0/evidence/G2/report.md`; Playwright 3/3 |
| I03 | M0: stdio MCP → pipe → test host probe | Claude design / Codex execution | I00 | done: PASS (second-user check NOT RUN) | `docs/m0/evidence/G3/report.md`; xunit 14/14 |
| I04 | M0: WebView2 → live frontend operation probe | Codex | I00, I02 | done: PASS | `docs/m0/evidence/G4/report.md`; xunit 19/19 |
| I05 | M0: Windows Visio/Word acceptance and backend decision | Codex + Windows tester | I01–I04 | done: conditional GO recommended | `docs/m0/results.md`, `results.json`, `decision.md` |
| I10 | M1: minimal canonical store, transaction engine, inverse history | Codex | I05 | done | 115 vitest + 24 C# parity tests; `docs/m1/results.md` |
| I11 | M1: WPF/WebView2 shell and JSON debug save/load | Codex; Claude review | I05, I10 | done | 69 host + 10 Windows tests; `docs/m1/evidence/I11/editor-in-webview2.png` |
| I12 | M1: one-page canvas, basic elements and inspector | Codex | I10, I11 | done | 9 Playwright tests; `docs/m1/evidence/I12/editor.png` |
| I20 | M2: full model and operations, groups, arrange, target resolution | Codex | I12 | done | 65 engine tests plus 5 browser tests; `docs/m2/evidence/I20/grouped-and-aligned.png` |
| I21 | M2: DrawScript parser/compiler and transaction drawer | Codex; Claude review | I20 | done | 31 script tests plus 2 browser tests; `docs/m2/evidence/I21/script-and-transactions.png` |
| I30 | M2: production IPC, admission gate and live MCP tools | Codex; Claude review | I11, I20, I21 | done | 15 IPC + 9 MCP + 11 admission + 8 live-control tests; `docs/m2/results.md` |
| I31 | M3: semantic queries, render snapshots and layout feedback | Codex | I12, I20, I30 | done | 9 layout + 6 render-planning + 7 browser pixel tests + 2 image-content + live visual loop; `docs/m3/evidence/I31/` |
| I40 | M4: production VSDX mappings and compatibility diagnostics | Codex; Claude review | I01, I20, I30, I31 | done | Feature matrix and supported-subset round-trip tests |
| I41 | M4: Visio/Word manual acceptance suite | Windows tester + Codex | I40 | done | Editable objects, glue, UUID retention, Word activation |
| I50 | M5: multipage/layers and approved asset library | Codex; Claude review | I20, I40 | done | Page/layer operations; one-logo and global-replace tests |
| I51 | M5: durable recovery, security limits and failure handling | Codex; Claude review | I11, I30, I40, I50 | done | Crash recovery and hostile-input boundary tests |
| I52 | M5: performance measurements and complete demo | Codex + Windows tester | I31, I41, I50, I51 | done (2026-10-18), with open items | All 14 spec §23 steps pass on the real stack (49 verifier checks); every §27 criterion has evidence, 5 with stated limits; §18 timings recorded in Debug and Release. Open: M0 second-user pipe check NOT RUN, no human usability or manual Visio/Word pass, no misalignment detector. See [definition-of-done](docs/acceptance/definition-of-done.md), [performance](docs/acceptance/performance.md), [demo evidence](docs/acceptance/evidence/DEMO) |

Input validation and safe IO are introduced in the first package that accepts
input, then tested and hardened in I51. They are not postponed until M5.
M1 includes a minimal command engine because its GUI and undo must already use
canonical transactions; M2 expands it instead of introducing a competing engine.

## Diagram reproduction improvements

Added 2026-10-18 following the light-mode reference-diagram functionality test.
These supplement I51/I52; they are not prerequisites already delivered by earlier packages.
Evidence: [reference-diagram test report](artifacts/reference-diagram/TEST-REPORT.md).
The probe reproduced 62 editable objects and all 39 text blocks, with icon approximations,
Visio wrapping differences that required correction, and five false thin-rule warnings.

| ID | Improvement | Depends on | Status | Exit evidence |
|---|---|---|---|---|
| R01 | Discoverable MCP operation schemas, examples, units, limits and targeting guidance | I30, canonical contracts | done (2026-10-18); installed companion updated | 29 self-contained schemas and validated examples; offline real-MCP canonical parity test; 12 MCP tests and 258 frontend tests pass; typecheck passes; published companion verified outside repo. See [R01 results](docs/m5/R01-operation-discovery.md) |
| R02 | Text measurement and fitting, including Visio export-aware checks | I31, I40 | done (2026-10-18); update installed alongside open app | get_text_layout with revision-tagged grow-box/shrink-font proposals and shared layout metrics; 267 frontend tests, 13 MCP tests, 24 Windows tests; typecheck/build pass; native Visio arrow widths fit. See [R02 results](docs/m5/R02-text-layout.md) |
| R03 | Consistent, searchable vector icons and custom geometry across editor, render and Visio | I12, I31, I40 | done (2026-10-18); development branch, activation pending | 26 pinned Lucide vectors; searchable Icons panel and read-only search_icons/get_icon previews with editable proposals; size/colour/stroke controls; shared supported path rendering; exact native round trips, live MCP/browser checks and real Visio gallery verification. See [R03 results](docs/m5/R03-vector-icons.md) |
| R04 | Reusable layout components with padding and alignment rules | I20, R02; R03 for icon-bearing components | done (2026-10-18); development branch, activation pending | Four measured editable templates; versioned MCP discovery and preview/create/reflow proposals; UI controls; current text/style/ID preservation and explicit alignment; plain SVG wrapping; real MCP native round trip and native Visio checks for four groups/32 fitting text boxes. See [R04 results](docs/m5/R04-layout-components.md) |
| R05 | Reference-diagram regression fixture and improved layout diagnostics | I31, I40; incremental coverage of R01–R04 | partial coverage from R02; not complete | R02 adds the 62-object fixture, reference fitting/preservation/export test and native Visio width verifier. Remaining: broader semantic/geometry and tolerant image comparisons; valid thin rules no longer receive ZERO_SIZE warnings; preserve explicit approximation reports |
| R06 | Enlarged source-reference crops through MCP | I31, I50 | done (2026-10-18); installed update requires app/MCP restart | render_reference_region resolves approved raster IDs and pinned hashes, crops integer source pixels and magnifies with exact nearest-pixel sampling; dimensions/mapping and invalid/oversized crop checks. See [R06/R07 results](docs/m5/R06-R07-reference-comparison.md) |
| R07 | Paired reference/canvas comparison through MCP | I31, R06 | done (2026-10-18); installed update requires app/MCP restart | compare_reference returns side-by-side or 50% overlay PNGs, one committed revision and source/canvas mappings including raster margins; real branch/container fixtures; source-version/document-pin/read-only/pixel/limit checks; no automatic alignment or accuracy score. See [R06/R07 results](docs/m5/R06-R07-reference-comparison.md) |
| R08 | Orthogonal connector routing that honours port direction and explicit waypoints, carried into Visio | I20, I40 | proposed (2026-10-18) | Swim-lane test: the editor's elbow route ignored port side and waypoints (arrows entered top ports sideways; two multi-bend routes collapsed). Straight-with-waypoints drew correctly in the editor but Visio dropped the waypoints (`waypoints_dropped`), leaving diagonal lines. Need an elbow/polyline route that leaves and enters at the port's side, accepts waypoints, and exports as a Visio connector with matching geometry |
| R09 | Swim-lane and phase-column structures (container with lanes and columns) | I20, R04 | proposed (2026-10-18) | The 5 lanes, 3 day columns, title bar and header cells took about 20 hand-placed objects. Need a lane/phase component (resizable lanes, labels, colours) that shapes drop into, reports lane membership and exports as a Visio swim-lane list. Layout checks gave 15 CONNECTOR_CROSSES_SHAPE and 4 OVERLAP warnings that were only lanes, strips and the frame, so containers must be understood by the inspector |
| R10 | Flowchart shape set (terminator, stored data, document, lined document, process, decision) | R03 | proposed (2026-10-18) | Terminator, stored-data and lined-document shapes were authored as `svgPath`; Visio draws their curves as short straight segments (`curves_flattened`). Need named presets with native Visio geometry and port sets |
| R11 | Gradient fills | I12, I40 | proposed (2026-10-18) | Original lane labels and shapes use Visio theme gradients; paints are solid or none only, so the recreation is flat. Needs a linear-gradient paint rendered in editor, snapshot and exported to Visio |
| R12 | Line element and connector styling (free line, arrowhead size, open ends) | I20 | proposed (2026-10-18) | Column rules and the dashed Day 2 line had to be unglued connectors and raised 8 UNGLUED_CONNECTOR_END warnings. Arrowheads are large relative to Visio's at 2.25 pt. Need a line primitive without glue warnings and an arrowhead size setting |
| R13 | Label attached to a connector and shape-on-connector placement | R04 | proposed (2026-10-18) | The original puts a small "PSGL Report" document on a connector; the recreation placed it by coordinates. Need a connector-anchored object or label that follows reroutes and moves with the connector |
| R14 | Text defaults and fit for reference reproduction (insets, vertical lane titles, default size) | R02 | proposed (2026-10-18) | Text had to be given explicit line breaks and per-shape sizes to wrap like the original; rotated lane titles needed a manual pre-rotation frame. Need inset/wrap control matching Visio and a rotated-text option on lane labels |
| R15 | Import a `.vsdx` (or read its structure) through MCP as a reference | I40 | proposed (2026-10-18) | No MCP tool opens a Visio file. Both comparison tests had to hand-parse the package XML (positions, groups, layers, geometry, text, arrows) to place objects exactly. An `import_visio_reference` tool returning a flattened, absolute-coordinate description (or an editable import with a loss report) would remove that work |
| R16 | Multi-branch connectors, joins and automatic line hops | R08, R12 | proposed (2026-10-18) | The Lego diagram has 67 lines that the original draws as many single-segment shapes with T-junctions (PAF/Logging/PWF… bus into MEEP) and 5 hand-placed arc "hops" where lines cross. Need connector branches that join an existing connector, and optional automatic hops at crossings, both carried to Visio |
| R17 | Nested containers whose contents move and resize together | R09 | proposed (2026-10-18) | Public cloud > tenant > component frames (four levels, dashed outlines, 66% transparent fills) were 14 loose rectangles with no containment. Need container objects with membership, inspector awareness (no false OVERLAP/CROSSES) and Visio container export |
| R18 | Reusable symbol shapes with an embedded label and ports (database cylinder, cloud, server) | R10 | proposed (2026-10-18) | The original uses stencil masters (13 blue databases, a cloud, an Azure AD icon). They were rebuilt as a cylinder plus a separate text box, and the Azure icon as a diamond. Need named symbols with label, ports and size behaviour that survive Visio export |

R08–R14 came from the PSGL swim-lane recreation and R15–R18 from the Lego infrastructure recreation (2026-10-18). Evidence and side-by-side Visio renders: [swim-lane comparison](docs/m5/R08-swimlane-comparison.md) and [Lego infrastructure comparison](docs/m5/R15-lego-infrastructure-comparison.md).

R06/R07 are delivered. Prioritise R03 next, then R04, while retaining I51 recovery as reliability work.
Use R05 incrementally and include the reproduction workflow in I52 final acceptance.

R06/R07 were requested after the split application-release-flow reproduction. Existing
`render_region` already enlarges canvas details by up to 8×, but cannot show a source
screenshot crop beside the result. Manual enlargement exposed gaps between the first
branch-icon segments; correcting them demonstrated the value of a paired inspection loop.
Small raster references still limit the detail that can be recovered; magnification
must not imply that an exact original vector or icon identity has been inferred.

## Evidence register

Planning evidence: four plan documents (overview, M0, editor, host/integration),
covering all 18 packages. Claude review task `7a968559-bffe-4a68-bbf1-150eaf5f4323`
returned **approve with corrections**. Codex incorporated all eight important
findings and the minor contract/coverage corrections. The exact verdict is preserved
in [Claude's review](docs/design/collaboration/claude-implementation-plan-review.md),
with [dispositions](docs/design/collaboration/implementation-review-disposition.md).
No additional post-correction Claude verdict is claimed.

Document verification passed: all packages covered once, no dependency cycles,
all local links/code fences valid, review-required interfaces/test cases present
and stale field/interface names removed. Production tasks retain their not-started
status; these checks are not runtime, build or feasibility evidence.

I00 (2026-10-02, Fedora 44, Node 24.21.0): in `spikes/contract-tests`, `npm test` went red
(6/8 failed on a placeholder schema) then green (8/8). Codex review task
`68fcf783-d9e6-430e-8423-4bab0b5cfe95` returned **approve with corrections** (2 important,
4 minor), all applied: the schema is now request-only and rejects scope in `params` for every
method; the regression tests went red (1/16) on the old schema, then `npm ci && npm test` passed 16/16. Probe versions, licences and
hashes are in [dependencies.json](docs/m0/dependencies.json); run details in
[environment.json](docs/m0/environment.json). .NET was not installed, so only npm deps were
installed; NuGet/.NET entries are pinned from registry metadata, not built.

M0 (2026-10-17, Windows 11, .NET 10.0.402, Node 24.14.1, WebView2 155.0.4283.60, Visio and Word Microsoft 365): I01 to I04 were
built test-first and pass; the user ran the Visio and Word checks by hand. G1 passes only with post-save patches over OfficeIMO.Visio 3.4.4.
Not probed and carried to M4: layer hide/lock/no-print, alpha fill, contain-fit image, hidden shape, document identity, copy/paste identity
collisions, group angle normalisation, orthogonal connectors with user waypoints. Not run: second-Windows-user pipe refusal. Details in
[results.md](docs/m0/results.md); the go/no-go is [decision.md](docs/m0/decision.md) (recommended GO with conditions, awaiting owner confirmation).

M1 (2026-10-17, same Windows machine): I10 to I12 built test-first. The engine, bridge and canvas logic have 115 vitest tests, the editor has 9
browser tests, the host has 69 C# tests and the real WPF/WebView2 shell has 10. Two bugs were found only by running the real thing (a bundle import
that broke inside WebView2, and shapes painted underneath the page sheet); both are fixed and now covered. Known gaps are listed in
[docs/m1/results.md](docs/m1/results.md). The M0 second-user pipe check remains open.

M2 so far (2026-10-17, same Windows machine): I20 and I21 built test-first. The engine and script suites have 216 vitest tests in total, the editor 16 browser
tests; the C# suites are unchanged in count (24, 69, 10) and still pass against the regenerated contracts. Details, decisions and known gaps are in
[docs/m2/results.md](docs/m2/results.md). The M0 second-user pipe check remains open.

For each completed package record: exact dependency
versions, commands, environment, fixture paths, observed results and remaining
limitations. Manual checks include Windows, Visio and Word versions and screenshots
or an acceptance report. Automated reopening alone cannot satisfy desktop acceptance.

## Design outcome and remaining dependencies

R1–R4 are resolved in [QUESTIONS.md](docs/design/QUESTIONS.md). Their implementation
checks cover explicit session scope, deduplication before revision validation,
per-connection FIFO gesture admission and canonical-only group bounds. Document
completion does not mark an implementation package complete.

The architectural design and reviewed implementation plan are ready for user review.
Q1 identifies the Windows/Visio/Word acceptance
environment; Q2 retains the source's best-effort lossy-import policy; Q3 concerns SVG
fallback only if native support fails. M0 functional evidence remains the production
gate. No dependency was installed and no feasibility probe was run during design.

## Current dependencies

- [Design progress](DESIGN_PROGRESS.md)
- [Feasibility and acceptance design](docs/design/feasibility-and-acceptance.md)
- [Questions for later review](docs/design/QUESTIONS.md)

Linux can support portable frontend/model tests and some .NET library work. WPF,
WebView2 and genuine Visio/Word interoperability require a Windows acceptance environment.
