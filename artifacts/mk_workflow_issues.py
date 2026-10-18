"""One-off: creates the W-series workflow issues on the GitHub project (run 2026-10-18)."""
import subprocess, json
R = 'rp2839/visio-mcp'
PID = subprocess.check_output(['gh', 'project', 'view', '3', '--owner', 'rp2839', '--format', 'json', '-q', '.id'], text=True).strip()
fields = json.loads(subprocess.check_output(['gh', 'project', 'field-list', '3', '--owner', 'rp2839', '--format', 'json'], text=True))['fields']
F = {f['name']: f for f in fields}


def opt(field, name):
    return next(o['id'] for o in F[field]['options'] if o['name'] == name)


COORD = """## Coordination

Unclaimed. This is a backlog proposal, not an approved design. Before editing, add the agent label, set In progress on the board, and post a claim with branch, scope and affected files. Before Done, link commits, tests, evidence and remaining limitations."""
SRC = """## Sources

- Reported by the user on 2026-10-18 while using the installed app, or found in a review of the File menu, window shell and host code that day."""

items = [
    ('W01', 'Ask to save unsaved changes when the window is closed, and close cleanly', 'bug', 'P0', 'S',
     'Closing the window with unsaved changes gives no prompt (`MainWindow.xaml.cs` has no Closing handler), so the work is left in recovery and the next start shows "Recover unsaved work?" almost every time. There is no clean way to close without saving.',
     ["Closing the window with unsaved changes asks Save / Don't save / Cancel; Cancel keeps the app open.",
      "Save writes the file (Save as for an untitled document) and then closes; Don't save discards the recovery session so the next start does not offer it.",
      'A crash or forced kill still leaves recovery behind and offers it, exactly as today.',
      'Regression tests for window close in the Windows suite, covering all three choices and an untitled document.'],
     'I51 (recovery), H01'),
    ('W02', 'Tabbed documents with session restore (like Notepad++)', 'enhancement', 'P0', 'XL',
     "The app holds one document. Wanted: each document is a tab; new and opened documents add tabs; the previous session's tabs reopen at start; closing a tab prompts to save if it has changes, and choosing not to save discards them.",
     ['Several documents open at once as tabs, each with its own undo history, selection, zoom and recovery session.',
      "The set of open documents (and which tab is active) is restored on the next start, including untitled documents with unsaved work.",
      "Closing a tab with changes asks Save / Don't save / Cancel; Don't save discards that document's changes and recovery; closing the window applies the same rule to every changed tab.",
      'MCP tools address a document by documentId; define which tab is the default target, what `get_document_summary` returns, and how an agent opens or creates a tab.',
      'Ctrl+Tab and Ctrl+W work; each tab shows the file name and an unsaved marker.'],
     'W01, H01, W04'),
    ('W03', 'File > New document (Ctrl+N) and page-size templates', 'enhancement', 'P1', 'S',
     'The File menu has Open and Save but no New. Starting a new drawing means restarting or editing the current one.',
     ['File > New and Ctrl+N create an untitled document (a tab once W02 exists; until then, after the unsaved-changes prompt).',
      'Choose a starting page: A4 landscape/portrait, A3, Letter and custom, plus a blank swim-lane or flow starter when R09 exists.',
      'The new document is untitled, unsaved and offered for recovery like any other.'],
     'W01'),
    ('W04', 'Show the document name and an unsaved marker in the window title and status bar', 'enhancement', 'P1', 'XS',
     'The window title is the fixed word "Diagram", so the user cannot see which file is open or whether it has changes. The status bar says "Protected to revision N" but not the file name.',
     ['Title shows file name (or Untitled), an asterisk when changed, and the app name.',
      'Status bar shows the full path on hover and the last-saved time.',
      'Taskbar title follows the same text.'],
     ''),
    ('W05', 'Recent files and reopen last file', 'enhancement', 'P2', 'S',
     'There is no way back to a recently used file except the Open dialog.',
     ['File > Recent lists the last 10 files, with missing files marked and removable.',
      'Start menu jump list lists the same files.',
      'The Open dialog starts in the last used folder.'],
     'W02'),
    ('W06', 'Recovery dialog that says what it is recovering, and handles several sessions', 'enhancement', 'P1', 'M',
     'The dialog says only "An earlier session ended with changes that were not saved ... revision 2". It does not name the document, show a preview or say how many sessions exist. It also appears for sessions the user abandoned by closing the window.',
     ['The dialog names each recoverable document (title or path, page count, last change time) with a thumbnail.',
      'Several recoverable sessions are listed; each can be recovered, deferred or discarded.',
      'Recovered work opens as its own document (a tab once W02 exists) and is offered Save as.',
      'Sessions already restored or discarded are never offered again (shares work with H01).'],
     'H01, W01'),
    ('W07', 'Detect a file changed on disk and handle save conflicts', 'enhancement', 'P1', 'M',
     'The current file can be changed by OneDrive sync, Visio or another person while it is open. Save would overwrite it silently.',
     ['Notice, on save or when the window regains focus, that the file on disk is newer than the version opened.',
      'Offer Reload, Keep mine (Save as) or Overwrite after showing the modified time and size.',
      'Test with an external write between open and save.'],
     'W01'),
    ('W08', 'Export pictures and PDF from the editor (PNG, SVG, JPEG, PDF) and copy as image', 'enhancement', 'P1', 'M',
     'The MCP render tools produce PNG, SVG and JPEG, but the editor menu offers only .vsdx export, so a person cannot get a picture of the diagram without an agent.',
     ['File > Export image: PNG, JPEG and SVG for the page, the selection or all pages, with scale and background choices.',
      'File > Export PDF (all pages).',
      'Edit > Copy as image puts the selection on the clipboard.',
      'Exports use the same render barrier and export-path policy as the MCP tools.'],
     'I31, I51'),
    ('W09', 'Print and print preview', 'enhancement', 'P2', 'M',
     'There is no Print command. Pages larger than the printer paper need fitting or tiling.',
     ['File > Print with page range, fit/tile, margins and orientation, and a preview.',
      'Print to PDF uses the same path as W08.'],
     'W08'),
    ('W10', 'Draggable guides and snapping to them', 'enhancement', 'P2', 'M',
     'The document model has page guides and the rulers draw, but there is no way to create, move or delete a guide in the editor, and objects do not snap to them.',
     ['Drag from a ruler to create a guide; drag it back to delete; move by dragging.',
      'Objects snap to guides (respecting the layer snap flag); guides persist, round-trip to Visio and are undoable.',
      'Show or hide guides in View.'],
     'I20'),
    ('W11', 'Find and replace text, and find objects by name or alias', 'enhancement', 'P2', 'M',
     'There is no way to search a large diagram (the Lego example has about 170 objects) for a label, or to select all objects with the same text, style or layer.',
     ['Ctrl+F searches text, names and aliases across pages and selects or steps through matches.',
      'Replace changes text in one transaction (undo reverts all).',
      'Select all of the same style or layer.'],
     'I20'),
    ('W12', 'Help: keyboard shortcut list, About and version, and a command palette', 'enhancement', 'P2', 'S',
     'Shortcuts are shown only next to menu items. There is no About, no version number and no way to find a command by name.',
     ['Help > Keyboard shortcuts lists every command and shortcut.',
      'Help > About shows the app, build and MCP companion versions and the data folders.',
      'Ctrl+Shift+P opens a command palette.'],
     ''),
    ('W13', 'Preferences: units, grid, default font, recovery interval and theme', 'enhancement', 'P2', 'M',
     'Units, grid spacing, default fonts and the recovery checkpoint interval are fixed. The units selector applies to the current session only.',
     ['A Preferences dialog (persisted per user) for units, default grid, default font and size, recovery interval, and light or dark theme.',
      'Defaults apply to new documents and are not written into existing ones.'],
     'W03'),
    ('W14', 'Drag and drop files onto the window', 'enhancement', 'P2', 'S',
     'Dropping a .diagram.json, .vsdx or picture onto the window does nothing.',
     ['Dropping a diagram or Visio file opens it (as a tab once W02 exists) after the unsaved-changes rule.',
      'Dropping a picture adds it to the library and offers to place it; pasted clipboard pictures do the same.'],
     'W02'),
    ('W15', 'Single instance, file association and open-with', 'enhancement', 'P1', 'M',
     'Starting the app twice launches two windows that share the MCP pipe name and the recovery folder. Double-clicking a document opens nothing.',
     ['A second launch forwards to the running window (and opens the file it was given).',
      '.diagram.json is associated with the app; Open with works for .vsdx through import.',
      'Behaviour with two Windows users is defined (relates to A01).'],
     'W02, A01'),
    ('W16', 'Installer, update and uninstall', 'enhancement', 'P1', 'L',
     'The app is installed by a developer script (`tools/publish-local.ps1`) that copies a build to a versions folder and rewrites the Start menu shortcut and the MCP registration. There is no installer, no update path and no uninstall.',
     ['A signed installer (MSIX or MSI) installs the app and MCP companion per user and registers the Start menu entry.',
      'In-place update keeps user data (library, recovery, settings) and tells the user what changed.',
      'Uninstall removes the program and offers to keep or delete user data.',
      'Document how MCP clients (Claude Code, Codex) are registered, ideally by a command in the app.'],
     ''),
    ('W17', 'Keyboard-only and screen-reader operation of the canvas', 'enhancement', 'P2', 'L',
     'Menus work by keyboard but shapes cannot be reached by Tab, selected, moved or connected without a mouse, and the canvas exposes no accessible structure.',
     ['Tab and arrow keys move between objects; Enter edits text; arrows nudge the selection by the grid step.',
      'Objects expose names, roles and positions to UI Automation.',
      'Connector creation without a pointer (choose source and target from a list).'],
     'A03'),
]
existing = {i['title']: i['number'] for i in json.loads(subprocess.check_output(['gh', 'issue', 'list', '-R', R, '--state', 'all', '--limit', '200', '--json', 'number,title'], text=True))}
import time


def item_id(num):
    for _ in range(10):
        for it in json.loads(subprocess.check_output(['gh', 'project', 'item-list', '3', '--owner', 'rp2839', '--limit', '200', '--format', 'json'], text=True))['items']:
            if it['content'].get('number') == num:
                return it['id']
        time.sleep(2)
    raise SystemExit(f'issue {num} not on the board')


for code, title, kind, prio, size, problem, ac, dep in items:
    full = f'[{code}] {title}'
    if full in existing and code == 'W01':
        continue
    body = (f"Stable work ID: **{code}**. This issue owns current status.\n\n## Problem and existing evidence\n\n{problem}\n\n## Dependencies\n\n{dep or 'None'}\n\n"
            "## Acceptance criteria\n\n" + '\n'.join(f'- [ ] {a}' for a in ac) + f"\n\n{COORD}\n\n{SRC}\n")
    if full in existing:
        num = existing[full]
        out = None
    else:
      out = subprocess.check_output(['gh', 'issue', 'create', '-R', R, '--title', f'[{code}] {title}', '--body', body, '--label', kind, '--label', 'status:backlog', '--label', 'area:workflow'], text=True).strip()
      num = int(out.rsplit('/', 1)[1])
    item = item_id(num)
    for name, val in (('Status', 'Backlog'), ('Priority', prio), ('Size', size)):
        subprocess.check_call(['gh', 'project', 'item-edit', '--project-id', PID, '--id', item, '--field-id', F[name]['id'], '--single-select-option-id', opt(name, val)], stdout=subprocess.DEVNULL)
    print(code, num, prio, size, flush=True)
