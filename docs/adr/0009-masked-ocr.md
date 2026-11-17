# ADR 0009 — Ignore dynamic regions before stability detection and OCR

Status: accepted, 2026-11-17. Owner requested video OCR unblocking and isolated per-post generation.

Whole-lens stability waiting prevents OCR when an embedded video continuously changes. A user can now mark up to eight lens-relative physical-pixel rectangles to ignore. The same white-masked frame feeds change detection, hashing and Windows OCR, preserving image dimensions and source text coordinates. OCR blocks intersecting an exclusion are discarded conservatively.

Session storage schema 3 adds `sessions.ignore_regions_json` with an empty default and migrates existing schema 1/2 databases without deleting text or results. Existing `provenance_json` accepts additive durable block geometry for the Observer. Images remain transient; masks contain geometry only.

Masks are fixed screen regions, not tracked video objects. They are cleared when the lens changes or a session is reopened, and after broad changes outside the mask suggest a scroll/page transition. The reader publishes a reset notice. Small controls or changes wholly inside the ignored region do not invalidate it. Subtitles inside ignored regions are excluded as well.

An explicit toolbar action can sample 13 local frames over approximately two seconds and find sustained motion tiles. Only a 128×80 luminance grid is retained between samples. Connected candidates receive a small margin; tiny/transient changes are rejected. Broad/global motion, candidates spanning most of the viewport, more than eight regions or total coverage above 65% are refused, with a manual-selection fallback. This is motion-based candidate detection, not video classification or object tracking. Visible masks can be cleared or manually replaced.

Analysis temporarily pauses normal observation, releases the service mutex between captures, and runs outside the Worker command loop. A capture-generation lease prevents late application or resuming observation after close/pause/move/session changes. Its original observation state is restored only when the lease still matches. No screenshots are saved, no model is called and no application is controlled.

Validation: deterministic masked animation/stability, unchanged coordinates, invalid inputs, broad/sparse-text motion invalidation, candidate/refusal tests and schema-2 upgrade tests; native animated WPF fixture through real WindowsCapture and Windows.Media.Ocr (`--dynamic-ocr`). A synthetic fixture does not establish correct motion classification for all sites or video layouts.
