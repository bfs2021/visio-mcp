# W14 file drop and clipboard picture acceptance

Updated: 2026-10-21

W14 adds bounded, native-gesture file ingress for drawing and supported picture files, plus explicit clipboard-picture paste. Native handlers mint short-lived, single-use tickets; the editor sees opaque ticket identifiers and safe display names. Drawing files flow through the guarded open path. Pictures are admitted through the existing asset preparation and sanitization path, then the user chooses whether to place them in the pinned drawing target or keep them in the library. Clipboard capture runs only for an explicit paste outside text editing and is checked against the native clipboard sequence. Internal shape paste retains precedence.

The implementation is on `codex/w14-file-drop`, currently stacked on W13 PR #71 (`a9a746e`). W15 PR #53 is merged into `main` (`61de40c`); current `origin/main` was `ba3923e` at acceptance. W13 remains open, so the W14 PR should target the W13 branch until W13 lands. No W13 or W15 code was copied or merged into this branch during acceptance.

## Automated results

| Check | Result |
| --- | --- |
| Frontend unit suite | PASS — 516/516 |
| TypeScript typecheck | PASS |
| Generated contract check | PASS |
| Production frontend build | PASS; existing bundle-size and maxgraph `eval` notices remain |
| Full Playwright browser suite, serial retry | PASS — 166/166 |
| Native Windows clipboard, ingress and picture filtered suite | PASS — 16/16 (see Task 5 evidence in the W14 SDD ledger) |
| Full Windows solution | INCOMPLETE — contracts 50/50, host 202/202, IPC 33/33, MCP 14/14 and Visio 156/156 passed in the PATH-corrected attempt; the Windows UI test host did not return results and the run was stopped. A standalone retry then failed to connect to the test host/data collector within 90 seconds. |

The first two-worker browser run had four startup failures caused by Vite failing to fetch a dynamically imported `src/main.tsx` module. The same full suite passed serially on retry. Both logs are retained. The initial Windows solution attempt also exposed that the child MCP process could not find `dotnet`; after adding the installed .NET directory to `PATH`, all suites before Windows UI passed. The later UI-harness stall is captured in the path-corrected log and the standalone runner output in this folder.

The full browser retry saved the queue screenshot [clipboard-picture-offer.png](../../artifacts/w14-file-drop/screenshots/clipboard-picture-offer.png). Test logs and the focused result ledger are under [artifacts/w14-file-drop/verification](../../artifacts/w14-file-drop/verification). The earlier focused native clipboard result includes saved/restored Windows clipboard state, bitmap capture/import, internal-copy marker precedence and invalidation, changed-sequence refusal, and busy-clipboard handling.

## Manual acceptance

| Manual check | Status | Notes |
| --- | --- | --- |
| Drop a drawing from Explorer | NOT RUN | Native WPF adapter and guarded-open behavior have automated coverage; a live Explorer gesture was not performed. |
| Drop supported pictures from Explorer and place/keep them | NOT RUN | Automated adapter, import, stale-target, library and placement coverage passed; a live Explorer gesture was not performed. |
| Paste a real external clipboard picture in the running app | NOT RUN | Native clipboard automated tests passed; interactive app acceptance was not performed in this run. |
| Open and inspect output in native Visio | NOT RUN | No native Visio acceptance session was performed for W14. |

No manual result is inferred from automated adapter tests. Full Windows UI acceptance remains outstanding because the test host did not complete in this environment.
