# ADR 0002: SQLite schema v1 and durable evidence

Date: 2026-11-16. Status: accepted for implementation.

Use Microsoft.Data.Sqlite, foreign keys and WAL. `PRAGMA user_version=1` is the migration version; refuse newer unknown versions. Creation of a new database is the first migration. All timestamps are UTC Unix milliseconds.

Keep sessions, content blocks, recent snapshots, snapshot/block relations and gap hypotheses in separate tables. Embedded JSON stores immutable observation DTOs and durable provenance. The initial implementation uses a smaller schema than the proposed architecture: snapshot details are JSON, and unimplemented Agent tables are deliberately absent. The public DTOs are versioned with IPC v1.

Each reconciliation is one transaction. Rebalance sortable fixed-width ordinal keys inside that transaction. First OCR reading, first sequence, first timestamp, observation count, most recent reading and fuzzy variants survive detailed-snapshot pruning. Snapshot relations cascade on pruning; content blocks never cascade from snapshots. Hashes are lookup metadata, never text uniqueness constraints.

Keep only the last nine effective observations. A sequence of identical normalized OCR blocks, including visual-only noise, does not advance the revision or consume another slot. Blank OCR does not erase content. Fuzzy variants are conservative (one character in long text, matching digits and Chinese negation count, plus exact sequence anchors); preserve both readings and flag uncertainty. Unanchored new text is appended with unknown ordering. Gap hypotheses require same foreground context, a prior overlapping scroll, and ongoing motion; content switches alone are not proof of a reading gap.

Data defaults to `%LOCALAPPDATA%\Glimpse`. `GLIMPSE_DATA_DIR` supports isolated tests or user-selected portable data location. Repository backup uses SQLite's backup API. Export contains accumulated text and source summaries. Session deletion cascades, checkpoints WAL and vacuums the database; it is not a forensic secure-erasure claim.

Tradeoff: saving currently rebalances/upserts all durable blocks. This favors transaction correctness and inspectability; long-session write cost must be measured before eight-hour acceptance. Document fallback alignment and full inspector payloads also need large-session profiling.
