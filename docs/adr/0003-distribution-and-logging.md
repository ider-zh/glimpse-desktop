# ADR 0003: Portable checkpoint, feature boundary and logging

Date: 2026-11-16. Status: accepted for development builds; repository license undecided.

Distribute a self-contained `win-x64` folder containing the WPF executable and a separately published Worker folder. Do not require a separately installed .NET runtime. Configuration/data remain outside the executable folder by default. Do not install a service, autostart entry, updater, telemetry or browser extension.

The Worker owns the database. Its named pipe is random per UI launch and current-user-only. Messages carry protocol version, request UUID, optional session ID, capture generation and UTC timestamp. Retry cache retains the last 128 command responses; duplicate creation requests do not create duplicate sessions. Three reconnect attempts after the first launch are allowed, with bounded backoff and heartbeat timeout. Restart restores inspection in a paused state; observation requires explicit user action.

The initial checkpoint was Session Reader only. The owner's later explicit Agent request supersedes that feature boundary as recorded in ADR 0004. Agent inference now exists but requires review and an explicit generation action; observation remains offline. Raw screenshot diagnostic capture is not enabled. `GLIMPSE_OCR_LANGUAGE` selects a local OCR language, `GLIMPSE_OCR_SCALE` controls enhancement, and `GLIMPSE_DATA_DIR` selects the data folder. No live credentials are needed for CI. Screen control, profile memory and automatic posting remain excluded.

Normal diagnostic output contains only stage names, error type/HRESULT, timings and counts. Do not write exception bodies, raw OCR, screenshot bytes, prompts or provider payloads into normal logs. Explicit `Worker --ocr <synthetic-image>` returns OCR JSON to the invoking console for fixture work. Native probes use only generated, consent-free synthetic content. The optional UI render captures the app's own WPF visual tree, not screen pixels.

Repository license/governance remain undecided. Do not add a guessed project license or claim this is a final licensed release. Record dependency provenance in `docs/DEPENDENCIES.md`.
