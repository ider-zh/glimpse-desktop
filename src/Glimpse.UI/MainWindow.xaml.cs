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
    private CodeBuddyLoginWindow? loginWindow;
    private SessionData? current;
    private PixelRect rect = new(100, 160, 700, 420);
    private bool observing, closing, excluded;
    private long generation;
    private readonly DispatcherTimer rectTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool resumeAfterMove;
    private bool loadingLanguages;
    public MainWindow()
    {
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
            observing = false; ObserveButton.Content = "开始观察";
            Status.Text = message; StateDot.Fill = Brushes.IndianRed;
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
            if (await Send("rect", new RectRequest(rect)) && resumeAfterMove)
            {
                resumeAfterMove = false;
                await Send("observe", new { });
            }
        };
        Loaded += (_, _) => worker.Start();
        Closing += OnClosing;
    }
    private async Task Receive(Envelope envelope)
    {
        switch (envelope.MessageType)
        {
            case "ready":
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
                        var savedResult = history.FirstOrDefault(result => result.Status is "completed" or "fallback");
                        if (current?.Id == restoredId && savedResult is not null) AgentView.ShowStoredResult(savedResult);
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
                StateDot.Fill = update.State switch
                {
                    "Recorded" => new SolidColorBrush(Color.FromRgb(25, 118, 111)),
                    "Processing" => Brushes.SteelBlue,
                    "Stabilizing" => Brushes.DarkGoldenrod,
                    "Error" => Brushes.IndianRed,
                    _ => Brushes.SlateGray
                };
                if (update.OcrMs > 0) Metrics.Text = $"{update.OcrLanguage} · OCR {update.OcrMs} ms · 保存 {update.CommitMs} ms";
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
        if (lens is not null) return true;
        try
        {
            lens = new LensWindow(rect);
            lens.RectangleChanged += value =>
            {
                rect = value;
                Geometry.Text = $"物理像素 ({rect.X}, {rect.Y}) · {rect.Width} × {rect.Height}";
                rectTimer.Stop(); rectTimer.Start();
            };
            lens.Moving += async () =>
            {
                resumeAfterMove = observing;
                if (observing) await Send("pause", new { });
            };
            lens.Show();
            return true;
        }
        catch (Exception ex) { lens?.Close(); lens = null; ShowError(ex.Message); return false; }
    }
    private async void NewSession(object sender, RoutedEventArgs e)
    {
        resumeAfterMove = false;
        if (!EnsureLens()) return;
        rect = lens!.CaptureRect;
        await Send("new", new StartRequest($"阅读 {DateTime.Now:MM-dd HH:mm:ss}", rect));
    }
    private async void OpenSession(object sender, RoutedEventArgs e)
    {
        resumeAfterMove = false; rectTimer.Stop();
        if (Sessions.SelectedItem is SessionSummary summary)
            await Send("resume", new SessionRequest(summary.Id));
    }
    private async void SelectRegion(object sender, RoutedEventArgs e)
    {
        resumeAfterMove = false; rectTimer.Stop();
        await Send("pause", new { });
        lens?.Hide();
        var selector = new SelectionWindow();
        if (selector.ShowDialog() == true && selector.Selected is { } selected) rect = selected;
        if (lens is not null) { lens.Restore(rect); lens.Show(); }
        else if (!EnsureLens()) return;
        if (current is null) await Send("new", new StartRequest($"阅读 {DateTime.Now:MM-dd HH:mm:ss}", rect));
        else await Send("rect", new RectRequest(rect));
    }
    private async void ToggleObserve(object sender, RoutedEventArgs e)
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
    private async void Refresh(object sender, RoutedEventArgs e)
    {
        if (current is not null && EnsureLens()) await Send("refresh", new { });
    }
    private async void CloseLens(object sender, RoutedEventArgs e)
    {
        resumeAfterMove = false; rectTimer.Stop();
        await Send("pause", new { });
        lens?.Close(); lens = null;
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
            current = null; lens?.Close(); lens = null; Document.Clear(); SnapshotDetail.Clear();
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
        AgentView.CancelPending();
        if (loginWindow is not null) await loginWindow.StopAndCloseAsync();
        lens?.Close();
        await worker.DisposeAsync();
        Close();
    }
    private record SnapshotRow(Snapshot Snapshot, string Label);
    private record OcrMode(int Scale, string Label);
}
