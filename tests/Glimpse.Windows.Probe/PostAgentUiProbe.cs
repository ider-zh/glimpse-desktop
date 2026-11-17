using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glimpse.Agent;
using Glimpse.Contracts;
using Glimpse.Providers;
using Glimpse.Storage;
using Glimpse.UI;
using Glimpse.Worker;

namespace Glimpse.Windows.Probe;

// Explicit synthetic-only WPF flow. Live mode requires an opt-in command and
// uses the installed CLI's own login; credentials are never opened by this probe.
internal static class PostAgentUiProbe
{
    private const string Pine = "PINE_POST_ONLY: 青松读书会周六十点讨论《小王子》，带一本书。";
    private const string Maple = "MAPLE_POST_ONLY: 红枫跑步队周日上午八点在虚构公园集合，路线五公里。";
    public static async Task Run(string directory, string imagePath, bool live)
    {
        directory = Path.GetFullPath(directory);
        string ordinary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glimpse");
        Check(!directory.TrimEnd(Path.DirectorySeparatorChar).Equals(ordinary, StringComparison.OrdinalIgnoreCase), "Normal application data is excluded");
        Check(!Directory.Exists(directory), "Fixture data directory must be new");
        var repository = new SessionRepository(Path.Combine(directory, "sessions.db"));
        var settings = new ProviderSettingsStore(directory);
        var defaults = await settings.LoadPublicAsync();
        var cli = defaults.Providers.Single(p => p.Id == "codebuddy-cli");
        if (live)
        {
            Check(File.Exists(cli.CliPath), "Installed CodeBuddy CLI required");
            await settings.SaveAsync(new(cli.Id, cli with { Model = "mimo-v2.6-pro", TimeoutSeconds = 120 }));
        }
        Guid pineId = Guid.NewGuid(), mapleId = Guid.NewGuid(), sessionId = Guid.NewGuid();
        ContentBlock Block(string text, int y, Guid? postId, string role) => new(Guid.NewGuid(), text, text, "confirmed",
            new(1, 1, text, 1, text, Geometry: new(1, new(10, y, 550, 24), "synthetic-posts", postId, role)));
        var session = new SessionData(sessionId, "Synthetic isolated post Agent acceptance", "paused", 1, 1,
            new(130, 150, 620, 400), 1,
            [Block("@青松读书会", 10, pineId, "author"), Block(Pine, 45, pineId, "body"),
             Block("@红枫跑步队", 150, mapleId, "author"), Block(Maple, 185, mapleId, "body"),
             Block("点赞 100 评论 20 分享 3", 300, null, "control")], [], []);
        await repository.SaveAsync(session);
        session = await repository.LoadAsync(sessionId) ?? throw new InvalidOperationException("Fixture did not persist.");
        var fake = new FixtureState();
        using var controller = live ? null : new AgentController(settings, repository, _ => Task.FromResult<SessionData?>(session),
            (_, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult<ILLMProvider>(new FixtureProvider(fake)); });
        WorkerClient? client = null;
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(live ? 7 : 1));
        var sidebar = new DockedToolbarWindow(session.Rect);
        var previews = new List<AgentContextPreview>();
        var results = new List<AgentResult>();
        int dispatched = 0, finished = 0;
        if (live)
        {
            string? previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_DIR");
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", directory);
            try { client = new WorkerClient(); }
            finally { Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", previous); }
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Received += envelope => { if (envelope.MessageType == "ready") ready.TrySetResult(); };
            client.Start(); await ready.Task.WaitAsync(stop.Token);
            await client.RequestAsync("resume", new SessionRequest(sessionId), cancellation: stop.Token);
        }
        sidebar.GenerateAsync = async (task, prompt, token) =>
        {
            try
            {
                var ids = sidebar.SelectedPostIds.ToList();
                var request = new AgentTaskRequest(sessionId, task, prompt, 12000, live ? 120 : 60,
                    PostId: ids.Count == 1 ? ids[0] : null, PostIds: ids.Count > 1 ? ids : null);
                var preview = live ? (await client!.RequestAsync("agent-preview", request, cancellation: token)).Read<AgentContextPreview>() :
                    await controller!.PreviewAsync(request, token);
                previews.Add(preview); ValidatePayload(preview, pineId, mapleId); sidebar.SetPreview(preview);
                using var registration = token.Register(() => { if (live) _ = CancelRemote(preview.PreviewId); else controller!.Cancel(preview.PreviewId); });
                dispatched++;
                var result = live ? (await client!.RequestAsync("agent-run", new { previewId = preview.PreviewId }, TimeSpan.FromMinutes(3), token)).Read<AgentResult>() :
                    await controller!.RunAsync(preview.PreviewId, token);
                results.Add(result); return result;
            }
            finally { finished++; }
        };
        try
        {
            sidebar.Show(); sidebar.SetSession(sessionId); sidebar.UpdateState(false, "合成分帖验收", 5, 150, true); sidebar.SetPostCatalog(session);
            await Task.Delay(100, stop.Token); sidebar.UpdateLayout();
            var postButtons = Find<WrapPanel>("PostButtons").Children.OfType<RadioButton>().ToList();
            Check(postButtons.Count == 2 && Find<TextBlock>("PostScopeNotice").Text.Contains("排除 1", StringComparison.Ordinal), "Two posts and excluded controls appear");
            postButtons.Single(b => Equals(b.Tag, pineId)).IsChecked = true;
            Check(sidebar.SelectedPostIds.SequenceEqual([pineId]), "Selecting a post fixes task scope");
            Click("SummaryButton"); await Until(() => finished == 1 && Find<Button>("SummaryButton").IsEnabled);
            Check(results.Count == 1 && results[0].Status == "completed" && results[0].PostId == pineId, "Single post produces validated scoped result");
            Check(!previews[0].UserPrompt.Contains(Maple, StringComparison.Ordinal), "Single-post payload excludes other post exactly");
            Console.WriteLine($"POST AGENT UI SINGLE PASS: mode={(live ? "authenticated-cli" : "offline")}; scopedPayload=true, validatedCards={results[0].Cards.Count}.");
            Find<CheckBox>("AllPostsToggle").IsChecked = true;
            Click("SummaryButton"); await Until(() => finished == 2 && Find<Button>("SummaryButton").IsEnabled);
            var batch = results.Last();
            Check(batch.Status == "completed" && batch.PostResults is { Count: 2 } && batch.PostResults.All(r => r.Status == "completed"),
                "Two independent children complete with validated cards");
            var cards = Find<ItemsControl>("ResultCards");
            Check(cards.Items.Count >= 2 && cards.Items.Cast<AgentCard>().Any(c => c.Title.Contains("青松", StringComparison.Ordinal)) &&
                cards.Items.Cast<AgentCard>().Any(c => c.Title.Contains("红枫", StringComparison.Ordinal)), "Cards retain separate source labels");
            Check(live || fake.MaximumActive == 2, "Offline batch actually runs two independent providers concurrently");
            Console.WriteLine($"POST AGENT UI BATCH PASS: children=2, validatedCards={cards.Items.Count}.");
            sidebar.UpdateLayout();
            for (int i = 0; i < cards.Items.Count; i++)
            {
                Find<TextBlock>("CopyStatus").Text = "";
                var container = (FrameworkElement)cards.ItemContainerGenerator.ContainerFromIndex(i);
                var copy = Descendants<Button>(container).Single(b => Equals(b.Content, "复制文字"));
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => Find<TextBlock>("CopyStatus").Text == "已复制文字。");
                Check(await ReadCopiedText(stop.Token) == ((AgentCard)cards.Items[i]).Text, "Each generated card copies exactly");
            }
            Save(sidebar, imagePath);
            Find<ScrollViewer>("AgentScroll").ScrollToEnd(); sidebar.UpdateLayout();
            Save(sidebar, imagePath + ".results.png");
            var completed = cards.Items.Cast<AgentCard>().ToList();
            fake.Hold = true; int beforeCalls = fake.Started;
            Click("SummaryButton"); await Until(() => dispatched == 3);
            if (live) await Task.Delay(3500, stop.Token);
            else await Until(() => fake.Started == beforeCalls + 2 && fake.Active == 2);
            Check(Find<Button>("CancelButton").IsEnabled && Find<Button>("CancelButton").IsVisible, "Parent cancellation remains reachable during batch");
            Guid canceledId = previews.Last().PreviewId;
            Click("CancelButton"); await Until(() => Find<Button>("SummaryButton").IsEnabled);
            Check(cards.Items.Cast<AgentCard>().SequenceEqual(completed) && Find<TextBlock>("PreviousResultNotice").IsVisible,
                "Cancel preserves exact prior cards with previous-result label");
            AgentResult? savedCanceled = null;
            while (savedCanceled is null)
            {
                savedCanceled = (await repository.ListAgentResultsAsync(sessionId)).FirstOrDefault(r => r.PreviewId == canceledId);
                if (savedCanceled is null) await Task.Delay(50, stop.Token);
            }
            Check(savedCanceled.Status == "canceled" && savedCanceled.PostResults is { Count: 2 } &&
                savedCanceled.PostResults.All(r => r.Status == "canceled"), "Parent history retains both canceled child outcomes");
            await Task.Delay(200, stop.Token);
            Check(!Application.Current.Windows.OfType<MainWindow>().Any(w => w.IsVisible) && cards.Items.Cast<AgentCard>().SequenceEqual(completed),
                "No main popup or late card replacement");
            Console.WriteLine($"POST AGENT UI PASS: mode={(live ? "authenticated-cli" : "offline")}; single/batch scopedpayloads, two child results, exactClipboardCards={completed.Count}, canceledchildren=2, preserved previous cards, no main popup; synthetic data only.");
        }
        finally { sidebar.CancelGeneration(); sidebar.CloseForOwner(); if (client is not null) await client.DisposeAsync(); }

        T Find<T>(string name) where T : FrameworkElement => (T)sidebar.FindName(name);
        void Click(string name) { var b = Find<Button>(name); Check(b.IsEnabled, name + " enabled"); b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        async Task Until(Func<bool> condition) { while (!condition()) await Task.Delay(30, stop.Token); }
        async Task CancelRemote(Guid id)
        { try { await client!.RequestAsync("agent-cancel", new { previewId = id }, cancellation: stop.Token); } catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or ObjectDisposedException) { } }
    }
    private static void ValidatePayload(AgentContextPreview preview, Guid pine, Guid maple)
    {
        foreach (var child in preview.Children ?? [preview])
        {
            Check(child.PostId == pine || child.PostId == maple, "Each outbound task has a post boundary");
            Check(child.UserPrompt.Contains(child.PostId == pine ? Pine : Maple, StringComparison.Ordinal) &&
                !child.UserPrompt.Contains(child.PostId == pine ? Maple : Pine, StringComparison.Ordinal), "No cross-post content enters a child payload");
            Check(!child.UserPrompt.Contains("点赞 100", StringComparison.Ordinal), "Control text is excluded");
        }
    }
    private sealed class FixtureState
    {
        public int Active, MaximumActive, Started;
        public bool Hold;
        public ConcurrentQueue<string> Payloads { get; } = new();
    }
    private sealed class FixtureProvider(FixtureState state) : ILLMProvider
    {
        public string Id => "offline-post-ui";
        public async Task<ProviderResponse> GenerateAsync(ProviderRequest request, CancellationToken cancellation)
        {
            state.Payloads.Enqueue(request.UserPrompt);
            Interlocked.Increment(ref state.Started);
            int active = Interlocked.Increment(ref state.Active);
            int maximum;
            do { maximum = state.MaximumActive; } while (active > maximum && Interlocked.CompareExchange(ref state.MaximumActive, active, maximum) != maximum);
            try
            {
                await Task.Delay(state.Hold ? Timeout.Infinite : 160, cancellation);
                string text = request.UserPrompt.Contains("PINE_POST_ONLY", StringComparison.Ordinal) ? "青松读书会：周六十点讨论《小王子》。" : "红枫跑步队：周日上午八点集合。";
                return new(JsonSerializer.Serialize(new { schema = "summary.v1", keyPoints = new[] { text } }), "offline-synthetic");
            }
            finally { Interlocked.Decrement(ref state.Active); }
        }
    }
    private static async Task<string> ReadCopiedText(CancellationToken token)
    {
        for (int i = 0; ; i++) { try { return Clipboard.GetText(); } catch (ExternalException) when (i < 4) { await Task.Delay(80, token); } }
    }
    private static void Save(Window window, string path)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); using var output = File.Create(path); encoder.Save(output);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T item) yield return item; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException("Post Agent UI probe failed: " + label); }
}
