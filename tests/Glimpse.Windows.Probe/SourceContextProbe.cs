using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Glimpse.Worker;
using Glimpse.Contracts;
using Glimpse.Core;

namespace Glimpse.Windows.Probe;

internal static class SourceContextProbe
{
    public static async Task Run()
    {
        await RunReaderIdentity();
        Process? first = null, second = null;
        var own = new Window { Title = "Glimpse own-control context fixture", Width = 300, Height = 120,
            Content = new TextBlock { Text = "Synthetic Glimpse toolbar", Margin = new Thickness(20) } };
        try
        {
            first = StartFixture();
            nint external = await WindowOf(first);
            var tracker = new CaptureSourceContext(Environment.ProcessId);
            string? initial = tracker.Resolve(external);
            Check(initial == external.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture), "external source HWND recorded");
            // MainWindowHandle appears before the child finishes its initial Show
            // activation. Let that activation settle before testing our own window.
            await Task.Delay(500);
            own.Show(); own.Activate();
            await Task.Delay(150);
            nint ownHandle = new WindowInteropHelper(own).Handle;
            // Windows may deny programmatic foreground activation. Verify native
            // HWND ownership even in that case, and never claim actual focus passed.
            bool actualFocus = GetForegroundWindow() == ownHandle;
            Check(tracker.Resolve(actualFocus ? GetForegroundWindow() : ownHandle) == initial, "own-control HWND retains source identity");
            Console.WriteLine($"Native context classification: actualOwnWindowFocusAvailable={actualFocus}");
            Check(tracker.Resolve(0) == initial, "temporarily unknown foreground retains known source");
            second = StartFixture();
            nint anotherExternal = await WindowOf(second);
            Check(tracker.Resolve(anotherExternal) != initial, "different external source gets separate context");
            own.Activate(); await Task.Delay(100);
            Check(tracker.Resolve(ownHandle) == anotherExternal.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                "own controls retain latest external context");
            tracker.Reset(initial);
            Check(tracker.Resolve(ownHandle) == initial, "reopened session seeds previous source context");
            Console.WriteLine("PASS: native external/own HWND classification, source switch and reopen context");
        }
        finally
        {
            own.Close();
            await CloseFixture(second);
            await CloseFixture(first);
        }
    }

    public static async Task RunReaderIdentity()
    {
        string context = "synthetic-source-one";
        var repository = new MemoryRepository();
        await using var reader = new ReaderService(repository, _ => Task.CompletedTask,
            screenCapture: new StillCapture(), ocrEngine: new FixedOcr(), sourceContextProvider: () => context);
        using var stop = new CancellationTokenSource();
        await reader.SetSessionAsync(new(Guid.NewGuid(), "Synthetic source identity", "paused", 1, 1,
            new(0, 0, 128, 128), 0, [], [], []), true, stop.Token);
        var loop = reader.RunAsync(stop.Token);
        try
        {
            await Revision(reader, 1);
            var first = (await reader.GetCurrentSessionAsync())!;
            Guid original = new PostSegmenter().Build(first).Posts[0].Id;
            await reader.SetIgnoreRegionsAsync([new(0, 64, 64, 64)], stop.Token);
            Check((await reader.GetCurrentSessionAsync())!.IgnoreRegions?.Count == 1, "source-one mask applied");
            context = "synthetic-source-two";
            await Revision(reader, 2);
            var second = (await reader.GetCurrentSessionAsync())!;
            Check(second.Blocks.Count == 2, "same pixels in new source must create new evidence");
            Check(second.IgnoreRegions?.Count == 0, "a new external source must discard old fixed-position masks");
            var posts = new PostSegmenter().Build(second).Posts;
            Check(posts.Count == 2 && posts.Single(p => p.IsVisible).Id != original, "new source has independent post context");
            Console.WriteLine("PASS: Reader records identical pixels after source switch, invalidates old hash and clears the old source mask.");
        }
        finally { stop.Cancel(); try { await loop; } catch (OperationCanceledException) { } }
    }
    private static async Task Revision(ReaderService reader, int revision)
    {
        for (int i = 0; i < 100; i++)
        {
            if ((await reader.GetCurrentSessionAsync())!.Revision >= revision) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Source switch did not produce a stable observation.");
    }
    private sealed class StillCapture : IScreenCapture
    {
        public byte[] Capture(PixelRect rect) => new byte[rect.Width * rect.Height * 4];
    }
    private sealed class FixedOcr : IOcrEngine
    {
        public string Name => "Synthetic same pixels";
        public Task<List<OcrBlock>> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellation) =>
            Task.FromResult<List<OcrBlock>>([new("Identical visible content in different synthetic windows.", new(0, 0, 128, 22))]);
    }
    private sealed class MemoryRepository : ISessionRepository
    {
        public Task<List<SessionSummary>> ListAsync(CancellationToken cancellation = default) => Task.FromResult(new List<SessionSummary>());
        public Task<SessionData?> LoadAsync(Guid id, CancellationToken cancellation = default) => Task.FromResult<SessionData?>(null);
        public Task SaveAsync(SessionData session, CancellationToken cancellation = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken cancellation = default) => Task.CompletedTask;
    }

    private static Process StartFixture()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--fixture");
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start synthetic source window.");
    }
    private static async Task<nint> WindowOf(Process child)
    {
        for (int i = 0; i < 100; i++)
        {
            child.Refresh();
            if (child.MainWindowHandle != 0) return child.MainWindowHandle;
            if (child.HasExited) throw new InvalidOperationException("Source fixture exited before creating a window.");
            await Task.Delay(50);
        }
        throw new TimeoutException("Source fixture window did not appear.");
    }
    private static async Task CloseFixture(Process? child)
    {
        if (child is null) return;
        try
        {
            if (!child.HasExited)
            {
                child.CloseMainWindow();
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { child.Kill(true); await child.WaitForExitAsync(); }
            }
        }
        finally { child.Dispose(); }
    }
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
