using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Glimpse.Contracts;

namespace Glimpse.UI;

public partial class DockedToolbarWindow : Window
{
    public event Action? RecordsRequested;
    public event Action? ToggleObservationRequested;
    public event Action? ReselectRequested;
    public event Action? CloseLensRequested;
    public event Action? SettingsRequested;
    public Func<string, string, CancellationToken, Task<AgentResult>>? GenerateAsync { get; set; }
    public bool IsCaptureExcluded { get; private set; }

    private PixelRect? captureRect;
    private bool ready, ownerClosing, userClosing, followQueued, hasText, observing, closed;
    private string status = "等待开始观察";
    private const double PreferredWidth = 360, PreferredHeight = 760;

    public DockedToolbarWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            Native.Exclude(this);
            IsCaptureExcluded = true;
            HwndSource.FromHwnd(Native.Handle(this)).AddHook(WindowMessage);
            ready = true;
            UpdateControls();
            QueueFollow();
        };
        SizeChanged += (_, _) => QueueFollow();
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; ready = false; CancelGeneration(); PromptInput.Clear(); ClearAgentResults(); ClearPreview(); };
    }
    public DockedToolbarWindow(PixelRect rect) : this() => captureRect = rect;

    public void UpdateState(bool observing, string status, int blockCount, int characterCount, bool hasSessionText)
    {
        if (!Dispatcher.CheckAccess())
        { Dispatcher.BeginInvoke(() => UpdateState(observing, status, blockCount, characterCount, hasSessionText)); return; }
        this.observing = observing;
        this.status = string.IsNullOrWhiteSpace(status) ? observing ? "正在观察选定区域" : "观察已停止" : status;
        bool textAvailabilityChanged = hasText != hasSessionText;
        hasText = hasSessionText;
        Counts.Text = $"已记录 {Math.Max(0, blockCount):N0} 块 · {Math.Max(0, characterCount):N0} 字";
        if (textAvailabilityChanged && !generating && !resultShown)
            SetAgentStatus(hasText ? "点击任务，或发送自定义问题。" : "先记录一些文字，再点击任务或发送问题。");
        UpdateControls();
    }

    public void Follow(PixelRect rect)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Follow(rect)); return; }
        captureRect = rect;
        if (!ready || ownerClosing || userClosing) return;
        PositionNearLens();
    }
    private void QueueFollow()
    {
        if (!ready || followQueued || ownerClosing || userClosing) return;
        followQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            followQueued = false;
            if (ready && !ownerClosing && !userClosing) PositionNearLens();
        });
    }
    private void PositionNearLens()
    {
        if (captureRect is not { } rect) return;
        var work = ToolbarPlacement.WorkArea(rect);
        uint dpi = ToolbarPlacement.GetDpiForWindow(Native.Handle(this));
        double scale = dpi > 0 ? dpi / 96d : VisualTreeHelper.GetDpi(this).DpiScaleX;
        // A move across monitors lets WPF handle WM_DPICHANGED first. The queued
        // follow then recomputes physical dimensions from the new window DPI.
        int width = Math.Max(1, (int)Math.Ceiling(PreferredWidth * scale));
        int height = Math.Max(1, (int)Math.Ceiling(PreferredHeight * scale));
        var placement = ToolbarPlacement.Place(rect, width, height, work);
        if (Native.Bounds(this) != placement) Native.Position(this, placement);
    }
    private nint WindowMessage(nint hwnd, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (message is 0x02E0 or 0x007E or 0x001A) QueueFollow(); // DPI, display, work-area changes
        return 0;
    }

    private void UpdateControls()
    {
        if (ObserveButton is null) return;
        CaptureStatus.Text = status;
        StateLabel.Text = observing ? "观察中" : "已停止";
        StateDot.Fill = observing ? new SolidColorBrush(Color.FromRgb(25, 118, 111)) : new SolidColorBrush(Color.FromRgb(132, 146, 166));
        ObserveButton.Content = observing ? "停止观察" : "开始观察";
        ObserveButton.ToolTip = observing ? "立即停止截图和识别，已记录的内容保留" : "开始观察框内的本地识别";
        ObserveButton.IsEnabled = IsCaptureExcluded;
        UpdateAgentControls();
    }
    private void OpenRecords(object sender, RoutedEventArgs e) => RecordsRequested?.Invoke();
    private void ToggleObservation(object sender, RoutedEventArgs e) => ToggleObservationRequested?.Invoke();
    private void Reselect(object sender, RoutedEventArgs e) => ReselectRequested?.Invoke();
    private void CloseLens(object sender, RoutedEventArgs e) => RequestUserClose();
    private void RequestUserClose()
    {
        if (ownerClosing || userClosing) return;
        userClosing = true;
        CancelGeneration();
        try { CloseLensRequested?.Invoke(); }
        finally { if (IsVisible) { ownerClosing = true; Close(); } }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (ownerClosing) return;
        // Defer the callback outside Closing, because the owner can close the lens
        // and call CloseForOwner as part of handling this request.
        e.Cancel = true;
        Dispatcher.BeginInvoke(RequestUserClose);
    }
    public void CloseForOwner()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(CloseForOwner); return; }
        if (closed) return;
        CancelGeneration();
        ownerClosing = true; Close();
    }
}
