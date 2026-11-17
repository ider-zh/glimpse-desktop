# ADR 0007: Selection-first observation with an attached toolbar

Date: 2026-11-17. Status: development implementation.

The owner explicitly requested opening into region selection, automatic observation after drawing a valid region, an attached toolbar with record access, observation start/stop, prompt presets/custom input, and a close-frame action. Completing selection is now the explicit local-capture trigger. Model inference still requires reviewed context and a separate Generate action.

The production App starts MainWindow in selection-first mode. Region selection opens immediately while the Worker connects; local capture waits for connection/configuration and successful exclusion of Glimpse windows. Completing selection creates or updates a session and sends observe. Esc/cancel keeps capture stopped and makes the inspector available. Reopening saved records and Worker reconnect remain paused. The reusable MainWindow constructor defaults to inspector-only mode for existing native probes; the production App explicitly enables selection-first behavior.

A separate topmost, interactive WPF toolbar is owned by the lens. It is excluded from capture before controls are enabled. Native physical-pixel placement uses MonitorFromRect/GetMonitorInfo work areas and the toolbar's current GetDpiForWindow value, preferring right/left/below/above outside the lens. With a nearly full-display lens there may be no outside space, so controls are clamped inside the work area and remain reachable. WM_DPICHANGED/display/work-area notifications trigger placement recomputation; mixed-DPI hardware still requires acceptance testing.

The UI owns this workflow; Worker OCR/storage contracts and the one-second stability debounce are unchanged. WM_ENTERSIZEMOVE pauses active observation, and WM_EXITSIZEMOVE plus the existing geometry debounce resumes only after native movement ends. Stop cancels observation while keeping the lens. Close frame sends pause, closes lens/toolbar and shows preserved records. Closing the inspector/application stops its Worker and all observation. Toolbar presets and typed prompts route to AgentPanel's local context preview; they do not call a provider.

On-device probes use only a generated synthetic reading window and isolated data directory. They cover placement on both 96-DPI monitors, exclusion, native hit testing, preset/custom events, selection-triggered observation, first OCR text, stop/restart/close and cancel behavior. Agent UI tests require toolbar preparation to perform zero inference calls.

Primary references: [MonitorFromRect](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-monitorfromrect), [GetMonitorInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getmonitorinfoa), [GetDpiForWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow), [native move-loop completion](https://learn.microsoft.com/en-us/windows/win32/winmsg/wm-exitsizemove).
