using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Glimpse.Contracts;
using Glimpse.Core;

namespace Glimpse.Worker;

public sealed class ReaderService(ISessionRepository repository, Func<ReaderUpdate, Task> publish,
    string? initialLanguage = null, IScreenCapture? screenCapture = null, IOcrEngine? ocrEngine = null,
    int initialScale = 0, Func<string?>? sourceContextProvider = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim mutex = new(1);
    private readonly StabilityGate gate = new();
    private readonly IFrameChangeDetector detector = new FrameChangeDetector();
    private readonly MaskMotionGuard maskMotion = new();
    private readonly CaptureSourceContext sourceContext = CaptureSourceContext.ForCurrentWorker();
    private string? lastCaptureContext;
    private readonly IScreenCapture capture = screenCapture ?? new WindowsCapture();
    private readonly ISequenceAligner aligner = new SessionReconciler();
    private IOcrEngine? ocr = ocrEngine;
    private string? language = initialLanguage;
    private int scale = initialScale;
    private SessionData? current;
    private CancellationTokenSource? processing;
    private Task? job;
    private string? lastHash;
    private bool paused = true;
    private int motionSamples;
    private string? error;
    private string? captureNotice;
    private bool analyzingDynamicRegions;
    private long lastSentGeneration = -1;
    private string lastSentState = "";

    public async Task<SessionData?> GetCurrentSessionAsync(CancellationToken cancellation = default)
    {
        await mutex.WaitAsync(cancellation);
        try { return current; }
        finally { mutex.Release(); }
    }

    public async Task SetScaleAsync(int value, CancellationToken cancellation)
    {
        if (value is < 0 or > 2) throw new InvalidOperationException("Unknown OCR enhancement mode.");
        await mutex.WaitAsync(cancellation);
        try
        {
            processing?.Cancel(); scale = value; ocr = null;
            gate.Pause(); if (!paused) gate.Resume();
            detector.Reset(); lastHash = null; error = null;
            await Emit(true);
        }
        finally { mutex.Release(); }
    }

    public async Task SetLanguageAsync(string value, CancellationToken cancellation)
    {
        if (!WindowsOcr.Languages.Contains(value)) throw new InvalidOperationException("该 OCR 语言未安装。");
        await mutex.WaitAsync(cancellation);
        try
        {
            processing?.Cancel();
            // Wait for the previous native OCR call before replacing its engine.
            language = value;
            ocr = null;
            gate.Pause(); if (!paused) gate.Resume();
            detector.Reset(); lastHash = null; error = null;
            await Emit(true);
        }
        finally { mutex.Release(); }
    }

    public async Task SetSessionAsync(SessionData session, bool start, CancellationToken cancellation)
    {
        await mutex.WaitAsync(cancellation);
        try
        {
            processing?.Cancel();
            gate.Pause();
            if (current is not null && current.Id != session.Id)
                await repository.SaveAsync(current with { Status = "closed" }, cancellation);
            captureNotice = session.IgnoreRegions is { Count: > 0 } ? "重新打开记录后已清除旧忽略区域，请按当前画面重新框选。" : null;
            current = session with { Status = start ? "active" : "paused", IgnoreRegions = [] };
            sourceContext.Reset(session.Snapshots.LastOrDefault()?.Observation.ContextId);
            lastCaptureContext = session.Snapshots.LastOrDefault()?.Observation.ContextId;
            maskMotion.Reset();
            await repository.SaveAsync(current, cancellation);
            paused = !start;
            if (start) gate.Resume();
            detector.Reset(); lastHash = null; motionSamples = 0; error = null;
            await Emit(true);
        }
        finally { mutex.Release(); }
    }
    public async Task PauseAsync(bool value, CancellationToken cancellation)
    {
        await mutex.WaitAsync(cancellation);
        try
        {
            processing?.Cancel();
            paused = value;
            if (value) gate.Pause(); else gate.Resume();
            detector.Reset(); lastHash = null; error = null;
            if (current is not null)
            {
                current = current with { Status = value ? "paused" : "active" };
                await repository.SaveAsync(current, cancellation);
            }
            await Emit(true);
        }
        finally { mutex.Release(); }
    }
    public async Task SetRectAsync(PixelRect rect, CancellationToken cancellation)
    {
        if (!rect.IsValid) throw new InvalidOperationException("Invalid lens dimensions (32–7680 × 32–4320 physical pixels).");
        await mutex.WaitAsync(cancellation);
        try
        {
            if (current?.Rect == rect) return;
            processing?.Cancel();
            gate.Pause();
            if (!paused) gate.Resume();
            detector.Reset(); lastHash = null; motionSamples = 0;
            if (current is not null)
            {
                current = current with { Rect = rect, IgnoreRegions = [] };
                maskMotion.Reset(); captureNotice = null;
                await repository.SaveAsync(current, cancellation);
            }
            await Emit(true);
        }
        finally { mutex.Release(); }
    }
    public async Task AnalyzeDynamicRegionsAsync(CancellationToken cancellation)
    {
        Guid sessionId; PixelRect rect; bool wasPaused; long generation;
        await mutex.WaitAsync(cancellation);
        try
        {
            if (analyzingDynamicRegions) throw new InvalidOperationException("正在分析动态区域，请稍候。");
            if (current is null) throw new InvalidOperationException("请先选择观察区域。");
            analyzingDynamicRegions = true;
            sessionId = current.Id; rect = current.Rect; wasPaused = paused;
            paused = true; processing?.Cancel(); gate.Pause(); generation = gate.Generation;
            error = null; captureNotice = "正在本地分析动态区域，请保持页面不滚动…";
            current = current with { Status = "paused" };
            await Emit(true);
        }
        finally { mutex.Release(); }
        DynamicRegionAnalysis? analysis = null;
        try
        {
            var localDetector = new DynamicRegionDetector();
            for (int i = 0; i < 13; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                await mutex.WaitAsync(cancellation);
                try
                {
                    if (current?.Id != sessionId || current.Rect != rect || gate.Generation != generation)
                        throw new InvalidOperationException("观察区域或状态已变化，本次动态分析已取消。");
                }
                finally { mutex.Release(); }
                // Only one full frame is held at a time; the detector retains a small luminance grid.
                localDetector.Observe(capture.Capture(rect), rect.Width, rect.Height);
                // Uneven sampling avoids repeatedly hitting the same phase of a looping animation.
                if (i < 12) await Task.Delay(new[] { 101, 173, 127, 191 }[i % 4], cancellation);
            }
            analysis = localDetector.Analyze(rect.Width, rect.Height);
        }
        finally
        {
            await mutex.WaitAsync(CancellationToken.None);
            try
            {
                analyzingDynamicRegions = false;
                // A close/pause/move/session switch owns its newer state. Never resume it.
                if (current?.Id == sessionId && current.Rect == rect && gate.Generation == generation)
                {
                    if (analysis is { Regions.Count: > 0 })
                        current = new PostSegmenter().ExcludeVisibleRegions(current with { IgnoreRegions = analysis.Regions }, analysis.Regions);
                    captureNotice = analysis?.Notice ?? "动态区域分析已结束，请重试或手动框选视频。";
                    paused = wasPaused; gate.Pause(); if (!paused) gate.Resume();
                    detector.Reset(); maskMotion.Reset(); lastHash = null; motionSamples = 0;
                    current = current with { Status = paused ? "paused" : "active", UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                    await repository.SaveAsync(current, CancellationToken.None);
                    await Emit(true);
                }
            }
            finally { mutex.Release(); }
        }
    }

    public async Task SetIgnoreRegionsAsync(List<PixelRect> regions, CancellationToken cancellation)
    {
        await mutex.WaitAsync(cancellation);
        try
        {
            if (current is null) throw new InvalidOperationException("请先选择观察区域。");
            var validated = FrameMasker.Validate(regions, current.Rect.Width, current.Rect.Height);
            processing?.Cancel(); gate.Pause(); if (!paused) gate.Resume();
            detector.Reset(); maskMotion.Reset(); lastHash = null; motionSamples = 0; error = null;
            captureNotice = validated.Count > 0 ? $"已忽略 {validated.Count} 个区域；视频外的文字将继续识别。" : null;
            current = current with { IgnoreRegions = validated, UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            current = new PostSegmenter().ExcludeVisibleRegions(current, validated);
            await repository.SaveAsync(current, cancellation);
            await Emit(true);
        }
        finally { mutex.Release(); }
    }
    public async Task DetachAsync(Guid id, CancellationToken cancellation)
    {
        await mutex.WaitAsync(cancellation);
        try
        {
            if (current?.Id != id) return;
            paused = true; processing?.Cancel(); gate.Pause(); current = null;
            await Emit(true);
        }
        finally { mutex.Release(); }
    }
    public async Task RunAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            await mutex.WaitAsync(cancellation);
            try
            {
                if (!paused && current is not null)
                {
                    // Sample before the stability wait: opening our Agent sidebar later
                    // must keep the source seen while the external app was foreground.
                    string? observedContext = sourceContextProvider is null
                        ? sourceContext.Resolve(GetForegroundWindow()) : sourceContextProvider();
                    if (observedContext != lastCaptureContext)
                    {
                        if (lastCaptureContext is not null && observedContext is not null && current.IgnoreRegions is { Count: > 0 })
                        {
                            current = current with { IgnoreRegions = [] };
                            maskMotion.Reset();
                            captureNotice = "来源窗口已变化，已清除旧忽略区域，请按当前画面重新确认。";
                            await repository.SaveAsync(current, cancellation);
                            await Emit(true);
                        }
                        // A new source can contain identical pixels. Retire in-flight
                        // OCR and invalidate both detector reference and hash shortcut.
                        processing?.Cancel(); gate.Pause(); gate.Resume();
                        detector.Reset(); lastHash = null; motionSamples = 0;
                        lastCaptureContext = observedContext;
                    }
                    ocr ??= new WindowsOcr(language, scale);
                    var pixels = capture.Capture(current.Rect);
                    var ignored = current.IgnoreRegions?.ToArray() ?? [];
                    if (ignored.Length > 0 && maskMotion.HasGlobalMovement(pixels, current.Rect.Width, current.Rect.Height, ignored))
                    {
                        processing?.Cancel(); gate.Pause(); gate.Resume(); detector.Reset(); maskMotion.Reset(); lastHash = null;
                        current = current with { IgnoreRegions = [] };
                        ignored = [];
                        captureNotice = "检测到页面大幅变化，已清除固定位置的忽略区域，请重新框选视频。";
                        await repository.SaveAsync(current, cancellation);
                        await Emit(true);
                    }
                    // The same masked pixels feed change detection, hashing and native OCR.
                    // Blanking preserves lens-relative text coordinates.
                    FrameMasker.Apply(pixels, current.Rect.Width, current.Rect.Height, ignored);
                    bool changed = detector.HasChanged(pixels, current.Rect.Width, current.Rect.Height);
                    if (changed) { processing?.Cancel(); motionSamples++; }
                    bool ready = gate.Observe(changed, Environment.TickCount64);
                    if (ready)
                    {
                        string hash = Convert.ToHexString(SHA256.HashData(pixels));
                        if (hash == lastHash) gate.Commit(gate.Generation);
                        else
                        {
                            // WinRT OCR calls are serialized. A canceled result may still be
                            // completing natively; never invoke the engine concurrently.
                            var previous = job;
                            processing?.Dispose();
                            processing = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                            var token = processing.Token;
                            long generation = gate.Generation;
                            var sessionId = current.Id;
                            var rect = current.Rect;
                            var engine = ocr;
                            bool motion = motionSamples >= 3;
                            string? context = observedContext;
                            job = Task.Run(async () =>
                            {
                                if (previous is not null) { try { await previous; } catch (OperationCanceledException) { } }
                                await ProcessAsync(sessionId, generation, rect, pixels, hash, motion, context, ignored, engine, token);
                            }, CancellationToken.None);
                        }
                        motionSamples = 0;
                    }
                    await Emit(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                paused = true; processing?.Cancel(); gate.Pause();
                error = SafeError(ex);
                await Emit(true);
            }
            finally { mutex.Release(); }
            await Task.Delay(paused ? 300 : gate.State == "Recorded" ? 350 : 160, cancellation);
        }
    }
    private async Task ProcessAsync(Guid sessionId, long generation, PixelRect rect, byte[] pixels,
        string hash, bool motion, string? context, IReadOnlyList<PixelRect> ignored, IOcrEngine engine, CancellationToken cancellation)
    {
        try
        {
            var timer = Stopwatch.StartNew();
            var blocks = await engine.RecognizeAsync(pixels, rect.Width, rect.Height, cancellation);
            blocks = blocks.Where(b => !ignored.Any(r => FrameMasker.Intersects(b.Bounds, r))).ToList();
            int ocrMs = (int)timer.ElapsedMilliseconds;
            await mutex.WaitAsync(cancellation);
            try
            {
                if (paused || current?.Id != sessionId || gate.Generation != generation) return;
                var observation = new Observation(hash, engine.Name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ocrMs, blocks, motion, context);
                var result = aligner.Apply(current, observation);
                timer.Restart();
                await repository.SaveAsync(result.Session, cancellation);
                current = result.Session;
                lastHash = hash; error = null;
                gate.Commit(generation);
                await Emit(true, ocrMs, (int)timer.ElapsedMilliseconds);
            }
            finally { mutex.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await mutex.WaitAsync(CancellationToken.None);
            try
            {
                if (current?.Id == sessionId && gate.Generation == generation)
                {
                    paused = true; gate.Pause(); error = SafeError(ex);
                    await Emit(true);
                }
            }
            finally { mutex.Release(); }
        }
    }
    private async Task Emit(bool includeSession, int ocrMs = 0, int commitMs = 0)
    {
        string state = error is null ? gate.State : "Error";
        if (!includeSession && state == lastSentState && gate.Generation == lastSentGeneration) return;
        lastSentState = state; lastSentGeneration = gate.Generation;
        await publish(new(state, gate.Generation, includeSession ? current : null, error, ocrMs, commitMs, ocr?.Name, captureNotice));
    }
    internal static string SafeError(Exception ex) => ex switch
    {
        InvalidOperationException => ex.Message,
        _ => $"{ex.GetType().Name} (0x{ex.HResult:X8}). Observation stopped; retry or restart the worker."
    };
    public async ValueTask DisposeAsync()
    {
        processing?.Cancel();
        if (job is not null) await job;
        if (current is not null) await repository.SaveAsync(current with { Status = "closed" });
        processing?.Dispose();
        mutex.Dispose();
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
