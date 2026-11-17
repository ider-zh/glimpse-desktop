# Glimpse V0.1 — Product Requirements

Date: 2026-11-16. Status: approved design baseline for Codex, not implemented.

## 1. Product intent

A lightweight Windows desktop observer: user draws one fixed-position lens over a post, comment thread, chat, web page or document; while reading and scrolling, local OCR records **stable visible text**, merges overlap, and constructs a resumable session. On demand, an Agent uses the relevant session context to produce structured copyable suggestions.

The product is broadly usable across applications but **V0.1 is a screen-text assistant**, not general visual understanding. OCR cannot guarantee correct interpretation of pictures, complex tables, nested comment trees, identity or full page content.

## 2. Confirmed scope

| Area | V0.1 |
|---|---|
| Platform | Windows desktop; .NET 10 WPF |
| Lens | One rectangle in screen physical coordinates; transparent click-through interior |
| Controls | Interactable border and docked side toolbar; move/resize/pause/close |
| Answer UI | External panel or in-lens transparent overlay, switchable; exclude own windows from capture |
| Observation | Adaptive frame change detection; OCR on frame stable for 1.0s |
| Fast scrolling | Capture stable frames only; warn when a likely gap exists |
| Snapshots | 9 most recent **effective** OCR observations; exact duplicates do not use slots |
| Session | Deduplicated ordered text blocks persisted in local SQLite, reopen/continue |
| Agent | Multiple provider adapters; user-triggered tasks, contextual quick actions and free-form prompt |
| Outputs | Task-driven validated cards: comment/reply variations, summary, analysis; copy buttons |
| Automation | No application control, automatic posting or message sending |
| Long-term user memory | Explicitly excluded |
| Distribution | Self-contained portable folder; user data under LocalAppData by default |

## 3. Lens interaction

- User selects rectangle similar to screenshot selection and may move/resize it later.
- Inner transparent rectangle lets mouse clicks, selection and scrolling reach the underlying app.
- Border and toolbar receive input; reply panel receives input only over visible controls.
- Toolbar indicators: delta-only ON/OFF, capture status, effective snapshot count **n/9**, accumulated unique block/character count, possible-gap warning, pause, actions.
- A user must be able to pause observation immediately and see whether capture is active.
- Exclude Glimpse UI/overlay from its own capture and verify with actual capture backend. Failure must not silently permit feedback loops; surface a diagnostic warning and disable in-lens overlay if necessary.
- Lens is **fixed screen coordinate**, not attached to an app. Window-switch detection may suggest a new session; user can explicitly start/resume sessions.

## 4. Observation states

- Idle: gray; no significant change.
- Changing: gray; significant visual change continues.
- Stabilizing: amber; 1.0s since last significant change, timer resets on further change.
- Processing: blue; OCR, reconciliation and database transaction in progress.
- Recorded: green; **latest stable observation fully processed and committed**; zero new blocks is valid.
- Error: red; OCR, IPC, capture or storage failure.
- Paused: gray; capture stopped.

Gap warnings are orthogonal to states. Green never means OCR was perfect or all scroll content was seen. Stale async results must be tied to capture generation and must not overwrite a newer state.

## 5. Session semantics

- A Session has a UUID, title, timestamps, lens coordinates, state and document revision.
- Closing the lens **closes but does not delete** the session. User can resume it, restoring text and optionally the saved rectangle.
- Session text is not limited to 9 frames; 9 is only the retained detailed OCR-snapshot window.
- Only stable, meaningfully changed captures become effective snapshots. Identical/no-content-delta observations should not consume slots.
- Store raw OCR text/block positions/confidence (nullable), not raw images by default.
- Retain distinct identical short comments when their spatial/sequence context suggests different items.
- Sequence order is an inference: mark unknown/ambiguous rather than inventing post/reply relationships.
- A gap flags **possible unread content**, not proof of omission; record why and allow subsequent resolution.
- Do not automatically upload session history. User action triggers a provider call with task-relevant context.

## 6. Actions and cards

Quick actions: “帮我评论”, “帮我回复”, “总结”, “给我建议”. A free-form prompt is also supported. Context-sensitive toolbar recommendations should initially use local heuristics, not background LLM calls.

Owner update (2026-11-17): the attached sidebar exposes these as direct one-click commands with inline answers, cancellation and copying, plus a custom question send action and explicit settings shortcut. Each click builds filtered context and triggers one request, without opening the inspector or requiring another preview confirmation. Optional last-payload inspection remains inline; the inspector retains its existing preview-first flow. Preserve previous answers during retries/cancel/failure and keep progress/cancel visible while reading results.

Suggested card schema families: `comment.v1` (options with tone/text), `reply.v1`, `summary.v1` (key points), `analysis.v1` (findings/caveats). Validate server/model output against a local schema; on failure show a plain-text fallback and an error indicator. Copy actions must copy exactly the visible proposed text. Never auto-submit it.

## 7. Privacy and safety

- User explicitly chooses capture region and can pause/stop instantly.
- Raw images are transient memory-only by default; opt-in, clearly labeled diagnostics can save them.
- Local SQLite may contain sensitive OCR text. Provide session list, inspect and delete.
- Sensitive-data filtering of outbound text is best-effort and cannot promise removal of all secrets. Preview last payload sent to provider, record which provider was used, and minimize context sent.
- Screen text is untrusted data, including prompt-injection strings or third-party claims about the user.
- No automatic extraction of user profile or durable cross-session user memories.
- Never persist API keys in plain SQLite or logs; use OS-backed protection. Never log raw request payloads by default.

## 8. Out of scope

Mobile/macOS; multiple concurrent lenses; accessibility-tree or semantic browser integration as required source; general image interpretation; automatic replies/posts/clicks; complete nested comment topology; user-level memory; cross-device sync; A2A/MCP server; silent always-on model inference; universal OCR accuracy.

## 9. Unresolved decisions

- Concrete default OCR backend and license/model distribution (must be benchmarked on CPU-only hardware).
- Minimum supported Windows build and exact capture backend for region capture.
- Portable data mode and app update mechanism.
- User-facing retention/deletion policy for saved sessions.
- Licensing of this repository.

Resolve each with an ADR before implementation commits that lock the choice.
