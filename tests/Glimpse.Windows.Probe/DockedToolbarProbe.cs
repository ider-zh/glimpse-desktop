using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glimpse.Agent;
using Glimpse.Contracts;
using Glimpse.UI;
using TextBox = System.Windows.Controls.TextBox;

namespace Glimpse.Windows.Probe;

// Explicit, on-device UI probe. It creates its own synthetic windows, raises only
// their routed events, and renders only their WPF tree. OCR is restricted to the
// probe's own generated fixture; it performs no model call.
internal static class DockedToolbarProbe
{
    public static async Task Run(string imagePath)
    {
        PlacementChecks();
        var rect = new PixelRect(120, 160, 620, 400);
        var lens = new LensWindow(rect);
        var toolbar = new DockedToolbarWindow(rect);
        var observation = new ObservationToolbarWindow();
        int records = 0, toggles = 0, reselects = 0, closes = 0;
        bool negativeMonitor = false;
        var prompts = new List<(string Task, string Prompt)>();
        Guid sessionId = Guid.NewGuid();
        observation.RecordsRequested += () => records++;
        observation.ToggleObservationRequested += () => toggles++;
        observation.ReselectRequested += () => reselects++;
        observation.CloseLensRequested += () => closes++;
        toolbar.GenerateAsync = (task, prompt, _) =>
        {
            prompts.Add((task, prompt));
            return Task.FromResult(new AgentResult(Guid.NewGuid(), sessionId, 4, "offline-sidebar", task,
                CardValidator.SchemaFor(task), "completed", [new("合成卡片", "合成工具栏测试结果。")], null, null, 0, []));
        };
        try
        {
            lens.Show(); toolbar.Show(); observation.Show(); observation.Follow(rect);
            toolbar.SetSession(sessionId);
            toolbar.UpdateState(true, "合成测试 · 正在记录", 4, 144, true);
            toolbar.Follow(rect);
            await Task.Delay(250);
            toolbar.UpdateLayout();
            Check(toolbar.FindName("PostButtons") is not null, "Agent sidebar exposes independent post selection");
            Check(toolbar.FindName("ObserveButton") is null && toolbar.FindName("CloseLensButton") is null, "Capture controls are absent from Agent sidebar");
            nint handle = new WindowInteropHelper(toolbar).Handle;
            Check(Native.GetDpiForWindow(handle) == 96, "This physical placement check requires current 96 DPI");
            Check(Native.GetWindowDisplayAffinity(handle, out uint affinity) && affinity == 0x11, "Toolbar is excluded from capture");
            Check((Native.GetWindowLongPtr(handle, -20).ToInt64() & 0x20) == 0, "Toolbar is not click-through");
            var first = Bounds(toolbar);
            Check(!Intersects(first, rect), "Toolbar remains outside initial capture region");
            Check(first.X >= rect.X + rect.Width, "Toolbar docks on right when space permits");
            Check(!Intersects(Bounds(observation), rect) && !Intersects(Bounds(observation), first), "Observation toolbar is separate from capture and Agent sidebar");
            Check(Native.GetWindowDisplayAffinity(new WindowInteropHelper(observation).Handle, out uint observationAffinity) && observationAffinity == 0x11, "Observation toolbar is excluded from capture");
            var observe = Find<Button>(observation, "ObserveButton");
            Check(!string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(observe)) && observe.ToolTip is not null,
                "Icon capture controls have accessible names and hover hints");
            var hitPoint = observe.PointToScreen(new(observe.ActualWidth / 2, observe.ActualHeight / 2));
            Check(Native.GetAncestor(Native.WindowFromPoint(new((int)hitPoint.X, (int)hitPoint.Y)), 2) == new WindowInteropHelper(observation).Handle,
                "Toolbar button receives native hit testing");

            Click(observation, "RecordsButton"); Click(observation, "ObserveButton"); Click(observation, "ReselectButton");
            Check(records == 1 && toggles == 1 && reselects == 1, "Record/toggle/reselect events forwarded once");
            var prompt = Find<TextBox>(toolbar, "PromptInput");
            foreach (var (button, task) in new[] { ("CommentButton", "comment"), ("ReplyButton", "reply"),
                ("SummaryButton", "summary"), ("AdviceButton", "analysis"), ("SendButton", "custom") })
            {
                prompt.Text = task == "custom" ? "合成问题：请按两点解释。" : "合成补充：保持简洁。";
                int before = prompts.Count;
                Click(toolbar, button);
                await Until(() => prompts.Count == before + 1 && Find<Button>(toolbar, button).IsEnabled);
                Check(prompts.Count == before + 1 && prompts[^1] == (task, prompt.Text), "Task and custom prompt forwarded exactly");
            }
            toolbar.UpdateState(false, "合成测试 · 已停止记录", 4, 144, true);
            Check(Find<Button>(observation, "ObserveButton").IsEnabled, "Stopped observation can be restarted");

            var moved = rect with { X = 1440, Y = 280, Width = 340 };
            lens.Restore(moved); toolbar.Follow(moved); observation.Follow(moved);
            await Task.Delay(150);
            var second = Bounds(toolbar);
            Check(second != first && !Intersects(second, moved), "Toolbar follows moved lens without entering capture");
            Check(second.X + second.Width <= moved.X, "Toolbar docks left near right screen edge");

            negativeMonitor = Native.GetSystemMetrics(76) < 0;
            if (negativeMonitor)
            {
                var negative = new PixelRect(Native.GetSystemMetrics(76) + 120, 180, 540, 350);
                lens.Restore(negative); toolbar.Follow(negative);
                await Task.Delay(150);
                var positioned = Bounds(toolbar);
                Check(positioned.X < 0 && !Intersects(positioned, negative), "Native toolbar follows a negative-coordinate monitor");
            }
            // Return to a predictable primary-monitor layout for the standalone image.
            lens.Restore(rect); toolbar.Follow(rect); observation.Follow(rect); toolbar.UpdateLayout();
            SaveVisual(toolbar, imagePath);
            SaveVisual(observation, imagePath + ".observation.png");
            Click(observation, "CloseLensButton");
            Check(closes == 1, "Close-lens action forwarded once");
            observation.CloseForOwner(); toolbar.CloseForOwner();
            Check(closes == 1, "Owner cleanup does not repeat close-lens action");
        }
        finally
        {
            observation.CloseForOwner(); toolbar.CloseForOwner(); lens.Close();
        }
        await WorkflowChecks();
        Console.WriteLine($"DOCKED TOOLBAR PASS: native 96-DPI right/left following, excluded/clickable window, preset/custom prompt and record controls; negative-monitor={negativeMonitor}; startup selection/auto-observe, pause/restart/close and canceled selection; only synthetic fixture OCR, no model calls.");
    }

    private static async Task WorkflowChecks()
    {
        string directory = Path.Combine(Path.GetTempPath(), "glimpse-toolbar-workflow-" + Guid.NewGuid().ToString("N"));
        var fixture = new Window
        {
            Title = "Glimpse synthetic toolbar workflow fixture", Width = 900, Height = 650,
            Left = 40, Top = 60, Topmost = true, Background = Brushes.White,
            Content = new TextBlock
            {
                Margin = new Thickness(32), FontSize = 22, LineHeight = 38, TextWrapping = TextWrapping.Wrap,
                Text = "SYNTHETIC READING FIXTURE\nThe Pine Reading Club meets on Saturday at ten.\nBring one book and one question.\nThis generated fixture contains no personal information.\nOnly these synthetic words may be observed during this test."
            }
        };
        MainWindow? window = null;
        try
        {
            fixture.Show(); fixture.UpdateLayout();
            await Task.Delay(150);
            var origin = fixture.PointToScreen(new(32, 80));
            var capture = new PixelRect((int)origin.X, (int)origin.Y, 640, 360);
            int selections = 0;
            string? previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_DIR");
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", directory);
            try { window = new MainWindow(true, () => { selections++; return capture; }); }
            finally { Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", previous); }
            var active = window!;
            active.Show();
            await Until(() => active.IsObserving && active.SelectedRegion == capture && active.DockedToolbar is { IsVisible: true });
            Check(selections == 1, "App startup invokes region selection exactly once");
            Check(!Intersects(Bounds(active.DockedToolbar!), capture), "Startup toolbar stays outside synthetic capture");
            await Until(() => !Find<TextBlock>(active.DockedToolbar!, "Counts").Text.StartsWith("已记录 0 ", StringComparison.Ordinal));
            Check(Find<Button>(active.DockedToolbar!, "SummaryButton").IsEnabled, "Synthetic OCR makes one-click sidebar tasks available");
            Check(active.ObservationToolbar is { IsVisible: true }, "Separate observation toolbar is visible");
            await active.AddIgnoreRegionAsync(new PixelRect(400, 220, 120, 100));
            await Until(() => Find<Button>(active.ObservationToolbar!, "ClearIgnoreRegionsButton").IsEnabled);
            Check(active.IsObserving, "Adding ignored video area preserves observing state");
            Click(active.ObservationToolbar!, "ClearIgnoreRegionsButton");
            await Until(() => !Find<Button>(active.ObservationToolbar!, "ClearIgnoreRegionsButton").IsEnabled);
            Click(active.ObservationToolbar!, "AnalyzeDynamicRegionsButton");
            Check(!Find<Button>(active.ObservationToolbar!, "AnalyzeDynamicRegionsButton").IsEnabled,
                "Dynamic-area analysis shows a bounded busy state");
            Check(Find<Button>(active.ObservationToolbar!, "CloseLensButton").IsEnabled,
                "Close remains available while dynamic-area analysis runs");
            await Until(() => Find<Button>(active.ObservationToolbar!, "AnalyzeDynamicRegionsButton").IsEnabled);
            Check(active.IsObserving, "Static scene analysis restores active observation");
            Click(active.ObservationToolbar!, "RecordsButton");
            await Until(() => active.IsVisible && Find<TabControl>(active, "Views").SelectedIndex == 1);
            Check(selections == 1 && active.IsObserving, "Opening records preserves observation without repeating startup selection");
            await active.ToggleObservationAsync();
            await Until(() => !active.IsObserving);
            await active.ToggleObservationAsync();
            await Until(() => active.IsObserving);
            await active.CloseObservationAsync();
            await Until(() => !active.IsObserving && active.DockedToolbar is null);
            Check(active.ObservationToolbar is null, "Closing capture removes both attached surfaces");
            Check(selections == 1 && active.IsVisible, "Closing the lens shows records without another selection");
            await CloseWindow(active); window = null;

            Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", directory);
            try { window = new MainWindow(true, () => { selections++; return null; }); }
            finally { Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", previous); }
            var canceled = window!;
            canceled.Show();
            await Until(() => selections == 2);
            await Task.Delay(350);
            Check(!canceled.IsObserving && canceled.DockedToolbar is null && canceled.IsVisible, "Canceled startup selection keeps inspector available without observing");
            await CloseWindow(canceled); window = null;
        }
        finally
        {
            if (window is not null) await CloseWindow(window);
            fixture.Close();
            await ProbeCleanup.DeleteAsync(directory);
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (!condition()) await Task.Delay(40, timeout.Token);
    }
    private static async Task CloseWindow(Window window)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static void PlacementChecks()
    {
        const int width = 360, height = 760;
        var primary = new PixelRect(0, 0, 1920, 1080);
        var rightCapture = new PixelRect(120, 160, 620, 400);
        var right = ToolbarPlacement.Place(rightCapture, width, height, primary);
        Check(Contained(right, primary) && !Intersects(right, rightCapture) && right.X >= rightCapture.X + rightCapture.Width,
            "Pure placement chooses available right side");
        var leftCapture = new PixelRect(1500, 100, 360, 500);
        var left = ToolbarPlacement.Place(leftCapture, width, height, primary);
        Check(Contained(left, primary) && !Intersects(left, leftCapture) && left.X + left.Width <= leftCapture.X,
            "Pure placement chooses left near screen edge");
        var negativeArea = new PixelRect(-1920, 0, 1920, 1080);
        var negativeCapture = new PixelRect(-1780, 180, 520, 400);
        var negative = ToolbarPlacement.Place(negativeCapture, width, height, negativeArea);
        Check(Contained(negative, negativeArea) && negative.X < 0 && !Intersects(negative, negativeCapture), "Negative physical coordinates preserved");
        foreach (var capture in new[] { new PixelRect(-20, -30, 90, 100), new PixelRect(1880, 1020, 80, 100), primary })
            Check(Contained(ToolbarPlacement.Place(capture, width, height, primary), primary), "Edge/full-screen fallback remains on display");
        foreach (var capture in new[] { rightCapture, leftCapture, new PixelRect(120, 25, 620, 400), primary })
        {
            var strip = ToolbarPlacement.PlaceObservation(capture, 356, 91, primary);
            Check(Contained(strip, primary), "Compact observation strip remains reachable");
            if (capture != primary) Check(!Intersects(strip, capture), "Compact observation strip prefers above/below capture");
        }
    }
    private static T Find<T>(FrameworkElement window, string name) where T : FrameworkElement =>
        window.FindName(name) as T ?? throw new InvalidOperationException("Toolbar control missing: " + name);
    private static void Click(FrameworkElement window, string name)
    {
        var button = Find<Button>(window, name);
        Check(button.IsEnabled && button.IsVisible, "Toolbar action is visible and enabled: " + name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static PixelRect Bounds(Window window)
    {
        Check(Native.GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds), "Native toolbar bounds available");
        return new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }
    private static bool Intersects(PixelRect a, PixelRect b) => a.X < b.X + b.Width && a.X + a.Width > b.X && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
    private static bool Contained(PixelRect inner, PixelRect outer) => inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;
    private static void SaveVisual(Window window, string path)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = File.Create(path); encoder.Save(output);
    }
    private static void Check(bool condition, string detail)
    { if (!condition) throw new InvalidOperationException("Docked toolbar probe failed: " + detail); }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public readonly struct Point(int x, int y) { public readonly int X = x, Y = y; }
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] public static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll")] public static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint window, int index);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(nint window, out Rect rect);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint window);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    }
}
