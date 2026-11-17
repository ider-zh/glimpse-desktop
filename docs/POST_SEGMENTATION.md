# Local Observer and isolated post contexts

The Observer is deterministic local code in Glimpse.Core/PostSegmenter.cs. It does not call a model while observing. Provider requests remain user-triggered.

## Evidence and identity

SessionReconciler records the latest viewport-relative physical-pixel bounds, sequence, foreground context identifier, inferred post identifier and role in each block's Provenance.Geometry. This optional JSON field is backward compatible with older provenance and persists through the existing provenance_json column. Evicting the ninth detailed snapshot does not remove these source anchors or the accumulated text.

Alignment candidates must have the same foreground context identifier. Identical text in two different source windows receives independent block and post identities; a window change is not evidence of a scroll gap. Unknown and known source contexts are not silently equated.

WorkerClient supplies its UI process ID to the supervised Worker. CaptureSourceContext ignores foreground HWNDs owned by that UI process and retains the last external source, so opening Glimpse controls does not fragment the observed post. A real source-context change invalidates the detector reference, hash shortcut and pending OCR generation even when the pixels are identical. The source is sampled before the stability wait and restored from the last saved observation when reopening a session.

PostSegmenter.Annotate returns a new session with inferred annotations; callers persist it atomically with reconciliation. Build derives a catalog without mutating its input. Catalog evidence contains block IDs, coordinate bounds, sequence and role. A post's ID derives from its first durable source block; reused blocks anchor identity across overlapping scroll observations.

## Conservative layout rules

- A row containing at least two distinct, recognized action labels (for example Like / Comment / Share) is a control strip and a post boundary. An isolated word such as Reply is not globally deleted.
- Multiple distinct navigation labels form an excluded navigation column only when located at the viewport edge.
- Author headers, recognized author/time headers, substantial vertical gaps and existing post identities establish vertical boundaries.
- Co-visible substantive text separated by a horizontal gutter is partitioned into independent columns. Text spanning that gutter is left unassigned instead of being added to both posts.
- Indented author lines are treated as comments within the parent group. Unindented author lines conservatively begin separate posts.
- A fragment without enough evidence for a group is unassigned. Conflicting known identities are never merged; new ambiguous fragments between them remain unassigned.
- Applying an ignored region also marks intersecting evidence from the latest visible snapshot as masked. It remains in raw records but is excluded from all post scopes. Older snapshots cannot reclassify it; clearing a mask alone does not restore stale text, while a genuine subsequent OCR observation can restore it. Historical screen positions are not applied to the new mask when no visible snapshot exists.
- Historical text without surviving geometry remains accessible as one explicitly uncertain legacy group, rather than being discarded or presented as a confidently segmented feed.

## Generation boundary

Scope(session, postId) validates the requested post and returns only its allowed blocks and corresponding filtered snapshot OCR. It retains the original session ID and revision so a frozen inference can be tied to the captured evidence. Other posts, recognized controls and unassigned fragments are excluded from that task.

The application dispatcher builds each child request from a separate scoped session. Multi-post execution must not send the parent envelope or other children's context to a provider. Model output and observed text are untrusted; this feature does not grant tools, browser control, posting or automatic sending.

## Limitations and validation

OCR text and geometry do not reveal an application's true DOM or semantic post tree. Unindented comments, quote posts, complex masonry layouts and a title separated from its body by a large media area can be split conservatively. Body-only groups are flagged uncertain. Interface controls may remain in legacy text or unrecognized layouts. Users should inspect and select the intended post; the catalog is not a guarantee that every post boundary is correct.

Focused deterministic tests cover disjoint two-post evidence, control strips, preserving ordinary uses of control words, scroll overlap identities, durable geometry after twelve observations with nine snapshots, SQLite reload, legacy text, unassigned fragments, edge navigation, two-column feeds with spanning headers, separate source windows and indented comments. These tests establish the local data boundary; Windows visual and live provider acceptance are separate checks.
