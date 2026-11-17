# ADR 0008: One-click tasks and inline sidebar answers

Date: 2026-11-17. Status: development implementation.

The owner explicitly replaced the toolbar's dropdown/preview-to-inspector flow with direct task buttons and answers in the sidebar, and requested an interaction-designer review. A click on comment/reply/summary/advice or sending a custom question now authorizes one model request. This supersedes the mandatory second Generate click for the sidebar only. The inspector's original reviewed-preview workflow remains available.

The sidebar receives an async generation callback from MainWindow. It loads the saved engine configuration, asks Worker for a new filtered/frozen context, exposes that context in an optional inline expander, and submits its one-shot ID immediately. It never shows the inspector to generate or display errors. The settings entry explicitly opens and scrolls to engine settings. Session changes, closing and cancellation invalidate the sidebar's local operation version and cancel the targeted backend request. Canceling a prepared ID also retires it before a queued background run can start, closing the cancel-before-dispatch race.

Interaction review identified ambiguous send semantics, loss of previous answers after failure/cancel, scrolling cancellation controls, and missing task provenance. The revised sidebar labels custom submission as a question, explains direct commands, keeps generation feedback/cancel visible, identifies the executing task, and retains the last completed/fallback answer until a new answer replaces it. Session changes still clear unrelated answers. Results remain locally validated, visibly mark raw-text fallback, and only offer copying; there is no automatic posting or application control.

The actual Windows clipboard can be briefly locked, including during test readback. Copy writes now retry four times with short asynchronous waits, then report a retry action if still blocked. Tests retry only clipboard read access and still require exact character/line-ending equality; they never relax text comparison. Terminal input/authentication behavior is unchanged.

Offline WPF tests cover direct action count, task/prompt forwarding, inline cards/copy, explicit settings, busy guard, late results, cancellation/session changes/close, and retained previous results. Opt-in authenticated CodeBuddy testing uses only the existing synthetic fixture. Normal capture does not trigger inference. Original DPI/OCR/long-running acceptance limits remain open.
