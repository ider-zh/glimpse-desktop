using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glimpse.Contracts;
using Glimpse.Storage;
using Glimpse.UI;
using TextBox = System.Windows.Controls.TextBox;

namespace Glimpse.Windows.Probe;

// Opt-in, real authenticated CLI inference with the explicitly synthetic fixture.
// Never run in CI or point at the user's ordinary data folder.
internal static class LiveAgentUiProbe
{
    public static async Task Run(string directory, string imagePath)
    {
        directory = Path.GetFullPath(directory);
        var repository = new SessionRepository(Path.Combine(directory, "sessions.db"));
        var session = await repository.LoadAsync((await repository.ListAsync()).Single().Id) ?? throw new InvalidOperationException();
        if (session.Title != "CodeBuddy CLI 真实流程 · 合成内容") throw new InvalidOperationException("Live probe requires the synthetic CodeBuddy fixture.");
        string? previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_DIR");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", directory);
        await using var client = new WorkerClient();
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", previous);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Received += e => { if (e.MessageType == "ready") ready.TrySetResult(); };
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        client.Start(); await ready.Task.WaitAsync(stop.Token);
        await client.RequestAsync("resume", new SessionRequest(session.Id), cancellation: stop.Token);
        var panel = new AgentPanel
        {
            LoadSettingsAsync = async ct => (await client.RequestAsync("agent-settings-get", new { }, cancellation: ct)).Read<AgentSettings>(),
            SaveSettingsAsync = async (update, ct) => (await client.RequestAsync("agent-settings-save", update, cancellation: ct)).Read<AgentSettings>(),
            LoadModelsAsync = async ct => (await client.RequestAsync("agent-models-get", new { }, TimeSpan.FromSeconds(50), ct)).Read<AgentModelCatalog>(),
            PreviewAsync = async (request, ct) => (await client.RequestAsync("agent-preview", request, cancellation: ct)).Read<AgentContextPreview>(),
            GenerateAsync = async (id, ct) =>
            {
                using var registration = ct.Register(() => _ = Cancel(id));
                return (await client.RequestAsync("agent-run", new { previewId = id }, TimeSpan.FromMinutes(3), ct)).Read<AgentResult>();
            }
        };
        var window = new Window { Title = "Glimpse 合成 CLI 验收", Width = 1000, Height = 920, Content = panel };
        window.Show();
        try
        {
            await Until(() => ((TextBlock)panel.FindName("ModelListStatus")).Text.StartsWith("已读取", StringComparison.Ordinal));
            var models = (ComboBox)panel.FindName("Model");
            Check(models.Items.OfType<AgentModelOption>().Any(m => m.Id == "mimo-v2.6-pro"), "Authenticated model discovery includes MiMo");
            models.SelectedItem = models.Items.OfType<AgentModelOption>().Single(m => m.Id == "mimo-v2.6-pro");
            await Task.Delay(60);
            Check(models.Text == "mimo-v2.6-pro", "Model selection stores ID rather than display name");
            ((Expander)panel.FindName("SettingsExpander")).IsExpanded = true;
            window.UpdateLayout();
            Descendants<Button>(panel).Single(b => (string?)b.Content == "保存并切换").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => ((TextBlock)panel.FindName("TaskStatus")).Text.Contains("保存", StringComparison.Ordinal));
            var configured = (await client.RequestAsync("agent-settings-get", new { }, cancellation: stop.Token)).Read<AgentSettings>();
            Check(configured.Providers.Single(p => p.Id == "codebuddy-cli").Model == "mimo-v2.6-pro", "Selected model persisted in isolated profile");
            panel.SetSession(session);
            ((Expander)panel.FindName("SettingsExpander")).IsExpanded = false;
            await Preview();
            Click("GenerateButton");
            Console.WriteLine("LIVE CANCEL STARTED: worker=" + client.ProcessId);
            await Task.Delay(3500, stop.Token);
            Check(((Button)panel.FindName("CancelButton")).IsEnabled, "Real generation still active before cancel");
            Click("CancelButton");
            await Until(() => ((Button)panel.FindName("PreviewButton")).IsEnabled);
            Check(((TextBlock)panel.FindName("TaskStatus")).Text.Contains("已取消", StringComparison.Ordinal), "Cancel acknowledged by UI");
            await Task.Delay(500, stop.Token);
            Check(((ItemsControl)panel.FindName("Cards")).Items.Count == 0, "Canceled invocation yields no late cards");
            Console.WriteLine("LIVE CANCEL PASS: worker=" + client.ProcessId);
            await Preview(); Click("GenerateButton");
            await Until(() => ((Button)panel.FindName("PreviewButton")).IsEnabled);
            var cards = (ItemsControl)panel.FindName("Cards");
            Check(cards.Items.Count > 0, "Real CLI returned validated cards: " + ((TextBlock)panel.FindName("TaskStatus")).Text);
            window.UpdateLayout();
            for (int i = 0; i < cards.Items.Count; i++)
            {
                var container = (FrameworkElement)cards.ItemContainerGenerator.ContainerFromIndex(i);
                var copy = Descendants<Button>(container).Single(b => (string?)b.Content == "复制文字");
                copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(Clipboard.GetText() == ((AgentCard)cards.Items[i]).Text, "Actual generated-card clipboard equality");
            }
            var history = await repository.ListAgentResultsAsync(session.Id);
            Check(history.Any(r => r.Status == "canceled") && history.First().Status == "completed", "Real canceled/completed history persisted");
            var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(imagePath))!);
            using (var output = File.Create(imagePath)) encoder.Save(output);
            Console.WriteLine($"LIVE AGENT UI PASS: real authenticated CodeBuddy model discovery, MiMo ID selection/save, preview redaction, generation, cancellation, {cards.Items.Count} validated cards, exact clipboard text and SQLite history. Synthetic session only.");
        }
        finally { panel.CancelPending(); window.Close(); }

        async Task Preview()
        {
            await Until(() => ((Button)panel.FindName("PreviewButton")).IsEnabled);
            Click("PreviewButton"); await Until(() => ((Button)panel.FindName("GenerateButton")).IsEnabled);
            string text = ((TextBox)panel.FindName("PreviewText")).Text;
            Check(!text.Contains("synthetic@example.test", StringComparison.Ordinal) && text.Contains("[REDACTED_EMAIL]", StringComparison.Ordinal), "Live preview redaction");
            ((Expander)panel.FindName("PreviewExpander")).IsExpanded = false;
        }
        void Click(string name)
        {
            var button = (Button)panel.FindName(name); Check(button.IsEnabled, name + " enabled");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        async Task Until(Func<bool> condition)
        { while (!condition()) await Task.Delay(40, stop.Token); }
        async Task Cancel(Guid id)
        { try { await client.RequestAsync("agent-cancel", new { previewId = id }); } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { } }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) yield return found;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    private static void Check(bool condition, string label)
    { if (!condition) throw new InvalidOperationException("Live Agent UI probe failed: " + label); }
}
