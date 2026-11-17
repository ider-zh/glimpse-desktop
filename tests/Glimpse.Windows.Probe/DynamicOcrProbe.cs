using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Glimpse.Contracts;
using Glimpse.Core;
using Glimpse.Storage;
using Glimpse.Worker;

namespace Glimpse.Windows.Probe;

internal static class DynamicOcrProbe
{
    // Uses an actual animated WPF fixture, WindowsCapture and Windows.Media.Ocr.
    public static async Task Run()
    {
        await MaskCancellation();
        string folder = Path.Combine(Path.GetTempPath(), "glimpse-video-ocr-" + Guid.NewGuid());
        var canvas = new Canvas { Background = Brushes.White };
        var video = new Border { Width = 400, Height = 360, Background = Brushes.DarkRed,
            Child = new TextBlock { Text = "HIDDEN VIDEO TEXT", FontSize = 24, Foreground = Brushes.White, Margin = new Thickness(20) } };
        canvas.Children.Add(video);
        var text = new TextBlock { Text = "OUTSIDE VIDEO\nOCR STILL WORKS\nStable reading text", FontFamily = new FontFamily("Arial"),
            FontSize = 28, Foreground = Brushes.Black, Width = 360, TextWrapping = TextWrapping.Wrap };
        Canvas.SetLeft(text, 430); Canvas.SetTop(text, 30); canvas.Children.Add(text);
        var window = new Window { Title = "Glimpse animated OCR fixture", WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, Width = 820, Height = 360, Left = 100, Top = 100,
            Content = canvas, Topmost = true, ShowInTaskbar = false };
        bool animateWhole = false;
        int animationTick = 0;
        var animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        animation.Tick += (_, _) =>
        {
            // Continuously vary luminance: one, two or three elapsed ticks all differ
            // by more than the detector threshold, unlike a two-color phase alias.
            byte shade = (byte)(30 + animationTick++ * 53 % 180);
            var brush = new SolidColorBrush(Color.FromRgb(shade, shade, shade));
            video.Background = brush;
            if (animateWhole) canvas.Background = brush;
        };
        ReaderService? service = null; Task? loop = null;
        using var stop = new CancellationTokenSource();
        try
        {
            window.Show(); await Task.Delay(250);
            var origin = canvas.PointToScreen(new Point(0, 0));
            var end = canvas.PointToScreen(new Point(canvas.ActualWidth, canvas.ActualHeight));
            var videoEnd = canvas.PointToScreen(new Point(410, 360));
            var rect = new PixelRect((int)origin.X, (int)origin.Y, (int)(end.X - origin.X), (int)(end.Y - origin.Y));
            var mask = new PixelRect(0, 0, (int)(videoEnd.X - origin.X), rect.Height);
            var repo = new SessionRepository(Path.Combine(folder, "sessions.db"));
            string? language = WindowsOcr.Languages.FirstOrDefault(x => x.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            ReaderUpdate? lastUpdate = null;
            IScreenCapture capture = new WindowsCapture();
            bool nativeCapture = true;
            try { _ = capture.Capture(rect); }
            catch (Win32Exception ex)
            {
                nativeCapture = false; capture = new RenderedCapture(canvas);
                Console.WriteLine($"DESKTOP CAPTURE UNAVAILABLE: Win32 error {ex.NativeErrorCode}; continuing with synthetic animated bitmap fixtures and real Windows OCR, not claiming screen-capture validation.");
            }
            service = new ReaderService(repo, update => { lastUpdate = update; return Task.CompletedTask; }, language, screenCapture: capture);
            var session = new SessionData(Guid.NewGuid(), "Animated video OCR fixture", "paused", 1, 1, rect, 0, [], [], []);
            animation.Start();
            await service.SetSessionAsync(session, true, stop.Token);
            loop = service.RunAsync(stop.Token);
            await Task.Delay(2200);
            if (lastUpdate?.State is "Error" or "Paused")
                throw new InvalidOperationException($"Native fixture did not remain active before masking: {lastUpdate.State} {lastUpdate.Error}.");
            if ((await service.GetCurrentSessionAsync())!.Revision != 0)
                throw new InvalidOperationException("Unmasked animation unexpectedly passed the stability gate.");
            await service.SetIgnoreRegionsAsync([mask], stop.Token);
            var timer = Stopwatch.StartNew();
            SessionData current;
            do
            {
                await Task.Delay(100);
                current = (await service.GetCurrentSessionAsync())!;
            } while (current.Revision == 0 && timer.Elapsed < TimeSpan.FromSeconds(12));
            string captured = string.Join(" ", current.Blocks.Select(b => b.Text));
            if (!captured.Contains("OUTSIDE", StringComparison.OrdinalIgnoreCase) ||
                !captured.Contains("WORKS", StringComparison.OrdinalIgnoreCase) ||
                captured.Contains("HIDDEN", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Native OCR did not recognize only the static text outside the animated mask. state={lastUpdate?.State}, error={lastUpdate?.Error}, revision={current.Revision}, masks={current.IgnoreRegions?.Count}, chars={captured.Length}, outside={captured.Contains("OUTSIDE", StringComparison.OrdinalIgnoreCase)}, works={captured.Contains("WORKS", StringComparison.OrdinalIgnoreCase)}, hidden={captured.Contains("HIDDEN", StringComparison.OrdinalIgnoreCase)}.");
            var loaded = await repo.LoadAsync(session.Id);
            if (loaded?.IgnoreRegions?.Count != 1) throw new InvalidOperationException("Mask was not persisted.");
            long ocrMs = timer.ElapsedMilliseconds;
            canvas.Background = Brushes.Black;
            timer.Restart();
            while ((await service.GetCurrentSessionAsync())!.IgnoreRegions?.Count > 0 && timer.Elapsed < TimeSpan.FromSeconds(3))
                await Task.Delay(100);
            if ((await service.GetCurrentSessionAsync())!.IgnoreRegions?.Count != 0)
                throw new InvalidOperationException("Broad outside movement failed to clear the fixed-position mask.");
            await service.PauseAsync(true, stop.Token);
            await service.SetIgnoreRegionsAsync([mask], stop.Token);
            await service.SetRectAsync(rect, stop.Token);
            if ((await service.GetCurrentSessionAsync())!.IgnoreRegions?.Count != 1)
                throw new InvalidOperationException("Equal rectangle update cleared the mask.");
            await service.SetRectAsync(rect with { X = rect.X + 1 }, stop.Token);
            if ((await service.GetCurrentSessionAsync())!.IgnoreRegions?.Count != 0)
                throw new InvalidOperationException("Lens movement did not invalidate the mask.");
            Console.WriteLine($"DYNAMIC OCR {(nativeCapture ? "NATIVE CAPTURE" : "SYNTHETIC FIXTURE")} PASS: continuous animation blocked unmasked OCR; real Windows OCR recorded static outside text in {ocrMs} ms after masking; hidden text absent, storage, broad-motion reset and rect invalidation passed.");

            await service.SetRectAsync(rect, stop.Token);
            canvas.Background = Brushes.White;
            text.Text = "OUTSIDE VIDEO\nAUTO MASK WORKS\nStable reading text";
            await service.AnalyzeDynamicRegionsAsync(stop.Token);
            current = (await service.GetCurrentSessionAsync())!;
            if (current.IgnoreRegions is not { Count: > 0 } || current.Status != "paused" ||
                current.IgnoreRegions.Any(r => FrameMasker.Intersects(new((430 * rect.Width / 820.0), 30, 350 * rect.Width / 820.0, 100), r)))
                throw new InvalidOperationException($"Native dynamic analysis failed to isolate animation or changed the paused state. state={current.Status}, notice={lastUpdate?.CaptureNotice}, candidates={System.Text.Json.JsonSerializer.Serialize(current.IgnoreRegions, Envelope.Json)}.");
            await service.PauseAsync(false, stop.Token);
            timer.Restart();
            do
            {
                await Task.Delay(100); current = (await service.GetCurrentSessionAsync())!;
            } while (!current.Blocks.Any(b => b.Text.Contains("AUTO", StringComparison.OrdinalIgnoreCase)) && timer.Elapsed < TimeSpan.FromSeconds(10));
            if (!current.Blocks.Any(b => b.Text.Contains("AUTO", StringComparison.OrdinalIgnoreCase)) || current.Blocks.Any(b => b.Text.Contains("HIDDEN", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Automatically masked native OCR did not recognize new outside text.");
            await service.PauseAsync(true, stop.Token);
            animateWhole = true;
            await service.AnalyzeDynamicRegionsAsync(stop.Token);
            current = (await service.GetCurrentSessionAsync())!;
            if (lastUpdate?.CaptureNotice?.Contains("大范围", StringComparison.Ordinal) != true || current.IgnoreRegions is not { Count: > 0 } || current.Status != "paused")
                throw new InvalidOperationException("Broad-motion automatic refusal erased a prior mask or resumed paused observation.");
            await service.PauseAsync(false, stop.Token);
            var pendingAnalysis = service.AnalyzeDynamicRegionsAsync(stop.Token);
            await Task.Delay(200);
            await service.PauseAsync(true, stop.Token);
            try { await pendingAnalysis; throw new InvalidOperationException("Changed observation state did not invalidate analysis."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("已变化", StringComparison.Ordinal)) { }
            if ((await service.GetCurrentSessionAsync())!.Status != "paused")
                throw new InvalidOperationException("A late analysis resumed observation after the user paused it.");
            Console.WriteLine("AUTO DYNAMIC OCR PASS: bounded local sampling isolated sampled animation; real Windows OCR read new outside text; global motion refused without erasing existing mask; paused state preserved and a late analysis cannot resume capture.");
        }
        finally
        {
            animation.Stop(); stop.Cancel();
            if (loop is not null) try { await loop; } catch (OperationCanceledException) { }
            if (service is not null) await service.DisposeAsync();
            window.Close();
            await ProbeCleanup.DeleteAsync(folder);
        }
    }

    private static async Task MaskCancellation()
    {
        string folder = Path.Combine(Path.GetTempPath(), "glimpse-mask-cancel-" + Guid.NewGuid());
        var repository = new SessionRepository(Path.Combine(folder, "sessions.db"));
        var delayed = new DelayedOcr();
        var service = new ReaderService(repository, _ => Task.CompletedTask, screenCapture: new StillCapture(), ocrEngine: delayed);
        using var stop = new CancellationTokenSource();
        var session = new SessionData(Guid.NewGuid(), "Mask cancellation fixture", "paused", 1, 1, new(0, 0, 128, 128), 0, [], [], []);
        await service.SetSessionAsync(session, true, stop.Token);
        var loop = service.RunAsync(stop.Token);
        try
        {
            await delayed.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.SetIgnoreRegionsAsync([new(0, 0, 64, 128)], stop.Token);
            await service.PauseAsync(true, stop.Token);
            delayed.Finish.TrySetResult();
            await Task.Delay(200);
            var saved = (await repository.LoadAsync(session.Id))!;
            if (saved.Revision != 0 || saved.IgnoreRegions?.Count != 1)
                throw new InvalidOperationException("Mask edit allowed an obsolete native OCR result to commit.");
            await service.SetSessionAsync(saved, false, stop.Token);
            if ((await service.GetCurrentSessionAsync())!.IgnoreRegions?.Count != 0)
                throw new InvalidOperationException("Reopening saved session reused an old screen-position mask.");
            Console.WriteLine("MASK CANCELLATION PASS: changing ignored regions invalidated delayed OCR, and reopening discarded stale masks.");
        }
        finally
        {
            delayed.Finish.TrySetResult(); stop.Cancel();
            try { await loop; } catch (OperationCanceledException) { }
            await service.DisposeAsync(); await ProbeCleanup.DeleteAsync(folder);
        }
    }

    private sealed class StillCapture : IScreenCapture
    {
        public byte[] Capture(PixelRect rect) => new byte[rect.Width * rect.Height * 4];
    }
    private sealed class RenderedCapture(Canvas element) : IScreenCapture
    {
        private readonly byte[] original = Load("video-ocr-static.png");
        private readonly byte[] automatic = Load("video-ocr-auto.png");
        private static byte[] Load(string name)
        {
            var frame = BitmapFrame.Create(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[820 * 360 * 4]; bitmap.CopyPixels(pixels, 820 * 4, 0); return pixels;
        }
        public byte[] Capture(PixelRect rect)
        {
            var video = (Border)element.Children[0]; var text = (TextBlock)element.Children[1];
            var source = text.Text.Contains("AUTO", StringComparison.Ordinal) ? automatic : original;
            var videoColor = ((SolidColorBrush)video.Background).Color;
            var background = ((SolidColorBrush)element.Background).Color;
            var pixels = new byte[rect.Width * rect.Height * 4];
            for (int y = 0; y < rect.Height; y++)
                for (int x = 0; x < rect.Width; x++)
            {
                int sx = x * 820 / rect.Width, sy = y * 360 / rect.Height;
                int from = (sy * 820 + sx) * 4, to = (y * rect.Width + x) * 4;
                source.AsSpan(from, 4).CopyTo(pixels.AsSpan(to, 4));
                var color = sx < 400 ? videoColor : background;
                if (sx < 400 && source[from] < 200 || sx >= 400 && source[from] > 240)
                { pixels[to] = color.B; pixels[to + 1] = color.G; pixels[to + 2] = color.R; }
            }
            return pixels;
        }
    }
    private sealed class DelayedOcr : IOcrEngine
    {
        public string Name => "Mask cancellation fixture";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<List<OcrBlock>> RecognizeAsync(byte[] bgra, int width, int height, CancellationToken cancellation)
        {
            Started.TrySetResult(); await Finish.Task;
            return [new("Obsolete result before video mask", new(0, 0, 128, 20))];
        }
    }
}
