# W11 find, replace and matching selection

Issue #33. Approved design: ../design/W11-find-replace.md. Branch codex/w11-find-replace, stacked on W10 PR #50.

Ctrl+F or Edit > Find and replace opens current-tab literal search across pages. Text includes shape/text blocks and connector labels; names/aliases are searchable but never renamed by replacement. Results show matched fields, page, snippet and locked/hidden state. Previous/Next wrap, selecting and revealing a visible result. Named groups navigate to visible leaves; hidden members remain hidden and unselected.

Replace current/all updates only TextBlock.value through UUID set operations from one pinned scope/revision. All changes form one atomic transaction and undo entry. A stale revision refuses the entire batch. More than 1000 changed objects is refused without chunking. Identical replacements are no-ops. Replacement strings are literal, including dollar signs. Locked or hidden content (including ancestor/layer protection) is excluded.

Edit > Select same style compares kind and applicable appearance including text/connector label formatting, excluding value, geometry and identity. Select same layer membership compares the entire normalized layer set. Both stay on the current page and exclude hidden/locked objects; selection does not change revision. Groups have no comparable style and report that limitation.

Search controls and modal guards preserve New/save/conflict/recovery/export workflows. Closing Find restores opener focus. State belongs to the tab. Search is recomputed on document/session/revision or option changes. The result list shows 200 rows; Next/Previous still traverse all matches. No regex mode, external index service, new host contract or MCP endpoint.

Review findings fixed with regression coverage: connector-label discovery/replacement and formatting; offscreen group reveal; hidden descendants during group selection; focus restoration. Fresh native real-panel acceptance verifies one revision, JSON save and native Visio export/import preserving UUID, text and bold formatting. Manual human acceptance remains NOT RUN.

Verification results and initial failures/retries are retained in ../../artifacts/w11-find-replace/. Full final suite results will be added before review.

Final frontend checks: 421/421 unit tests; typecheck, generated check and production build PASS. Existing maxGraph eval/chunk warnings remain. Initial full browser:119pass/1export-during-drag failure during source editing; focused reviewed feature tests5/5pass; final stable-source full browser121/121pass. Initial failure retained, not represented as a clean first run.

Native final verification: dotnet test Diagram.Windows.slnx -m:1 -nr:false → 430/430 PASS (Contracts24, Host145, IPC17, MCP14, Visio149, Windows81). Final enhanced FindReplaceTests focused run1/1 PASS against the final production build, including shape bold and connector-label italic preservation through JSON save and native Visio export/import. Native Office tests were run sequentially; no human manual sign-off is claimed.
