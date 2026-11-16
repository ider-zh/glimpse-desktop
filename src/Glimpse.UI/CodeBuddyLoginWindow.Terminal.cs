using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Glimpse.Contracts;
using Microsoft.Web.WebView2.Core;

namespace Glimpse.UI;

public partial class CodeBuddyLoginWindow
{
    private const string TerminalHost = "glimpse-terminal.invalid";
    private const string TerminalPage = "https://glimpse-terminal.invalid/index.html";
    private readonly string terminalProfile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "glimpse-terminal-" + Guid.NewGuid().ToString("N")));
    private readonly TaskCompletionSource browserExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource terminalInitialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool terminalReady, fallbackReady, terminalDisposed, outputStarted;
    private long outputOffset;
    private int terminalColumns = 110, terminalRows = 32;
    private int appliedColumns, appliedRows;
    private string lastTerminalScreen = "";

    private async Task InitializeTerminalAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: terminalProfile);
            environment.BrowserProcessExited += (_, _) => browserExited.TrySetResult();
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            await EmbeddedTerminal.EnsureCoreWebView2Async(environment, options);
            if (ending || terminalDisposed) { await DisposeTerminalAsync(); return; }
            var core = EmbeddedTerminal.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(TerminalHost,
                Path.Combine(AppContext.BaseDirectory, "TerminalAssets"), CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += (_, args) => { if (!IsTerminalPage(args.Uri)) args.Cancel = true; };
            core.FrameNavigationStarting += (_, args) => args.Cancel = true;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                if (!IsTerminalResource(args.Request.Uri))
                    args.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
            };
            core.WebMessageReceived += OnTerminalMessage;
            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled && !ending)
                    UseTerminalFallback("本地终端页面未能载入。");
            };
            core.Navigate(TerminalPage);
            await terminalInitialized.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            if (!ending) UseTerminalFallback($"交互终端初始化未完成（{ex.GetType().Name}）。");
        }
    }

    private static bool IsTerminalPage(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.AbsoluteUri == TerminalPage;

    private static bool IsTerminalResource(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == TerminalHost &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath is
            "/index.html" or "/terminal.js" or "/terminal.css" or "/vendor/xterm.js" or "/vendor/xterm.css" or "/vendor/addon-fit.js";

    private async void OnTerminalMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (ending || fallbackReady || !IsTerminalPage(args.Source) || args.WebMessageAsJson.Length > 256 * 1024) return;
        try
        {
            using var json = JsonDocument.Parse(args.WebMessageAsJson);
            var value = json.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
            switch (type.GetString())
            {
                case "ready":
                    terminalReady = true; TerminalLoading.Visibility = Visibility.Collapsed;
                    terminalInitialized.TrySetResult();
                    LoginStatus.Text = "交互终端已就绪。点击“启动登录终端”后直接用键盘选择和确认。";
                    PostTerminal(new { type = "output", reset = true, text = "\u001b[38;2;125;226;208mGlimpse · CodeBuddy\u001b[0m\r\n\r\n点击上方“启动登录终端”开始登录。\r\n" });
                    UpdateControls();
                    break;
                case "input":
                    if (value.TryGetProperty("text", out var inputText) && inputText.ValueKind == JsonValueKind.String && inputText.GetString() is { Length: > 0 and <= 4096 } text)
                        await SendAsync(text);
                    break;
                case "resize":
                    if (value.TryGetProperty("columns", out var columns) && columns.TryGetInt32(out int cols) &&
                        value.TryGetProperty("rows", out var rows) && rows.TryGetInt32(out int rowCount) && cols is >= 20 and <= 240 && rowCount is >= 5 and <= 100)
                    { terminalColumns = cols; terminalRows = rowCount; await ResizeTerminalAsync(); }
                    break;
                case "copy":
                    if (value.TryGetProperty("text", out var copyText) && copyText.ValueKind == JsonValueKind.String && copyText.GetString() is { Length: > 0 and <= 128 * 1024 } selection)
                    { Clipboard.SetText(selection); LoginStatus.Text = "已复制终端选中文字。"; }
                    break;
                case "paste": PasteTerminalText(); break;
            }
        }
        catch (Exception ex)
        {
            if (!ending) LoginStatus.Text = $"终端操作未完成（{ex.GetType().Name}）。可重试或结束登录进程。";
        }
    }

    private async Task ResizeTerminalAsync()
    {
        if (resize is null || ending || stopPending || !running || loginId is not Guid id ||
            appliedColumns == terminalColumns && appliedRows == terminalRows || !await transport.WaitAsync(0)) return;
        int columns = terminalColumns, rows = terminalRows;
        try { await resize(id, columns, rows, CancellationToken.None); appliedColumns = columns; appliedRows = rows; }
        catch (Exception ex) { if (!ending) LoginStatus.Text = $"终端尺寸更新未完成（{ex.GetType().Name}）。"; }
        finally { transport.Release(); }
    }

    private void PostTerminal(object message)
    {
        if (!terminalReady || terminalDisposed || EmbeddedTerminal.CoreWebView2 is not { } core || !IsTerminalPage(core.Source)) return;
        try { core.PostWebMessageAsJson(JsonSerializer.Serialize(message)); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    private void RenderTerminal(CliLoginSnapshot snapshot)
    {
        if (!terminalReady) return;
        string raw = snapshot.TerminalOutput;
        long end = snapshot.OutputStart + raw.Length;
        if (raw.Length > 0 && end > outputOffset)
        {
            bool truncated = outputStarted && outputOffset < snapshot.OutputStart;
            int offset = (int)Math.Clamp(outputOffset - snapshot.OutputStart, 0, raw.Length);
            PostTerminal(new { type = "output", reset = !outputStarted || truncated, text = raw[offset..] });
            outputStarted = true; outputOffset = end;
            if (truncated) LoginStatus.Text = "终端输出超过内存保留范围，较早的显示记录已截断。";
        }
        else if (!outputStarted && raw.Length == 0 && snapshot.ScreenText.Length > 0 && lastTerminalScreen != snapshot.ScreenText)
        {
            // Compatibility with offline fixtures and an older Worker; normal CLI
            // sessions use the VT stream, preserving cursor movement and colors.
            PostTerminal(new { type = "output", reset = true, text = snapshot.ScreenText.Replace("\n", "\r\n") });
        }
        lastTerminalScreen = snapshot.ScreenText;
    }

    private void UseTerminalFallback(string reason)
    {
        if (ending) return;
        terminalReady = false; fallbackReady = true;
        EmbeddedTerminal.Visibility = Visibility.Collapsed; TerminalLoading.Visibility = Visibility.Collapsed;
        Terminal.Visibility = FallbackControls.Visibility = Visibility.Visible;
        TerminalToolbar.Visibility = Visibility.Collapsed;
        LoginStatus.Text = reason + "可使用基本模式，或安装 WebView2 Runtime 后重试。";
        UpdateControls();
    }
    private void ChangeFont(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string delta } && int.TryParse(delta, out int change)) PostTerminal(new { type = "font", delta = Math.Clamp(change, -1, 1) }); }
    private void CopySelection(object sender, RoutedEventArgs e) => PostTerminal(new { type = "copySelection" });
    private void PasteTerminal(object sender, RoutedEventArgs e) => PasteTerminalText();
    private void PasteTerminalText()
    {
        if (!terminalReady || ending || stopPending || !running) return;
        try
        {
            string text = Clipboard.GetText();
            if (text.Length > 4096) { LoginStatus.Text = "一次最多粘贴 4096 个字符。请分段粘贴。"; return; }
            PostTerminal(new { type = "paste", text });
        }
        catch (System.Runtime.InteropServices.ExternalException) { LoginStatus.Text = "剪贴板正被占用，请重试。"; }
    }
    private void InstallRuntime(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/#download-section") { UseShellExecute = true }); }
        catch (Exception ex) { LoginStatus.Text = $"无法打开下载页面（{ex.GetType().Name}）。请手动安装 Microsoft Edge WebView2 Runtime。"; }
    }

    private async Task DisposeTerminalAsync()
    {
        if (terminalDisposed) return;
        terminalDisposed = true; terminalReady = false;
        EmbeddedTerminal.Dispose();
        try { await browserExited.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { }
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!terminalProfile.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(terminalProfile).StartsWith("glimpse-terminal-", StringComparison.Ordinal)) return;
        try { if (Directory.Exists(terminalProfile)) Directory.Delete(terminalProfile, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
