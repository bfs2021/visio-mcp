# M0 results (I05)

Recorded 2026-10-17. Machine-readable version: [results.json](results.json). Decision: [decision.md](decision.md).

All four feasibility gates passed on Windows 11 with Visio and Word (Microsoft 365) installed.
G1 passes only because of post-save patches over OfficeIMO.Visio. Several checks from the D8 list were not probed; they are listed under each gate and carried into M4.

| Gate | Outcome | Evidence | Tests |
|---|---|---|---|
| G1 native VSDX | **PASS, with backend caveat** | [G1 report](evidence/G1/report.md) | 10/10 on generated file; 10/10 on a copy of the Visio-saved v7 file |
| G2 canvas | **PASS** | [G2 report](evidence/G2/report.md) | 3/3 Playwright |
| G3 MCP/IPC | **PASS** (one check NOT RUN) | [G3 report](evidence/G3/report.md) | 14/14 xunit |
| G4 WebView2 | **PASS** | [G4 report](evidence/G4/report.md) | 19/19 xunit |
| I00 contracts | PASS | `spikes/contract-tests` | 16/16 vitest |

Environment: Windows 11 Enterprise 10.0.26300, .NET SDK 10.0.402, Node 24.14.1, WebView2 runtime 155.0.4283.60, Visio and Word Microsoft 365. Versions that differ from the I00 pins are in [dependencies.json](dependencies.json) under `usedInstead`.

## G1: what was proven and what was not
Proven in real Visio and Word, with the user as the manual checker: the file opens without repair; text and fill are editable; glued connectors follow a moved shape and stay straight; the full UUID in `User.AgentId` survives a Visio save; layers and connection points survive; a PNG picture is an independent object with unchanged bytes; the file embeds in Word, activates and saves; SVG source and image provenance cells survive; the page stays 297 x 210 mm with a metric UI; a resized group keeps child proportions; one shape can sit in two layers.

OfficeIMO.Visio 3.4.4 gets seven things wrong, each with a Visio-verified patch in `spikes/vsdx`: connector glue, connector reroute geometry, UUIDs read as numbers, page size units, auto page resize, literal group child values, and no picture API. Version 3.5.1 was tested and fixes none of them.

Not probed, so not claimed: layer hide/lock/no-print behaviour, alpha fill, contain-fit image, hidden shape, document identity, copy/paste identity collisions, group angle normalisation, orthogonal connectors with user waypoints, and geometry compared to 0.01 pt / 0.01 degrees. Rotation of 33 degrees is asserted on the generated file only. Picture replacement through the Visio UI is not available in this Visio build.

## G2: canvas
maxGraph 0.24.0 supports move, resize, rotate, glued connectors, picture display and offscreen PNG export with no model leakage and no canvas taint (PNG and SVG assets). Defect: Escape during a rotate drag throws; the probe patches it. Gesture begin/commit/cancel is derived by wrapping handler methods, which depends on maxGraph internals.

## G3: MCP/IPC
The official C# SDK serves a stdio tool that reaches a test host through the 4-byte little-endian pipe framing. Verified: partial reads, 32 MiB cap before allocation, handshake-first, not_running, disconnect mid-call (unknown outcome, no retry), 8 MiB frame in about 15 ms, stdout is MCP only, current-user ACL, same-user peer checks. **NOT RUN:** refusal of a second Windows user (needs a second account).

## G4: WebView2
Fixed virtual origin with CSP, exact-origin and child-frame rejection, blocked navigation and new windows, denied remote fetch, readiness handshake, session and document scope, duplicate transactions applied once, renderer crash settling pending calls as unknown with a fresh session after reload, host-served hashed assets verified in the page, and an MCP client reading the live canvas through the pipe (revision and position matched the DOM).

## Open items carried forward
1. Write the exporter as our own package writer (or keep the patch layer), not OfficeIMO's connector or page output. OfficeIMO stays for reading and basic shapes.
2. M4 feature matrix must cover the unprobed G1 items above before any preset is offered.
3. Pin maxGraph and keep the Escape-during-rotate patch and the handler hooks under test; re-check on any upgrade.
4. Run the second-user pipe check when a second Windows account is available (before I51).
5. Production Vite config must keep `/assets/` free for host-served images and keep `unsafe-eval` out of the CSP.
