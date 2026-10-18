# Performance against spec section 18

Recorded 2026-10-18 on the development machine: Windows 11 Enterprise 10.0.26300, 4 logical processors, .NET 10.0.13, WebView2 155.0.4283.62,
Visio 16.0 (not involved in these figures). One machine, one session. These are measurements, not a guarantee for other hardware.

## Method
- **Fixture:** one A4 page with **500 visible elements** (460 labelled shapes in a grid and 40 glued connectors), built through the real MCP tools.
  Spec §18 asks for 1 to 10 pages and up to 500 visible elements on a page; the page count was not varied on the live stack (see gaps).
- **Live stack** (`tests/Diagram.Windows.Tests/PerformanceTests.cs`): the real WPF/WebView2 editor, the real named pipe, the real `Diagram.Mcp`
  shim and a stdio MCP client. Each figure is the median of 10 runs (5 for batch, preview and save), run in both Debug and Release.
  Raw samples are in `evidence/PERF/live-timings-{debug,release}.json`.
- **Engine only** (`src/web/tests/performance/targets.test.ts`): the command engine without transport, 15 runs.
  Run with `npm --prefix src/web test -- tests/performance/targets.test.ts`; output `evidence/PERF/engine-timings.json`.
- **Preferred targets are reported, not gated.** The tests assert only generous ceilings (1 s for a painted edit, 3 s for a batch, 5 s for a preview,
  15 s for a save) that a real regression would cross, and the correctness of what was measured (the page really holds 500 elements, a batch changes
  only what it names, the preview is a real image).

## Results (median; p95 of the runs in brackets)

| Spec §18 target | Preferred | Debug | Release | Result |
|---|---|---|---|---|
| Single transaction, agent sees it committed (MCP call to answer) | < 100 ms (acceptable < 250 ms) | 8.2 ms (13) | 7.3 ms (12) | met |
| Single transaction, painted in the editor (commit and two animation frames) | < 100 ms (acceptable < 250 ms) | 31.3 ms (39.7) | 31.4 ms (77.5) | met |
| 100-operation batch (atomic, 500-element page) | < 500 ms | 34.0 ms (34.7) | 34.3 ms (36.1) | met |
| Semantic summary of 500 elements, excluding MCP transport (host only) | < 250 ms | 1.2 ms (2.3) | 1.2 ms (20.3) | met |
| Same, over the stdio MCP client (82 KB of JSON) | (includes transport) | 7.7 ms (13.3) | 7.0 ms (10.8) | for information |
| Page preview, 1600 x 1200 PNG (197 KB) | < 1 s | 45.3 ms (178.4) | 46.8 ms (173.4) | met |
| `.vsdx` save of the 500-element document (43 KB file) | < 3 s | 128.3 ms (206.3) | 130.0 ms (197.2) | met |

Engine only (no transport): single edit on 500 elements 0.56 ms median; a 100-operation batch 5.0 ms; reading a 500-element, 5-page document 3.1 ms.

Drag and resize ("interactive at normal desktop frame rates") has no number in the spec. The Playwright suite drives real mouse drags on the canvas and
the demo drags a block with real mouse events through WebView2; neither measures frame rate, so this item is **observed working, not measured**.

## Finding: a four-second freeze when the test process starts the shim
Early runs showed one call in a few hundred taking about 4.1 to 4.5 s, always 4 to 7 s after the test began. Isolating it:
- it also hit direct pipe calls, UI-thread dispatches and plain `Task.Delay` timers in the same process, so it is a freeze of the whole process;
- it did not depend on the shim's logging, on a JavaScript long task (none recorded) or on garbage collection (no generation-2 collection);
- a process that hosts the editor but never starts the shim did not freeze, and neither did one that has no editor;
- starting the shim (`dotnet Diagram.Mcp.dll`) from the test process was enough, even with no MCP call at all.

In use the shim is a separate process started by the agent's host, so this is a property of how the test starts it on this machine (the cause was not
found beyond that). The benchmark therefore runs a 10 s warm-up before measuring and records the longest warm-up call
(4.1 s Debug, 4.0 s Release) in the report. Anyone seeing a multi-second first call after launching the shim should know it exists; it is not in the
steady-state figures above.

## Gaps
1. One machine, 4 logical processors; no second machine, no slower-hardware run.
2. The live fixture is one page of 500 elements. Ten pages was not measured on the live stack; the engine test reads a 5-page document.
3. "UI responsiveness" is shown by the painted-edit time, not by a frame-rate or input-latency trace.
4. Larger pictures and text-heavy elements were not part of the fixture (the shapes carry short labels).
5. Save time is for the app's own export; opening a file Visio saved was not timed.
