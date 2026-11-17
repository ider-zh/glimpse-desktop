using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Glimpse.Contracts;

namespace Glimpse.UI;

public partial class DockedToolbarWindow
{
    private Guid? sessionId;
    private bool generating, resultShown;
    private long generationVersion;
    private CancellationTokenSource? generationCancellation;
    private AgentResult? displayedAnswer;
    private string currentTask = "custom";
    private DispatcherTimer? copyNoticeTimer;

    public void SetSession(Guid? id)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetSession(id)); return; }
        if (sessionId == id) return;
        generationVersion++;
        generationCancellation?.Cancel(); generationCancellation = null;
        generating = false; sessionId = id; hasText = false;
        ClearAgentResults(); ClearPreview();
        SetAgentStatus(id is null ? "打开或开始一段阅读后使用助手。" : "先记录一些文字，再点击任务或发送问题。");
        UpdateAgentControls();
    }

    public void SetPreview(AgentContextPreview preview)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetPreview(preview)); return; }
        if (closed || ownerClosing || sessionId != preview.SessionId || !generating || generationCancellation?.IsCancellationRequested != false) return;
        PreviewSummary.Text = $"修订 {preview.DocumentRevision} · {preview.SelectedBlockIds.Count} 块 · 约 {preview.EstimatedTokens} tokens · 过滤 {preview.RedactionCount} 处敏感模式";
        PreviewText.Text = preview.SystemPrompt + "\n\n" + preview.UserPrompt;
        PreviewExpander.Visibility = Visibility.Visible;
    }

    public void ShowStoredResult(AgentResult result)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowStoredResult(result)); return; }
        if (closed || ownerClosing || generating || resultShown || result.SessionId != sessionId || result.Status is not ("completed" or "fallback")) return;
        RenderAgentResult(result, restored: true);
    }

    public void CancelGeneration()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(CancelGeneration); return; }
        generationCancellation?.Cancel();
        if (generating && !closed) SetAgentStatus($"正在取消{TaskLabel(currentTask)}…");
        UpdateAgentControls();
    }

    private async void QuickGenerate(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string task }) await GenerateInlineAsync(task, PromptInput.Text);
    }
    private async void SendPrompt(object sender, RoutedEventArgs e) => await SendCustomAsync();
    private async Task SendCustomAsync()
    {
        if (string.IsNullOrWhiteSpace(PromptInput.Text))
        { SetAgentStatus("请先输入要问的问题。", true); PromptInput.Focus(); return; }
        await GenerateInlineAsync("custom", PromptInput.Text);
    }
    private async Task GenerateInlineAsync(string task, string prompt)
    {
        if (closed || ownerClosing || userClosing || generating || !hasText || sessionId is not Guid requestedSession || GenerateAsync is not { } callback) return;
        long version = ++generationVersion;
        using var cancellation = new CancellationTokenSource();
        generationCancellation = cancellation; generating = true; currentTask = task;
        ClearCopyNotice(); MarkPreviousAnswer(); ClearPreview();
        SetAgentStatus(BusyStatus(task)); UpdateAgentControls();
        Task<AgentResult>? pending = null;
        try
        {
            pending = callback(task, prompt, cancellation.Token);
            var result = await pending.WaitAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (version != generationVersion || sessionId != requestedSession || result.SessionId != requestedSession || closed || ownerClosing) return;
            RenderAgentResult(result);
        }
        catch (OperationCanceledException)
        {
            if (version == generationVersion && !closed && !ownerClosing)
            { MarkPreviousAnswer(); SetAgentStatus($"本次{TaskLabel(task)}已取消" + RetainedAnswerSuffix()); }
        }
        catch (Exception ex)
        {
            if (version == generationVersion && !closed && !ownerClosing)
            {
                MarkPreviousAnswer();
                string detail = ex is InvalidOperationException ? ex.Message : $"请检查设置后重试（{ex.GetType().Name}）。";
                SetAgentStatus($"本次{TaskLabel(task)}失败。{detail}" + (displayedAnswer is null ? "" : " 上次结果已保留。"), true);
            }
        }
        finally
        {
            if (pending is { IsCompleted: false })
                _ = pending.ContinueWith(late => _ = late.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (version == generationVersion)
            {
                generationCancellation = null; generating = false;
                UpdateAgentControls();
            }
        }
    }

    private void RenderAgentResult(AgentResult result, bool restored = false)
    {
        string task = TaskLabel(result.TaskType);
        if (result.Status is not ("completed" or "fallback"))
        {
            MarkPreviousAnswer();
            SetAgentStatus(result.Status switch
            {
                "canceled" => $"本次{task}已取消" + RetainedAnswerSuffix(),
                "timeout" => $"本次{task}超时" + RetainedAnswerSuffix() + "可重试或在设置中调整等待上限。",
                _ => $"本次{task}失败。{result.Error ?? "请检查设置后重试。"}" + (displayedAnswer is null ? "" : " 上次结果已保留。")
            }, result.Status is "error" or "timeout");
            return;
        }
        ClearAgentResults();
        displayedAnswer = result;
        resultShown = true;
        ResultCards.ItemsSource = result.Cards;
        string provider = result.ProviderId switch { "codebuddy-cli" => "CodeBuddy CLI", "deepseek" => "DeepSeek", "xiaomi-mimo" => "Xiaomi MiMo", _ => result.ProviderId };
        ResultSummary.Text = $"{task} · {provider} · 修订 {result.DocumentRevision} · {result.ElapsedMs / 1000d:0.0} 秒" +
            (restored ? " · 已保存的结果" : "");
        ResultWarnings.Text = string.Join("\n", result.Warnings.Distinct());
        if (result.Status == "fallback" && result.FallbackText is not null)
        { FallbackText.Text = result.FallbackText; FallbackPanel.Visibility = Visibility.Visible; }
        PreviousResultNotice.Visibility = restored ? Visibility.Visible : Visibility.Collapsed;
        SetAgentStatus(restored ? $"已恢复上次{task}，可直接复制。" : result.Status switch
        {
            "completed" => $"{task}已生成，可复制结果。",
            _ => $"本次{task}的格式未通过校验，请检查原始文字。"
        }, result.Status == "fallback");
        if (!restored) RevealNewAnswer(result);
    }
    private void ClearAgentResults()
    {
        ResultCards.ItemsSource = null; ResultSummary.Text = ""; ResultWarnings.Text = "";
        FallbackText.Clear(); FallbackPanel.Visibility = Visibility.Collapsed; resultShown = false; displayedAnswer = null;
        PreviousResultNotice.Visibility = Visibility.Collapsed;
        ClearCopyNotice();
    }
    private void ClearPreview()
    {
        PreviewText.Clear(); PreviewSummary.Text = "";
        PreviewExpander.Visibility = Visibility.Collapsed; PreviewExpander.IsExpanded = false;
    }
    private void UpdateAgentControls()
    {
        if (SendButton is null) return;
        bool canGenerate = !closed && !ownerClosing && !userClosing && !generating && hasText && sessionId is not null && GenerateAsync is not null;
        CommentButton.IsEnabled = ReplyButton.IsEnabled = SummaryButton.IsEnabled = AdviceButton.IsEnabled = SendButton.IsEnabled = canGenerate;
        PromptInput.IsEnabled = !generating;
        SettingsButton.IsEnabled = !generating;
        CancelButton.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = generating && generationCancellation?.IsCancellationRequested == false;
    }
    private void SetAgentStatus(string message, bool problem = false)
    {
        AgentStatus.Text = message;
        AgentStatus.Foreground = problem ? new SolidColorBrush(Color.FromRgb(155, 103, 9)) : new SolidColorBrush(Color.FromRgb(97, 115, 135));
    }
    private void PromptEdited(object sender, TextChangedEventArgs e)
    {
        if (AgentStatus is not null && !generating && !resultShown && hasText) SetAgentStatus("点击任务，或发送自定义问题。");
    }
    private async void PromptKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true; await SendCustomAsync();
    }
    private void CancelRequest(object sender, RoutedEventArgs e) => CancelGeneration();
    private void OpenSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private async void CopyCard(object sender, RoutedEventArgs e)
    { if (sender is Button { Tag: string text }) await CopyResultTextAsync(text); }
    private async void CopyFallback(object sender, RoutedEventArgs e) => await CopyResultTextAsync(FallbackText.Text);
    private async Task CopyResultTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        Guid? copiedSession = sessionId;
        long copiedVersion = generationVersion;
        ShowCopyNotice("正在复制…");
        bool copied = await ClipboardCopy.TrySetTextAsync(text);
        if (closed || ownerClosing || sessionId != copiedSession || generationVersion != copiedVersion) return;
        ShowCopyNotice(copied ? "已复制文字。" : "剪贴板正被占用，请重试复制。");
    }

    private void MarkPreviousAnswer() => PreviousResultNotice.Visibility = displayedAnswer is null ? Visibility.Collapsed : Visibility.Visible;
    private string RetainedAnswerSuffix() => displayedAnswer is null ? "。" : "，上次结果已保留。";
    private static string TaskLabel(string task) => task switch
    { "comment" => "评论", "reply" => "回复", "summary" => "总结", "analysis" => "建议", _ => "回答" };
    private static string BusyStatus(string task) => task switch
    { "comment" => "正在生成评论…", "reply" => "正在生成回复…", "summary" => "正在总结…", "analysis" => "正在生成建议…", _ => "正在回答问题…" };

    private void ShowCopyNotice(string text)
    {
        CopyStatus.Text = text; CopyStatus.Visibility = Visibility.Visible;
        copyNoticeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        copyNoticeTimer.Stop();
        copyNoticeTimer.Tick -= HideCopyNotice;
        copyNoticeTimer.Tick += HideCopyNotice;
        copyNoticeTimer.Start();
    }
    private void HideCopyNotice(object? sender, EventArgs e) => ClearCopyNotice();
    private void ClearCopyNotice()
    {
        copyNoticeTimer?.Stop(); CopyStatus.Text = ""; CopyStatus.Visibility = Visibility.Collapsed;
    }
    private void RevealNewAnswer(AgentResult result)
    {
        long version = generationVersion;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (closed || ownerClosing || generationVersion != version || displayedAnswer?.PreviewId != result.PreviewId) return;
            ResultCards.UpdateLayout();
            FrameworkElement? container = result.Status == "fallback" ? FallbackPanel :
                ResultCards.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            if (container is not null) (FirstHeading(container) ?? container).BringIntoView();
        });
    }
    private static FrameworkElement? FirstHeading(DependencyObject parent)
    {
        if (parent is TextBlock text) return text;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = FirstHeading(VisualTreeHelper.GetChild(parent, index));
            if (child is not null) return child;
        }
        return null;
    }
}
