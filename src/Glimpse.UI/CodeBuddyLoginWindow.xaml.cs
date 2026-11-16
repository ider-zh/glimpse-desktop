using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Glimpse.Contracts;

namespace Glimpse.UI;

public partial class CodeBuddyLoginWindow : Window
{
    private readonly Func<CancellationToken, Task<CliLoginSnapshot>> start;
    private readonly Func<Guid, CancellationToken, Task<CliLoginSnapshot>> read;
    private readonly Func<Guid, string, CancellationToken, Task> input;
    private readonly Func<Guid, CancellationToken, Task> stop;
    private readonly Func<Guid, int, int, CancellationToken, Task>? resize;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly SemaphoreSlim transport = new(1);
    private Guid? loginId;
    private Task? starting;
    private Task? stopping;
    private Task? shutdown;
    private bool running, ending, allowClose, excluded, startPending, stopPending;
    private string[] links = [];
    private string lastRenderedStatus = "";

    public CodeBuddyLoginWindow(Func<CancellationToken, Task<CliLoginSnapshot>> start,
        Func<Guid, CancellationToken, Task<CliLoginSnapshot>> read,
        Func<Guid, string, CancellationToken, Task> input,
        Func<Guid, CancellationToken, Task> stop,
        Func<Guid, int, int, CancellationToken, Task>? resize = null)
    {
        this.start = start; this.read = read; this.input = input; this.stop = stop; this.resize = resize;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            try { Native.Exclude(this); excluded = true; }
            catch (InvalidOperationException)
            {
                StartButton.IsEnabled = false;
                LoginStatus.Text = "无法将登录窗口排除于截图，登录终端未启动。请关闭窗口后重试。";
            }
        };
        timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) => await InitializeTerminalAsync();
        Closing += OnClosing;
        Closed += async (_, _) =>
        {
            timer.Stop(); Terminal.Clear(); Input.Clear(); SelectedLink.Clear(); Links.ItemsSource = null; links = [];
            await DisposeTerminalAsync();
        };
    }

    private async void StartLogin(object sender, RoutedEventArgs e)
    {
        if (!excluded || !(terminalReady || fallbackReady) || ending || running || startPending) return;
        startPending = true;
        stopping = null;
        starting = StartAsync();
        await starting;
    }

    private async Task StartAsync()
    {
        StartButton.IsEnabled = false;
        outputOffset = 0; outputStarted = false; lastTerminalScreen = ""; lastRenderedStatus = ""; appliedColumns = appliedRows = 0;
        PostTerminal(new { type = "output", reset = true, text = "" });
        LoginStatus.Text = "正在启动本机登录终端…";
        try
        {
            // Receive the session ID even if the window is closing, then stop that exact
            // session. Canceling only the IPC waiter could leave a started CLI unowned.
            var snapshot = await start(CancellationToken.None);
            loginId = snapshot.LoginId;
            if (ending) return;
            Render(snapshot);
            await ResizeTerminalAsync();
            PostTerminal(new { type = "fit" });
            if (running) timer.Start();
        }
        catch (Exception ex)
        {
            LoginStatus.Text = $"登录终端未能启动（{ex.GetType().Name}）。请检查已保存的 CLI 路径和 Worker 连接。";
            running = false;
        }
        finally { startPending = false; UpdateControls(); }
    }

    private async Task RefreshAsync()
    {
        if (ending || stopPending || !running || loginId is not Guid id || !await transport.WaitAsync(0)) return;
        try { Render(await read(id, CancellationToken.None)); }
        catch (Exception ex)
        {
            timer.Stop(); running = false;
            LoginStatus.Text = $"终端连接中断（{ex.GetType().Name}）。可结束进程或关闭窗口后重试。";
        }
        finally { transport.Release(); UpdateControls(); await ResizeTerminalAsync(); }
    }

    private void Render(CliLoginSnapshot snapshot)
    {
        if (ending || stopPending || loginId != snapshot.LoginId) return;
        if (Terminal.Text != snapshot.ScreenText)
        {
            double horizontal = Terminal.HorizontalOffset, vertical = Terminal.VerticalOffset;
            Terminal.Text = snapshot.ScreenText;
            Terminal.ScrollToHorizontalOffset(horizontal); Terminal.ScrollToVerticalOffset(vertical);
        }
        string? selected = Links.SelectedItem as string;
        string[] latest = snapshot.Links.Where(value => ValidLink(value) is not null).Distinct().Take(20).ToArray();
        if (!links.SequenceEqual(latest))
        {
            links = latest; Links.ItemsSource = links;
            Links.SelectedItem = selected is not null && links.Contains(selected) ? selected : links.FirstOrDefault();
        }
        running = snapshot.Status == "running";
        if (!running) timer.Stop();
        if (lastRenderedStatus != snapshot.Status)
            LoginStatus.Text = running ? "按终端提示选择登录方式，并在浏览器中完成验证。登录状态请通过重新生成确认。" :
                $"登录进程已结束{(snapshot.ExitCode is int code ? $"（退出代码 {code}）" : "")}。这不代表认证成功；请关闭窗口，重新预览并生成验证。";
        lastRenderedStatus = snapshot.Status;
        RenderTerminal(snapshot);
        UpdateControls();
    }

    private async Task SendAsync(string text)
    {
        if (ending || stopPending || !running || loginId is not Guid id) return;
        await transport.WaitAsync();
        try
        {
            if (ending || stopPending || !running) return;
            KeyButtons.IsEnabled = InputPanel.IsEnabled = false;
            await input(id, text, CancellationToken.None);
        }
        catch (Exception ex) { LoginStatus.Text = $"终端输入未完成（{ex.GetType().Name}）。可重试或结束登录进程。"; }
        finally { transport.Release(); UpdateControls(); }
    }

    private async void SendKey(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        string value = key switch { "up" => "\u001b[A", "down" => "\u001b[B", "tab" => "\t", "enter" => "\r", "escape" => "\u001b", _ => "" };
        if (value.Length > 0) await SendAsync(value);
    }
    private async void SendText(object sender, RoutedEventArgs e) => await SendInputAsync();
    private async void InputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; await SendInputAsync();
    }
    private async Task SendInputAsync()
    {
        string text = Input.Text;
        Input.Clear();
        await SendAsync(text + "\r");
    }
    private async void StopLogin(object sender, RoutedEventArgs e) => await EndSessionAsync();
    private Task EndSessionAsync() => stopping is { IsCompleted: false } ? stopping : stopping = EndSessionCoreAsync();
    private async Task EndSessionCoreAsync()
    {
        stopPending = true; timer.Stop(); running = false; UpdateControls();
        await transport.WaitAsync();
        try
        {
            if (loginId is Guid id)
            {
                await stop(id, CancellationToken.None);
                loginId = null;
            }
            links = []; Links.ItemsSource = null; SelectedLink.Clear(); UpdateLinkControls();
            LoginStatus.Text = "登录进程已结束。若已在浏览器完成验证，请关闭窗口后重新预览并生成。";
        }
        catch (Exception ex)
        {
            LoginStatus.Text = $"无法确认登录进程已结束（{ex.GetType().Name}）。关闭 Glimpse 会结束其 Worker 和登录进程。";
        }
        finally { stopPending = false; transport.Release(); UpdateControls(); }
    }
    public Task StopAndCloseAsync() => shutdown ??= StopAndCloseCoreAsync();
    private async Task StopAndCloseCoreAsync()
    {
        ending = true; timer.Stop(); UpdateControls();
        if (starting is not null) await starting;
        await EndSessionAsync();
        allowClose = true; Close();
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        await StopAndCloseAsync();
    }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void UpdateControls()
    {
        StartButton.IsEnabled = excluded && (terminalReady || fallbackReady) && !ending && !running && loginId is null && !startPending && !stopPending;
        StopButton.IsEnabled = !ending && loginId is not null && !stopPending;
        KeyButtons.IsEnabled = InputPanel.IsEnabled = !ending && !stopPending && running;
        PasteButton.IsEnabled = terminalReady && !ending && !stopPending && running;
        CopySelectionButton.IsEnabled = terminalReady && !ending;
        PostTerminal(new { type = "enabled", enabled = !ending && !stopPending && running });
        UpdateLinkControls();
    }
    private void LinkSelected(object sender, SelectionChangedEventArgs e)
    { SelectedLink.Text = Links.SelectedItem as string ?? ""; UpdateLinkControls(); }
    private void UpdateLinkControls()
    {
        if (OpenLinkButton is null) return;
        OpenLinkButton.IsEnabled = CopyLinkButton.IsEnabled = !ending && ValidLink(SelectedLink.Text) is not null;
    }
    private static Uri? ValidLink(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;
    private void OpenLink(object sender, RoutedEventArgs e)
    {
        if (ValidLink(SelectedLink.Text) is not Uri uri) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { LoginStatus.Text = $"无法打开浏览器（{ex.GetType().Name}）。请复制所选链接后手动打开。"; }
    }
    private void CopyLink(object sender, RoutedEventArgs e)
    {
        if (ValidLink(SelectedLink.Text) is null) return;
        try { Clipboard.SetText(SelectedLink.Text); LoginStatus.Text = "已复制所选链接。验证链接可能含临时凭据，请只在自己的浏览器打开。"; }
        catch (ExternalException) { LoginStatus.Text = "剪贴板正被占用，请重试。"; }
    }
}
