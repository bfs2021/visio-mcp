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

## Results (median of the runs; the worst run in brackets)

Recorded again on 2026-10-23 with the A05 method (shim started before the editor, a quiet gate instead of a fixed warm-up, every timed run checked against thread-pool starvation; see
[shim-startup-pause.md](shim-startup-pause.md)). The machine was **84 % busy (Debug run) and 96 % busy (Release run)** averaged over the run, which is far busier than during the I52 recording (not measured then, but
about 70 % when idle on 2026-10-23). Medians are therefore a conservative picture of a loaded machine, and the Release run is not faster than the Debug run because the load differed.

| Spec §18 target | Preferred | Debug | Release | Result |
|---|---|---|---|---|
| Single transaction, agent sees it committed (MCP call to answer) | < 100 ms (acceptable < 250 ms) | 12.6 ms (24) | 28.7 ms (3030, contaminated); clean 16.6 ms (66) | met |
| Single transaction, painted in the editor (commit and two animation frames) | < 100 ms (acceptable < 250 ms) | 33.1 ms (48) | 46.0 ms (3056, contaminated); clean 44.6 ms (91) | met on the median; one run in the Release set took 3 s (below) |
| 100-operation batch (atomic, 500-element page) | < 500 ms | 67 ms (1473, contaminated); clean 67 ms (68) | 62 ms (93) | met on the median; one run took 1.5 s (below) |
| Semantic summary of 500 elements, excluding MCP transport (host only) | < 250 ms | 1.1 ms (3.9) | 1.3 ms (2.3) | met |
| Same, over the stdio MCP client (82 KB of JSON) | (includes transport) | 14.7 ms (49) | 14.0 ms (46) | for information |
| Page preview, 1600 x 1200 PNG (197 KB) | < 1 s | 61.7 ms (204) | 58.4 ms (222) | met |
| `.vsdx` save of the 500-element document (43 KB file) | < 3 s | 152 ms (225) | 162 ms (219) | met |

**Contaminated** means the run overlapped a stretch in which work sat in this process's thread pool queue for 300 ms or more while the machine was this busy. Those single runs (1.5 s and 3.0 s) are real: an agent or
person using a machine this loaded would have waited that long. They are not caused by the editor or the shim (see the A05 document) and are reported in `contaminatedRuns`, with a `clean` figure beside the all-runs figure.
The preferred targets are about medians in the spec, and the medians are met; **"every call under the target" is not met on a machine this busy**, and nothing here claims it.

The I52 run of 2026-10-18 (Debug and Release, same fixture) gave 8 ms, 31 ms, 34 ms, 1.2 ms, 47 ms and 130 ms for the same rows, with the 4 s pause removed by a 10 s warm-up; those files were replaced by this run.

Engine only (no transport): single edit on 500 elements 0.56 ms median; a 100-operation batch 5.0 ms; reading a 500-element, 5-page document 3.1 ms.

Drag and resize ("interactive at normal desktop frame rates") has no number in the spec. The Playwright suite drives real mouse drags on the canvas and
the demo drags a block with real mouse events through WebView2; neither measures frame rate, so this item is **observed working, not measured** (A06, #21).

## Finding: starvation of the thread pool when the process starts a child (A05)
I52 reported "a four-second freeze of the whole process when the test starts the shim". A05 (#18) tested that. The process never stops as a whole; its **thread pool is starved** for 0.3 to 5 s, the
length growing with machine load (2.2 s at 70 % busy, 5.0 s at 96 %), and it happens when the process launches a child of any kind (the shim, a non-.NET program, a CPU-burning script), with or without the editor.
The product never launches a process, so the trigger is a property of the test setup. The benchmark now starts the shim first, waits for the pool to be quiet, and flags every run that overlaps a starved stretch.
Full method, 39 probe runs and limits: [shim-startup-pause.md](shim-startup-pause.md).

## Gaps
1. One machine, 4 logical processors; no second machine, no slower-hardware run.
2. The live fixture is one page of 500 elements. Ten pages was not measured on the live stack; the engine test reads a 5-page document.
3. "UI responsiveness" is shown by the painted-edit time, not by a frame-rate or input-latency trace.
4. Larger pictures and text-heavy elements were not part of the fixture (the shapes carry short labels).
5. Save time is for the app's own export; opening a file Visio saved was not timed.
