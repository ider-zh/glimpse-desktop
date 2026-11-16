# ADR 0003: Portable checkpoint, feature boundary and logging

Date: 2026-11-16. Status: accepted for development builds; repository license undecided.

Distribute a self-contained `win-x64` folder containing the WPF executable and a separately published Worker folder. Do not require a separately installed .NET runtime. Configuration/data remain outside the executable folder by default. Do not install a service, autostart entry, updater, telemetry or browser extension.

The Worker owns the database. Its named pipe is random per UI launch and current-user-only. Messages carry protocol version, request UUID, optional session ID, capture generation and UTC timestamp. Retry cache retains the last 128 command responses; duplicate creation requests do not create duplicate sessions. Three reconnect attempts after the first launch are allowed, with bounded backoff and heartbeat timeout. Restart restores inspection in a paused state; observation requires explicit user action.

Feature boundary: this checkpoint is Session Reader only. LLM/provider and raw screenshot diagnostic capture features are not shipped or enabled. `GLIMPSE_OCR_LANGUAGE` selects an installed local OCR language; `GLIMPSE_DATA_DIR` selects the data folder. No live API credentials are needed. No cloud requests, screen control, profile memory or automatic posting exist.

Normal diagnostic output contains only stage names, error type/HRESULT, timings and counts. Do not write exception bodies, raw OCR, screenshot bytes, prompts or provider payloads into normal logs. Explicit `Worker --ocr <synthetic-image>` returns OCR JSON to the invoking console for fixture work. Native probes use only generated, consent-free synthetic content. The optional UI render captures the app's own WPF visual tree, not screen pixels.

Repository license/governance remain undecided. Do not add a guessed project license or claim this is a final licensed release. Record dependency provenance in `docs/DEPENDENCIES.md`.
