using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Glimpse.Contracts;

namespace Glimpse.UI;

public partial class AgentPanel : UserControl
{
    public Func<CancellationToken, Task<AgentSettings>>? LoadSettingsAsync { get; set; }
    public Func<AgentProviderUpdate, CancellationToken, Task<AgentSettings>>? SaveSettingsAsync { get; set; }
    public Func<AgentTaskRequest, CancellationToken, Task<AgentContextPreview>>? PreviewAsync { get; set; }
    public Func<Guid, CancellationToken, Task<AgentResult>>? GenerateAsync { get; set; }

    private AgentSettings? settings;
    private AgentContextPreview? preview;
    private Guid? sessionId;
    private int documentRevision;
    private bool sessionHasText;
    private string taskType = "summary";
    private bool loading = true, busy, settingsDirty, operationIsSessionBound;
    private long operationVersion;
    private CancellationTokenSource? operationCancellation;

    public AgentPanel()
    {
        InitializeComponent();
        loading = false;
        UpdateControls();
        Loaded += async (_, _) =>
        {
            if (settings is null && !busy && LoadSettingsAsync is not null) await ReloadSettingsAsync();
        };
    }

    public void SetSession(SessionData? session)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetSession(session)); return; }
        bool switched = sessionId != session?.Id;
        if (switched)
        {
            if (operationIsSessionBound) CancelPending();
            InvalidatePreview();
            ClearResults();
            if (!busy) SetStatus(session is null ? "打开已有文字的会话后再预览。" : "已切换会话，请重新预览上下文。");
        }
        sessionId = session?.Id;
        documentRevision = session?.Revision ?? 0;
        sessionHasText = session?.Blocks.Count > 0;
        SessionSummary.Text = session is null ? "打开一个已有文字的会话后开始。" :
            $"{session.Title} · 修订 {session.Revision} · {session.Blocks.Count} 块文字";
        UpdatePreviewSummary();
        UpdateControls();
    }

    public void SetSettings(AgentSettings value)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetSettings(value)); return; }
        loading = true;
        settings = value;
        Providers.ItemsSource = value.Providers;
        Providers.SelectedItem = value.Providers.FirstOrDefault(p => p.Id == value.SelectedProviderId) ?? value.Providers.FirstOrDefault();
        LoadSelectedProfile();
        loading = false;
        settingsDirty = false;
        SettingsNotice.Text = "设置已保存；留空密钥输入框会保留已保存的密钥。";
        var selected = value.Providers.FirstOrDefault(p => p.Id == value.SelectedProviderId);
        CurrentEngine.Text = selected is null ? "尚未配置引擎" : $"当前：{selected.DisplayName}";
        SettingsExpander.Header = selected is null ? "引擎设置" : $"引擎设置 · {selected.DisplayName}";
        if (selected is null || selected.Kind == "openai-chat" && !selected.HasApiKey ||
            selected.Kind == "codebuddy-cli" && string.IsNullOrWhiteSpace(selected.CliPath)) SettingsExpander.IsExpanded = true;
        InvalidatePreview();
        UpdateControls();
    }

    public Task ReloadSettingsAsync()
    {
        if (busy) return Task.CompletedTask;
        return RunOperationAsync("正在读取引擎设置…", async cancellation =>
        {
            var callback = LoadSettingsAsync ?? throw new InvalidOperationException("Agent backend is not connected.");
            var value = await callback(cancellation);
            cancellation.ThrowIfCancellationRequested();
            SetSettings(value);
            SetStatus("引擎设置已读取。选择任务并预览上下文。");
        }, sessionBound: false);
    }

    public void CancelPending()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(CancelPending); return; }
        operationVersion++;
        operationCancellation?.Cancel();
        operationCancellation = null;
        busy = false;
        UpdateControls();
    }

    private void LoadSelectedProfile()
    {
        if (Providers.SelectedItem is not AgentProviderProfile profile) return;
        bool previous = loading;
        loading = true;
        BaseUrl.Text = profile.BaseUrl;
        Model.Text = profile.Model;
        CliPath.Text = profile.CliPath;
        CliHostPath.Text = profile.CliHostPath;
        Timeout.Text = profile.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        ContextBudget.Text = (settings?.ContextCharacterBudget ?? 12000).ToString(CultureInfo.InvariantCulture);
        ApiKey.Clear();
        bool isCli = profile.Kind == "codebuddy-cli";
        ApiFields.Visibility = isCli ? Visibility.Collapsed : Visibility.Visible;
        CliFields.Visibility = isCli ? Visibility.Visible : Visibility.Collapsed;
        ClearKeyButton.Visibility = isCli ? Visibility.Collapsed : Visibility.Visible;
        ClearKeyButton.IsEnabled = profile.HasApiKey;
        KeyStatus.Text = profile.HasApiKey ? "已保存密钥；不会回显。留空可继续使用。" : "未保存密钥；需要鉴权的服务请先配置。";
        loading = previous;
    }

    private void ProviderSelected(object sender, SelectionChangedEventArgs e)
    {
        if (loading) return;
        LoadSelectedProfile();
        settingsDirty = Providers.SelectedItem is AgentProviderProfile p && p.Id != settings?.SelectedProviderId;
        SettingsNotice.Text = settingsDirty ? "保存后切换到所选引擎。" : "当前引擎已保存。";
        InvalidatePreview();
        UpdateControls();
    }

    private void SettingsChanged(object sender, TextChangedEventArgs e) => MarkSettingsChanged();
    private void KeyChanged(object sender, RoutedEventArgs e) => MarkSettingsChanged();
    private void MarkSettingsChanged()
    {
        if (loading) return;
        settingsDirty = true;
        SettingsNotice.Text = "有未保存的设置。请先保存，再预览和生成。";
        InvalidatePreview();
        UpdateControls();
    }

    private AgentProviderProfile? ReadProfile()
    {
        if (Providers.SelectedItem is not AgentProviderProfile profile) { SetStatus("请选择一个引擎。", true); return null; }
        if (!int.TryParse(Timeout.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int timeout) || timeout is < 5 or > 180)
        { SetStatus("等待上限请填写 5–180 秒。", true); return null; }
        if (!int.TryParse(ContextBudget.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int budget) || budget is < 1024 or > 24000)
        { SetStatus("上下文字数上限请填写 1024–24000。", true); return null; }
        return profile with
        {
            BaseUrl = BaseUrl.Text.Trim(),
            Model = Model.Text.Trim(),
            CliPath = CliPath.Text.Trim(),
            CliHostPath = CliHostPath.Text.Trim(),
            TimeoutSeconds = timeout
        };
    }

    private async void SaveSettings(object sender, RoutedEventArgs e)
    {
        var profile = ReadProfile();
        if (profile is null || busy) return;
        int budget = int.Parse(ContextBudget.Text, CultureInfo.InvariantCulture);
        // The new secret is a one-time callback argument only: never bind, retain or log it.
        string newKey = ApiKey.Password;
        loading = true; ApiKey.Clear(); loading = false;
        await RunOperationAsync("正在保存引擎设置…", async cancellation =>
        {
            var callback = SaveSettingsAsync ?? throw new InvalidOperationException("Agent backend is not connected.");
            var saved = await callback(new(profile.Id, profile, string.IsNullOrEmpty(newKey) ? null : newKey,
                ContextCharacterBudget: budget), cancellation);
            cancellation.ThrowIfCancellationRequested();
            SetSettings(saved);
            SettingsExpander.IsExpanded = false;
            SetStatus("引擎已保存。现在可预览上下文。");
        }, sessionBound: false);
    }

    private async void ClearKey(object sender, RoutedEventArgs e)
    {
        if (busy || Providers.SelectedItem is not AgentProviderProfile profile) return;
        loading = true; ApiKey.Clear(); loading = false;
        await RunOperationAsync("正在清除所选引擎的密钥…", async cancellation =>
        {
            var callback = SaveSettingsAsync ?? throw new InvalidOperationException("Agent backend is not connected.");
            var saved = await callback(new(settings?.SelectedProviderId ?? profile.Id, profile, ClearApiKey: true,
                ContextCharacterBudget: settings?.ContextCharacterBudget ?? 12000), cancellation);
            cancellation.ThrowIfCancellationRequested();
            SetSettings(saved);
            SetStatus("所选引擎的密钥已清除。");
        }, sessionBound: false);
    }

    private async void ReloadSettings(object sender, RoutedEventArgs e) => await ReloadSettingsAsync();

    private void TaskSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string value }) return;
        taskType = value;
        InvalidatePreview();
        UpdateControls();
    }
    private void PromptChanged(object sender, TextChangedEventArgs e)
    {
        if (loading) return;
        InvalidatePreview();
        UpdateControls();
    }

    private async void PreviewContext(object sender, RoutedEventArgs e)
    {
        if (busy || sessionId is not Guid id || settings is null || settingsDirty) return;
        if (taskType == "custom" && string.IsNullOrWhiteSpace(Prompt.Text))
        { SetStatus("请输入想问的问题。", true); return; }
        var selected = settings.Providers.FirstOrDefault(p => p.Id == settings.SelectedProviderId);
        var request = new AgentTaskRequest(id, taskType, Prompt.Text, settings.ContextCharacterBudget, selected?.TimeoutSeconds ?? 60);
        await RunOperationAsync("正在整理并过滤会话上下文…", async cancellation =>
        {
            var callback = PreviewAsync ?? throw new InvalidOperationException("Agent backend is not connected.");
            var value = await callback(request, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (sessionId != value.SessionId) return;
            preview = value;
            PreviewText.Text = value.SystemPrompt + "\n\n" + value.UserPrompt;
            PreviewExpander.Visibility = Visibility.Visible;
            PreviewExpander.IsExpanded = true;
            UpdatePreviewSummary();
            SetStatus("请检查上方内容。点击“生成”后才会发送给所选引擎。");
        });
    }

    private async void Generate(object sender, RoutedEventArgs e)
    {
        if (busy || preview is null || settingsDirty) return;
        Guid previewId = preview.PreviewId;
        ClearResults();
        try
        {
            await RunOperationAsync("正在生成；你可以随时取消…", async cancellation =>
            {
                var callback = GenerateAsync ?? throw new InvalidOperationException("Agent backend is not connected.");
                var result = await callback(previewId, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (sessionId != result.SessionId || preview?.PreviewId != result.PreviewId) return;
                RenderResult(result);
            });
        }
        finally
        {
            // A preview authorizes one attempt only, including failed/canceled attempts.
            // Keep its text inspectable but never resend its consumed ID.
            if (preview?.PreviewId == previewId)
            {
                preview = null;
                PreviewExpander.IsExpanded = false;
                PreviewSummary.Text += " · 已使用，请重新预览后再生成";
                TaskStatus.Text += " 再次生成前请重新预览。";
                UpdateControls();
                if (Cards.Items.Count > 0 || FallbackPanel.Visibility == Visibility.Visible)
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (Cards.Items.Count > 0) Cards.BringIntoView();
                        else FallbackPanel.BringIntoView();
                    });
            }
        }
    }

    public void ShowStoredResult(AgentResult result)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowStoredResult(result)); return; }
        if (sessionId != result.SessionId || result.Status is not ("completed" or "fallback") ||
            busy || preview is not null || !string.IsNullOrEmpty(ResultSummary.Text)) return;
        InvalidatePreview();
        ClearResults();
        RenderResult(result, restored: true);
        UpdateControls();
    }

    private void RenderResult(AgentResult result, bool restored = false)
    {
        var provider = settings?.Providers.FirstOrDefault(p => p.Id == result.ProviderId);
        string providerLabel = provider?.DisplayName ?? result.ProviderId;
        // Stored results may predate a model-setting change, so do not attach today's
        // configured model name to a result that does not contain historical model data.
        string model = restored || string.IsNullOrWhiteSpace(provider?.Model) ? "" : $" · {provider.Model}";
        ResultSummary.Text = $"{providerLabel}{model} · 修订 {result.DocumentRevision} · {result.ElapsedMs / 1000d:0.0} 秒" +
            (restored ? " · 已保存的结果" : "");
        ResultWarnings.Text = string.Join("\n", result.Warnings.Distinct());
        Cards.ItemsSource = result.Cards;
        if (result.Status == "fallback" && result.FallbackText is not null)
        {
            FallbackText.Text = result.FallbackText;
            FallbackPanel.Visibility = Visibility.Visible;
        }
        SetStatus(restored ? "已恢复上次结果，可直接复制。再次生成前请重新预览。" : result.Status switch
        {
            "completed" => "已生成。复制后可自行修改；不会自动发布或发送。",
            "fallback" => "回答格式未通过校验，请检查下方原始文字。",
            "canceled" => "已取消生成。",
            "timeout" => "生成超时。可以重试或调整等待上限。",
            _ => result.Error ?? "生成未完成，请检查引擎设置后重试。"
        }, result.Status is "error" or "timeout" or "fallback");
    }

    private void Cancel(object sender, RoutedEventArgs e)
    {
        operationCancellation?.Cancel();
        SetStatus("正在取消…");
        CancelButton.IsEnabled = false;
    }

    private async Task RunOperationAsync(string message, Func<CancellationToken, Task> action, bool sessionBound = true)
    {
        if (busy) return;
        long version = ++operationVersion;
        using var cancellation = new CancellationTokenSource();
        operationCancellation = cancellation;
        operationIsSessionBound = sessionBound;
        busy = true;
        SetStatus(message);
        UpdateControls();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { if (version == operationVersion) SetStatus("已取消。"); }
        catch (Exception ex)
        {
            if (version == operationVersion) SetStatus($"操作未完成（{ex.GetType().Name}）。请检查引擎设置或重试。", true);
        }
        finally
        {
            if (version == operationVersion)
            {
                operationCancellation = null;
                operationIsSessionBound = false;
                busy = false;
                UpdateControls();
            }
        }
    }

    private void InvalidatePreview()
    {
        preview = null;
        if (PreviewExpander is null) return;
        PreviewText.Clear();
        PreviewExpander.Visibility = Visibility.Collapsed;
    }
    private void UpdatePreviewSummary()
    {
        if (preview is null) return;
        PreviewSummary.Text = $"修订 {preview.DocumentRevision} · {preview.SelectedBlockIds.Count} 块文字 · 约 {preview.EstimatedTokens} tokens · 已过滤 {preview.RedactionCount} 处敏感模式";
        var warnings = preview.Warnings.ToList();
        if (documentRevision != preview.DocumentRevision) warnings.Insert(0, "会话已有新记录；本次生成仍使用这份已预览的上下文。需要新内容时请重新预览。");
        warnings.Add("敏感内容过滤不能保证全部识别，请自行检查后再生成。");
        PreviewWarnings.Text = string.Join("\n", warnings.Distinct());
    }
    private void ClearResults()
    {
        Cards.ItemsSource = null;
        ResultSummary.Text = ""; ResultWarnings.Text = "";
        FallbackText.Clear(); FallbackPanel.Visibility = Visibility.Collapsed;
    }
    private void UpdateControls()
    {
        if (PreviewButton is null) return;
        SettingsFields.IsEnabled = !busy;
        TaskFields.IsEnabled = !busy;
        PreviewButton.IsEnabled = !busy && !settingsDirty && settings is not null && sessionId is not null && sessionHasText && PreviewAsync is not null;
        GenerateButton.IsEnabled = !busy && !settingsDirty && preview is not null && GenerateAsync is not null;
        CancelButton.IsEnabled = busy && operationIsSessionBound;
    }
    private void SetStatus(string message, bool problem = false)
    {
        TaskStatus.Text = message;
        TaskStatus.Foreground = problem ? new SolidColorBrush(Color.FromRgb(155, 103, 9)) : new SolidColorBrush(Color.FromRgb(97, 115, 135));
    }
    private void CopyText(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text }) Copy(text);
    }
    private void CopyFallback(object sender, RoutedEventArgs e) => Copy(FallbackText.Text);
    private void Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); SetStatus("已复制文字。不会自动发送。"); }
        catch (ExternalException) { SetStatus("剪贴板正被其他应用使用，请重试复制。", true); }
    }
}
