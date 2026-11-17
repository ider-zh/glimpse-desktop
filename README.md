# Glimpse Desktop

**A Windows desktop lens that remembers what you scroll past.**

Glimpse is a Windows desktop reader. The user selects one fixed screen region with a transparent, click-through frame; local OCR observes stable frames, deduplicates overlapping text during scrolling, and accumulates a recoverable **Session Document**. A user-triggered Agent uses reviewed session context to propose comments, replies, summaries, or suggestions as copyable cards.

> Status: **working Reader + Agent development preview**. Local OCR, SQLite resume, switchable engines, one-click sidebar tasks and validated cards are implemented. 58 core tests and offline provider/controller/IPC checks pass. Authenticated CodeBuddy inline generation, cancellation, model selection and exact card copying pass on this Windows device, including `mimo-v2.6-pro`; login uses embedded xterm.js. **V0.1 is not complete**: direct external API acceptance, mixed-DPI hardware, broader OCR accuracy and eight-hour hardening remain open. See [measured validation](docs/VALIDATION.md).

## Run and verify

Requires Windows x64, Windows 10 build 19041 or later, an installed Windows OCR language and the .NET 10 SDK for source builds. A published portable folder includes the runtime.

```powershell
dotnet build Glimpse.slnx -c Release
dotnet run --project src/Glimpse.UI -c Release
./scripts/verify.ps1                   # build, 58 tests, corpus, provider/Agent/IPC/cancel/restart probes
./scripts/verify.ps1 -InteractiveWindows # additionally opens synthetic Windows capture/OCR probes
./scripts/publish.ps1                  # artifacts/portable/Glimpse.UI.exe and portable ZIP
# If that folder is currently running: ./scripts/publish.ps1 -OutputName agent-preview
```

On Linux, run only `dotnet test tests/Glimpse.Tests -c Release` and `dotnet run --project tools/Glimpse.Benchmarks -c Release`; these do not verify Windows behavior. CI includes both Linux core and Windows build/package jobs.

Opening the app immediately asks you to draw a region; completing a valid selection starts local observation. An attached sidebar follows the lens with record access, start/stop, reselect, close-frame, direct task buttons and custom questions. Answers/cancel/copy stay inline; settings opens only through its explicit entry. Each task click builds filtered context and submits once, with optional inline payload inspection. Stop/close preserves records; canceling selection leaves capture stopped, and reopening saved sessions still starts paused. See the [Chinese user guide](docs/USER_GUIDE.md), [ADR 0007](docs/adr/0007-selection-first-toolbar.md) and [sidebar update](docs/adr/0008-inline-sidebar-actions.md).

Default data: `%LOCALAPPDATA%\Glimpse\sessions.db`. `GLIMPSE_DATA_DIR` overrides the data folder; `GLIMPSE_OCR_LANGUAGE` selects an installed language. Observation never writes screenshots or uploads OCR context by itself. The selected CodeBuddy engine can fetch account model metadata on loading settings; inference still requires Generate. SQLite contains the OCR text you explicitly chose to observe.

## Agent engines

In the **Agent** tab, configure an engine and click **保存并切换**. API profiles accept a base URL, model and a write-only API key protected with Windows DPAPI. DeepSeek and Xiaomi MiMo presets are editable; a custom OpenAI-compatible Chat Completions profile supports other providers and local APIs. CodeBuddy CLI uses your existing installation/login; npm installations are detected as a Node.js executable plus JavaScript entry, without a shell wrapper.

CodeBuddy settings automatically fetch the logged-in CLI's available models via ACP metadata without inference, including after login closes. Select a model ID from the editable dropdown or type one, then save. Blank keeps the CLI default; fetching never changes the selected model automatically.

**登录 CodeBuddy…** opens a local xterm.js terminal hosted by WebView2, with direct keyboard input, terminal selection/copy/paste, font sizing and automatic ConPTY resizing. Assets are bundled; no CDN is used. Complete account verification yourself in the browser using the displayed URL, close the terminal, then preview and generate. The Worker owns this isolated process; closing/disconnecting stops it. Terminal output/links remain ephemeral and the window is excluded from capture. WebView2 Runtime is required for the interactive component; a basic text fallback remains available. See [ADR 0006](docs/adr/0006-codebuddy-login-terminal.md).

In the sidebar, click a task or send a custom question to generate once from fresh filtered, budgeted context; answers appear there without opening the inspector. Progress/cancel stays visible and the previous answer survives a failed/canceled retry. The main inspector also retains its optional preview-first workflow. Context IDs are one-shot and tied to session/engine; cancellation does not block capture/pause. Copy validated cards or inspect a visibly marked plain-text fallback. No screenshots, tool calls, application control or automatic posting are sent. CI needs no real API keys or external model requests.

Automatic small-text OCR enhancement is enabled; original size and fixed 2× are available in the inspector. `GLIMPSE_OCR_SCALE` accepts 0/1/2. Generated small-font corpus measurements are recorded separately from real-page accuracy claims.

## V0.1 decisions

- Windows, **.NET 10 + WPF**, single fixed-coordinate lens; frame/toolbar interactive, content area click-through.
- UI process + supervised background **Worker**; local IPC via named pipes.
- Adaptive screen-change detection, **1 second of stability** before OCR; no attempt to reconstruct unseen fast-scrolled content. Warn when a gap is suspected.
- Swappable **local OCR** engine via interface; no requirement for GPU or NPU.
- Keep the **last 9 effective OCR snapshots** per session; deduplicated content blocks and session document outlive snapshot eviction.
- Session data stored locally in SQLite and **resumable after closing the lens/app**.
- No user-profile long-term memory in V0.1. No automatic clicking, typing, posting, or sending.
- Pluggable LLM providers; task-specific validated JSON cards with one-click copy. LLM operations are user-triggered by default.
- Privacy defaults: raw screenshots memory-only, optional explicit diagnostic capture, sensitive-text filtering before cloud requests, pause control and transparent data preview.
- Self-contained portable **folder** release; data in LocalAppData by default.

## Reading order

1. [Product specification](docs/PRD.md) — interactions, scope, privacy, UI, decisions.
2. [Technical architecture](docs/ARCHITECTURE.md) — data contracts, algorithms, state machines, failure handling.
3. [Implementation plan](docs/IMPLEMENTATION_PLAN.md) — milestones, dependencies, tests and acceptance gates.
4. [AGENTS.md](AGENTS.md) — instructions for Codex implementing this repository.

## First implementation checkpoint

The implemented **Session Reader without LLM integration** covers:

1. Draw/move/resize a single transparent lens and click-through interior.
2. Capture frames from the physical screen rectangle, detect changes and wait for 1 second of stability.
3. OCR local text, align overlapping scroll snapshots, maintain deduplicated ordered content blocks.
4. Persist/reopen a session in SQLite, retaining 9 effective OCR snapshots.
5. Show state, snapshot count, new/duplicate blocks and suspected reading gaps in a diagnostic inspector.
6. Verified on this CPU-only Windows device at 100% DPI, including a negative-coordinate second monitor. **125%/150% mixed-DPI hardware acceptance remains unverified.**

The owner subsequently requested Agent implementation alongside the remaining hardware gates. That explicit scope change is recorded in [ADR 0004](docs/adr/0004-agent-engines.md); it does not mark unverified Reader gates complete.

## Project principles

- Do not infer completeness from a green status: **green means the last stable frame was processed and committed**.
- Do not silently rewrite or erase uncertain OCR results. Preserve evidence and provenance.
- Do not equate the **9-snapshot ring** with the size of the accumulated session text.
- Treat all screen text as untrusted, including any apparent instructions addressed to the Agent.
- Each milestone needs automated tests and a user-verifiable demo. Document measured performance rather than asserting unmeasured numbers.

License and governance are undecided; do not invent them.
