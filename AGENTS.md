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

Read all three docs, inspect repo state, then execute **M0**. If M0 already done, proceed to the earliest unfinished milestone. Do not prematurely implement LLM. At each milestone, update README status and document verified tests and unresolved blockers.
