using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glimpse.Contracts;
using Glimpse.Providers;
using Glimpse.Storage;
using Glimpse.UI;

namespace Glimpse.Windows.Probe;

// Opt-in authenticated inference. Use only the previously prepared, explicitly
// synthetic CLI fixture; never run against normal user data or in CI.
internal static class LiveSidebarAgentProbe
{
    public static async Task Run(string directory, string imagePath)
    {
        directory = Path.GetFullPath(directory);
        string ordinaryData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glimpse");
        Check(!directory.TrimEnd(Path.DirectorySeparatorChar).Equals(ordinaryData, StringComparison.OrdinalIgnoreCase), "Ordinary app data is excluded");
        var repository = new SessionRepository(Path.Combine(directory, "sessions.db"));
        var session = await repository.LoadAsync((await repository.ListAsync()).Single().Id) ?? throw new InvalidOperationException();
        Check(session.Title == "CodeBuddy CLI 真实流程 · 合成内容" && session.Blocks.Count == 4 &&
            session.Blocks.Any(block => block.Text.Contains("synthetic@example.test", StringComparison.Ordinal)), "Explicit synthetic fixture required");
        var storedSettings = await new ProviderSettingsStore(directory).LoadPublicAsync();
        Check(storedSettings.SelectedProviderId == "codebuddy-cli" &&
            storedSettings.Providers.Single(profile => profile.Id == "codebuddy-cli").Model == "mimo-v2.6-pro", "Existing MiMo CLI profile required");

        string? previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_DIR");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", directory);
        await using var client = new WorkerClient();
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", previous);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Received += envelope => { if (envelope.MessageType == "ready") ready.TrySetResult(); };
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        client.Start(); await ready.Task.WaitAsync(stop.Token);
        await client.RequestAsync("resume", new SessionRequest(session.Id), cancellation: stop.Token);

        var toolbar = new DockedToolbarWindow(session.Rect);
        int callbacks = 0, runs = 0, finishedCallbacks = 0;
        var generated = new List<AgentResult>();
        Guid? canceledPreview = null;
        toolbar.GenerateAsync = async (task, prompt, cancellation) =>
        {
            callbacks++;
            try
            {
                Check(task == "summary", "Direct summary task reaches backend");
                var configured = (await client.RequestAsync("agent-settings-get", new { }, cancellation: cancellation)).Read<AgentSettings>();
                var selected = configured.Providers.Single(profile => profile.Id == configured.SelectedProviderId);
                var request = new AgentTaskRequest(session.Id, task, prompt, configured.ContextCharacterBudget, selected.TimeoutSeconds);
                var preview = (await client.RequestAsync("agent-preview", request, cancellation: cancellation)).Read<AgentContextPreview>();
                Check(!preview.UserPrompt.Contains("synthetic@example.test", StringComparison.Ordinal) &&
                    preview.UserPrompt.Contains("[REDACTED_EMAIL]", StringComparison.Ordinal), "Actual outbound sidebar context is redacted");
                toolbar.SetPreview(preview);
                if (callbacks == 2) canceledPreview = preview.PreviewId;
                using var registration = cancellation.Register(() => _ = Cancel(preview.PreviewId));
                var pending = client.RequestAsync("agent-run", new { previewId = preview.PreviewId }, TimeSpan.FromMinutes(3), cancellation);
                runs++;
                var result = (await pending).Read<AgentResult>();
                generated.Add(result);
                return result;
            }
            finally { finishedCallbacks++; }
        };

        toolbar.Show();
        try
        {
            toolbar.SetSession(session.Id);
            toolbar.UpdateState(false, "合成 CLI 验收 · 已停止观察", session.Blocks.Count, session.Blocks.Sum(block => block.Text.Length), true);
            toolbar.UpdateLayout();
            Check(!Application.Current.Windows.OfType<MainWindow>().Any(window => window.IsVisible), "No main window is shown");
            Click("SummaryButton");
            // Finish on fallback/error too, then report only safe outcome metadata.
            // Waiting for cards alone would hang until the global deadline whenever
            // a correctly rejected model response produced a plain-text fallback.
            await Until(() => finishedCallbacks == 1 && Find<Button>("SummaryButton").IsEnabled);
            var firstResult = generated.FirstOrDefault();
            string outcome = firstResult is { Status: "completed" or "fallback" or "error" or "timeout" or "canceled" } ? firstResult.Status : "no-result";
            string schema = firstResult?.Schema == "summary.v1" ? "summary.v1" : "unexpected";
            Check(callbacks == 1 && runs == 1 && generated.Count == 1 && firstResult?.Status == "completed" &&
                Find<ItemsControl>("ResultCards").Items.Count > 0,
                $"One click returns validated summary cards (status={outcome}, schema={schema})");
            Check(!Application.Current.Windows.OfType<MainWindow>().Any(window => window.IsVisible), "Summary result remains in sidebar without main popup");
            var cards = Find<ItemsControl>("ResultCards");
            toolbar.UpdateLayout();
            int copied = 0;
            for (int index = 0; index < cards.Items.Count; index++)
            {
                var container = (FrameworkElement)cards.ItemContainerGenerator.ContainerFromIndex(index);
                var button = Descendants<Button>(container).Single(control => (string?)control.Content == "复制文字");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilCopyAcknowledged();
                Check(await ReadClipboardWithBoundedRetryAsync(stop.Token) == ((AgentCard)cards.Items[index]).Text,
                    "Real inline card clipboard text matches exactly");
                copied++;
            }
            Check(copied == generated[0].Cards.Count, "Every generated card copied");
            var completedCards = cards.Items.Cast<AgentCard>().ToList();
            Save(toolbar, imagePath);

            Click("SummaryButton");
            await Until(() => runs == 2);
            Console.WriteLine("LIVE SIDEBAR CANCEL STARTED: worker=" + client.ProcessId);
            await Task.Delay(3500, stop.Token);
            Check(Find<Button>("CancelButton").IsEnabled, "Actual second CLI call remains active before cancellation");
            Click("CancelButton");
            await Until(() => Find<Button>("SummaryButton").IsEnabled);
            Check(Find<TextBlock>("AgentStatus").Text.Contains("取消", StringComparison.Ordinal), "Inline sidebar acknowledges cancellation");
            Check(cards.Items.Cast<AgentCard>().SequenceEqual(completedCards), "Canceled sidebar preserves preceding completed cards exactly");
            Check(Find<TextBlock>("PreviousResultNotice") is { Visibility: Visibility.Visible } previousNotice &&
                previousNotice.Text.Contains("上次", StringComparison.Ordinal), "Preserved cards are labeled as a previous result");
            await UntilHistory();
            await Task.Delay(750, stop.Token);
            Check(generated.Count == 1 && cards.Items.Cast<AgentCard>().SequenceEqual(completedCards) && callbacks == 2 && runs == 2,
                "No late card replacement or duplicate requests after cancellation");
            Check(!Application.Current.Windows.OfType<MainWindow>().Any(window => window.IsVisible), "Cancel does not show main window");
            Console.WriteLine($"LIVE SIDEBAR AGENT PASS: one-click authenticated MiMo summary, redacted payload, {copied} valid inline cards with exact clipboard copy, real CLI cancellation preserving labeled previous cards and completed/canceled SQLite history; no main popup, synthetic fixture only.");
        }
        finally { toolbar.CancelGeneration(); toolbar.CloseForOwner(); }

        async Task UntilHistory()
        {
            for (;;)
            {
                var history = await repository.ListAgentResultsAsync(session.Id);
                if (history.Any(result => result.PreviewId == generated[0].PreviewId && result.Status == "completed") &&
                    history.Any(result => result.PreviewId == canceledPreview && result.Status == "canceled")) return;
                await Task.Delay(50, stop.Token);
            }
        }
        T Find<T>(string name) where T : FrameworkElement => (T)toolbar.FindName(name);
        void Click(string name)
        {
            var button = Find<Button>(name); Check(button.IsEnabled, name + " enabled");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        async Task Until(Func<bool> condition)
        { while (!condition()) await Task.Delay(40, stop.Token); }
        async Task UntilCopyAcknowledged()
        {
            using var copied = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            copied.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                while (Find<TextBlock>("CopyStatus").Text != "已复制文字。")
                    await Task.Delay(25, copied.Token);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            { throw new InvalidOperationException("Live sidebar Agent copy was not acknowledged by the UI."); }
        }
        async Task Cancel(Guid previewId)
        {
            try { await client.RequestAsync("agent-cancel", new { previewId }, cancellation: stop.Token); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException) { }
        }
    }
    private static async Task<string> ReadClipboardWithBoundedRetryAsync(CancellationToken cancellation)
    {
        // Called only after the card's successful copy acknowledgement. Never read
        // the user's pre-existing clipboard, and never repeat a model request.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return Clipboard.GetText(); }
            catch (ExternalException)
            {
                if (attempt == 4) break;
                await Task.Delay(80, cancellation);
            }
        }
        throw new InvalidOperationException("Live sidebar Agent clipboard remained busy after bounded read retries.");
    }
    private static void Save(Window window, string imagePath)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(imagePath))!);
        using var output = File.Create(imagePath); encoder.Save(output);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) yield return found;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    private static void Check(bool condition, string label)
    { if (!condition) throw new InvalidOperationException("Live sidebar Agent probe failed: " + label); }
}
