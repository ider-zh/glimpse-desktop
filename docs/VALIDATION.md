# Reader and Agent validation — 2026-11-17

## Observer / isolated-post delivery checkpoint

- Final Release solution build: zero warnings/errors. Core unit tests: **87/87 passed**. All nine synthetic alignment corpora pass unchanged; these are synthetic accuracy results, not a claim about arbitrary pages.
- Offline provider, DPAPI, terminal, controller, actual Worker IPC, cancel/restart/shutdown regressions pass. New four-post probe confirms disjoint frozen payloads, fresh providers, maximum concurrency two, shared evidence budget, nested SQLite history, cancellation of active/queued jobs and one shared deadline.
- Native split-toolbar probe passed on this Windows device: separate excluded windows, icon tooltips/accessible names, attachment, local startup observation, ignore add/clear, automatic-analysis busy state, readable status, close accessibility and state restoration. Mixed-DPI acceptance remains open.
- Authenticated installed CodeBuddy CLI with `mimo-v2.6-pro`: a selected synthetic post generated two valid cards; two isolated posts generated seven valid cards; every batch card copied exactly via the actual clipboard. A subsequent two-post request was canceled, both children persisted as canceled, previous cards stayed visible, and no main-window popup or late replacement occurred. Credentials were consumed only by CLI itself.
- Actual animated WPF → WindowsCapture → Windows.Media.Ocr manual-mask probe passed before desktop access became unavailable: unmasked animation blocked OCR; excluding the video recorded the outside text after approximately 1486 ms and omitted hidden video text.
- **Final desktop limitation:** Windows later returned native error 5 (access denied) for desktop capture and prevented forced foreground activation. The final automatic-region test therefore explicitly used generated synthetic animation frames plus the real Windows OCR engine, not desktop screenshots: manual/automatic masks, new outside text, broad-motion refusal, prior-mask preservation and late-pause invalidation passed. This does not establish final automatic-region end-to-end acceptance on a live unlocked desktop. The owner was asked to unlock the desktop for that remaining rerun.
- Source identity probe using injected capture/source IDs passes even with identical pixels: external source changes invalidate pending OCR/hash and clear old masks. Native HWND process classification is covered; actual own-window focus testing reports availability rather than claiming focus when Windows rejects it.
- Four extra mask-isolation cases cover retaining raw video text while excluding it from model context, no resurrection by old snapshots, historical same-position text preservation, and re-observation after clearing an entirely masked frame. Missing old layout is explicitly uncertain.

Reproduce with `./scripts/verify.ps1`; opt-in desktop probes include `--dynamic-ocr`, `--docked-toolbar <image>` and `--source-context`. The dynamic probe labels a synthetic-frame fallback when native desktop capture is unavailable. `--post-agent-ui <new-fixture-directory> <image>` uses an offline model; `--live-post-agent-ui <new-fixture-directory> <image>` explicitly invokes the logged-in CLI with synthetic content and must not run in CI.

This is a measured development checkpoint, **not completed V0.1 acceptance**. Automated regression uses offline providers. Initial CodeBuddy checks found HTTP 401 with exit code 0; after the owner completed login, real generation, cancellation, model discovery/selection and exact copying passed with synthetic context. Glimpse did not inspect credential files or automate login. All screen fixtures were generated synthetic text; no user browsing content or screen images are committed. The owner explicitly requested Agent now and subagent implementation, superseding the original wait-for-Reader-acceptance ordering; hardware gates below remain open.

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
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --login-terminal-tests
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --login-ui artifacts/codebuddy-login-ui.png
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --docked-toolbar artifacts/docked-toolbar.png
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --sidebar-agent artifacts/sidebar-agent.png
./scripts/publish.ps1
```

Close other test fixtures before running an interactive probe. These native probes deliberately open topmost generated test windows. Do not run concurrently with publishing: builds share NuGet/MSBuild intermediate state. `--ui-render` writes only the app's WPF visual tree, not a screen capture. For OCR fixture files, `Glimpse.Worker --ocr image.png [language]` prints explicit diagnostic JSON.

The installed CLI check is opt-in and excluded from CI. First complete `codebuddy` → `/login` yourself. Use a **new** fixture directory for each run; preparation refuses existing directories. Start the UI from the same PowerShell process to inherit its isolated data folder, open the synthetic session, preview, generate/cancel, and copy a successful card back into the local prompt to compare. Do not launch observation over personal content during this test.

```powershell
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --prepare-codebuddy-live artifacts/my-new-codebuddy-check
$env:GLIMPSE_DATA_DIR = "$PWD/artifacts/my-new-codebuddy-check"
dotnet run --project src/Glimpse.UI -c Release --no-build
# Optional real request: emits exit code, output lengths, JSON/auth booleans only.
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --diagnose-codebuddy-live artifacts/my-new-codebuddy-check
# Opt-in real model metadata and inference with the prepared synthetic fixture:
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --codebuddy-models artifacts/my-new-codebuddy-check
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --live-agent-ui artifacts/my-new-codebuddy-check artifacts/live-cards.png
dotnet run --project tests/Glimpse.Windows.Probe -c Release --no-build -- --live-sidebar-agent artifacts/my-new-codebuddy-check artifacts/live-sidebar.png
Remove-Item Env:GLIMPSE_DATA_DIR
```

`--prepare-agent-copy-fixture <new-directory>` instead creates an explicitly labeled offline stored card for independent clipboard testing. It performs no inference and must not be counted as authenticated generation. The app's regular data folder and running older preview were left untouched during the recorded checks.

## Observed results

| Check | Observed result | Evidence / limits |
|---|---|---|
| Release solution build | 0 warnings / 0 errors | Windows source build |
| Core/storage/Agent tests | 58 passed | Reader/context/card/history/error tests plus all five task answer examples validated against their actual schemas, and schema-declaration rejection regression |
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
| Provider adapters | Offline HTTP header/path/body/timeout/cancel/size tests, actual DPAPI round trip, synthetic child-process stdin/kill/output/exit tests pass | These regression probes make no supplier calls; separate live CLI evidence below. CLI test checks empty tools/MCP and disabled hooks/settings. |
| Agent controller | Frozen redacted preview, no inference during preview/configuration, strict schemas/fallback, old session/engine/replayed preview rejection, targeted cancellation and durable results pass | Pure fake providers plus real SQLite and settings; delayed fake ignores cancellation deliberately. |
| Agent transport | Actual UI client → Worker → loopback Chat Completions → local card validation → SQLite history passes | Heartbeat and pause complete while response is deliberately withheld; canceled request returns canceled. Mock server receives exactly reviewed messages and no images/tools. |
| Agent WPF UI | Real preview/generate controls produce cards; preview email redacted; consumed preview disables generation; session switch clears old cards | `--agent-ui`, own WPF visual-tree render with synthetic data only. |
| Installed CodeBuddy CLI 2.139.0 | Authenticated default-model WPF generation: 17.5 seconds, 6 validated summary cards; selected `mimo-v2.6-pro`: 14.6 seconds, 5 validated cards | Independent paused synthetic session, email/phone redacted. Real MiMo generation canceled at 3.5 seconds; no late cards, fresh preview required. Next attempt completed, every card's real Copy button matched clipboard text exactly; canceled/completed SQLite history verified. Initial 401/exit0 finding remains covered by error regression. |
| CLI model discovery | 16 models discovered from the logged-in CLI, editable dropdown selection saved ID correctly | ACP initialize → blank session/new metadata, never session/prompt, no filesystem/terminal tools offered. Automatic selected-engine/login-close refresh and manual refresh. Offline ACP tests also cover unwanted tool requests, projection/deduplication, authentication errors and cancellation. |
| Result clipboard | Copy button → paste into local prompt → select text equals fixture exactly, including Chinese and newline | Separately labeled `offline-copy-fixture` stored result, real Windows clipboard, maximized WPF window. This proves card copying/history only, **not successful CLI generation**. |
| App login terminal | Actual Windows ConPTY/Job child, TTY keyboard input, split/wrapped URL extraction, bounded memory, native exit and stop pass | `--login-terminal-tests` uses a synthetic executable only. First run caught inherited redirected standard handles overriding the terminal; explicit null startup handles fixed it. Input deadlines and shutdown are bounded; no real authentication was initiated. |
| Login transport/UI | Worker pipe preserves login IDs/raw VT/input/resize/links, rejects simultaneous generation/config changes, serves heartbeat and cleans up on disconnect; xterm/WebView2 native component tested on device | Local bundled xterm 6.0.0 + addon-fit 0.11.0, WebView2 1.0.4314.35. Input, ANSI menu rendering, resize, terminal selection/clipboard, safe links, blocked external navigation, stop and close-during-start tested with synthetic delegates; composite WPF/WebView2 capture inspected. No real login is automated. See ADR 0006. |
| UI | controls fit the default inspector; synthetic click/scroll/new-session/start/close flow checked | WPF visual-tree render inspected. External screenshots intentionally exclude the app's windows. |
| Attached observation toolbar | Physical-pixel right/left docking, native clickable/excluded window, negative-monitor following and all task/control events pass | Actual 96-DPI Windows placement and native hit testing; own WPF render inspected. Nearly full-screen placement clamps controls into the work area. Mixed-DPI/disconnect matrix remains open. |
| Selection-first workflow | Startup selector invoked once; completing selection starts actual synthetic OCR, record access preserves observation, stop/restart/close and canceled selection pass | `--docked-toolbar`: deterministic injected region selector, actual MainWindow/lens/Worker/SQLite and an isolated generated reading window. No user screen content or model calls. Closing frame retains records; reconnect/saved restore remain paused. |
| Inspector preview bridge | Reply/custom/summary fields reach locally reviewed context; blank custom prompts rejected; no inference during inspector preparation | `--agent-ui`: the legacy inspector retains its separate Generate action. The owner-authorized sidebar flow below now sends on a direct task click. |
| Inline sidebar UX | Four direct task buttons plus custom question send, inline validated cards/copy, explicit settings, task-specific feedback, sticky progress/cancel and retained previous answers pass | `--sidebar-agent`: real WPF handlers, local fake provider and exact clipboard checks. Busy double-click, empty input, error/timeout, cancel/late-result/session/close cases covered. Interaction-designer review of real success/error renders passed after two rounds. |
| Authenticated sidebar flow | One click produced 6 validated MiMo summary cards inline; all copied exactly; next real CLI call canceled while preserving labeled previous cards and SQLite history | `--live-sidebar-agent`, existing isolated synthetic session only. No main-window popup. Initial attempts exposed a transient clipboard lock and a model returning schema instead of content; bounded clipboard access retries and clearer answer-instance instructions fixed the observed path without relaxing schema validation. |
| Distribution | self-contained win-x64 folder and ZIP generated; packaged UI/Worker connect with `DOTNET_ROOT` pointing to a deliberately absent folder and multilevel lookup disabled | UI and Worker are independently published; see publish script. Both runtimeconfig files declare included frameworks. A genuinely fresh machine remains untested. |

The recorded end-to-end run used a 720×480 region for approximately 28 seconds and reported P95 OCR + commit 66 ms and 142 MiB working set for the **single in-process probe**, including its WPF fixture. This excludes stability wait and does not represent the two-process production working set or 1080p performance. See raw output for exact duration. One native run and a short fixture are not an eight-hour soak or a statistically representative benchmark.

## Milestone gates

| Milestone | Implementation | Gate still open |
|---|---|---|
| M0 | Solution, SDK policy, formatting, CI and ADRs implemented; local and shutdown-fix remote Windows/Linux checks pass | Each new commit still requires its own CI result |
| M1 | Selection, hollow move/resize lens, capture, exclusion, pause and inspector implemented | Actual 125%/150% mixed-DPI movement/resizing and display-disconnect matrix |
| M2 | Local OCR, language/mode selection, bounded small-text enhancement, 1000 ms debounce and generations; rendered small-font/blank/mixed corpus passes | Real-page accuracy and wider CPU-only hardware sample; 10-pixel Chinese remains difficult |
| M3 | Atomic SQLite, 9-snapshot ring, durable provenance, sequence alignment, uncertainty/gap hypotheses, inspect/export/delete/resume implemented | Consented/sanitized real scroll corpus with fixed acceptance thresholds; contextual page-switch ambiguity and long-session profiling |
| M4 | User-authorized implementation: switchable engines, DPAPI configuration, preview/filtering, cards, cancel/history; authenticated CLI workflow and account model discovery tested | Direct external API provider acceptance, enterprise-login variants and wider CLI-version matrix remain unverified. |
| M5 | Portable publishing and basic recovery/privacy controls implemented | Fresh-machine launch, eight-hour soak, full memory/idle CPU/P95 1080p measurements, licensing/governance decision |

## Remaining risks and next work

The first remote Windows CI failure occurred after all functional checks passed: final removal of the supervisor probe's SQLite file encountered a sharing violation. Worker shutdown now closes its pipe, waits up to three seconds for a normal exit, and only then kills a stalled process. The supervisor probe additionally requires exit code 0 and durable `closed` session state. Test-only cleanup retries transient OS file locks for at most five seconds and still fails persistent locks; its path guard restricts recursive removal to newly-created `glimpse-*` temp directories.

- The 256×160 detector now sees small text changes missed by the first sparse prototype. It ignores a handful of changed sample pixels, but animated text/spinners may still postpone stability indefinitely. No claim of perfect animation filtering.
- Global fallback alignment permits returning to evicted text. Ambiguous repeats and unanchored page switches retain evidence with uncertainty. Foreground window identity is only a heuristic; different pages within one application can still resemble a scroll gap.
- Saving upserts/rebalances the full accumulated document. Document fallback DP alignment and complete inspector IPC payloads can grow with a very long session. Eight-hour acceptance is open until these costs are profiled and bounded.
- Chinese OCR errors and changing line wraps can produce extra retained blocks. Do not silently remove those readings just to improve a duplicate metric. Original/fuzzy variant evidence remains available after snapshot eviction.
- Agent was implemented under the owner's explicit request, with offline acceptance evidence and the limited installed CLI results above. Remaining Reader hardware/quality gates are still open. Repository licensing is still undecided.

During development, probes caught the first-frame uncertainty bug and the original sparse detector's insensitivity to small changes; both have regression tests. The first 20-position fixture was too short and reached the bottom early; it was extended to 90 paragraphs. A negative-monitor probe initially allowed another window to obscure its test fixture; it now keeps the fixture topmost. An overlapping build/publish restore caused NETSDK1047 during development; sequential publish succeeded afterward. These failed attempts are not counted as passing evidence.
