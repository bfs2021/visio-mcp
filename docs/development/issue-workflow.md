# Work coordination through GitHub Issues

GitHub Issues is the source of current work status, ownership, dependencies and handovers for Codex, Claude and human contributors.
[IMPLEMENTATION_PROGRESS.md](../../IMPLEMENTATION_PROGRESS.md) preserves completed package history and a stable-ID-to-issue index.
Designs, compatibility reports and acceptance evidence stay versioned in the repository; link to them from the issue.

## Find and claim work

1. Pull the latest main and read the relevant open issue, dependencies and recent comments before changing files. Search for an existing issue before creating a new one.
2. Preserve work IDs in titles, for example `[R08b] Route orthogonal connectors around other shapes`. GitHub issue numbers are separate IDs.
3. Check that nobody has already claimed the work. Post a claim naming Codex or Claude, the branch, intended scope and likely shared files. Add exactly one `agent:codex` or `agent:claude` label and replace the current status label with `status:in-progress`.
4. Read the issue back before editing. Issue claims are a coordination convention, not an atomic lock: if two claims overlap, resolve ownership and scope first.
5. Use a feature branch such as `codex/r05-regression` or the other contributor's normal branch convention. Share contract/schema changes explicitly in the issue before implementing changes that affect another active task.

Assignees represent actual GitHub users. Do not invent separate agent accounts when both agents use the owner's account; agent labels identify the current implementer.
Acceptance criteria migrated from proposals are scope/checklists, not approval of an implementation design. Existing design-review requirements still apply.

## Workflow states

Use exactly one status label. These labels are the authoritative status until a GitHub Project with matching statuses is available.

| State | Label | Meaning |
|---|---|---|
| Backlog | `status:backlog` | Proposed or unclaimed work; no implied design approval |
| Ready | `status:ready` | Scope agreed and dependencies satisfied; available to claim |
| In progress | `status:in-progress` | A contributor is actively working; see claim comment |
| Review | `status:review` | Concrete implementation or acceptance evidence is ready for review |
| Done | `status:done` | Criteria met, remaining limits recorded, integrated as intended and issue closed |

Area labels are `area:reproduction`, `area:acceptance` and `area:reliability`. Keep the GitHub open/closed state consistent with the status label.
If blocked, retain the actual work state, add native blocking dependencies where possible, and explain the blocker in a comment. Completed historical packages can be referenced through their repository reports without manufacturing open issues for them.

The user created [Project 3](https://github.com/users/rp2839/projects/3/views/1). Board target: **Backlog → Ready → In progress → Review → Done**.
Population and status verification are pending authenticated Project access: the existing API credential lacks Projects scope. Repository Issues access is working.
Do not publish a second manually maintained status table in Markdown. When the Project is enabled, make its Status field authoritative and automate synchronization of the labels or retire the status labels; do not require agents to maintain two independent statuses.

## Review, handover and completion

Before review, record:

- The branch and commit/PR links, what changed and the reason.
- Exact verification commands and results; link repository evidence and distinguish historical runs from fresh tests.
- Remaining work, compatibility limits and manual checks marked PASS, FAIL or NOT RUN. Automated Office interaction is not manual sign-off.
- Any overlap with another issue and which agent should pick up the next step.

Before closing, verify the acceptance checklist, preserve relevant evidence in the repository, and confirm integration. Scope remaining work into linked follow-ups rather than hiding it in a completion comment.
For a handover, record the current commit, outstanding checks and next action; release the agent label if nobody is continuing immediately.
Current issue status must not depend on either agent's chat history.

## Migration record — 2026-10-18

Migrated 13 open reproduction packages (R05, R08b/R08c, R09–R18) and eight acceptance/reliability follow-ups (A01–A06, H01–H02).
R08 had already been delivered on main and was retained as completed history. All migrated issues started unclaimed in Backlog.
Native blocking links: R16 depends on R12, R17 on R09, and R18 on R10. Links to completed dependencies remain in issue bodies and source reports.
The I52 automated MCP portion remains signed off; migration does not reopen the entire I52 package or claim that its remaining manual/security checks passed.

[All open work](https://github.com/rp2839/visio-mcp/issues?q=is%3Aissue%20is%3Aopen) ·
[In progress](https://github.com/rp2839/visio-mcp/issues?q=is%3Aissue%20is%3Aopen%20label%3Astatus%3Ain-progress) ·
[Awaiting review](https://github.com/rp2839/visio-mcp/issues?q=is%3Aissue%20is%3Aopen%20label%3Astatus%3Areview)
