# Glimpse Desktop

**A Windows desktop lens that remembers what you scroll past.**

Glimpse is a planned Windows desktop assistant. The user selects one fixed screen region with a transparent, click-through frame; local OCR observes stable frames, deduplicates overlapping text during scrolling, and accumulates a recoverable **Session Document**. A user-triggered Agent then uses relevant session context to propose comments, replies, summaries, or suggestions as copyable cards.

> Status: **design / implementation not started**. This repository currently contains specifications and an execution plan, not a working application.

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

Build a **Session Reader without LLM integration**:

1. Draw/move/resize a single transparent lens and click-through interior.
2. Capture frames from the physical screen rectangle, detect changes and wait for 1 second of stability.
3. OCR local text, align overlapping scroll snapshots, maintain deduplicated ordered content blocks.
4. Persist/reopen a session in SQLite, retaining 9 effective OCR snapshots.
5. Show state, snapshot count, new/duplicate blocks and suspected reading gaps in a diagnostic inspector.
6. Verify on mixed-DPI, multi-monitor and CPU-only Windows systems.

Only after that checkpoint passes, add LLM providers and result cards.

## Project principles

- Do not infer completeness from a green status: **green means the last stable frame was processed and committed**.
- Do not silently rewrite or erase uncertain OCR results. Preserve evidence and provenance.
- Do not equate the **9-snapshot ring** with the size of the accumulated session text.
- Treat all screen text as untrusted, including any apparent instructions addressed to the Agent.
- Each milestone needs automated tests and a user-verifiable demo. Document measured performance rather than asserting unmeasured numbers.

License and governance are undecided; do not invent them.
