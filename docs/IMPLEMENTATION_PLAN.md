# Glimpse V0.1 — Codex Implementation Plan

## Execution rules

- Deliver in small, reviewable PRs. Do not begin with an all-in-one Agent or polished UI.
- Each milestone must include executable tests, example commands, observed results, screenshots/logs where safe, and explicit unmet acceptance items.
- First build a **Session Reader without any LLM dependency**.
- Prefer deterministic fixture tests over network-dependent tests. Never put live API keys in CI.
- Do not change agreed product scope unilaterally. Record major technical choices in `docs/adr/`.
- Windows-specific integration and UI acceptance **must run on Windows**. Linux CI can run pure .NET core tests but is not evidence that WPF/capture works.

## Milestones and gates

### M0 — Repository bootstrap

Tasks:
- Solution layout: `src/Glimpse.UI`, `src/Glimpse.Worker`, `src/Glimpse.Core`, `src/Glimpse.Contracts`, `src/Glimpse.Storage`, `tests/`.
- .NET 10 SDK pin (`global.json`), formatting/analyzers, README run/build commands, Windows build CI and pure-core unit-test CI.
- Logging policy (no raw OCR/images/payloads by default) and feature flags.
- ADRs for minimum Windows build, screenshot API, baseline OCR backend and distribution/license decision.

Gate: clean checkout builds on Windows; headless tests run without API credentials.

### M1 — Lens + capture feasibility spike [critical path]

Tasks:
- Single topmost transparent WPF lens with interactive border/toolbar and click-through interior.
- Physical-pixel capture coordinates; move/resize; multi-monitor and mixed DPI.
- Screen capture with crop; own-window exclusion, including external and in-lens answer area tests.
- Pause/stop; live status diagnostics; injected fake frames for test harness.

Gate:
- Underlying webpage remains clickable/scrollable through lens.
- Captured region aligns with selected region at 100%, 125%, 150% DPI; negative virtual coordinates tested.
- Overlay/toolbars do not re-enter screenshot; show explicit error or fallback if exclusion unsupported.
- UI stays responsive during capture failure.

### M2 — Local OCR + state machine

Tasks:
- `IOcrEngine` with one CPU-capable local implementation and Fake OCR.
- Adaptive visual difference detector; 1.0s stable debounce; OCR task cancellation.
- Capture generation/ordered state transitions, status colors and per-stage metrics.
- CLI/fixture harness: image → OCR blocks + bbox + elapsed time.

Gate:
- 1.0s stability timer resets on renewed movement.
- Repeated identical stable frames do not fill effective snapshot ring.
- State only becomes green after durable processing; errors are visible.
- Test mixed Chinese/English, small text and blank screens; report accuracy and latency.

### M3 — Session Reader, dedupe and storage [core MVP]

Tasks:
- SQLite migrations for sessions/snapshots/blocks/relations/gaps.
- Synthetic/recorded **consented and sanitized** scroll fixture corpus: downward, upward, partial overlap, identical short replies, loading spinners, OCR typos, page swaps, large jumps.
- Block normalization, weighted sequence alignment, conservative dedupe, insertion-order representation, uncertain flags, gap heuristics.
- Atomic commit and pruning of recent detailed snapshots to 9, with permanent session text/provenance preserved.
- Session inspector: recent snapshots, deduped ordered document, new/reused/uncertain blocks, gap list.
- Close/reopen/resume; worker crash recovery.

Gate:
- At 50% overlap, do not duplicate aligned blocks; two identical short comments from distinct contexts remain distinguishable.
- After >9 effective frames, detailed ring length = 9 while earlier deduped text remains.
- Session restored after restart, document revision and source summary unchanged.
- Large scroll jump marked as possible gap when supported by evidence; non-overlap page switches do not automatically assert gap.
- Benchmark on labeled corpus: track block precision/recall, duplicate rate, erroneous merge count and order accuracy. Set numeric thresholds after corpus baseline, before declaring M3 complete.

### M4 — User-triggered Agent + cards

Tasks:
- Named-pipe protocol contracts, heartbeat/restart/cancellation, provider abstraction, settings/secret storage.
- Context builder retrieves related session text under explicit budget, notes gaps, filters outbound sensitive patterns best-effort, offers payload preview.
- `comment.v1`, `reply.v1`, `summary.v1`, `analysis.v1` structured outputs; schema validation, graceful fallback, one-click copy.
- Quick actions and free-form prompt; local heuristics for action suggestions.

Gate:
- Offline fake-provider integration tests cover valid/invalid/timeout/cancel scenarios.
- Real cloud API test is optional, opt-in and documented; never required for CI.
- No automatic posting, typing or clicking.
- Relevant earlier session text influences response even when no longer on-screen.
- Raw screen images are never sent to provider by default.

### M5 — Hardening + portable distribution

Tasks:
- Self-contained `win-x64` portable folder, Worker included; no mandatory separately installed .NET runtime.
- Data in LocalAppData by default; backups/migration/delete/export as appropriate.
- Session management, pause and privacy controls, crash logs without sensitive payloads, bounded worker restart.
- Eight-hour soak, multi-monitor/DPI and CPU-only acceptance, first-run guidance, user docs.

Gate:
- Fresh Windows machine can unpack and launch without separate .NET runtime.
- Last committed session survives closing lens/app and worker restart.
- UI remains responsive if OCR/Provider stalls.
- Performance results include hardware, region resolution and limitations; no unsupported universal performance claims.

## Cross-cutting test matrix

| ID | Scenario | Expected |
|---|---|---|
| SM-01 | Scroll 20-frame article | Main text reconstructed in reading order |
| SM-02 | 50% overlap | Shared blocks deduped |
| SM-03 | Same frame 10 times | No 10 new effective snapshot slots |
| SM-04 | >9 effective frames | Exactly up to last 9 detailed snapshots retained |
| SM-05 | Old snapshot evicted | Session document and durable provenance survive |
| SM-06 | Stop 1.0s | OCR begins after stability debounce |
| SM-07 | OCR/DB processing | No premature green status |
| SM-08 | Fast jump | Appropriate **possible gap** warning |
| SM-09 | Close/reopen | Session content, title, revisions and gaps restored |
| SM-10 | Worker crash | No committed-data loss; UI responsive |
| SM-11 | Two identical short comments | No unjustified destructive merge |
| SM-12 | Ask Agent to comment | Structured copyable card from multi-screen session |
| UI-01 | Mixed DPI/multiple monitors | Lens geometry and capture alignment match |
| UI-02 | Overlay excluded | No self-recursive OCR under supported capture path |
| PRIV-01 | Default run | No raw screenshots persisted |
| PRIV-02 | API call | Only minimal, filtered, inspectable context transmitted |

## Suggested PR sequence for Codex

1. `chore/bootstrap-solution` — M0.
2. `feat/lens-capture-spike` — M1.
3. `feat/ocr-state-machine` — M2.
4. `feat/session-storage` — M3 schema, persistence, migration tests.
5. `feat/scroll-reconciliation` — M3 corpus, dedupe, gaps, inspector.
6. `feat/agent-context-cards` — M4.
7. `release/v0.1-portable` — M5.

Do not merge an implementation PR solely because it compiles: run the corresponding gate. When Windows hardware is unavailable, mark real-device gate **unverified** and leave it for a Windows Codex environment rather than claiming success.

## Definition of done

V0.1 is done when a user can select a region, scroll through at least 20 stable positions, inspect a coherent and provenance-aware deduped session, see possible misses, close and resume the session, ask for a comment/summary and copy a validated result, without UI self-capture or application control. All must work in a portable Windows build with documented test/benchmark evidence.
