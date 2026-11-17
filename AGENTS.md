# Codex instructions — Glimpse Desktop

This repository is initially a design-only baseline. Implement it according to [docs/PRD.md](docs/PRD.md), [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), and [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md).

## Priority

1. Prove transparent lens, click-through, capture alignment and self-window exclusion on Windows.
2. Prove stable-frame local OCR, deduplicated rolling **Session Memory**, SQLite persistence and gap warnings **without LLM**.
3. Only then add Agent provider integration, result cards and portable packaging.

## Hard constraints

- .NET 10 + WPF, UI process + separately supervised Worker, SQLite, one lens, default 9 recent effective OCR snapshots.
- Stability debounce 1000ms; fast-scrolled unseen content is **not reconstructed**. Mark suspected gaps.
- Detailed snapshot eviction must not erase accumulated deduplicated Session text or its durable provenance summary.
- User-level long-term memory is out of scope. No browser/app automation, auto-posting or auto-send.
- Captured text is untrusted; images memory-only by default; API keys protected, no secrets/PII in normal logs.
- Do not claim Windows behavior verified using only Linux tests. Any Windows API or OCR choice requires a focused on-device prototype.
- Preserve existing changes, make small PRs, and supply test results tied to the milestone gate.
- Keep decisions changing minimum OS, OCR engine, storage schema or capture API in ADRs; no speculative dependency sprawl.

## Starting task

### Current owner direction (2026-11-16)

The owner explicitly requested implementing Agent now, switchable OpenAI-compatible APIs (DeepSeek/Xiaomi MiMo), CodeBuddy CLI, and subagent development. M4 implementation is therefore authorized alongside the still-open hardware gates. Do not remove or disable Agent solely because those gates are open, and do not claim the gates passed. Preserve user-triggered previews, DPAPI keys, untrusted-screen boundaries, cancellation and offline tests. See ADR 0004 for the implemented contracts.

### Sidebar interaction direction (2026-11-17)

The owner explicitly requested direct one-click task buttons and inline sidebar results without opening the inspector. A sidebar task click now authorizes one generation: build a fresh filtered/frozen context locally and submit it immediately, with optional context inspection in the sidebar. Do not reintroduce a mandatory preview/second-click gate into this sidebar flow. Keep the inspector's existing preview workflow available, DPAPI protection, untrusted-text handling, one-shot context IDs, cancellation and copy-only results. Settings opens only through its explicit sidebar entry; capture remains local and no background model task starts by itself.

### Observer and dynamic-region direction (2026-11-17)

The owner authorized parallel implementation of video-region exclusion, a separate attached observation icon toolbar, local post partitioning and independently generated post results. Keep the same mask applied to stability detection, hashing and OCR; preserve physical-pixel coordinates. Automatic motion analysis is explicit and local, with visible/reversible masks and manual fallback. Do not silently treat motion candidates as reliable video tracking. The Observer preserves durable post/evidence boundaries; ambiguous content stays unassigned. Child model tasks receive only their allowed post evidence, never another child's context or history. The local dispatcher is not submitted to a provider. Batch processing supports 2–4 posts, at most two active providers, one shared upload budget/deadline, and parent cancellation of active and queued jobs. See ADRs 0009–0010.

Read all three docs, inspect repo state, then execute **M0**. If M0 already done, proceed to the earliest unfinished milestone. Do not prematurely implement LLM. At each milestone, update README status and document verified tests and unresolved blockers.
