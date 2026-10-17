# M0 decision

Recorded 2026-10-17. Evidence: [results.md](results.md), [results.json](results.json).

## Decision: GO for M1, with conditions (recommended, awaiting the project owner's confirmation)

The M0 rule is that M1 may start only if all gates pass. G1, G2, G3 and G4 each passed their functional checks on Windows with real Visio and Word. No material failure occurred, so production work is not stopped.

This is a conditional pass because of what the evidence does not cover. The conditions below are part of the decision, not optional follow-ups.

## Backend decision
Keep OfficeIMO.Visio 3.4.4 (pinned, lockfile) as a reader and for basic shapes. The VSDX exporter must go through our own package writer, or the patch layer proven in `spikes/vsdx`, for connectors, groups, pictures, page setup and string cells. Reasons: OfficeIMO drops connector glue on a Visio save, zig-zags on reroute, writes page size in the wrong units, writes literal group child values and has no picture API. Version 3.5.1 fixes none of these, so do not upgrade for M0 reasons. Only `Diagram.Visio` may reference OfficeIMO types.

No alternative backend was evaluated, because every gap has a Visio-verified patch. If a production check fails that a patch cannot fix, repeat G1 against an alternative using the same scene.

## Conditions on M1 and later
1. **M4 feature matrix** must probe what G1 did not: layer hide/lock/no-print, alpha fill, contain-fit image, hidden shape, document identity, copy/paste identity collisions, group angle normalisation, orthogonal connectors with user waypoints, and 0.01 pt / 0.01 degree geometry comparison between generated and Visio-saved files. Do not offer a preset for any of these until probed.
2. **Canvas:** pin @maxgraph/core 0.24.0; carry the Escape-during-rotate patch and the gesture-hook tests into the production canvas; re-run G2 before any maxGraph upgrade.
3. **IPC:** run the second-Windows-user refusal check before I51; M1 does not depend on it. Peer identity checks must happen after the first frame is read.
4. **Frontend build and CSP:** keep `/assets/<sha256>` reserved for host-served images, keep `unsafe-eval` out of the CSP, and inline approved image bytes for offscreen export.
5. **Picture replacement:** agents replace the media part directly; do not design a Visio UI replace-picture step.
6. **Environment:** the manual checks ran on one Windows machine and one Microsoft 365 build. I41 must repeat desktop acceptance against the production exporter.

## What would reverse this decision
A production-exporter file that Visio repairs or flattens, a Word activation failure on that file, or a maxGraph upgrade that breaks the gesture hooks without a fix.
