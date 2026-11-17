using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Glimpse.Contracts;
using Microsoft.Win32;

namespace Glimpse.UI;

public partial class MainWindow : Window
{
    private readonly WorkerClient worker = new();
    private LensWindow? lens;
    private DockedToolbarWindow? toolbar;
    private ObservationToolbarWindow? observationToolbar;
    private CodeBuddyLoginWindow? loginWindow;
    private SessionData? current;
    private PixelRect rect = new(100, 160, 700, 420);
    private bool observing, closing, excluded;
    private long generation;
    private readonly DispatcherTimer rectTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool resumeAfterMove;
    private bool loadingLanguages;
    private readonly Func<PixelRect?>? regionSelection;
    private readonly CancellationTokenSource shutdown = new();
    private TaskCompletionSource<bool> workerConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool workerConnected, selectingRegion, closingObservation, lensMoving, started;
    public bool IsObserving => observing;
    public PixelRect? SelectedRegion => lens?.CaptureRect;
    public DockedToolbarWindow? DockedToolbar => toolbar;
    public ObservationToolbarWindow? ObservationToolbar => observationToolbar;
    public MainWindow(bool selectOnStartup = false, Func<PixelRect?>? regionSelection = null)
    {
        this.regionSelection = regionSelection;
        InitializeComponent();
        UpdateViewLayout();
        AgentView.LoadSettingsAsync = async cancellation =>
            (await worker.RequestAsync("agent-settings-get", new { }, cancellation: cancellation)).Read<AgentSettings>();
        AgentView.SaveSettingsAsync = async (update, cancellation) =>
            (await worker.RequestAsync("agent-settings-save", update, cancellation: cancellation)).Read<AgentSettings>();
        AgentView.PreviewAsync = async (request, cancellation) =>
            (await worker.RequestAsync("agent-preview", request, cancellation: cancellation)).Read<AgentContextPreview>();
        AgentView.GenerateAsync = async (previewId, cancellation) =>
        {
            using var registration = cancellation.Register(() => _ = CancelAgentAsync(previewId));
            return (await worker.RequestAsync("agent-run", new { previewId }, TimeSpan.FromMinutes(4), cancellation)).Read<AgentResult>();
        };
        AgentView.LoginAsync = OpenCodeBuddyLoginAsync;
        AgentView.LoadModelsAsync = async cancellation =>
            (await worker.RequestAsync("agent-models-get", new { }, TimeSpan.FromSeconds(50), cancellation)).Read<AgentModelCatalog>();
        SourceInitialized += (_, _) =>
        {
            try { Native.Exclude(this); excluded = true; }
            catch (Exception ex) { ShowError(ex.Message); }
        };
        worker.ConnectionChanged += message => Dispatcher.BeginInvoke(() =>
        {
            workerConnected = false;
            resumeAfterMove = false; lensMoving = false; rectTimer.Stop();
            if (workerConnection.Task.IsCompleted) workerConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
            observing = false; ObserveButton.Content = "开始观察";
            Status.Text = message; StateDot.Fill = Brushes.IndianRed;
            UpdateToolbar();
            if (!selectingRegion && !closing && !IsVisible && toolbar is null) Show();
        });
        worker.Received += envelope => Dispatcher.BeginInvoke(async () =>
        {
            try { await Receive(envelope); }
            catch (Exception ex) { ShowError($"{ex.GetType().Name}：无法完成操作，请重试。"); }
        });
        rectTimer.Tick += async (_, _) =>
        {
            rectTimer.Stop();
            if (current is null) return;
            if (await Send("rect", new RectRequest(rect)) && resumeAfterMove && !lensMoving)
            {
                resumeAfterMove = false;
                await Send("observe", new { });
            }
        };
        Loaded += async (_, _) =>
        {
            if (started) return;
            started = true;
            worker.Start();
            if (selectOnStartup) await SelectObservationRegionAsync(startNewSession: true);
        };
        Closing += OnClosing;
    }
    private async Task Receive(Envelope envelope)
    {
        switch (envelope.MessageType)
        {
            case "ready":
                workerConnected = true;
                generation = 0;
                loadingLanguages = true;
                var languages = envelope.Payload.GetProperty("languages").EnumerateArray().Select(l => l.GetString()!).ToList();
                OcrLanguages.ItemsSource = languages;
                string? preferred = envelope.Payload.GetProperty("language").GetString();
                OcrLanguages.SelectedItem = languages.Find(l => l == preferred) ??
                    languages.Find(l => l.StartsWith(System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase)) ?? languages.FirstOrDefault();
                var modes = new[] { new OcrMode(0, "自动 · 小字增强"), new OcrMode(1, "原始尺寸"), new OcrMode(2, "固定 2 倍增强") };
                OcrModes.ItemsSource = modes;
                int preferredScale = envelope.Payload.TryGetProperty("scale", out var scaleValue) ? scaleValue.GetInt32() : 0;
                OcrModes.SelectedItem = modes.FirstOrDefault(m => m.Scale == preferredScale) ?? modes[0];
                loadingLanguages = false;
                if (OcrLanguages.SelectedItem is string selectedLanguage) await Send("language", new { language = selectedLanguage });
                if (OcrModes.SelectedItem is OcrMode mode) await Send("ocr-mode", new { scale = mode.Scale });
                Status.Text = "Worker 已连接。选择区域后开始观察。";
                StateDot.Fill = Brushes.SlateGray;
                await Send("list", new { });
                await AgentView.ReloadSettingsAsync();
                if (current is not null) await Send("resume", new SessionRequest(current.Id));
                workerConnection.TrySetResult(true);
                UpdateToolbar();
                break;
            case "sessions":
                Guid? selected = (Sessions.SelectedItem as SessionSummary)?.Id ?? current?.Id;
                var list = envelope.Read<List<SessionSummary>>();
                Sessions.ItemsSource = list;
                Sessions.SelectedItem = list.Find(s => s.Id == selected) ?? list.FirstOrDefault();
                break;
            // Correlated request errors are handled by their caller. In particular, an
            // Agent configuration error must not mislabel a still-running Reader paused.
            case "error": break;
            case "update":
                var update = envelope.Read<ReaderUpdate>();
                if (update.Generation < generation) return;
                generation = update.Generation;
                if (update.Session is not null)
                {
                    bool switched = current?.Id != update.Session.Id;
                    current = update.Session;
                    if (switched) { rect = current.Rect; if (lens is not null) lens.Restore(rect); }
                    Render();
                    if (switched)
                    {
                        Guid restoredId = current.Id;
                        var history = (await worker.RequestAsync("agent-history", new SessionRequest(restoredId))).Read<List<AgentResult>>();
                        var savedResult = history.FirstOrDefault(result => result.Status is "completed" or "fallback" or "partial");
                        if (current?.Id == restoredId && savedResult is not null)
                        {
                            AgentView.ShowStoredResult(savedResult);
                            toolbar?.ShowStoredResult(savedResult);
                        }
                        if (update.Generation != generation) return;
                    }
                }
                observing = update.State is not ("Paused" or "Error");
                ObserveButton.Content = observing ? "暂停观察" : "开始观察";
                Status.Text = update.Error ?? update.State switch
                {
                    "Recorded" => "已记录 · 当前稳定帧已处理并保存",
                    "Processing" => "处理中 · 正在识别或写入数据库",
                    "Stabilizing" => "等待画面稳定 1 秒…",
                    "Changing" => "画面正在变化…",
                    "Paused" => "已暂停 · 不再截图",
                    "Idle" => "等待画面变化…",
                    _ => update.State
                };
                if (!string.IsNullOrWhiteSpace(update.CaptureNotice)) Status.Text = update.CaptureNotice;
                StateDot.Fill = update.State switch
                {
                    "Recorded" => new SolidColorBrush(Color.FromRgb(25, 118, 111)),
                    "Processing" => Brushes.SteelBlue,
                    "Stabilizing" => Brushes.DarkGoldenrod,
                    "Error" => Brushes.IndianRed,
                    _ => Brushes.SlateGray
                };
                if (update.OcrMs > 0) Metrics.Text = $"{update.OcrLanguage} · OCR {update.OcrMs} ms · 保存 {update.CommitMs} ms";
                UpdateToolbar();
                break;
        }
    }
    private void Render()
    {
        if (current is null) return;
        AgentView.SetSession(current);
        SessionTitle.Text = current.Title;
        Geometry.Text = $"物理像素 ({rect.X}, {rect.Y}) · {rect.Width} × {rect.Height}";
        Counts.Text = $"最近快照 {current.Snapshots.Count}/9 · 累计 {current.Blocks.Count} 块 · {current.Blocks.Sum(b => b.Text.Length)} 字符 · 修订 {current.Revision}";
        Document.Text = current.Blocks.Count == 0 ? "等待第一个稳定画面。" : string.Join("\n\n", current.Blocks.Select(b =>
            (b.Status == "uncertain" ? "[顺序或重复待确认] " : "") + b.Text));
        int? selected = (Snapshots.SelectedItem as SnapshotRow)?.Snapshot.Sequence;
        var rows = current.Snapshots.AsEnumerable().Reverse().Select(s => new SnapshotRow(s,
            $"#{s.Sequence} · 新增 {s.NewBlocks} / 复用 {s.ReusedBlocks} / 待确认 {s.UncertainBlocks} · {s.Observation.ProcessingMs} ms")).ToList();
        Snapshots.ItemsSource = rows;
        Snapshots.SelectedItem = rows.Find(r => r.Snapshot.Sequence == selected) ?? rows.FirstOrDefault();
        Gaps.Text = current.Gaps.Count == 0 ? "暂无警告。没有警告也不代表内容完整。快速滚过的内容无法重建。" :
            string.Join("\n\n", current.Gaps.Select(g => $"#{g.FromSequence} → #{g.ToSequence} · 可能遗漏（推测）\n{g.Reason}"));
        UpdateToolbar();
    }
    private async Task CancelAgentAsync(Guid previewId)
    {
        try { await worker.SendAsync("agent-cancel", new { previewId }); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException) { }
    }
    private Task OpenCodeBuddyLoginAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (loginWindow is not null) { loginWindow.Activate(); return Task.CompletedTask; }
        loginWindow = new CodeBuddyLoginWindow(
            async token => (await worker.RequestAsync("agent-login-start", new { }, cancellation: token)).Read<CliLoginSnapshot>(),
            async (id, token) => (await worker.RequestAsync("agent-login-read", new CliLoginRequest(id), cancellation: token)).Read<CliLoginSnapshot>(),
            async (id, text, token) => { await worker.RequestAsync("agent-login-input", new CliLoginInput(id, text), cancellation: token); },
            async (id, token) => { await worker.RequestAsync("agent-login-stop", new CliLoginRequest(id), cancellation: token); },
            async (id, columns, rows, token) => { await worker.RequestAsync("agent-login-resize", new CliLoginResize(id, columns, rows), cancellation: token); }) { Owner = this };
        try { loginWindow.ShowDialog(); }
        finally { loginWindow = null; }
        return Task.CompletedTask;
    }
    private void ViewChanged(object sender, SelectionChangedEventArgs e)
    { if (ReferenceEquals(e.Source, Views)) UpdateViewLayout(); }
    private void UpdateViewLayout()
    {
        if (ReaderControls is null || CompactCapture is null || AgentTab is null) return;
        bool agentVisible = ReferenceEquals(Views.SelectedItem, AgentTab);
        ReaderControls.Visibility = agentVisible ? Visibility.Collapsed : Visibility.Visible;
        CompactCapture.Visibility = agentVisible ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void PauseObservation(object sender, RoutedEventArgs e)
    { resumeAfterMove = false; await Send("pause", new { }); }
    private async Task<bool> Send<T>(string command, T payload)
    {
        try { await worker.SendAsync(command, payload); return true; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException or InvalidOperationException)
        { ShowError(ex is InvalidOperationException ? ex.Message : "Worker 未连接。请等待重连后重试。"); return false; }
    }
    private void ShowError(string message)
    {
        Status.Text = message; StateDot.Fill = Brushes.IndianRed;
        observing = false; ObserveButton.Content = "开始观察";
        UpdateToolbar();
        if (!closing && !selectingRegion && !IsVisible && toolbar is null) Show();
    }
    private async void LanguageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!loadingLanguages && OcrLanguages.SelectedItem is string language)
            await Send("language", new { language });
    }
    private async void OcrModeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!loadingLanguages && OcrModes.SelectedItem is OcrMode mode)
            await Send("ocr-mode", new { scale = mode.Scale });
    }
    private bool EnsureLens()
    {
        if (!excluded) { ShowError("窗口排除验证失败，观察已禁用。"); return false; }
        if (lens is not null)
        {
            lens.Show(); toolbar?.Show(); toolbar?.Follow(lens.CaptureRect);
            observationToolbar?.Show(); observationToolbar?.Follow(lens.CaptureRect);
            return true;
        }
        try
        {
            lens = new LensWindow(rect);
            lens.RectangleChanged += value =>
            {
                rect = value;
                toolbar?.Follow(value);
                observationToolbar?.Follow(value);
                lens?.SetIgnoreRegions([]);
                Geometry.Text = $"物理像素 ({rect.X}, {rect.Y}) · {rect.Width} × {rect.Height}";
                rectTimer.Stop(); rectTimer.Start();
            };
            lens.Moving += async () =>
            {
                lensMoving = true;
                resumeAfterMove = observing;
                if (observing) await Send("pause", new { });
            };
            lens.MoveCompleted += () =>
            {
                lensMoving = false;
                rectTimer.Stop(); rectTimer.Start();
            };
            lens.Closed += async (_, _) =>
            {
                if (!closing && !closingObservation) await CloseObservationAsync();
            };
            lens.Show();
            toolbar = new DockedToolbarWindow { Owner = lens };
            toolbar.CloseLensRequested += async () => await CloseObservationAsync();
            toolbar.SettingsRequested += OpenSidebarSettings;
            toolbar.GenerateAsync = GenerateSidebarAsync;
            observationToolbar = new ObservationToolbarWindow { Owner = lens };
            observationToolbar.RecordsRequested += OpenRecords;
            observationToolbar.ToggleObservationRequested += async () => await ToggleObservationAsync();
            observationToolbar.ReselectRequested += async () => await SelectObservationRegionAsync();
            observationToolbar.IgnoreRegionRequested += async () => await SelectIgnoreRegionAsync();
            observationToolbar.AnalyzeDynamicRegionsRequested += async () => await AnalyzeDynamicRegionsAsync();
            observationToolbar.ClearIgnoreRegionsRequested += async () => await ClearIgnoreRegionsAsync();
            observationToolbar.CloseLensRequested += async () => await CloseObservationAsync();
            observationToolbar.Follow(rect); observationToolbar.Show();
            toolbar.Follow(rect); toolbar.Show(); UpdateToolbar();
            return true;
        }
        catch (Exception ex)
        {
            closingObservation = true;
            toolbar?.CloseForOwner(); toolbar = null;
            observationToolbar?.CloseForOwner(); observationToolbar = null;
            lens?.Close(); lens = null; closingObservation = false;
            ShowError(ex.Message); return false;
        }
    }
    private async void NewSession(object sender, RoutedEventArgs e)
    {
        await SelectObservationRegionAsync(startNewSession: true);
    }
    private async void OpenSession(object sender, RoutedEventArgs e)
    {
        resumeAfterMove = false; rectTimer.Stop();
        if (Sessions.SelectedItem is SessionSummary summary)
            await Send("resume", new SessionRequest(summary.Id));
    }
    private async void SelectRegion(object sender, RoutedEventArgs e)
        => await SelectObservationRegionAsync();

    public async Task SelectObservationRegionAsync(bool startNewSession = false)
    {
        if (selectingRegion || closing) return;
        selectingRegion = true;
        resumeAfterMove = false; rectTimer.Stop();
        try
        {
            if (workerConnected && !await Send("pause", new { })) return;
            lens?.Hide(); toolbar?.Hide(); observationToolbar?.Hide(); Hide();
            PixelRect? selected;
            if (regionSelection is not null) selected = regionSelection();
            else
            {
                var selector = new SelectionWindow();
                selected = selector.ShowDialog() == true ? selector.Selected : null;
            }
            if (selected is null || !selected.IsValid)
            {
                lens?.Show(); toolbar?.Show(); observationToolbar?.Show(); Show(); Activate();
                Status.Text = "已取消框选，观察保持停止。可重新选择区域或打开已有记录。";
                UpdateToolbar(); return;
            }
            rect = selected;
            await workerConnection.Task.WaitAsync(TimeSpan.FromSeconds(20), shutdown.Token);
            if (closing || !EnsureLens()) return;
            lens!.Restore(rect); toolbar?.Follow(rect); observationToolbar?.Follow(rect); rectTimer.Stop();
            bool prepared = startNewSession || current is null ?
                await Send("new", new StartRequest($"阅读 {DateTime.Now:MM-dd HH:mm:ss}", rect)) :
                await Send("rect", new RectRequest(rect));
            if (!prepared || !await Send("observe", new { })) { Show(); return; }
            Hide();
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex) { ShowError(ex is TimeoutException ? "Worker 尚未就绪。请等待连接后重新选择区域。" : "框选未能完成，请重新选择区域。"); Show(); }
        finally { selectingRegion = false; }
    }
    private async void ToggleObserve(object sender, RoutedEventArgs e)
        => await ToggleObservationAsync();

    public async Task ToggleObservationAsync()
    {
        resumeAfterMove = false;
        if (observing) { await Send("pause", new { }); return; }
        if (!EnsureLens()) return;
        if (current is null)
        {
            ShowError("请先新建或打开一个会话。"); return;
        }
        await Send("rect", new RectRequest(lens!.CaptureRect));
        await Send("observe", new { });
    }
    public async Task SelectIgnoreRegionAsync()
    {
        if (lens is null || current is null || selectingRegion || closingObservation) return;
        bool resume = observing;
        Guid sessionId = current.Id;
        PixelRect bounds = lens.CaptureRect;
        selectingRegion = true;
        try
        {
            await Send("pause", new { });
            var selector = new SelectionWindow(bounds);
            if (selector.ShowDialog() == true && selector.Selected is { } selected && current?.Id == sessionId && lens?.CaptureRect == bounds)
                await AddIgnoreRegionAsync(new(selected.X - bounds.X, selected.Y - bounds.Y, selected.Width, selected.Height));
        }
        finally
        {
            selectingRegion = false;
            if (resume && !closing && current?.Id == sessionId && lens?.CaptureRect == bounds) await Send("observe", new { });
        }
    }
    public async Task AddIgnoreRegionAsync(PixelRect region)
    {
        if (current is null || lens is null) return;
        var masks = current.IgnoreRegions?.ToList() ?? [];
        masks.Add(region);
        await Send("ignore-regions", new IgnoreRegionsRequest(masks));
    }
    public async Task ClearIgnoreRegionsAsync()
    { if (current is not null) await Send("ignore-regions", new IgnoreRegionsRequest([])); }
    public async Task AnalyzeDynamicRegionsAsync()
    {
        if (current is null || lens is null || observationToolbar is not { } target || selectingRegion || closingObservation) return;
        target.SetAnalyzing(true);
        try { await worker.RequestAsync("analyze-dynamic-regions", new { }, cancellation: shutdown.Token); }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or ObjectDisposedException)
        { Status.Text = ex is InvalidOperationException ? ex.Message : "动态区域分析未完成，请检查连接或手动框选忽略区域。"; }
        finally { if (observationToolbar == target) { target.SetAnalyzing(false); UpdateToolbar(); } }
    }
    private async void Refresh(object sender, RoutedEventArgs e)
    {
        if (current is not null && EnsureLens()) await Send("refresh", new { });
    }
    private async void CloseLens(object sender, RoutedEventArgs e)
        => await CloseObservationAsync();

    public async Task CloseObservationAsync()
    {
        if (closingObservation) return;
        closingObservation = true;
        resumeAfterMove = false; rectTimer.Stop();
        toolbar?.CancelGeneration();
        lensMoving = false;
        try
        {
            if (workerConnected) await Send("pause", new { });
            observing = false; ObserveButton.Content = "开始观察";
            toolbar?.CloseForOwner(); toolbar = null;
            observationToolbar?.CloseForOwner(); observationToolbar = null;
            lens?.Close(); lens = null;
            if (!closing) { OpenRecords(); Status.Text = "观察框已关闭，已记录文字保留。选择区域可再次开始。"; }
        }
        finally { closingObservation = false; }
    }
    private void UpdateToolbar()
    {
        toolbar?.SetSession(current?.Id);
        toolbar?.UpdateState(observing, Status.Text, current?.Blocks.Count ?? 0,
            current?.Blocks.Sum(b => b.Text.Length) ?? 0, current?.Blocks.Count > 0);
        toolbar?.SetPostCatalog(current);
        observationToolbar?.UpdateState(observing, Status.Text, current?.IgnoreRegions?.Count ?? 0);
        lens?.SetIgnoreRegions(current?.IgnoreRegions);
    }
    private void OpenRecords()
    {
        Views.SelectedIndex = 1;
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate();
    }
    private void OpenSidebarSettings()
    {
        Views.SelectedItem = AgentTab;
        var settings = (Expander)AgentView.FindName("SettingsExpander");
        settings.IsExpanded = true;
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => settings.BringIntoView());
    }
    private async Task<AgentResult> GenerateSidebarAsync(string taskType, string prompt, CancellationToken cancellation)
    {
        if (current is not { Blocks.Count: > 0 } session)
            throw new InvalidOperationException("请先观察并记录一些文字，再发送任务。");
        if (AgentView.HasUnsavedSettings)
            throw new InvalidOperationException("引擎设置尚未保存。请点击侧边栏的设置入口保存后再发送。");
        var target = toolbar ?? throw new InvalidOperationException("侧边栏已关闭。");
        var postIds = target.SelectedPostIds.ToList();
        if (postIds.Count == 0) throw new InvalidOperationException("尚未识别到可处理的正文，请继续观察或调整区域。");
        var settings = (await worker.RequestAsync("agent-settings-get", new { }, cancellation: cancellation)).Read<AgentSettings>();
        var provider = settings.Providers.Single(p => p.Id == settings.SelectedProviderId);
        var request = new AgentTaskRequest(session.Id, taskType, prompt, settings.ContextCharacterBudget, provider.TimeoutSeconds,
            PostIds: postIds);
        var preview = (await worker.RequestAsync("agent-preview", request, cancellation: cancellation)).Read<AgentContextPreview>();
        cancellation.ThrowIfCancellationRequested();
        if (current?.Id != session.Id || toolbar != target)
            throw new OperationCanceledException(cancellation);
        target.SetPreview(preview);
        using var registration = cancellation.Register(() => _ = CancelAgentAsync(preview.PreviewId));
        return (await worker.RequestAsync("agent-run", new { previewId = preview.PreviewId }, TimeSpan.FromMinutes(8), cancellation)).Read<AgentResult>();
    }
    private async void DeleteSession(object sender, RoutedEventArgs e)
    {
        if (Sessions.SelectedItem is not SessionSummary selected) return;
        resumeAfterMove = false; rectTimer.Stop();
        if (!await Send("pause", new { })) return;
        if (MessageBox.Show(this, $"永久删除“{selected.Title}”及其本机 OCR 记录？", "删除会话", MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Send("delete", new SessionRequest(selected.Id));
        if (current?.Id == selected.Id)
        {
            await CloseObservationAsync();
            current = null; Document.Clear(); SnapshotDetail.Clear();
            AgentView.SetSession(null);
            Snapshots.ItemsSource = null; Counts.Text = "会话已删除"; SessionTitle.Text = "选择区域，开始一段阅读";
        }
    }
    private void ExportSession(object sender, RoutedEventArgs e)
    {
        if (current is null) return;
        var dialog = new SaveFileDialog { FileName = $"glimpse-{current.Id:N}.md", Filter = "Markdown 文本|*.md" };
        if (dialog.ShowDialog(this) != true) return;
        var text = new StringBuilder($"# {current.Title}\n\nRevision: {current.Revision}\n\n");
        foreach (var b in current.Blocks)
            text.AppendLine(b.Text).AppendLine().AppendLine($"> {b.Status} · first snapshot #{b.Source.FirstSequence} · observed {b.Source.Observations} times").AppendLine();
        foreach (var gap in current.Gaps) text.AppendLine($"> Possible gap #{gap.FromSequence}–#{gap.ToSequence}: {gap.Reason}");
        File.WriteAllText(dialog.FileName, text.ToString(), new UTF8Encoding(false));
    }
    private void SnapshotSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Snapshots.SelectedItem is not SnapshotRow row || current is null) return;
        SnapshotDetail.Text = string.Join("\n\n", row.Snapshot.Observation.Blocks.Select((b, i) =>
        {
            var durable = current.Blocks.Find(block => block.Id == row.Snapshot.BlockIds[i]);
            return $"{b.Text}\n位置 {b.Bounds.X:0},{b.Bounds.Y:0} · {b.Bounds.Width:0}×{b.Bounds.Height:0}\n首次 #{durable?.Source.FirstSequence} · 观测 {durable?.Source.Observations} 次 · {durable?.Status}\n原始证据：{durable?.Source.OriginalText}";
        }));
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true; closing = true; rectTimer.Stop();
        shutdown.Cancel();
        AgentView.CancelPending();
        if (loginWindow is not null) await loginWindow.StopAndCloseAsync();
        toolbar?.CloseForOwner(); toolbar = null;
        observationToolbar?.CloseForOwner(); observationToolbar = null;
        lens?.Close(); lens = null;
        await worker.DisposeAsync();
        Close();
    }
    private record SnapshotRow(Snapshot Snapshot, string Label);
    private record OcrMode(int Scale, string Label);
}
