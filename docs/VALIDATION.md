# Reader and Agent validation — 2026-11-16

This is a measured development checkpoint, **not completed V0.1 acceptance**. No real provider/API credentials or external model calls were used. All screen fixtures were generated synthetic text; no user browsing content or screen images are committed. The owner explicitly requested Agent now and subagent implementation, superseding the original wait-for-Reader-acceptance ordering; hardware gates below remain open.

## Environment and reproducibility

Windows 11 build 26100, x64, Intel Core i7-5820K @ 3.30 GHz (12 logical processors), .NET SDK 10.0.404. Two 1920×1080 displays, virtual desktop `(-1920,0,3840,1080)`, both tested at 96 DPI. Installed OCR languages: en-US, zh-Hans-CN. No GPU/NPU dependency is used by the implementation.

```powershell
./scripts/verify.ps1
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --negative
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --reader
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --fixture
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --ui-render artifacts/inspector.png
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --agent-ui artifacts/agent-cards.png
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --ocr-corpus
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --provider-tests
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --agent-tests
./scripts/publish.ps1
```

Close other test fixtures before running an interactive probe. These native probes deliberately open topmost generated test windows. Do not run concurrently with publishing: builds share NuGet/MSBuild intermediate state. `--ui-render` writes only the app's WPF visual tree, not a screen capture. For OCR fixture files, `Glimpse.Worker --ocr image.png [language]` prints explicit diagnostic JSON.

## Observed results

| Check | Observed result | Evidence / limits |
|---|---|---|
| Release solution build | 0 warnings / 0 errors | Windows source build |
| Core/storage/Agent tests | 51 passed | Reader tests plus 6 image preparation checks, 21 Agent context/card/fake-provider tests, schema-v1 migration/Agent history/cascade-deletion test |
| Labeled synthetic corpus | 9 scenarios pass; block precision/recall/order = 1; duplicate/erroneous merge counts = 0 | [raw metrics](evidence/synthetic-v1.json). Thresholds apply only to this corpus; they are not real OCR accuracy claims. |
| Own-window exclusion | zero pixel mismatch with a magenta excluded window plus lens | 650×420 physical pixels; [negative-monitor native output](evidence/windows-negative-96dpi.json) |
| Click-through and alignment | underlying fixture receives hit test; actual button click increments counter; wheel scroll changes fixture | Native WindowFromPoint plus computer-use checks; physical crop exact at current 96 DPI |
| Native OCR | English 221 ms; Chinese 362 ms in recorded negative-monitor run | Includes engine initialization; no universal latency claim. English heading exact; Chinese sentence CER 3/17 = 17.65% after removing inter-character whitespace. English-only recognition omits the Chinese sentence. |
| Rendered OCR corpus | Adaptive CER 49/696 = 7.04%; native 161/696 = 23.13%; blank empty, original-coordinate boxes valid | 12 labeled two-line cases, Chinese/mixed/English, fonts 10/12/16/24, same device. [Raw results](evidence/rendered-ocr-v1.json); not universal or real-page accuracy. |
| Full reader | 20 stable positions committed, revision 20, 9 detailed snapshots, 71 accumulated OCR blocks, restore verified | [raw reader result](evidence/reader-20-positions.json). This proves pipeline growth/recovery, **not that all 71 blocks are unique/correct**; OCR line clipping/variation is conservatively retained. |
| IPC | current-user pipe, heartbeat, request-id deduplication, forced Worker kill and manual reconnect/resume pass | `--ipc <worker-exe>` |
| Cancellation | delayed fake OCR ignores cancellation; stale result still cannot commit or emit green after session switch | `--worker-tests`, real SQLite plus fake capture/OCR |
| UI supervision | actual UI client reconnects after its Worker is killed, loads committed session paused | `--worker-tests`; bounded automatic retry policy |
| Normal shutdown | Worker exits with code 0 and durable closed-session state; five consecutive local recovery/shutdown cycles pass | The initial Windows CI cleanup failure is fixed; [Windows + Linux CI run](https://github.com/ider-zh/glimpse-desktop/actions/runs/43842513842) passed on the shutdown-fix commit. New feature commits are checked independently. |
| Provider adapters | Offline HTTP header/path/body/timeout/cancel/size tests, actual DPAPI round trip, synthetic child-process stdin/kill/output/exit tests pass | No network to DeepSeek/MiMo and no real authenticated CodeBuddy generation. CLI test checks empty tools/MCP and disabled hooks/settings. |
| Agent controller | Frozen redacted preview, no inference during preview/configuration, strict schemas/fallback, old session/engine/replayed preview rejection, targeted cancellation and durable results pass | Pure fake providers plus real SQLite and settings; delayed fake ignores cancellation deliberately. |
| Agent transport | Actual UI client → Worker → loopback Chat Completions → local card validation → SQLite history passes | Heartbeat and pause complete while response is deliberately withheld; canceled request returns canceled. Mock server receives exactly reviewed messages and no images/tools. |
| Agent WPF UI | Real preview/generate controls produce cards; preview email redacted; consumed preview disables generation; session switch clears old cards | `--agent-ui`, own WPF visual-tree render with synthetic data only. |
| UI | controls fit the default inspector; synthetic click/scroll/new-session/start/close flow checked | WPF visual-tree render inspected. External screenshots intentionally exclude the app's windows. |
| Distribution | self-contained win-x64 folder and ZIP generated; packaged UI/Worker connect with `DOTNET_ROOT` pointing to a deliberately absent folder and multilevel lookup disabled | UI and Worker are independently published; see publish script. Both runtimeconfig files declare included frameworks. A genuinely fresh machine remains untested. |

The recorded end-to-end run used a 720×480 region for approximately 28 seconds and reported P95 OCR + commit 66 ms and 142 MiB working set for the **single in-process probe**, including its WPF fixture. This excludes stability wait and does not represent the two-process production working set or 1080p performance. See raw output for exact duration. One native run and a short fixture are not an eight-hour soak or a statistically representative benchmark.

## Milestone gates

| Milestone | Implementation | Gate still open |
|---|---|---|
| M0 | Solution, SDK policy, formatting, CI and ADRs implemented; local and shutdown-fix remote Windows/Linux checks pass | Each new commit still requires its own CI result |
| M1 | Selection, hollow move/resize lens, capture, exclusion, pause and inspector implemented | Actual 125%/150% mixed-DPI movement/resizing and display-disconnect matrix |
| M2 | Local OCR, language/mode selection, bounded small-text enhancement, 1000 ms debounce and generations; rendered small-font/blank/mixed corpus passes | Real-page accuracy and wider CPU-only hardware sample; 10-pixel Chinese remains difficult |
| M3 | Atomic SQLite, 9-snapshot ring, durable provenance, sequence alignment, uncertainty/gap hypotheses, inspect/export/delete/resume implemented | Consented/sanitized real scroll corpus with fixed acceptance thresholds; contextual page-switch ambiguity and long-session profiling |
| M4 | User-authorized implementation: switchable DeepSeek/MiMo/custom API/CodeBuddy engines, DPAPI configuration, preview/filtering, four card schemas, cancel/timeout and history | Actual external provider and authenticated CLI acceptance remain optional and unverified; user supplies credentials/login and explicitly triggers generation |
| M5 | Portable publishing and basic recovery/privacy controls implemented | Fresh-machine launch, eight-hour soak, full memory/idle CPU/P95 1080p measurements, licensing/governance decision |

## Remaining risks and next work

The first remote Windows CI failure occurred after all functional checks passed: final removal of the supervisor probe's SQLite file encountered a sharing violation. Worker shutdown now closes its pipe, waits up to three seconds for a normal exit, and only then kills a stalled process. The supervisor probe additionally requires exit code 0 and durable `closed` session state. Test-only cleanup retries transient OS file locks for at most five seconds and still fails persistent locks; its path guard restricts recursive removal to newly-created `glimpse-*` temp directories.

- The 256×160 detector now sees small text changes missed by the first sparse prototype. It ignores a handful of changed sample pixels, but animated text/spinners may still postpone stability indefinitely. No claim of perfect animation filtering.
- Global fallback alignment permits returning to evicted text. Ambiguous repeats and unanchored page switches retain evidence with uncertainty. Foreground window identity is only a heuristic; different pages within one application can still resemble a scroll gap.
- Saving upserts/rebalances the full accumulated document. Document fallback DP alignment and complete inspector IPC payloads can grow with a very long session. Eight-hour acceptance is open until these costs are profiled and bounded.
- Chinese OCR errors and changing line wraps can produce extra retained blocks. Do not silently remove those readings just to improve a duplicate metric. Original/fuzzy variant evidence remains available after snapshot eviction.
- Agent was implemented under the owner's explicit request, with offline acceptance evidence and no real credential use. Remaining Reader hardware/quality gates are still open. Repository licensing is still undecided.

During development, probes caught the first-frame uncertainty bug and the original sparse detector's insensitivity to small changes; both have regression tests. The first 20-position fixture was too short and reached the bottom early; it was extended to 90 paragraphs. A negative-monitor probe initially allowed another window to obscure its test fixture; it now keeps the fixture topmost. An overlapping build/publish restore caused NETSDK1047 during development; sequential publish succeeded afterward. These failed attempts are not counted as passing evidence.
