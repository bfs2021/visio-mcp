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

## The remaining scenarios (A06, #21)

Measured on 2026-10-23 with `PerformanceScenarioTests` (live stack, the A05 method, Debug and Release). Raw samples and the environment of each run:
`evidence/PERF/scenario-{ten-pages,drag-resize,large-pictures,text-heavy}-{debug,release}.json`. The machine was 76 to 93 % busy during these runs, so read the figures as those of a loaded machine.
Each table gives the median (Debug / Release); runs that overlapped a starved stretch are counted in the JSON (`contaminatedRuns`) and none of the medians below moved by more than a millisecond when they were left out.

### Ten pages (10 x 55 elements = 550; summary 95 KB; `.vsdx` 57 KB from the app, 81 KB after Visio saved it)
| Measurement | Preferred | Debug / Release |
|---|---|---|
| Build the 550 elements and 10 pages in one DrawScript transaction | n/a | 107 / 120 ms |
| Summary, all pages | < 250 ms | 18.8 / 21.5 ms |
| Summary, one page | < 250 ms | 5.8 / 9.7 ms |
| Edit on page 1, as a DrawScript transaction (includes compiling) | < 100 ms | 8.4 / 6.4 ms |
| Edit on page 10, same | < 100 ms | 7.6 / 5.4 ms |
| Preview of page 1 / 5 / 10 at 1600 x 1200 | < 1 s | 90 / 66 / 68 ms and 64 / 93 / 74 ms |
| Switch pages in the editor (click the tab, wait until that page is drawn) | n/a | 30.8 / 30.9 ms |
| `.vsdx` save, 10 pages | < 3 s | 163 / 159 ms |
| Open the app's own `.vsdx`, 10 pages | n/a | 60.5 / 62.4 ms |
| **Open the file Visio saved from it**, 10 pages | n/a | **90.2 / 68.0 ms** |

### Drag and resize, traced frame by frame (500-element page: 460 shapes, 40 glued connectors)
A script in the page records every animation frame and, for each pointer move, the time to the next frame and to the message after it (an approximation of "painted"; not photon-to-pixel). Real mouse events through WebView2, one every 16 ms, 40 per gesture.

| Gesture | Frame interval (p50 / max) | Frames over 33 ms | Input to next frame (p50 / p95 / max) | Release to committed |
|---|---|---|---|---|
| Drag one shape | 15.6 / 15.7 ms (64 fps) | 0 | 10.0 / 16.7 / 17.6 ms (Debug); 9.8 / 18.0 / 19.2 (Release) | 34 / 32 ms |
| Resize one shape | 15.6 / 15.7 ms (64 fps) | 0 | 13.8 / 16.8 / 17.2; 9.3 / 18.9 / 20.4 | 33 / 27 ms |
| Select all (Edit > Select all, 500 elements) and drag the whole page | 15.6 ms; **one frame of 656 ms** at release | 1 | 9.2 / 15.7 / 18.8; 7.8 / 15.6 / 16.7 | **189 / 196 ms** |

Dragging stays at the display's rate with about one frame of input latency. Moving all 500 elements keeps up while the pointer moves, then stalls for about 0.65 s when the gesture is committed; the window is offscreen, so the frame rate is that of this harness.

### Large pictures (8 PNG pictures of 2400 x 1600, 5.6 to 5.8 MB each, 45.2 MB in all, on one page)
| Measurement | Preferred | Debug / Release |
|---|---|---|
| Prepare and import one picture into the library | n/a | 36 / 34 ms |
| Place all eight with one script | n/a | 240 / 278 ms |
| Editor decodes and draws all eight | n/a | 174 / 86 ms |
| Summary | < 250 ms | 3.3 / 2.3 ms |
| **Preview at 1600 x 1200 (1.15 MB PNG)** | **< 1 s** | **1540 / 1374 ms: not met** |
| `inspect_layout` | n/a | 14.5 / 3.2 ms |
| `.vsdx` save (45.2 MB file) | < 3 s | 1283 / 1218 ms |
| Open that `.vsdx` | n/a | 187 / 212 ms |

**Limit found.** With four more such pictures (12 in all, 68.5 MB) the `.vsdx` export is **refused after 1.5 / 2.2 s with `internal_error`: "the exported file could not be read back, so it was not saved: the file is larger than 67108864 bytes"**,
and nothing is left behind. The 64 MiB budget (`InputLimits.NativeJsonBytes`) is applied to what the exporter reads back, so a document whose pictures add up to more than that cannot be saved as `.vsdx`, and the error code does not say so.

### Text-heavy pages (5 pages; 200 wrapped boxes of 300 characters and 100 labelled shapes; 65,970 characters; summary 112 KB; `.vsdx` 42 KB)
| Measurement | Preferred | Debug / Release |
|---|---|---|
| Build, one transaction | n/a | 118 / 110 ms |
| Summary | < 250 ms | 17.1 / 12.9 ms |
| `inspect_layout` over all five pages (850 issues reported; the workload overlaps by design) | n/a | 124 / 112 ms |
| `get_text_layout` for 50 elements, Visio target | n/a | 15.0 / 11.8 ms |
| Preview of page 1 | < 1 s | 142 / 137 ms |
| `.vsdx` save | < 3 s | 99 / 101 ms |
| Open that `.vsdx` | n/a | 43 / 41 ms |

### What these show
- The ten-page and text-heavy workloads meet every preferred target by a wide margin; opening a file Visio saved is as fast as opening the app's own.
- **Pictures are the one measured workload above a preferred target:** the page preview with eight 6-megapixel pictures takes 1.4 to 1.5 s (target 1 s). Everything else in the picture workload is fast. Where the time goes was not profiled.
- Moving everything on a 500-element page stalls the display for about 0.65 s at the moment of release (one frame), though the drag itself keeps up.
- The `.vsdx` export has a hard ceiling of 64 MiB of content and reports it as `internal_error`.

## Finding: starvation of the thread pool when the process starts a child (A05)
I52 reported "a four-second freeze of the whole process when the test starts the shim". A05 (#18) tested that. The process never stops as a whole; its **thread pool is starved** for 0.3 to 5 s, the
length growing with machine load (2.2 s at 70 % busy, 5.0 s at 96 %), and it happens when the process launches a child of any kind (the shim, a non-.NET program, a CPU-burning script), with or without the editor.
The product never launches a process, so the trigger is a property of the test setup. The benchmark now starts the shim first, waits for the pool to be quiet, and flags every run that overlaps a starved stretch.
Full method, 39 probe runs and limits: [shim-startup-pause.md](shim-startup-pause.md).

## Gaps
1. One machine, 4 logical processors, 76 to 96 % busy during the recordings; no second machine and no slower hardware ([#16](https://github.com/rp2839/visio-mcp/issues/16)).
2. The page-count dimension was measured at ten pages of 55 elements, not ten pages of 500. A 500-element page was measured separately.
3. Drag and resize were traced in an offscreen WebView2 window with synthetic mouse events. The frame rate is that of this harness, and input-to-frame time is not input-to-pixel time; a visible window with a real pointer was not traced.
4. Pictures are generated (smooth gradients with seeded noise), not photographs, so their compression and decode cost differ from real ones. Memory use of the editor was not measured.
5. Text-heavy content used a 16-word vocabulary in Latin script; right-to-left and East Asian text were not measured.
6. Edits on a later page were measured as DrawScript transactions because `apply_operations` cannot name a page.
7. The cause of the 1.4 to 1.5 s preview with large pictures, and of the 0.65 s frame at the end of a select-all drag, was not profiled.
