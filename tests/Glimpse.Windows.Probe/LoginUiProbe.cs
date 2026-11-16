using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.Diagnostics;
using Glimpse.Contracts;
using Glimpse.UI;
using Glimpse.Providers;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TextBox = System.Windows.Controls.TextBox;

namespace Glimpse.Windows.Probe;

internal static class LoginUiProbe
{
    public static async Task Run(string imagePath)
    {
        await ControlsAndClipboard(imagePath);
        await CloseDuringStart();
        await CloseAfterReadFailure();
        await ExitIsNotAuthenticationProof();
        await NativeTerminalRoundTrip();
        Console.WriteLine("LOGIN UI PASS: local xterm/WebView2 VT rendering, native ConPTY-to-xterm input/output round trip, resize, selection/clipboard, blocked external navigation, explicit start, single-flight polling, safe-link filtering, stop/close cleanup, pending-start cleanup and no authentication-success inference. No installed CodeBuddy CLI or external browser was launched.");
    }

    private static async Task ControlsAndClipboard(string imagePath)
    {
        const string link = "https://example.test/login?state=synthetic-only&verify=fixture-123";
        Guid id = Guid.NewGuid();
        int starts = 0, reads = 0, stops = 0, activeReads = 0, maxReads = 0;
        var inputs = new List<string>();
        var sizes = new List<(int Columns, int Rows)>();
        string initialOutput = "\u001b[2J\u001b[H\u001b[38;2;125;226;208m离线登录测试（不会连接 CodeBuddy）\u001b[0m\r\n请选择登录方式：\r\n> 中国站\r\n  国际站";
        var window = new CodeBuddyLoginWindow(_ =>
        {
            starts++;
            return Task.FromResult(new CliLoginSnapshot(id, "running", "离线登录测试（不会连接 CodeBuddy）\n请选择登录方式：\n> 中国站\n  国际站", [], TerminalOutput: initialOutput));
        }, async (session, _) =>
        {
            Check(session == id, "read session ID");
            maxReads = Math.Max(maxReads, ++activeReads);
            try
            {
                await Task.Delay(350); reads++;
                return new(id, "running", "离线登录测试（不会连接 CodeBuddy）\n请选择登录方式：\n> 中国站\n  国际站\n\n请在自己的浏览器打开验证链接：\n" + link,
                    [link, "javascript:alert('fixture')", "https://name:password@example.test/private", "file:///C:/synthetic.txt"],
                    TerminalOutput: initialOutput + "\r\n\r\n请在自己的浏览器打开验证链接：\r\n" + link);
            }
            finally { activeReads--; }
        }, (session, text, _) =>
        {
            Check(session == id, "input session ID"); inputs.Add(text); return Task.CompletedTask;
        }, (session, _) =>
        {
            Check(session == id, "stop session ID"); stops++; return Task.CompletedTask;
        }, (session, columns, rows, _) =>
        {
            Check(session == id && columns is >= 20 and <= 240 && rows is >= 5 and <= 100, "resize bounds and session");
            sizes.Add((columns, rows)); return Task.CompletedTask;
        });
        window.Show();
        try
        {
            await Ready(window);
            Check(starts == 0 && reads == 0, "no automatic CLI start");
            Click(window, "StartButton");
            await Until(() => ((TextBox)window.FindName("Terminal")).Text.Contains(link, StringComparison.Ordinal));
            Check(starts == 1 && reads > 0 && maxReads == 1, "explicit start and non-overlapping polling");
            var links = (ComboBox)window.FindName("Links");
            Check(links.Items.Count == 1 && (string?)links.SelectedItem == link, "only safe links displayed");
            Check(((TextBox)window.FindName("SelectedLink")).Text == link, "full link preserved");
            var web = (WebView2)window.FindName("EmbeddedTerminal");
            await UntilAsync(async () => (await TerminalText(web)).Contains("中国站", StringComparison.Ordinal));
            Check(!(await TerminalText(web)).Contains('\u001b'), "VT sequences interpreted by xterm");
            await web.CoreWebView2.ExecuteScriptAsync("window.glimpseTerminal.input('\\u001b[B')");
            await Until(() => inputs.Count == 1);
            await web.CoreWebView2.ExecuteScriptAsync("window.glimpseTerminal.input('\\r')");
            await Until(() => inputs.Count == 2);
            Check(inputs[0] == "\u001b[B" && inputs[1] == "\r", "terminal key sequences");
            await web.CoreWebView2.ExecuteScriptAsync("window.glimpseTerminal.input('synthetic account hint\\r')");
            await Until(() => inputs.Count == 3);
            Check(inputs[2] == "synthetic account hint\r", "direct xterm input submission");
            await Until(() => sizes.Count > 0);
            var originalSize = sizes[^1];
            window.Width -= 100;
            await Until(() => sizes.Any(size => size != originalSize));
            await web.CoreWebView2.ExecuteScriptAsync("window.glimpseTerminal.selectAll()");
            Click(window, "CopySelectionButton");
            await Until(() => ((TextBlock)window.FindName("LoginStatus")).Text == "已复制终端选中文字。");
            Check(Clipboard.GetText().Contains("中国站", StringComparison.Ordinal), "xterm selection clipboard");
            web.CoreWebView2.Navigate("https://example.test/blocked-navigation");
            await Task.Delay(100);
            Check(web.CoreWebView2.Source == "https://glimpse-terminal.invalid/index.html" && web.Visibility == Visibility.Visible,
                "external navigation blocked without replacing terminal");
            // Read only the synthetic URL this button has just written. Never inspect
            // the user's pre-existing clipboard, and never open the browser in tests.
            Click(window, "CopyLinkButton");
            Check(Clipboard.GetText() == link, "actual clipboard round trip");
            window.UpdateLayout();
            await RenderWindow(window, web, imagePath);
            Click(window, "StopButton");
            await Until(() => stops == 1 && links.Items.Count == 0 && ((Button)window.FindName("StartButton")).IsEnabled);
            Check(!((Button)window.FindName("OpenLinkButton")).IsEnabled, "stopped links cannot open");
        }
        finally { await window.StopAndCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        Check(stops == 1, "explicit stop is not repeated on close");
    }

    private static async Task CloseDuringStart()
    {
        Guid id = Guid.NewGuid();
        int starts = 0, stops = 0;
        var pending = new TaskCompletionSource<CliLoginSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new CodeBuddyLoginWindow(_ => { starts++; return pending.Task; },
            (_, _) => throw new InvalidOperationException("Unexpected offline poll."),
            (_, _, _) => Task.CompletedTask,
            (session, _) => { Check(session == id, "late-start stop ID"); stops++; return Task.CompletedTask; });
        window.Show();
        try
        {
            await Ready(window);
            Click(window, "StartButton");
            Check(starts == 1, "pending start invoked");
            var closing = window.StopAndCloseAsync();
            await Task.Delay(30);
            Check(!closing.IsCompleted && stops == 0, "close awaits pending start ownership");
            pending.SetResult(new(id, "running", "synthetic pending start", []));
            await closing.WaitAsync(TimeSpan.FromSeconds(4));
            Check(stops == 1 && !window.IsVisible, "close stops newly received session");
            Check(((TextBox)window.FindName("Terminal")).Text.Length == 0, "closed terminal cleared");
        }
        finally
        {
            pending.TrySetResult(new(id, "running", "synthetic pending start", []));
            await window.StopAndCloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task CloseAfterReadFailure()
    {
        Guid id = Guid.NewGuid(); int stops = 0;
        var window = new CodeBuddyLoginWindow(_ => Task.FromResult(new CliLoginSnapshot(id, "running", "synthetic output", [])),
            (_, _) => throw new IOException("Synthetic disconnected pipe."),
            (_, _, _) => Task.CompletedTask,
            (session, _) => { Check(session == id, "read-failure stop ID"); stops++; return Task.CompletedTask; });
        window.Show();
        try
        {
            await Ready(window);
            Click(window, "StartButton");
            await Until(() => ((TextBlock)window.FindName("LoginStatus")).Text.Contains("终端连接中断", StringComparison.Ordinal));
            Check(((Button)window.FindName("StopButton")).IsEnabled, "read failure retains stop ownership");
        }
        finally { await window.StopAndCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        Check(stops == 1, "read failure still stopped on close");
    }

    private static async Task ExitIsNotAuthenticationProof()
    {
        Guid id = Guid.NewGuid();
        var window = new CodeBuddyLoginWindow(_ => Task.FromResult(new CliLoginSnapshot(id, "exited", "synthetic exit", [], 0)),
            (_, _) => throw new InvalidOperationException("Unexpected offline poll."),
            (_, _, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);
        window.Show();
        try
        {
            await Ready(window);
            Click(window, "StartButton");
            Check(((TextBlock)window.FindName("LoginStatus")).Text.Contains("不代表认证成功", StringComparison.Ordinal), "exit code is not authentication proof");
        }
        finally { await window.StopAndCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static async Task NativeTerminalRoundTrip()
    {
        // This executable's /login dispatch is a tiny Console.ReadKey fixture. The
        // installed CodeBuddy CLI, authentication files and network are never used.
        var profile = new AgentProviderProfile("codebuddy-cli", "Offline xterm round trip", "codebuddy-cli", "", "",
            Path.Combine(AppContext.BaseDirectory, "Glimpse.Windows.Probe.exe"), "", 30, false);
        CodeBuddyLoginSession? session = null;
        int stops = 0, childId = 0;
        CodeBuddyLoginSession Current(Guid id) => session is { } current && current.Id == id ? current :
            throw new InvalidOperationException("Unexpected offline terminal session.");
        var window = new CodeBuddyLoginWindow(_ =>
        {
            session = CodeBuddyLoginSession.Start(profile);
            return Task.FromResult(session.Read());
        }, (id, _) => Task.FromResult(Current(id).Read()),
            (id, text, cancellation) => Current(id).SendAsync(text, cancellation),
            async (id, _) => { await Current(id).DisposeAsync(); session = null; stops++; },
            (id, columns, rows, _) => { Current(id).Resize(columns, rows); return Task.CompletedTask; });
        window.Show();
        try
        {
            await Ready(window);
            Click(window, "StartButton");
            var web = (WebView2)window.FindName("EmbeddedTerminal");
            await UntilAsync(async () => (await TerminalText(web)).Contains("LOGIN_FIXTURE_READY", StringComparison.Ordinal));
            string text = await TerminalText(web);
            Check(text.Contains("TTY=true", StringComparison.Ordinal), "native fixture attached to terminal");
            childId = int.Parse(text.Split("PID=", StringSplitOptions.None)[1].Split('\n')[0].Trim(),
                System.Globalization.CultureInfo.InvariantCulture);
            await web.CoreWebView2.ExecuteScriptAsync("window.glimpseTerminal.input('\\r')");
            await UntilAsync(async () => (await TerminalText(web)).Contains("KEYS_RECEIVED", StringComparison.Ordinal));
            Check(((ComboBox)window.FindName("Links")).Items.Cast<string>().Contains("https://example.test/verify?code=synthetic-only"),
                "native VT URL available alongside xterm");
        }
        finally
        {
            await window.StopAndCloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (session is not null) { await session.DisposeAsync(); session = null; }
        }
        Check(stops == 1 && session is null, "closing xterm stops owned native session");
        if (childId != 0)
        {
            try { using var child = Process.GetProcessById(childId); Check(child.HasExited, "native xterm fixture process terminated"); }
            catch (ArgumentException) { }
        }
    }

    private static void Click(Window window, string name)
    {
        var button = (Button)window.FindName(name);
        Check(button.IsEnabled, name + " enabled");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Check(bool condition, string label)
    { if (!condition) throw new InvalidOperationException("Login UI probe failed: " + label); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await condition()) await Task.Delay(20, timeout.Token);
    }
    private static async Task Ready(Window window)
    {
        await Until(() => ((Button)window.FindName("StartButton")).IsEnabled);
        Check(((FrameworkElement)window.FindName("FallbackControls")).Visibility == Visibility.Collapsed,
            "mature terminal ready (WebView2 Runtime required for this opt-in probe)");
    }
    private static async Task<string> TerminalText(WebView2 web) =>
        JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync("window.glimpseTerminal.text()")) ?? "";
    private static async Task RenderWindow(Window window, WebView2 web, string path)
    {
        window.UpdateLayout();
        var chrome = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        chrome.Render(window);
        using var terminalImage = new MemoryStream();
        await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, terminalImage);
        terminalImage.Position = 0;
        var terminal = new BitmapImage(); terminal.BeginInit(); terminal.CacheOption = BitmapCacheOption.OnLoad;
        terminal.StreamSource = terminalImage; terminal.EndInit();
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(chrome, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            Point origin = web.TransformToAncestor(window).Transform(new Point());
            drawing.DrawImage(terminal, new Rect(origin.X, origin.Y, web.ActualWidth, web.ActualHeight));
        }
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var output = File.Create(path); encoder.Save(output);
    }
}
