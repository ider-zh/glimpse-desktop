# ADR 0010 — Local Observer and isolated post tasks

Accepted 2026-11-17. The owner requested content partitioning, independent subagents and an observation toolbar separate from the Agent sidebar.

## Evidence and isolation

The local Observer groups OCR using coordinates, gutters, author boundaries, action strips and prior matched block identity. It does not call a model during observation. Conservative interface classification uses layout and multiple control labels rather than deleting every occurrence of words such as “回复”. Conflicting identities and unresolved spanning fragments stay unassigned. Indented comment authors remain with a parent when supported by layout. Source-window changes cannot reuse an identical-text anchor from another source.

`BlockGeometry` is an additive field in existing provenance JSON: sequence, lens-relative physical-pixel bounds, source context, post ID and role survive the nine-snapshot ring. Older records without coordinates remain available as one explicitly uncertain group. This does not reconstruct unseen text or guarantee thread relationships.

The sidebar defaults to a visible post near the viewport centre and preserves explicit user selection. The user may choose a single post or all 2–4 recognised posts. One click freezes exact filtered messages. Each child receives only its post's allowed block IDs and scoped OCR evidence, user instruction, uncertainty and gaps. The parent is a local dispatcher; its envelope is never sent to a model. Cross-post synthesis is not implicit. The inspector requires a specific sidebar selection when multiple posts exist.

The configured screen-context budget is divided across children rather than multiplied. At most two fresh provider instances/CLI processes run concurrently, with no conversation shared between posts. A single configured deadline covers running and queued children. Parent cancellation/invalidation stops active children and prevents queued provider creation. Results retain source labels, revision and nested per-post status; partial answers remain copyable and are clearly labelled. Frozen previews do not expand while OCR continues.

Explicit mask edits invalidate prepared previews and active/queued model work. Dynamic-region analysis blocks new previews/runs until it finishes. Previously visible OCR evidence inside a new exclusion is marked `masked` in provenance and omitted from post contexts without deleting raw text. Re-observation after clearing a mask can restore it. Historical text that merely occupied the same screen coordinates on another frame is not silently discarded.

The current CLI engine is still tool-disabled and sandboxed to an ephemeral directory; this feature does not enable CodeBuddy's autonomous tools or allow screen text to issue commands. API keys remain DPAPI protected, images remain transient, and no inference starts without a user action.

## Interaction

An excluded-from-capture icon toolbar manages observation, region selection, dynamic-region masks, records and closing the lens. The separate excluded sidebar manages post selection, instructions, cancellation and result copying. Icons have tooltips and accessible names; capture state also has readable text. Both surfaces follow the physical-pixel lens and are clamped to the monitor work area. Closing stops observation while retaining records.

## Limits

OCR-only grouping is a heuristic, not DOM extraction. Dense multi-column layouts, unindented comment authors and incomplete posts can remain ambiguous. Uncertainty is visible; users should verify selected content through the payload inspector. No browsing, posting, user-level long-term memory or background model inference is introduced.
