# W11 — find, replace and matching selection

Status: approved by the user in Codex chat, 2026-10-19. Issue: https://github.com/rp2839/visio-mcp/issues/33
Planned branch: codex/w11-find-replace, after W10. Design published with W10 for review.

## Purpose

Help a person navigate a large drawing by text/name/alias, change repeated labels safely, and select comparable objects without finding them manually. Search is scoped to the active document tab; it never searches or changes another open drawing.

## Existing foundation and scope

Extend the existing editor selection, page activation, canonical text patches and transaction engine. TextBlock, element names/aliases, style/layer fields, UUID lookup across pages, locked-target validation and undo already exist. Add Edit > Find and replace (Ctrl+F) and a bounded find panel/dialog; no new public MCP tool, canonical field or native host endpoint.

## Search and navigation

Search literal text, display name and alias across current-document pages, with case-insensitive matching by default and a case-sensitive option. No regex or fuzzy search. An empty query gives no matches. Deduplicate each object's matches across fields and show page/name/snippet plus which fields matched. Order results by page order then stable object order/ID. Recompute on committed revisions so renamed/deleted results never become stale commands.

Next/Previous wraps through results, activates the result's page and reveals/selects it through existing editor behaviour. Locked objects may be found for inspection but cannot be replaced. Hidden objects are excluded by default; an Include hidden option lists them with an explicit hidden state, without silently unhiding or unlocking anything. A hidden result reports why it cannot be selected on the canvas. Closing search does not mutate drawing or undo state. Tab switching, New, close, recovery, export and file-conflict dialogs retain their existing modal protections and keyboard focus rules.

## Replacement

Offer Replace current and Replace all for literal matches in TextBlock.value only. A name/alias-only match does not rename anything; aliases/UUIDs must not change and break agent references. Preserve all text formatting, geometry, styles, layers and metadata. Show the replaceable count and explicit locked/hidden/ineligible exclusions. Hidden-object replacement is excluded; do not silently alter invisible content.

Build replacement operations from one pinned document/session/revision snapshot, using UUID targets. Replace all across pages is one existing atomic transaction and one undo step. Refuse over-limit batches rather than chunking them into partial undo histories. On concurrent edits or a locked-target change, refuse the complete proposal and refresh results; never apply a stale or partial replacement. A replacement producing identical text is a no-op.

## Select comparable objects

Add Edit selection actions using one selected seed on the current page. Select same style compares applicable canonical appearance fields for the same element kind, including text formatting but excluding text value, geometry, IDs, names, aliases and metadata. Select same layer membership compares the normalized complete set of layer IDs; an unlayered seed matches other unlayered objects. Groups/types with no meaningful comparable style report that limitation. Select only visible objects that the existing editor can select; never unhide/unlock or jump pages for this action. Selection alone adds no undo revision.

## Boundaries and coordination

Likely files: pure search/matching helpers, a find panel/dialog, EditorModel, Toolbar Edit menu and App shortcut/modal wiring. Preserve the W07 save flow and coordinate W05/W08 shared UI integration if those PRs land. Use UUID targets for cross-page edits and existing planner/lock checks; no new search index service, dependencies, host routes or contract changes. Search covers the actual bounded document and existing transaction limits, not arbitrary files.

## Verification and delivery

Unit tests cover field matching, case/Unicode/literal punctuation, stable ordering, duplicate matches, style/layer semantics, hidden/locked exclusions and bounded cross-page replacement. Browser tests use Ctrl+F and real controls, step across pages, verify one-undo replacement, preserve formatting/unsaved tabs, and invalidate stale results after agent edits. Native Windows checks exercise real editor/host lifecycle and save/open persistence; native Visio checks verify changed text and retained identity/style after export/import. Manual human acceptance remains explicitly separate.

Run the usual complete frontend/browser/Windows checks, with bounded workers and native desktop runs sequentially. Retain failures/retries honestly, commit in small steps, request read-only review, push its own PR and update #33/Project without changing the historical tracker. No automatic merge to main.
