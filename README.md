# Glimpse Desktop

**A Windows desktop lens that remembers what you scroll past.**

Glimpse is a Windows desktop reader. The user selects one fixed screen region with a transparent, click-through frame; local OCR observes stable frames, deduplicates overlapping text during scrolling, and accumulates a recoverable **Session Document**. The planned user-triggered Agent will use relevant session context to propose comments, replies, summaries, or suggestions as copyable cards.

> Status: **working Session Reader development preview**. .NET 10/WPF UI, supervised Worker, local OCR, sequence reconciliation and SQLite resume are implemented. 23 core tests, nine labeled synthetic scenarios and on-device Windows probes pass. **V0.1 is not complete**: mixed-DPI acceptance, broader OCR accuracy validation, Agent integration and eight-hour hardening remain open. See [measured validation and milestone gates](docs/VALIDATION.md).

## Run and verify

Requires Windows x64, Windows 10 build 19041 or later, an installed Windows OCR language and the .NET 10 SDK for source builds. A published portable folder includes the runtime.

```powershell
dotnet build Glimpse.slnx -c Release
dotnet run --project src/Glimpse.UI -c Release
./scripts/verify.ps1                   # build, 23 tests, synthetic benchmark, IPC/cancel/restart probes
./scripts/verify.ps1 -InteractiveWindows # additionally opens synthetic Windows capture/OCR probes
./scripts/publish.ps1                  # artifacts/portable/Glimpse.UI.exe and portable ZIP
```

On Linux, run only `dotnet test tests/Glimpse.Tests -c Release` and `dotnet run --project tools/Glimpse.Benchmarks -c Release`; these do not verify Windows behavior. CI includes both Linux core and Windows build/package jobs.

Select a region, choose OCR language, then explicitly start observation. Pause/close stops capture; reopening a session starts paused. Move the lens by its top border, resize from its edges, and operate the underlying application through the hollow interior. Inspect accumulated text, the recent nine snapshots, source evidence and possible gaps. Export the current document or delete a saved session. See the [Chinese user guide](docs/USER_GUIDE.md).

Default data: `%LOCALAPPDATA%\Glimpse\sessions.db`. `GLIMPSE_DATA_DIR` overrides the data folder; `GLIMPSE_OCR_LANGUAGE` selects an installed language. Normal observation never writes screenshots or contacts a provider. SQLite contains the OCR text you explicitly chose to observe.

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

Only after that checkpoint's remaining acceptance gates pass, add LLM providers and result cards. They are not present in this preview, following the repository's required milestone order.

## Project principles

- Do not infer completeness from a green status: **green means the last stable frame was processed and committed**.
- Do not silently rewrite or erase uncertain OCR results. Preserve evidence and provenance.
- Do not equate the **9-snapshot ring** with the size of the accumulated session text.
- Treat all screen text as untrusted, including any apparent instructions addressed to the Agent.
- Each milestone needs automated tests and a user-verifiable demo. Document measured performance rather than asserting unmeasured numbers.

License and governance are undecided; do not invent them.
