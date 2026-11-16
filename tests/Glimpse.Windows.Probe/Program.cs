using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Glimpse.Contracts;
using Glimpse.UI;
using Glimpse.Worker;

namespace Glimpse.Windows.Probe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--agent-tests"))
        {
            AgentControllerProbes.Run().GetAwaiter().GetResult();
            AgentPipeProbe.Run().GetAwaiter().GetResult(); return 0;
        }
        if (args.Contains("--provider-child")) return ProviderProbes.RunChild(args);
        if (args.Contains("--provider-tests"))
        {
            ProviderProbes.Run().GetAwaiter().GetResult(); return 0;
        }
        if (args.Contains("--worker-tests"))
        {
            WorkerProbes.Run().GetAwaiter().GetResult(); return 0;
        }
        if (args.Length == 2 && args[0] == "--ipc")
        {
            PipeProbe.Run(args[1]).GetAwaiter().GetResult(); return 0;
        }
        if (args.Contains("--fixture"))
        {
            var fixtureApp = new Application();
            fixtureApp.Run(CreateFixture());
            return 0;
        }
        int exit = 1;
        Application app;
        if (args.Contains("--ui-render") || args.Contains("--agent-ui"))
        {
            var uiApp = new Glimpse.UI.App { LaunchMainWindow = false, ShutdownMode = ShutdownMode.OnExplicitShutdown };
            uiApp.InitializeComponent(); app = uiApp;
        }
        else app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                if (args.Length == 2 && args[0] == "--agent-ui") await AgentUiProbe.Run(args[1]);
                else if (args.Contains("--ocr-corpus")) await OcrCorpusProbe.Run();
                else if (args.Contains("--reader")) await RunReaderProbe();
                else if (args.Length == 2 && args[0] == "--ui-render") await RenderUi(args[1]);
                else await RunProbe(args.Contains("--negative"));
                exit = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"PROBE FAILED: {ex}"); }
            finally { app.Shutdown(); }
        };
        app.Run();
        return exit;
    }
    private static async Task RenderUi(string path)
    {
        var window = new MainWindow();
        window.Show();
        await Task.Delay(1500);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,
            (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var file = File.Create(path)) encoder.Save(file);
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.SetResult();
        window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine("UI render saved (own visual tree only; no screen capture).");
    }
    private static async Task RunReaderProbe()
    {
        string data = Path.Combine(Path.GetTempPath(), "glimpse-reader-probe-" + Guid.NewGuid());
        var fixture = CreateFixture();
        fixture.Topmost = true;
        fixture.Show(); fixture.Activate();
        await Task.Delay(500);
        var origin = fixture.PointToScreen(new(4, 4));
        var rect = new PixelRect((int)origin.X, (int)origin.Y, 720, 480);
        var repository = new Glimpse.Storage.SessionRepository(Path.Combine(data, "sessions.db"));
        var updates = System.Threading.Channels.Channel.CreateUnbounded<ReaderUpdate>();
        using var stop = new CancellationTokenSource();
        var service = new ReaderService(repository, update => { updates.Writer.TryWrite(update); return Task.CompletedTask; }, "en-US");
        var loop = service.RunAsync(stop.Token);
        var session = new SessionData(Guid.NewGuid(), "Synthetic real OCR reader probe", "paused", 1, 1, rect, 0, [], [], []);
        var watch = Stopwatch.StartNew();
        var latencies = new List<int>();
        try
        {
            await service.SetSessionAsync(session, true, stop.Token);
            var scroller = (ScrollViewer)fixture.Content;
            for (int i = 0; i < 20; i++)
            {
                scroller.ScrollToVerticalOffset(i * 3 * 51);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                ReaderUpdate update;
                do
                {
                    update = await updates.Reader.ReadAsync(timeout.Token);
                    if (update.Error is not null) throw new InvalidOperationException(update.Error);
                }
                while (update.State != "Recorded" || update.Session is null || update.Session.Revision <= session.Revision);
                session = update.Session;
                latencies.Add(update.OcrMs + update.CommitMs);
                Console.Error.WriteLine($"Stable position {i + 1}/20 committed, revision={session.Revision}, blocks={session.Blocks.Count}");
            }
            await service.PauseAsync(true, stop.Token);
            if (session.Snapshots.Count != 9 || session.Blocks.Count < 40)
                throw new InvalidOperationException("Real OCR reader ring/document growth failed.");
            var restored = await repository.LoadAsync(session.Id);
            if (restored!.Revision != session.Revision || restored.Blocks.Count != session.Blocks.Count)
                throw new InvalidOperationException("Real OCR reader restore failed.");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                passed = true,
                stablePositions = 20,
                session.Revision,
                detailedSnapshots = session.Snapshots.Count,
                accumulatedBlocks = session.Blocks.Count,
                elapsedMs = watch.ElapsedMilliseconds,
                p95OcrAndCommitMs = latencies.Order().ElementAt(18),
                restoreVerified = true,
                workingSetMb = Environment.WorkingSet / 1024 / 1024
            }, Envelope.Json));
        }
        finally
        {
            stop.Cancel();
            try { await loop; } catch (OperationCanceledException) { }
            await service.DisposeAsync(); fixture.Close();
            await ProbeCleanup.DeleteAsync(data);
        }
    }
    private static Window CreateFixture()
    {
        var panel = new StackPanel { Background = Brushes.White, Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Glimpse synthetic reading fixture", FontSize = 28, Foreground = Brushes.Black });
        panel.Children.Add(new TextBlock { Text = "中文阅读测试：保留每一次稳定观察。", FontSize = 24, Foreground = Brushes.Black, Margin = new Thickness(0, 15, 0, 15) });
        var count = new TextBlock { Text = "Clicks: 0", FontSize = 22, Foreground = Brushes.Black };
        var button = new Button { Content = "Click through the lens", Height = 42, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(20, 4, 20, 4) };
        int clicks = 0;
        button.Click += (_, _) => count.Text = $"Clicks: {++clicks}";
        panel.Children.Add(button); panel.Children.Add(count);
        for (int i = 0; i < 90; i++) panel.Children.Add(new TextBlock
        {
            Text = $"Paragraph {i:D3}: synthetic article, safe for capture tests.",
            FontSize = 20,
            Foreground = Brushes.Black,
            Margin = new Thickness(0, 12, 0, 12)
        });
        return new Window
        {
            Title = "Glimpse Synthetic Fixture",
            Width = 800,
            Height = 650,
            Left = 60,
            Top = 60,
            Background = Brushes.White,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Visible }
        };
    }
    private static async Task RunProbe(bool negative)
    {
        var fixture = CreateFixture();
        fixture.Topmost = true;
        fixture.Show(); fixture.Activate();
        if (negative)
        {
            int x = GetSystemMetrics(76);
            if (x >= 0) throw new InvalidOperationException("Negative-coordinate monitor unavailable.");
            SetWindowPos(new WindowInteropHelper(fixture).Handle, 0, x + 60, 60, 800, 650, 0x14);
        }
        await Task.Delay(500);
        nint fixtureHwnd = new WindowInteropHelper(fixture).Handle;
        var origin = fixture.PointToScreen(new(10, 10));
        var rect = new PixelRect((int)origin.X, (int)origin.Y, 650, 420);
        var capture = new WindowsCapture();
        byte[] baseline = capture.Capture(rect);
        var lens = new LensWindow(rect);
        lens.Show();
        var overlay = new Window
        {
            Title = "Excluded probe overlay",
            WindowStyle = WindowStyle.None,
            Width = 180,
            Height = 100,
            Left = fixture.Left + 60,
            Top = fixture.Top + 60,
            Topmost = true,
            Background = Brushes.Magenta,
            ShowInTaskbar = false
        };
        overlay.SourceInitialized += (_, _) =>
        {
            if (!SetWindowDisplayAffinity(new WindowInteropHelper(overlay).Handle, 0x11))
                throw new InvalidOperationException("Display affinity unsupported.");
        };
        overlay.Show();
        await Task.Delay(500);
        byte[] excluded = capture.Capture(rect);
        double mismatch = baseline.Zip(excluded).Count(pair => pair.First != pair.Second) / (double)baseline.Length;
        if (mismatch > .005) throw new InvalidOperationException($"Self exclusion mismatch {mismatch:P3}");
        overlay.Close();
        await Task.Delay(200);
        var center = new PointNative { X = rect.X + 500, Y = rect.Y + 300 };
        nint target = WindowFromPoint(center);
        nint root = GetAncestor(target, 2);
        if (root != fixtureHwnd) throw new InvalidOperationException($"Hollow lens hit target {target}, root {root}, expected fixture {fixtureHwnd}, lens {new WindowInteropHelper(lens).Handle}, capture {lens.CaptureRect}, point {center.X},{center.Y}.");
        var actual = lens.CaptureRect;
        if (actual != rect) throw new InvalidOperationException($"Physical crop mismatch: {actual} vs {rect}");
        var timer = Stopwatch.StartNew();
        var ocr = new WindowsOcr("en-US");
        var lines = await ocr.RecognizeAsync(excluded, rect.Width, rect.Height, CancellationToken.None);
        int elapsed = (int)timer.ElapsedMilliseconds;
        if (!string.Join(" ", lines.Select(l => l.Text)).Contains("synthetic", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Local OCR failed synthetic English fixture.");
        var chinese = new List<OcrBlock>();
        int chineseMs = 0;
        if (WindowsOcr.Languages.Contains("zh-Hans-CN"))
        {
            timer.Restart();
            chinese = await new WindowsOcr("zh-Hans-CN").RecognizeAsync(excluded, rect.Width, rect.Height, CancellationToken.None);
            chineseMs = (int)timer.ElapsedMilliseconds;
        }
        string expectedChinese = "中文阅读测试：保留每一次稳定观察。";
        string? observedChinese = chinese.Select(l => string.Concat(l.Text.Where(c => !char.IsWhiteSpace(c))))
            .FirstOrDefault(t => t.Contains("稳定", StringComparison.Ordinal));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            os = Environment.OSVersion.VersionString,
            processorCount = Environment.ProcessorCount,
            dpi = GetDpiForWindow(fixtureHwnd),
            virtualDesktop = new { x = GetSystemMetrics(76), y = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79) },
            rect,
            selfCaptureMismatch = mismatch,
            clickThroughHitTest = true,
            physicalAlignment = true,
            languages = WindowsOcr.Languages,
            englishOcrMs = elapsed,
            englishLines = lines.Select(l => l.Text),
            chineseOcrMs = chineseMs,
            chineseLines = chinese.Select(l => l.Text)
            ,
            chineseCharacterErrorRate = observedChinese is null ? (double?)null :
                EditDistance(expectedChinese, observedChinese) / (double)expectedChinese.Length
        }, new JsonSerializerOptions(Envelope.Json) { WriteIndented = true }));
        lens.Close(); fixture.Close();
    }
    private static int EditDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(d[i - 1, j] + 1, Math.Min(d[i, j - 1] + 1, d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)));
        return d[a.Length, b.Length];
    }
    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PointNative point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flag);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
