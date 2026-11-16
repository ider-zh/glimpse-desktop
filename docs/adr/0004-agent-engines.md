# ADR 0004: User-triggered Agent engines and schema v2

Date: 2026-11-16. Status: implemented development preview.

The product owner explicitly requested Agent implementation, switchable OpenAI-compatible Chat Completions engines (DeepSeek and Xiaomi MiMo), CodeBuddy CLI, and subagent development. This authorizes M4 implementation alongside the remaining M1–M3 hardware gates. Those gates remain unverified where noted; implementing Agent does not mark them complete.

## Boundaries

- `Glimpse.Agent` is pure .NET: context selection, best-effort privacy filtering, strict versioned card validation, cancellation and timeout. `ILLMProvider` accepts trusted system text and a frozen, quoted untrusted screen-context payload, never images. Earlier relevant blocks can be selected beyond the nine-snapshot ring.
- `Glimpse.Providers` supplies OpenAI-compatible HTTP and CodeBuddy subprocess adapters. The Worker owns settings, keys and inference. UI only sees public profiles and a `HasApiKey` flag. New API keys are one-time, current-user pipe inputs stored via Windows DPAPI; empty input retains a key, explicit clearing removes it. No API keys are stored in SQLite, logs or result history.
- Profiles are DeepSeek, Xiaomi MiMo, custom OpenAI-compatible API and CodeBuddy CLI. Base URL, model, timeout and context budget are configurable. API endpoints accept root, `/v1`, or full `/chat/completions`; HTTPS is required except loopback HTTP used for local services/tests. HTTP redirects and implicit retries are disabled. MiMo uses its documented `api-key` and `max_completion_tokens`; other profiles use Bearer and `max_tokens`.
- CodeBuddy runs directly as an installed executable, or `node.exe` plus the detected npm JavaScript entry. No shell command construction. Prompts go through stdin; only static system instructions/schema are process arguments. It uses a fresh empty working directory, no tools, strict empty MCP configuration, hooks disabled, no session persistence, no project/user setting sources, and bounded output. This is not an OS sandbox. CLI authentication remains the user's normal CodeBuddy login; the application never reads CLI credentials.

## Explicit request flow

1. Save/select an engine. This creates no inference request.
2. Request a local context preview. It has a UUID, session/revision, selected source IDs, warnings and filtered exact message strings. Previewing needs no credentials and makes no provider call.
3. Explicitly generate using the one-shot preview UUID. Worker verifies session and engine profile, then sends precisely the reviewed strings. Continuing OCR never silently expands that payload. Changing session/settings invalidates previews; expired, reused or unknown previews are rejected.
4. Run model work outside the pipe command loop. Heartbeats, capture, pause and targeted cancellation remain responsive. Four task schemas are validated locally (`comment.v1`, `reply.v1`, `summary.v1`, `analysis.v1`); custom questions use analysis cards. Invalid JSON is visibly marked and offered as plain text. Copy copies exactly the visible proposed text; no posting, typing or clicking exists.
5. Persist result metadata/cards/fallback and status in SQLite `agent_turns`, linked to the original document revision. Schema version 2 adds this table and index without altering Reader evidence. Session deletion cascades; late results cannot recreate a deleted session. Prompts/outbound context are not persisted separately or logged.

Limits: context budget is 1024–24000 characters, not an exact tokenizer count; displayed token estimate is approximate. Calls are 5–180 seconds, output bounded by adapters and validator. Privacy filtering is best-effort and cannot detect every secret. Providers may continue remote computation after client cancellation. Live API calls and authenticated CLI generation are optional, user-triggered acceptance work, not CI requirements.

Official integration references checked during implementation: [DeepSeek Chat API](https://api-docs.deepseek.com/api/create-chat-completion/), [DeepSeek model/base URL reference](https://api-docs.deepseek.com/quick_start/pricing/), [MiMo first API call](https://mimo.mi.com/docs/en-US/quick-start/summary/first-api-call), [CodeBuddy headless mode](https://www.codebuddy.ai/docs/cli/headless), [CLI flags](https://www.codebuddy.ai/docs/cli/cli-reference), [hook settings](https://www.codebuddy.cn/docs/cli/settings).
