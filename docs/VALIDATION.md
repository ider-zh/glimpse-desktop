# Session Reader validation — 2026-11-16

This is a measured development checkpoint, **not completed V0.1 acceptance**. No provider/API credentials were used. All screen fixtures were generated synthetic text; no user browsing content or screen images are committed.

## Environment and reproducibility

Windows 11 build 26100, x64, Intel Core i7-5820K @ 3.30 GHz (12 logical processors), .NET SDK 10.0.404. Two 1920×1080 displays, virtual desktop `(-1920,0,3840,1080)`, both tested at 96 DPI. Installed OCR languages: en-US, zh-Hans-CN. No GPU/NPU dependency is used by the implementation.

```powershell
./scripts/verify.ps1
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --negative
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --reader
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --fixture
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --ui-render artifacts/inspector.png
./scripts/publish.ps1
```

Close other test fixtures before running an interactive probe. These native probes deliberately open topmost generated test windows. Do not run concurrently with publishing: builds share NuGet/MSBuild intermediate state. `--ui-render` writes only the app's WPF visual tree, not a screen capture. For OCR fixture files, `Glimpse.Worker --ocr image.png [language]` prints explicit diagnostic JSON.

## Observed results

| Check | Observed result | Evidence / limits |
|---|---|---|
| Release solution build | 0 warnings / 0 errors | Windows source build |
| Core/storage tests | 23 passed | debounce reset, stale generations, pause, fine text changes, scrolling/order, conservative duplicate handling (including shared-header/different-neighbor replies), typo evidence, digits/negation, 9-snapshot pruning, restart/backup/cascade deletion, canceled writes, IPC DTO geometry |
| Labeled synthetic corpus | 9 scenarios pass; block precision/recall/order = 1; duplicate/erroneous merge counts = 0 | [raw metrics](evidence/synthetic-v1.json). Thresholds apply only to this corpus; they are not real OCR accuracy claims. |
| Own-window exclusion | zero pixel mismatch with a magenta excluded window plus lens | 650×420 physical pixels; [negative-monitor native output](evidence/windows-negative-96dpi.json) |
| Click-through and alignment | underlying fixture receives hit test; actual button click increments counter; wheel scroll changes fixture | Native WindowFromPoint plus computer-use checks; physical crop exact at current 96 DPI |
| Native OCR | English 221 ms; Chinese 362 ms in recorded negative-monitor run | Includes engine initialization; no universal latency claim. English heading exact; Chinese sentence CER 3/17 = 17.65% after removing inter-character whitespace. English-only recognition omits the Chinese sentence. |
| Full reader | 20 stable positions committed, revision 20, 9 detailed snapshots, 71 accumulated OCR blocks, restore verified | [raw reader result](evidence/reader-20-positions.json). This proves pipeline growth/recovery, **not that all 71 blocks are unique/correct**; OCR line clipping/variation is conservatively retained. |
| IPC | current-user pipe, heartbeat, request-id deduplication, forced Worker kill and manual reconnect/resume pass | `--ipc <worker-exe>` |
| Cancellation | delayed fake OCR ignores cancellation; stale result still cannot commit or emit green after session switch | `--worker-tests`, real SQLite plus fake capture/OCR |
| UI supervision | actual UI client reconnects after its Worker is killed, loads committed session paused | `--worker-tests`; bounded automatic retry policy |
| UI | controls fit the default inspector; synthetic click/scroll/new-session/start/close flow checked | WPF visual-tree render inspected. External screenshots intentionally exclude the app's windows. |
| Distribution | self-contained win-x64 folder and ZIP generated; packaged UI/Worker connect with `DOTNET_ROOT` pointing to a deliberately absent folder and multilevel lookup disabled | UI and Worker are independently published; see publish script. Both runtimeconfig files declare included frameworks. A genuinely fresh machine remains untested. |

The recorded end-to-end run used a 720×480 region for approximately 28 seconds and reported P95 OCR + commit 66 ms and 142 MiB working set for the **single in-process probe**, including its WPF fixture. This excludes stability wait and does not represent the two-process production working set or 1080p performance. See raw output for exact duration. One native run and a short fixture are not an eight-hour soak or a statistically representative benchmark.

## Milestone gates

| Milestone | Implementation | Gate still open |
|---|---|---|
| M0 | Solution, SDK policy, analyzers/formatting, tests, CI and ADRs implemented; local build/tests pass | Remote CI result is separate from local results |
| M1 | Selection, hollow move/resize lens, capture, exclusion, pause and inspector implemented | Actual 125%/150% mixed-DPI movement/resizing and display-disconnect matrix |
| M2 | Local OCR, language selection, adaptive detector, 1000 ms debounce, cancellation/generations and stage timings implemented | Labeled small-font/blank/mixed-language image accuracy and wider CPU-only hardware sample; Chinese quality remains limited |
| M3 | Atomic SQLite, 9-snapshot ring, durable provenance, sequence alignment, uncertainty/gap hypotheses, inspect/export/delete/resume implemented | Consented/sanitized real scroll corpus with fixed acceptance thresholds; contextual page-switch ambiguity and long-session profiling |
| M4 | **Not started** | Repository instruction requires Session Reader acceptance first. Provider adapters, secret storage, outbound context preview/filtering and validated result cards are absent. |
| M5 | Portable publishing and basic recovery/privacy controls implemented | Fresh-machine launch, eight-hour soak, full memory/idle CPU/P95 1080p measurements, licensing/governance decision |

## Remaining risks and next work

The first remote Windows CI failure occurred after all functional checks passed: final removal of the supervisor probe's SQLite file encountered a sharing violation. Worker shutdown now closes its pipe, waits up to three seconds for a normal exit, and only then kills a stalled process. The supervisor probe additionally requires exit code 0 and durable `closed` session state. Test-only cleanup retries transient OS file locks for at most five seconds and still fails persistent locks; its path guard restricts recursive removal to newly-created `glimpse-*` temp directories.

- The 256×160 detector now sees small text changes missed by the first sparse prototype. It ignores a handful of changed sample pixels, but animated text/spinners may still postpone stability indefinitely. No claim of perfect animation filtering.
- Global fallback alignment permits returning to evicted text. Ambiguous repeats and unanchored page switches retain evidence with uncertainty. Foreground window identity is only a heuristic; different pages within one application can still resemble a scroll gap.
- Saving upserts/rebalances the full accumulated document. Document fallback DP alignment and complete inspector IPC payloads can grow with a very long session. Eight-hour acceptance is open until these costs are profiled and bounded.
- Chinese OCR errors and changing line wraps can produce extra retained blocks. Do not silently remove those readings just to improve a duplicate metric. Original/fuzzy variant evidence remains available after snapshot eviction.
- No API integration or license was invented to bypass the repository's decisions and acceptance order. Continue with the open M1–M3 gates before M4.

During development, probes caught the first-frame uncertainty bug and the original sparse detector's insensitivity to small changes; both have regression tests. The first 20-position fixture was too short and reached the bottom early; it was extended to 90 paragraphs. A negative-monitor probe initially allowed another window to obscure its test fixture; it now keeps the fixture topmost. An overlapping build/publish restore caused NETSDK1047 during development; sequential publish succeeded afterward. These failed attempts are not counted as passing evidence.
