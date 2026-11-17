using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glimpse.Agent;
using Glimpse.Contracts;
using Glimpse.UI;
using TextBox = System.Windows.Controls.TextBox;

namespace Glimpse.Windows.Probe;

// Actual sidebar handlers and clipboard, deterministic model output. No account,
// OCR or external model is needed; every displayed string is a generated fixture.
internal static class SidebarAgentProbe
{
    private const string CopyText = "合成结果：青松读书会。\n带一本书和一个讨论问题。";
    public static async Task Run(string imagePath)
    {
        int mainWindows = MainWindowCount();
        Guid sessionId = Guid.NewGuid();
        var session = new SessionData(sessionId, "合成侧栏测试", "paused", 1, 1, new(140, 140, 600, 360), 7,
            [new(Guid.NewGuid(), "合成阅读内容：周六十点讨论一本书。", "synthetic", "confirmed", new(1, 1, "Synthetic fixture", 1, "Synthetic fixture"))], [], []);
        var sidebar = new DockedToolbarWindow(session.Rect);
        var requests = new List<(string Task, string Prompt)>();
        var pendingStatuses = new List<(string Task, string Status)>();
        int settings = 0;
        sidebar.SettingsRequested += () => settings++;
        sidebar.GenerateAsync = async (task, prompt, cancellation) =>
        {
            requests.Add((task, prompt));
            pendingStatuses.Add((task, Find<TextBlock>(sidebar, "AgentStatus").Text));
            var preview = new SessionContextBuilder().Build(session, new(sessionId, task, prompt));
            sidebar.SetPreview(preview);
            return await new AgentRunner(new FixtureProvider()).RunPreparedAsync(preview, cancellation: cancellation);
        };
        try
        {
            sidebar.Show(); sidebar.SetSession(sessionId);
            sidebar.UpdateState(true, "合成测试 · 正在观察", 1, 23, true);
            await Task.Delay(150); sidebar.UpdateLayout();
            Check(!Descendants<ComboBox>(sidebar).Any(), "Quick tasks need no dropdown");
            Check(Equals(Find<Button>(sidebar, "SendButton").Content, "发送问题"), "Custom action explicitly says it sends a question");
            var resultScroll = Find<ScrollViewer>(sidebar, "AgentScroll");
            Check(!IsInside(resultScroll, Find<TextBlock>(sidebar, "AgentStatus")) && !IsInside(resultScroll, Find<Button>(sidebar, "CancelButton")),
                "Generation status and cancel controls stay outside result scrolling");
            var prompt = Find<TextBox>(sidebar, "PromptInput");
            foreach (var (button, task) in new[] { ("CommentButton", "comment"), ("ReplyButton", "reply"),
                ("SummaryButton", "summary"), ("AdviceButton", "analysis"), ("SendButton", "custom") })
            {
                prompt.Text = task == "custom" ? "合成提问：请解释准备事项。" : "合成要求：请保持简洁。";
                int before = requests.Count;
                Click(sidebar, button);
                await Until(() => requests.Count == before + 1 && Find<Button>(sidebar, button).IsEnabled && Cards(sidebar).Count == 1);
                Check(requests[^1] == (task, prompt.Text), "One click forwards exact task and prompt");
                Check(Cards(sidebar).Single().Text == CopyText, "Validated card appears inline");
                Check(MatchesTask(Find<TextBlock>(sidebar, "ResultSummary").Text, task), "Result metadata identifies its task");
                Check(!Find<TextBlock>(sidebar, "PreviousResultNotice").IsVisible, "Fresh success replaces the previous-result marker");
                Check(settings == 0 && MainWindowCount() == mainWindows, "Generation never requests settings or opens the main window");
            }
            Check(requests.Count == 5, "Each direct action invokes generation exactly once");
            Check(pendingStatuses.All(item => MatchesTask(item.Status, item.Task)), "Busy feedback identifies the specific task");
            Check(Find<TextBox>(sidebar, "PreviewText").Text.Contains("合成阅读内容", StringComparison.Ordinal), "Prepared context remains available inline");
            await Task.Delay(100); sidebar.UpdateLayout();
            await CopyExactly(sidebar);
            SaveVisual(sidebar, imagePath);

            prompt.Clear(); int beforeEmpty = requests.Count;
            var emptySend = Find<Button>(sidebar, "SendButton");
            if (emptySend.IsEnabled) emptySend.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(80);
            Check(requests.Count == beforeEmpty, "Empty custom question never invokes a provider");
            sidebar.GenerateAsync = (_, _, _) => throw new InvalidOperationException("Synthetic configuration unavailable");
            Click(sidebar, "CommentButton");
            await Until(() => Find<Button>(sidebar, "CommentButton").IsEnabled);
            Check(settings == 0 && MainWindowCount() == mainWindows && Find<TextBlock>(sidebar, "AgentStatus").Text.Length > 0,
                "Generation failure remains inline without an implicit settings jump");
            PreviousAnswerRemains(sidebar, "Failure preserves the last completed answer");
            string errorStatus = Find<TextBlock>(sidebar, "AgentStatus").Text;
            await CopyExactly(sidebar);
            Check(Find<TextBlock>(sidebar, "AgentStatus").Text == errorStatus, "Copy toast does not replace an actionable error");
            SaveVisual(sidebar, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(imagePath))!,
                Path.GetFileNameWithoutExtension(imagePath) + "-error.png"));

            sidebar.GenerateAsync = (_, _, _) => Task.FromResult(Result(sessionId, "analysis", "") with
                { Status = "timeout", Cards = [], Error = "Synthetic timeout" });
            Click(sidebar, "AdviceButton");
            await Until(() => Find<Button>(sidebar, "AdviceButton").IsEnabled);
            PreviousAnswerRemains(sidebar, "Timeout preserves the last completed answer");
            Check(MatchesTask(Find<TextBlock>(sidebar, "AgentStatus").Text, "analysis"), "Timeout feedback identifies its task");
            Click(sidebar, "SettingsButton");
            Check(settings == 1 && MainWindowCount() == mainWindows, "Settings is requested only by its explicit button");

            CancellationToken canceled = default;
            int pendingCalls = 0;
            var late = new TaskCompletionSource<AgentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            sidebar.GenerateAsync = (_, _, token) =>
            {
                pendingCalls++; canceled = token;
                sidebar.SetPreview(new SessionContextBuilder().Build(session, new(sessionId, "summary")));
                return late.Task;
            };
            Click(sidebar, "SummaryButton");
            await Until(() => canceled.CanBeCanceled && Find<Button>(sidebar, "CancelButton").IsEnabled);
            PreviousAnswerRemains(sidebar, "Pending generation preserves the last completed answer");
            Find<Expander>(sidebar, "PreviewExpander").IsExpanded = true;
            sidebar.UpdateLayout(); resultScroll.ScrollToEnd();
            await Task.Delay(50); sidebar.UpdateLayout();
            Check(resultScroll.ScrollableHeight > 0, "Expanded context creates a real result-scroll scenario");
            Check(InsideWindow(sidebar, Find<Button>(sidebar, "CancelButton")) && InsideWindow(sidebar, Find<TextBlock>(sidebar, "AgentStatus")),
                "Cancel and generation status remain reachable after result scrolling");
            Find<Button>(sidebar, "SummaryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(pendingCalls == 1 && Find<Button>(sidebar, "ObserveButton").IsEnabled, "Busy sidebar rejects duplicate generation and preserves observation controls");
            Click(sidebar, "CancelButton");
            Check(canceled.IsCancellationRequested, "Cancel reaches the active token");
            late.SetResult(Result(sessionId, "summary", "LATE_CANCELED_RESULT"));
            await Task.Delay(100);
            PreviousAnswerRemains(sidebar, "Canceled late answer cannot erase or replace the previous answer");
            Check(MatchesTask(Find<TextBlock>(sidebar, "AgentStatus").Text, "summary"), "Cancellation feedback identifies its task");

            CancellationToken switched = default;
            var afterSwitch = new TaskCompletionSource<AgentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            sidebar.GenerateAsync = (_, _, token) => { switched = token; return afterSwitch.Task; };
            await Until(() => Find<Button>(sidebar, "ReplyButton").IsEnabled);
            Click(sidebar, "ReplyButton"); await Until(() => switched.CanBeCanceled);
            Guid nextSession = Guid.NewGuid(); sidebar.SetSession(nextSession);
            Check(switched.IsCancellationRequested, "Switching sessions cancels active generation");
            afterSwitch.SetResult(Result(sessionId, "reply", "LATE_OTHER_SESSION_RESULT"));
            await Task.Delay(100);
            Check(Cards(sidebar).Count == 0, "Different session inherits no old cards");
            Check(!Find<TextBlock>(sidebar, "PreviousResultNotice").IsVisible && Find<TextBox>(sidebar, "PreviewText").Text.Length == 0,
                "Session change also clears previous-result labels and old context");

            sidebar.UpdateState(true, "合成测试 · 第二会话", 1, 20, true);
            sidebar.ShowStoredResult(Result(nextSession, "summary", CopyText));
            Check(Cards(sidebar).Single().Text == CopyText, "Next session can restore its own result");
            CancellationToken closed = default;
            var afterClose = new TaskCompletionSource<AgentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            sidebar.GenerateAsync = (_, _, token) => { closed = token; return afterClose.Task; };
            Click(sidebar, "AdviceButton"); await Until(() => closed.CanBeCanceled);
            sidebar.CloseForOwner();
            Check(closed.IsCancellationRequested, "Closing sidebar cancels active generation");
            Check(Cards(sidebar).Count == 0, "Closing clears displayed results");
            afterClose.SetResult(Result(nextSession, "analysis", "LATE_CLOSED_RESULT"));
            await Task.Delay(100);
            Check(MainWindowCount() == mainWindows, "Completion after close cannot open main window");
            Console.WriteLine("SIDEBAR AGENT PASS: direct task/custom actions, task-specific status/metadata, sticky cancel, validated inline cards, preserved prior answers on pending/cancel/error/timeout, real clipboard/error-safe copy feedback, session/close guards; no main-window jump, account or network.");
        }
        finally { sidebar.CloseForOwner(); }
    }
    private static AgentResult Result(Guid session, string task, string text) => new(Guid.NewGuid(), session, 7,
        "offline-sidebar", task, CardValidator.SchemaFor(task), "completed", [new("合成结果", text)], null, null, 0, []);
    private sealed class FixtureProvider : ILLMProvider
    {
        public string Id => "offline-sidebar";
        public Task<ProviderResponse> GenerateAsync(ProviderRequest request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string json = request.Schema switch
            {
                "comment.v1" or "reply.v1" => JsonSerializer.Serialize(new { schema = request.Schema, options = new[] { new { tone = "简洁", text = CopyText } } }),
                "summary.v1" => JsonSerializer.Serialize(new { schema = request.Schema, keyPoints = new[] { CopyText } }),
                _ => JsonSerializer.Serialize(new { schema = request.Schema, findings = new[] { CopyText }, caveats = Array.Empty<string>() })
            };
            return Task.FromResult(new ProviderResponse(json, "offline-fixture"));
        }
    }
    private static int MainWindowCount() => Application.Current.Windows.OfType<MainWindow>().Count();
    private static bool MatchesTask(string value, string task) => task switch
    {
        "comment" => value.Contains("评论", StringComparison.Ordinal),
        "reply" => value.Contains("回复", StringComparison.Ordinal),
        "summary" => value.Contains("总结", StringComparison.Ordinal),
        "analysis" => value.Contains("建议", StringComparison.Ordinal) || value.Contains("分析", StringComparison.Ordinal),
        _ => value.Contains("问题", StringComparison.Ordinal) || value.Contains("提问", StringComparison.Ordinal) || value.Contains("回答", StringComparison.Ordinal)
    };
    private static void PreviousAnswerRemains(FrameworkElement sidebar, string detail)
    {
        Check(Cards(sidebar).Count == 1 && Cards(sidebar).Single().Text == CopyText, detail);
        var marker = Find<TextBlock>(sidebar, "PreviousResultNotice");
        Check(marker.IsVisible && marker.Text.Contains("上次结果", StringComparison.Ordinal), "Preserved answer is explicitly labeled as previous");
    }
    private static async Task CopyExactly(FrameworkElement sidebar)
    {
        var copy = Descendants<Button>(sidebar).Single(button => Equals(button.Content, "复制文字"));
        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Find<TextBlock>(sidebar, "CopyStatus").Text == "已复制文字。");
        string clipboard = await ReadSyntheticClipboard();
        if (clipboard != CopyText)
            Console.WriteLine(JsonSerializer.Serialize(new { copyTagIsText = copy.Tag is string,
                copyTagMatches = Equals(copy.Tag, CopyText), clipboardLength = clipboard.Length,
                expectedLength = CopyText.Length, exactCopy = false,
                lineEndingNormalizedEqual = clipboard.Replace("\r\n", "\n", StringComparison.Ordinal) == CopyText,
                copyReportedSuccess = Find<TextBlock>(sidebar, "CopyStatus").Text == "已复制文字。" }));
        Check(clipboard == CopyText, "Real clipboard preserves Chinese and newline exactly");
    }
    private static async Task<string> ReadSyntheticClipboard()
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return Clipboard.GetText(); }
            catch (ExternalException) when (attempt < 3) { await Task.Delay(80); }
        }
    }
    private static bool IsInside(DependencyObject ancestor, DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
    private static bool InsideWindow(Window window, FrameworkElement element)
    {
        if (!element.IsVisible) return false;
        Rect bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        return bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= window.ActualWidth && bounds.Bottom <= window.ActualHeight;
    }
    private static List<AgentCard> Cards(FrameworkElement sidebar) => Find<ItemsControl>(sidebar, "ResultCards").Items.Cast<AgentCard>().ToList();
    private static T Find<T>(FrameworkElement root, string name) where T : FrameworkElement => root.FindName(name) as T ?? throw new InvalidOperationException("Sidebar control missing: " + name);
    private static void Click(FrameworkElement root, string name)
    {
        var button = Find<Button>(root, name);
        Check(button.IsVisible && button.IsEnabled, "Sidebar action is available: " + name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (T item in Descendants<T>(child)) yield return item;
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static void SaveVisual(Window window, string path)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = File.Create(path); encoder.Save(output);
    }
    private static void Check(bool condition, string detail)
    { if (!condition) throw new InvalidOperationException("Sidebar Agent probe failed: " + detail); }
}
