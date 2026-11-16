using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Glimpse.Contracts;
using Glimpse.Core;

namespace Glimpse.Worker;

public sealed class ReaderService(ISessionRepository repository, Func<ReaderUpdate, Task> publish,
    string? initialLanguage = null, IScreenCapture? screenCapture = null, IOcrEngine? ocrEngine = null,
    int initialScale = 0) : IAsyncDisposable
{
    private readonly SemaphoreSlim mutex = new(1);
    private readonly StabilityGate gate = new();
    private readonly IFrameChangeDetector detector = new FrameChangeDetector();
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
            current = session with { Status = start ? "active" : "paused" };
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
            processing?.Cancel();
            gate.Pause();
            if (!paused) gate.Resume();
            detector.Reset(); lastHash = null; motionSamples = 0;
            if (current is not null)
            {
                current = current with { Rect = rect };
                await repository.SaveAsync(current, cancellation);
            }
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
                    ocr ??= new WindowsOcr(language, scale);
                    var pixels = capture.Capture(current.Rect);
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
                            string context = GetForegroundWindow().ToString();
                            job = Task.Run(async () =>
                            {
                                if (previous is not null) { try { await previous; } catch (OperationCanceledException) { } }
                                await ProcessAsync(sessionId, generation, rect, pixels, hash, motion, context, engine, token);
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
        string hash, bool motion, string context, IOcrEngine engine, CancellationToken cancellation)
    {
        try
        {
            var timer = Stopwatch.StartNew();
            var blocks = await engine.RecognizeAsync(pixels, rect.Width, rect.Height, cancellation);
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
        await publish(new(state, gate.Generation, includeSession ? current : null, error, ocrMs, commitMs, ocr?.Name));
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
