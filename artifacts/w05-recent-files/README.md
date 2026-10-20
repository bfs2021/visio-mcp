# W05 evidence

See [report](../../docs/m5/W05-recent-files.md). Final full .NET run on product commit 70106b9: 429 passed, 0 failed, 0 skipped; frontend 391 and browser 107 passed, with typecheck/generate-check/build successful. Native screenshot recent-dialog.png was visually inspected in the actual WebView2 host.

Negative controls: initial store/native red logs show absent functionality; browser red waits for absent menu. pinned-open-red reproduces both JSON/Visio late-open data-loss cases against previous host source; corruption-warning-red reproduces three silent warning losses. folder-deadline-red records absent directory deadline API. Corrected targeted runs passed eight store cases and five Windows cases. An early short probe test exposed shared thread-pool starvation; bounded dedicated workers and a scheduling-tolerant injected test resolved it.

The interrupted .NET log and partial final-results folder are not completion evidence. verified-results and dotnet-tests.txt supersede them with the complete successful run. Frontend/browser source is unchanged since the recorded successful runs.

Manual human acceptance and native picker interaction: NOT RUN. Start/taskbar jump lists and startup activation: NOT IMPLEMENTED, pending W15 #37 under the approved design.
