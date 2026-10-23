# A05: the pause seen when the test process starts the shim

Issue [#18](https://github.com/rp2839/visio-mcp/issues/18). Recorded 2026-10-23 on the I52 machine (Windows 11, 4 logical processors, .NET 10.0.13, WebView2 155).
Raw evidence: [evidence/A05](evidence/A05) (39 probe runs, one JSON file each, plus `summary.json`). Re-run: `DIAGRAM_RUN_DIAGNOSTICS=1 DIAGRAM_PROBE_SECONDS=12 DIAGRAM_PROBE_REPEAT=3 dotnet test tests/Diagram.Windows.Tests --filter ShimPauseProbeTests`, then `node tools/summarize-shim-pause.mjs`.

## What I52 saw, and what that left open
Starting the shim (`dotnet Diagram.Mcp.dll`) from the process that hosts the editor was followed, a few seconds later, by one call taking 4.0 to 4.5 s. Timers, UI dispatches and pipe calls were all late
at once, so I52 called it "a freeze of the whole process". That was an inference from late continuations, which also need the thread pool. It had not been tested.

## What the probes show
`PoolSampler` runs on a thread of its own and samples every 20 ms: garbage-collection pause time, collections, thread-pool size and queue, process CPU time and machine-wide CPU use.
If the whole process stops, that thread stops too. If only the thread pool is starved, it keeps going and the pool queue is non-empty.

| Finding | Evidence |
|---|---|
| **The process never stopped as a whole.** | 0 gaps in the sampler's own samples in 39 runs; GC pause time did not move. |
| **The thread pool is starved for a stretch.** Work waits in the queue for 0.3 to 2.2 s while the pool stays at the same size (7 to 8 threads). | `poolStarvedWindows` in every file. Pool timers and UI-thread dispatches, which both finish on pool threads, are late for the same stretch. |
| **The machine was busy before the test began.** | 63 to 77 % CPU with nothing of ours running (8 one-second samples), 66 to 73 % averaged over every control run. `explorer.exe` alone has used about 571,000 CPU-seconds. |
| **It is not specific to the shim.** A child that is not .NET (`cmd` running `ping`) and a PowerShell that only burns every processor for three seconds produce the same stretches, with and without the editor in the process. | s4, s7, s12, s13 below. |
| **It is not specific to the editor being in the process.** | s7 and s13 have no editor. |
| **Who starts the child matters.** Starting the shim from another process (as an agent's host would) caused no starvation in the observing process in 3 runs; the shim started this way is also what production does. | s8. |
| **With no child started there was none in 3 runs** (36 s). | s1. |
| **The production topology cannot tell us more.** The real `Diagram.App.exe` ran in its own process and the shim was started from the test process. The observer's own pool was starved in every run, and the app's pipe answered late once per run (0.97, 1.02 and 1.12 s) at the same instants. But the answer is read by the observer's pool, so this cannot separate a delay in the app from a delay in the observer; the app process itself was not instrumented. | s9. |
| **The product never starts a process, so the trigger cannot occur in it.** Neither the app nor the shim launches a child (the static guard `BoundaryGuardTests.NoCodeStartsProgramsOrOpensConnectionsOfItsOwn` fails the build if either starts a program). Only the test code does. | `tests/Diagram.Host.Tests/BoundaryGuardTests.cs`. |
| **The size of the stretch follows machine load.** The longest in the 39 probe runs was 2.2 s at about 70 % machine CPU. In a later benchmark run (Release) the machine averaged 96 % busy and the stretch at the shim launch was **5.0 s**, with a second of 3.0 s later: the size I52 reported. I52 did not record machine load, so its runs cannot be compared directly. | `evidence/PERF/live-timings-release.json`, `setup.starvedStretchesSeen`. |

Per scenario (3 runs each, 12 s observed after the child started):

| Scenario | Runs | Whole-process stops | Worst pool starvation (ms) | Median of per-run worst (ms) | Runs with starvation >= 1 s | Windows >= 0.7 s (of which within 1.5 s of the child start) | Machine CPU busy | App pipe worst (ms) |
|---|---|---|---|---|---|---|---|---|
| s1 editor only (control) | 3 | 0 | 0 | 0 | 0 | 0 (0) | 68% |  |
| s2 editor + shim via MCP library | 3 | 0 | 738 | 725 | 0 | 3 (1) | 70% |  |
| s3 editor + shim, plain `Process.Start` | 3 | 0 | 2220 | 1502 | 2 | 4 (2) | 69% |  |
| s4 editor + `cmd` child | 3 | 0 | 992 | 701 | 0 | 3 (1) | 69% |  |
| s5 no editor + shim via MCP library | 3 | 0 | 0 | 0 | 0 | 0 (0) | 68% |  |
| s6 no editor + shim, plain `Process.Start` | 3 | 0 | 0 | 0 | 0 | 0 (0) | 67% |  |
| s7 no editor + `cmd` child | 3 | 0 | 918 | 0 | 0 | 1 (0) | 71% |  |
| s8 editor; shim started by another process | 3 | 0 | 0 | 0 | 0 | 0 (0) | 70% |  |
| s9 real app in its own process; shim from the test process | 3 | 0 | 1997 | 1092 | 2 | 4 (3) | 71% | 1117 |
| s10 editor + short-lived .NET child | 3 | 0 | 0 | 0 | 0 | 0 (0) | 70% |  |
| s11 editor + the shim's native launcher | 3 | 0 | 704 | 0 | 0 | 1 (0) | 68% |  |
| s12 editor + CPU-burning child | 3 | 0 | 2094 | 591 | 1 | 1 (0) | 81% |  |
| s13 no editor + CPU-burning child | 3 | 0 | 1558 | 761 | 1 | 2 (1) | 79% |  |

## Conclusion
What I52 called a four-second freeze of the application is **thread-pool starvation in the observing process, on a machine that is already about 70 % busy**. It is intermittent (0.7 to 2.2 s in the probe runs at about 70 % machine CPU, and up to 5.0 s in a benchmark run at 96 %),
it is triggered by launching a child from the process, and the child need not be the shim, a .NET program, or anything with the editor present. No evidence in these runs points at a defect in the shim, the host or the application, and the trigger (a process launching a child) is something
the product never does; the production-shaped run (s9) is inconclusive about the app's own responsiveness, because it was measured from the starved observer.

**What this does not establish.** The exact mechanism by which launching a child starves the pool was not isolated (candidates: process creation and anti-malware scanning contending for cores that are already
busy; the calling thread being blocked inside process creation while pool threads wait for CPU). With 3 runs per scenario the differences between the scenarios that did and did not show starvation
(for example s10 and s11 against s3) are not large enough to rank them. No fix to the product is proposed, because nothing in these runs points at the product.

## Mitigation, and why it is not a longer warm-up
`PerformanceTests` no longer sleeps for ten seconds. Instead it:
1. starts the shim **before** the editor exists and before anything is timed (the shim connects to the pipe only on its first call);
2. waits for a **quiet gate**: the thread pool has had nothing queued for three seconds (or a minute passes and the report says the gate was not reached);
3. keeps the `PoolSampler` running through the whole benchmark and checks **every timed run** against the starved stretches it saw. A run that overlaps one is counted in `contaminatedRuns` and shown beside a `clean`
   figure; the headline figures still include every run, so nothing is hidden. The benchmark also fails if the process stops as a whole.

Results with the new method (five Debug and Release runs on 2026-10-23): timed runs overlapped a starved stretch in four of them (a `.vsdx` save of 2.0 s, a 100-operation batch of 1.5 s twice, a single edit of 3.0 s) and
were flagged each time; the clean medians were unaffected. The machine was 77 to 96 % busy during these runs, so the medians themselves are higher than in I52 (for example the batch: about 62 to 67 ms against 34 ms), which is
also a finding: **the I52 figures were taken on a quieter machine than the one this benchmark now runs on.** See [performance.md](performance.md).

## Limits
- One machine, one session each, 3 runs per scenario; the load on the machine came from software outside this repository and changes through the day.
- No ETW or kernel trace was taken; the cause below "pool starved while the machine is busy" is not identified.
- The app process itself was never instrumented, so "the app did not freeze in production" is argued from the static guard, not measured.
- The standalone-app scenario used the real `Diagram.App.exe` with the user's own data folders and was closed normally after each run.
