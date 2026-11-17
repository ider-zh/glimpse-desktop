using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glimpse.Agent;
using Glimpse.Contracts;
using Glimpse.UI;
using TextBox = System.Windows.Controls.TextBox;

namespace Glimpse.Windows.Probe;

internal static class AgentUiProbe
{
    public static async Task Run(string imagePath)
    {
        string directory = Path.Combine(Path.GetTempPath(), "glimpse-agent-ui-" + Guid.NewGuid());
        string? previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_DIR");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", directory);
        var window = new MainWindow();
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_DIR", previous);
        var panel = (AgentPanel)window.FindName("AgentView");
        var block = new ContentBlock(Guid.NewGuid(), "这是一段用于界面测试的合成内容。联系 synthetic@example.test。",
            "synthetic", "confirmed", new(1, 1, "Synthetic fixture", 1, "Synthetic fixture"));
        var session = new SessionData(Guid.NewGuid(), "Agent 界面测试", "paused", 1, 1, new(0,0,128,128), 3, [block], [], []);
        var settings = new AgentSettings("offline", [new("offline", "离线测试引擎", "openai-chat", "https://fixture.invalid/v1", "offline-model", "", "", 60, true)]);
        AgentContextPreview? preview = null;
        int generations = 0;
        panel.LoadSettingsAsync = _ => Task.FromResult(settings);
        panel.PreviewAsync = (request, _) => Task.FromResult(preview = new SessionContextBuilder().Build(session, request));
        panel.GenerateAsync = (_, cancellation) => { generations++; return new AgentRunner(new Fake()).RunPreparedAsync(preview!, cancellation: cancellation); };
        window.Show();
        try
        {
            await Task.Delay(1200);
            await panel.ReloadSettingsAsync(); panel.SetSession(session);
            ((Expander)panel.FindName("SettingsExpander")).IsExpanded = false;
            if (!await panel.PrepareFromToolbarAsync("reply", "简短、礼貌地回复。") || preview?.TaskType != "reply" ||
                ((TextBox)panel.FindName("Prompt")).Text != "简短、礼貌地回复。")
                throw new InvalidOperationException("Toolbar task and prompt did not reach the reviewed context.");
            if (await panel.PrepareFromToolbarAsync("custom", "") || ((Button)panel.FindName("GenerateButton")).IsEnabled)
                throw new InvalidOperationException("Empty custom toolbar prompt should not prepare generation.");
            if (!await panel.PrepareFromToolbarAsync("custom", "解释文中的主要限制。") || preview?.TaskType != "custom")
                throw new InvalidOperationException("Custom toolbar question did not prepare local context.");
            if (!await panel.PrepareFromToolbarAsync("summary", "总结关键点。") || generations != 0)
                throw new InvalidOperationException("Toolbar preparation must never trigger model inference.");
            ((Button)panel.FindName("PreviewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => ((Button)panel.FindName("GenerateButton")).IsEnabled);
            if (((TextBox)panel.FindName("PreviewText")).Text.Contains("synthetic@example.test", StringComparison.Ordinal))
                throw new InvalidOperationException("UI preview contains unfiltered email fixture.");
            ((Button)panel.FindName("GenerateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => ((ItemsControl)panel.FindName("Cards")).Items.Count == 2);
            await Task.Delay(100); // Allow the result's Dispatcher scroll/layout to complete.
            if (((Button)panel.FindName("GenerateButton")).IsEnabled)
                throw new InvalidOperationException("UI did not invalidate the consumed one-shot preview.");
            window.UpdateLayout();
            var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(imagePath))!);
            using (var output = File.Create(imagePath)) encoder.Save(output);
            panel.SetSession(session with { Id = Guid.NewGuid() });
            if (((ItemsControl)panel.FindName("Cards")).Items.Count != 0)
                throw new InvalidOperationException("Switching sessions retained unrelated cards.");
            Console.WriteLine("AGENT UI PASS: toolbar reply/custom/summary prompt bridge performs local preview only; actual WPF generate controls, redacted display, validated cards, one-shot button state and session-change clearing. Render contains only synthetic UI data.");
        }
        finally
        {
            var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.TrySetResult();
            window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ProbeCleanup.DeleteAsync(directory);
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private sealed class Fake : ILLMProvider
    {
        public string Id => "offline";
        public Task<ProviderResponse> GenerateAsync(ProviderRequest request, CancellationToken cancellation) =>
            Task.FromResult(new ProviderResponse("{\"schema\":\"summary.v1\",\"keyPoints\":[\"阅读记录保存在本机，会话可恢复。\",\"生成使用已预览的上下文，结果供你复制。\"]}"));
    }
}
