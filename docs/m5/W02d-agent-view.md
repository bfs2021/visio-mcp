# W02d: agent-only WebView and MCP document tools

Issue #43, part of #24. Design: `docs/design/tabbed-documents.md` section 3.2a.

## Architecture
- A second WebView2 (`AgentView`), hosted in an off-screen window, started on first use, same bundle opened with `?view=agent` (`entry.ts` chooses `main` or `agentMain`). It has the engine, request router and snapshot renderer and no interface, tabs or host services.
- One `BridgeRouter` talks to both pages as channels (`user`, `agent`). Every document is registered with the channel that holds it; a request goes to the holder of the document it names, one that names none goes to the person's page. Only the person's page may call `host.*` (the agent channel gets `method_not_found`). Agent documents are never the "current" document. A renderer failure resets only its own channel.
- **Handover** (`DocumentService.ShowAsync`): freeze the document (changes refused with retryable `busy`), snapshot it, the person's page opens it as a new tab (same document id, fresh session, unsaved, fresh history), the agent page drops its copy (its close event is ignored because the router now maps the id to the person's page), the old recovery session is finished once the new one is safe. Refused with `busy_user_editing` while the person is mid-gesture, in which case the document stays where it was.
- The person's window shows background documents as dashed tabs; clicking one asks the host to bring it in, closing one asks twice if it has work (then discards it and its session). The list is pushed by the host (debounced) and read at start.
- Recovery: one session per document in either page. If the agent page dies, those sessions stay on disk and are offered as unfinished work.

## MCP tools
- `list_documents`: id, session, title, revision, pages, unsaved, saved path, view (`editor` | `background`), active.
- `create_document(title?, widthPt?, heightPt?)`: new empty document in the background; never takes focus.
- `show_document(documentId)`: switch to an editor tab, or hand a background document over (new sessionId returned).
- `get_document_summary` with no arguments describes the active editor tab and, when several documents are open, lists them.
- Tool text now says an identity can come from `list_documents` / `create_document` and changes when a document moves.
- Still not offered: opening files, saving, closing documents.

## Tests added
Host `ChannelTests` (6); browser `agentdocs.spec.ts` (5); MCP shim test (1, plus the identity rules); Windows `AgentViewTests` (8, driving the real second WebView: create, edit, render a PNG in the hidden view, list, hand over with content and new session, switch to an existing tab, freeze, discard, host services denied, crash then offered again).

## Results
Unit 335, browser 84, Windows 58, host 138, MCP 14: all pass. (One unexplained failure of `CrashRecoveryTests.AnApplicationThatDiesIsOffered...` in a single full Windows run; it passed on re-run and six isolated runs.)
