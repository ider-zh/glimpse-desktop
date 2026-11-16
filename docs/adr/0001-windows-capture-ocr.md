# ADR 0001: Windows baseline, capture and local OCR

Date: 2026-11-16. Status: accepted for the Session Reader checkpoint; mixed-scale acceptance pending.

- Minimum API baseline: Windows 10 build 19041 (2004), x64. The application and Worker are PerMonitorV2 aware.
- Use GDI `BitBlt` with `CAPTUREBLT`, cropping an explicit virtual-desktop physical-pixel rectangle. Reject invalid/off-desktop rectangles. Images never leave process memory during normal observation.
- Exclude every Glimpse top-level window using `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` and check the returned affinity before allowing capture. The lens uses an actual hollow Win32 window region, so its interior does not participate in mouse hit testing across application processes. A separate inspector is the only answer/diagnostic panel in this checkpoint.
- Native spike compares a synthetic fixture before/after a magenta excluded overlay and the lens, checks actual `WindowFromPoint` hit testing and physical capture bounds. Failure is a release blocker. Runtime affinity failures prevent observation; they do not fall back to unexcluded capture.
- Use CPU-capable `Windows.Media.Ocr`, with nullable confidence because this engine does not supply calibrated confidence values. Language capabilities are supplied by Windows; select an installed language in the inspector. Do not download an OCR model or require a GPU/NPU. Missing languages produce an actionable error.
- Keep capture/OCR behind `IScreenCapture` and `IOcrEngine`; detector and sequence reconciliation are pure .NET. No dependency on LLM APIs.

Limitations: Windows display affinity is a capture hint, not DRM; driver/remote-desktop behavior needs testing. GDI does not promise GPU/protected-content capture. New rendering paths must repeat exclusion/alignment tests. Only the tested Windows device is evidence; see `docs/VALIDATION.md`.

References: [Microsoft display-affinity API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity), [Windows OCR](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine).
