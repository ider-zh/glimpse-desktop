# Glimpse V0.1 — Architecture and Session Memory

The current local Observer/post evidence extension and its conservative layout limitations are documented in [POST_SEGMENTATION.md](POST_SEGMENTATION.md). Post contexts are isolated by durable block IDs; local segmentation does not initiate model calls.

## 1. Components

- `Glimpse.UI` (.NET 10 WPF): physical-pixel lens geometry adapter, transparent topmost border, click-through hit-test, side toolbar, session inspector and result cards.
- `Glimpse.Worker` (.NET 10 executable): supervised background worker for screen capture, adaptive change detection, OCR, reconciliation, persistence, context building and Agent provider calls.
- `Glimpse.Contracts`: versioned IPC DTOs and result-card DTOs.
- `Glimpse.Core`: pure testable models, session state machine, alignment and dedupe, privacy/context budgeting.
- `Glimpse.Storage`: SQLite schema/migrations/repositories; single-writer ownership by Worker.
- `Glimpse.Tests`: unit, synthetic scroll fixtures, integration and end-to-end harness.

UI↔Worker communication: named pipes, JSON envelopes with `protocol_version`, `request_id`, `session_id`, `capture_generation`, `timestamp`, `message_type`, `payload`. Support cancellation, heartbeat, reconnect, idempotency and timeouts. Avoid public TCP ports in V0.1.

Use explicit interfaces `IScreenCapture`, `IFrameChangeDetector`, `IOcrEngine`, `ISequenceAligner`, `ISessionRepository`, `IContextBuilder`, `ILLMProvider`. Fake implementations are mandatory for deterministic tests.

## 2. Capture and coordinate invariants

- Persist and process all capture geometry in **screen physical pixels**; WPF DIPs converted at UI boundary.
- Test multi-monitor virtual-desktop negative coordinates and per-monitor scaling changes.
- Capture selected monitor/window as supported by API, then crop user rectangle, or evaluate an appropriate region API; never assume a selection rectangle is directly a capture item.
- Exclude own top-level overlay windows using supported Windows APIs where possible; verify under chosen capture backend. Fail visibly when excluded capture cannot be guaranteed.
- No screenshot is persisted by default. GPU resources/bitmaps are disposed promptly.
- Avoid OCR when scene does not materially change; allow a manual refresh.
- Defer OCR until frame has been stable for 1000ms; debounce visual noise. Processing remains cancellable.

## 3. Data entities and schema

SQLite migrations are versioned. Use UTC Unix milliseconds or ISO-8601 consistently, not mixed representations. Enable foreign keys and plan WAL/backup strategy.

```sql
CREATE TABLE sessions (
  id TEXT PRIMARY KEY,
  title TEXT NOT NULL,
  status TEXT NOT NULL CHECK(status IN ('active','paused','closed')),
  created_at_ms INTEGER NOT NULL,
  updated_at_ms INTEGER NOT NULL,
  capture_rect_json TEXT NOT NULL,
  snapshot_limit INTEGER NOT NULL DEFAULT 9,
  document_revision INTEGER NOT NULL DEFAULT 0,
  schema_version INTEGER NOT NULL
);
CREATE TABLE ocr_snapshots (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  sequence_no INTEGER NOT NULL,
  captured_at_ms INTEGER NOT NULL,
  frame_hash TEXT NOT NULL,
  ocr_engine TEXT NOT NULL,
  processing_ms INTEGER NOT NULL,
  raw_blocks_json TEXT NOT NULL,
  new_block_count INTEGER NOT NULL,
  match_quality REAL,
  UNIQUE(session_id, sequence_no)
);
CREATE TABLE content_blocks (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  content TEXT NOT NULL,
  normalized_text TEXT NOT NULL,
  content_hash TEXT NOT NULL,
  first_seen_at_ms INTEGER NOT NULL,
  last_seen_at_ms INTEGER NOT NULL,
  first_sequence INTEGER NOT NULL,
  order_key TEXT NOT NULL,
  confidence REAL,
  content_type TEXT NOT NULL DEFAULT 'unknown',
  status TEXT NOT NULL DEFAULT 'confirmed',
  provenance_json TEXT NOT NULL DEFAULT '{}'
);
CREATE TABLE snapshot_blocks (
  snapshot_id TEXT NOT NULL REFERENCES ocr_snapshots(id) ON DELETE CASCADE,
  block_id TEXT NOT NULL REFERENCES content_blocks(id) ON DELETE CASCADE,
  bbox_json TEXT NOT NULL,
  match_score REAL,
  ocr_text TEXT NOT NULL,
  relation_type TEXT NOT NULL,
  PRIMARY KEY(snapshot_id, block_id)
);
CREATE TABLE capture_gaps (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  from_sequence INTEGER NOT NULL,
  to_sequence INTEGER NOT NULL,
  detected_at_ms INTEGER NOT NULL,
  reason TEXT NOT NULL,
  severity TEXT NOT NULL,
  resolved INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE agent_turns (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  created_at_ms INTEGER NOT NULL,
  task_type TEXT NOT NULL,
  document_revision INTEGER NOT NULL,
  provider_id TEXT,
  model_id TEXT,
  output_schema TEXT,
  result_json TEXT,
  status TEXT NOT NULL
);
CREATE INDEX idx_blocks_session_order ON content_blocks(session_id, order_key);
CREATE INDEX idx_snapshots_session_seq ON ocr_snapshots(session_id, sequence_no DESC);
CREATE INDEX idx_gaps_session ON capture_gaps(session_id, resolved);
```

**Snapshot eviction/provenance invariant:** a content block must remain meaningful after its source snapshot is evicted. Keep first-seen timestamp, sequence, original OCR text, and durable provenance summary on content block. Do not leave dangling links. The `snapshot_blocks` table is a temporary recent-observation association, not the only source of provenance.

Stable `order_key` must permit insertion between existing blocks (fractional rank or sortable key); rebalance atomically. `content_hash` must never be a global uniqueness constraint: two people can post identical comments.

## 4. Rolling OCR alignment

Pipeline:

1. Cheap downscaled frame change detection; ignore transient cursor/animation noise where possible.
2. After 1000ms stable, capture current full selected rectangle, assign `capture_generation`.
3. Detect duplicate frame via visual hash; skip OCR if effectively unchanged. Compare effective text to avoid consuming snapshot slots on duplicate OCR.
4. Run `IOcrEngine.RecognizeAsync`; preserve text, bounding rectangles, line order, nullable confidence.
5. Normalize Unicode/whitespace for matching **without removing negation, numerals or punctuation semantics**; retain original text.
6. Compare new block sequence to recent snapshot sequences and session blocks with:
   - exact text match,
   - weighted edit similarity,
   - neighborhood/sequence alignment,
   - scroll displacement/relative geometry,
   - cautious block splitting/merging.
7. Link exact/fuzzy matches, correct OCR variants only with stronger evidence and preserve previous observations; insert novel blocks in inferred order. Ambiguous duplicates or ordering remain marked uncertain.
8. If overlap is weak and visual/scroll evidence suggests a discontinuity, record `capture_gap`; first check whether app/window/content context switched. Gap is a **hypothesis**, not guaranteed loss.
9. In a single SQLite transaction: commit blocks/links/gaps/snapshot, increment document revision, prune detailed snapshots to most recent 9. Only after successful commit emit `Recorded`.
10. Emit metrics: effective frames, new/merged/uncertain blocks, gap count, OCR latency and total end-to-end latency.

```text
prev: A B C D E
curr:     C D E F G
overlap:  C D E
new:            F G
```

Do **not** implement simple set-based dedupe. Multiple identical “支持” comments may be distinct. When uncertain, prefer conservative duplicate preservation with a flag to an irreversible merge.

## 5. Capture state machine

```text
Idle -> Changing -> Stabilizing --(stable 1000ms)--> Processing -> Recorded -> Idle
                      | changed                        | failure
                      +------> Changing                  +------> Error
Paused <---- pause from any active state; resume -> Idle
```

- `Idle` gray, `Changing` gray, `Stabilizing` amber, `Processing` blue, `Recorded` green, `Error` red, `Paused` gray.
- Capture and session lifecycle are separate; gaps are a separate badge/warning.
- Use monotonically increasing capture generations: stale OCR jobs cannot publish state over newer jobs.
- Green means successful processing and **durable SQLite commit**, not complete/accurate reading.
- Worker crash/restart: preserve committed content; UI shows disconnected state and retries with bounded backoff. In-flight data may be lost but must never masquerade as committed.
- Pause stops capturing and cancels pending work; the database can still be inspected.

## 6. Agent Context

Do not send the raw 9-snapshot ring or entire session by default. `IContextBuilder` reads deduped blocks and provenance, retrieves task-relevant snippets, applies token budget and best-effort sensitive-text filter, and attaches gap/uncertainty warnings. No user-long-term profile.

Provider interfaces support configured cloud-compatible APIs and local providers later. Keep credentials in Windows protected storage, never plaintext SQLite/logs. All observed screen text is **untrusted data**, never privileged Agent instructions.

Validated versioned result schemas `comment.v1`, `reply.v1`, `summary.v1`, `analysis.v1`; on invalid provider output use a visible plain-text fallback. Copy is user-initiated, no app automation.

## 7. Performance targets (hypotheses to benchmark)

For an ordinary CPU-only Windows PC and one 1080p region: P95 stable-change-to-context-update ≤1.5s (recognition + commit, **excluding the 1.0s stability wait**; measure wait separately); idle average CPU <1%; process working set target ≤350MB excluding optional local LLM; continuous 8-hour run with no hangs or unbounded memory growth. Measure rather than assert. If impossible on baseline hardware, document revised targets, reasons and tradeoffs.

Record change-detector cost, OCR latency, dedupe latency, transaction latency, memory and duplicate/recall metrics. Explicitly define hardware, OCR language, frame dimensions and data fixtures for every benchmark.
