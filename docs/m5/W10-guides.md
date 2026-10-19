# W10 guide editing and snapping

Issue #32. Design: ../design/W10-guides.md. Branch codex/w10-guides.

Ruler drag creates page guides; existing guides move/delete as one pinned transaction and undo step. Show guides and Snap to guides are per-tab view controls. The canonical planner chooses guide edge/centre alignment before grid fallback, using eight screen pixels at current zoom. maxGraph pre-snapping is disabled so it cannot discard raw pointer proximity or override layer permissions. Pointer previews remain transient until release. Imported off-page guides are retained.

Verification in progress. Fresh automated evidence will be linked before review. Manual human acceptance remains NOT RUN.

Review caught and regression tests reproduce: handle no-op click drift, creation dimensions rounding away from a guide, rotated resize/grid coupling, raw pointer pre-snapping, picture placement bypass. Fixes are verified by targeted tests.

Known UX: drag previews follow raw pointer coordinates; canonical snapping is committed on release. No persistence for view toggles (W13). No new MCP endpoint; existing setPage and operation discovery expose PagePatch.guides.

Fresh verification: frontend 411/411; initial full browser after raw-pointer integration 112 passed / 2 failed (visual alignment feedback and picture grid behavior), both corrected and 21/21 focused browser tests pass. Windows solution: 427 passed / 1 failed (untouched Office GlueFollowsMovesAndGroupsScaleInVisio); focused native rerun plus original/repeated guide roundtrips 3/3 pass. Windows UI suite 80/80. Typecheck, generated check and production build pass, with existing maxGraph eval/chunk warnings. Full final browser verification follows. Logs: ../../artifacts/w10-guides/.
